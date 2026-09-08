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
        public System.Collections.Generic.List<string> SearchHistory { get; set; } = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.Dictionary<string, int> ExecutionCounts { get; set; } = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
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