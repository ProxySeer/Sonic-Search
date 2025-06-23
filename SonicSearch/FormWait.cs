using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class FormWait : Form
{
    private Label label;
    private Label statusLabel;
    private PictureBox pictureBox;
    private Button btnHide;

    private Panel titlePanel;

    public FormWait()
    {
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.CenterParent;
        this.Width = 300;
        this.Height = 140;
        this.BackColor = Color.White;

      
        titlePanel = new Panel
        {
            Height = 32,
            Dock = DockStyle.Top,
            BackColor = Color.White
        };
        titlePanel.MouseDown += Drag_MouseDown;
        titlePanel.MouseMove += Drag_MouseMove;
        titlePanel.MouseUp += Drag_MouseUp;
        this.Controls.Add(titlePanel);

    
        label = new Label
        {
            Text = "Please wait...",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 0, 0)
        };
        label.MouseDown += Drag_MouseDown;
        label.MouseMove += Drag_MouseMove;
        label.MouseUp += Drag_MouseUp;
        titlePanel.Controls.Add(label);

     
        btnHide = new Button
        {
            Text = "✖",
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            Size = new Size(30, 30),
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White
        };
        btnHide.FlatAppearance.BorderSize = 0;
        btnHide.Click += BtnHide_Click;
        titlePanel.Controls.Add(btnHide);

      
        statusLabel = new Label
        {
            Text = "Analyzing files...",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = Color.White
        };
        this.Controls.Add(statusLabel);

   
        pictureBox = new PictureBox
        {
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Bottom,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
            Image = SonicSearch.Properties.Resources._1zno
        };
        this.Controls.Add(pictureBox);

        this.Load += FormWait_Load;
    }
    public void UpdateStatus(string message)
    {
        statusLabel.Text = message;
    }
    public void SetStatus(string text, Color? foreColor = null)
    {
        if (label.InvokeRequired)
        {
            label.Invoke((MethodInvoker)(() => SetStatusInternal(text, foreColor)));
        }
        else
        {
            SetStatusInternal(text, foreColor);
        }
    }
    private void SetStatusInternal(string text, Color? foreColor)
    {
        label.Text = text;
        if (foreColor.HasValue)
            label.ForeColor = foreColor.Value;
    }

    private Point _dragOffset;
    private bool _dragging = false;

    private void Drag_MouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragOffset = e.Location;
        }
    }

    private void Drag_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            var dx = e.X - _dragOffset.X;
            var dy = e.Y - _dragOffset.Y;

            this.Left += dx;
            this.Top += dy;

            if (this.Owner != null)
            {
                this.Owner.Left += dx;
                this.Owner.Top += dy;
            }
        }
    }

    private void Drag_MouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _dragging = false;
    }

    private void BtnHide_Click(object sender, EventArgs e)
    {
        this.Hide();
        if (this.Owner != null)
            this.Owner.Close();
    }

    private void FormWait_Load(object sender, EventArgs e)
    {

 

        label.Width = this.ClientSize.Width;
        pictureBox.Size = new Size(this.ClientSize.Width, this.ClientSize.Height - label.Height - 20);
        pictureBox.Location = new Point(0, label.Bottom + 5);

 
        if (this.Owner != null)
        {
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(
                this.Owner.Location.X + (this.Owner.Width - this.Width) / 2,
                this.Owner.Location.Y + (this.Owner.Height - this.Height) / 2
            );
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableShadow();
    }

    private void EnableShadow()
    {
        int val = 2;
        DwmApi.DwmSetWindowAttribute(this.Handle, 2, ref val, 4);
        var margins = new Margins { cxLeftWidth = 1, cxRightWidth = 1, cyTopHeight = 1, cyBottomHeight = 1 };
        DwmApi.DwmExtendFrameIntoClientArea(this.Handle, ref margins);
    }



    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    public static class DwmApi
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins pMarInset);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    }
    public static class NativeMethods
    {
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    }


}
