using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace SonicSearch
{
    /// <summary>
    /// Reads Chrome/Edge bookmarks straight off disk (their "Bookmarks" file is plain JSON, one
    /// per profile) and merges matches into search results the same way SystemTools merges
    /// built-in Windows tools - no browser extension or running-browser dependency needed.
    /// Firefox isn't covered: its bookmarks live in a places.sqlite database, which would need an
    /// actual SQLite reader dependency rather than the JSON parser already available here.
    /// </summary>
    public static class BrowserBookmarks
    {
        public sealed class Bookmark
        {
            public string Name;
            public string Url;
            /// <summary>Path to the owning browser's exe, used only so the bookmark's icon in
            /// search results is that browser's icon (a real file IconHelper can extract from) -
            /// null if the browser wasn't found where expected.</summary>
            public string BrowserExePath;
        }

        private static readonly object _lock = new object();
        private static List<Bookmark> _cache;

        public static List<Bookmark> All
        {
            get
            {
                lock (_lock)
                {
                    if (_cache == null) _cache = Load();
                    return _cache;
                }
            }
        }

        /// <summary>Re-reads the Bookmarks files from disk - call after the user might have
        /// added/removed bookmarks, since the initial load is cached for the app's lifetime.</summary>
        public static void Reload()
        {
            lock (_lock) { _cache = Load(); }
        }

        public static bool IsUrl(string s) =>
            !string.IsNullOrEmpty(s) &&
            (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        private static List<Bookmark> Load()
        {
            var result = new List<Bookmark>();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            AddChromiumBookmarks(result,
                Path.Combine(localAppData, "Google", "Chrome", "User Data"),
                FirstExisting(
                    Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe")));

            AddChromiumBookmarks(result,
                Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
                FirstExisting(
                    Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")));

            return result;
        }

        private static string FirstExisting(params string[] candidates)
        {
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        private static void AddChromiumBookmarks(List<Bookmark> result, string userDataDir, string browserExe)
        {
            if (!Directory.Exists(userDataDir)) return;

            string[] profileDirs;
            try { profileDirs = Directory.GetDirectories(userDataDir); }
            catch { return; }

            foreach (var profileDir in profileDirs)
            {
                string bookmarksFile = Path.Combine(profileDir, "Bookmarks");
                if (!File.Exists(bookmarksFile)) continue;

                try
                {
                    // The running browser can have this file open/mid-write - copy it first
                    // rather than reading it directly, the standard defensive pattern every
                    // third-party bookmark reader uses for this exact file.
                    string tempCopy = Path.GetTempFileName();
                    string json;
                    try
                    {
                        File.Copy(bookmarksFile, tempCopy, true);
                        json = File.ReadAllText(tempCopy);
                    }
                    finally
                    {
                        try { File.Delete(tempCopy); } catch { }
                    }

                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    if (!(serializer.DeserializeObject(json) is Dictionary<string, object> root)) continue;
                    if (!(root.TryGetValue("roots", out var rootsObj)) || !(rootsObj is Dictionary<string, object> roots)) continue;

                    foreach (var kvp in roots)
                    {
                        if (kvp.Value is Dictionary<string, object> node)
                            WalkNode(node, result, browserExe);
                    }
                }
                catch { }
            }
        }

        private static void WalkNode(Dictionary<string, object> node, List<Bookmark> result, string browserExe)
        {
            if (node.TryGetValue("type", out var typeObj) && (typeObj as string) == "url")
            {
                node.TryGetValue("name", out var nameObj);
                node.TryGetValue("url", out var urlObj);
                string name = nameObj as string;
                string url = urlObj as string;
                if (!string.IsNullOrWhiteSpace(name) && IsUrl(url))
                    result.Add(new Bookmark { Name = name, Url = url, BrowserExePath = browserExe });
                return;
            }

            // JavaScriptSerializer deserializes a JSON array as either ArrayList or object[]
            // depending on context - IEnumerable covers both without needing to know which.
            if (node.TryGetValue("children", out var childrenObj) && childrenObj is IEnumerable children)
            {
                foreach (var child in children)
                {
                    if (child is Dictionary<string, object> childNode)
                        WalkNode(childNode, result, browserExe);
                }
            }
        }
    }
}
