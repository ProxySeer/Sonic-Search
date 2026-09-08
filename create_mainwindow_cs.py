import os

src_dir = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch'

main_xaml_cs = '''using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using NtfsReader;
using System.Drawing;
using System.Windows.Interop;

namespace SonicSearch
{
    public partial class MainWindow : Window
    {
        private List<FileItem> _allItems = new List<FileItem>();
        private FileItem[] _searchSnapshot = new FileItem[0];
        private int _searchSnapshotCount = 0;
        
        private CancellationTokenSource _searchCancellationTokenSource;
        private System.Windows.Threading.DispatcherTimer _debounceTimer;
        
        private string _currentDrive = "C";
        
        // FSW
        private FileSystemWatcher _watcher;
        private object _fswLock = new object();
        private HashSet<string> _fswCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _fswDeleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Threading.DispatcherTimer _fswTimer;

        public MainWindow()
        {
            InitializeComponent();
            
            _debounceTimer = new System.Windows.Threading.DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(300);
            _debounceTimer.Tick += DebounceTimer_Tick;
            
            _fswTimer = new System.Windows.Threading.DispatcherTimer();
            _fswTimer.Interval = TimeSpan.FromMilliseconds(1000);
            _fswTimer.Tick += FswTimer_Tick;
            _fswTimer.Start();
            
            Loaded += MainWindow_Loaded;
        }
        
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadFileSystemData("C");
        }

        private async void LoadFileSystemData(string driveLetter)
        {
            _currentDrive = driveLetter;
            lblStatus.Text = $"Loading {driveLetter}:\\\\ ...";
            listView.ItemsSource = null;

            try
            {
                var nodes = await Task.Run(() =>
                {
                    try {
                        var ntfsReader = new NtfsReader.NtfsReader(new DriveInfo(driveLetter), RetrieveMode.All);
                        return ntfsReader.GetNodes(driveLetter + ":\\\\").ToList();
                    } catch { return new List<INode>(); }
                });

                await Task.Run(() =>
                {
                    var fileItems = new List<FileItem>(nodes.Count);
                    foreach (var node in nodes)
                    {
                        if (node.FullName.Contains("") || node.FullName.Contains("") || 
                            node.FullName.Contains("") || node.FullName.Contains(""))
                            continue;
                        
                        fileItems.Add(new FileItem
                        {
                            FullName = node.FullName,
                            FileName = Path.GetFileName(node.FullName),
                            Size = node.Size,
                            LastWriteTime = node.LastChangeTime
                        });
                    }
                    _allItems = fileItems;
                });

                SetupFileSystemWatcher(driveLetter);
                TriggerSearch();
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error loading drive: " + ex.Message;
            }
        }
        
        private void SetupFileSystemWatcher(string driveLetter)
        {
            if (_watcher != null) {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            
            try {
                _watcher = new FileSystemWatcher(driveLetter + ":\\\\");
                _watcher.IncludeSubdirectories = true;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                
                _watcher.Created += (s, e) => {
                    lock (_fswLock) { _fswCreated.Add(e.FullPath); _fswDeleted.Remove(e.FullPath); }
                };
                
                _watcher.Deleted += (s, e) => {
                    lock (_fswLock) { _fswDeleted.Add(e.FullPath); _fswCreated.Remove(e.FullPath); }
                };
                
                _watcher.Renamed += (s, e) => {
                    lock (_fswLock) { 
                        _fswDeleted.Add(e.OldFullPath);
                        _fswCreated.Add(e.FullPath);
                    }
                };
                
                _watcher.EnableRaisingEvents = true;
            } catch { }
        }
        
        private void FswTimer_Tick(object sender, EventArgs e)
        {
            if (_allItems == null) return;
            
            List<string> created = null;
            List<string> deleted = null;
            
            lock (_fswLock)
            {
                if (_fswCreated.Count > 0) { created = _fswCreated.ToList(); _fswCreated.Clear(); }
                if (_fswDeleted.Count > 0) { deleted = _fswDeleted.ToList(); _fswDeleted.Clear(); }
            }
            
            if (created == null && deleted == null) return;
            
            bool changed = false;
            
            if (deleted != null)
            {
                int removed = _allItems.RemoveAll(x => {
                    foreach (var d in deleted) {
                        if (string.Equals(x.FullName, d, StringComparison.OrdinalIgnoreCase) || 
                            x.FullName.StartsWith(d + "\\\\", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                });
                if (removed > 0) changed = true;
            }
            
            if (created != null)
            {
                foreach (var c in created)
                {
                    try {
                        var fi = new FileInfo(c);
                        if (fi.Exists || Directory.Exists(c)) {
                            _allItems.Add(new FileItem { 
                                FullName = c, 
                                FileName = Path.GetFileName(c), 
                                Size = fi.Exists ? fi.Length : 0, 
                                LastWriteTime = fi.Exists ? fi.LastWriteTime : DateTime.Now 
                            });
                            changed = true;
                        }
                    } catch { }
                }
            }
            
            if (changed && string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                listView.ItemsSource = _allItems;
                lblStatus.Text = $"Matches: {_allItems.Count:N0}";
            }
        }

        private void txtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void DebounceTimer_Tick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            TriggerSearch();
        }

        private void TriggerSearch()
        {
            string query = txtSearch.Text.Trim();
            ApplyFilterAsync(query);
        }

        private async void ApplyFilterAsync(string pattern)
        {
            _searchCancellationTokenSource?.Cancel();
            _searchCancellationTokenSource = new CancellationTokenSource();
            var token = _searchCancellationTokenSource.Token;

            if (string.IsNullOrWhiteSpace(pattern))
            {
                listView.ItemsSource = _allItems;
                lblStatus.Text = $"Matches: {_allItems.Count:N0}";
                return;
            }
            
            if (_searchSnapshot.Length < _allItems.Count)
            {
                _searchSnapshot = new FileItem[_allItems.Count + 100000];
            }
            _allItems.CopyTo(_searchSnapshot);
            _searchSnapshotCount = _allItems.Count;

            bool isWildcard = pattern.Contains("*") || pattern.Contains("?");
            Regex wildRegex = null;
            if (isWildcard)
            {
                string regexPattern = Regex.Escape(pattern).Replace("\\\\*", ".*").Replace("\\\\?", ".");
                try {
                    wildRegex = new Regex(regexPattern, RegexOptions.IgnoreCase);
                } catch { }
            }

            Func<FileItem, bool> matcher = item =>
            {
                if (wildRegex != null) return wildRegex.IsMatch(item.FileName);
                return item.FileName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            };

            try
            {
                var matched = await Task.Run(() =>
                {
                    var result = new List<FileItem>(50000);
                    int count = 0;
                    for (int i = 0; i < _searchSnapshotCount; i++)
                    {
                        if (++count % 10000 == 0 && token.IsCancellationRequested)
                            token.ThrowIfCancellationRequested();
                        
                        FileItem item = _searchSnapshot[i];
                        if (item == null) continue;
                            
                        if (matcher(item))
                        {
                            result.Add(item);
                            if (result.Count >= 50000) break;
                        }
                    }
                    return result;
                }, token);

                if (!token.IsCancellationRequested)
                {
                    listView.ItemsSource = matched;
                    lblStatus.Text = $"Matches: {matched.Count:N0}";
                }
            }
            catch (OperationCanceledException) { }
        }

        private void listView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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
            
            lblPreviewDetails.Text = sb.ToString();
            
            // Icon
            Task.Run(() => {
                try {
                    Bitmap bmp = null;
                    if (Directory.Exists(item.FullName)) {
                        var img = IconHelper.GetIconForFolder(item.FullName);
                        if (img != null) bmp = new Bitmap(img);
                    } else if (File.Exists(item.FullName)) {
                        var icon = System.Drawing.Icon.ExtractAssociatedIcon(item.FullName);
                        if (icon != null) bmp = icon.ToBitmap();
                    }
                    
                    if (bmp != null) {
                        Dispatcher.Invoke(() => {
                            if (listView.SelectedItem == item) {
                                picPreviewIcon.Source = Imaging.CreateBitmapSourceFromHBitmap(
                                    bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            }
                            bmp.Dispose();
                        });
                    }
                } catch { }
            });
        }

        private void btnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) FileUtils.Open(item.FullName);
        }

        private void btnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (listView.SelectedItem is FileItem item) FileUtils.OpenFileLocationAndSelect(item.FullName);
        }
    }
    
    public class FileItem
    {
        public string FullName { get; set; }
        public string FileName { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public string Extension => Path.GetExtension(FileName)?.TrimStart('.').ToUpperInvariant() ?? "";
        
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
}'''

with open(os.path.join(src_dir, 'MainWindow.xaml.cs'), 'w', encoding='utf-8') as f:
    f.write(main_xaml_cs)

print("MainWindow.xaml.cs created.")
