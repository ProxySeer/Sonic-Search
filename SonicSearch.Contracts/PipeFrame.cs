namespace SonicSearch.Contracts
{
    /// <summary>
    /// One length-prefixed JSON message on the SonicSearchService pipe. After the client sends
    /// a plain "SNAPSHOT:&lt;driveLetter&gt;\n" request line, the server replies with exactly one
    /// frame of Type "snapshot" (Items populated), then keeps the connection open and pushes a
    /// frame of Type "delta" per incremental USN-journal change (Delta populated) until the client
    /// disconnects. Type "error" (Error populated) means the service couldn't service the request
    /// (e.g. USN journal unavailable) - the GUI falls back to its own direct NtfsReader path.
    /// </summary>
    public class PipeFrame
    {
        public string Type { get; set; }
        public FileItemDto[] Items { get; set; }
        public DeltaDto Delta { get; set; }
        public string Error { get; set; }
    }
}
