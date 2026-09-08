import os
import re

filepath = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch/FormMain.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Add fields for Preview Panel
fields = '''        // Preview Panel Fields
        private SplitContainer splitContainerPreview;
        private Panel panelPreview;
        private PictureBox picPreviewIcon;
        private Label lblPreviewName;
        private Label lblPreviewDetails;
        private Button btnPreviewOpenFolder;
        private Button btnPreviewOpenFile;
        
'''

content = content.replace('public FormMain()', fields + 'public FormMain()')

# 2. Add InitializePreviewPanel method
init_preview = '''
        private void InitializePreviewPanel()
        {
            splitContainerPreview = new SplitContainer();
            splitContainerPreview.Dock = DockStyle.Fill;
            splitContainerPreview.SplitterWidth = 4;
            splitContainerPreview.FixedPanel = FixedPanel.Panel2;
            splitContainerPreview.Orientation = Orientation.Vertical;
            
            // Swap out fastObjectLvFileSystem
            var parent = fastObjectLvFileSystem.Parent;
            parent.Controls.Remove(fastObjectLvFileSystem);
            
            splitContainerPreview.Panel1.Controls.Add(fastObjectLvFileSystem);
            parent.Controls.Add(splitContainerPreview);
            parent.Controls.SetChildIndex(splitContainerPreview, 0); // ensure it's in front or back appropriately
            
            // Build Panel2
            panelPreview = new Panel();
            panelPreview.Dock = DockStyle.Fill;
            panelPreview.Padding = new Padding(15);
            panelPreview.BackColor = Color.FromArgb(32, 32, 32); // Dark theme
            panelPreview.ForeColor = Color.White;
            
            picPreviewIcon = new PictureBox();
            picPreviewIcon.Size = new Size(128, 128);
            picPreviewIcon.SizeMode = PictureBoxSizeMode.Zoom;
            picPreviewIcon.Location = new Point((splitContainerPreview.Panel2.Width - 128) / 2, 20);
            picPreviewIcon.Anchor = AnchorStyles.Top;
            
            lblPreviewName = new Label();
            lblPreviewName.AutoSize = false;
            lblPreviewName.TextAlign = ContentAlignment.TopCenter;
            lblPreviewName.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            lblPreviewName.Location = new Point(10, 160);
            lblPreviewName.Width = splitContainerPreview.Panel2.Width - 20;
            lblPreviewName.Height = 50;
            lblPreviewName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            
            lblPreviewDetails = new Label();
            lblPreviewDetails.AutoSize = false;
            lblPreviewDetails.TextAlign = ContentAlignment.TopLeft;
            lblPreviewDetails.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblPreviewDetails.Location = new Point(10, 220);
            lblPreviewDetails.Width = splitContainerPreview.Panel2.Width - 20;
            lblPreviewDetails.Height = 150;
            lblPreviewDetails.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            
            btnPreviewOpenFile = new Button();
            btnPreviewOpenFile.Text = "Open File";
            btnPreviewOpenFile.FlatStyle = FlatStyle.Flat;
            btnPreviewOpenFile.BackColor = Color.FromArgb(45, 45, 48);
            btnPreviewOpenFile.ForeColor = Color.White;
            btnPreviewOpenFile.Size = new Size(100, 30);
            btnPreviewOpenFile.Location = new Point(10, 380);
            btnPreviewOpenFile.Click += (s, e) => { if (fastObjectLvFileSystem.SelectedObject is FileItem fi) FileUtils.OpenFile(fi.FullName); };
            
            btnPreviewOpenFolder = new Button();
            btnPreviewOpenFolder.Text = "Open Folder";
            btnPreviewOpenFolder.FlatStyle = FlatStyle.Flat;
            btnPreviewOpenFolder.BackColor = Color.FromArgb(45, 45, 48);
            btnPreviewOpenFolder.ForeColor = Color.White;
            btnPreviewOpenFolder.Size = new Size(100, 30);
            btnPreviewOpenFolder.Location = new Point(120, 380);
            btnPreviewOpenFolder.Click += (s, e) => { if (fastObjectLvFileSystem.SelectedObject is FileItem fi) FileUtils.OpenFolderAndSelectFile(fi.FullName); };
            
            panelPreview.Controls.Add(picPreviewIcon);
            panelPreview.Controls.Add(lblPreviewName);
            panelPreview.Controls.Add(lblPreviewDetails);
            panelPreview.Controls.Add(btnPreviewOpenFile);
            panelPreview.Controls.Add(btnPreviewOpenFolder);
            
            splitContainerPreview.Panel2.Controls.Add(panelPreview);
            splitContainerPreview.Panel2.Resize += (s, e) => {
                picPreviewIcon.Left = (splitContainerPreview.Panel2.Width - picPreviewIcon.Width) / 2;
                lblPreviewName.Width = splitContainerPreview.Panel2.Width - 20;
                lblPreviewDetails.Width = splitContainerPreview.Panel2.Width - 20;
            };
            
            splitContainerPreview.SplitterDistance = splitContainerPreview.Width - 280;
            
            // Hook up selection event
            fastObjectLvFileSystem.SelectionChanged += FastObjectLvFileSystem_SelectionChanged;
            
            UpdatePreviewPanel(null);
        }
        
        private void FastObjectLvFileSystem_SelectionChanged(object sender, EventArgs e)
        {
            var selected = fastObjectLvFileSystem.SelectedObject as FileItem;
            UpdatePreviewPanel(selected);
        }
        
        private void UpdatePreviewPanel(FileItem item)
        {
            if (item == null)
            {
                picPreviewIcon.Image = null;
                lblPreviewName.Text = "No file selected";
                lblPreviewDetails.Text = "";
                btnPreviewOpenFile.Visible = false;
                btnPreviewOpenFolder.Visible = false;
                return;
            }
            
            lblPreviewName.Text = item.FileName;
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Location:");
            string dir = System.IO.Path.GetDirectoryName(item.FullName);
            sb.AppendLine(dir);
            sb.AppendLine();
            sb.AppendLine("Size: " + item.FormattedSize);
            sb.AppendLine("Modified: " + item.FormattedDate);
            
            try {
                var fi = new System.IO.FileInfo(item.FullName);
                if (fi.Exists) {
                    sb.AppendLine("Created: " + fi.CreationTime.ToString("yyyy-MM-dd HH:mm"));
                }
            } catch { }
            
            lblPreviewDetails.Text = sb.ToString();
            
            btnPreviewOpenFile.Visible = true;
            btnPreviewOpenFolder.Visible = true;
            
            // Extract large icon asynchronously
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    // ExtractAssociatedIcon gets a 32x32 icon, but WinApiHelper might have a better one?
                    // Let's use IconHelper.GetLargeIcon
                    var bmp = IconHelper.GetLargeIconBitmap(item.FullName);
                    if (bmp != null) {
                        this.BeginInvoke((MethodInvoker)(() => {
                            if (fastObjectLvFileSystem.SelectedObject == item) {
                                var old = picPreviewIcon.Image;
                                picPreviewIcon.Image = bmp;
                                if (old != null) old.Dispose();
                            } else {
                                bmp.Dispose();
                            }
                        }));
                    }
                } catch { }
            });
        }
'''

content = content.replace('private void InitializePreviewPanel', '// already injected')
content = content.replace('private void LoadFileSystemData', init_preview + '\n        private void LoadFileSystemData')

# 3. Call InitializePreviewPanel in constructor
call_pattern = r'ApplyModernTheme\(\);'
call_replacement = 'ApplyModernTheme();\n            InitializePreviewPanel();'
content = re.sub(call_pattern, call_replacement, content)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
