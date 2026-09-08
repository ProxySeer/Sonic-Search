import os

filepath = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch/FormMain.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

closed_method = '''
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var watcher in _driveWatchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _driveWatchers.Clear();
            base.OnFormClosed(e);
        }
'''

content = content.replace('private void FormMain_Load(object sender, EventArgs e)', closed_method + '\n        private void FormMain_Load(object sender, EventArgs e)')

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
