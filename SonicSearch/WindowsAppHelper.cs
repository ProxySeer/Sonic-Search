using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace SonicSearch
{
    /// <summary>
    /// Handles packaged/Store (MSIX) apps under C:\Program Files\WindowsApps, whose install path
    /// bakes the exact version into the folder name (e.g.
    /// "Claude_1.49585.0.0_x64__pzs8sxrjxfjjc\app\claude.exe") - that folder gets replaced on
    /// every update, so a favorite/search result pointing at the exact exe path silently breaks
    /// the next time the app updates in the background. The fix is to resolve and launch by the
    /// package's AppUserModelId instead (via the real Windows package API, not guessing), which
    /// Windows keeps stable across updates regardless of the underlying path.
    /// </summary>
    public static class WindowsAppHelper
    {
        private static readonly Regex WindowsAppsPathPattern = new Regex(
            @"\\WindowsApps\\(?<name>[^_\\]+)_(?<version>[^_\\]+)_(?<arch>[^_\\]+)__(?<publisherId>[^\\]+)\\",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsWindowsAppsPath(string path) =>
            !string.IsNullOrEmpty(path) && path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Extracts the PackageFamilyName ("Name_publisherId") from a WindowsApps path -
        /// this half of the folder name stays constant across updates, only the version segment
        /// changes.</summary>
        public static string TryGetPackageFamilyName(string path)
        {
            var m = WindowsAppsPathPattern.Match(path ?? "");
            if (!m.Success) return null;
            return m.Groups["name"].Value + "_" + m.Groups["publisherId"].Value;
        }

        /// <summary>Looks up the package's current AppUserModelId and install location via the
        /// real Windows package API (PackageManager.FindPackagesForUser + GetAppListEntries) -
        /// this is exactly what the Start Menu itself uses, so it's always correct regardless of
        /// which version is currently installed. Returns null if the package is no longer
        /// installed at all (not just moved/updated).</summary>
        public static (string AppUserModelId, string InstalledLocationPath) TryResolveCurrentApp(string packageFamilyName)
        {
            if (string.IsNullOrEmpty(packageFamilyName)) return (null, null);

            try
            {
                var packageManager = new PackageManager();
                Package pkg = packageManager.FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
                if (pkg == null) return (null, null);

                var entry = pkg.GetAppListEntries().FirstOrDefault();
                string aumid = entry?.AppUserModelId;
                string installedPath = null;
                try { installedPath = pkg.InstalledLocation?.Path; } catch { /* best-effort */ }

                return (aumid, installedPath);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("WindowsAppHelper: resolving package '" + packageFamilyName + "' failed: " + ex);
                return (null, null);
            }
        }

        /// <summary>Launches a packaged app by AppUserModelId via the shell's AppsFolder alias -
        /// Explorer resolves this to whatever the CURRENT install actually is, unlike a raw path.</summary>
        public static void LaunchByAppUserModelId(string aumid)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = true });
        }

        /// <summary>Given a stale WindowsApps exe path (e.g. from a favorite saved before an app
        /// update) and the package's current install location, rebuilds the equivalent CURRENT
        /// path - e.g. "...\Claude_1.2.0_x64__abc\app\claude.exe" + new install location ->
        /// "...\Claude_1.3.0_x64__abc\app\claude.exe". Used only for icon extraction (falling
        /// back to the generic blank icon is harmless; launching always goes through the AUMID
        /// instead, which doesn't need this at all).</summary>
        public static string TryRebuildCurrentPath(string staleFullPath, string currentInstalledLocationPath)
        {
            if (string.IsNullOrEmpty(staleFullPath) || string.IsNullOrEmpty(currentInstalledLocationPath)) return null;

            var m = WindowsAppsPathPattern.Match(staleFullPath);
            if (!m.Success) return null;

            // Everything after the matched "...\PackageName_Version_Arch__PublisherId\" prefix is
            // the relative path inside the package (e.g. "app\claude.exe") - that part doesn't
            // change between versions, only the package root does.
            string relative = staleFullPath.Substring(m.Index + m.Length);
            try
            {
                string candidate = Path.Combine(currentInstalledLocationPath, relative);
                return File.Exists(candidate) ? candidate : null;
            }
            catch { return null; }
        }
    }
}
