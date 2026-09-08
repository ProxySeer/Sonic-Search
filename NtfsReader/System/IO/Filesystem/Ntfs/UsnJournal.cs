/*
    The NtfsReader library.

    Copyright (C) 2008 Danny Couture

    This library is free software; you can redistribute it and/or
    modify it under the terms of the GNU Lesser General Public
    License as published by the Free Software Foundation; either
    version 2.1 of the License, or (at your option) any later version.

    This library is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
    Lesser General Public License for more details.

    You should have received a copy of the GNU Lesser General Public
    License along with this library; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA

    For the full text of the license see the "License.txt" file.
*/
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace System.IO.Filesystem.Ntfs
{
    /// <summary>
    /// A single change record read from the NTFS USN change journal.
    /// </summary>
    public struct UsnChange
    {
        public UInt64 FileReferenceNumber;
        public UInt64 ParentFileReferenceNumber;
        public string FileName;
        public UsnJournal.UsnReason Reason;
        public Int64 Usn;
        public bool IsDirectory;
    }

    /// <summary>
    /// Thrown when the requested starting USN is no longer valid (the journal
    /// was deleted/recreated or has wrapped past it). The caller must fall
    /// back to a full re-scan (e.g. via NtfsReader) and start polling again
    /// from the current journal position.
    /// </summary>
    public class UsnJournalInvalidatedException : InvalidOperationException
    {
        public UsnJournalInvalidatedException(string message) : base(message) { }
    }

    /// <summary>
    /// Reads incremental file-system change notifications from the NTFS USN
    /// (Update Sequence Number) change journal, allowing a caller that already
    /// has a snapshot from <see cref="NtfsReader"/> to apply deltas instead of
    /// re-scanning the whole Master File Table.
    /// </summary>
    public sealed partial class UsnJournal : IDisposable
    {
        private SafeFileHandle _volumeHandle;
        private UInt64 _journalId;

        public UsnJournal(DriveInfo driveInfo)
        {
            if (driveInfo == null)
                throw new ArgumentNullException("driveInfo");

            StringBuilder builder = new StringBuilder(1024);
            GetVolumeNameForVolumeMountPoint(driveInfo.RootDirectory.Name, builder, builder.Capacity);
            string volume = builder.ToString().TrimEnd(new char[] { '\\' });

            _volumeHandle = CreateFile(
                volume,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (_volumeHandle == null || _volumeHandle.IsInvalid)
                throw new IOException(
                    string.Format(
                        "Unable to open volume {0} for USN journal access. Make sure it exists and that you have Administrator privileges.",
                        driveInfo));

            EnsureJournalExists();
        }

        /// <summary>
        /// The current NextUsn of the journal, i.e. the cursor value to persist
        /// and pass as <c>startUsn</c> to <see cref="GetChanges"/> next time.
        /// </summary>
        public Int64 CurrentUsn
        {
            get { return QueryJournal().NextUsn; }
        }

        private unsafe void EnsureJournalExists()
        {
            USN_JOURNAL_DATA_V0 data;
            uint bytesReturned;
            bool ok = DeviceIoControl(
                _volumeHandle,
                FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero,
                0,
                out data,
                (uint)Marshal.SizeOf(typeof(USN_JOURNAL_DATA_V0)),
                out bytesReturned,
                IntPtr.Zero);

            if (ok)
            {
                _journalId = data.UsnJournalID;
                return;
            }

            //journal doesn't exist yet on this volume - create one
            CREATE_USN_JOURNAL_DATA createData = new CREATE_USN_JOURNAL_DATA
            {
                MaximumSize = 32 * 1024 * 1024,   // 32 MB
                AllocationDelta = 4 * 1024 * 1024 // 4 MB
            };

            if (!DeviceIoControl(
                    _volumeHandle,
                    FSCTL_CREATE_USN_JOURNAL,
                    ref createData,
                    (uint)Marshal.SizeOf(typeof(CREATE_USN_JOURNAL_DATA)),
                    IntPtr.Zero,
                    0,
                    out bytesReturned,
                    IntPtr.Zero))
                throw new IOException("Unable to create the USN change journal on this volume.");

            data = QueryJournal();
            _journalId = data.UsnJournalID;
        }

        private unsafe USN_JOURNAL_DATA_V0 QueryJournal()
        {
            USN_JOURNAL_DATA_V0 data;
            uint bytesReturned;
            if (!DeviceIoControl(
                    _volumeHandle,
                    FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero,
                    0,
                    out data,
                    (uint)Marshal.SizeOf(typeof(USN_JOURNAL_DATA_V0)),
                    out bytesReturned,
                    IntPtr.Zero))
                throw new IOException("Unable to query the USN change journal.");

            return data;
        }

        /// <summary>
        /// Read every change recorded in the journal since <paramref name="startUsn"/>.
        /// </summary>
        /// <param name="startUsn">The USN cursor to resume from (0 to read from the
        /// beginning of what the journal currently retains).</param>
        /// <exception cref="UsnJournalInvalidatedException">
        /// The journal was deleted/recreated (different JournalID) or has wrapped
        /// past <paramref name="startUsn"/> since it was captured - the caller must
        /// do a full re-scan and resume polling from <see cref="CurrentUsn"/>.
        /// </exception>
        public List<UsnChange> GetChanges(Int64 startUsn)
        {
            USN_JOURNAL_DATA_V0 journalData = QueryJournal();

            if (journalData.UsnJournalID != _journalId)
                throw new UsnJournalInvalidatedException("The USN journal was recreated since the last read; a full re-scan is required.");

            if (startUsn < journalData.LowestValidUsn)
                throw new UsnJournalInvalidatedException("The requested USN has aged out of the journal; a full re-scan is required.");

            var changes = new List<UsnChange>();

            const uint bufferSize = 4096 * 16; // 64 KB read buffer
            byte[] buffer = new byte[bufferSize];

            Int64 cursor = startUsn;

            while (true)
            {
                Int64 nextUsn = ReadBatch(cursor, buffer, bufferSize, changes);

                if (nextUsn <= cursor)
                    break; // caught up to the head of the journal, or nothing came back

                cursor = nextUsn;
            }

            return changes;
        }

        /// <summary>
        /// Reads one FSCTL_READ_USN_JOURNAL batch starting at <paramref name="cursor"/>,
        /// appends the parsed records to <paramref name="changes"/>, and returns the
        /// journal's reported NextUsn for this batch.
        /// </summary>
        private unsafe Int64 ReadBatch(Int64 cursor, byte[] buffer, uint bufferSize, List<UsnChange> changes)
        {
            READ_USN_JOURNAL_DATA_V0 readData = new READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = cursor,
                ReasonMask = 0xFFFFFFFF, // all reasons
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalID = _journalId
            };

            uint bytesReturned;
            fixed (byte* bufferPtr = buffer)
            {
                bool ok = DeviceIoControl(
                    _volumeHandle,
                    FSCTL_READ_USN_JOURNAL,
                    ref readData,
                    (uint)Marshal.SizeOf(typeof(READ_USN_JOURNAL_DATA_V0)),
                    (IntPtr)bufferPtr,
                    bufferSize,
                    out bytesReturned,
                    IntPtr.Zero);

                if (!ok)
                    throw new IOException("Unable to read the USN change journal.");

                if (bytesReturned <= sizeof(Int64))
                    return cursor; // only the "next USN" header came back - nothing new

                Int64 nextUsn = *(Int64*)bufferPtr;

                uint offset = sizeof(Int64);
                while (offset < bytesReturned)
                {
                    USN_RECORD_V2* record = (USN_RECORD_V2*)(bufferPtr + offset);
                    if (record->RecordLength == 0)
                        break;

                    char* namePtr = (char*)(bufferPtr + offset + record->FileNameOffset);
                    string fileName = new string(namePtr, 0, record->FileNameLength / sizeof(char));

                    changes.Add(new UsnChange
                    {
                        FileReferenceNumber = record->FileReferenceNumber,
                        ParentFileReferenceNumber = record->ParentFileReferenceNumber,
                        FileName = fileName,
                        Reason = (UsnReason)record->Reason,
                        Usn = record->Usn,
                        IsDirectory = (record->FileAttributes & 0x10) != 0 // FILE_ATTRIBUTE_DIRECTORY
                    });

                    offset += record->RecordLength;
                }

                return nextUsn;
            }
        }

        /// <summary>
        /// Resolve the current full path of a file/directory given its NTFS file
        /// reference number (as reported by a <see cref="UsnChange"/>), by opening
        /// it directly via its file ID rather than walking any cached node tree.
        /// Returns null if the file no longer exists or can't be opened (e.g. it
        /// was deleted again after the change record was written).
        /// </summary>
        public string ResolvePath(UInt64 fileReferenceNumber)
        {
            var fileId = new FILE_ID_DESCRIPTOR
            {
                Size = Marshal.SizeOf(typeof(FILE_ID_DESCRIPTOR)),
                Type = FILE_ID_TYPE.FileIdType,
                FileId = (Int64)fileReferenceNumber
            };

            using (var handle = OpenFileById(
                       _volumeHandle,
                       ref fileId,
                       0, // query metadata only, no read/write access needed
                       FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                       IntPtr.Zero,
                       FILE_FLAG_BACKUP_SEMANTICS))
            {
                if (handle == null || handle.IsInvalid)
                    return null;

                var pathBuilder = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(handle, pathBuilder, (uint)pathBuilder.Capacity, FILE_NAME_NORMALIZED);
                if (length == 0 || length >= pathBuilder.Capacity)
                    return null;

                string path = pathBuilder.ToString();

                //strip the extended-length \\?\ prefix Windows adds
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                    path = path.Substring(4);

                return path;
            }
        }

        public void Dispose()
        {
            if (_volumeHandle != null)
            {
                _volumeHandle.Dispose();
                _volumeHandle = null;
            }
        }
    }
}
