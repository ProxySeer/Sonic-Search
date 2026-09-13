using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class FileUtils
{
    /// <summary>
    /// Opens the specified file using the default associated application - or, for a bookmark's
    /// URL (see SonicSearch.BrowserBookmarks), the default browser. ShellExecute happily accepts
    /// either, so the only extra step is skipping the File.Exists/Directory.Exists check, which a
    /// URL would never pass.
    /// </summary>
    public static void Open(string filePath)
    {
        if (SonicSearch.BrowserBookmarks.IsUrl(filePath))
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            return;
        }

        // A packaged/Store app's WindowsApps path bakes its exact version into the folder name,
        // so it silently stops existing the next time the app auto-updates - launch by its
        // AppUserModelId instead (see WindowsAppHelper), which Windows keeps stable regardless
        // of which version is currently installed. Falls through to the normal path below if the
        // package can't be resolved (e.g. genuinely uninstalled, not just updated).
        if (SonicSearch.WindowsAppHelper.IsWindowsAppsPath(filePath))
        {
            string familyName = SonicSearch.WindowsAppHelper.TryGetPackageFamilyName(filePath);
            var (aumid, _) = SonicSearch.WindowsAppHelper.TryResolveCurrentApp(familyName);
            if (!string.IsNullOrEmpty(aumid))
            {
                SonicSearch.WindowsAppHelper.LaunchByAppUserModelId(aumid);
                return;
            }
        }

        if (File.Exists(filePath) || Directory.Exists(filePath))
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        else
        {
            ShowMissingFileWarning(filePath);
        }
    }

    /// <summary>
    /// Copies the full path of the file or directory to the clipboard.
    /// </summary>
    public static void CopyPathToClipboard(string filePath)
    {
        if (SonicSearch.BrowserBookmarks.IsUrl(filePath) || File.Exists(filePath) || Directory.Exists(filePath))
        {
            Clipboard.SetText(filePath);
        }
        else
        {
            ShowMissingFileWarning(filePath);
        }
    }

    /// <summary>
    /// Copies just the name of the file or directory to the clipboard.
    /// </summary>
    public static void CopyFileNameToClipboard(string filePath)
    {
        if (File.Exists(filePath) || Directory.Exists(filePath))
        {
            Clipboard.SetText(Path.GetFileName(filePath));
        }
        else
        {
            ShowMissingFileWarning(filePath);
        }
    }

    /// <summary>
    /// Copies the parent directory path of the file or directory to the clipboard.
    /// </summary>
    public static void CopyDirectoryPathToClipboard(string filePath)
    {
        if (File.Exists(filePath) || Directory.Exists(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Clipboard.SetText(dir);
            }
        }
        else
        {
            ShowMissingFileWarning(filePath);
        }
    }

    /// <summary>
    /// Opens File Explorer and selects the specified file.
    /// </summary>
    public static void OpenFileLocationAndSelect(string filePath)
    {
        if (SonicSearch.BrowserBookmarks.IsUrl(filePath))
        {
            MessageBox.Show("This is a bookmark, not a file - there's no folder to open.",
                "Not a File", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (File.Exists(filePath) || Directory.Exists(filePath))
        {
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        else
        {
            ShowMissingFileWarning(filePath);
        }
    }

    /// <summary>
    /// Creates a shortcut to a file or folder at the specified shortcut path.
    /// </summary>
    public static void CreateShortcut(string targetPath, string shortcutPath)
    {
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            ShowMissingFileWarning(targetPath);
            return;
        }

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        var shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.Save();
    }

    /// <summary>
    /// Displays the properties dialog for a file or folder.
    /// </summary>
    public static void ShowFileProperties(string filePath)
    {
        if (SonicSearch.BrowserBookmarks.IsUrl(filePath))
        {
            MessageBox.Show("This is a bookmark, not a file - there are no file properties to show.",
                "Not a File", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            ShowMissingFileWarning(filePath);
            return;
        }

        var info = new WinApiHelper.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<WinApiHelper.SHELLEXECUTEINFO>(),
            lpVerb = "properties",
            lpFile = filePath,
            nShow = WinApiHelper.SW_SHOW,
            fMask = WinApiHelper.SEE_MASK_INVOKEIDLIST
        };

        WinApiHelper.ShellExecuteEx(ref info);
    }

    /// <summary>
    /// Shows a warning if the specified file or folder does not exist.
    /// </summary>
    private static void ShowMissingFileWarning(string filePath)
    {
        MessageBox.Show(
            $"The file or folder does not exist:\n{filePath}",
            "Not Found",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }
}
