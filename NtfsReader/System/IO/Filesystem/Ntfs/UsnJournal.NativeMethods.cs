using System.Runtime.InteropServices;
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
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace System.IO.Filesystem.Ntfs
{
    public sealed partial class UsnJournal
    {
        private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
        private const uint FSCTL_CREATE_USN_JOURNAL = 0x000900e7;
        private const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;

        [Flags]
        public enum UsnReason : uint
        {
            DataOverwrite = 0x00000001,
            DataExtend = 0x00000002,
            DataTruncation = 0x00000004,
            NamedDataOverwrite = 0x00000010,
            NamedDataExtend = 0x00000020,
            NamedDataTruncation = 0x00000040,
            FileCreate = 0x00000100,
            FileDelete = 0x00000200,
            EaChange = 0x00000400,
            SecurityChange = 0x00000800,
            RenameOldName = 0x00001000,
            RenameNewName = 0x00002000,
            IndexableChange = 0x00004000,
            BasicInfoChange = 0x00008000,
            HardLinkChange = 0x00010000,
            CompressionChange = 0x00020000,
            EncryptionChange = 0x00040000,
            ObjectIdChange = 0x00080000,
            ReparsePointChange = 0x00100000,
            StreamChange = 0x00200000,
            TransactedChange = 0x00400000,
            IntegrityChange = 0x00800000,
            Close = 0x80000000
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct USN_JOURNAL_DATA_V0
        {
            public UInt64 UsnJournalID;
            public Int64 FirstUsn;
            public Int64 NextUsn;
            public Int64 LowestValidUsn;
            public Int64 MaxUsn;
            public UInt64 MaximumSize;
            public UInt64 AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CREATE_USN_JOURNAL_DATA
        {
            public UInt64 MaximumSize;
            public UInt64 AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct READ_USN_JOURNAL_DATA_V0
        {
            public Int64 StartUsn;
            public UInt32 ReasonMask;
            public UInt32 ReturnOnlyOnClose;
            public UInt64 Timeout;
            public UInt64 BytesToWaitFor;
            public UInt64 UsnJournalID;
        }

        //USN_RECORD_V2 - the FileName field is a variable-length UTF-16 array
        //placed right after the fixed header, at FileNameOffset bytes in.
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct USN_RECORD_V2
        {
            public UInt32 RecordLength;
            public UInt16 MajorVersion;
            public UInt16 MinorVersion;
            public UInt64 FileReferenceNumber;
            public UInt64 ParentFileReferenceNumber;
            public Int64 Usn;
            public Int64 TimeStamp;
            public UInt32 Reason;
            public UInt32 SourceInfo;
            public UInt32 SecurityId;
            public UInt32 FileAttributes;
            public UInt16 FileNameLength;
            public UInt16 FileNameOffset;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref CREATE_USN_JOURNAL_DATA lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            out USN_JOURNAL_DATA_V0 lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref READ_USN_JOURNAL_DATA_V0 lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32", CharSet = CharSet.Auto, BestFitMapping = false)]
        private static extern bool GetVolumeNameForVolumeMountPoint(String volumeName, System.Text.StringBuilder uniqueVolumeName, int uniqueNameBufferCapacity);

        [DllImport("kernel32", CharSet = CharSet.Auto, BestFitMapping = false, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_NAME_NORMALIZED = 0x0;

        private enum FILE_ID_TYPE : int
        {
            FileIdType = 0,
            ObjectIdType = 1,
            ExtendedFileIdType = 2
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_ID_DESCRIPTOR
        {
            public int Size;
            public FILE_ID_TYPE Type;
            public Int64 FileId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle OpenFileById(
            SafeFileHandle hVolumeHint,
            ref FILE_ID_DESCRIPTOR lpFileId,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwFlagsAndAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
    }
}
