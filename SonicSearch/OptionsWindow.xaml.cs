using System.Reflection;
using System.Windows;

namespace SonicSearch
{
    public partial class OptionsWindow : Window
    {
        public OptionsWindow()
        {
            InitializeComponent();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            lblAppVersion.Text = version != null
                ? $"Version {version.Major}.{version.Minor}.{version.Build}"
                : "Version 1.0.0";

            txtMaxResults.Text = AppSettings.Instance.MaxResults.ToString();
            txtPrioritize.Text = AppSettings.Instance.PrioritizedExtensions;
            txtExclude.Text = AppSettings.Instance.ExcludedExtensions;
            txtDebounce.Text = AppSettings.Instance.DebounceMs.ToString();
            txtReindexMinutes.Text = AppSettings.Instance.AutoReindexMinutes.ToString();
            chkQuickStart.IsChecked = AppSettings.Instance.QuickStartFirstResult;
            chkRealtimeWatcher.IsChecked = AppSettings.Instance.EnableRealtimeWatcher;
            txtMonitoredFolders.Text = AppSettings.Instance.MonitoredFolders ?? "";
            txtIncludedFolders.Text = AppSettings.Instance.IncludedIndexFolders ?? "";
            txtExcludedFolders.Text = AppSettings.Instance.ExcludedIndexFolders ?? "";

            string searchMode = AppSettings.Instance.SearchMode ?? "Contains";
            foreach (System.Windows.Controls.ComboBoxItem item in cmbSearchMode.Items)
            {
                if (item.Content.ToString().StartsWith(searchMode, System.StringComparison.OrdinalIgnoreCase))
                {
                    cmbSearchMode.SelectedItem = item;
                    break;
                }
            }
            
            txtHotkeyKey.Text = string.IsNullOrEmpty(AppSettings.Instance.HotkeyKey) ? "S" : AppSettings.Instance.HotkeyKey.ToUpper();
            foreach (System.Windows.Controls.ComboBoxItem item in cmbHotkeyModifier.Items)
            {
                if (string.Equals(item.Content.ToString(), AppSettings.Instance.HotkeyModifiers, System.StringComparison.OrdinalIgnoreCase))
                {
                    cmbHotkeyModifier.SelectedItem = item;
                    break;
                }
            }
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to monitor in real-time:";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    string current = txtMonitoredFolders.Text.Trim();
                    if (string.IsNullOrEmpty(current))
                    {
                        txtMonitoredFolders.Text = dialog.SelectedPath;
                    }
                    else
                    {
                        if (current.EndsWith(",") || current.EndsWith(";"))
                            txtMonitoredFolders.Text = current + " " + dialog.SelectedPath;
                        else
                            txtMonitoredFolders.Text = current + ", " + dialog.SelectedPath;
                    }
                }
            }
        }

        private void BtnAddIncludedFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to include in search index (and its subfolders):";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    string current = txtIncludedFolders.Text.Trim();
                    if (string.IsNullOrEmpty(current))
                    {
                        txtIncludedFolders.Text = dialog.SelectedPath;
                    }
                    else
                    {
                        if (current.EndsWith(",") || current.EndsWith(";"))
                            txtIncludedFolders.Text = current + " " + dialog.SelectedPath;
                        else
                            txtIncludedFolders.Text = current + ", " + dialog.SelectedPath;
                    }
                }
            }
        }

        private void BtnAddExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to exclude from search index:";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    string current = txtExcludedFolders.Text.Trim();
                    if (string.IsNullOrEmpty(current))
                    {
                        txtExcludedFolders.Text = dialog.SelectedPath;
                    }
                    else
                    {
                        if (current.EndsWith(",") || current.EndsWith(";"))
                            txtExcludedFolders.Text = current + " " + dialog.SelectedPath;
                        else
                            txtExcludedFolders.Text = current + ", " + dialog.SelectedPath;
                    }
                }
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtMaxResults.Text, out int max) && max > 0) AppSettings.Instance.MaxResults = max;
            if (int.TryParse(txtDebounce.Text, out int debounce) && debounce >= 0) AppSettings.Instance.DebounceMs = debounce;
            if (int.TryParse(txtReindexMinutes.Text, out int reindex) && reindex >= 0) AppSettings.Instance.AutoReindexMinutes = reindex;
            
            AppSettings.Instance.PrioritizedExtensions = txtPrioritize.Text;
            AppSettings.Instance.ExcludedExtensions = txtExclude.Text;
            AppSettings.Instance.QuickStartFirstResult = chkQuickStart.IsChecked == true;
            AppSettings.Instance.EnableRealtimeWatcher = chkRealtimeWatcher.IsChecked == true;
            AppSettings.Instance.MonitoredFolders = txtMonitoredFolders.Text.Trim();
            AppSettings.Instance.IncludedIndexFolders = txtIncludedFolders.Text.Trim();
            AppSettings.Instance.ExcludedIndexFolders = txtExcludedFolders.Text.Trim();

            if (cmbSearchMode.SelectedItem is System.Windows.Controls.ComboBoxItem modeItem)
            {
                string txt = modeItem.Content.ToString();
                if (txt.StartsWith("Starts With", System.StringComparison.OrdinalIgnoreCase)) AppSettings.Instance.SearchMode = "StartsWith";
                else if (txt.StartsWith("Exact Match", System.StringComparison.OrdinalIgnoreCase)) AppSettings.Instance.SearchMode = "Exact";
                else if (txt.StartsWith("Regex", System.StringComparison.OrdinalIgnoreCase)) AppSettings.Instance.SearchMode = "Regex";
                else AppSettings.Instance.SearchMode = "Contains";
            }

            if (cmbHotkeyModifier.SelectedItem is System.Windows.Controls.ComboBoxItem cbi)
            {
                AppSettings.Instance.HotkeyModifiers = cbi.Content.ToString();
            }
            string key = txtHotkeyKey.Text.Trim().ToUpper();
            if (!string.IsNullOrEmpty(key))
            {
                AppSettings.Instance.HotkeyKey = key.Substring(0, 1);
            }

            AppSettings.Save();
            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}