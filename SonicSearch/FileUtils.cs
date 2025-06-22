using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class FileUtils
{
    /// <summary>
    /// Opens the specified file using the default associated application.
    /// </summary>
    public static void Open(string filePath)
    {
        if (File.Exists(filePath))
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
        if (File.Exists(filePath) || Directory.Exists(filePath))
        {
            Clipboard.SetText(filePath);
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
        if (File.Exists(filePath))
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
