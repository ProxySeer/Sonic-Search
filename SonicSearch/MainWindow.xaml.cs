using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.IO.Filesystem.Ntfs;
using System.Drawing;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Documents;

namespace SonicSearch
{
    public partial class MainWindow : Window
    {
        private List<FileItem> _allItems = new List<FileItem>();
        private readonly object _allItemsLock = new object();
        private volatile FileItem[] _cachedSnapshot = new FileItem[0];
        
        private CancellationTokenSource _searchCancellationTokenSource;
        private System.Windows.Threading.DispatcherTimer _debounceTimer;
        
        /// <summary>The drives actually loaded by the most recent LoadFileSystemData call - kept
        /// separately from AppSettings.Instance.IndexedDrives (the user's current SELECTION,
        /// which can change in Settings before a reindex picks it up) so reindex/reconnect paths
        /// always operate on what's really loaded right now.</summary>
        private List<string> _currentDrives = new List<string> { "C" };
        
        // FSW
        private List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private object _fswLock = new object();
        private HashSet<string> _fswCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _fswDeleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _fswRefreshPending = false;
        private int _lastFswSearchRefreshTicks = 0;
        private System.Windows.Threading.DispatcherTimer _fswTimer;
        private System.Windows.Threading.DispatcherTimer _reindexTimer;
        private bool _isIndexing = false;

        // USN Journal incremental indexing
        // One journal + cursor per indexed drive - NodeIndex is only unique WITHIN a single
        // volume's MFT, so each drive needs its own journal/cursor rather than one shared pair.
        private readonly Dictionary<string, UsnJournal> _usnJournals = new Dictionary<string, UsnJournal>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _lastUsnByDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Threading.DispatcherTimer _usnPollTimer;
        private readonly object _usnLock = new object();

        // Indexing-service mode (AppSettings.Instance.UseIndexingService): the pipe replaces
        // both the direct NtfsReader scan and USN polling above - see LoadFileSystemData and
        // PipeClient.cs.
        // One PipeClient per indexed drive - a PipeClient is a single connection tied to one
        // drive's SNAPSHOT request (see PipeClient.ConnectAndGetSnapshot), so multi-drive service
        // mode needs one per selected drive rather than a single shared connection.
        private readonly List<PipeClient> _pipeClients = new List<PipeClient>();

        // Favorites view
        private enum ViewMode { Search, Favorites }
        private ViewMode _currentView = ViewMode.Search;
        private string _activeFavoriteGroupId;
        private bool _jiggleMode = false;
        private readonly List<(RotateTransform Rotate, Border Badge)> _jiggleTargets = new List<(RotateTransform, Border)>();
        private FavoriteGroup _draggedFromGroup;
        private DragAdorner _dragPreviewAdorner;

        // Filename-prefix trie, rebuilt lazily whenever the snapshot it was built from changes.
        // See GetOrBuildNameTrie / ApplyFilterAsync.
        private readonly object _nameTrieLock = new object();
        private FileNameTrie _nameTrie;
        private FileItem[] _nameTrieSourceSnapshot;

        // True while ApplyFilterAsync's background search work is running. USN journal polling
        // checks this so it never cancels/restarts an in-progress search (content search in
        // particular can take a while, and file-system activity during that window is routine).
        private volatile bool _searchInProgress = false;
        private string _readyStatusText = "Ready";
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        
        private IntPtr _windowHandle;
        private HwndSource _source;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _windowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(HwndHook);
            UpdateGlobalHotkey();
        }

        private void UpdateGlobalHotkey()
        {
            if (_windowHandle == IntPtr.Zero) return;
            UnregisterHotKey(_windowHandle, HOTKEY_ID);

            uint mod = MOD_CONTROL;
            string modStr = AppSettings.Instance.HotkeyModifiers ?? "Ctrl";
            if (modStr.IndexOf("Ctrl", StringComparison.OrdinalIgnoreCase) >= 0 && modStr.IndexOf("Alt", StringComparison.OrdinalIgnoreCase) >= 0)
                mod = MOD_CONTROL | MOD_ALT;
            else if (modStr.IndexOf("Ctrl", StringComparison.OrdinalIgnoreCase) >= 0 && modStr.IndexOf("Shift", StringComparison.OrdinalIgnoreCase) >= 0)
                mod = MOD_CONTROL | MOD_SHIFT;
            else if (modStr.IndexOf("Alt", StringComparison.OrdinalIgnoreCase) >= 0)
                mod = MOD_ALT;
            else if (modStr.IndexOf("Win", StringComparison.OrdinalIgnoreCase) >= 0)
                mod = MOD_WIN;
            else
                mod = MOD_CONTROL;

            uint vk = 0x53; // Default 'S'
            string keyStr = AppSettings.Instance.HotkeyKey ?? "S";
            if (!string.IsNullOrEmpty(keyStr))
            {
                char c = char.ToUpperInvariant(keyStr[0]);
                if (c >= 'A' && c <= 'Z') vk = (uint)c;
                else if (c >= '0' && c <= '9') vk = (uint)c;
            }

            RegisterHotKey(_windowHandle, HOTKEY_ID, mod, vk);
        }

        protected override void OnClosed(EventArgs e)
        {
            _source?.RemoveHook(HwndHook);
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _usnPollTimer?.Stop();
            lock (_usnLock)
            {
                foreach (var j in _usnJournals.Values) j?.Dispose();
                _usnJournals.Clear();
                _lastUsnByDrive.Clear();
            }
            foreach (var c in _pipeClients) c?.Dispose();
            _pipeClients.Clear();
            base.OnClosed(e);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleWindowVisibility();
                handled = true;
            }
            return IntPtr.Zero;
        }
        
        private void ToggleWindowVisibility()
        {
            IntPtr foregroundHwnd = WinApiHelper.GetForegroundWindow();
            bool isForeground = (_windowHandle != IntPtr.Zero && foregroundHwnd == _windowHandle);

            if (this.Visibility == Visibility.Visible && isForeground)
            {
                // Only hide if the window was already in the foreground and focused
                CloseSuggestionsPopup();
                this.Hide();
            }
            else
            {
                // Bring to top and focus
                if (this.Visibility != Visibility.Visible)
                {
                    this.Show();
                }

                this.WindowState = WindowState.Normal;
                this.Topmost = true;
                this.Topmost = false; // Reset topmost so it doesn't stay pinned over everything forever
                this.Activate();

                if (_windowHandle != IntPtr.Zero)
                {
                    WinApiHelper.SetForegroundWindow(_windowHandle);
                }

                // Bringing the window up via the global hotkey shouldn't pop the suggestions/
                // history list open by itself - only actually typing (txtSearch_TextChanged) or
                // the user deliberately clicking/tabbing into the box should do that.
                _suppressNextFocusSuggestions = true;
                txtSearch.Focus();
                txtSearch.SelectAll();
            }
        }

        private void CloseSuggestionsPopup()
        {
            if (popupSuggestions != null && popupSuggestions.IsOpen)
            {
                popupSuggestions.IsOpen = false;
            }
        }

        
        

        
        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        internal enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }

        internal enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
            ACCENT_INVALID_STATE = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [DllImport("user32.dll")]
        internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        private void EnableBlur()
        {
            var windowHelper = new WindowInteropHelper(this);
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, // Use standard blur, not acrylic, to avoid grain
                GradientColor = (0 << 24) | (0x101010) // No tint
            };

            var accentStructSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentStructSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = accentStructSize,
                Data = accentPtr
            };

            SetWindowCompositionAttribute(windowHelper.Handle, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }

        public MainWindow()
        {
            AppSettings.Load();
            InitializeComponent();
            
            _debounceTimer = new System.Windows.Threading.DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, AppSettings.Instance.DebounceMs));
            _debounceTimer.Tick += DebounceTimer_Tick;
            
            _fswTimer = new System.Windows.Threading.DispatcherTimer();
            _fswTimer.Interval = TimeSpan.FromMilliseconds(1000);
            _fswTimer.Tick += FswTimer_Tick;
            _fswTimer.Start();

            SetupAutoReindexTimer();
            SetupTrayIcon();
            
            this.Deactivated += (s, e) => CloseSuggestionsPopup();
            this.IsVisibleChanged += (s, e) => { if (!this.IsVisible) CloseSuggestionsPopup(); };

            this.PreviewKeyDown += MainWindow_ViewShortcut_PreviewKeyDown;

            Loaded += MainWindow_Loaded;
        }

        // Ctrl+1 = Search view, Ctrl+2 = Favorites view. Deliberately not Ctrl+F/Ctrl+S: Ctrl+S
        // is the default *global* show/hide hotkey (fires via WM_HOTKEY regardless of focus),
        // so reusing it for an in-window view switch would fire both at once whenever the
        // window happens to be focused.
        private void MainWindow_ViewShortcut_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_jiggleMode && e.Key == Key.Escape)
            {
                ExitJiggleMode();
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

            if (e.Key == Key.D1 || e.Key == Key.NumPad1)
            {
                SwitchToView(ViewMode.Search);
                e.Handled = true;
            }
            else if (e.Key == Key.D2 || e.Key == Key.NumPad2)
            {
                SwitchToView(ViewMode.Favorites);
                e.Handled = true;
            }
        }

        private void SetupTrayIcon()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon();
                _notifyIcon.Text = "SonicSearch (Running)";
                
                // Try load application icon from exe / base directory
                try
                {
                    string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sonic-icon.ico");
                    if (File.Exists(iconPath))
                    {
                        _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                    }
                    else
                    {
                        _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetEntryAssembly().Location) 
                                           ?? System.Drawing.SystemIcons.Application;
                    }
                }
                catch
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }

                var contextMenu = new System.Windows.Forms.ContextMenuStrip
                {
                    Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new DarkTrayMenuColors()),
                    BackColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x22),
                    ForeColor = System.Drawing.Color.FromArgb(0xEE, 0xEE, 0xEE),
                    ShowImageMargin = false
                };
                var showItem = new System.Windows.Forms.ToolStripMenuItem("Show / Hide (Ctrl+S)", null, (s, e) => ToggleWindowVisibility());
                var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Settings...", null, (s, e) => {
                    this.Show();
                    this.Activate();
                    SettingsButton_Click(null, null);
                });
                var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit SonicSearch", null, (s, e) => {
                    // Explicitly Close() the window first rather than calling Application.Shutdown()
                    // directly - CloseButton_Click now just Hide()s the window instead of closing
                    // it (so the titlebar X goes to tray, not exit), and WPF's app-wide shutdown
                    // sequence doesn't reliably fire OnClosed (where the pipe client to
                    // SonicSearchService gets disposed) for a window that was only ever hidden,
                    // never actually closed. Skipping that cleanup left the client-side pipe
                    // connection dangling, which is what made the NEXT launch fail to connect to
                    // the service ("Indexing Service Not Running" right after a normal close+
                    // reopen). Close() runs OnClosed for certain, and the app then shuts down on
                    // its own once this - the only window - is gone (default ShutdownMode).
                    this.Close();
                });

                foreach (var mi in new[] { showItem, settingsItem, exitItem })
                    mi.ForeColor = System.Drawing.Color.FromArgb(0xEE, 0xEE, 0xEE);

                contextMenu.Items.Add(showItem);
                contextMenu.Items.Add(settingsItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(exitItem);

                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.DoubleClick += (s, e) => ToggleWindowVisibility();
                _notifyIcon.Visible = true;
            }
            catch { }
        }

        /// <summary>
        /// Dark palette for the tray icon's WinForms ContextMenuStrip, matching the rest of the
        /// app's theme (#1E1E22 background, #3F3F46 hover/border, #EEEEEE text) instead of the
        /// default light Windows menu - WinForms menus don't follow the app's own WPF styling
        /// automatically, so this needs its own ProfessionalColorTable.
        /// </summary>
        private class DarkTrayMenuColors : System.Windows.Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Background = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x22);
            private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x3F, 0x3F, 0x46);
            private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0x3F, 0x3F, 0x46);

            public override System.Drawing.Color ToolStripDropDownBackground => Background;
            public override System.Drawing.Color ImageMarginGradientBegin => Background;
            public override System.Drawing.Color ImageMarginGradientMiddle => Background;
            public override System.Drawing.Color ImageMarginGradientEnd => Background;
            public override System.Drawing.Color MenuBorder => Border;
            public override System.Drawing.Color MenuItemBorder => Hover;
            public override System.Drawing.Color MenuItemSelected => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
            public override System.Drawing.Color MenuItemPressedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemPressedGradientEnd => Hover;
            public override System.Drawing.Color SeparatorDark => Border;
            public override System.Drawing.Color SeparatorLight => Border;
        }

        private void SetupAutoReindexTimer()
        {
            if (_reindexTimer != null)
            {
                _reindexTimer.Stop();
                _reindexTimer.Tick -= ReindexTimer_Tick;
                _reindexTimer = null;
            }

            int minutes = AppSettings.Instance.AutoReindexMinutes;
            if (minutes > 0)
            {
                _reindexTimer = new System.Windows.Threading.DispatcherTimer();
                _reindexTimer.Interval = TimeSpan.FromMinutes(minutes);
                _reindexTimer.Tick += ReindexTimer_Tick;
                _reindexTimer.Start();
            }
        }

        private void ReindexTimer_Tick(object sender, EventArgs e)
        {
            // Skip this tick if a search (e.g. a slow content: search) is in flight - the timer
            // fires again next interval, so it's fine to just defer rather than interrupt it.
            if (!_isIndexing && !_searchInProgress)
            {
                LoadFileSystemData(GetConfiguredDrives());
            }
        }

        /// <summary>Reads AppSettings.Instance.IndexedDrives, falling back to just "C" if it's
        /// empty (e.g. an older settings.json from before this feature existed), and drops any
        /// drive that's no longer actually present (unplugged external/removable drive) rather
        /// than letting a stale selection fail the whole reindex.</summary>
        private static List<string> GetConfiguredDrives()
        {
            var configured = AppSettings.Instance.IndexedDrives;
            if (configured == null || configured.Count == 0)
                return new List<string> { "C" };

            var present = new HashSet<string>(
                DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name.TrimEnd('\\', ':')),
                StringComparer.OrdinalIgnoreCase);

            var result = configured.Where(d => present.Contains(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return result.Count > 0 ? result : new List<string> { "C" };
        }
        
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (popupSuggestions != null && popupSuggestions.IsOpen)
            {
                popupSuggestions.IsOpen = false;
            }
            DragMove();
        }

        
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            txtSearch.Text = string.Empty;
            txtSearch.Focus();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            string oldIncluded = AppSettings.Instance.IncludedIndexFolders ?? "";
            string oldExcluded = AppSettings.Instance.ExcludedIndexFolders ?? "";
            var oldDrives = new HashSet<string>(GetConfiguredDrives(), StringComparer.OrdinalIgnoreCase);
            var optionsWindow = new OptionsWindow();
            optionsWindow.Owner = this;
            if (optionsWindow.ShowDialog() == true)
            {
                // Apply updated settings
                UpdateGlobalHotkey();
                if (_debounceTimer != null)
                {
                    _debounceTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, AppSettings.Instance.DebounceMs));
                }
                SetupAutoReindexTimer();
                SetupFileSystemWatcher();

                string newIncluded = AppSettings.Instance.IncludedIndexFolders ?? "";
                string newExcluded = AppSettings.Instance.ExcludedIndexFolders ?? "";
                var newDrives = new HashSet<string>(GetConfiguredDrives(), StringComparer.OrdinalIgnoreCase);
                if (!string.Equals(oldIncluded, newIncluded, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(oldExcluded, newExcluded, StringComparison.OrdinalIgnoreCase) ||
                    !oldDrives.SetEquals(newDrives))
                {
                    // Reload index with the new folder filter scope and/or drive selection
                    LoadFileSystemData(GetConfiguredDrives());
                }
                else
                {
                    TriggerSearch();
                }
            }
        }

        /// <summary>
        /// Sets both the status dot and the status text to the same color, so the whole
        /// status line reads as one colored signal (ready/searching/error/etc.) instead of
        /// just the small dot changing.
        /// </summary>
        private void SetStatusColor(System.Windows.Media.Color color)
        {
            var brush = new System.Windows.Media.SolidColorBrush(color);
            lblStatusDot.Foreground = brush;
            lblStatus.Foreground = brush;
        }

        private static System.Windows.Media.Animation.ObjectAnimationUsingKeyFrames _statusSpinnerAnimation;

        /// <summary>
        /// Lazily decodes Resources/1zno.gif into a frame-by-frame WPF animation the first time
        /// it's needed, then reuses the same decoded frames for every later show - decoding a GIF
        /// is the only non-trivial cost here, and this is a tiny status icon (not a full-size
        /// image), so caching it once keeps repeated show/hide (e.g. one search after another)
        /// effectively free.
        /// </summary>
        private static System.Windows.Media.Animation.ObjectAnimationUsingKeyFrames GetStatusSpinnerAnimation()
        {
            if (_statusSpinnerAnimation != null) return _statusSpinnerAnimation;

            var decoder = new GifBitmapDecoder(
                new Uri("pack://application:,,,/Resources/1zno.gif"),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            var animation = new System.Windows.Media.Animation.ObjectAnimationUsingKeyFrames
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            TimeSpan t = TimeSpan.Zero;
            foreach (var frame in decoder.Frames)
            {
                animation.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteObjectKeyFrame(frame, t));
                // GIF frame delay lives in the frame's metadata (hundredths of a second); fall
                // back to a sane default for GIFs that omit it (delay 0 is common and means "use
                // the previous/default rate", not "no delay").
                int delayCentiseconds = 10;
                if (frame.Metadata is BitmapMetadata meta)
                {
                    try
                    {
                        var raw = meta.GetQuery("/grctlext/Delay");
                        if (raw is ushort d && d > 0) delayCentiseconds = d;
                    }
                    catch { }
                }
                t += TimeSpan.FromMilliseconds(delayCentiseconds * 10);
            }
            // Closing key frame so the loop's last held frame has the right duration too.
            animation.Duration = t;

            _statusSpinnerAnimation = animation;
            return animation;
        }

        /// <summary>
        /// Shows the small loading-gif spinner next to the status dot for the duration of
        /// indexing/searching. The animation only runs while visible - HideStatusSpinner detaches
        /// it - so it costs nothing while idle, which covers the "if not a performance issue"
        /// concern: the GIF is tiny (a status icon, not a full image) and decoded once and cached.
        /// </summary>
        private void ShowStatusSpinner()
        {
            if (imgStatusSpinner == null) return;
            imgStatusSpinner.Visibility = Visibility.Visible;
            imgStatusSpinner.BeginAnimation(System.Windows.Controls.Image.SourceProperty, GetStatusSpinnerAnimation());
        }

        private void HideStatusSpinner()
        {
            if (imgStatusSpinner == null) return;
            imgStatusSpinner.BeginAnimation(System.Windows.Controls.Image.SourceProperty, null);
            imgStatusSpinner.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Only reachable at all when the app is running unelevated (indexing-service mode) -
        /// Explorer runs unelevated too, and Windows' UIPI unconditionally blocks OLE
        /// drag-and-drop from a lower- into a higher-integrity window, so when the app is
        /// elevated (the default, non-service mode) the OS drops these events before they ever
        /// reach here. No extra guard needed on our end for that case.
        /// </summary>
        private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// Dropping files/folders from Explorer adds them to Favorites - the currently active
        /// group if already in the Favorites view, otherwise the first group - reusing the same
        /// ItemPaths list the right-click "Add to Group" flow writes to (see
        /// AddToGroupSubmenu_Opened) rather than inventing a second favorites-like mechanism.
        /// </summary>
        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            var paths = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;

            var group = (FavoriteGroups != null && _currentView == ViewMode.Favorites)
                ? FavoriteGroups.FirstOrDefault(g => g.Id == _activeFavoriteGroupId) ?? FavoriteGroups.FirstOrDefault()
                : FavoriteGroups?.FirstOrDefault();
            if (group == null) return;

            if (group.ItemPaths == null) group.ItemPaths = new List<string>();
            int added = 0;
            foreach (var path in paths)
            {
                if (!group.ItemPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    group.ItemPaths.Add(path);
                    added++;
                }
            }
            if (added == 0) return;

            AppSettings.Save();
            if (_currentView == ViewMode.Favorites) RefreshFavoritesGrid();

            SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
            lblStatus.Text = string.Format("Added {0} item{1} to \"{2}\"", added, added == 1 ? "" : "s", group.Name);
        }

        private void ManualReindex_Click(object sender, RoutedEventArgs e)
        {
            if (_isIndexing) return;
            LoadFileSystemData(GetConfiguredDrives());
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Hides to the tray instead of quitting outright - the app keeps running (indexing,
            // live-sync, the global hotkey) in the background either way, so a titlebar X that
            // silently killed all of that would be surprising. The tray icon's own right-click
            // menu already has "Exit SonicSearch" for anyone who actually wants to quit.
            this.Hide();

            // One-time-per-hide nudge (native Windows balloon, anchored to the tray icon at the
            // bottom-right) so it's obvious the X didn't just quit the app.
            try
            {
                _notifyIcon?.ShowBalloonTip(2500, "SonicSearch", "SonicSearch is still running in the background.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadFileSystemData(GetConfiguredDrives());
        }

        private async void LoadFileSystemData(List<string> driveLetters)
        {
            if (_isIndexing) return;
            if (driveLetters == null || driveLetters.Count == 0) driveLetters = new List<string> { "C" };
            _isIndexing = true;
            _currentDrives = driveLetters;
            SetStatusColor(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)); // Blue
            ShowStatusSpinner();
            lblStatus.Text = driveLetters.Count == 1
                ? $"Indexing {driveLetters[0]}:\\ Master File Table..."
                : $"Indexing {string.Join(", ", driveLetters.Select(d => d + ":"))}\\ Master File Tables...";
            if (string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                listView.ItemsSource = null;
            }

            bool useService = AppSettings.Instance.UseIndexingService;

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                List<FileItem> fileItems;
                if (useService)
                {
                    try
                    {
                        fileItems = await LoadViaServiceAsync(driveLetters);
                    }
                    catch (Exception ex)
                    {
                        // Service mode means the app runs unelevated on purpose (see App.xaml.cs)
                        // - if SonicSearchService isn't there to answer, there's no direct
                        // NtfsReader fallback to silently drop back to, since that read needs
                        // admin rights this process deliberately doesn't have. Say so plainly
                        // rather than surfacing the raw pipe exception ("The operation has timed
                        // out"), which gives no hint that the fix is in Settings.
                        SetStatusColor(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // Red
                        HideStatusSpinner();
                        lblStatus.Text = "Indexing service isn't responding.";
                        Trace.WriteLine("Indexing service connection failed: " + ex);
                        ThemedMessageBox.Show(this,
                            "The background indexing service isn't running, so SonicSearch has nothing to search.\n\n" +
                            "Open Settings and toggle \"Enable Background Indexing Service\" off then on again to reinstall it, or turn it off entirely to go back to running SonicSearch elevated.",
                            "Indexing Service Not Running", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                else
                {
                    fileItems = await Task.Run(() =>
                    {
                        var incFolders = GetIncludedFolders();
                        var excFolders = GetExcludedFolders();
                        var merged = new List<FileItem>();
                        Exception firstFailure = null;
                        int failureCount = 0;

                        foreach (var driveLetter in driveLetters)
                        {
                            try
                            {
                                var drive = new DriveInfo(driveLetter);
                                var ntfsReader = new NtfsReader(drive, RetrieveMode.StandardInformations);
                                var nodes = ntfsReader.GetNodes(driveLetter + ":\\");

                                var driveItems = nodes.AsParallel().Select(node =>
                                {
                                    string fullName = node.FullName;
                                    if (!IsPathIndexable(fullName, incFolders, excFolders)) return null;

                                    return new FileItem
                                    {
                                        NodeIndex = node.NodeIndex,
                                        FullName = fullName,
                                        FileName = node.Name ?? Path.GetFileName(fullName) ?? fullName,
                                        Size = node.Size > 0 ? (long)node.Size : 0,
                                        LastWriteTime = node.LastChangeTime,
                                        IsDirectory = (node.Attributes & Attributes.Directory) != 0
                                    };
                                })
                                .Where(x => x != null)
                                .ToList();

                                merged.AddRange(driveItems);
                            }
                            catch (Exception ex)
                            {
                                failureCount++;
                                if (firstFailure == null) firstFailure = ex;
                                Trace.WriteLine("Indexing drive " + driveLetter + " failed: " + ex);
                            }
                        }

                        // Only a TOTAL failure (every selected drive failed) surfaces as the
                        // blocking admin/IO error dialog below - one bad drive (not NTFS, access
                        // denied, unplugged mid-scan) shouldn't throw away good results from the
                        // drives that DID succeed, just get logged and skipped.
                        if (merged.Count == 0 && failureCount == driveLetters.Count && firstFailure != null)
                            throw firstFailure;

                        return merged;
                    });
                }

                lock (_allItemsLock)
                {
                    _allItems = fileItems;
                    _cachedSnapshot = fileItems.ToArray();
                }

                if (useService)
                    StartServiceDeltaSubscription();
                else
                    StartUsnJournalPolling(driveLetters);

                stopwatch.Stop();
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                _readyStatusText = $"Ready — Indexed {_allItems.Count:N0} files in {elapsedSec:F2}s";

                SetupFileSystemWatcher();

                if (string.IsNullOrWhiteSpace(txtSearch.Text))
                {
                    SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    HideStatusSpinner();
                    lblStatus.Text = _readyStatusText;
                }
                else
                {
                    TriggerSearch();
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                SetStatusColor(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // Red
                    HideStatusSpinner();
                lblStatus.Text = "Requires Administrator Rights! Error: " + ex.Message;
                ThemedMessageBox.Show(this, "Please run SonicSearch as Administrator to read the Master File Table (MFT).", "Admin Required", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (IOException ex)
            {
                // Covers both access-denied opening the volume handle (thrown as IOException by
                // NtfsReader) and genuine read/format failures - don't kill the app for either;
                // let the user pick another drive or retry.
                SetStatusColor(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // Red
                    HideStatusSpinner();
                lblStatus.Text = "Requires Administrator Rights! Error: " + ex.Message;
                ThemedMessageBox.Show(this, "Please run SonicSearch as Administrator to read the Master File Table (MFT).", "Admin Required", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                // A single bad/corrupt volume shouldn't take down the whole app.
                SetStatusColor(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // Red
                    HideStatusSpinner();
                lblStatus.Text = "Indexing failed: " + ex.Message;
                Trace.WriteLine("LoadFileSystemData failed for drive(s) " + string.Join(",", driveLetters) + ": " + ex);
            }
            finally
            {
                _isIndexing = false;
            }
        }

        private static List<string> GetIncludedFolders()
        {
            return (AppSettings.Instance.IncludedIndexFolders ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimEnd('\\'))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        private static List<string> GetExcludedFolders()
        {
            return (AppSettings.Instance.ExcludedIndexFolders ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimEnd('\\'))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        /// <summary>NodeIndex is only unique WITHIN one volume's MFT - indexing more than one
        /// drive at once means a USN/delta change has to also be checked against the drive it
        /// came from before matching it to an existing FileItem by NodeIndex alone, or a rename
        /// on D: could silently update an unrelated file that happens to share the same
        /// NodeIndex on C:. driveLetter may be a bare letter ("C") or an item's own FullName - only
        /// the first character is ever compared.</summary>
        private static bool SameDrive(FileItem fi, string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter) || string.IsNullOrEmpty(fi.FullName)) return true;
            return char.ToUpperInvariant(fi.FullName[0]) == char.ToUpperInvariant(driveLetter[0]);
        }

        /// <summary>
        /// Applies the same system-file / include-folder / exclude-folder rules used by the
        /// full MFT scan, so USN journal-driven incremental updates stay consistent with it.
        /// </summary>
        private static bool IsPathIndexable(string fullName, List<string> incFolders, List<string> excFolders)
        {
            if (string.IsNullOrEmpty(fullName)) return false;

            // Skip internal NTFS system files ($MFT, $LogFile, etc.)
            if (fullName.IndexOf(@"\$", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (excFolders.Count > 0)
            {
                for (int i = 0; i < excFolders.Count; i++)
                {
                    if (fullName.IndexOf(excFolders[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                }
            }

            if (incFolders.Count > 0)
            {
                bool isIncluded = false;
                for (int i = 0; i < incFolders.Count; i++)
                {
                    if (fullName.StartsWith(incFolders[i], StringComparison.OrdinalIgnoreCase))
                    {
                        isIncluded = true;
                        break;
                    }
                }
                if (!isIncluded) return false;
            }

            return true;
        }

        /// <summary>
        /// Service-mode equivalent of the direct NtfsReader scan above: connects to
        /// SonicSearchService, requests a snapshot for this drive, and converts the wire DTOs
        /// into this window's own FileItem (which - unlike the DTO - carries the lazily-loaded
        /// WPF icon). The connection itself happens on a background thread since Connect() and
        /// the initial frame read both block.
        /// </summary>
        private Task<List<FileItem>> LoadViaServiceAsync(List<string> driveLetters)
        {
            return Task.Run(() =>
            {
                foreach (var old in _pipeClients) old?.Dispose();
                _pipeClients.Clear();

                var incFolders = GetIncludedFolders();
                var excFolders = GetExcludedFolders();
                var merged = new List<FileItem>();
                Exception firstFailure = null;

                // One connection per drive - PipeClient.ConnectAndGetSnapshot ties one connection
                // to one SNAPSHOT request, so N drives means N connections (kept open afterward
                // for delta subscription - see StartServiceDeltaSubscription).
                foreach (var driveLetter in driveLetters)
                {
                    try
                    {
                        var client = new PipeClient();
                        var items = client.ConnectAndGetSnapshot(driveLetter);
                        _pipeClients.Add(client);

                        // AsParallel here too - matches the direct path (see the other branch
                        // above) and the service's own parallel scan (PipeServer.ScanDrive); this
                        // filter+convert step is otherwise-identical per-item work run ~1M times.
                        var converted = items
                            .AsParallel()
                            .Where(dto => IsPathIndexable(dto.FullName, incFolders, excFolders))
                            .Select(dto => new FileItem
                            {
                                NodeIndex = dto.NodeIndex,
                                FullName = dto.FullName,
                                FileName = dto.FileName,
                                Size = dto.Size,
                                LastWriteTime = dto.LastWriteTime,
                                IsDirectory = dto.IsDirectory
                            })
                            .ToList();

                        merged.AddRange(converted);
                    }
                    catch (Exception ex)
                    {
                        if (firstFailure == null) firstFailure = ex;
                        Trace.WriteLine("Service connection for drive " + driveLetter + " failed: " + ex);
                    }
                }

                // Same partial-failure tolerance as the direct path: only throw (surfacing the
                // "service isn't responding" message) if every drive's connection failed.
                if (_pipeClients.Count == 0 && firstFailure != null)
                    throw firstFailure;

                return merged;
            });
        }

        /// <summary>
        /// Wires up live updates for service mode: SonicSearchService pushes one DeltaDto per
        /// USN-journal change it observes (see PipeServer.ResolveDelta) - this applies each one
        /// to _allItems the same way PollUsnJournalBackground/ApplyUsnChange do for the direct
        /// path, then refreshes the UI identically. On disconnect, falls back to a full rescan
        /// via the service (matches the direct path's UsnJournalInvalidatedException recovery).
        /// </summary>
        private void StartServiceDeltaSubscription()
        {
            // One subscription per drive's connection - a drive's own delta stream only ever
            // reports changes for that drive (see PipeServer's per-connection journal), so this
            // just wires up the same handler on each of them.
            foreach (var client in _pipeClients.ToList())
            {
                client.DeltaReceived += delta =>
                {
                    var incFolders = GetIncludedFolders();
                    var excFolders = GetExcludedFolders();
                    bool changed;
                    lock (_allItemsLock)
                    {
                        changed = ApplyServiceDelta(delta, incFolders, excFolders);
                        if (changed) _cachedSnapshot = _allItems.ToArray();
                    }

                    if (!changed) return;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (string.IsNullOrWhiteSpace(txtSearch.Text))
                        {
                            _readyStatusText = string.Format("Ready — Indexed {0:N0} files", _allItems.Count);
                            HideStatusSpinner();
                            lblStatus.Text = _readyStatusText;
                        }
                        else if (!_searchInProgress)
                        {
                            TriggerSearch();
                        }
                    }));
                };

                client.Disconnected += () =>
                {
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        // A full reindex (of every selected drive, not just the one that dropped -
                        // simpler and matches the direct path's "any journal invalidated -> full
                        // rescan" recovery) is the only recovery from a dropped service connection,
                        // but reconnecting instantly every time risks a tight reconnect/reindex
                        // loop if whatever caused the drop keeps recurring (PipeServer.cs has its
                        // own retry logic for transient errors now, but this is a second line of
                        // defense) - a short delay costs nothing on the ordinary "service briefly
                        // restarted" case and turns a potential runaway loop into, at worst, a slow
                        // retry cycle. LoadFileSystemData's own _isIndexing guard means it's safe
                        // for more than one drive's connection to drop around the same time and
                        // each independently schedule this - only the first actually reindexes.
                        Trace.WriteLine("Indexing service disconnected; reconnecting shortly.");
                        await Task.Delay(2000);
                        LoadFileSystemData(_currentDrives);
                    }));
                };
            }
        }

        /// <summary>Applies one DeltaDto to _allItems in place. Must be called with
        /// _allItemsLock held. Mirrors ApplyUsnChange's semantics exactly - see DeltaDto's doc
        /// comment for why the resolution work already happened service-side.</summary>
        private bool ApplyServiceDelta(SonicSearch.Contracts.DeltaDto delta, List<string> incFolders, List<string> excFolders)
        {
            if (delta.IsRemoval)
                return _allItems.RemoveAll(fi => fi.NodeIndex == delta.NodeIndex && SameDrive(fi, delta.DriveLetter)) > 0;

            if (!IsPathIndexable(delta.FullName, incFolders, excFolders))
                return _allItems.RemoveAll(fi => fi.NodeIndex == delta.NodeIndex && SameDrive(fi, delta.DriveLetter)) > 0;

            var existingIndex = _allItems.FindIndex(fi => fi.NodeIndex == delta.NodeIndex && SameDrive(fi, delta.DriveLetter));
            if (existingIndex >= 0)
            {
                var item = _allItems[existingIndex];
                item.UpdatePath(delta.FullName, delta.FileName);
                item.Size = delta.Size;
                item.LastWriteTime = delta.LastWriteTime;
            }
            else
            {
                _allItems.Add(new FileItem
                {
                    NodeIndex = delta.NodeIndex,
                    FullName = delta.FullName,
                    FileName = delta.FileName,
                    Size = delta.Size,
                    LastWriteTime = delta.LastWriteTime,
                    IsDirectory = delta.IsDirectory
                });
            }
            return true;
        }

        /// <summary>
        /// Starts polling the NTFS USN change journal for the given drive so routine
        /// create/rename/delete/modify activity can be applied to the in-memory index
        /// without a full MFT rescan. Falls back to a full rescan automatically if the
        /// journal is unavailable or gets invalidated (wrapped/recreated) while polling.
        /// </summary>
        private void StartUsnJournalPolling(List<string> driveLetters)
        {
            lock (_usnLock)
            {
                foreach (var j in _usnJournals.Values) j?.Dispose();
                _usnJournals.Clear();
                _lastUsnByDrive.Clear();

                foreach (var driveLetter in driveLetters)
                {
                    try
                    {
                        var journal = new UsnJournal(new DriveInfo(driveLetter));
                        _lastUsnByDrive[driveLetter] = journal.CurrentUsn;
                        _usnJournals[driveLetter] = journal;
                    }
                    catch (Exception ex)
                    {
                        // USN journal isn't available (e.g. non-NTFS volume, or access denied for
                        // the journal specifically) - the app still works via full rescans/FSW for
                        // this one drive, while the others keep live-syncing normally.
                        Trace.WriteLine("USN journal unavailable for drive " + driveLetter + ": " + ex.Message);
                    }
                }

                if (_usnJournals.Count == 0) return;
            }

            if (_usnPollTimer == null)
            {
                _usnPollTimer = new System.Windows.Threading.DispatcherTimer();
                _usnPollTimer.Interval = TimeSpan.FromSeconds(3);
                _usnPollTimer.Tick += (s, e) => PollUsnJournal();
            }
            _usnPollTimer.Stop();
            _usnPollTimer.Start();
        }

        private volatile bool _usnPollRunning = false;

        private void PollUsnJournal()
        {
            // This fires on the DispatcherTimer's Tick, i.e. the UI thread. Everything it does
            // - GetChanges (DeviceIoControl in a loop) and ApplyUsnChange's ResolvePath
            // (OpenFileById + GetFinalPathNameByHandle per changed file) - is a blocking Win32
            // call. Previously this ran inline here, so a burst of routine file-system activity
            // (browser cache, logs, temp files) could stall the entire UI - including whatever
            // you were typing - for however long that batch took, every 3 seconds. All of it now
            // runs on a background thread; only the final status/UI refresh is marshaled back.
            // USN-journal polling is a separate live-sync mechanism from the FileSystemWatcher
            // layer SetupFileSystemWatcher/FswTimer_Tick already gate on this same setting - it
            // was running unconditionally every 3 seconds regardless of "Real-time File Watcher"
            // being off, which is the more expensive of the two under heavy disk activity
            // elsewhere on the machine (GetChanges + per-changed-file OpenFileById path
            // resolution, all blocking Win32 calls). Turning the setting off now actually stops
            // all of the app's live-sync CPU use, not just half of it.
            if (!AppSettings.Instance.EnableRealtimeWatcher) return;
            if (_isIndexing || _searchInProgress || _usnPollRunning) return;

            Dictionary<string, UsnJournal> journals;
            lock (_usnLock)
            {
                journals = new Dictionary<string, UsnJournal>(_usnJournals, StringComparer.OrdinalIgnoreCase);
            }
            if (journals.Count == 0) return;

            _usnPollRunning = true;
            Task.Run(() =>
            {
                try
                {
                    // Sequential across drives on this one background thread - each drive's own
                    // GetChanges/ResolvePath calls are already what makes a SINGLE drive's poll
                    // "blocking work", so running N drives one after another here just extends
                    // the batch a bit rather than adding real parallelism complexity for what's
                    // still a periodic background tick, not something latency-sensitive.
                    foreach (var kvp in journals)
                        PollUsnJournalBackground(kvp.Key, kvp.Value);
                }
                finally
                {
                    _usnPollRunning = false;
                }
            });
        }

        private void PollUsnJournalBackground(string driveLetter, UsnJournal journal)
        {
            long lastUsn = _lastUsnByDrive.TryGetValue(driveLetter, out var l) ? l : 0;

            List<UsnChange> changes;
            try
            {
                changes = journal.GetChanges(lastUsn);
            }
            catch (UsnJournalInvalidatedException)
            {
                // Journal wrapped or was recreated since our cursor was captured - the only safe
                // recovery is a full re-scan (of every selected drive, not just this one - simpler
                // than reloading a single drive mid-multi-drive-index, and LoadFileSystemData's
                // _isIndexing guard means this is harmless even if it fires more than once).
                Trace.WriteLine("USN journal invalidated for drive " + driveLetter + "; falling back to full rescan.");
                Dispatcher.BeginInvoke(new Action(() => LoadFileSystemData(_currentDrives)));
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("USN journal poll failed for drive " + driveLetter + ": " + ex);
                return;
            }

            if (changes.Count == 0) return;

            var incFolders = GetIncludedFolders();
            var excFolders = GetExcludedFolders();

            bool changed = false;
            try
            {
                lock (_allItemsLock)
                {
                    foreach (var change in changes)
                        changed |= ApplyUsnChange(driveLetter, journal, change, incFolders, excFolders);

                    if (changed)
                        _cachedSnapshot = _allItems.ToArray();
                }
            }
            catch (ObjectDisposedException)
            {
                // The journal (and its underlying volume handle) was disposed mid-poll - most
                // likely the window closed while this background poll was still resolving a
                // changed file's path. Nothing to recover: just stop, there's no window left
                // to update anyway.
                return;
            }

            _lastUsnByDrive[driveLetter] = changes[changes.Count - 1].Usn + 1;

            if (!changed) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.IsNullOrWhiteSpace(txtSearch.Text))
                {
                    _readyStatusText = string.Format("Ready — Indexed {0:N0} files", _allItems.Count);
                    HideStatusSpinner();
                    lblStatus.Text = _readyStatusText;
                }
                else if (!_searchInProgress)
                {
                    // Only refresh visible results if nothing is currently searching - never
                    // cancel/restart an in-flight search (especially a slow content: search)
                    // just because routine background file activity touched the index.
                    TriggerSearch();
                }
            }));
        }

        /// <summary>
        /// Applies one USN change record to <see cref="_allItems"/> in place. Must be called
        /// with <see cref="_allItemsLock"/> held. Returns true if the in-memory index changed.
        /// </summary>
        private bool ApplyUsnChange(string driveLetter, UsnJournal journal, UsnChange change, List<string> incFolders, List<string> excFolders)
        {
            uint nodeIndex = (uint)(change.FileReferenceNumber & 0xFFFFFFFF);

            bool isRemoval =
                (change.Reason & (UsnJournal.UsnReason.FileDelete | UsnJournal.UsnReason.RenameOldName)) != 0;

            if (isRemoval)
            {
                return _allItems.RemoveAll(fi => fi.NodeIndex == nodeIndex && SameDrive(fi, driveLetter)) > 0;
            }

            // Only bother resolving the path for reasons that can actually affect what we show.
            const UsnJournal.UsnReason relevantReasons =
                UsnJournal.UsnReason.FileCreate | UsnJournal.UsnReason.RenameNewName |
                UsnJournal.UsnReason.DataExtend | UsnJournal.UsnReason.DataTruncation |
                UsnJournal.UsnReason.DataOverwrite | UsnJournal.UsnReason.BasicInfoChange;

            if ((change.Reason & relevantReasons) == 0)
                return false;

            string fullName = journal.ResolvePath(change.FileReferenceNumber);
            var existingIndex = _allItems.FindIndex(fi => fi.NodeIndex == nodeIndex && SameDrive(fi, driveLetter));

            if (fullName == null || !IsPathIndexable(fullName, incFolders, excFolders))
            {
                // File vanished or moved out of scope since the change was recorded.
                if (existingIndex >= 0)
                {
                    _allItems.RemoveAt(existingIndex);
                    return true;
                }
                return false;
            }

            long size = 0;
            DateTime lastWrite = DateTime.Now;
            try
            {
                var info = new FileInfo(fullName);
                if (info.Exists)
                {
                    size = info.Length;
                    lastWrite = info.LastWriteTime;
                }
            }
            catch { /* best-effort metadata refresh */ }

            if (existingIndex >= 0)
            {
                var item = _allItems[existingIndex];
                item.UpdatePath(fullName, Path.GetFileName(fullName));
                item.Size = size;
                item.LastWriteTime = lastWrite;
            }
            else
            {
                _allItems.Add(new FileItem
                {
                    NodeIndex = nodeIndex,
                    FullName = fullName,
                    FileName = Path.GetFileName(fullName),
                    Size = size,
                    LastWriteTime = lastWrite,
                    IsDirectory = change.IsDirectory
                });
            }

            return true;
        }

        // No longer takes a driveLetter - it only ever watches fixed special folders (Desktop,
        // Documents, Downloads, custom monitored folders) that don't depend on which drive(s) are
        // indexed, so the parameter was unused.
        private void SetupFileSystemWatcher()
        {
            // Dispose existing watchers
            if (_watchers != null)
            {
                foreach (var w in _watchers)
                {
                    try {
                        w.EnableRaisingEvents = false;
                        w.Dispose();
                    } catch { }
                }
                _watchers.Clear();
            }
            
            if (!AppSettings.Instance.EnableRealtimeWatcher)
            {
                return;
            }

            // Determine target folders to watch
            var targetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string customFolders = AppSettings.Instance.MonitoredFolders;
            if (!string.IsNullOrWhiteSpace(customFolders))
            {
                var splits = customFolders.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in splits)
                {
                    string trimmed = s.Trim();
                    if (Directory.Exists(trimmed))
                        targetFolders.Add(Path.GetFullPath(trimmed));
                }
            }

            // Always ensure key user work directories (Desktop, Documents, Downloads) are monitored by default
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                    targetFolders.Add(Path.GetFullPath(desktop));

                string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (!string.IsNullOrEmpty(commonDesktop) && Directory.Exists(commonDesktop))
                    targetFolders.Add(Path.GetFullPath(commonDesktop));

                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents))
                    targetFolders.Add(Path.GetFullPath(documents));

                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                {
                    string downloads = Path.Combine(userProfile, "Downloads");
                    if (Directory.Exists(downloads))
                        targetFolders.Add(Path.GetFullPath(downloads));
                }
            }
            catch { }

            foreach (var folder in targetFolders)
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder);
                    watcher.IncludeSubdirectories = true;
                    watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
                    
                    watcher.Created += (s, e) => {
                        if (IsIgnoredSystemPath(e.FullPath)) return;
                        lock (_fswLock) { _fswCreated.Add(e.FullPath); _fswDeleted.Remove(e.FullPath); }
                    };
                    
                    watcher.Deleted += (s, e) => {
                        if (IsIgnoredSystemPath(e.FullPath)) return;
                        lock (_fswLock) { _fswDeleted.Add(e.FullPath); _fswCreated.Remove(e.FullPath); }
                    };
                    
                    watcher.Renamed += (s, e) => {
                        if (!IsIgnoredSystemPath(e.OldFullPath)) {
                            lock (_fswLock) { _fswDeleted.Add(e.OldFullPath); }
                        }
                        if (!IsIgnoredSystemPath(e.FullPath)) {
                            lock (_fswLock) { _fswCreated.Add(e.FullPath); }
                        }
                    };

                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch { }
            }
        }

        private static bool IsIgnoredSystemPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            // Ignore noisy Windows system/temp logs and locks
            if (path.IndexOf(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(@"\Windows\Temp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(@"\Windows\Logs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(@"\Windows\ServiceProfiles", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(@"\System Volume Information", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }
        
        private async void FswTimer_Tick(object sender, EventArgs e)
        {
            if (!AppSettings.Instance.EnableRealtimeWatcher || _allItems == null) return;
            
            List<string> created = null;
            List<string> deleted = null;
            
            lock (_fswLock)
            {
                if (_fswCreated.Count > 0) { created = _fswCreated.ToList(); _fswCreated.Clear(); }
                if (_fswDeleted.Count > 0) { deleted = _fswDeleted.ToList(); _fswDeleted.Clear(); }
            }
            
            if (created == null && deleted == null) return;
            
            // Process collection updates in background thread to avoid freezing UI or spinning CPU
            bool changed = await Task.Run(() =>
            {
                bool hasChanged = false;
                lock (_allItemsLock)
                {
                    if (deleted != null && deleted.Count > 0)
                    {
                        var delSet = new HashSet<string>(deleted, StringComparer.OrdinalIgnoreCase);
                        int removed = _allItems.RemoveAll(x => {
                            if (delSet.Contains(x.FullName)) return true;
                            foreach (var d in deleted) {
                                if (x.FullName.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                            return false;
                        });
                        if (removed > 0) hasChanged = true;
                    }
                    
                    if (created != null && created.Count > 0)
                    {
                        foreach (var c in created)
                        {
                            try {
                                var fi = new FileInfo(c);
                                bool isDir = Directory.Exists(c);
                                if (fi.Exists || isDir) {
                                    string name = Path.GetFileName(c) ?? c;
                                    _allItems.Add(new FileItem { 
                                        FullName = c, 
                                        FileName = name, 
                                        Size = fi.Exists ? fi.Length : 0, 
                                        LastWriteTime = fi.Exists ? fi.LastWriteTime : DateTime.Now,
                                        IsDirectory = isDir
                                    });
                                    hasChanged = true;
                                }
                            } catch { }
                        }
                    }

                    if (hasChanged)
                    {
                        _cachedSnapshot = _allItems.ToArray();
                    }
                }
                return hasChanged;
            });
            
            if (changed) _fswRefreshPending = true;

            if (string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                if (changed)
                {
                    listView.ItemsSource = null;
                    SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    HideStatusSpinner();
                    lblStatus.Text = _readyStatusText;
                    resultsGrid.Visibility = Visibility.Collapsed;
                    _fswRefreshPending = false;
                }
                return;
            }

            // The underlying index (_allItems/_cachedSnapshot, updated above) stays current every
            // tick regardless, but re-running the VISIBLE search and rebuilding the results list
            // on every single tick that sees any disk activity (this timer runs every 1 second)
            // was jarring - routine background noise across the watched folders kept re-flashing
            // the list. Throttle the visible refresh to at most once every 3 seconds; a change
            // that arrives mid-cooldown just sets the pending flag and gets picked up on a later
            // tick once the cooldown clears, rather than being silently dropped.
            if (!_searchInProgress && _fswRefreshPending)
            {
                int now = Environment.TickCount;
                if (now - _lastFswSearchRefreshTicks >= 3000)
                {
                    _lastFswSearchRefreshTicks = now;
                    _fswRefreshPending = false;
                    TriggerSearch();
                }
            }
        }

        private void txtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (btnClear != null)
            {
                btnClear.Visibility = string.IsNullOrEmpty(txtSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            }

            string query = txtSearch.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                HideStatusSpinner();
                lblStatus.Text = _readyStatusText;
            }
            else
            {
                SetStatusColor(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber
                ShowStatusSpinner();
                lblStatus.Text = $"Searching \"{query}\"...";
            }

            // The qualifier/history suggestions popup is a full-drive-search affordance
            // (content:, location:, past global queries) - none of that applies while filtering
            // favorites, so keep it closed there instead of popping up mid-typing.
            if (_currentView == ViewMode.Favorites)
                CloseSuggestionsPopup();
            else
                UpdateSuggestionsPopup();

            // Use higher debounce (450ms) for content searches so keystrokes don't thrash disk I/O
            bool isContentSearch = query.IndexOf("content:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   query.IndexOf("text:", StringComparison.OrdinalIgnoreCase) >= 0;
            int debounceMs = isContentSearch ? 450 : Math.Max(10, AppSettings.Instance.DebounceMs);
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(debounceMs);

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        /// <summary>Set right before a programmatic txtSearch.Focus() call (e.g. when switching
        /// back from Favorites view) that should NOT pop the suggestions/history list open -
        /// that's only wanted when the user actually clicks/tabs into the box themselves.</summary>
        private bool _suppressNextFocusSuggestions = false;

        private void txtSearch_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressNextFocusSuggestions)
            {
                _suppressNextFocusSuggestions = false;
                return;
            }

            // Same reasoning as txtSearch_TextChanged: the qualifier/history popup is a
            // full-drive-search affordance that doesn't apply while filtering favorites.
            if (_currentView == ViewMode.Favorites) return;

            UpdateSuggestionsPopup();
        }

        private void UpdateSuggestionsPopup()
        {
            if (!this.IsVisible)
            {
                CloseSuggestionsPopup();
                return;
            }

            string full = txtSearch.Text;
            int caret = txtSearch.CaretIndex;
            string beforeCaret = (caret >= 0 && caret <= full.Length) ? full.Substring(0, caret) : full;
            
            // If completely empty, show recent search history entries
            if (string.IsNullOrWhiteSpace(full))
            {
                var historyList = AppSettings.Instance.SearchHistory ?? new List<string>();
                if (historyList.Count > 0)
                {
                    var items = historyList.Take(6).Select(h => new SuggestionItem
                    {
                        Title = h,
                        Description = "Recent search",
                        CompletionText = h,
                        IconKind = "History",
                        IsHistory = true
                    }).ToList();

                    lstSuggestions.ItemsSource = items;
                    lstSuggestions.SelectedIndex = -1;
                    popupSuggestions.IsOpen = true;
                    return;
                }
                popupSuggestions.IsOpen = false;
                return;
            }

            // Find current word being typed at caret position
            int lastSpace = beforeCaret.LastIndexOf(' ');
            string currentWord = lastSpace >= 0 ? beforeCaret.Substring(lastSpace + 1) : beforeCaret;

            var suggestions = new List<SuggestionItem>();

            // Case 1: user typed "ext:" or "type:"
            if (currentWord.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) || currentWord.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                int colon = currentWord.IndexOf(':');
                string filterPrefix = currentWord.Substring(0, colon + 1); // "ext:" or "type:"
                string sub = currentWord.Substring(colon + 1).ToLowerInvariant();

                var commonExts = new[] {
                    Tuple.Create("pdf", "PDF Document"),
                    Tuple.Create("exe", "Application"),
                    Tuple.Create("zip", "Compressed Archive"),
                    Tuple.Create("rar", "WinRAR Archive"),
                    Tuple.Create("7z", "7-Zip Archive"),
                    Tuple.Create("docx", "Word Document"),
                    Tuple.Create("xlsx", "Excel Spreadsheet"),
                    Tuple.Create("txt", "Text Document"),
                    Tuple.Create("png", "PNG Image"),
                    Tuple.Create("jpg", "JPEG Image"),
                    Tuple.Create("mp4", "MP4 Video"),
                    Tuple.Create("mp3", "MP3 Audio"),
                    Tuple.Create("iso", "Disk Image"),
                    Tuple.Create("cs", "C# Source Code"),
                    Tuple.Create("sln", "Visual Studio Solution")
                };

                if (filterPrefix.Equals("type:", StringComparison.OrdinalIgnoreCase) || filterPrefix.Equals("kind:", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(sub) || "folder".StartsWith(sub) || "folders".StartsWith(sub))
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = $"{filterPrefix}folder",
                            Description = "Folders only",
                            CompletionText = ReplaceCurrentWord(full, caret, $"{filterPrefix}folder"),
                            IconKind = "Folder"
                        });
                    }
                    if (string.IsNullOrEmpty(sub) || "file".StartsWith(sub) || "files".StartsWith(sub))
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = $"{filterPrefix}file",
                            Description = "Files only",
                            CompletionText = ReplaceCurrentWord(full, caret, $"{filterPrefix}file"),
                            IconKind = "File"
                        });
                    }
                }

                foreach (var ext in commonExts)
                {
                    if (string.IsNullOrEmpty(sub) || ext.Item1.StartsWith(sub))
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = $"{filterPrefix}{ext.Item1}",
                            Description = ext.Item2,
                            CompletionText = ReplaceCurrentWord(full, caret, $"{filterPrefix}{ext.Item1}"),
                            IconKind = "Type"
                        });
                    }
                }

                AddHistoricalFilterSuggestions(suggestions, full, caret, filterPrefix, sub, 3);
            }
            // Case 2: user typed "size:"
            else if (currentWord.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            {
                int colon = currentWord.IndexOf(':');
                string sub = currentWord.Substring(colon + 1).ToLowerInvariant();

                var sizeOptions = new[] {
                    Tuple.Create("size:>1gb", "Files larger than 1 Gigabyte"),
                    Tuple.Create("size:>100mb", "Files larger than 100 Megabytes"),
                    Tuple.Create("size:>10mb", "Files larger than 10 Megabytes"),
                    Tuple.Create("size:<1mb", "Files smaller than 1 Megabyte"),
                    Tuple.Create("size:<500kb", "Files smaller than 500 Kilobytes"),
                    Tuple.Create("size:<100kb", "Small files under 100 Kilobytes")
                };

                foreach (var opt in sizeOptions)
                {
                    if (string.IsNullOrEmpty(sub) || opt.Item1.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = opt.Item1,
                            Description = opt.Item2,
                            CompletionText = ReplaceCurrentWord(full, caret, opt.Item1),
                            IconKind = "Size"
                        });
                    }
                }

                AddHistoricalFilterSuggestions(suggestions, full, caret, "size:", sub, 3);
            }
            // Case 3: user typed "date:" or "modified:"
            else if (currentWord.StartsWith("date:", StringComparison.OrdinalIgnoreCase) || currentWord.StartsWith("modified:", StringComparison.OrdinalIgnoreCase))
            {
                int colon = currentWord.IndexOf(':');
                string filterPrefix = currentWord.Substring(0, colon + 1);
                string sub = currentWord.Substring(colon + 1).ToLowerInvariant();

                var dateOptions = new[] {
                    Tuple.Create($"{filterPrefix}today", "Files modified today"),
                    Tuple.Create($"{filterPrefix}yesterday", "Files modified yesterday"),
                    Tuple.Create($"{filterPrefix}>7d", "Files modified in the last 7 days"),
                    Tuple.Create($"{filterPrefix}>30d", "Files modified in the last 30 days"),
                    Tuple.Create($"{filterPrefix}>2026-01-01", "Files modified after Jan 1, 2026")
                };

                foreach (var opt in dateOptions)
                {
                    if (string.IsNullOrEmpty(sub) || opt.Item1.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = opt.Item1,
                            Description = opt.Item2,
                            CompletionText = ReplaceCurrentWord(full, caret, opt.Item1),
                            IconKind = "Date"
                        });
                    }
                }

                AddHistoricalFilterSuggestions(suggestions, full, caret, filterPrefix, sub, 3);
            }
            // Case 4: user typed "location:" or "path:" or "in:"
            else if (currentWord.StartsWith("location:", StringComparison.OrdinalIgnoreCase) || currentWord.StartsWith("path:", StringComparison.OrdinalIgnoreCase) || currentWord.StartsWith("in:", StringComparison.OrdinalIgnoreCase))
            {
                int colon = currentWord.IndexOf(':');
                string filterPrefix = currentWord.Substring(0, colon + 1);
                string sub = currentWord.Substring(colon + 1).ToLowerInvariant();

                var locOptions = new[] {
                    Tuple.Create($"{filterPrefix}desktop", "Search only in Desktop"),
                    Tuple.Create($"{filterPrefix}downloads", "Search only in Downloads folder"),
                    Tuple.Create($"{filterPrefix}documents", "Search only in Documents folder"),
                    Tuple.Create($"{filterPrefix}temp", "Search only in Temp directory"),
                    Tuple.Create($"{filterPrefix}user", "Search only in user profile")
                };

                foreach (var opt in locOptions)
                {
                    if (string.IsNullOrEmpty(sub) || opt.Item1.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = opt.Item1,
                            Description = opt.Item2,
                            CompletionText = ReplaceCurrentWord(full, caret, opt.Item1),
                            IconKind = "Folder"
                        });
                    }
                }

                AddHistoricalFilterSuggestions(suggestions, full, caret, filterPrefix, sub, 3);
            }
            // Case 4.5: user typed "drive:" - suggest the letters actually enabled in Settings
            else if (currentWord.StartsWith("drive:", StringComparison.OrdinalIgnoreCase))
            {
                int colon = currentWord.IndexOf(':');
                string filterPrefix = currentWord.Substring(0, colon + 1);
                string sub = currentWord.Substring(colon + 1).ToLowerInvariant();

                foreach (var letter in GetConfiguredDrives())
                {
                    string opt = filterPrefix + letter.ToLowerInvariant();
                    if (string.IsNullOrEmpty(sub) || opt.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = opt,
                            Description = "Search only on " + letter.ToUpperInvariant() + ":",
                            CompletionText = ReplaceCurrentWord(full, caret, opt),
                            IconKind = "Filter"
                        });
                    }
                }
            }
            // Case 5: user is typing prefixes like "e", "s", "d", "m", "l", "p"
            else if (currentWord.Length > 0 && !currentWord.Contains(":"))
            {
                var filterHints = new[] {
                    Tuple.Create("location:", "Filter by folder path (e.g. location:desktop, location:temp)", "Folder"),
                    Tuple.Create("path:", "Filter by folder path (e.g. path:downloads)", "Folder"),
                    Tuple.Create("content:", "Full-text search inside file contents (e.g. content:password)", "Filter"),
                    Tuple.Create("folder:", "Filter folders only", "Folder"),
                    Tuple.Create("file:", "Filter files only", "File"),
                    Tuple.Create("ext:", "Filter by file extension (e.g. ext:pdf, ext:exe)", "Filter"),
                    Tuple.Create("type:", "Filter by type (e.g. type:folder, type:file, type:pdf)", "Filter"),
                    Tuple.Create("size:", "Filter by file size (e.g. size:>10mb, size:<1gb)", "Filter"),
                    Tuple.Create("date:", "Filter by modification date (e.g. date:today, date:>7d)", "Filter"),
                    Tuple.Create("drive:", "Restrict to one indexed drive (e.g. drive:d)", "Filter")
                };

                foreach (var hint in filterHints)
                {
                    if (hint.Item1.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase))
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = hint.Item1,
                            Description = hint.Item2,
                            // No trailing space: hint.Item1 is a bare qualifier ("drive:",
                            // "location:", ...) still awaiting its value, which should stay
                            // smushed onto the same word (so typing right after it continues
                            // triggering that qualifier's OWN value autocomplete, e.g. Case 4.5
                            // for drive:) - a trailing space here would silently start a brand
                            // new word instead, falling through to this same generic hint list
                            // again for whatever gets typed next.
                            CompletionText = ReplaceCurrentWord(full, caret, hint.Item1, addTrailingSpace: false),
                            IconKind = hint.Item3
                        });

                        // Also surface values previously typed for this filter, e.g. typing
                        // "loc" suggests "location:desktop" if that's been searched before.
                        AddHistoricalFilterSuggestions(suggestions, full, caret, hint.Item1, "", 2);
                    }
                }
            }

            // Also check if any recent history matches the current query
            var history = AppSettings.Instance.SearchHistory ?? new List<string>();
            foreach (var h in history)
            {
                if (h.IndexOf(full.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 && !h.Equals(full.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    suggestions.Add(new SuggestionItem
                    {
                        Title = h,
                        Description = "Recent search",
                        CompletionText = h,
                        IconKind = "History",
                        IsHistory = true
                    });
                    if (suggestions.Count >= 7) break;
                }
            }

            if (suggestions.Count > 0)
            {
                lstSuggestions.ItemsSource = suggestions;
                lstSuggestions.SelectedIndex = -1;
                popupSuggestions.IsOpen = true;
            }
            else
            {
                popupSuggestions.IsOpen = false;
            }
        }

        private static string ReplaceCurrentWord(string full, int caret, string replacement)
        {
            return ReplaceCurrentWord(full, caret, replacement, addTrailingSpace: true);
        }

        private static string ReplaceCurrentWord(string full, int caret, string replacement, bool addTrailingSpace)
        {
            if (caret < 0 || caret > full.Length) caret = full.Length;
            string before = full.Substring(0, caret);
            string after = full.Substring(caret);

            int lastSpace = before.LastIndexOf(' ');
            string newBefore = (lastSpace >= 0 ? before.Substring(0, lastSpace + 1) : "") + replacement;

            // Add trailing space for convenience if not already there
            if (addTrailingSpace && !after.StartsWith(" "))
                newBefore += " ";

            return newBefore + after;
        }

        /// <summary>
        /// Mines past search history for every distinct value the user has previously typed
        /// after a given filter qualifier (e.g. "location:") - so "location:desktop" that was
        /// typed as part of some earlier query can be recalled as a suggestion later, even if
        /// that exact full query was never repeated. Newest usage first.
        /// </summary>
        private static List<string> GetHistoricalFilterValues(string filterPrefix)
        {
            var history = AppSettings.Instance.SearchHistory ?? new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<string>();

            foreach (var entry in history)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var tokens = entry.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < tokens.Length; i++)
                {
                    string tok = tokens[i];
                    string combined = null;

                    if (tok.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
                    {
                        // "location:" "desktop" written as two tokens
                        combined = filterPrefix + tokens[i + 1];
                    }
                    else if (tok.Length > filterPrefix.Length && tok.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // "location:desktop" written as one token
                        combined = tok;
                    }

                    if (combined != null && seen.Add(combined))
                        results.Add(combined);
                }
            }

            return results;
        }

        /// <summary>
        /// Appends up to <paramref name="maxToAdd"/> previously-used values for
        /// <paramref name="filterPrefix"/> (optionally narrowed to those containing
        /// <paramref name="sub"/>) to <paramref name="suggestions"/>, skipping anything
        /// already present (e.g. a preset option that happens to match).
        /// </summary>
        private void AddHistoricalFilterSuggestions(List<SuggestionItem> suggestions, string full, int caret, string filterPrefix, string sub, int maxToAdd)
        {
            int added = 0;
            foreach (var val in GetHistoricalFilterValues(filterPrefix))
            {
                if (added >= maxToAdd) break;

                string valuePart = val.Substring(filterPrefix.Length);
                if (!string.IsNullOrEmpty(sub) && valuePart.IndexOf(sub, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string completion = ReplaceCurrentWord(full, caret, val);
                if (suggestions.Any(s => string.Equals(s.CompletionText, completion, StringComparison.OrdinalIgnoreCase)))
                    continue;

                suggestions.Add(new SuggestionItem
                {
                    Title = val,
                    Description = "Used before",
                    CompletionText = completion,
                    IconKind = "History"
                });
                added++;
            }
        }

        private async void txtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (popupSuggestions.IsOpen && lstSuggestions.Items.Count > 0)
            {
                if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    int next = lstSuggestions.SelectedIndex + 1;
                    if (next >= lstSuggestions.Items.Count) next = 0;
                    lstSuggestions.SelectedIndex = next;
                    lstSuggestions.ScrollIntoView(lstSuggestions.SelectedItem);
                    return;
                }
                else if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    int prev = lstSuggestions.SelectedIndex - 1;
                    if (prev < 0) prev = lstSuggestions.Items.Count - 1;
                    lstSuggestions.SelectedIndex = prev;
                    lstSuggestions.ScrollIntoView(lstSuggestions.SelectedItem);
                    return;
                }
                else if (e.Key == Key.Tab || (e.Key == Key.Enter && lstSuggestions.SelectedIndex >= 0 && !AppSettings.Instance.QuickStartFirstResult))
                {
                    e.Handled = true;
                    if (lstSuggestions.SelectedItem is SuggestionItem item)
                    {
                        ApplySuggestion(item);
                    }
                    else if (lstSuggestions.Items.Count > 0)
                    {
                        ApplySuggestion((SuggestionItem)lstSuggestions.Items[0]);
                    }
                    return;
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    popupSuggestions.IsOpen = false;
                    return;
                }
            }

            if (e.Key == Key.Down && listView.Items.Count > 0 && (!popupSuggestions.IsOpen || lstSuggestions.SelectedIndex < 0))
            {
                e.Handled = true;
                listView.Focus();
                if (listView.SelectedIndex < 0) listView.SelectedIndex = 0;
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                popupSuggestions.IsOpen = false;
                string q = txtSearch.Text.Trim();
                if (!string.IsNullOrEmpty(q))
                {
                    SaveSearchHistory(q);
                }

                // If debounce is still pending or results are not yet populated, run search immediately
                if (_debounceTimer.IsEnabled)
                {
                    _debounceTimer.Stop();
                    await ApplyFilterAsync(q);
                }

                // If user hits Enter directly in search box, launch the top result (just like Spotlight / Everything)
                if (listView.Items.Count > 0)
                {
                    FileItem topItem = (listView.SelectedItem as FileItem) ?? (listView.Items[0] as FileItem);
                    if (topItem != null)
                    {
                        ExecuteItem(topItem);
                    }
                }
            }
        }

        private void lstSuggestions_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null && !(element is ListBoxItem))
            {
                // The remove-history "X" is inside the same ListBoxItem it removes - let its own
                // MouseLeftButtonUp handler (RemoveHistoryItem_Click) run instead of applying the
                // suggestion underneath it, since this PreviewMouseDown (tunneling, attached to
                // the ListBox itself) would otherwise see the click first.
                if (element is FrameworkElement fe && (fe.Tag as string) == "RemoveHistory") return;
                element = VisualTreeHelper.GetParent(element);
            }

            if (element is ListBoxItem item && item.DataContext is SuggestionItem suggestion)
            {
                e.Handled = true;
                ApplySuggestion(suggestion);
            }
        }

        private void RemoveHistoryItem_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is SuggestionItem suggestion)) return;

            // Raw pixel VerticalOffset capture/restore (the first attempt at this) didn't hold up
            // across the ItemsSource swap UpdateSuggestionsPopup does - remembering WHICH item was
            // at this position and scrolling that item back into view after the rebuild is more
            // robust than a pixel offset, since it doesn't depend on row-height/extent staying
            // identical across the swap.
            int removedIndex = lstSuggestions.Items.IndexOf(suggestion);

            AppSettings.Instance.SearchHistory?.RemoveAll(x => string.Equals(x, suggestion.CompletionText, StringComparison.OrdinalIgnoreCase));
            AppSettings.Save();
            UpdateSuggestionsPopup();

            if (removedIndex >= 0 && lstSuggestions.Items.Count > 0)
            {
                int targetIndex = Math.Min(removedIndex, lstSuggestions.Items.Count - 1);
                var target = lstSuggestions.Items[targetIndex];
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    lstSuggestions.ScrollIntoView(target);
                }));
            }
        }

        private void ApplySuggestion(SuggestionItem suggestion)
        {
            if (suggestion == null) return;
            txtSearch.Text = suggestion.CompletionText;
            txtSearch.CaretIndex = txtSearch.Text.Length;
            txtSearch.Focus();
            popupSuggestions.IsOpen = false;
        }

        private void SaveSearchHistory(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            if (AppSettings.Instance.SearchHistory == null)
                AppSettings.Instance.SearchHistory = new List<string>();

            // Remove existing duplicate
            AppSettings.Instance.SearchHistory.RemoveAll(x => string.Equals(x, query, StringComparison.OrdinalIgnoreCase));
            // Insert at top
            AppSettings.Instance.SearchHistory.Insert(0, query);
            // Limit to last 20
            if (AppSettings.Instance.SearchHistory.Count > 20)
                AppSettings.Instance.SearchHistory = AppSettings.Instance.SearchHistory.Take(20).ToList();

            AppSettings.Save();
        }

        private void DebounceTimer_Tick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            TriggerSearch();
        }

        private void TriggerSearch()
        {
            string query = txtSearch.Text.Trim();
            if (_currentView == ViewMode.Favorites)
            {
                FilterFavorites(query);
                return;
            }

            // _allItems/_cachedSnapshot are only assigned once, right at the end of the initial
            // MFT/service load (see LoadFileSystemData) - searching before that finishes runs
            // against a stale/empty snapshot and flashes "No results" for a query that actually
            // does match, a few seconds before LoadFileSystemData's own TriggerSearch() call
            // (once loading completes) shows the real results. Skip running the search now and
            // let that same call pick up whatever's currently typed once indexing is done,
            // instead of showing a misleading empty result in between.
            if (_isIndexing) return;

            _ = ApplyFilterAsync(query);
        }

        private List<FileItem> _favoritesSearchMatches = new List<FileItem>();

        /// <summary>
        /// Search-box filtering while in the Favorites view. "Results" behaves as a real,
        /// selectable tab alongside the group tabs (see CreateSearchResultsTabChip) rather than
        /// a mode that takes over the whole view: typing a query computes the cross-group match
        /// list and auto-switches TO the Results tab, but the user can then click any group tab
        /// to browse it normally (the search box is left untouched) and click back onto Results
        /// later to return to the same match list without re-searching. Clearing the box drops
        /// the Results tab entirely and falls back to whichever group tab was last selected.
        /// </summary>
        private void FilterFavorites(string query)
        {
            bool wasActive = _favoritesSearchActive;
            _favoritesSearchActive = !string.IsNullOrWhiteSpace(query);

            if (!_favoritesSearchActive)
            {
                _favoritesShowingResultsTab = false;
                _favoritesSearchMatches = new List<FileItem>();
                SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                HideStatusSpinner();
                lblStatus.Text = _readyStatusText;
            }
            else
            {
                // A brand new search (box just went from empty to non-empty) always jumps to the
                // Results tab, since that's the whole point of typing - but a query edited WHILE
                // the user is looking at a group tab (search box untouched, they clicked away)
                // stays on that group; only auto-switches back once they return to Results
                // themselves.
                if (!wasActive) _favoritesShowingResultsTab = true;

                var matches = new List<FileItem>();
                if (FavoriteGroups != null)
                {
                    foreach (var group in FavoriteGroups)
                    {
                        if (group?.ItemPaths == null) continue;
                        foreach (var path in group.ItemPaths)
                        {
                            string name = Path.GetFileName(path.TrimEnd('\\'));
                            if (string.IsNullOrEmpty(name)) name = path;
                            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            var fi = BuildFileItemForPath(path);
                            if (fi != null) matches.Add(fi);
                        }
                    }
                }
                // Force each icon to load now rather than leaving it to the ItemTemplate's
                // {Binding Icon} to trigger lazily during layout - FileItem.Icon has no
                // INotifyPropertyChanged, so a binding only ever reads it once, at first
                // evaluation; computing it up front removes any doubt about whether that first
                // read landed before or after the icon was actually ready.
                foreach (var m in matches) _ = m.Icon;
                _favoritesSearchMatches = matches;

                HideStatusSpinner();
                if (matches.Count > 0)
                {
                    SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    lblStatus.Text = $"Found {matches.Count:N0} match{(matches.Count == 1 ? "" : "es")} in Favorites";
                }
                else
                {
                    SetStatusColor(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF)); // Gray
                    lblStatus.Text = $"No favorites found for \"{query}\"";
                }
            }

            RefreshFavoritesView();
        }

        /// <summary>
        /// Shows whichever of (a) the Results list or (b) the active group's bubble grid matches
        /// the current tab selection - called after any change that could affect which one that
        /// should be (a new search, a tab click, a group create/rename/delete).
        /// </summary>
        private void UpdateFavoritesContentDisplay()
        {
            // Results now renders through the same bubble-tile grid as every other tab (see
            // RefreshFavoritesGrid) instead of a separate ListBox, so group tabs and the Results
            // tab look identical - just fed from a different source.
            favoritesGrid.Visibility = Visibility.Visible;
            RefreshFavoritesGrid();

            if (_favoritesSearchActive && !_favoritesShowingResultsTab)
            {
                // Otherwise a stale "Found N matches"/"No favorites found" from the last search
                // stays on screen while actually browsing a group tab's own grid, which reads as
                // if the grid below it is somehow still "the search result".
                SetStatusColor(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF)); // Gray
                lblStatus.Text = "Viewing \"" + (ActiveFavoriteGroup?.Name ?? "Favorites") + "\" - search active on Results tab";
            }
        }

        private void FavoritesSearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (favoritesSearchResultsList.SelectedItem is FileItem item)
                ExecuteItem(item);
        }

        /// <summary>
        /// Resolves a location alias to every path it could plausibly mean, since some "one
        /// folder" the user thinks of are actually two+ separate folders on disk that Explorer
        /// merges in its UI - most notably Desktop: %USERPROFILE%\Desktop holds the current
        /// user's own icons, but many installers (Git for Windows among them) put their shortcut
        /// in the shared C:\Users\Public\Desktop instead, and Explorer shows both together as one
        /// "Desktop". Matching only the first would silently miss anything from the second.
        /// </summary>
        private List<string> ResolveLocationCandidates(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string key = input.Trim().ToLowerInvariant();
            var result = new List<string>();

            switch (key)
            {
                case "desktop":
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
                    if (result.Count == 0) result.Add("Desktop");
                    break;
                case "downloads":
                case "download":
                    string userProf = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string dl = System.IO.Path.Combine(userProf, "Downloads");
                    result.Add(System.IO.Directory.Exists(dl) ? dl : "Downloads");
                    break;
                case "documents":
                case "doc":
                case "docs":
                case "mydocuments":
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments));
                    if (result.Count == 0) result.Add("Documents");
                    break;
                case "pictures":
                case "pics":
                case "photos":
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonPictures));
                    if (result.Count == 0) result.Add("Pictures");
                    break;
                case "music":
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonMusic));
                    if (result.Count == 0) result.Add("Music");
                    break;
                case "videos":
                case "video":
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos));
                    if (result.Count == 0) result.Add("Videos");
                    break;
                case "startmenu":
                case "start menu":
                case "start":
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
                    AddIfExists(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
                    if (result.Count == 0) result.Add("Start Menu");
                    break;
                case "temp":
                case "tmp":
                    result.Add(System.IO.Path.GetTempPath().TrimEnd('\\'));
                    break;
                case "user":
                case "profile":
                case "home":
                    result.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    break;
                case "system":
                case "system32":
                    result.Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
                    break;
                case "programfiles":
                case "programs":
                    result.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
                    break;
                default:
                    result.Add(input.Trim());
                    break;
            }

            return result;
        }

        private static void AddIfExists(List<string> list, string path)
        {
            if (!string.IsNullOrEmpty(path) && System.IO.Directory.Exists(path))
                list.Add(path);
        }

        /// <summary>
        /// Returns a filename-prefix trie built from <paramref name="snapshot"/>, reusing the
        /// previously-built one when the snapshot instance hasn't changed (snapshots are
        /// replaced wholesale on reindex/incremental update, so reference equality is enough
        /// to detect staleness cheaply).
        /// </summary>
        private FileNameTrie GetOrBuildNameTrie(FileItem[] snapshot)
        {
            lock (_nameTrieLock)
            {
                if (!ReferenceEquals(_nameTrieSourceSnapshot, snapshot))
                {
                    _nameTrie = new FileNameTrie(snapshot);
                    _nameTrieSourceSnapshot = snapshot;
                }
                return _nameTrie;
            }
        }

        private async Task ApplyFilterAsync(string pattern)
        {
            // Belt-and-suspenders: the search box is disabled and cleared while in Favorites
            // view (see SwitchToView), but this guards against any other codepath (a stray
            // debounce tick, a background USN-triggered refresh) ever showing search results
            // on top of the favorites grid.
            if (_currentView != ViewMode.Search) return;

            _searchCancellationTokenSource?.Cancel();
            _searchCancellationTokenSource = new CancellationTokenSource();
            var token = _searchCancellationTokenSource.Token;

            if (string.IsNullOrWhiteSpace(pattern))
            {
                listView.ItemsSource = null;
                SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    HideStatusSpinner();
                lblStatus.Text = _readyStatusText;
                resultsGrid.Visibility = Visibility.Collapsed;
                return;
            }
            
            pattern = pattern.Trim();

            _searchInProgress = true;

            FileItem[] snapshot = _cachedSnapshot;
            if (snapshot == null || snapshot.Length == 0)
            {
                lock (_allItemsLock)
                {
                    snapshot = _allItems?.ToArray() ?? new FileItem[0];
                    _cachedSnapshot = snapshot;
                }
            }
            if (snapshot.Length == 0) return;

            // Parse filter qualifiers: ext:, type:, size:, date:, modified:
            // Allow both "ext:exe" and "ext: exe" with optional spaces
            var extFilters = new List<string>();
            bool? folderFilter = null; // true = folders only, false = files only
            char? driveFilter = null; // restricts matches to this drive letter, e.g. "drive:d"
            List<string> locationFilters = null;
            string contentQuery = null;
            long? minSize = null;
            long? maxSize = null;
            DateTime? minDate = null;
            DateTime? maxDate = null;
            var textTerms = new List<string>();

            string[] tokens = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int t = 0; t < tokens.Length; t++)
            {
                string tok = tokens[t];
                string nextTok = (t + 1 < tokens.Length) ? tokens[t + 1] : null;

                if (tok.Equals("folder:", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("folders:", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("dir:", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("directory:", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("directories:", StringComparison.OrdinalIgnoreCase))
                {
                    folderFilter = true;
                }
                else if (tok.Equals("file:", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("files:", StringComparison.OrdinalIgnoreCase))
                {
                    folderFilter = false;
                }
                // "file:<name>" / "folder:<name>" - restrict to files/folders AND search by name
                // in one qualifier (e.g. "file:aicon*"). Without this, "file:" only matched as its
                // own bare token (files-only, no name filter), so anything typed straight after
                // the colon fell through to the catch-all below and was searched for literally -
                // "file:aicon*" would look for a filename that literally contains "file:aicon*",
                // which of course never matches.
                else if (tok.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && tok.Length > "file:".Length)
                {
                    folderFilter = false;
                    textTerms.Add(tok.Substring("file:".Length));
                }
                else if (tok.StartsWith("files:", StringComparison.OrdinalIgnoreCase) && tok.Length > "files:".Length)
                {
                    folderFilter = false;
                    textTerms.Add(tok.Substring("files:".Length));
                }
                else if (tok.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) && tok.Length > "folder:".Length)
                {
                    folderFilter = true;
                    textTerms.Add(tok.Substring("folder:".Length));
                }
                else if (tok.StartsWith("folders:", StringComparison.OrdinalIgnoreCase) && tok.Length > "folders:".Length)
                {
                    folderFilter = true;
                    textTerms.Add(tok.Substring("folders:".Length));
                }
                else if (tok.Equals("kind:folder", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("type:folder", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("kind:folders", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("type:folders", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("kind:dir", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("type:dir", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("kind:directory", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("type:directory", StringComparison.OrdinalIgnoreCase))
                {
                    folderFilter = true;
                }
                else if (tok.Equals("kind:file", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("type:file", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("kind:files", StringComparison.OrdinalIgnoreCase) ||
                         tok.Equals("type:files", StringComparison.OrdinalIgnoreCase))
                {
                    folderFilter = false;
                }
                else if (tok.Equals("ext:", StringComparison.OrdinalIgnoreCase) || tok.Equals("type:", StringComparison.OrdinalIgnoreCase) || tok.Equals("kind:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(nextTok))
                    {
                        string val = nextTok.Trim();
                        if (val.Equals("folder", StringComparison.OrdinalIgnoreCase) || val.Equals("folders", StringComparison.OrdinalIgnoreCase) || val.Equals("dir", StringComparison.OrdinalIgnoreCase) || val.Equals("directory", StringComparison.OrdinalIgnoreCase))
                        {
                            folderFilter = true;
                        }
                        else if (val.Equals("file", StringComparison.OrdinalIgnoreCase) || val.Equals("files", StringComparison.OrdinalIgnoreCase))
                        {
                            folderFilter = false;
                        }
                        else
                        {
                            extFilters.Add(val.TrimStart('.').ToUpperInvariant());
                        }
                        t++; // Consume next token
                    }
                }
                else if (tok.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("type:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = tok.IndexOf(':');
                    string val = tok.Substring(colon + 1).Trim();
                    if (val.Equals("folder", StringComparison.OrdinalIgnoreCase) || val.Equals("folders", StringComparison.OrdinalIgnoreCase) || val.Equals("dir", StringComparison.OrdinalIgnoreCase) || val.Equals("directory", StringComparison.OrdinalIgnoreCase))
                    {
                        folderFilter = true;
                    }
                    else if (val.Equals("file", StringComparison.OrdinalIgnoreCase) || val.Equals("files", StringComparison.OrdinalIgnoreCase))
                    {
                        folderFilter = false;
                    }
                    else if (!string.IsNullOrEmpty(val))
                    {
                        extFilters.Add(val.TrimStart('.').ToUpperInvariant());
                    }
                }
                else if (tok.Equals("size:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(nextTok))
                    {
                        ParseSizeFilter(nextTok, ref minSize, ref maxSize);
                        t++; // Consume next token
                    }
                }
                else if (tok.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = tok.IndexOf(':');
                    string val = tok.Substring(colon + 1).Trim();
                    ParseSizeFilter(val, ref minSize, ref maxSize);
                }
                else if (tok.Equals("date:", StringComparison.OrdinalIgnoreCase) || tok.Equals("modified:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(nextTok))
                    {
                        ParseDateFilter(nextTok, ref minDate, ref maxDate);
                        t++; // Consume next token
                    }
                }
                else if (tok.StartsWith("date:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("modified:", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = tok.IndexOf(':');
                    string val = tok.Substring(colon + 1).Trim();
                    ParseDateFilter(val, ref minDate, ref maxDate);
                }
                else if (tok.Equals("content:", StringComparison.OrdinalIgnoreCase) || tok.Equals("text:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(nextTok))
                    {
                        contentQuery = nextTok.Trim('\"', '\'');
                        t++; // Consume next token
                    }
                }
                else if (tok.StartsWith("content:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = tok.IndexOf(':');
                    string val = tok.Substring(colon + 1).Trim().Trim('\"', '\'');
                    if (!string.IsNullOrEmpty(val)) contentQuery = val;
                }
                else if (tok.Equals("drive:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(nextTok))
                    {
                        driveFilter = ParseDriveFilter(nextTok);
                        t++; // Consume next token
                    }
                }
                else if (tok.StartsWith("drive:", StringComparison.OrdinalIgnoreCase) && tok.Length > "drive:".Length)
                {
                    driveFilter = ParseDriveFilter(tok.Substring("drive:".Length));
                }
                else if (tok.Equals("location:", StringComparison.OrdinalIgnoreCase) || tok.Equals("path:", StringComparison.OrdinalIgnoreCase) || tok.Equals("in:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(nextTok))
                    {
                        locationFilters = ResolveLocationCandidates(nextTok.Trim('\"', '\''));
                        t++; // Consume next token
                    }
                }
                else if (tok.StartsWith("location:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("path:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("in:", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = tok.IndexOf(':');
                    string val = tok.Substring(colon + 1).Trim().Trim('\"', '\'');
                    if (!string.IsNullOrEmpty(val)) locationFilters = ResolveLocationCandidates(val);
                }
                else
                {
                    textTerms.Add(tok);
                }
            }

            string primaryQuery = string.Join(" ", textTerms).Trim();
            string searchLower = primaryQuery.ToLowerInvariant();
            string mode = AppSettings.Instance.SearchMode ?? "Contains";

            Regex regex = null;
            if (mode.Equals("Regex", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(primaryQuery))
            {
                try {
                    regex = new Regex(primaryQuery, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                } catch {
                    // Invalid regex, treat as standard contains
                    regex = null;
                }
            }

            bool isWildcard = (mode.Equals("Contains", StringComparison.OrdinalIgnoreCase) || mode.Equals("StartsWith", StringComparison.OrdinalIgnoreCase)) 
                              && (primaryQuery.Contains("*") || primaryQuery.Contains("?"));
            string[] parts = isWildcard ? searchLower.Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries) : null;
            bool endsWithWildcard = isWildcard && searchLower.EndsWith("*");
            bool startsWithWildcard = isWildcard && searchLower.StartsWith("*");
            string lastPart = (parts != null && parts.Length > 0) ? parts[parts.Length - 1] : null;
            string firstPart = (parts != null && parts.Length > 0) ? parts[0] : null;

            Func<FileItem, bool> matcher = item =>
            {
                // 0. Folder / File type filter
                if (folderFilter.HasValue && item.IsDirectory != folderFilter.Value)
                    return false;

                // 0.05 Drive filter - restricts matches to one of the (possibly several)
                // currently-indexed drives, e.g. "drive:d photos".
                if (driveFilter.HasValue &&
                    (string.IsNullOrEmpty(item.FullName) || char.ToUpperInvariant(item.FullName[0]) != driveFilter.Value))
                    return false;

                // 0.1 Location / Path filter - matches if the item's path contains ANY of the
                // resolved candidate paths/names (see ResolveLocationCandidates: "desktop" alone
                // resolves to both the user's own Desktop and the shared Public Desktop, since
                // Explorer visually merges both into one "Desktop" even though they're different
                // folders on disk - matching only the user's own would silently miss shortcuts an
                // installer placed in the shared one, like a Start Menu app's "Git Bash" icon).
                if (locationFilters != null && locationFilters.Count > 0)
                {
                    string dir = item.DirectoryName;
                    string full = item.FullName;
                    bool anyMatch = false;
                    foreach (var candidate in locationFilters)
                    {
                        if ((dir != null && dir.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (full != null && full.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            anyMatch = true;
                            break;
                        }
                    }
                    if (!anyMatch) return false;
                }

                // 1. Extension filter
                if (extFilters.Count > 0 && !extFilters.Contains(item.Extension))
                    return false;

                // 2. Size filter
                if (minSize.HasValue && item.Size < minSize.Value)
                    return false;
                if (maxSize.HasValue && item.Size > maxSize.Value)
                    return false;

                // 3. Date filter
                if (minDate.HasValue && item.LastWriteTime < minDate.Value)
                    return false;
                if (maxDate.HasValue && item.LastWriteTime > maxDate.Value)
                    return false;

                // If user only specified filters (e.g. "ext:pdf size:>10mb") and no text query, accept item!
                if (string.IsNullOrEmpty(primaryQuery))
                    return true;

                string fn = item.LowerName ?? item.FileName;

                // 4. Text query matching according to selected Search Mode
                if (regex != null)
                {
                    return regex.IsMatch(item.FileName);
                }
                else if (mode.Equals("Exact", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(fn, searchLower, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(item.FileName, primaryQuery, StringComparison.OrdinalIgnoreCase);
                }
                else if (mode.Equals("StartsWith", StringComparison.OrdinalIgnoreCase) && !isWildcard)
                {
                    return fn.StartsWith(searchLower, StringComparison.Ordinal);
                }
                else
                {
                    // Contains or Wildcard
                    if (!isWildcard)
                        return fn.IndexOf(searchLower, StringComparison.Ordinal) >= 0;

                    if (parts.Length == 0) return true;

                    int currentIndex = 0;
                    for (int p = 0; p < parts.Length; p++)
                    {
                        int found = fn.IndexOf(parts[p], currentIndex, StringComparison.Ordinal);
                        if (found == -1) return false;
                        currentIndex = found + parts[p].Length;
                    }

                    if (!endsWithWildcard && !fn.EndsWith(lastPart, StringComparison.Ordinal))
                        return false;

                    if (!startsWithWildcard && !fn.StartsWith(firstPart, StringComparison.Ordinal))
                        return false;

                    return true;
                }
            };

            try
            {
                var matched = await Task.Run(() =>
                {
                    int maxResults = AppSettings.Instance.MaxResults;
                    if (maxResults <= 0) maxResults = 100;
                    
                    var excludeArr = AppSettings.Instance.ExcludedExtensions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().TrimStart('.').ToUpperInvariant()).ToList();
                    var prioArr = AppSettings.Instance.PrioritizedExtensions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().TrimStart('.').ToUpperInvariant()).ToList();
                        
                    var result = new List<FileItem>(maxResults * 2);
                    var prioResult = new List<FileItem>(maxResults);
                    
                    int total = snapshot.Length;

                    if (!string.IsNullOrEmpty(contentQuery))
                    {
                        // Collect candidates matching name/extension/date/size filters first
                        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        var userCandidates = new List<FileItem>(4000);
                        var otherCandidates = new List<FileItem>(10000);

                        // Skipping the noise-folder filter for ANY location: filter (the previous
                        // condition) was too broad: location: doesn't have to name a noise folder
                        // directly - "location:desktop" is a normal top-level folder that can
                        // perfectly well CONTAIN a coding project with its own node_modules/.git/
                        // obj/bin subfolders, and those got let through wholesale. That's exactly
                        // what made a real "location:desktop" content search balloon to tens of
                        // thousands of irrelevant build-artifact candidates - drowning out a rare
                        // search term in noise (and making a common substring look like it has
                        // far more "real" matches than it does). Only actually bypass the noise
                        // filter when the location filter ITSELF names a noise folder - i.e. the
                        // user explicitly pointed at (or into) a node_modules/.git/etc. folder on
                        // purpose - which is the one legitimate reason to want those included.
                        bool locationTargetsIgnoredFolder = locationFilters != null &&
                            locationFilters.Any(lf => ContentSearcher.IsIgnoredPath(lf));

                        for (int i = 0; i < total; i++)
                        {
                            if (token.IsCancellationRequested) return new List<FileItem>();
                            FileItem it = snapshot[i];
                            if (it == null) continue;
                            if (excludeArr.Count > 0 && excludeArr.Contains(it.Extension)) continue;
                            if (matcher(it) && ContentSearcher.IsSearchableFile(it))
                            {
                                if (!locationTargetsIgnoredFolder && ContentSearcher.IsIgnoredPath(it.FullName))
                                {
                                    continue; // Skip node_modules, .git, .vs, AppData Temp
                                }

                                if (!string.IsNullOrEmpty(userProfile) && it.FullName.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
                                {
                                    userCandidates.Add(it);
                                }
                                else
                                {
                                    otherCandidates.Add(it);
                                }
                            }
                        }

                        // Order candidates: user files (Desktop, Documents, user profile) first!
                        var candidates = new List<FileItem>(Math.Min(50000, userCandidates.Count + otherCandidates.Count));

                        // Content search opens and Boyer-Moore-scans every candidate file - orders
                        // of magnitude more expensive per item than a plain filename match - so an
                        // UNSCOPED content search (no location:) still gets a much lower cap than
                        // a plain filename match would, to keep worst-case (no match found) latency
                        // bounded when it could otherwise span the whole drive. But when location:
                        // IS set, the user already bounded the search themselves - second-guessing
                        // that with a low cap on top is what caused this exact regression: a file
                        // fell after the cap in scan order and got silently excluded even though it
                        // was squarely inside the requested folder. Trust the explicit scope instead.
                        int maxTotalCandidates = !string.IsNullOrEmpty(contentQuery)
                            ? ((locationFilters != null && locationFilters.Count > 0) ? 50000 : 5000)
                            : (extFilters.Count > 0 || !string.IsNullOrEmpty(primaryQuery) || (locationFilters != null && locationFilters.Count > 0)) ? 50000 : 4000;

                        // Whichever cap applies, prioritize the most recently modified files
                        // within each bucket before truncating - a brand-new/just-edited file
                        // (exactly what someone testing "did content search pick up my new file"
                        // would create) should never be the one that happens to fall after an
                        // arbitrary cap in raw MFT scan order.
                        if (!string.IsNullOrEmpty(contentQuery))
                        {
                            userCandidates.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                            otherCandidates.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                        }

                        candidates.AddRange(userCandidates.Take(maxTotalCandidates));

                        int remainingSlots = maxTotalCandidates - candidates.Count;
                        if (remainingSlots > 0)
                        {
                            candidates.AddRange(otherCandidates.Take(remainingSlots));
                        }

                        int totalCandidates = candidates.Count;
                        int scannedFiles = 0;
                        int lastUiUpdateTicks = Environment.TickCount;
                        int listedSoFar = 0;
                        System.Collections.ObjectModel.ObservableCollection<FileItem> streamingResults = null;

                        // Parallel Boyer-Moore scan across candidate files using all CPU cores with live progress & streaming
                        var matchedItems = new System.Collections.Concurrent.ConcurrentBag<(int Index, FileItem Item)>();
                        // Separate from matchedItems (which stays index-ordered for the final
                        // result set) - this tracks matches in the order they're actually FOUND,
                        // so the live streaming view below can just append new entries to an
                        // ObservableCollection instead of re-sorting and swapping the whole
                        // ItemsSource on every update (which re-renders every row, not just the
                        // new one - that's what read as "refreshing every second").
                        var discoveryOrder = new List<FileItem>();
                        var discoveryLock = new object();
                        var po = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount };

                        // Throttled progress reporter shared by every worker thread. Two things make the
                        // displayed count jump back and forth if done naively: (1) each thread captures its
                        // own "currentScanned" snapshot and queues it via Dispatcher.BeginInvoke, but threads
                        // race so a smaller snapshot can end up queued after a larger one; (2) the 200ms
                        // throttle window itself wasn't synchronized, so multiple threads could pass it at
                        // once and queue several out-of-order updates. Fixing this: only one thread is ever
                        // allowed through the throttle at a time (via CompareExchange), and the queued
                        // callback re-reads the shared counters at the moment it actually runs on the UI
                        // thread instead of using a stale per-thread snapshot - since scannedFiles only ever
                        // increases and callbacks execute in the order they were queued, what's displayed is
                        // guaranteed to never decrease.
                        void ReportProgress()
                        {
                            int now = Environment.TickCount;
                            int last = Volatile.Read(ref lastUiUpdateTicks);
                            if (now - last <= 200) return;
                            if (Interlocked.CompareExchange(ref lastUiUpdateTicks, now, last) != last) return;
                            if (token.IsCancellationRequested) return;

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (token.IsCancellationRequested) return;

                                int scanned = Volatile.Read(ref scannedFiles);
                                int found = matchedItems.Count;
                                if (found > 0)
                                {
                                    SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                                    lblStatus.Text = $"Found {found} (scanning {scanned:N0}/{totalCandidates:N0})...";

                                    // Stream matches into the results list as they're found rather
                                    // than making the user wait for the whole scan to finish just
                                    // to see a match that turned up in the first second. Appends
                                    // only the newly-discovered items to an ObservableCollection
                                    // instead of swapping ItemsSource wholesale each time - a full
                                    // swap re-renders every row on every update (visible as
                                    // constant "refreshing"), while appending to an
                                    // ObservableCollection only adds the new row and leaves
                                    // everything already on screen (including scroll position)
                                    // untouched. Final ordering gets corrected to proper scan
                                    // order once the whole scan completes (see sortedContentMatches
                                    // below) - this view is deliberately just "found order" while
                                    // still in progress.
                                    List<FileItem> newlyFound = null;
                                    lock (discoveryLock)
                                    {
                                        if (discoveryOrder.Count > listedSoFar)
                                        {
                                            int take = Math.Min(discoveryOrder.Count, maxResults) - listedSoFar;
                                            if (take > 0)
                                            {
                                                newlyFound = discoveryOrder.GetRange(listedSoFar, take);
                                                listedSoFar += take;
                                            }
                                        }
                                    }
                                    if (newlyFound != null && newlyFound.Count > 0)
                                    {
                                        if (streamingResults == null)
                                        {
                                            streamingResults = new System.Collections.ObjectModel.ObservableCollection<FileItem>();
                                            listView.ItemsSource = streamingResults;
                                        }
                                        foreach (var item in newlyFound) streamingResults.Add(item);

                                        if (resultsGrid.Visibility != Visibility.Visible)
                                        {
                                            resultsGrid.Visibility = Visibility.Visible;
                                            this.SizeToContent = SizeToContent.Manual;
                                            this.SizeToContent = SizeToContent.Height;
                                        }
                                    }
                                }
                                else
                                {
                                    lblStatus.Text = $"Scanning content: {scanned:N0} of {totalCandidates:N0} files...";
                                }
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }

                        try
                        {
                            Parallel.ForEach(candidates.Select((item, idx) => (Index: idx, Item: item)), po, (cand, loopState) =>
                            {
                                if (token.IsCancellationRequested || matchedItems.Count >= maxResults * 2)
                                {
                                    loopState.Stop();
                                    return;
                                }

                                int currentScanned = Interlocked.Increment(ref scannedFiles);

                                if (ContentSearcher.SearchFile(cand.Item.FullName, contentQuery, token, out string snippet))
                                {
                                    cand.Item.MatchSnippet = snippet;
                                    matchedItems.Add(cand);
                                    lock (discoveryLock) { discoveryOrder.Add(cand.Item); }

                                    // Pre-load icon on thread pool
                                    _ = cand.Item.Icon;

                                    // Update progress in status bar without repeatedly rebuilding listView (which destroys scroll position)
                                    ReportProgress();

                                    if (matchedItems.Count >= maxResults)
                                    {
                                        loopState.Stop();
                                    }
                                }
                                else if (currentScanned % 100 == 0)
                                {
                                    ReportProgress();
                                }
                            });
                        }
                        catch (OperationCanceledException) { return new List<FileItem>(); }

                        // Keep deterministic order matching candidate discovery sequence
                        var sortedContentMatches = matchedItems.OrderBy(x => x.Index).ToList();
                        foreach (var it in sortedContentMatches)
                        {
                            if (prioArr.Count > 0 && prioArr.Contains(it.Item.Extension)) prioResult.Add(it.Item);
                            else result.Add(it.Item);
                        }
                    }
                    else
                    {
                        // Fast path: a plain "starts with <name>" query (the common case of
                        // typing the beginning of a filename, no wildcards/qualifiers beyond
                        // what's already filtered by `matcher`) can be served from the filename
                        // prefix trie instead of scanning the whole snapshot.
                        bool useTriePrefix = string.IsNullOrEmpty(contentQuery) && regex == null &&
                            !isWildcard && !string.IsNullOrEmpty(primaryQuery) &&
                            mode.Equals("StartsWith", StringComparison.OrdinalIgnoreCase);

                        IEnumerable<int> candidateIndices = useTriePrefix
                            ? (IEnumerable<int>)GetOrBuildNameTrie(snapshot).PrefixSearch(primaryQuery)
                            : Enumerable.Range(0, total);

                        // Queries with no plain text term (e.g. "location:desktop ext:exe" - just
                        // qualifiers) can't use the trie shortcut above and have to scan the whole
                        // snapshot. NTFS iterates files in inode order, not by folder, so matches
                        // for a specific folder are scattered thinly across the entire index -
                        // often requiring a scan through a large fraction of *all* files on the
                        // drive before finding enough matches to stop early. Doing that on a
                        // single thread was the actual bottleneck; spread it across every core
                        // the same way the content: search branch above already does.
                        var matchedIndexed = new System.Collections.Concurrent.ConcurrentBag<(int Index, FileItem Item)>();
                        int oversample = maxResults * 3; // gather a bit extra so post-sort keeps scan order deterministic
                        var scanOptions = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount };

                        try
                        {
                            Parallel.ForEach(candidateIndices, scanOptions, (i, loopState) =>
                            {
                                if (token.IsCancellationRequested || matchedIndexed.Count >= oversample)
                                {
                                    loopState.Stop();
                                    return;
                                }

                                FileItem item = snapshot[i];
                                if (item == null) return;
                                if (excludeArr.Count > 0 && excludeArr.Contains(item.Extension)) return;

                                if (matcher(item))
                                {
                                    matchedIndexed.Add((i, item));
                                    if (matchedIndexed.Count >= oversample)
                                        loopState.Stop();
                                }
                            });
                        }
                        catch (OperationCanceledException) { return new List<FileItem>(); }

                        if (token.IsCancellationRequested) return new List<FileItem>();

                        // Preserve the original "encountered in scan order" classification/ordering
                        // (prioritized extensions first, capped independently) now that matches have
                        // been harvested out of order by multiple threads.
                        foreach (var it in matchedIndexed.OrderBy(x => x.Index))
                        {
                            if (prioArr.Count > 0 && prioArr.Contains(it.Item.Extension))
                            {
                                if (prioResult.Count < maxResults) prioResult.Add(it.Item);
                            }
                            else if (result.Count < maxResults)
                            {
                                result.Add(it.Item);
                            }

                            if ((prioArr.Count == 0 || prioResult.Count >= maxResults) && result.Count >= maxResults)
                                break;
                        }
                    }
                    
                    if (token.IsCancellationRequested) return new List<FileItem>();

                    var execMap = AppSettings.Instance.ExecutionCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    var allMatched = new List<FileItem>(prioResult.Count + result.Count);
                    allMatched.AddRange(prioResult);
                    allMatched.AddRange(result);

                    foreach (var item in allMatched)
                    {
                        if (execMap.TryGetValue(item.FullName, out int execCount))
                        {
                            item.ExecutionCount = execCount;
                        }
                        else
                        {
                            item.ExecutionCount = 0;
                        }
                    }

                    // Sort items:
                    // For content search, keep stable candidate sequence so results don't re-shuffle after search completes.
                    // For standard file name search:
                    // 1. Frequently executed / clicked items first (ExecutionCount descending)
                    // 2. Prioritized extensions next
                    // 3. Keep stable scan order
                    List<FileItem> sorted;
                    if (!string.IsNullOrEmpty(contentQuery))
                    {
                        sorted = allMatched.Take(maxResults).ToList();
                    }
                    else
                    {
                        sorted = allMatched
                            .OrderByDescending(x => x.ExecutionCount)
                            .ThenByDescending(x => prioArr.Count > 0 && prioArr.Contains(x.Extension))
                            .Take(maxResults)
                            .ToList();
                    }

                    // Surface matching Windows tools (Device Manager, Control Panel, ...) above
                    // regular file results, the same way a launcher would - but only for a plain
                    // name search; a tool isn't a meaningful match for size:/date:/ext:/location:/
                    // drive: filters, content search, or a folder/file-only restriction.
                    if (string.IsNullOrEmpty(contentQuery) && !string.IsNullOrEmpty(primaryQuery) &&
                        folderFilter == null && extFilters.Count == 0 && driveFilter == null &&
                        !minSize.HasValue && !maxSize.HasValue && !minDate.HasValue && !maxDate.HasValue &&
                        (locationFilters == null || locationFilters.Count == 0))
                    {
                        var toolMatches = SystemTools.All
                            .Where(t => t.Name.IndexOf(primaryQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                            .OrderByDescending(t => t.Name.StartsWith(primaryQuery, StringComparison.OrdinalIgnoreCase))
                            .ThenBy(t => t.Name.Length)
                            .Take(3)
                            .Select(t =>
                            {
                                var fi = new FileInfo(t.Target);
                                return new FileItem
                                {
                                    FullName = t.Target,
                                    FileName = t.Name,
                                    IsDirectory = false,
                                    Size = fi.Exists ? fi.Length : 0,
                                    LastWriteTime = fi.Exists ? fi.LastWriteTime : DateTime.Now,
                                    IsSystemTool = true
                                };
                            })
                            .ToList();

                        var bookmarkMatches = BrowserBookmarks.All
                            .Where(b => b.Name.IndexOf(primaryQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                            .OrderByDescending(b => b.Name.StartsWith(primaryQuery, StringComparison.OrdinalIgnoreCase))
                            .ThenBy(b => b.Name.Length)
                            .Take(3)
                            .Select(b => new FileItem
                            {
                                FullName = b.Url,
                                FileName = b.Name,
                                IsDirectory = false,
                                LastWriteTime = DateTime.Now,
                                IsBookmark = true,
                                BookmarkIconSource = b.BrowserExePath
                            })
                            .ToList();

                        if (toolMatches.Count > 0 || bookmarkMatches.Count > 0)
                            sorted = toolMatches.Concat(bookmarkMatches).Concat(sorted).Take(maxResults).ToList();
                    }

                    // Pre-resolve icons on background thread so UI thread never stutters or hangs during hover/scroll!
                    for (int r = 0; r < sorted.Count; r++)
                    {
                        if (token.IsCancellationRequested) return new List<FileItem>();
                        _ = sorted[r].Icon; // Trigger cache on background thread
                    }
                    
                    return sorted;
                });

                if (!token.IsCancellationRequested)
                {
                    var sv = GetScrollViewer(listView);
                    double prevOffset = sv?.VerticalOffset ?? 0;

                    listView.ItemsSource = matched;

                    if (prevOffset > 0 && sv != null && matched != null && matched.Count > 0)
                    {
                        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                        {
                            sv.ScrollToVerticalOffset(prevOffset);
                        }));
                    }

                    HideStatusSpinner();
                    if (matched == null || matched.Count == 0) {
                        SetStatusColor(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF)); // Gray
                        lblStatus.Text = $"No results found for \"{txtSearch.Text.Trim()}\"";
                        resultsGrid.Visibility = Visibility.Collapsed;
                    } else {
                        SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                        lblStatus.Text = $"Found {matched.Count:N0} match{(matched.Count == 1 ? "" : "es")}";
                        if (resultsGrid.Visibility != Visibility.Visible) {
                            resultsGrid.Visibility = Visibility.Visible;
                            this.SizeToContent = SizeToContent.Manual;
                            this.SizeToContent = SizeToContent.Height;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (token == _searchCancellationTokenSource?.Token)
                    _searchInProgress = false;
            }
        }

        private CancellationTokenSource _previewCts;
        private void listView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var pToken = _previewCts.Token;

            var item = listView.SelectedItem as FileItem;
            if (item == null)
            {
                previewPanel.Visibility = Visibility.Hidden;
                return;
            }
            
            previewPanel.Visibility = Visibility.Visible;
            lblPreviewName.Text = item.FileName;
            lblPreviewType.Text = item.IsSystemTool ? "System Tool" : item.IsDirectory ? "Folder" : (string.IsNullOrEmpty(item.Extension) ? "File" : item.Extension + " File");

            lblInfoLocation.Text = Path.GetDirectoryName(item.FullName) ?? item.FullName;
            lblInfoSize.Text = item.IsDirectory ? "—" : item.FormattedSize;
            lblInfoTypeBadge.Text = item.IsDirectory ? "Folder" : (string.IsNullOrEmpty(item.Extension) ? "—" : item.Extension);
            lblInfoModified.Text = item.FormattedDate;

            try
            {
                var fi = new FileInfo(item.FullName);
                if (fi.Exists)
                {
                    rowCreated.Visibility = Visibility.Visible;
                    lblInfoCreated.Text = fi.CreationTime.ToString("yyyy-MM-dd HH:mm");
                }
                else
                {
                    rowCreated.Visibility = Visibility.Collapsed;
                }
            }
            catch { rowCreated.Visibility = Visibility.Collapsed; }

            if (!string.IsNullOrEmpty(item.MatchSnippet))
            {
                rowContentMatch.Visibility = Visibility.Visible;
                lblInfoMatch.Text = "\"" + item.MatchSnippet + "\"";
            }
            else
            {
                rowContentMatch.Visibility = Visibility.Collapsed;
            }

            // Icon (cancellable background extraction using high-res 64x64/Jumbo icon)
            Task.Run(() => {
                if (pToken.IsCancellationRequested) return;
                try {
                    var imgSource = IconHelper.GetJumboIconForPath(item.FullName);
                    if (imgSource != null && !pToken.IsCancellationRequested) {
                        Dispatcher.Invoke(() => {
                            if (!pToken.IsCancellationRequested && listView.SelectedItem == item) {
                                picPreviewIcon.Source = imgSource;
                            }
                        });
                    }
                } catch { }
            }, pToken);
        }

        /// <summary>Parses "drive:d", "drive:d:", or "drive:D:\" alike into just the drive
        /// letter - accepts the trailing colon/slash a user would naturally type since a bare
        /// letter alone ("d") is the one form that's actually required.</summary>
        private static char? ParseDriveFilter(string val)
        {
            val = (val ?? "").Trim().Trim('\"', '\'');
            if (val.Length == 0) return null;
            char c = char.ToUpperInvariant(val[0]);
            return (c >= 'A' && c <= 'Z') ? (char?)c : null;
        }

        private static void ParseSizeFilter(string val, ref long? minSize, ref long? maxSize)
        {
            if (string.IsNullOrWhiteSpace(val)) return;

            string op = "";
            string numStr = val;

            if (val.StartsWith(">=") || val.StartsWith("<="))
            {
                op = val.Substring(0, 2);
                numStr = val.Substring(2);
            }
            else if (val.StartsWith(">") || val.StartsWith("<"))
            {
                op = val.Substring(0, 1);
                numStr = val.Substring(1);
            }

            long multiplier = 1;
            numStr = numStr.Trim();

            if (numStr.EndsWith("gb", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1024L * 1024L * 1024L;
                numStr = numStr.Substring(0, numStr.Length - 2).Trim();
            }
            else if (numStr.EndsWith("mb", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1024L * 1024L;
                numStr = numStr.Substring(0, numStr.Length - 2).Trim();
            }
            else if (numStr.EndsWith("kb", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1024L;
                numStr = numStr.Substring(0, numStr.Length - 2).Trim();
            }
            else if (numStr.EndsWith("b", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1L;
                numStr = numStr.Substring(0, numStr.Length - 1).Trim();
            }

            if (double.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedVal))
            {
                long bytes = (long)(parsedVal * multiplier);
                if (op == ">" || op == ">=")
                {
                    minSize = bytes;
                }
                else if (op == "<" || op == "<=")
                {
                    maxSize = bytes;
                }
                else
                {
                    // Exact or approximate (within 5%)
                    minSize = (long)(bytes * 0.95);
                    maxSize = (long)(bytes * 1.05);
                }
            }
        }

        private static void ParseDateFilter(string val, ref DateTime? minDate, ref DateTime? maxDate)
        {
            if (string.IsNullOrWhiteSpace(val)) return;

            string op = "";
            string dateStr = val;

            if (val.StartsWith(">=") || val.StartsWith("<="))
            {
                op = val.Substring(0, 2);
                dateStr = val.Substring(2);
            }
            else if (val.StartsWith(">") || val.StartsWith("<"))
            {
                op = val.Substring(0, 1);
                dateStr = val.Substring(1);
            }

            dateStr = dateStr.Trim();
            DateTime target;

            if (string.Equals(dateStr, "today", StringComparison.OrdinalIgnoreCase))
            {
                target = DateTime.Today;
            }
            else if (string.Equals(dateStr, "yesterday", StringComparison.OrdinalIgnoreCase))
            {
                target = DateTime.Today.AddDays(-1);
            }
            else if (dateStr.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(dateStr.Substring(0, dateStr.Length - 1), out double days))
            {
                target = DateTime.Now.AddDays(-days);
            }
            else if (!DateTime.TryParse(dateStr, out target))
            {
                return;
            }

            if (op == ">" || op == ">=")
            {
                minDate = target;
            }
            else if (op == "<" || op == "<=")
            {
                maxDate = target;
            }
            else
            {
                // Exact day match
                minDate = target.Date;
                maxDate = target.Date.AddDays(1).AddTicks(-1);
            }
        }

        /// <summary>
        /// Shows the user's pinned favorite files/folders as the result list when the search
        /// box is empty (both at startup and after clearing a query), so they're one glance
        /// away instead of requiring a search. No-op (leaves whatever was already shown alone
        /// via a plain clear) if there are no favorites.
        /// </summary>
        // ---- Favorite groups ------------------------------------------------------------

        private List<FavoriteGroup> FavoriteGroups => AppSettings.Instance.FavoriteGroups;

        private bool IsInGroup(FavoriteGroup group, string path)
        {
            return group?.ItemPaths != null && group.ItemPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsInAnyGroup(string path)
        {
            return FavoriteGroups != null && FavoriteGroups.Any(g => IsInGroup(g, path));
        }

        /// <summary>Adds the path to the group (if not already present) or removes it (toggle).</summary>
        private void ToggleItemInGroup(FavoriteGroup group, string path)
        {
            if (group == null || string.IsNullOrEmpty(path)) return;
            if (group.ItemPaths == null) group.ItemPaths = new List<string>();

            int idx = group.ItemPaths.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) group.ItemPaths.RemoveAt(idx);
            else group.ItemPaths.Add(path);

            AppSettings.Save();
            if (_currentView == ViewMode.Favorites) RefreshFavoritesGrid();
        }

        private FavoriteGroup CreateFavoriteGroup(string name)
        {
            var group = new FavoriteGroup { Name = string.IsNullOrWhiteSpace(name) ? "New Group" : name.Trim() };
            FavoriteGroups.Add(group);
            AppSettings.Save();
            return group;
        }

        private void DeleteFavoriteGroup(FavoriteGroup group)
        {
            if (group == null || FavoriteGroups.Count <= 1) return; // always keep at least one tab
            FavoriteGroups.Remove(group);
            if (_activeFavoriteGroupId == group.Id)
                _activeFavoriteGroupId = FavoriteGroups.FirstOrDefault()?.Id;
            AppSettings.Save();
            RefreshFavoritesView();
        }

        private FavoriteGroup ActiveFavoriteGroup => FavoriteGroups.FirstOrDefault(g => g.Id == _activeFavoriteGroupId);

        // ---- View switching ---------------------------------------------------------------

        private void FavoritesToggle_Click(object sender, RoutedEventArgs e)
        {
            SwitchToView(_currentView == ViewMode.Favorites ? ViewMode.Search : ViewMode.Favorites);
        }

        private void SwitchToView(ViewMode mode)
        {
            if (_currentView == mode) return;
            _currentView = mode;
            ExitJiggleMode();

            // The search box now stays live in both views - it used to be swapped out for a
            // static "★ Favorites" header, but the actual want was to search WITHIN favorites,
            // not lose the search box entirely. Typing swaps just the bubble grid for a results
            // list (favoritesSearchResultsList) while the group tab row stays visible/usable;
            // clearing it goes back to the grid. See FilterFavorites and TriggerSearch's
            // Favorites branch.
            if (mode == ViewMode.Favorites)
            {
                _searchCancellationTokenSource?.Cancel();
                CloseSuggestionsPopup();

                if (previewPanel != null) previewPanel.Visibility = Visibility.Hidden;
                // resultsGrid (the main Search view's results panel) was never being hidden here -
                // only the other direction (leaving Favorites) collapsed it. If a global search had
                // just been run in Search view, resultsGrid was already Visible and stayed that
                // way, rendering right behind/around favoritesView's tab bar since both sit in the
                // same Grid.Row - exactly the "old search results still showing behind Favorites"
                // bug.
                resultsGrid.Visibility = Visibility.Collapsed;
                favoritesView.Visibility = Visibility.Visible;
                // FilterFavorites already calls RefreshFavoritesView() itself at the end - an
                // extra call here first (with whatever _favoritesSearchActive/
                // _favoritesShowingResultsTab happened to be left at from the LAST time Favorites
                // was open) rebuilt the tab bar and grid once in a stale state, then FilterFavorites
                // rebuilt it again correctly - two back-to-back rebuilds with different states is
                // exactly the kind of window a rendering glitch (grid tiles briefly visible under
                // the results list) can slip through. One rebuild, already state-correct, instead.
                FilterFavorites(txtSearch.Text.Trim());
            }
            else
            {
                favoritesView.Visibility = Visibility.Collapsed;
                resultsGrid.Visibility = Visibility.Collapsed;

                if (!string.IsNullOrWhiteSpace(txtSearch.Text))
                    TriggerSearch();
                _suppressNextFocusSuggestions = true;
                txtSearch.Focus();
            }

            SetFavoritesToggleActive(mode == ViewMode.Favorites);
            this.SizeToContent = SizeToContent.Manual;
            this.SizeToContent = SizeToContent.Height;
        }

        private const string StarIconData = "M12,2 L15.09,8.26 L22,9.27 L17,14.14 L18.18,21.02 L12,17.77 L5.82,21.02 L7,14.14 L2,9.27 L8.91,8.26 Z";
        private const string SearchIconData = "M15.5,14h-0.79l-0.28,-0.27C15.41,12.59 16,11.11 16,9.5 16,5.91 13.09,3 9.5,3S3,5.91 3,9.5 5.91,16 9.5,16c1.61,0 3.09,-0.59 4.23,-1.57l0.27,0.28v0.79l5,4.99L20.49,19l-4.99,-5zM9.5,14C7.01,14 5,11.99 5,9.5S7.01,5 9.5,5 14,7.01 14,9.5 11.99,14 9.5,14z";

        /// <summary>
        /// The toggle button always means "switch to the other view", so its icon and tooltip
        /// swap to reflect where it will take you next: a star while in Search (go to
        /// Favorites), a magnifying glass while in Favorites (go back to Search).
        /// </summary>
        private void SetFavoritesToggleActive(bool active)
        {
            // The glyph always shows the DESTINATION (a search icon while you're in Favorites,
            // since clicking takes you back to Search) - so coloring that glyph blue read as "the
            // search view/feature is active", which is backwards from what's actually true.
            // Indicate "you're in Favorites right now" with a highlighted pill background behind
            // a neutral-colored icon instead, matching how the other toolbar buttons already
            // signal state (background highlight, not tinted glyph).
            if (btnFavoritesToggle.Template?.FindName("starPath", btnFavoritesToggle) is System.Windows.Shapes.Path starPath)
            {
                starPath.Data = System.Windows.Media.Geometry.Parse(active ? SearchIconData : StarIconData);
                starPath.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));
            }
            // Set on the Button itself (which the template's Border picks up via
            // TemplateBinding), not on the "bdr" element directly - a local value placed straight
            // on "bdr" would outrank the ControlTemplate.Triggers IsMouseOver setter that also
            // targets "bdr", permanently breaking the hover highlight.
            btnFavoritesToggle.Background = active
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x3B, 0x82, 0xF6))
                : System.Windows.Media.Brushes.Transparent;
            btnFavoritesToggle.ToolTip = active ? "Back to Search (Ctrl+1)" : "Favorites (Ctrl+2)";
        }

        // ---- Tab bar ------------------------------------------------------------------------

        private void RefreshFavoritesView()
        {
            if (string.IsNullOrEmpty(_activeFavoriteGroupId) || FavoriteGroups.All(g => g.Id != _activeFavoriteGroupId))
                _activeFavoriteGroupId = FavoriteGroups.FirstOrDefault()?.Id;

            favoritesTabBar.Children.Clear();
            if (_favoritesSearchActive)
                favoritesTabBar.Children.Add(CreateSearchResultsTabChip());
            foreach (var group in FavoriteGroups)
                favoritesTabBar.Children.Add(CreateTabChip(group));
            favoritesTabBar.Children.Add(CreateAddTabChip());

            UpdateFavoritesContentDisplay();
        }

        private bool _favoritesSearchActive = false;
        /// <summary>True when the pinned "Results" tab (rather than a real group tab) is the
        /// one currently selected/shown - see FilterFavorites and UpdateFavoritesContentDisplay.</summary>
        private bool _favoritesShowingResultsTab = false;

        /// <summary>
        /// A real, clickable tab pinned at the front of the row while a favorites search is
        /// active (removed once the search box is cleared) - selecting it shows the cross-group
        /// match list; clicking any other tab shows that group's own grid instead, without
        /// touching the search box or losing the match list (see FilterFavorites/
        /// UpdateFavoritesContentDisplay). Styled active/inactive the same way CreateTabChip
        /// marks its own selected group, since exactly one of these tabs is ever "current".
        /// </summary>
        private Border CreateSearchResultsTabChip()
        {
            bool isActive = _favoritesShowingResultsTab;
            var label = new TextBlock
            {
                Text = "Results",
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            var chip = new Border
            {
                Background = new SolidColorBrush(isActive
                    ? System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)
                    : System.Windows.Media.Color.FromArgb(0x55, 0x3B, 0x82, 0xF6)),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = label
            };

            chip.MouseLeftButtonUp += (s, e) =>
            {
                if (_favoritesShowingResultsTab) return;
                _favoritesShowingResultsTab = true;
                RefreshFavoritesView();
            };

            return chip;
        }

        /// <summary>
        /// Shows/hides the left/right chevron buttons based on whether the tab strip actually
        /// has anything to scroll to in that direction. ScrollChanged already fires when the tab
        /// bar's content size changes (adding/removing a group), not just on manual scrolling, so
        /// this one handler covers both "a group was added/removed" and "the user scrolled".
        /// </summary>
        private void FavoritesTabScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            btnTabScrollLeft.Visibility = favoritesTabScroller.HorizontalOffset > 0.5
                ? Visibility.Visible : Visibility.Collapsed;
            btnTabScrollRight.Visibility = favoritesTabScroller.HorizontalOffset < favoritesTabScroller.ScrollableWidth - 0.5
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnTabScrollLeft_Click(object sender, RoutedEventArgs e)
        {
            favoritesTabScroller.ScrollToHorizontalOffset(Math.Max(0, favoritesTabScroller.HorizontalOffset - 160));
        }

        private void BtnTabScrollRight_Click(object sender, RoutedEventArgs e)
        {
            favoritesTabScroller.ScrollToHorizontalOffset(
                Math.Min(favoritesTabScroller.ScrollableWidth, favoritesTabScroller.HorizontalOffset + 160));
        }

        private Border CreateTabChip(FavoriteGroup group)
        {
            // No real group chip should look "active" while the pinned Results chip is showing -
            // only one tab should ever read as selected at a time.
            bool isActive = group.Id == _activeFavoriteGroupId && !_favoritesShowingResultsTab;

            var label = new TextBlock
            {
                Text = group.Name,
                Foreground = isActive ? System.Windows.Media.Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA1, 0xA1, 0xAA)),
                FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };

            var chip = new Border
            {
                Background = isActive
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x21, 0x21, 0x24)),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = label,
                AllowDrop = true
            };

            chip.MouseLeftButtonUp += (s, e) =>
            {
                // Selects this group's own grid - leaves the search box and the cached match list
                // untouched, so clicking back onto the pinned Results tab later returns to the
                // same results without re-searching.
                if (_activeFavoriteGroupId == group.Id && !_favoritesShowingResultsTab) return;
                _activeFavoriteGroupId = group.Id;
                _favoritesShowingResultsTab = false;
                RefreshFavoritesView();
            };

            // Dropping a dragged tile onto a different group's tab moves it there.
            chip.Drop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(string))) return;
                string path = (string)e.Data.GetData(typeof(string));
                MoveItemBetweenGroups(_draggedFromGroup, group, path);
            };

            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "Rename" };
            rename.Click += (s, e) => BeginInlineRenameChip(chip, group);
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += (s, e) =>
            {
                if (FavoriteGroups.Count <= 1)
                {
                    ThemedMessageBox.Show(this, "At least one favorites group must remain.", "Can't Delete", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                int count = group.ItemPaths?.Count ?? 0;
                if (ThemedMessageBox.Show(this,
                        string.Format("Delete group \"{0}\" and remove its {1} item(s) from favorites?", group.Name, count),
                        "Delete Group", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    DeleteFavoriteGroup(group);
                }
            };
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            chip.ContextMenu = menu;

            return chip;
        }

        private Border CreateAddTabChip()
        {
            var plus = new TextBlock
            {
                Text = "+",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA1, 0xA1, 0xAA)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var chip = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x21, 0x21, 0x24)),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand,
                Child = plus
            };
            chip.MouseLeftButtonUp += (s, e) =>
            {
                var group = CreateFavoriteGroup("New Group");
                _activeFavoriteGroupId = group.Id;
                RefreshFavoritesView();

                // The newly created tab is the second-to-last chip (last is always "+").
                if (favoritesTabBar.Children.Count >= 2 &&
                    favoritesTabBar.Children[favoritesTabBar.Children.Count - 2] is Border newChip)
                {
                    BeginInlineRenameChip(newChip, group);
                }
            };
            return chip;
        }

        private void BeginInlineRenameChip(Border chip, FavoriteGroup group)
        {
            var box = new TextBox
            {
                Text = group.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.White,
                CaretBrush = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA)),
                MinWidth = 60
            };
            chip.Child = box;
            box.Focus();
            box.SelectAll();

            bool committed = false;
            Action commit = () =>
            {
                if (committed) return;
                committed = true;
                if (!string.IsNullOrWhiteSpace(box.Text))
                {
                    group.Name = box.Text.Trim();
                    AppSettings.Save();
                }
                RefreshFavoritesView();
            };

            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { committed = true; RefreshFavoritesView(); e.Handled = true; }
            };
            box.LostFocus += (s, e) => commit();
        }

        // ---- Bubble grid --------------------------------------------------------------------

        private void RefreshFavoritesGrid()
        {
            ExitJiggleMode();
            _jiggleTargets.Clear();
            favoritesGrid.Children.Clear();

            // The Results tab shows the same bubble-tile grid as any real group, just sourced
            // from the cross-group search matches instead of one group's ItemPaths - group is
            // passed as null since a match doesn't belong to any single one of the groups it
            // might have come from (see CreateFavoriteTile's null-group handling).
            if (_favoritesSearchActive && _favoritesShowingResultsTab)
            {
                if (_favoritesSearchMatches == null || _favoritesSearchMatches.Count == 0)
                {
                    lblFavoritesEmpty.Visibility = Visibility.Visible;
                    return;
                }
                lblFavoritesEmpty.Visibility = Visibility.Collapsed;
                for (int i = 0; i < _favoritesSearchMatches.Count; i++)
                    favoritesGrid.Children.Add(CreateFavoriteTile(null, _favoritesSearchMatches[i].FullName, i));
                return;
            }

            var group = ActiveFavoriteGroup;
            if (group == null || group.ItemPaths == null || group.ItemPaths.Count == 0)
            {
                lblFavoritesEmpty.Visibility = Visibility.Visible;
                return;
            }
            lblFavoritesEmpty.Visibility = Visibility.Collapsed;

            var paths = group.ItemPaths.ToList();
            for (int i = 0; i < paths.Count; i++)
                favoritesGrid.Children.Add(CreateFavoriteTile(group, paths[i], i));
        }

        private Border CreateFavoriteTile(FavoriteGroup group, string path, int index)
        {
            bool isDirectory = Directory.Exists(path);
            string name = Path.GetFileName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = path;

            // Icon renders natively at 68/60 of this resting visual size (see iconImage/iconFrame
            // below) - this is the scale that shrinks it back down to the original 60px look at
            // rest; hover then animates UP toward 1.0 (full native resolution) instead of past it.
            const double TileRestScale = 60.0 / 68.0;

            var rotate = new RotateTransform(0);
            var scale = new ScaleTransform(0.7 * TileRestScale, 0.7 * TileRestScale);
            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(scale);
            transformGroup.Children.Add(rotate);

            // Rendered at 1.14x the resting visual size (matching the hover scale below) rather
            // than at exactly the resting size - RenderTransform-based scaling stretches whatever
            // was already rasterized at THIS declared size, so animating a scale UP PAST 1.0 (the
            // old 1.0->1.14 hover) meant upsampling an already-small 40px raster, which looks soft
            // regardless of how high-res the source icon is. Rendering natively larger and having
            // the "rest" state scale DOWN to fit, with hover scaling back UP to 1.0 (this native,
            // never-upsampled size), means hover is always showing the icon at its sharpest.
            var iconImage = new System.Windows.Controls.Image
            {
                Width = 46,
                Height = 46,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);

            var iconFrame = new Border
            {
                Width = 68,
                Height = 68,
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x30)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = iconImage,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = name,
                FontSize = 11.5,
                Foreground = System.Windows.Media.Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Width = 76,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Wraps just the icon (68px) rather than the wider 84px label-driven stack below, so
            // the hover-remove button (added to this wrapper, not the stack) aligns to the icon's
            // actual corner - it was previously anchored to the stack's right edge instead, which
            // sat visibly further right than the icon itself ("x was too far away").
            var iconWrapper = new Grid { Width = 68, Height = 68, HorizontalAlignment = HorizontalAlignment.Center };
            iconWrapper.Children.Add(iconFrame);

            var stack = new StackPanel { Width = 84, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(iconWrapper);
            stack.Children.Add(label);

            var deleteBadge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = "✕",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            // Quick-remove button, top-right, shown on hover - a faster path than the existing
            // jiggle-mode flow (press-and-hold, wait for the wobble, then tap the top-left badge)
            // for the common case of "just get this one tile off my favorites right now".
            var hoverCloseButton = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0),
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand,
                ToolTip = "Remove from group",
                Child = new TextBlock
                {
                    Text = "✕",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            hoverCloseButton.MouseEnter += (s, e) =>
                hoverCloseButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            hoverCloseButton.MouseLeave += (s, e) =>
                hoverCloseButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46));
            // MouseLeftButtonDown must also be intercepted here, not just Up: tile's own
            // MouseLeftButtonDown handler (below) calls tile.CaptureMouse() whenever it sees the
            // press at all, and mouse capture overrides normal hit-testing for every subsequent
            // event - so the button-up event was always being routed to whatever captured the
            // mouse (tile) rather than to hoverCloseButton, no matter how precisely it was
            // clicked. That's what made it open the file instead of removing it: tile's own
            // MouseLeftButtonUp saw "not moved" and executed the item. Marking the down event
            // Handled here stops it from ever reaching tile's handler, so tile never captures the
            // mouse for a press that started on this button.
            hoverCloseButton.MouseLeftButtonDown += (s, e) => e.Handled = true;
            // No single group to remove FROM when this tile represents a Results-tab search
            // match (matches are pulled across every group) - the quick-remove "x" only makes
            // sense on a real group tab, so it just doesn't show for those tiles (see MouseEnter
            // below).
            if (group != null)
            {
                hoverCloseButton.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (ThemedMessageBox.Show(this, $"Remove \"{name}\" from \"{group.Name}\"?", "Remove Favorite",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                    group.ItemPaths.Remove(path);
                    AppSettings.Save();
                    RefreshFavoritesGrid();
                };
            }

            var root = new Grid
            {
                RenderTransform = transformGroup,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                Opacity = 0
            };
            iconWrapper.Children.Add(hoverCloseButton);
            root.Children.Add(stack);
            root.Children.Add(deleteBadge);

            // The inter-tile spacing lives here (on tile itself, outside its own hit-test bounds)
            // rather than as an inner margin on `stack` above - a transparent Border's Background
            // is hit-testable across its FULL bounds including any padding/margin inside it, so
            // spacing added inside the tile was still "inside" one tile or the other and reacted
            // to hover, even though it visually read as empty space between two separate icons.
            var tile = new Border { Background = System.Windows.Media.Brushes.Transparent, CornerRadius = new CornerRadius(10), Child = root, AllowDrop = true, Margin = new Thickness(8), ToolTip = BuildFavoriteTileTooltip(path, isDirectory) };

            // Pop-in entrance: scale up from tiny with a slight overshoot bounce + fade in,
            // staggered per tile so the grid cascades in rather than all tiles popping at once.
            var stagger = TimeSpan.FromMilliseconds(Math.Min(index, 12) * 35);
            var bounce = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
            // Starts at 0.7 rather than the original 0.4 - RenderTransform-based scaling (unlike
            // the icon Image's own BitmapScalingMode=HighQuality) goes through WPF's software
            // rasterizer for this AllowsTransparency window, which has no mipmap-quality
            // downsampling, so shrinking a bitmap that far down looked visibly soft/blurry during
            // the animation. A shallower scale range keeps the pop-in bounce while making that
            // softness much less noticeable.
            var scaleAnim = new DoubleAnimation(0.7 * TileRestScale, TileRestScale, TimeSpan.FromMilliseconds(320)) { BeginTime = stagger, EasingFunction = bounce };
            var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = stagger };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            root.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

            // Jiggle/reorder/delete-badge are group-membership operations (reorder within a
            // group, remove from a group) that don't apply to a Results-tab tile (group == null,
            // since a search match can't be pinned to just one of the groups it might belong to).
            if (group != null)
            {
                _jiggleTargets.Add((rotate, deleteBadge));
                if (_jiggleMode)
                {
                    deleteBadge.Visibility = Visibility.Visible;
                    StartJiggleAnimation(rotate);
                }
            }

            // Async high-res icon load, same pattern as the search-result preview panel.
            Task.Run(() =>
            {
                try
                {
                    var src = isDirectory ? null : IconHelper.GetJumboIconForPath(path);
                    if (src == null)
                        src = IconHelper.GetSmallIconForPath(isDirectory ? path.TrimEnd('\\') + "\\" : path);
                    if (src != null)
                        Dispatcher.BeginInvoke(new Action(() => iconImage.Source = src));
                }
                catch { }
            });

            // Tactile hover: scale up + a soft blue glow behind the icon frame, both animated in
            // and back out. Independent of the entrance/jiggle animations (different transform,
            // different property).
            var hoverGlow = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA), BlurRadius = 24, ShadowDepth = 0, Opacity = 0 };
            iconFrame.Effect = hoverGlow;

            tile.MouseEnter += (s, e) =>
            {
                if (_jiggleMode) return;
                var hoverScale = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, hoverScale);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, hoverScale);
                hoverGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(140)));
                hoverCloseButton.Visibility = Visibility.Visible;
            };
            tile.MouseLeave += (s, e) =>
            {
                hoverCloseButton.Visibility = Visibility.Collapsed;
                if (_jiggleMode) return;
                var restScale = new DoubleAnimation(TileRestScale, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, restScale);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, restScale);
                hoverGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
            };

            System.Windows.Threading.DispatcherTimer holdTimer = null;
            System.Windows.Point downPos = default;
            bool moved = false;

            tile.MouseLeftButtonDown += (s, e) =>
            {
                if (_jiggleMode) return; // dragging is handled below once already jiggling
                if (group == null) return; // Results-tab tile: plain click-to-open only, no jiggle/drag
                downPos = e.GetPosition(tile);
                moved = false;
                holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                holdTimer.Tick += (s2, e2) =>
                {
                    holdTimer.Stop();
                    if (!moved) EnterJiggleMode();
                };
                holdTimer.Start();
                tile.CaptureMouse();
            };

            tile.MouseMove += (s, e) =>
            {
                if (_jiggleMode)
                {
                    // Already jiggling: a press-drag on this tile picks it up for reorder/move.
                    if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        _draggedFromGroup = group;
                        StartTileDragPreview(stack);
                        try { DragDrop.DoDragDrop(tile, path, DragDropEffects.Move); }
                        finally
                        {
                            EndTileDragPreview();
                            // DoDragDrop runs its own internal drag loop and can swallow the
                            // mouse-up that ends it, so this tile's own MouseLeftButtonUp handler
                            // (which normally releases capture) may never fire after a drag. Left
                            // uncleared, that stale capture silently blocks every other tile's
                            // CaptureMouse() call from succeeding - "only the first tile works".
                            if (tile.IsMouseCaptured) tile.ReleaseMouseCapture();
                        }
                    }
                    return;
                }

                if (holdTimer == null || e.LeftButton != MouseButtonState.Pressed || moved) return;
                var pos = e.GetPosition(tile);
                if (Math.Abs(pos.X - downPos.X) > 6 || Math.Abs(pos.Y - downPos.Y) > 6)
                {
                    moved = true;
                    holdTimer.Stop();
                    // Standard desktop drag-and-drop: moving the mouse while held starts a drag
                    // immediately - no need to long-press first. Long-press (below, when the
                    // hold timer fires undisturbed) is reserved for entering jiggle/delete mode.
                    _draggedFromGroup = group;
                    StartTileDragPreview(stack);
                    try { DragDrop.DoDragDrop(tile, path, DragDropEffects.Move); }
                    finally
                    {
                        EndTileDragPreview();
                        if (tile.IsMouseCaptured) tile.ReleaseMouseCapture();
                    }
                }
            };

            tile.MouseLeftButtonUp += (s, e) =>
            {
                holdTimer?.Stop();
                tile.ReleaseMouseCapture();
                if (!_jiggleMode && !moved)
                {
                    var fi = BuildFileItemForPath(path);
                    if (fi != null) ExecuteItem(fi);
                }
                moved = false;
            };

            if (group != null)
            {
                deleteBadge.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    group.ItemPaths.Remove(path);
                    AppSettings.Save();
                    RefreshFavoritesGrid();
                };

                tile.Drop += (s, e) =>
                {
                    e.Handled = true;
                    if (!e.Data.GetDataPresent(typeof(string))) return;
                    string draggedPath = (string)e.Data.GetData(typeof(string));
                    if (string.Equals(draggedPath, path, StringComparison.OrdinalIgnoreCase)) return;

                    if (_draggedFromGroup == group)
                    {
                        int from = group.ItemPaths.IndexOf(draggedPath);
                        int to = group.ItemPaths.IndexOf(path);
                        if (from >= 0 && to >= 0 && from != to)
                        {
                            group.ItemPaths.RemoveAt(from);
                            group.ItemPaths.Insert(to, draggedPath);
                            AppSettings.Save();
                            RefreshFavoritesGrid();
                        }
                    }
                    else
                    {
                        MoveItemBetweenGroups(_draggedFromGroup, group, draggedPath);
                    }
                };
            }

            var tileMenu = new ContextMenu();
            var openItem = new MenuItem { Header = "Open" };
            openItem.Click += (s, e) => { var fi = BuildFileItemForPath(path); if (fi != null) ExecuteItem(fi); };
            var openLocItem = new MenuItem { Header = "Open File Location" };
            openLocItem.Click += (s, e) => FileUtils.OpenFileLocationAndSelect(path);
            var copyPathItem = new MenuItem { Header = "Copy Path" };
            copyPathItem.Click += (s, e) => FileUtils.CopyPathToClipboard(path);
            tileMenu.Items.Add(openItem);
            tileMenu.Items.Add(openLocItem);
            tileMenu.Items.Add(copyPathItem);

            // Group-membership actions only apply to a real group tile - a Results-tab tile
            // (group == null) just gets Open/Open Location/Properties.
            if (group != null)
            {
                var removeItem = new MenuItem { Header = "Remove from Group" };
                removeItem.Click += (s, e) => { group.ItemPaths.Remove(path); AppSettings.Save(); RefreshFavoritesGrid(); };
                var moveToItem = new MenuItem { Header = "Move to Group" };
                foreach (var otherGroup in FavoriteGroups.Where(g => g.Id != group.Id))
                {
                    var moveTarget = new MenuItem { Header = otherGroup.Name };
                    moveTarget.Click += (s, e) => MoveItemBetweenGroups(group, otherGroup, path);
                    moveToItem.Items.Add(moveTarget);
                }
                tileMenu.Items.Add(new Separator());
                tileMenu.Items.Add(removeItem);
                if (moveToItem.Items.Count > 0) tileMenu.Items.Add(moveToItem);
            }

            var propsItem = new MenuItem { Header = "Properties" };
            propsItem.Click += (s, e) => FileUtils.ShowFileProperties(path);
            tileMenu.Items.Add(new Separator());
            tileMenu.Items.Add(propsItem);
            tile.ContextMenu = tileMenu;

            return tile;
        }

        private void StartJiggleAnimation(RotateTransform rotate)
        {
            var rnd = _jiggleRandom;
            double amplitude = 2.0 + rnd.NextDouble();
            double duration = 0.12 + rnd.NextDouble() * 0.06;

            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(-amplitude, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(amplitude, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(duration))));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(-amplitude, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(duration * 2))));
            rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        /// <summary>
        /// Full-path-plus-details tooltip for a favorites tile, since the tile itself only shows
        /// a truncated/ellipsized filename - hovering is otherwise the only way to tell apart two
        /// same-named files from different folders, or to see size/modified date at a glance.
        /// </summary>
        private static string BuildFavoriteTileTooltip(string path, bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    var di = new DirectoryInfo(path);
                    return string.Format("{0}\nFolder\nModified: {1:g}", path, di.LastWriteTime);
                }
                var fi = new FileInfo(path);
                return string.Format("{0}\n{1}\nModified: {2:g}", path, FormatFileSize(fi.Length), fi.LastWriteTime);
            }
            catch
            {
                return path;
            }
        }

        private static string FormatFileSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return string.Format("{0:0.#} {1}", size, units[unit]);
        }

        private readonly Random _jiggleRandom = new Random();

        private void EnterJiggleMode()
        {
            if (_jiggleMode) return;
            _jiggleMode = true;
            btnFavoritesDone.Visibility = Visibility.Visible;

            foreach (var (rotate, badge) in _jiggleTargets)
            {
                badge.Visibility = Visibility.Visible;
                StartJiggleAnimation(rotate);
            }
        }

        private void ExitJiggleMode()
        {
            if (!_jiggleMode) return;
            _jiggleMode = false;
            btnFavoritesDone.Visibility = Visibility.Collapsed;

            foreach (var (rotate, badge) in _jiggleTargets)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                rotate.Angle = 0;
                badge.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnFavoritesDone_Click(object sender, RoutedEventArgs e)
        {
            ExitJiggleMode();
        }

        private void MoveItemBetweenGroups(FavoriteGroup from, FavoriteGroup to, string path)
        {
            if (from == null || to == null || from == to || string.IsNullOrEmpty(path)) return;
            from.ItemPaths.Remove(path);
            if (to.ItemPaths == null) to.ItemPaths = new List<string>();
            if (!to.ItemPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                to.ItemPaths.Add(path);
            AppSettings.Save();
            RefreshFavoritesGrid();
        }

        private FileItem BuildFileItemForPath(string path)
        {
            try
            {
                // A favorited/pinned bookmark's stored "path" is just its URL - handled first
                // since Directory.Exists/File.Exists below would just (harmlessly) return false
                // for one, but with no name to show and nothing left in this method to fall back
                // to, it would otherwise be silently dropped as if it didn't exist at all.
                if (BrowserBookmarks.IsUrl(path))
                    return new FileItem { FullName = path, FileName = path, IsDirectory = false, IsBookmark = true, LastWriteTime = DateTime.Now };

                if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    return new FileItem { FullName = path, FileName = di.Name, IsDirectory = true, LastWriteTime = di.LastWriteTime };
                }
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    return new FileItem { FullName = path, FileName = fi.Name, IsDirectory = false, Size = fi.Length, LastWriteTime = fi.LastWriteTime };
                }
            }
            catch { }
            return null;
        }

        private System.Windows.Point _lastDragPreviewPos = new System.Windows.Point(double.NaN, double.NaN);

        private void FavoritesGrid_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;

            // e.GetPosition gives WPF device-independent units relative to favoritesGrid - the
            // same coordinate space the Popup below is anchored in (PlacementTarget=favoritesGrid,
            // Placement=Relative), so this is a direct, DPI-correct offset with no conversion
            // needed. The previous version mixed System.Windows.Forms.Cursor.Position (raw
            // physical screen pixels) with a WPF Popup's offsets (DIPs) - correct only at 100%
            // display scaling, and increasingly wrong toward screen edges otherwise, which is
            // exactly the "preview miles away from the cursor" symptom.
            if (_dragPreviewAdorner != null)
            {
                var pos = e.GetPosition(favoritesGrid);
                if (Math.Abs(pos.X - _lastDragPreviewPos.X) < 1 && Math.Abs(pos.Y - _lastDragPreviewPos.Y) < 1)
                    return;
                _lastDragPreviewPos = pos;
                _dragPreviewAdorner.UpdatePosition(pos);
            }
        }

        /// <summary>
        /// Shows a semi-transparent "ghost" of the tile being dragged, following the cursor -
        /// DragDrop.DoDragDrop shows nothing but a bare cursor by default. Call once right
        /// before DragDrop.DoDragDrop; pair with EndTileDragPreview() in a finally block.
        /// </summary>
        /// <param name="content">
        /// The tile's inner content (icon + label stack), not the outer tile/root element -
        /// the outer Grid carries the entrance pop-in animation (Opacity 0->1, staggered per
        /// tile index), and snapshotting it directly could capture a tile mid-fade-in as
        /// invisible if grabbed quickly after the grid refreshes. The inner content has no
        /// opacity/transform of its own, so its rendering is always the tile's steady-state look.
        /// </param>
        private void StartTileDragPreview(UIElement content)
        {
            // Jiggle mode keeps every tile spinning via a Forever-repeating animation. Left
            // running during a drag, the render thread has to keep evaluating all of those on
            // top of DoDragDrop's own modal message loop - freeze them at their current angle for
            // the duration of the drag; EndTileDragPreview restarts them.
            foreach (var (rotate, _) in _jiggleTargets)
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);

            var size = content.RenderSize;
            if (size.Width <= 0 || size.Height <= 0) return;

            // Snapshot the tile's current appearance into a static bitmap so the preview doesn't
            // change if the source tile is later removed/rebuilt (e.g. RefreshFavoritesGrid runs
            // mid-drag when dropping on a different tab).
            var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);

            var layer = AdornerLayer.GetAdornerLayer(favoritesGrid);
            if (layer == null) return;

            // A Popup was tried here previously but caused two problems: (1) it moved by setting
            // HorizontalOffset/VerticalOffset every DragOver, and AllowsTransparency popups own a
            // real layered HWND, so each of those writes is a Win32 SetWindowPos done inside
            // DoDragDrop's own modal loop - laggy; (2) worse, a popup covering the drop area sits
            // on top of favoritesGrid's window, so Windows' OLE drag-and-drop (which routes
            // Drop/DragOver to whichever HWND is physically under the cursor, independent of WPF
            // hit-testing/IsHitTestVisible) delivered to the popup instead of the real window,
            // silently breaking Drop entirely. An Adorner has no HWND of its own - it paints
            // inside the window that's already the real drop target, so moving it is just a cheap
            // repaint and it never intercepts OLE drag routing.
            _lastDragPreviewPos = Mouse.GetPosition(favoritesGrid);
            _dragPreviewAdorner = new DragAdorner(favoritesGrid, rtb, size, _lastDragPreviewPos);
            layer.Add(_dragPreviewAdorner);
        }

        private void EndTileDragPreview()
        {
            // Resume the jiggle spin (paused in StartTileDragPreview) if we're still jiggling -
            // a drop can trigger RefreshFavoritesGrid, in which case the newly rebuilt tiles are
            // wired up already-jiggling by CreateFavoriteTile, so only restart here when the
            // grid wasn't rebuilt out from under these targets.
            if (_jiggleMode)
            {
                foreach (var (rotate, _) in _jiggleTargets)
                    StartJiggleAnimation(rotate);
            }

            if (_dragPreviewAdorner != null)
            {
                var layer = AdornerLayer.GetAdornerLayer(favoritesGrid);
                layer?.Remove(_dragPreviewAdorner);
            }
            _dragPreviewAdorner = null;
        }

        private void FavoritesGrid_Drop(object sender, System.Windows.DragEventArgs e)
        {
            // Only handles drops on empty space in the panel - drops on a specific tile or tab
            // chip are already handled (and Handled=true'd) by their own Drop handlers, so
            // bubbling stops before reaching here in that case.
            if (!e.Data.GetDataPresent(typeof(string))) return;
            string path = (string)e.Data.GetData(typeof(string));
            var group = ActiveFavoriteGroup;
            if (group == null) return;

            if (_draggedFromGroup == group)
            {
                int from = group.ItemPaths.IndexOf(path);
                if (from >= 0)
                {
                    group.ItemPaths.RemoveAt(from);
                    group.ItemPaths.Add(path);
                    AppSettings.Save();
                    RefreshFavoritesGrid();
                }
            }
            else if (_draggedFromGroup != null)
            {
                MoveItemBetweenGroups(_draggedFromGroup, group, path);
            }
        }

        private void ExecuteItem(FileItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FullName)) return;

            // Increment execution/click count
            if (AppSettings.Instance.ExecutionCounts == null)
            {
                AppSettings.Instance.ExecutionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            if (AppSettings.Instance.ExecutionCounts.TryGetValue(item.FullName, out int count))
            {
                AppSettings.Instance.ExecutionCounts[item.FullName] = count + 1;
            }
            else
            {
                AppSettings.Instance.ExecutionCounts[item.FullName] = 1;
            }

            item.ExecutionCount = AppSettings.Instance.ExecutionCounts[item.FullName];

            // Search history was previously only saved on pressing Enter in the search box - the
            // far more common path (typing a query, then clicking a result with the mouse instead
            // of pressing Enter) never recorded anything, so the most recent search often didn't
            // show up at the top of history the next time the box was empty. Saving here instead
            // covers every way of actually opening something (Enter, click, double-click, quick
            // start) uniformly. Guarded to the Search view with real query text so opening a
            // Favorites tile (where txtSearch is cleared/hidden) doesn't record garbage entries.
            if (_currentView == ViewMode.Search && !string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                SaveSearchHistory(txtSearch.Text.Trim());
            }

            AppSettings.Save();

            FileUtils.Open(item.FullName);

            if (AppSettings.Instance.HideAfterOpen)
            {
                CloseSuggestionsPopup();
                this.Hide();
            }
        }

        private void listView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (listView.SelectedItem is FileItem item)
            {
                ExecuteItem(item);
            }
        }

        private void listView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && listView.SelectedItem is FileItem item)
            {
                e.Handled = true;
                ExecuteItem(item);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                txtSearch.Focus();
                txtSearch.SelectAll();
            }
        }

        private void btnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) ExecuteItem(item);
        }

        private void btnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) FileUtils.OpenFileLocationAndSelect(item.FullName);
        }

        // WPF doesn't select a ListViewItem on right-click by default (only left-click does),
        // so without this the context menu would act on whatever was last left-clicked instead
        // of the item the user actually right-clicked.
        private void ListViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem lvi) lvi.IsSelected = true;
        }

        private void CtxOpen_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) ExecuteItem(item);
        }

        private void CtxOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) FileUtils.OpenFileLocationAndSelect(item.FullName);
        }

        private void CtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) FileUtils.CopyPathToClipboard(item.FullName);
        }

        private void CtxCopyName_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) FileUtils.CopyFileNameToClipboard(item.FullName);
        }

        private void CtxProperties_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) FileUtils.ShowFileProperties(item.FullName);
        }

        private void FileItemContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!(sender is ContextMenu menu)) return;

            // Not using menu.FindName("ctxAddToGroup") here: that depends on this resource
            // having its own registered NameScope, which - unlike a NameScope on the page's own
            // root - isn't something to take on faith for an x:Name declared inside a
            // ResourceDictionary-hosted ContextMenu. Finding it by its known header instead
            // has no such dependency.
            var addToGroupItem = menu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(m => "★ Add to Group".Equals(m.Header as string));
            if (addToGroupItem == null) return;

            try
            {
                // PlacementTarget is the exact ListViewItem the context menu was opened on, and
                // its DataContext is the bound FileItem - a more reliable source than
                // listView.SelectedItem, whose update timing relative to the context menu
                // opening isn't guaranteed. Fall back to SelectedItem if that gives nothing.
                FileItem item = (menu.PlacementTarget as FrameworkElement)?.DataContext as FileItem;
                if (item == null) item = listView.SelectedItem as FileItem;
                if (item == null)
                {
                    addToGroupItem.Items.Clear();
                    addToGroupItem.Items.Add(new MenuItem { Header = "(select an item first)", IsEnabled = false });
                    return;
                }

                addToGroupItem.Items.Clear();

                foreach (var group in FavoriteGroups)
                {
                    bool isMember = IsInGroup(group, item.FullName);
                    var groupItem = new MenuItem { Header = (isMember ? "✓ " : "") + group.Name };
                    groupItem.Click += (s, args) => ToggleItemInGroup(group, item.FullName);
                    addToGroupItem.Items.Add(groupItem);
                }

                if (FavoriteGroups.Count > 0)
                    addToGroupItem.Items.Add(new Separator());

                // Typing a name here and pressing Enter creates a new group and adds the item to it.
                var newGroupBox = new TextBox
                {
                    Width = 160,
                    Text = "",
                    Background = System.Windows.Media.Brushes.Transparent,
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#3F3F46")
                };
                System.Windows.Controls.ToolTipService.SetToolTip(newGroupBox, "Type a name and press Enter to create a new group");
                newGroupBox.KeyDown += (s, args) =>
                {
                    if (args.Key == Key.Enter && !string.IsNullOrWhiteSpace(newGroupBox.Text))
                    {
                        var group = CreateFavoriteGroup(newGroupBox.Text);
                        ToggleItemInGroup(group, item.FullName);
                        menu.IsOpen = false;
                        args.Handled = true;
                    }
                };
                var newGroupItem = new MenuItem { Header = newGroupBox, StaysOpenOnClick = true };
                addToGroupItem.Items.Add(newGroupItem);

                // Focus the textbox once the submenu's actually open so typing works immediately.
                // Unsubscribe first: this handler is a named method (not a lambda), so -= actually
                // finds and removes the previous subscription instead of silently piling up a new
                // one every time the context menu opens.
                addToGroupItem.SubmenuOpened -= AddToGroupSubmenu_Opened;
                addToGroupItem.SubmenuOpened += AddToGroupSubmenu_Opened;
            }
            catch (Exception ex)
            {
                // Surface the failure in the menu itself instead of silently leaving the
                // "Loading..." placeholder in place with no clue why.
                addToGroupItem.Items.Clear();
                addToGroupItem.Items.Add(new MenuItem { Header = "Error: " + ex.Message, IsEnabled = false });
                Trace.WriteLine("FileItemContextMenu_Opened failed: " + ex);
            }
        }

        private void AddToGroupSubmenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!(sender is MenuItem mi) || mi.Items.Count == 0) return;
            if (mi.Items[mi.Items.Count - 1] is MenuItem lastItem && lastItem.Header is TextBox tb)
                Dispatcher.BeginInvoke(new Action(() => tb.Focus()));
        }

        private static ScrollViewer GetScrollViewer(DependencyObject depObj)
        {
            if (depObj == null) return null;
            if (depObj is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
    }
    
    public class FileItem
    {
        /// <summary>Low 32 bits of the NTFS file reference number (MFT record index), used to
        /// correlate this item with USN journal change records for incremental updates.</summary>
        public uint NodeIndex { get; set; }
        public string FullName { get; set; }
        public string FileName { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public int ExecutionCount { get; set; }

        public bool IsDirectory { get; set; }
        public string MatchSnippet { get; set; }
        /// <summary>True for a built-in Windows tool shortcut (see <see cref="SystemTools"/>)
        /// rather than an item that came from the MFT index.</summary>
        public bool IsSystemTool { get; set; }

        /// <summary>True for a browser bookmark (see <see cref="BrowserBookmarks"/>) - FullName
        /// holds the URL rather than a filesystem path for these.</summary>
        public bool IsBookmark { get; set; }
        /// <summary>Owning browser's exe path, used as the icon source for a bookmark instead of
        /// FullName (which is a URL, not something IconHelper can extract an icon from).</summary>
        public string BookmarkIconSource { get; set; }

        public string DisplayName => (Extension == "LNK" && FileName != null && FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            ? FileName.Substring(0, FileName.Length - 4)
            : FileName;

        private string _lowerName;
        public string LowerName => _lowerName ?? (_lowerName = (FileName ?? "").ToLowerInvariant());

        private string _directoryName;
        // Path.GetDirectoryName expects a filesystem path - FullName holds the raw URL for a
        // bookmark, so just show that directly as the "location" line instead of feeding a URL
        // through path-parsing logic that was never meant to see one.
        public string DirectoryName => _directoryName ?? (_directoryName = IsBookmark ? (FullName ?? "") : (Path.GetDirectoryName(FullName) ?? ""));

        private string _extension;
        public string Extension
        {
            get
            {
                if (_extension == null)
                {
                    string name = FileName ?? "";
                    int dot = name.LastIndexOf('.');
                    _extension = (dot >= 0 && dot < name.Length - 1) ? name.Substring(dot + 1).ToUpperInvariant() : "";
                }
                return _extension;
            }
            set => _extension = value;
        }

        private System.Windows.Media.ImageSource _icon;
        private bool _iconLoaded;
        public System.Windows.Media.ImageSource Icon
        {
            get
            {
                if (!_iconLoaded)
                {
                    _iconLoaded = true;
                    // FullName is a URL for a bookmark, not something IconHelper can extract an
                    // icon from - use the owning browser's exe icon instead (falls back to null,
                    // i.e. no icon, if the browser's path wasn't found).
                    _icon = IconHelper.GetSmallIconForPath(IsBookmark ? BookmarkIconSource : FullName);
                }
                return _icon;
            }
        }

        /// <summary>
        /// Updates FullName/FileName for an item that's being refreshed in place (a USN-journal
        /// rename/move applied to an existing index entry - see ApplyUsnChange/ApplyServiceDelta)
        /// and resets every field derived from them. DirectoryName/Extension/LowerName/Icon are
        /// all lazily cached on first access; setting FullName/FileName directly (the previous
        /// code) left those caches holding the OLD path forever; the item would keep reporting
        /// its original folder/extension/icon indefinitely afterward - e.g. silently drifting out
        /// of a location: filter's match set, or matching a location: filter it should no longer
        /// be under. This is the only correct way to move/rename a FileItem in place.
        /// </summary>
        public void UpdatePath(string fullName, string fileName)
        {
            FullName = fullName;
            FileName = fileName;
            _directoryName = null;
            _extension = null;
            _lowerName = null;
            _icon = null;
            _iconLoaded = false;
        }

        public string FormattedSize
        {
            get
            {
                if (Size >= 1_073_741_824) return $"{(Size / 1_073_741_824.0):F2} GB";
                if (Size >= 1_048_576) return $"{(Size / 1_048_576.0):F2} MB";
                if (Size >= 1_024) return $"{(Size / 1_024.0):F2} KB";
                return $"{(Size)} B";
            }
        }

        public string FormattedDate => LastWriteTime.ToString("yyyy-MM-dd HH:mm");
    }
}