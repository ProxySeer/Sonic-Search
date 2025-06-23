namespace SonicSearch
{
    partial class FormMain
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FormMain));
            this.treeListView1 = new BrightIdeasSoftware.TreeListView();
            this.imageList1 = new System.Windows.Forms.ImageList(this.components);
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage2 = new System.Windows.Forms.TabPage();
            this.fastObjectLvFileSystem = new BrightIdeasSoftware.FastObjectListView();
            this.panel1 = new System.Windows.Forms.Panel();
            this.btnSelectPath = new System.Windows.Forms.Button();
            this.lblSelectedPath = new System.Windows.Forms.Label();
            this.btnSearch = new System.Windows.Forms.Button();
            this.txtSearch = new System.Windows.Forms.TextBox();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.panel2 = new System.Windows.Forms.Panel();
            this.btnCalculate = new System.Windows.Forms.Button();
            this.btnRefresh = new System.Windows.Forms.Button();
            this.lblTotalSize = new System.Windows.Forms.Label();
            this.picTotalSize = new System.Windows.Forms.PictureBox();
            this.btnContact = new System.Windows.Forms.Button();
            this.lblTotalCount = new System.Windows.Forms.Label();
            this.picTotalFiles = new System.Windows.Forms.PictureBox();
            ((System.ComponentModel.ISupportInitialize)(this.treeListView1)).BeginInit();
            this.tabControl1.SuspendLayout();
            this.tabPage2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.fastObjectLvFileSystem)).BeginInit();
            this.panel1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            this.panel2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.picTotalSize)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.picTotalFiles)).BeginInit();
            this.SuspendLayout();
            // 
            // treeListView1
            // 
            this.treeListView1.CellEditUseWholeCell = false;
            this.treeListView1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.treeListView1.HideSelection = false;
            this.treeListView1.Location = new System.Drawing.Point(3, 3);
            this.treeListView1.Name = "treeListView1";
            this.treeListView1.ShowGroups = false;
            this.treeListView1.Size = new System.Drawing.Size(786, 388);
            this.treeListView1.TabIndex = 1;
            this.treeListView1.UseCompatibleStateImageBehavior = false;
            this.treeListView1.View = System.Windows.Forms.View.Details;
            this.treeListView1.VirtualMode = true;
            // 
            // imageList1
            // 
            this.imageList1.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("imageList1.ImageStream")));
            this.imageList1.TransparentColor = System.Drawing.Color.Transparent;
            this.imageList1.Images.SetKeyName(0, "document.png");
            this.imageList1.Images.SetKeyName(1, "folder (2).png");
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabPage2);
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(800, 420);
            this.tabControl1.TabIndex = 2;
            this.tabControl1.SelectedIndexChanged += new System.EventHandler(this.tabControl1_SelectedIndexChanged);
            // 
            // tabPage2
            // 
            this.tabPage2.Controls.Add(this.fastObjectLvFileSystem);
            this.tabPage2.Controls.Add(this.panel1);
            this.tabPage2.Location = new System.Drawing.Point(4, 22);
            this.tabPage2.Name = "tabPage2";
            this.tabPage2.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage2.Size = new System.Drawing.Size(792, 394);
            this.tabPage2.TabIndex = 1;
            this.tabPage2.Text = "Search Filesystem";
            this.tabPage2.UseVisualStyleBackColor = true;
            // 
            // fastObjectLvFileSystem
            // 
            this.fastObjectLvFileSystem.CellEditUseWholeCell = false;
            this.fastObjectLvFileSystem.CopySelectionOnControlC = false;
            this.fastObjectLvFileSystem.Dock = System.Windows.Forms.DockStyle.Fill;
            this.fastObjectLvFileSystem.HideSelection = false;
            this.fastObjectLvFileSystem.Location = new System.Drawing.Point(3, 57);
            this.fastObjectLvFileSystem.Name = "fastObjectLvFileSystem";
            this.fastObjectLvFileSystem.ShowGroups = false;
            this.fastObjectLvFileSystem.Size = new System.Drawing.Size(786, 334);
            this.fastObjectLvFileSystem.TabIndex = 1;
            this.fastObjectLvFileSystem.UseCompatibleStateImageBehavior = false;
            this.fastObjectLvFileSystem.View = System.Windows.Forms.View.Details;
            this.fastObjectLvFileSystem.VirtualMode = true;
            this.fastObjectLvFileSystem.DoubleClick += new System.EventHandler(this.fastObjectLvFileSystem_DoubleClick);
            // 
            // panel1
            // 
            this.panel1.Controls.Add(this.btnSelectPath);
            this.panel1.Controls.Add(this.lblSelectedPath);
            this.panel1.Controls.Add(this.btnSearch);
            this.panel1.Controls.Add(this.txtSearch);
            this.panel1.Dock = System.Windows.Forms.DockStyle.Top;
            this.panel1.Location = new System.Drawing.Point(3, 3);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(786, 54);
            this.panel1.TabIndex = 0;
            // 
            // btnSelectPath
            // 
            this.btnSelectPath.BackColor = System.Drawing.SystemColors.ControlLight;
            this.btnSelectPath.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnSelectPath.FlatAppearance.BorderSize = 0;
            this.btnSelectPath.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnSelectPath.Image = ((System.Drawing.Image)(resources.GetObject("btnSelectPath.Image")));
            this.btnSelectPath.Location = new System.Drawing.Point(6, 34);
            this.btnSelectPath.Name = "btnSelectPath";
            this.btnSelectPath.Padding = new System.Windows.Forms.Padding(0, 2, 0, 0);
            this.btnSelectPath.Size = new System.Drawing.Size(16, 16);
            this.btnSelectPath.TabIndex = 17;
            this.btnSelectPath.UseVisualStyleBackColor = false;
            this.btnSelectPath.Click += new System.EventHandler(this.btnSelectPath_Click);
            // 
            // lblSelectedPath
            // 
            this.lblSelectedPath.AutoSize = true;
            this.lblSelectedPath.Font = new System.Drawing.Font("Microsoft Sans Serif", 6.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblSelectedPath.Location = new System.Drawing.Point(28, 30);
            this.lblSelectedPath.Name = "lblSelectedPath";
            this.lblSelectedPath.Padding = new System.Windows.Forms.Padding(0, 7, 0, 0);
            this.lblSelectedPath.Size = new System.Drawing.Size(21, 19);
            this.lblSelectedPath.TabIndex = 16;
            this.lblSelectedPath.Text = "C:\\";
            // 
            // btnSearch
            // 
            this.btnSearch.Location = new System.Drawing.Point(602, 5);
            this.btnSearch.Name = "btnSearch";
            this.btnSearch.Size = new System.Drawing.Size(75, 23);
            this.btnSearch.TabIndex = 15;
            this.btnSearch.Text = "Search";
            this.btnSearch.UseVisualStyleBackColor = true;
            this.btnSearch.Click += new System.EventHandler(this.btnSearch_Click);
            // 
            // txtSearch
            // 
            this.txtSearch.AutoCompleteMode = System.Windows.Forms.AutoCompleteMode.SuggestAppend;
            this.txtSearch.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtSearch.Location = new System.Drawing.Point(5, 6);
            this.txtSearch.Name = "txtSearch";
            this.txtSearch.Size = new System.Drawing.Size(590, 22);
            this.txtSearch.TabIndex = 14;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.treeListView1);
            this.tabPage1.Location = new System.Drawing.Point(4, 22);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage1.Size = new System.Drawing.Size(792, 394);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "Compute Filesystem";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // panel2
            // 
            this.panel2.Controls.Add(this.btnCalculate);
            this.panel2.Controls.Add(this.btnRefresh);
            this.panel2.Controls.Add(this.lblTotalSize);
            this.panel2.Controls.Add(this.picTotalSize);
            this.panel2.Controls.Add(this.btnContact);
            this.panel2.Controls.Add(this.lblTotalCount);
            this.panel2.Controls.Add(this.picTotalFiles);
            this.panel2.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.panel2.Location = new System.Drawing.Point(0, 420);
            this.panel2.Name = "panel2";
            this.panel2.Size = new System.Drawing.Size(800, 30);
            this.panel2.TabIndex = 3;
            // 
            // btnCalculate
            // 
            this.btnCalculate.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCalculate.Location = new System.Drawing.Point(544, 4);
            this.btnCalculate.Name = "btnCalculate";
            this.btnCalculate.Size = new System.Drawing.Size(87, 23);
            this.btnCalculate.TabIndex = 21;
            this.btnCalculate.Text = "Calculate Size";
            this.btnCalculate.UseVisualStyleBackColor = true;
            this.btnCalculate.Visible = false;
            this.btnCalculate.Click += new System.EventHandler(this.btnCalculate_Click);
            // 
            // btnRefresh
            // 
            this.btnRefresh.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnRefresh.Location = new System.Drawing.Point(637, 3);
            this.btnRefresh.Name = "btnRefresh";
            this.btnRefresh.Size = new System.Drawing.Size(75, 23);
            this.btnRefresh.TabIndex = 18;
            this.btnRefresh.Text = "Refresh";
            this.btnRefresh.UseVisualStyleBackColor = true;
            this.btnRefresh.Click += new System.EventHandler(this.btnRefresh_Click);
            // 
            // lblTotalSize
            // 
            this.lblTotalSize.AutoEllipsis = true;
            this.lblTotalSize.AutoSize = true;
            this.lblTotalSize.Font = new System.Drawing.Font("Microsoft Sans Serif", 6.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblTotalSize.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.lblTotalSize.Location = new System.Drawing.Point(165, 9);
            this.lblTotalSize.Name = "lblTotalSize";
            this.lblTotalSize.Size = new System.Drawing.Size(62, 12);
            this.lblTotalSize.TabIndex = 20;
            this.lblTotalSize.Text = "Total Size :";
            this.lblTotalSize.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // picTotalSize
            // 
            this.picTotalSize.Image = ((System.Drawing.Image)(resources.GetObject("picTotalSize.Image")));
            this.picTotalSize.Location = new System.Drawing.Point(143, 7);
            this.picTotalSize.Name = "picTotalSize";
            this.picTotalSize.Size = new System.Drawing.Size(16, 16);
            this.picTotalSize.TabIndex = 19;
            this.picTotalSize.TabStop = false;
            // 
            // btnContact
            // 
            this.btnContact.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnContact.Location = new System.Drawing.Point(718, 3);
            this.btnContact.Name = "btnContact";
            this.btnContact.Size = new System.Drawing.Size(75, 23);
            this.btnContact.TabIndex = 18;
            this.btnContact.Text = "Contact";
            this.btnContact.UseVisualStyleBackColor = true;
            this.btnContact.Click += new System.EventHandler(this.btnContact_Click);
            // 
            // lblTotalCount
            // 
            this.lblTotalCount.AutoEllipsis = true;
            this.lblTotalCount.AutoSize = true;
            this.lblTotalCount.Font = new System.Drawing.Font("Microsoft Sans Serif", 6.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblTotalCount.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.lblTotalCount.Location = new System.Drawing.Point(26, 9);
            this.lblTotalCount.Name = "lblTotalCount";
            this.lblTotalCount.Size = new System.Drawing.Size(65, 12);
            this.lblTotalCount.TabIndex = 13;
            this.lblTotalCount.Text = "Total Files :";
            this.lblTotalCount.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // picTotalFiles
            // 
            this.picTotalFiles.Image = ((System.Drawing.Image)(resources.GetObject("picTotalFiles.Image")));
            this.picTotalFiles.Location = new System.Drawing.Point(6, 7);
            this.picTotalFiles.Name = "picTotalFiles";
            this.picTotalFiles.Size = new System.Drawing.Size(16, 16);
            this.picTotalFiles.TabIndex = 14;
            this.picTotalFiles.TabStop = false;
            // 
            // FormMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.tabControl1);
            this.Controls.Add(this.panel2);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "FormMain";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Sonic Search";
            this.Load += new System.EventHandler(this.FormFolderSize_Load);
            ((System.ComponentModel.ISupportInitialize)(this.treeListView1)).EndInit();
            this.tabControl1.ResumeLayout(false);
            this.tabPage2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.fastObjectLvFileSystem)).EndInit();
            this.panel1.ResumeLayout(false);
            this.panel1.PerformLayout();
            this.tabPage1.ResumeLayout(false);
            this.panel2.ResumeLayout(false);
            this.panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.picTotalSize)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.picTotalFiles)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private BrightIdeasSoftware.TreeListView treeListView1;
        private System.Windows.Forms.ImageList imageList1;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.TabPage tabPage2;
        private System.Windows.Forms.Panel panel1;
        private BrightIdeasSoftware.FastObjectListView fastObjectLvFileSystem;
        private System.Windows.Forms.Button btnSearch;
        private System.Windows.Forms.TextBox txtSearch;
        private System.Windows.Forms.Button btnSelectPath;
        private System.Windows.Forms.Label lblSelectedPath;
        private System.Windows.Forms.Panel panel2;
        private System.Windows.Forms.Label lblTotalCount;
        private System.Windows.Forms.PictureBox picTotalFiles;
        private System.Windows.Forms.Label lblTotalSize;
        private System.Windows.Forms.PictureBox picTotalSize;
        private System.Windows.Forms.Button btnContact;
        private System.Windows.Forms.Button btnRefresh;
        private System.Windows.Forms.Button btnCalculate;
    }
}