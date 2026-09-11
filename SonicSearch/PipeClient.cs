using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using SonicSearch.Contracts;

namespace SonicSearch
{
    /// <summary>
    /// GUI-side counterpart to SonicSearch.Service's PipeServer. Connects, requests a snapshot
    /// for one drive, hands it back synchronously, then keeps reading delta frames on a
    /// background thread and raises <see cref="DeltaReceived"/> for each one until told to stop
    /// or the pipe drops (raising <see cref="Disconnected"/> either way) - the caller decides how
    /// to react (MainWindow falls back to a full rescan or the direct NtfsReader path).
    /// </summary>
    public class PipeClient : IDisposable
    {
        private NamedPipeClientStream _pipe;
        private Thread _readThread;
        private volatile bool _stopping;

        public event Action<DeltaDto> DeltaReceived;
        public event Action Disconnected;

        /// <summary>Connects and requests the snapshot; throws on failure (no service running,
        /// timeout, etc.) - caller should catch and fall back to the direct NtfsReader path.</summary>
        public FileItemDto[] ConnectAndGetSnapshot(string driveLetter, int timeoutMs = 5000)
        {
            var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(timeoutMs);

            // The trailing flag tells the service whether to run its USN-journal poll loop at all
            // for this connection - the service has no access to the GUI's AppSettings.json on
            // its own, so without this it always polled regardless of the "Real-time File
            // Watcher" setting, which is why turning that off didn't actually stop the ongoing
            // background CPU/disk work while running in service mode.
            bool realtime = AppSettings.Instance.EnableRealtimeWatcher;
            PipeProtocol.WriteRequestLine(pipe, "SNAPSHOT:" + driveLetter + " " + (realtime ? "1" : "0"));

            var result = PipeProtocol.ReadAnyFrame(pipe);
            if (result == null) throw new InvalidOperationException("Indexing service closed the connection before replying.");
            if (result.JsonFrame?.Type == "error") throw new InvalidOperationException("Indexing service error: " + result.JsonFrame.Error);
            if (result.SnapshotItems == null)
                throw new InvalidOperationException("Unexpected response from indexing service.");

            _pipe = pipe;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();

            return result.SnapshotItems;
        }

        private void ReadLoop()
        {
            try
            {
                while (!_stopping)
                {
                    PipeProtocol.ReadResult result;
                    try
                    {
                        result = PipeProtocol.ReadAnyFrame(_pipe);
                    }
                    catch
                    {
                        break;
                    }
                    if (result == null) break;
                    if (result.JsonFrame?.Type == "delta" && result.JsonFrame.Delta != null)
                        DeltaReceived?.Invoke(result.JsonFrame.Delta);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("PipeClient read loop failed: " + ex);
            }
            finally
            {
                if (!_stopping) Disconnected?.Invoke();
            }
        }

        public void Dispose()
        {
            _stopping = true;
            try { _pipe?.Dispose(); } catch { }
        }
    }
}
