using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public static class IconHelper
{
    private static readonly Dictionary<string, int> smallIconMap = new();
    private static readonly Dictionary<string, int> largeIconMap = new();

    public static ImageList ImageListSmall { get; set; }
    public static ImageList ImageListLarge { get; set; }

    // --- Dedicated STA thread for COM-based icon extraction --------------------------------
    // All the icon calls in this file (Task.Run(() => IconHelper.GetJumboIconForPath(...)) in
    // MainWindow) run on ThreadPool threads, which default to the MTA apartment. Plain
    // SHGetFileInfo lookups against the shared system icon cache tolerate that fine, but a file
    // type registered with its own ShellEx\IconHandler COM object (VisualStudio.Launcher.slnx's
    // "VSPackage icon" handler is exactly this - confirmed via the registry: HKLM\Software\
    // Classes\VisualStudio.Launcher.slnx\ShellEx\IconHandler) is a classic single-threaded-
    // apartment COM component. Calling into it from an MTA thread silently fails - the shell
    // swallows the error and falls back to a generic icon - rather than throwing something we
    // could catch and retry. Routing just the .lnk-resolution path (the one place we found this
    // actually biting) through one persistent STA thread fixes it without paying the cost of
    // serializing every icon lookup in the app through a single thread.
    private static readonly BlockingCollection<Action> _staQueue = new BlockingCollection<Action>();
    private static Thread _staThread;
    private static readonly object _staInitLock = new object();

    private static void EnsureStaThread()
    {
        if (_staThread != null) return;
        lock (_staInitLock)
        {
            if (_staThread != null) return;
            var t = new Thread(() =>
            {
                foreach (var action in _staQueue.GetConsumingEnumerable())
                {
                    try { action(); } catch { }
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _staThread = t;
        }
    }

    private static T RunOnSta<T>(Func<T> func)
    {
        EnsureStaThread();
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
        _staQueue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    // .sln/.slnx are registered with their own STA-only ShellEx\IconHandler COM object
    // (VSFileHandler_64.dll, ThreadingModel=Apartment - confirmed via the registry) - the exact
    // same class of problem the .lnk-resolution comment above describes, just hit directly
    // instead of through a shortcut. Every icon lookup in this file runs on a ThreadPool (MTA)
    // thread by default (see the Task.Run(() => IconHelper...) call sites in MainWindow), and
    // calling an STA-only COM handler from there fails silently - the shell swallows the error
    // and falls back to the generic blank-document icon rather than throwing anything we could
    // catch. Routing just these known extensions through the dedicated STA thread fixes it
    // without paying the cost of serializing every icon lookup in the app through one thread.
    private static bool NeedsStaIconHandler(string ext) =>
        ext == ".sln" || ext == ".slnx";

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    // --- .lnk resolution -----------------------------------------------------------------
    // SHGetFileInfo asks the shell to resolve a shortcut's icon for us, but that resolution can
    // fail silently (returning the blank generic icon) when the icon location baked into the
    // .lnk points at something the shell's icon cache doesn't have handy - e.g. a shortcut to a
    // data file (like a .slnx) with no custom IconLocation set, where Explorer falls back to
    // asking the file-type association for an icon and that lookup comes up empty for an
    // extension with no registered icon of its own. Reading the .lnk directly via IShellLink and
    // extracting from whatever it actually points at (its own IconLocation, or failing that the
    // resolved target file) is more reliable than hoping SHGetFileInfo's implicit resolution works.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription(string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory(string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments(string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation(string pszIconPath, int iIcon);
        void SetRelativePath(string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath(string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

    /// <summary>
    /// Resolves a .lnk to (iconPath, iconIndex) it should be drawn from - either the shortcut's
    /// own explicit IconLocation, or, if that's blank (the common case), its resolved target -
    /// falling back to the target for a data-file shortcut with no custom icon set.
    /// </summary>
    private static (string path, int index, bool isExplicitIconResource) ResolveLnkIconSource(string lnkPath)
    {
        IShellLinkW link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);

            var iconPathSb = new System.Text.StringBuilder(260);
            link.GetIconLocation(iconPathSb, iconPathSb.Capacity, out int iconIndex);
            string iconPath = iconPathSb.ToString();
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                return (iconPath, iconIndex, true);

            var targetSb = new System.Text.StringBuilder(260);
            link.GetPath(targetSb, targetSb.Capacity, IntPtr.Zero, 0);
            string target = targetSb.ToString();
            if (!string.IsNullOrWhiteSpace(target) && (File.Exists(target) || Directory.Exists(target)))
                return (target, 0, false);
        }
        catch { }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
        }
        return (null, 0, false);
    }

    /// <summary>
    /// Extracts an icon by explicitly resolving the .lnk's real icon source first (see
    /// ResolveLnkIconSource) rather than trusting SHGetFileInfo's own shortcut resolution, which
    /// can silently fall back to a blank generic icon for shortcuts to file types with no
    /// registered icon of their own (e.g. a .slnx). Returns null to let callers fall back to
    /// their normal SHGetFileInfo path if this doesn't turn up anything better.
    /// </summary>
    private static Image GetLnkIcon(string lnkPath, bool large)
    {
        return RunOnSta(() => GetLnkIconCore(lnkPath, large));
    }

    private static Image GetLnkIconCore(string lnkPath, bool large)
    {
        var (sourcePath, iconIndex, isExplicitIconResource) = ResolveLnkIconSource(lnkPath);
        if (sourcePath == null) return null;

        if (!isExplicitIconResource && Directory.Exists(sourcePath))
            return GetShellFolderIcon(large ? SHGFI_LARGEICON : SHGFI_SMALLICON);

        if (!isExplicitIconResource)
        {
            // The shortcut has no explicit IconLocation of its own - we resolved its target
            // instead, which could be any kind of file (not just one with an embedded icon
            // resource like an exe/dll/ico). ExtractIconEx only reads embedded resources, so for
            // an arbitrary data file (a .slnx, a .txt, ...) it comes back empty; go through the
            // normal shell-association icon lookup instead, which is what actually resolves "this
            // extension's registered icon" (or the generic document icon if none is registered -
            // the same icon Explorer itself would show).
            return GetShellIcon(sourcePath, false, large ? SHGFI_LARGEICON : SHGFI_SMALLICON, useRealFile: true);
        }

        var largeHandles = new IntPtr[1];
        var smallHandles = new IntPtr[1];
        try
        {
            uint extracted = ExtractIconEx(sourcePath, iconIndex, largeHandles, smallHandles, 1);
            if (extracted == 0) return null;

            IntPtr hIcon = large ? largeHandles[0] : smallHandles[0];
            IntPtr other = large ? smallHandles[0] : largeHandles[0];
            if (other != IntPtr.Zero) NativeMethods.DestroyIcon(other);
            if (hIcon == IntPtr.Zero) return null;

            using Icon icon = Icon.FromHandle(hIcon);
            Image img = icon.ToBitmap();
            NativeMethods.DestroyIcon(hIcon);
            return img;
        }
        catch { return null; }
    }

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

        // See NeedsStaIconHandler.
        if (NeedsStaIconHandler(Path.GetExtension(filePath)?.ToLowerInvariant() ?? ""))
            return RunOnSta(() => GetIconIndexCore(filePath, small));

        return GetIconIndexCore(filePath, small);
    }

    private static int GetIconIndexCore(string filePath, bool small)
    {
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
            // For .exe/.ico/.lnk, read the icon off the real file (its actual embedded resource,
            // or - for .lnk - the shortcut's target icon) rather than the generic per-extension
            // placeholder; see GetShellIcon's useRealFile parameter for why that distinction
            // requires dropping SHGFI_USEFILEATTRIBUTES.
            bool useRealFile = uniqueIcon && File.Exists(filePath);
            icon = (ext == ".lnk" && useRealFile) ? GetLnkIcon(filePath, !small) : null;
            if (icon == null)
            {
                icon = small
                    ? GetShellIcon(filePath, false, SHGFI_SMALLICON, useRealFile)
                    : GetShellIcon(filePath, false, SHGFI_LARGEICON, useRealFile);
            }
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
    /// <param name="useRealFile">
    /// When true, omits SHGFI_USEFILEATTRIBUTES so the shell reads the icon off the actual file
    /// on disk instead of guessing a generic icon from the extension alone. SHGFI_USEFILEATTRIBUTES
    /// exists so callers can ask "what icon would a file of this type get" without the file
    /// necessarily existing - fast and fine for most extensions, since they all share one static
    /// icon anyway. But .exe/.lnk/.ico each carry their OWN per-file icon (an exe's embedded
    /// resource, a shortcut's target icon, an ico's own image) - passing USEFILEATTRIBUTES for
    /// those short-circuits all of that and returns the same blank generic icon for every one of
    /// them, which is exactly the "lnk/exe icons don't extract" bug.
    /// </param>
    private static Image GetShellIcon(string path, bool isFolder, uint sizeFlag, bool useRealFile = false)
    {
        SHFILEINFO shinfo = new();
        uint flags = SHGFI_ICON | sizeFlag | (useRealFile ? 0 : SHGFI_USEFILEATTRIBUTES);
        uint attr = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        IntPtr hImg = SHGetFileInfo(path, useRealFile ? 0 : attr, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);

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
        path = ResolveIfStaleWindowsAppsPath(path);

        // See NeedsStaIconHandler.
        if (NeedsStaIconHandler(Path.GetExtension(path)?.ToLowerInvariant() ?? ""))
            return RunOnSta(() => GetSmallIconForPathCore(path));

        return GetSmallIconForPathCore(path);
    }

    private static ImageSource GetSmallIconForPathCore(string path)
    {
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
            // Request the LARGE (32x32) shell icon here, not SHGFI_SMALLICON's 16x16, even though
            // the list row only displays this at ~20x20 - WPF then downscales it with
            // BitmapScalingMode="HighQuality" (set on the Image in MainWindow.xaml), and
            // downscaling a bigger source always looks sharp, while upscaling a 16px icon to 20px
            // (what SHGFI_SMALLICON gave before) blurs it. This is the "small icons quality bad"
            // fix - the row size never changed, only which native icon size backs it.
            Image img;
            if (isFolder)
            {
                img = GetShellFolderIcon(SHGFI_LARGEICON);
            }
            else
            {
                bool useRealFile = uniqueIcon && File.Exists(path);
                img = (ext == ".lnk" && useRealFile) ? GetLnkIcon(path, true) : null;
                if (img == null)
                    img = GetShellIcon(path, false, SHGFI_LARGEICON, useRealFile);
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

    /// <summary>A favorited/indexed WindowsApps exe path (e.g. a packaged Store app like Claude)
    /// bakes its exact version into the folder name and stops existing the moment that app
    /// auto-updates - re-resolve to wherever the SAME package currently lives (see
    /// WindowsAppHelper) so its icon still extracts correctly instead of falling back to the
    /// generic blank one. A no-op for every other path, and for a WindowsApps path that still
    /// exists as-is (the common case, most of the time between updates).</summary>
    private static string ResolveIfStaleWindowsAppsPath(string path)
    {
        if (File.Exists(path) || !SonicSearch.WindowsAppHelper.IsWindowsAppsPath(path)) return path;

        string familyName = SonicSearch.WindowsAppHelper.TryGetPackageFamilyName(path);
        var (_, installedLocation) = SonicSearch.WindowsAppHelper.TryResolveCurrentApp(familyName);
        string current = SonicSearch.WindowsAppHelper.TryRebuildCurrentPath(path, installedLocation);
        return current ?? path;
    }

    public static ImageSource GetJumboIconForPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        path = ResolveIfStaleWindowsAppsPath(path);

        // See NeedsStaIconHandler - a direct .sln/.slnx (not reached through the .lnk branch
        // below, which handles STA itself via GetLnkIcon) needs the whole lookup run on the STA
        // thread or its icon handler silently fails and we get the generic blank icon back.
        if (NeedsStaIconHandler(Path.GetExtension(path)?.ToLowerInvariant() ?? ""))
            return RunOnSta(() => GetJumboIconForPathCore(path));

        return GetJumboIconForPathCore(path);
    }

    private static ImageSource GetJumboIconForPathCore(string path)
    {
        // For .lnk, resolve explicitly before asking the shell's system image list - that list
        // is keyed by the same implicit shortcut-icon resolution that silently falls back to a
        // blank generic icon for shortcuts to file types with no icon of their own registered
        // (e.g. a .slnx), and it would "succeed" (return a valid, just wrong, icon index) rather
        // than throwing, so nothing below would ever notice and fall back on its own.
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            try
            {
                Image lnkImg = GetLnkIcon(path, true);
                if (lnkImg is Bitmap lnkBmp)
                {
                    IntPtr hBmp = lnkBmp.GetHbitmap();
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(
                            hBmp, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                    finally
                    {
                        DeleteObject(hBmp);
                        lnkBmp.Dispose();
                    }
                }
            }
            catch { }
        }

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

        // Fallback 1: direct SHGetFileInfo large icon (32x32, upgraded to 48x48 when the shell
        // has it cached). More reliable than the jumbo system-image-list lookup above for file
        // types that don't participate cleanly in that cache - .msc MMC snap-ins in particular
        // (Device Manager, Services, etc.) consistently fail the jumbo path and would otherwise
        // fall through to the generic "blank document" icon from ExtractAssociatedIcon below.
        try
        {
            bool isFolder = Directory.Exists(path) || path.EndsWith(@"\") || path.EndsWith("/");
            string ext0 = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            bool uniqueIcon0 = ext0 == ".exe" || ext0 == ".ico" || ext0 == ".lnk";
            bool useRealFile0 = !isFolder && uniqueIcon0 && File.Exists(path);
            Image img = isFolder ? GetShellFolderIcon(SHGFI_LARGEICON) : GetShellIcon(path, false, SHGFI_LARGEICON, useRealFile0);
            if (img is Bitmap bmp0)
            {
                IntPtr hBitmap = bmp0.GetHbitmap();
                try
                {
                    var imgSource = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    imgSource.Freeze();
                    return imgSource;
                }
                finally
                {
                    DeleteObject(hBitmap);
                    bmp0.Dispose();
                }
            }
        }
        catch { }

        // Fallback 2: extract associated icon if file exists
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
