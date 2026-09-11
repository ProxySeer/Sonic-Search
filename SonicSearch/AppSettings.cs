using System;
using System.IO;
using System.Web.Script.Serialization;

namespace SonicSearch
{
    public class AppSettings
    {
        public int MaxResults { get; set; } = 50;
        public string ExcludedExtensions { get; set; } = ".dll, .sys, .cache, .tmp";
        public string PrioritizedExtensions { get; set; } = ".exe, .sln, .md, .txt";
        public int DebounceMs { get; set; } = 120;
        public bool EnableRealtimeWatcher { get; set; } = true;
        public string MonitoredFolders { get; set; } = "";
        public string IncludedIndexFolders { get; set; } = ""; // Empty = entire drive, or specific paths
        public string ExcludedIndexFolders { get; set; } = "$Recycle.Bin, System Volume Information"; // Folders to prune from index
        public int AutoReindexMinutes { get; set; } = 30; // 0 = disabled, e.g. 30 min
        public string SearchMode { get; set; } = "Contains"; // "Contains", "Exact", "StartsWith", "Regex"
        public string HotkeyModifiers { get; set; } = "Ctrl";
        public string HotkeyKey { get; set; } = "S";
        public bool QuickStartFirstResult { get; set; } = true;
        public bool HideAfterOpen { get; set; } = false;
        /// <summary>When true, the app runs unelevated and gets its file index from
        /// SonicSearchService over a named pipe instead of reading the MFT/USN journal itself -
        /// see ServiceInstallHelper.cs and PipeClient.cs. Off by default: it requires installing
        /// a service (one elevated action, done from Settings), so it's strictly opt-in.</summary>
        public bool UseIndexingService { get; set; } = false;

        /// <summary>Drive letters (e.g. "C", "D") to index and search across, in addition to C.
        /// Null/empty means just C - see MainWindow.GetConfiguredDrives, which is the only place
        /// this is actually read from.</summary>
        public System.Collections.Generic.List<string> IndexedDrives { get; set; } = new System.Collections.Generic.List<string> { "C" };

        // Content-search (content:) tuning - previously hardcoded HashSets in ContentSearcher.cs.
        // Empty string means "use ContentSearcher's built-in defaults"; ContentSearcher.ApplySettings
        // falls back to those whenever the corresponding setting here is blank, so an existing
        // settings.json with none of these set behaves identically to before this was added.
        public string ContentSearchTextExtensions { get; set; } = "";
        public string ContentSearchBinaryExtensions { get; set; } = "";
        public string ContentSearchIgnoredFolders { get; set; } = "";
        public System.Collections.Generic.List<string> SearchHistory { get; set; } = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.Dictionary<string, int> ExecutionCounts { get; set; } = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Obsolete flat favorites list, kept only so <see cref="Load"/> can migrate
        /// it into <see cref="FavoriteGroups"/> once. Do not use for new code.</summary>
        public System.Collections.Generic.List<string> FavoritePaths { get; set; } = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<FavoriteGroup> FavoriteGroups { get; set; } = new System.Collections.Generic.List<FavoriteGroup>();

        public static AppSettings Instance { get; set; } = new AppSettings();

        private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static void Load()
        {
            try {
                if (File.Exists(ConfigPath)) {
                    string json = File.ReadAllText(ConfigPath);
                    var js = new JavaScriptSerializer();
                    var loaded = js.Deserialize<AppSettings>(json);
                    if (loaded != null) Instance = loaded;
                }
            } catch { }

            MigrateFavorites();
        }

        /// <summary>
        /// One-time migration from the old flat FavoritePaths list to FavoriteGroups, and a
        /// safety net that guarantees at least one group always exists so the Favorites view
        /// always has a tab to show.
        /// </summary>
        private static void MigrateFavorites()
        {
            if (Instance.FavoriteGroups == null)
                Instance.FavoriteGroups = new System.Collections.Generic.List<FavoriteGroup>();

            if (Instance.FavoriteGroups.Count == 0 && Instance.FavoritePaths != null && Instance.FavoritePaths.Count > 0)
            {
                Instance.FavoriteGroups.Add(new FavoriteGroup
                {
                    Name = "Favorites",
                    ItemPaths = new System.Collections.Generic.List<string>(Instance.FavoritePaths)
                });
                Instance.FavoritePaths.Clear();
                Save();
            }

            if (Instance.FavoriteGroups.Count == 0)
                Instance.FavoriteGroups.Add(new FavoriteGroup { Name = "Favorites" });
        }
        
        public static void Save()
        {
            try {
                var js = new JavaScriptSerializer();
                string json = js.Serialize(Instance);
                File.WriteAllText(ConfigPath, json);
            } catch { }
        }
    }
}