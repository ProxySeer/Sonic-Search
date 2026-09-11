using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Filesystem.Ntfs;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using SonicSearch.Contracts;

namespace SonicSearch.Service
{
    /// <summary>
    /// Hosts the named pipe SonicSearch's GUI connects to instead of reading the MFT/USN journal
    /// itself. One background thread continuously creates a new pipe server instance and waits
    /// for a connection; each accepted connection is handled on its own thread so more than one
    /// GUI (or the same GUI reconnecting after a restart) can be served independently. Each
    /// connection gets its OWN NtfsReader scan and its OWN UsnJournal poll loop - simpler and more
    /// robust than sharing one journal across clients and broadcasting, at the cost of duplicating
    /// the (cheap, this machine already does it fine today in-process) scan work if two GUIs were
    /// ever connected at once, which in practice is never.
    /// </summary>
    public class PipeServer
    {
        private volatile bool _stopping;
        private Thread _acceptThread;
        private readonly List<Thread> _connectionThreads = new List<Thread>();

        // Diagnostic log - the AcceptLoop's catch block previously swallowed whatever exception
        // CreateServerStream()/WaitForConnection() threw with no record of it at all, which is
        // why repeated "pipe connect fails, service shows Running" reports couldn't be pinned
        // down past speculation. ProgramData is world-readable by default, so this is readable
        // without elevation even though the service itself runs as LocalSystem.
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SonicSearch", "Service", "service.log");

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        public void Start()
        {
            _stopping = false;
            Log("=== PipeServer.Start() ===");
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            // Nudge the accept loop off its blocking WaitForConnection by connecting to ourselves.
            try
            {
                using (var nudge = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut))
                {
                    nudge.Connect(200);
                }
            }
            catch { }
        }

        private void AcceptLoop()
        {
            Log("AcceptLoop started, thread " + Thread.CurrentThread.ManagedThreadId);
            int iteration = 0;
            while (!_stopping)
            {
                iteration++;
                NamedPipeServerStream server;
                try
                {
                    Log("[" + iteration + "] Creating server stream...");
                    server = CreateServerStream();
                    Log("[" + iteration + "] Waiting for connection...");
                    server.WaitForConnection();
                    Log("[" + iteration + "] Connection accepted.");
                }
                catch (Exception ex)
                {
                    Log("[" + iteration + "] EXCEPTION in create/wait: " + ex);
                    if (_stopping) return;
                    Thread.Sleep(500);
                    continue;
                }

                if (_stopping)
                {
                    server.Dispose();
                    return;
                }

                var thread = new Thread(() => HandleConnection(server)) { IsBackground = true };
                lock (_connectionThreads) _connectionThreads.Add(thread);
                thread.Start();
            }
            Log("AcceptLoop exiting (_stopping=true)");
        }

        private static NamedPipeServerStream CreateServerStream()
        {
            // Everyone can connect and read/write - this pipe carries file names/sizes/dates
            // only (no file contents, no credentials), and any process on the box can already
            // enumerate the same information by asking the shell, so there's nothing to protect
            // by restricting it to a narrower ACL.
            //
            // FullControl (not just ReadWrite) is required here, not because clients need those
            // extra rights, but because creating each SUBSequent instance of a multi-instance
            // pipe re-applies this security descriptor, and Windows requires WRITE_DAC on the
            // existing pipe to do that. ReadWrite alone doesn't grant WRITE_DAC, so instance #1
            // succeeds but every instance after it fails with UnauthorizedAccessException
            // ("Access to the path is denied") - which is exactly the bug that made the service
            // accept exactly one connection ever and then permanently fail to accept another.
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            return new NamedPipeServerStream(
                PipeProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }

        private void HandleConnection(NamedPipeServerStream pipe)
        {
            Log("HandleConnection started, thread " + Thread.CurrentThread.ManagedThreadId);
            try
            {
                string request = PipeProtocol.ReadRequestLine(pipe);
                Log("Request line: " + (request ?? "(null)"));
                if (request == null || !request.StartsWith("SNAPSHOT:", StringComparison.OrdinalIgnoreCase))
                    return;

                // Format: "SNAPSHOT:<drive> <realtime:0|1>" - the trailing flag is optional (older
                // clients omit it) and defaults to enabled, matching the pre-existing behavior of
                // always polling. This is how the GUI's "Real-time File Watcher" setting reaches
                // the service at all - the service has no access to the GUI's AppSettings.json by
                // itself, and its poll loop previously ran unconditionally regardless of that
                // setting being off, which was the actual source of ongoing background CPU/disk
                // work (and fan noise) while service mode was connected but the user believed
                // real-time monitoring was disabled.
                string body = request.Substring("SNAPSHOT:".Length).Trim();
                bool realtimeRequested = true;
                int sp = body.IndexOf(' ');
                string driveLetter = (sp >= 0 ? body.Substring(0, sp) : body).TrimEnd(':');
                if (sp >= 0)
                {
                    string flag = body.Substring(sp + 1).Trim();
                    realtimeRequested = flag != "0";
                }
                if (driveLetter.Length == 0) return;

                UsnJournal journal;
                long lastUsn;
                try
                {
                    var items = ScanDrive(driveLetter);
                    PipeProtocol.WriteSnapshotFrame(pipe, items);

                    if (realtimeRequested)
                    {
                        journal = new UsnJournal(new DriveInfo(driveLetter));
                        lastUsn = journal.CurrentUsn;
                    }
                    else
                    {
                        journal = null;
                        lastUsn = 0;
                    }
                }
                catch (Exception ex)
                {
                    Log("Scan/journal setup EXCEPTION for drive " + driveLetter + ": " + ex);
                    try { PipeProtocol.WriteJsonFrame(pipe, new PipeFrame { Type = "error", Error = ex.Message }); } catch { }
                    return;
                }

                if (!realtimeRequested)
                {
                    // No journal was even opened - just hold the connection open cheaply (a
                    // periodic wake to check IsConnected, no USN polling/disk work at all) until
                    // the GUI disconnects. This is the actual fix for "fans spin while connected,
                    // stop when the app closes, even with the watcher setting off" - the service
                    // previously had no way to know that setting and always polled regardless.
                    while (!_stopping && pipe.IsConnected)
                        Thread.Sleep(1000);
                    return;
                }

                using (journal)
                {
                    int consecutiveFailures = 0;
                    while (!_stopping && pipe.IsConnected)
                    {
                        Thread.Sleep(3000);
                        if (_stopping || !pipe.IsConnected) break;

                        List<UsnChange> changes;
                        try
                        {
                            changes = journal.GetChanges(lastUsn);
                            consecutiveFailures = 0;
                        }
                        catch (UsnJournalInvalidatedException)
                        {
                            // Journal wrapped since our cursor was captured - genuinely
                            // unrecoverable without a fresh cursor, so end this connection; the
                            // GUI's own reconnect-on-disconnect handling requests a new SNAPSHOT.
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            // The volume handle was torn down (service stopping) - nothing to
                            // recover, the outer using(journal)/loop condition will exit shortly.
                            break;
                        }
                        catch
                        {
                            // Anything else (a transient I/O hiccup, a momentary access glitch
                            // under heavy disk activity - e.g. the GUI mid-scanning many files for
                            // a content: search) used to unconditionally kill this connection,
                            // which forced the GUI into a full reindex to reconnect, which
                            // re-triggered whatever search was running, which caused MORE heavy
                            // disk activity, which could trip this same catch again - a
                            // self-sustaining reconnect/reindex loop. Retry in place instead;
                            // only give up after several consecutive failures in a row, which is
                            // a much stronger signal something is actually, persistently wrong.
                            consecutiveFailures++;
                            if (consecutiveFailures >= 5) break;
                            continue;
                        }

                        if (changes.Count == 0) continue;

                        foreach (var change in changes)
                        {
                            var delta = ResolveDelta(journal, change);
                            if (delta == null) continue;
                            PipeProtocol.WriteJsonFrame(pipe, new PipeFrame { Type = "delta", Delta = delta });
                        }

                        lastUsn = changes[changes.Count - 1].Usn + 1;
                    }
                }
            }
            catch (Exception ex) { Log("HandleConnection EXCEPTION: " + ex); }
            finally
            {
                try { pipe.Dispose(); } catch { }
                lock (_connectionThreads) _connectionThreads.Remove(Thread.CurrentThread);
            }
        }

        private static FileItemDto[] ScanDrive(string driveLetter)
        {
            var drive = new DriveInfo(driveLetter);
            var ntfsReader = new NtfsReader(drive, RetrieveMode.StandardInformations);
            var nodes = ntfsReader.GetNodes(driveLetter + ":\\");

            // No include/exclude-folder filtering here - that's user Settings living in the GUI's
            // AppSettings (a different process/profile than this LocalSystem service), so the GUI
            // applies its own IsPathIndexable filter to whatever we hand back, exactly like it
            // already does when scanning directly. This keeps all search/filter policy in one
            // place instead of needing to keep two copies in sync.
            //
            // AsParallel matters here: the direct (non-service) GUI path has always mapped these
            // ~1M nodes in parallel - a plain sequential Select was the one real regression in
            // service-mode's otherwise-inherent extra cost (unfiltered payload + IPC transfer).
            return nodes.AsParallel().Select(node => new FileItemDto
            {
                NodeIndex = node.NodeIndex,
                FullName = node.FullName,
                FileName = node.Name ?? Path.GetFileName(node.FullName) ?? node.FullName,
                Size = node.Size > 0 ? (long)node.Size : 0,
                LastWriteTime = node.LastChangeTime,
                IsDirectory = (node.Attributes & Attributes.Directory) != 0
            }).ToArray();
        }

        /// <summary>
        /// Resolves one raw USN change record into a DeltaDto, mirroring what
        /// MainWindow.xaml.cs's ApplyUsnChange used to compute locally - see DeltaDto's doc
        /// comment. Returns null for a reason that doesn't affect what's shown (matches the
        /// original's early-return for irrelevant Reason flags).
        /// </summary>
        private static DeltaDto ResolveDelta(UsnJournal journal, UsnChange change)
        {
            uint nodeIndex = (uint)(change.FileReferenceNumber & 0xFFFFFFFF);

            bool isRemoval = (change.Reason & (UsnJournal.UsnReason.FileDelete | UsnJournal.UsnReason.RenameOldName)) != 0;
            if (isRemoval)
                return new DeltaDto { NodeIndex = nodeIndex, IsRemoval = true };

            const UsnJournal.UsnReason relevantReasons =
                UsnJournal.UsnReason.FileCreate | UsnJournal.UsnReason.RenameNewName |
                UsnJournal.UsnReason.DataExtend | UsnJournal.UsnReason.DataTruncation |
                UsnJournal.UsnReason.DataOverwrite | UsnJournal.UsnReason.BasicInfoChange;

            if ((change.Reason & relevantReasons) == 0)
                return null;

            string fullName = journal.ResolvePath(change.FileReferenceNumber);
            if (fullName == null)
                return new DeltaDto { NodeIndex = nodeIndex, IsRemoval = true };

            long size = 0;
            DateTime lastWrite = DateTime.Now;
            try
            {
                var info = new FileInfo(fullName);
                if (info.Exists)
                {
                    size = info.Length;
                    lastWrite = info.LastWriteTime;
                }
            }
            catch { /* best-effort metadata refresh, matches original */ }

            return new DeltaDto
            {
                NodeIndex = nodeIndex,
                FullName = fullName,
                FileName = Path.GetFileName(fullName),
                Size = size,
                LastWriteTime = lastWrite,
                IsDirectory = change.IsDirectory
            };
        }
    }
}
