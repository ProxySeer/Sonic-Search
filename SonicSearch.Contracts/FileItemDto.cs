using System;

namespace SonicSearch.Contracts
{
    /// <summary>
    /// Plain-data wire representation of one indexed file/folder, sent from
    /// SonicSearch.Service to the GUI over the named pipe. Deliberately NOT the GUI's own
    /// FileItem class (which carries WPF-specific members like a lazily-loaded ImageSource icon)
    /// - the service has no WPF reference and shouldn't need one just to report a filename.
    /// </summary>
    public class FileItemDto
    {
        public uint NodeIndex { get; set; }
        public string FullName { get; set; }
        public string FileName { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public bool IsDirectory { get; set; }
    }
}
