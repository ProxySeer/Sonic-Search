import os

filepath = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch/MainWindow.xaml.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

# Inject a fake item into _allItems at the end of LoadFileSystemData
target = '''                _allItems = fileItems;
                SetupFileSystemWatchers();
                TriggerSearch();'''

replacement = '''                fileItems.Add(new FileItem { FullName = @"C:\Fake\Battle.net.exe", FileName = "Battle.net.exe", Size = 12345, LastWriteTime = DateTime.Now });
                _allItems = fileItems;
                SetupFileSystemWatchers();
                TriggerSearch();'''

content = content.replace(target, replacement)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
