import os
import re

filepath = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch/FormMain.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

watcher_fields = '''        private System.Collections.Concurrent.ConcurrentDictionary<string, List<INode>> _driveCache = new System.Collections.Concurrent.ConcurrentDictionary<string, List<INode>>(StringComparer.OrdinalIgnoreCase);
        private System.Collections.Concurrent.ConcurrentDictionary<string, FileSystemWatcher> _driveWatchers = new System.Collections.Concurrent.ConcurrentDictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);'''

content = content.replace('private System.Collections.Concurrent.ConcurrentDictionary<string, List<INode>> _driveCache = new System.Collections.Concurrent.ConcurrentDictionary<string, List<INode>>(StringComparer.OrdinalIgnoreCase);', watcher_fields)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
