using System;

namespace SonicSearch.Contracts
{
    /// <summary>
    /// One incremental change to the index, resolved by the service (via the USN journal) and
    /// pushed to the GUI. Mirrors exactly what MainWindow.xaml.cs's ApplyUsnChange used to resolve
    /// locally before this feature existed - the service does the same resolution work (turning a
    /// raw USN journal record into "here's the current name/size/date, or here's what to remove"),
    /// the GUI just applies it to its in-memory list instead of re-deriving it.
    /// </summary>
    public class DeltaDto
    {
        /// <summary>Which drive this change came from (e.g. "C") - NodeIndex alone is only
        /// unique WITHIN one volume's MFT, so a GUI indexing multiple drives at once needs this
        /// to avoid matching a change against an unrelated file that happens to share the same
        /// NodeIndex on a different drive.</summary>
        public string DriveLetter { get; set; }
        public uint NodeIndex { get; set; }
        public bool IsRemoval { get; set; }
        public string FullName { get; set; }
        public string FileName { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public bool IsDirectory { get; set; }
    }
}
