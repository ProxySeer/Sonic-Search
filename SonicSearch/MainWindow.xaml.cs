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

namespace SonicSearch
{
    public partial class MainWindow : Window
    {
        private List<FileItem> _allItems = new List<FileItem>();
        private readonly object _allItemsLock = new object();
        private volatile FileItem[] _cachedSnapshot = new FileItem[0];
        
        private CancellationTokenSource _searchCancellationTokenSource;
        private System.Windows.Threading.DispatcherTimer _debounceTimer;
        
        private string _currentDrive = "C";
        
        // FSW
        private List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private object _fswLock = new object();
        private HashSet<string> _fswCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _fswDeleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Threading.DispatcherTimer _fswTimer;
        private System.Windows.Threading.DispatcherTimer _reindexTimer;
        private bool _isIndexing = false;

        // USN Journal incremental indexing
        private UsnJournal _usnJournal;
        private long _lastUsn;
        private System.Windows.Threading.DispatcherTimer _usnPollTimer;
        private readonly object _usnLock = new object();

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
                _usnJournal?.Dispose();
                _usnJournal = null;
            }
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

            Loaded += MainWindow_Loaded;
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

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                var showItem = new System.Windows.Forms.ToolStripMenuItem("Show / Hide (Ctrl+S)", null, (s, e) => ToggleWindowVisibility());
                var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Settings...", null, (s, e) => {
                    this.Show();
                    this.Activate();
                    SettingsButton_Click(null, null);
                });
                var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit SonicSearch", null, (s, e) => {
                    Application.Current.Shutdown();
                });

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
            if (!_isIndexing && !_searchInProgress && !string.IsNullOrEmpty(_currentDrive))
            {
                LoadFileSystemData(_currentDrive);
            }
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
                SetupFileSystemWatcher(_currentDrive);

                string newIncluded = AppSettings.Instance.IncludedIndexFolders ?? "";
                string newExcluded = AppSettings.Instance.ExcludedIndexFolders ?? "";
                if (!string.Equals(oldIncluded, newIncluded, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(oldExcluded, newExcluded, StringComparison.OrdinalIgnoreCase))
                {
                    // Reload index with new folder filter scope
                    LoadFileSystemData(_currentDrive);
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadFileSystemData("C");
        }

        private async void LoadFileSystemData(string driveLetter)
        {
            if (_isIndexing) return;
            _isIndexing = true;
            _currentDrive = driveLetter;
            SetStatusColor(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)); // Blue
            lblStatus.Text = $"Indexing {driveLetter}:\\ Master File Table...";
            if (string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                listView.ItemsSource = null;
            }

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                var fileItems = await Task.Run(() =>
                {
                    var drive = new DriveInfo(driveLetter);
                    var ntfsReader = new NtfsReader(drive, RetrieveMode.StandardInformations);
                    var nodes = ntfsReader.GetNodes(driveLetter + ":\\");

                    var incFolders = GetIncludedFolders();
                    var excFolders = GetExcludedFolders();

                    return nodes.AsParallel().Select(node =>
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
                });

                lock (_allItemsLock)
                {
                    _allItems = fileItems;
                    _cachedSnapshot = fileItems.ToArray();
                }

                StartUsnJournalPolling(driveLetter);

                stopwatch.Stop();
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                _readyStatusText = $"Ready — Indexed {_allItems.Count:N0} files in {elapsedSec:F2}s";

                SetupFileSystemWatcher(driveLetter);

                if (string.IsNullOrWhiteSpace(txtSearch.Text))
                {
                    SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
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
                lblStatus.Text = "Requires Administrator Rights! Error: " + ex.Message;
                System.Windows.MessageBox.Show("Please run SonicSearch as Administrator to read the Master File Table (MFT).", "Admin Required", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (IOException ex)
            {
                // Covers both access-denied opening the volume handle (thrown as IOException by
                // NtfsReader) and genuine read/format failures - don't kill the app for either;
                // let the user pick another drive or retry.
                SetStatusColor(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // Red
                lblStatus.Text = "Requires Administrator Rights! Error: " + ex.Message;
                System.Windows.MessageBox.Show("Please run SonicSearch as Administrator to read the Master File Table (MFT).", "Admin Required", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                // A single bad/corrupt volume shouldn't take down the whole app.
                SetStatusColor(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // Red
                lblStatus.Text = "Indexing failed: " + ex.Message;
                Trace.WriteLine("LoadFileSystemData failed for drive " + driveLetter + ": " + ex);
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
        /// Starts polling the NTFS USN change journal for the given drive so routine
        /// create/rename/delete/modify activity can be applied to the in-memory index
        /// without a full MFT rescan. Falls back to a full rescan automatically if the
        /// journal is unavailable or gets invalidated (wrapped/recreated) while polling.
        /// </summary>
        private void StartUsnJournalPolling(string driveLetter)
        {
            lock (_usnLock)
            {
                _usnJournal?.Dispose();
                _usnJournal = null;

                try
                {
                    var journal = new UsnJournal(new DriveInfo(driveLetter));
                    _lastUsn = journal.CurrentUsn;
                    _usnJournal = journal;
                }
                catch (Exception ex)
                {
                    // USN journal isn't available (e.g. non-NTFS volume, or access denied for
                    // the journal specifically) - the app still works via full rescans/FSW.
                    Trace.WriteLine("USN journal unavailable for drive " + driveLetter + ": " + ex.Message);
                    return;
                }
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

        private void PollUsnJournal()
        {
            // Defer this tick entirely while a search is running or a scan is already in
            // progress - the timer fires again next interval, so nothing is lost by waiting.
            if (_isIndexing || _searchInProgress) return;

            UsnJournal journal;
            lock (_usnLock)
            {
                journal = _usnJournal;
            }
            if (journal == null) return;

            List<UsnChange> changes;
            try
            {
                changes = journal.GetChanges(_lastUsn);
            }
            catch (UsnJournalInvalidatedException)
            {
                // Journal wrapped or was recreated since our cursor was captured - the only
                // safe recovery is a full re-scan, after which polling resumes from the
                // journal's new current position.
                Trace.WriteLine("USN journal invalidated for drive " + _currentDrive + "; falling back to full rescan.");
                _ = Dispatcher.InvokeAsync(() => LoadFileSystemData(_currentDrive));
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("USN journal poll failed: " + ex);
                return;
            }

            if (changes.Count == 0) return;

            var incFolders = GetIncludedFolders();
            var excFolders = GetExcludedFolders();

            bool changed = false;
            lock (_allItemsLock)
            {
                foreach (var change in changes)
                    changed |= ApplyUsnChange(journal, change, incFolders, excFolders);

                if (changed)
                    _cachedSnapshot = _allItems.ToArray();
            }

            _lastUsn = changes[changes.Count - 1].Usn + 1;

            if (changed)
            {
                if (string.IsNullOrWhiteSpace(txtSearch.Text))
                {
                    _readyStatusText = string.Format("Ready — Indexed {0:N0} files", _allItems.Count);
                    lblStatus.Text = _readyStatusText;
                }
                else if (!_searchInProgress)
                {
                    // Only refresh visible results if nothing is currently searching - never
                    // cancel/restart an in-flight search (especially a slow content: search)
                    // just because routine background file activity touched the index.
                    TriggerSearch();
                }
            }
        }

        /// <summary>
        /// Applies one USN change record to <see cref="_allItems"/> in place. Must be called
        /// with <see cref="_allItemsLock"/> held. Returns true if the in-memory index changed.
        /// </summary>
        private bool ApplyUsnChange(UsnJournal journal, UsnChange change, List<string> incFolders, List<string> excFolders)
        {
            uint nodeIndex = (uint)(change.FileReferenceNumber & 0xFFFFFFFF);

            bool isRemoval =
                (change.Reason & (UsnJournal.UsnReason.FileDelete | UsnJournal.UsnReason.RenameOldName)) != 0;

            if (isRemoval)
            {
                return _allItems.RemoveAll(fi => fi.NodeIndex == nodeIndex) > 0;
            }

            // Only bother resolving the path for reasons that can actually affect what we show.
            const UsnJournal.UsnReason relevantReasons =
                UsnJournal.UsnReason.FileCreate | UsnJournal.UsnReason.RenameNewName |
                UsnJournal.UsnReason.DataExtend | UsnJournal.UsnReason.DataTruncation |
                UsnJournal.UsnReason.DataOverwrite | UsnJournal.UsnReason.BasicInfoChange;

            if ((change.Reason & relevantReasons) == 0)
                return false;

            string fullName = journal.ResolvePath(change.FileReferenceNumber);
            var existingIndex = _allItems.FindIndex(fi => fi.NodeIndex == nodeIndex);

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
                item.FullName = fullName;
                item.FileName = Path.GetFileName(fullName);
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

        private void SetupFileSystemWatcher(string driveLetter)
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
            
            if (changed)
            {
                if (string.IsNullOrWhiteSpace(txtSearch.Text))
                {
                    listView.ItemsSource = null;
                    SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    lblStatus.Text = _readyStatusText;
                    resultsGrid.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Refresh search view immediately so new file appears on screen!
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
                lblStatus.Text = _readyStatusText;
            }
            else
            {
                SetStatusColor(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber
                lblStatus.Text = $"Searching \"{query}\"...";
            }

            UpdateSuggestionsPopup();

            // Use higher debounce (450ms) for content searches so keystrokes don't thrash disk I/O
            bool isContentSearch = query.IndexOf("content:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   query.IndexOf("text:", StringComparison.OrdinalIgnoreCase) >= 0;
            int debounceMs = isContentSearch ? 450 : Math.Max(10, AppSettings.Instance.DebounceMs);
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(debounceMs);

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void txtSearch_GotFocus(object sender, RoutedEventArgs e)
        {
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
                    Tuple.Create("date:", "Filter by modification date (e.g. date:today, date:>7d)", "Filter")
                };

                foreach (var hint in filterHints)
                {
                    if (hint.Item1.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase))
                    {
                        suggestions.Add(new SuggestionItem
                        {
                            Title = hint.Item1,
                            Description = hint.Item2,
                            CompletionText = ReplaceCurrentWord(full, caret, hint.Item1),
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
            if (caret < 0 || caret > full.Length) caret = full.Length;
            string before = full.Substring(0, caret);
            string after = full.Substring(caret);

            int lastSpace = before.LastIndexOf(' ');
            string newBefore = (lastSpace >= 0 ? before.Substring(0, lastSpace + 1) : "") + replacement;
            
            // Add trailing space for convenience if not already there
            if (!after.StartsWith(" "))
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
                element = VisualTreeHelper.GetParent(element);
            }

            if (element is ListBoxItem item && item.DataContext is SuggestionItem suggestion)
            {
                e.Handled = true;
                ApplySuggestion(suggestion);
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
            _ = ApplyFilterAsync(query);
        }

        private string ResolveLocationAlias(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string key = input.Trim().ToLowerInvariant();

            switch (key)
            {
                case "desktop":
                    return ResolveSpecialFolderOrFallback(Environment.SpecialFolder.Desktop, "Desktop");
                case "downloads":
                case "download":
                    string userProf = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string dl = System.IO.Path.Combine(userProf, "Downloads");
                    return System.IO.Directory.Exists(dl) ? dl : "Downloads";
                case "documents":
                case "doc":
                case "docs":
                case "mydocuments":
                    return ResolveSpecialFolderOrFallback(Environment.SpecialFolder.MyDocuments, "Documents");
                case "pictures":
                case "pics":
                case "photos":
                    return ResolveSpecialFolderOrFallback(Environment.SpecialFolder.MyPictures, "Pictures");
                case "music":
                    return ResolveSpecialFolderOrFallback(Environment.SpecialFolder.MyMusic, "Music");
                case "videos":
                case "video":
                    return ResolveSpecialFolderOrFallback(Environment.SpecialFolder.MyVideos, "Videos");
                case "temp":
                case "tmp":
                    return System.IO.Path.GetTempPath().TrimEnd('\\');
                case "user":
                case "profile":
                case "home":
                    return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                case "system":
                case "system32":
                    return Environment.GetFolderPath(Environment.SpecialFolder.System);
                case "programfiles":
                case "programs":
                    return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                default:
                    return input.Trim();
            }
        }

        /// <summary>
        /// Resolves a Windows known folder, but falls back to just the plain folder name
        /// (e.g. "Desktop") - which the location filter then matches as a path substring
        /// anywhere in the tree - if the API-resolved path doesn't actually exist. This
        /// matters because Environment.GetFolderPath can resolve to the wrong profile's
        /// folder when SonicSearch is elevated via a different account, or to a stale path
        /// when the folder has been redirected (OneDrive Desktop/Documents backup, policy
        /// redirection, etc.) - without this fallback, "location:desktop" would silently
        /// match nothing even though the file is sitting right there on the real desktop.
        /// </summary>
        private static string ResolveSpecialFolderOrFallback(Environment.SpecialFolder folder, string fallbackName)
        {
            string resolved = Environment.GetFolderPath(folder);
            return (!string.IsNullOrEmpty(resolved) && System.IO.Directory.Exists(resolved)) ? resolved : fallbackName;
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
            _searchCancellationTokenSource?.Cancel();
            _searchCancellationTokenSource = new CancellationTokenSource();
            var token = _searchCancellationTokenSource.Token;

            if (string.IsNullOrWhiteSpace(pattern))
            {
                listView.ItemsSource = null;
                SetStatusColor(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
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
            string locationFilter = null;
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
                else if (tok.Equals("location:", StringComparison.OrdinalIgnoreCase) || tok.Equals("path:", StringComparison.OrdinalIgnoreCase) || tok.Equals("in:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(nextTok))
                    {
                        locationFilter = ResolveLocationAlias(nextTok.Trim('\"', '\''));
                        t++; // Consume next token
                    }
                }
                else if (tok.StartsWith("location:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("path:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("in:", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = tok.IndexOf(':');
                    string val = tok.Substring(colon + 1).Trim().Trim('\"', '\'');
                    if (!string.IsNullOrEmpty(val)) locationFilter = ResolveLocationAlias(val);
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

                // 0.1 Location / Path filter
                if (!string.IsNullOrEmpty(locationFilter))
                {
                    string dir = item.DirectoryName;
                    string full = item.FullName;
                    if ((dir == null || dir.IndexOf(locationFilter, StringComparison.OrdinalIgnoreCase) < 0) &&
                        (full == null || full.IndexOf(locationFilter, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        return false;
                    }
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
                    int count = 0;

                    if (!string.IsNullOrEmpty(contentQuery))
                    {
                        // Collect candidates matching name/extension/date/size filters first
                        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        var userCandidates = new List<FileItem>(4000);
                        var otherCandidates = new List<FileItem>(10000);

                        for (int i = 0; i < total; i++)
                        {
                            if (token.IsCancellationRequested) return new List<FileItem>();
                            FileItem it = snapshot[i];
                            if (it == null) continue;
                            if (excludeArr.Count > 0 && excludeArr.Contains(it.Extension)) continue;
                            if (matcher(it) && ContentSearcher.IsSearchableFile(it))
                            {
                                // Only ignore AppData/node_modules if user didn't explicitly target them with location:
                                if (string.IsNullOrEmpty(locationFilter) && ContentSearcher.IsIgnoredPath(it.FullName))
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
                        
                        // If no extension filter, name filter, or location filter is specified, cap total candidates to 4,000 to keep unconstrained search under 1-2 seconds
                        int maxTotalCandidates = (extFilters.Count > 0 || !string.IsNullOrEmpty(primaryQuery) || !string.IsNullOrEmpty(locationFilter)) ? 50000 : 4000;
                        candidates.AddRange(userCandidates.Take(maxTotalCandidates));
                        
                        int remainingSlots = maxTotalCandidates - candidates.Count;
                        if (remainingSlots > 0)
                        {
                            candidates.AddRange(otherCandidates.Take(remainingSlots));
                        }

                        int totalCandidates = candidates.Count;
                        int scannedFiles = 0;
                        int lastUiUpdateTicks = Environment.TickCount;

                        // Parallel Boyer-Moore scan across candidate files using all CPU cores with live progress & streaming
                        var matchedItems = new System.Collections.Concurrent.ConcurrentBag<(int Index, FileItem Item)>();
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

                        foreach (int i in candidateIndices)
                        {
                            if (++count % 25000 == 0 && token.IsCancellationRequested)
                                return new List<FileItem>();

                            FileItem item = snapshot[i];
                            if (item == null) continue;

                            if (excludeArr.Count > 0 && excludeArr.Contains(item.Extension)) continue;

                            if (matcher(item))
                            {
                                if (prioArr.Count > 0 && prioArr.Contains(item.Extension)) {
                                    prioResult.Add(item);
                                    if (prioResult.Count >= maxResults) break;
                                } else {
                                    if (result.Count < maxResults) result.Add(item);
                                }

                                if (prioArr.Count == 0 && result.Count >= maxResults) break;
                                if (prioResult.Count >= maxResults && result.Count >= maxResults) break;
                            }
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
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Location:");
            sb.AppendLine(Path.GetDirectoryName(item.FullName));
            sb.AppendLine();
            sb.AppendLine("Size: " + item.FormattedSize);
            sb.AppendLine("Modified: " + item.FormattedDate);
            
            try {
                var fi = new FileInfo(item.FullName);
                if (fi.Exists) {
                    sb.AppendLine("Created: " + fi.CreationTime.ToString("yyyy-MM-dd HH:mm"));
                }
            } catch { }

            if (!string.IsNullOrEmpty(item.MatchSnippet))
            {
                sb.AppendLine();
                sb.AppendLine("Content Match:");
                sb.AppendLine("\"" + item.MatchSnippet + "\"");
            }
            
            lblPreviewDetails.Text = sb.ToString();
            
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
            AppSettings.Save();

            FileUtils.Open(item.FullName);
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

        public string DisplayName => (Extension == "LNK" && FileName != null && FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            ? FileName.Substring(0, FileName.Length - 4)
            : FileName;

        private string _lowerName;
        public string LowerName => _lowerName ?? (_lowerName = (FileName ?? "").ToLowerInvariant());

        private string _directoryName;
        public string DirectoryName => _directoryName ?? (_directoryName = Path.GetDirectoryName(FullName) ?? "");

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
                    _icon = IconHelper.GetSmallIconForPath(FullName);
                }
                return _icon;
            }
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