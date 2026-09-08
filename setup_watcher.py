import os
import re

filepath = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch/FormMain.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

setup_watcher = '''                        List<INode> loadedNodes;
                        if (!_driveCache.TryGetValue(drive.Name, out loadedNodes))
                        {
                            loadedNodes = ntfsReader.GetNodes(drive.Name);
                            _driveCache[drive.Name] = loadedNodes;
                            
                            // Setup watcher for live updates
                            try {
                                var watcher = new FileSystemWatcher(drive.Name);
                                watcher.IncludeSubdirectories = true;
                                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                                
                                watcher.Created += (s, e) => {
                                    if (_currentDrive + ":\\\\" != drive.Name) return;
                                    var fi = new FileInfo(e.FullPath);
                                    var item = new FileItem { FullName = e.FullPath, FileName = e.Name, Size = fi.Exists ? fi.Length : 0, LastWriteTime = DateTime.Now };
                                    this.Invoke((MethodInvoker)(() => {
                                        dataSourceFileSystem.AllItems.Add(item);
                                        if (string.IsNullOrWhiteSpace(txtSearch.Text)) {
                                            dataSourceFileSystem.SetObjects(dataSourceFileSystem.AllItems);
                                            fastObjectLvFileSystem.BuildList();
                                            SetTotalSizeAndCount();
                                        }
                                    }));
                                };
                                
                                watcher.Deleted += (s, e) => {
                                    if (_currentDrive + ":\\\\" != drive.Name) return;
                                    this.Invoke((MethodInvoker)(() => {
                                        var items = dataSourceFileSystem.AllItems;
                                        int removed = items.RemoveAll(x => string.Equals(x.FullName, e.FullPath, StringComparison.OrdinalIgnoreCase) || x.FullName.StartsWith(e.FullPath + "\\\\", StringComparison.OrdinalIgnoreCase));
                                        if (removed > 0 && string.IsNullOrWhiteSpace(txtSearch.Text)) {
                                            dataSourceFileSystem.SetObjects(dataSourceFileSystem.AllItems);
                                            fastObjectLvFileSystem.BuildList();
                                            SetTotalSizeAndCount();
                                        }
                                    }));
                                };
                                
                                watcher.Renamed += (s, e) => {
                                    if (_currentDrive + ":\\\\" != drive.Name) return;
                                    this.Invoke((MethodInvoker)(() => {
                                        var items = dataSourceFileSystem.AllItems;
                                        foreach(var item in items) {
                                            if (string.Equals(item.FullName, e.OldFullPath, StringComparison.OrdinalIgnoreCase)) {
                                                item.FullName = e.FullPath;
                                                item.FileName = e.Name;
                                            }
                                        }
                                        if (string.IsNullOrWhiteSpace(txtSearch.Text)) {
                                            fastObjectLvFileSystem.RefreshObjects(items);
                                        }
                                    }));
                                };
                                
                                watcher.EnableRaisingEvents = true;
                                _driveWatchers[drive.Name] = watcher;
                            } catch { }
                        }'''

content = content.replace('''                        List<INode> loadedNodes;
                        if (!_driveCache.TryGetValue(drive.Name, out loadedNodes))
                        {
                            loadedNodes = ntfsReader.GetNodes(drive.Name);
                            _driveCache[drive.Name] = loadedNodes;
                        }''', setup_watcher)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
