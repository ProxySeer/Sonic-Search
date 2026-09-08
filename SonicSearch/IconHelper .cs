using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
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
        string ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
        bool uniqueIcon = ext == ".exe" || ext == ".ico" || ext == ".lnk";
        string key = isFolder ? "__folder__" : (uniqueIcon ? filePath.ToLowerInvariant() : ext);

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

    private static readonly Dictionary<string, ImageSource> _wpfSmallIconCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource GetSmallIconForPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        bool isFolder = Directory.Exists(path) || path.EndsWith(@"\") || path.EndsWith("/");
        string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
        bool uniqueIcon = ext == ".exe" || ext == ".ico" || ext == ".lnk";
        string key = isFolder ? "__folder__" : (uniqueIcon ? path.ToLowerInvariant() : ext);

        lock (_wpfSmallIconCache)
        {
            if (_wpfSmallIconCache.TryGetValue(key, out var cached))
                return cached;
        }

        ImageSource result = null;
        try
        {
            Image img;
            if (isFolder)
            {
                img = GetShellFolderIcon(SHGFI_SMALLICON);
            }
            else
            {
                img = GetShellIcon(path, false, SHGFI_SMALLICON);
            }

            if (img is Bitmap bmp)
            {
                IntPtr hBitmap = bmp.GetHbitmap();
                try
                {
                    result = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    result.Freeze();
                }
                finally
                {
                    DeleteObject(hBitmap);
                    bmp.Dispose();
                }
            }
        }
        catch { }

        if (result != null)
        {
            lock (_wpfSmallIconCache)
            {
                _wpfSmallIconCache[key] = result;
            }
        }

        return result;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);


    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const int SHIL_EXTRALARGE = 0x2; // 48x48
    private const int SHIL_JUMBO = 0x4;      // 256x256

    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig]
        int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    public static ImageSource GetJumboIconForPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            SHFILEINFO shinfo = new();
            uint flags = SHGFI_SYSICONINDEX;
            uint attr = (Directory.Exists(path) || path.EndsWith(@"\") || path.EndsWith("/")) 
                ? FILE_ATTRIBUTE_DIRECTORY 
                : FILE_ATTRIBUTE_NORMAL;

            IntPtr res = SHGetFileInfo(path, attr, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
            if (res != IntPtr.Zero && shinfo.iIcon >= 0)
            {
                Guid iidImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                // Try Jumbo (256x256) first, fallback to Extra Large (48x48)
                int hr = SHGetImageList(SHIL_JUMBO, ref iidImageList, out IImageList spImageList);
                if (hr != 0 || spImageList == null)
                {
                    hr = SHGetImageList(SHIL_EXTRALARGE, ref iidImageList, out spImageList);
                }

                if (hr == 0 && spImageList != null)
                {
                    IntPtr hIcon = IntPtr.Zero;
                    if (spImageList.GetIcon(shinfo.iIcon, 1 /* ILD_TRANSPARENT */, out hIcon) == 0 && hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            var imgSource = Imaging.CreateBitmapSourceFromHIcon(
                                hIcon,
                                System.Windows.Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            imgSource.Freeze();
                            return imgSource;
                        }
                        finally
                        {
                            NativeMethods.DestroyIcon(hIcon);
                        }
                    }
                }
            }
        }
        catch { }

        // Fallback: extract associated icon if file exists
        try
        {
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    using var bmp = icon.ToBitmap();
                    IntPtr hBmp = bmp.GetHbitmap();
                    try
                    {
                        var imgSource = Imaging.CreateBitmapSourceFromHBitmap(
                            hBmp, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        imgSource.Freeze();
                        return imgSource;
                    }
                    finally
                    {
                        DeleteObject(hBmp);
                    }
                }
            }
        }
        catch { }

        return null;
    }

}
