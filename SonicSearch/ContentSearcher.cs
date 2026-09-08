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

        // Comprehensive set of known text / code extensions that are safe and relevant for full text search
        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TXT", "MD", "LOG", "JSON", "XML", "HTML", "HTM", "CSS", "JS", "TS", "JSX", "TSX",
            "CS", "CPP", "C", "H", "HPP", "PY", "JAVA", "GO", "RS", "PHP", "RB", "SH", "BAT", "CMD", "PS1",
            "CONFIG", "INI", "YAML", "YML", "SQL", "CSV", "TSV", "SLN", "CSPROJ", "VBPROJ", "VCXPROJ",
            "GITIGNORE", "ENV", "PROPERTIES", "RC", "RTF", "REG"
        };

        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EXE", "DLL", "SYS", "BIN", "DAT", "ISO", "IMG", "VHD", "VMDK", "OBJ", "PDB", "LIB",
            "ZIP", "RAR", "7Z", "TAR", "GZ", "BZ2", "XZ", "MSI", "CAB",
            "MP4", "MKV", "AVI", "MOV", "WMV", "FLV", "WEBM", "MPEG", "MPG",
            "MP3", "WAV", "FLAC", "AAC", "OGG", "M4A", "WMA",
            "JPG", "JPEG", "PNG", "GIF", "BMP", "ICO", "WEBP", "TIFF", "PSD", "SVG", "AI",
            "PDF", "DOCX", "XLSX", "PPTX", "DOC", "XLS", "PPT"
        };

        // Noise/deep dependency directories to deprioritize/skip in content scanning unless requested
        private static readonly string[] IgnoredFolders = new string[]
        {
            "\\node_modules\\", "\\.git\\", "\\.vs\\", "\\obj\\", "\\bin\\", 
            "\\AppData\\Local\\", "\\AppData\\Roaming\\",
            "\\$Recycle.Bin\\", "\\Windows\\WinSxS\\", "\\Windows\\System32\\", "\\Windows\\SysWOW64\\",
            "\\.nuget\\", "\\.cargo\\", "\\.rustup\\", "\\.gradle\\", "\\.m2\\",
            "\\Program Files\\", "\\Program Files (x86)\\", "\\ProgramData\\"
        };

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
            if (item.Size <= 0 || item.Size > MaxFileSizeBytes) return false;
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
                byte[] pattern = Encoding.UTF8.GetBytes(queryLower);
                if (pattern.Length == 0) return false;

                int[] badCharTable = BuildBadCharTable(pattern);
                int m = pattern.Length;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[BufferSize + m];
                    int bytesRead;
                    int overlap = 0;
                    long streamOffset = 0;

                    while ((bytesRead = stream.Read(buffer, overlap, BufferSize)) > 0)
                    {
                        if (token.IsCancellationRequested) return false;

                        int totalBytes = overlap + bytesRead;

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
                                matchSnippet = ExtractLineSnippet(filePath, matchFileOffset);
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

        private static string ExtractLineSnippet(string filePath, long offset)
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
                    string raw = Encoding.UTF8.GetString(snippetBuf, 0, read);

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
