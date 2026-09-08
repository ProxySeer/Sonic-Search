import os
import re

filepath = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch/FormMain.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

# Replace ApplyFilterAsync to include detailed logging to a file in the app directory
new_apply = '''        private async void ApplyFilterAsync(string pattern)
        {
            _searchCancellationTokenSource?.Cancel();
            _searchCancellationTokenSource = new CancellationTokenSource();
            var token = _searchCancellationTokenSource.Token;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long t1=0, t2=0, t3=0, t4=0;

            if (string.IsNullOrWhiteSpace(pattern) && !chkRegex.Checked)
            {
                dataSourceFileSystem.SetFilteredItemsFast((List<FileItem>)dataSourceFileSystem.AllItems);
                fastObjectLvFileSystem.BuildList();
                UpdateStatusAfterSearch(dataSourceFileSystem.AllItems.Count);
                return;
            }

            string folderFilter = lblSelectedPath.Text.Replace("Scope: ", "").Trim();
            if (folderFilter.Equals("Entire Drive", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(folderFilter))
                folderFilter = null;
            else
                folderFilter = Path.GetFullPath(folderFilter).ToLowerInvariant();

            bool isRegex = chkRegex.Checked || pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase);
            if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                pattern = pattern.Substring(6);

            bool matchCase = chkMatchCase.Checked;
            bool matchPath = chkMatchPath.Checked;
            
            Regex regex = null;
            if (isRegex)
            {
                try
                {
                    regex = new Regex(pattern, matchCase ? RegexOptions.None : RegexOptions.None | RegexOptions.IgnoreCase);
                }
                catch { return; }
            }
            
            StringComparison comp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            
            Regex wildRegex = null;
            bool isWildcard = !isRegex && (pattern.Contains("*") || pattern.Contains("?"));
            if (isWildcard)
            {
                string regexPattern = "^" + Regex.Escape(pattern).replace("\\\\*", ".*").replace("\\\\?", ".") + "$";
                wildRegex = new Regex(regexPattern, matchCase ? RegexOptions.None : RegexOptions.None | RegexOptions.IgnoreCase);
            }

            Func<FileItem, bool> matcher = item =>
            {
                string target = matchPath ? item.FullName : item.FileName;
                if (isRegex) return regex.IsMatch(target);
                else if (isWildcard) return wildRegex.IsMatch(target);
                else return target.IndexOf(pattern, comp) >= 0;
            };

            var items = (List<FileItem>)dataSourceFileSystem.AllItems;
            t1 = sw.ElapsedMilliseconds;

            try
            {
                var matched = await Task.Run(() =>
                {
                    try
                    {
                        var result = new List<FileItem>(items.Count / 100);
                        int count = 0;
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (++count % 10000 == 0 && token.IsCancellationRequested)
                                token.ThrowIfCancellationRequested();
                            
                            FileItem item;
                            try { item = items[i]; } catch (ArgumentOutOfRangeException) { break; }
                            
                            if (folderFilter != null && !item.FullName.StartsWith(folderFilter, StringComparison.OrdinalIgnoreCase))
                                continue;
                                
                            if (matcher(item))
                                result.Add(item);
                        }
                        return result;
                    }
                    catch (OperationCanceledException)
                    {
                        return new List<FileItem>();
                    }
                    catch (AggregateException ae)
                    {
                        ae.Handle(ex => ex is OperationCanceledException);
                        return new List<FileItem>();
                    }
                }, token);
                
                t2 = sw.ElapsedMilliseconds;

                if (!token.IsCancellationRequested)
                {
                    fastObjectLvFileSystem.BeginUpdate();
                    dataSourceFileSystem.SetFilteredItemsFast(matched);
                    t3 = sw.ElapsedMilliseconds;
                    
                    fastObjectLvFileSystem.BuildList();
                    fastObjectLvFileSystem.EndUpdate();
                    t4 = sw.ElapsedMilliseconds;
                    
                    UpdateStatusAfterSearch(matched.Count);
                    
                    try {
                        System.IO.File.AppendAllText("search_perf.log", 
                            $"[{DateTime.Now:HH:mm:ss.fff}] Search '{pattern}': Setup={t1}ms, TaskRun={t2-t1}ms, SetObjects={t3-t2}ms, BuildList={t4-t3}ms, Total={t4}ms. Matches: {matched.Count}\\n");
                    } catch { }
                }
            }
            catch (OperationCanceledException) { }
        }'''

# use regex to replace the entire method
pattern = r'private async void ApplyFilterAsync\(string pattern\)\s*\{.*?catch \(OperationCanceledException\) \{ \}\s*\}'
content = re.sub(pattern, new_apply, content, flags=re.DOTALL)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
