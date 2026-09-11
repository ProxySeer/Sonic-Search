 using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SonicSearch
{
    /// <summary>
    /// High-performance full-text content searcher using Boyer-Moore byte matching on streaming buffers.
    /// Handles UTF-8 / ASCII text search with line snippet extraction and multi-threaded parallel scanning.
    /// </summary>
    public static class ContentSearcher
    {
        private const int BufferSize = 131072; // 128 KB streaming buffer for high-speed sequential disk I/O
        private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB max file size limit per file

        // Built-in defaults, used whenever the corresponding AppSettings string is blank. Kept as
        // the fallback (rather than deleted) so existing settings.json files with none of the
        // three ContentSearch* fields set behave exactly as before this became configurable.
        private static readonly HashSet<string> DefaultTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TXT", "MD", "LOG", "JSON", "XML", "HTML", "HTM", "CSS", "JS", "TS", "JSX", "TSX",
            "CS", "CPP", "C", "H", "HPP", "PY", "JAVA", "GO", "RS", "PHP", "RB", "SH", "BAT", "CMD", "PS1",
            "CONFIG", "INI", "YAML", "YML", "SQL", "CSV", "TSV", "SLN", "CSPROJ", "VBPROJ", "VCXPROJ",
            "GITIGNORE", "ENV", "PROPERTIES", "RC", "RTF", "REG"
        };

        private static readonly HashSet<string> DefaultBinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EXE", "DLL", "SYS", "BIN", "DAT", "ISO", "IMG", "VHD", "VMDK", "OBJ", "PDB", "LIB",
            "ZIP", "RAR", "7Z", "TAR", "GZ", "BZ2", "XZ", "MSI", "CAB",
            "MP4", "MKV", "AVI", "MOV", "WMV", "FLV", "WEBM", "MPEG", "MPG",
            "MP3", "WAV", "FLAC", "AAC", "OGG", "M4A", "WMA",
            "JPG", "JPEG", "PNG", "GIF", "BMP", "ICO", "WEBP", "TIFF", "PSD", "SVG", "AI",
            "PDF", "DOCX", "XLSX", "PPTX", "DOC", "XLS", "PPT"
        };

        private static readonly string[] DefaultIgnoredFolders = new string[]
        {
            "\\node_modules\\", "\\.git\\", "\\.vs\\", "\\obj\\", "\\bin\\",
            "\\AppData\\Local\\", "\\AppData\\Roaming\\",
            "\\$Recycle.Bin\\", "\\Windows\\WinSxS\\", "\\Windows\\System32\\", "\\Windows\\SysWOW64\\",
            "\\.nuget\\", "\\.cargo\\", "\\.rustup\\", "\\.gradle\\", "\\.m2\\",
            "\\Program Files\\", "\\Program Files (x86)\\", "\\ProgramData\\"
        };

        // User-overridable versions, populated by ApplySettings() from AppSettings.Instance - null
        // (not just empty) means "not customized, use the Default* set above".
        private static HashSet<string> _textExtensions;
        private static HashSet<string> _binaryExtensions;
        private static string[] _ignoredFolders;

        private static HashSet<string> TextExtensions => _textExtensions ?? DefaultTextExtensions;
        private static HashSet<string> BinaryExtensions => _binaryExtensions ?? DefaultBinaryExtensions;
        private static string[] IgnoredFolders => _ignoredFolders ?? DefaultIgnoredFolders;

        /// <summary>
        /// Re-reads the three ContentSearch* settings from AppSettings.Instance. Call once at
        /// startup (App.xaml.cs) and again whenever Settings is saved (OptionsWindow), so a
        /// change takes effect on the very next content: search without restarting the app.
        /// </summary>
        public static void ApplySettings()
        {
            _textExtensions = ParseExtensionList(AppSettings.Instance.ContentSearchTextExtensions);
            _binaryExtensions = ParseExtensionList(AppSettings.Instance.ContentSearchBinaryExtensions);
            _ignoredFolders = ParseFolderList(AppSettings.Instance.ContentSearchIgnoredFolders);
        }

        private static HashSet<string> ParseExtensionList(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string ext = raw.Trim().TrimStart('.');
                if (ext.Length > 0) set.Add(ext);
            }
            return set.Count > 0 ? set : null;
        }

        private static string[] ParseFolderList(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return null;
            var list = new List<string>();
            foreach (var raw in csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim().Trim('\\');
                if (name.Length > 0) list.Add("\\" + name + "\\");
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static int[] BuildBadCharTable(byte[] pattern)
        {
            int[] table = new int[256];
            int m = pattern.Length;
            for (int i = 0; i < 256; i++)
            {
                table[i] = m;
            }
            for (int i = 0; i < m - 1; i++)
            {
                table[pattern[i]] = m - 1 - i;
            }
            return table;
        }

        public static bool IsSearchableFile(FileItem item)
        {
            if (item == null || item.IsDirectory) return false;
            // Only item.Size (the INDEX's cached size, refreshed by USN/FSW updates - not
            // necessarily current to the second) gates candidacy here, and SearchFile below
            // already does its own authoritative FileInfo.Length check against the real file
            // before ever opening it - so this only needs to reject files the cache says are
            // way too large (skip opening something huge as a fast pre-filter). Also rejecting
            // Size <= 0 used to mean any file whose cached size hadn't caught up yet with a very
            // recent edit (freshly created, then immediately typed into and saved - exactly a
            // "content:X location:Y" test right after creating a test file) was silently excluded
            // from content search entirely, even though it clearly existed and had content - the
            // name/location search found it fine (it doesn't check Size at all), which is why
            // that worked while content: didn't.
            if (item.Size > MaxFileSizeBytes) return false;
            if (BinaryExtensions.Contains(item.Extension)) return false;
            
            // Prioritize text extensions, or allow files without extension / unknown extension under 2MB
            if (TextExtensions.Contains(item.Extension)) return true;
            return false;
        }

        public static bool IsIgnoredPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            for (int i = 0; i < IgnoredFolders.Length; i++)
            {
                if (fullPath.IndexOf(IgnoredFolders[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public static bool SearchFile(string filePath, string query, CancellationToken token, out string matchSnippet)
        {
            matchSnippet = null;
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(query)) return false;

            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists || fileInfo.Length == 0 || fileInfo.Length > MaxFileSizeBytes)
                    return false;

                string queryLower = query.ToLowerInvariant();

                // Notepad (and plenty of other editors) happily save plain .txt files as UTF-16
                // with a BOM rather than UTF-8/ASCII - each Latin character then takes 2 bytes
                // ('h' = 0x68 0x00), which a UTF-8-encoded search pattern can never match via raw
                // byte comparison. Sniff the file's own BOM and build the pattern in that same
                // encoding instead of always assuming UTF-8/ASCII.
                Encoding fileEncoding = Encoding.UTF8;
                int bomLength = 0;
                using (var bomStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] bom = new byte[4];
                    int bomRead = bomStream.Read(bom, 0, 4);
                    if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                    {
                        fileEncoding = Encoding.UTF8; bomLength = 3;
                    }
                    else if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                    {
                        fileEncoding = Encoding.Unicode; bomLength = 2; // UTF-16 LE
                    }
                    else if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                    {
                        fileEncoding = Encoding.BigEndianUnicode; bomLength = 2; // UTF-16 BE
                    }
                }

                byte[] pattern = fileEncoding.GetBytes(queryLower);
                if (pattern.Length == 0) return false;

                int[] badCharTable = BuildBadCharTable(pattern);
                int m = pattern.Length;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan))
                {
                    if (bomLength > 0) stream.Seek(bomLength, SeekOrigin.Begin);

                    byte[] buffer = new byte[BufferSize + m];
                    int bytesRead;
                    int overlap = 0;
                    long streamOffset = bomLength;

                    while ((bytesRead = stream.Read(buffer, overlap, BufferSize)) > 0)
                    {
                        if (token.IsCancellationRequested) return false;

                        int totalBytes = overlap + bytesRead;

                        // A-Z -> a-z case folding on raw bytes. Safe for UTF-16 LE/BE Latin text
                        // too: the ASCII letter byte still falls in 0x41-0x5A at its own byte
                        // position, and the paired 0x00 high/low byte never does, so it's left
                        // untouched either way - no need to special-case the encoding here.
                        for (int i = overlap; i < totalBytes; i++)
                        {
                            byte b = buffer[i];
                            if (b >= 0x41 && b <= 0x5A) // 'A'-'Z' -> 'a'-'z'
                            {
                                buffer[i] = (byte)(b + 0x20);
                            }
                        }

                        int s = 0;
                        while (s <= totalBytes - m)
                        {
                            int j = m - 1;
                            while (j >= 0 && pattern[j] == buffer[s + j])
                            {
                                j--;
                            }

                            if (j < 0)
                            {
                                long matchFileOffset = streamOffset + s;
                                matchSnippet = ExtractLineSnippet(filePath, matchFileOffset, fileEncoding);
                                return true;
                            }
                            else
                            {
                                byte mismatched = buffer[s + j];
                                s += Math.Max(1, badCharTable[mismatched]);
                            }
                        }

                        overlap = Math.Min(m - 1, totalBytes);
                        Array.Copy(buffer, totalBytes - overlap, buffer, 0, overlap);
                        streamOffset += bytesRead;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static string ExtractLineSnippet(string filePath, long offset, Encoding fileEncoding)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long startPos = Math.Max(0, offset - 80);
                    int length = (int)Math.Min(250, fs.Length - startPos);
                    fs.Seek(startPos, SeekOrigin.Begin);

                    byte[] snippetBuf = new byte[length];
                    int read = fs.Read(snippetBuf, 0, length);
                    string raw = fileEncoding.GetString(snippetBuf, 0, read);

                    raw = raw.Replace("\r\n", " ").Replace("\n", " ").Replace("\t", " ").Trim();
                    if (raw.Length > 150)
                    {
                        raw = raw.Substring(0, 150) + "...";
                    }
                    return raw;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
