using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class IconHelper
{
    private static readonly Dictionary<string, int> smallIconMap = new();
    private static readonly Dictionary<string, int> largeIconMap = new();

    public static ImageList ImageListSmall { get; set; }
    public static ImageList ImageListLarge { get; set; }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    public static int GetIconIndexSmall(string filePath)
    {
        return GetIconIndex(filePath, true);
    }

    public static int GetIconIndexLarge(string filePath)
    {
        return GetIconIndex(filePath, false);
    }

    private static int GetIconIndex(string filePath, bool small)
    {
        if (string.IsNullOrEmpty(filePath)) return -1;

        bool isFolder = Directory.Exists(filePath) || filePath.EndsWith("\\");
        string key = isFolder ? "__folder__" : Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";

        var iconMap = small ? smallIconMap : largeIconMap;
        var list = small ? ImageListSmall : ImageListLarge;

        if (iconMap.TryGetValue(key, out int cachedIndex))
            return cachedIndex;

        Image icon;

        if (isFolder)
        {
            icon = small ? GetShellFolderIcon(SHGFI_SMALLICON) : GetShellFolderIcon(SHGFI_LARGEICON);
        }
        else
        {
            icon = small
                ? GetShellIcon(filePath, false, SHGFI_SMALLICON)
                : GetShellIcon(filePath, false, SHGFI_LARGEICON);
        }

        int index = list.Images.Count;
        list.Images.Add(icon);
        iconMap[key] = index;
        return index;
    }
    private static Image GetShellFolderIcon(uint sizeFlag)
    {
        SHFILEINFO shinfo = new();
        uint flags = SHGFI_ICON | sizeFlag | SHGFI_USEFILEATTRIBUTES;
        string dummyFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows); // like C:\Windows

        IntPtr hImg = SHGetFileInfo(dummyFolderPath, FILE_ATTRIBUTE_DIRECTORY,
            ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);

        if (shinfo.hIcon != IntPtr.Zero)
        {
            using Icon icon = Icon.FromHandle(shinfo.hIcon);
            Image img = icon.ToBitmap();
            NativeMethods.DestroyIcon(shinfo.hIcon);
            return img;
        }

        return SystemIcons.WinLogo.ToBitmap();
    }
    private static Image GetShellIcon(string path, bool isFolder, uint sizeFlag)
    {
        SHFILEINFO shinfo = new();
        uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | sizeFlag;
        uint attr = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        IntPtr hImg = SHGetFileInfo(path, attr, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);

        if (shinfo.hIcon != IntPtr.Zero)
        {
            using Icon icon = Icon.FromHandle(shinfo.hIcon);
            Image img = icon.ToBitmap();
            NativeMethods.DestroyIcon(shinfo.hIcon);
            return img;
        }

        return SystemIcons.WinLogo.ToBitmap(); // Fallback
    }

    public static Image GetIconForFolder(string folderPath)
    {
        SHFILEINFO shinfo = new();
        IntPtr hImg = SHGetFileInfo(folderPath, FILE_ATTRIBUTE_DIRECTORY, ref shinfo, (uint)Marshal.SizeOf(shinfo),
            SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | SHGFI_LARGEICON);

        if (shinfo.hIcon != IntPtr.Zero)
        {
            using Icon icon = Icon.FromHandle(shinfo.hIcon);
            Image img = icon.ToBitmap();
            NativeMethods.DestroyIcon(shinfo.hIcon);
            return img;
        }

        return GetDefaultFolderIcon();
    }

    private static Image GetDefaultFolderIcon()
    {
        SHFILEINFO shinfo = new();
        IntPtr hImg = SHGetFileInfo("C:\\", FILE_ATTRIBUTE_DIRECTORY, ref shinfo, (uint)Marshal.SizeOf(shinfo),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (shinfo.hIcon != IntPtr.Zero)
        {
            using Icon icon = Icon.FromHandle(shinfo.hIcon);
            Image img = icon.ToBitmap();
            NativeMethods.DestroyIcon(shinfo.hIcon);
            return img;
        }

        return SystemIcons.WinLogo.ToBitmap();
    }
}
