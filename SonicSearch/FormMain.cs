using BrightIdeasSoftware;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Filesystem.Ntfs;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;


namespace SonicSearch
{
    public partial class FormMain : Form
    {
        public FormMain()
        {
            InitializeComponent();
        }
        //private readonly ConcurrentDictionary<string, INode> allNodes = new();
        public List<INode> Nodes { get; private set; }
        private readonly Stopwatch stopwatch = new();
        private FileSystemVirtualDataSource dataSourceFileSystem;
        private FormWait waitForm = new FormWait();
        private ContextMenuStrip contextMenu;

        private void FormFolderSize_Load(object sender, EventArgs e)
        {
            stopwatch.Start();
            ShowInfoControls(false);
            InitializeContextMenu();
            ConfigureFastObjectListViewFileSystem();
            ConfigureTreeListView();
            this.Enabled = false;
            waitForm = new FormWait();
            waitForm.Owner = this;
            waitForm.Show(); //

            Task.Run(() => LoadFileSystemData("C"));
        }
        private void InitializeContextMenu()
        {
            contextMenu = new ContextMenuStrip();

            contextMenu.Items.Add("Open", null, (s, e) => OpenSelectedItem());
            contextMenu.Items.Add("Open Location", null, (s, e) => OpenSelectedItemLocation());
            contextMenu.Items.Add("Copy Path", null, (s, e) => CopySelectedItemPath());
            contextMenu.Items.Add("Properties", null, (s, e) => ShowSelectedItemProperties());

            fastObjectLvFileSystem.ContextMenuStrip = contextMenu;
        }
        private FileItem? GetRightClickedItem(MouseEventArgs e)
        {
            var hit = fastObjectLvFileSystem.OlvHitTest(e.X, e.Y);
            return hit?.Item?.RowObject as FileItem;
        }

        private void OpenSelectedItem()
        {
            if (fastObjectLvFileSystem.SelectedObject is FileItem item)
                FileUtils.Open(item.FullName);
        }

        private void OpenSelectedItemLocation()
        {
            if (fastObjectLvFileSystem.SelectedObject is FileItem item)
                FileUtils.OpenFileLocationAndSelect(item.FullName);
        }

        private void CopySelectedItemPath()
        {
            if (fastObjectLvFileSystem.SelectedObject is FileItem item)
                FileUtils.CopyPathToClipboard(item.FullName);
        }

        private void ShowSelectedItemProperties()
        {
            if (fastObjectLvFileSystem.SelectedObject is FileItem item)
                FileUtils.ShowFileProperties(item.FullName);
        }
        private void ShowInfoControls(bool isVisible)
        {
            lblTotalCount.Visible = isVisible;
            lblTotalSize.Visible = isVisible;
            picTotalFiles.Visible = isVisible;
            picTotalSize.Visible = isVisible;

        }
        private void SetTotalSizeAndCount()
        {
            lblTotalCount.Text = "Total Count: " + Nodes.Count.ToString();

            if (treeListView1.Roots?.OfType<FolderNode>().FirstOrDefault() is FolderNode root)
            {
                lblTotalSize.Text = "Total Size: " + root.FormattedSize;
            }
            else
            {
                lblTotalSize.Text = "Total Size: 0 B";
            }
        }
        private void LoadFileSystemData(string driveLetter)
        {
            try
            {
                waitForm.Invoke((MethodInvoker)(() => waitForm.UpdateStatus("Reading file system...")));

                var drive = new DriveInfo(driveLetter);
                var ntfsReader = new NtfsReader(drive, RetrieveMode.All);

              
                Nodes = ntfsReader.GetNodes(drive.Name);

                waitForm.Invoke((MethodInvoker)(() => waitForm.UpdateStatus($"Discovered {Nodes.Count:N0} entries. Indexing...")));



                
                var fileItems = Nodes.AsParallel().Select(node => new FileItem
                {
                    FullName = node.FullName,
                    FileName = Path.GetFileName(node.FullName),
                    Size = node.Size > 0 ? (long)node.Size : 0,
                    LastWriteTime = node.LastChangeTime
                }).ToList();
                
                
                waitForm.Invoke((MethodInvoker)(() => waitForm.UpdateStatus("Preparing file list...")));


                this.Invoke((MethodInvoker)(() =>
                {
                    dataSourceFileSystem.ReplaceAllItems(fileItems);
                    fastObjectLvFileSystem.BuildList();

                    stopwatch.Stop();
                    Text = $"Sonic Search Loaded in {stopwatch.Elapsed.TotalSeconds:F2} seconds";
                    ShowInfoControls(true);
                    SetTotalSizeAndCount();

                    waitForm.Close();
                    waitForm.Dispose();
                    this.Enabled = true;
                    this.BringToFront();
                }));
            }
            catch (Exception ex)
            {
                this.Invoke((MethodInvoker)(() =>
                {
                    MessageBox.Show($"Error: {ex.Message}", "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
            }
        }
        private FolderNode BuildTreeFromCachedNodes(List<INode> nodeList, string rootPath)
        {
            rootPath = rootPath.TrimEnd('\\').ToUpperInvariant();
            return BuildTreeFromMftNodes(nodeList, rootPath);
        }
        private void ConfigureTreeListView()
        {
            var smallIcons = new ImageList
            {
                ImageSize = new Size(16, 16),
                ColorDepth = ColorDepth.Depth32Bit
            };

            IconHelper.ImageListSmall = smallIcons;
            treeListView1.SmallImageList = smallIcons;
            treeListView1.Columns.Clear();

            treeListView1.Columns.Add(new OLVColumn("Name", "Name")
            {
                Width = 300,
                ImageGetter = x => IconHelper.GetIconIndexSmall(x is FolderNode f ? f.FullPath : (x as FileNode)?.FullPath ?? string.Empty)
            });

            treeListView1.Columns.Add(new OLVColumn("Size", "FormattedSize")
            {
                Width = 100,
                TextAlign = HorizontalAlignment.Right
            });

            treeListView1.Columns.Add(new OLVColumn("Path", "FullPath")
            {
                Width = 400
            });

            treeListView1.CanExpandGetter = x => x is FolderNode f && f.Children?.Any() == true;
            treeListView1.ChildrenGetter = x => (x as FolderNode)?.Children;
        }

        private void ConfigureFastObjectListViewFileSystem()
        {
            var largeIcons = new ImageList
            {
                ImageSize = new Size(32, 32),
                ColorDepth = ColorDepth.Depth32Bit
            };

            IconHelper.ImageListLarge = largeIcons;
            fastObjectLvFileSystem.SmallImageList = largeIcons;

            fastObjectLvFileSystem.Columns.Clear();

            fastObjectLvFileSystem.Columns.Add(new OLVColumn
            {
                Text = "",
                Width = 50,
                TextAlign = HorizontalAlignment.Center,
                ImageGetter = x => (x as FileItem)?.IconIndex ?? -1
            });

            fastObjectLvFileSystem.Columns.Add(new OLVColumn("Full Path", "FullName") { Width = 400 });
            fastObjectLvFileSystem.Columns.Add(new OLVColumn("File Name", "FileName") { Width = 150 });
            fastObjectLvFileSystem.Columns.Add(new OLVColumn("Size (Bytes)", "Size") { Width = 100 });
            fastObjectLvFileSystem.Columns.Add(new OLVColumn("Last Write Time", "LastWriteTime") { Width = 150 });

            fastObjectLvFileSystem.FullRowSelect = true;
            fastObjectLvFileSystem.GridLines = false;
            fastObjectLvFileSystem.VirtualMode = true;

            dataSourceFileSystem = new FileSystemVirtualDataSource(new List<FileItem>());
            fastObjectLvFileSystem.VirtualListDataSource = dataSourceFileSystem;
            fastObjectLvFileSystem.MouseDown += fastObjectLvFileSystem_MouseDown;

        }
        private void fastObjectLvFileSystem_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var hit = fastObjectLvFileSystem.OlvHitTest(e.X, e.Y);
                if (hit?.Item != null)
                {
                    fastObjectLvFileSystem.SelectedObject = hit.RowObject;
                }
            }
        }

        private FolderNode BuildTreeFromMftNodes(List<INode> allNodes, string rootPath)
        {
            rootPath = rootPath.TrimEnd('\\').ToUpperInvariant();

            var folderNodes = new ConcurrentDictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);

            // Create root folder node
            folderNodes[rootPath] = new FolderNode
            {
                Name = Path.GetFileName(rootPath),
                FullPath = rootPath,
                Children = new List<object>(),
                TotalSize = 0
            };

            // Step 1: Create folder nodes in parallel
            Parallel.ForEach(allNodes, node =>
            {
                if (string.Equals(Path.GetFileName(node.FullName), "$BadClus", StringComparison.OrdinalIgnoreCase))
                    return;

                var parentDir = Path.GetDirectoryName(node.FullName)?.TrimEnd('\\').ToUpperInvariant();
                if (parentDir == null)
                    return;

                // Skip $BadClus folders
                if (string.Equals(Path.GetFileName(parentDir), "$BadClus", StringComparison.OrdinalIgnoreCase))
                    return;

                folderNodes.GetOrAdd(parentDir, dir => new FolderNode
                {
                    Name = Path.GetFileName(dir),
                    FullPath = dir,
                    Children = new List<object>(),
                    TotalSize = 0
                });
            });

            // Step 2: Link folder nodes to their parents (sequential - to avoid race conditions)
            foreach (var folder in folderNodes.Values.ToList())
            {
                if (string.Equals(folder.Name, "$BadClus", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parentDir = Path.GetDirectoryName(folder.FullPath)?.TrimEnd('\\').ToUpperInvariant();
                if (parentDir != null && folderNodes.ContainsKey(parentDir) && !string.Equals(folder.FullPath, rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    lock (folderNodes[parentDir].Children)
                    {
                        folderNodes[parentDir].Children.Add(folder);
                    }
                }
            }

            // Step 3: Add files as FileNodes in parallel, collecting children per folder in thread-safe way
            var folderChildrenMap = new ConcurrentDictionary<string, List<FileNode>>();

            Parallel.ForEach(allNodes, node =>
            {
                if (string.Equals(Path.GetFileName(node.FullName), "$BadClus", StringComparison.OrdinalIgnoreCase))
                    return;

                if ((node.Attributes & Attributes.Directory) != 0)
                    return;

                var parentDir = Path.GetDirectoryName(node.FullName)?.TrimEnd('\\').ToUpperInvariant();
                if (parentDir == null)
                    return;

                if (!folderNodes.ContainsKey(parentDir))
                    return;

                long size = (long)node.Size;
                if (size <= 50)
                {
                    try
                    {
                        var fi = new FileInfo(node.FullName);
                        if (fi.Exists)
                            size = fi.Length;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                var fileNode = new FileNode
                {
                    Name = Path.GetFileName(node.FullName),
                    FullPath = node.FullName,
                    Size = size > 0 ? size : 0
                };

                // Add to folderChildrenMap per folder (thread safe)
                var childrenList = folderChildrenMap.GetOrAdd(parentDir, _ => new List<FileNode>());
                lock (childrenList)
                {
                    childrenList.Add(fileNode);
                }
            });

            // Step 4: After all files processed, add the collected children to folder nodes
            foreach (var kvp in folderChildrenMap)
            {
                if (folderNodes.TryGetValue(kvp.Key, out var folderNode))
                {
                    lock (folderNode.Children)
                    {
                        folderNode.Children.AddRange(kvp.Value);
                    }
                }
            }

            var rootNode = folderNodes[rootPath];

            // Step 5: Compute sizes sequentially (recursive)
            void ComputeSizes(FolderNode folder)
            {
                folder.TotalSize = 0;
                foreach (var child in folder.Children)
                {
                    if (child is FolderNode childFolder)
                    {
                        if (string.Equals(childFolder.Name, "$BadClus", StringComparison.OrdinalIgnoreCase))
                            continue;

                        ComputeSizes(childFolder);
                        folder.TotalSize += childFolder.TotalSize;
                    }
                    else if (child is FileNode fileNode)
                    {
                        if (string.Equals(fileNode.Name, "$BadClus", StringComparison.OrdinalIgnoreCase))
                            continue;

                        folder.TotalSize += fileNode.Size;
                    }
                }

                folder.Children.Sort((a, b) =>
                {
                    long sizeA = a is FolderNode fa ? fa.TotalSize : ((FileNode)a).Size;
                    long sizeB = b is FolderNode fb ? fb.TotalSize : ((FileNode)b).Size;

                    int cmp = sizeB.CompareTo(sizeA);
                    if (cmp != 0) return cmp;

                    bool aIsFolder = a is FolderNode;
                    bool bIsFolder = b is FolderNode;

                    if (aIsFolder && !bIsFolder) return -1;
                    if (!aIsFolder && bIsFolder) return 1;
                    return 0;
                });
            }

            ComputeSizes(rootNode);

      
         
            return rootNode;
        }


        public class FolderNode
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public long TotalSize { get; set; }
            public List<object> Children { get; set; } = new List<object>(); // FolderNode or FileNode

            public string FormattedSize => FormatSize(TotalSize);

            private static string FormatSize(long size)
            {
                if (size >= 1_000_000_000)
                    return $"{size / 1_000_000_000.0:F2} GB";
                if (size >= 1_000_000)
                    return $"{size / 1_000_000.0:F2} MB";
                if (size >= 1_000)
                    return $"{size / 1_000.0:F2} KB";
                return $"{size} B";
            }
        }

        public class FileNode
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public long Size { get; set; }

            public string FormattedSize => FormatSize(Size);

            private static string FormatSize(long size)
            {
                if (size >= 1_000_000_000)
                    return $"{size / 1_000_000_000.0:F2} GB";
                if (size >= 1_000_000)
                    return $"{size / 1_000_000.0:F2} MB";
                if (size >= 1_000)
                    return $"{size / 1_000.0:F2} KB";
                return $"{size} B";
            }
        }
        static Func<FileItem, bool> CreateFastMatcher(string? pattern, Regex? currentRegex)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                // No filter, accept all
                return _ => true;
            }

            if (pattern.Contains("*"))
            {
                // Convert wildcard pattern (e.g. battle*.exe) to regex ^battle.*\.exe$
                var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);

                return fileItem => regex.IsMatch(fileItem.FileName);
            }
            else if (currentRegex != null)
            {
                return fileItem => currentRegex.IsMatch(fileItem.FileName);
            }
            else
            {
                // Plain text search, match filename contains pattern ignoring case
                return fileItem => fileItem.FileName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        private string WildcardToRegex(string pattern)
        {
            return "^" + Regex.Escape(pattern)
                             .Replace(@"\*", ".*")
                             .Replace(@"\?", ".") + "$";
        }
        private Regex? currentRegex;
        private async Task ApplyRegexFilterAsync(string pattern)
        {


            if (dataSourceFileSystem == null)
                return;


            // Your current tab key for favorites

            if (string.IsNullOrWhiteSpace(pattern))
            {
                currentRegex = null;

                // objectListView1.ModelFilter = null;
                // Show all filesystem items
                dataSourceFileSystem.SetObjects(dataSourceFileSystem.AllItems);
                fastObjectLvFileSystem.BeginUpdate();
                fastObjectLvFileSystem.BuildList();
                fastObjectLvFileSystem.EndUpdate();


                return;
            }

            try
            {
                string regexPattern = WildcardToRegex(pattern);
                currentRegex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                Console.WriteLine($"Compiled regex: {regexPattern}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Invalid regex: " + ex.Message);
                return;
            }

            string extFilter = null;
            var trimmedPattern = pattern.Trim();
            if (trimmedPattern.StartsWith("*.") &&
                !trimmedPattern.Substring(2).Contains("*") &&
                !trimmedPattern.Substring(2).Contains("?"))
            {
                extFilter = trimmedPattern.Substring(1).ToLowerInvariant();
            }





            string? folderFilter = lblSelectedPath.Text.Trim();
            folderFilter = string.IsNullOrWhiteSpace(folderFilter) ? null : Path.GetFullPath(folderFilter)?.ToLowerInvariant();

            string? ext = extFilter?.ToLowerInvariant(); // cache lowercase

            // Use the raw search input string, NOT regex pattern string
            var searchPattern = txtSearch.Text.Trim();

            var fileNameMatcher = CreateFastMatcher(searchPattern, currentRegex);

            var regexTask = Task.Run(() =>
            {
                List<FileItem> filteredByFolder;

                if (!string.IsNullOrEmpty(folderFilter))
                {
                    filteredByFolder = dataSourceFileSystem.AllItems
                        .Where(item => item.FullName.StartsWith(folderFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                else
                {
                    filteredByFolder = (List<FileItem>)dataSourceFileSystem.AllItems;
                }

                List<FileItem> filteredByExtension;

                if (ext != null)
                {
                    filteredByExtension = filteredByFolder
                        .Where(item =>
                        {
                            var fn = item.FileName;
                            int idx = fn.LastIndexOf('.');
                            return idx != -1 && fn.Length - idx <= 6 &&
                                   string.Compare(fn, idx, ext, 0, ext.Length, true) == 0;
                        })
                        .ToList();
                }
                else
                {
                    filteredByExtension = filteredByFolder;
                }

                if (filteredByFolder.Count == 0 || filteredByExtension.Count == 0)
                    return new List<FileItem>();

                var candidateItems = filteredByExtension;

                var matched = candidateItems
                    .AsParallel()
                    .WithDegreeOfParallelism(Environment.ProcessorCount)
                    .Where(item => fileNameMatcher(item))
                    .ToList();

                Console.WriteLine($"[Matcher] Matched {matched.Count} items under {folderFilter}");
                return matched;
            });

            try
            {
                var matchedItems = await regexTask;


                fastObjectLvFileSystem.BeginUpdate();
                dataSourceFileSystem.SetObjects(matchedItems);
                fastObjectLvFileSystem.BuildList();
                fastObjectLvFileSystem.EndUpdate();
            }
            catch (OperationCanceledException)
            {
                // ignore cancellation
            }



        }




        private async void btnSearch_Click(object sender, EventArgs e)
        {


            string query = txtSearch.Text.Trim();






            await ApplyRegexFilterAsync(query);

        }

        private void btnSelectPath_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select a folder";
                dialog.ShowNewFolderButton = true; // Set to false if you don't want the user to create a new folder

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string selectedPath = dialog.SelectedPath;

                

                    lblSelectedPath.Text = selectedPath;
                }

            }
        }

        private void btnContact_Click(object sender, EventArgs e)
        {
            string url = "https://www.linkedin.com/in/erol-%C3%A7imen-7b86552a0/"; // 🔗 Replace with your desired URL

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true // ✅ Required for launching URLs in .NET Core / .NET 5+
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open link: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            this.Enabled = false;
            waitForm = new FormWait();
            waitForm.Owner = this;
            waitForm.Show(); //
                             // Clear views
            treeListView1.ClearObjects();
            treeListView1.Roots = null;

            dataSourceFileSystem.SetObjects(new List<FileItem>());
            fastObjectLvFileSystem.BuildList();

            lblTotalCount.Text = "Total Count: 0";
            lblTotalSize.Text = "Total Size: 0 B";

            // Restart stopwatch if needed
            stopwatch.Reset();
            stopwatch.Start();

            // Reload asynchronously
            Task.Run(() => LoadFileSystemData("C"));
        }

        private void fastObjectLvFileSystem_DoubleClick(object sender, EventArgs e)
        {
            if (fastObjectLvFileSystem.SelectedObject is FileItem item &&
         !string.IsNullOrWhiteSpace(item.FullName))
            {
                FileUtils.Open(item.FullName);
            }
        }

        private void btnCalculate_Click(object sender, EventArgs e)
        {
            this.Enabled = false;
            waitForm = new FormWait();
            waitForm.Owner = this;
            waitForm.Show();

            Task.Run(() =>
            {
                try
                {
                   waitForm.Invoke((MethodInvoker)(() => waitForm.UpdateStatus("Building folder tree...")));

                    var rootNode = BuildTreeFromCachedNodes(Nodes, "C:\\");

                    this.Invoke((MethodInvoker)(() =>
                    {
                        treeListView1.Roots = new List<FolderNode> { rootNode };
                        SetTotalSizeAndCount();
                        waitForm.Close();
                        waitForm.Dispose();                        
                        this.Enabled = true;
                        this.BringToFront();
                    }));
                }
                catch (Exception ex)
                {
                    this.Invoke((MethodInvoker)(() =>
                  {
                      waitForm.Close();
                      waitForm.Dispose();
                      this.Enabled = true;
                      this.BringToFront();
                      MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                  }));

                }
            });
        }

        private void tabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            
                btnCalculate.Visible = tabControl1.SelectedIndex == 1 ? true : false;
        }
    }
}