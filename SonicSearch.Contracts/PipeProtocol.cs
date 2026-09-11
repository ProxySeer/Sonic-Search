using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SonicSearch.Contracts
{
    /// <summary>
    /// Framing shared by the service (writer of snapshot/delta frames, reader of request lines)
    /// and the GUI (reverse). Every frame is [1-byte kind][4-byte little-endian length][payload].
    /// Two payload kinds:
    ///  - Snapshot: hand-rolled compact binary (BinaryWriter, fixed field order), used only for
    ///    the one-time full-drive item list. A real drive can hold ~1M items - verbose per-
    ///    property JSON (the original approach) blew past 250MB for a ~940K-file C: drive, which
    ///    is both slow to build/parse and, at the time, exceeded this same protocol's own
    ///    sanity-check ceiling on frame length ("Implausible pipe frame length"). Binary encoding
    ///    is both far smaller and far faster to write/read for this volume of records.
    ///  - Json: everything else (the rare one-off delta/error messages) - simplicity matters more
    ///    than size here since these are tiny and infrequent.
    /// </summary>
    public static class PipeProtocol
    {
        public const string PipeName = "SonicSearchServicePipe";

        private const byte KindSnapshot = 1;
        private const byte KindJson = 2;


        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 32
        };

        public static void WriteRequestLine(Stream stream, string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        public static string ReadRequestLine(Stream stream)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\n') break;
                sb.Append((char)b);
            }
            return sb.Length == 0 ? null : sb.ToString().Trim();
        }

        public static void WriteSnapshotFrame(Stream stream, FileItemDto[] items)
        {
            using (var ms = new MemoryStream())
            {
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(items.Length);
                    foreach (var item in items)
                    {
                        bw.Write(item.NodeIndex);
                        bw.Write(item.FullName ?? "");
                        bw.Write(item.FileName ?? "");
                        bw.Write(item.Size);
                        bw.Write(item.LastWriteTime.Ticks);
                        bw.Write(item.IsDirectory);
                    }
                }
                WriteRaw(stream, KindSnapshot, ms.ToArray());
            }
        }

        public static void WriteJsonFrame(Stream stream, PipeFrame frame)
        {
            string json = Serializer.Serialize(frame);
            WriteRaw(stream, KindJson, Encoding.UTF8.GetBytes(json));
        }

        private static void WriteRaw(Stream stream, byte kind, byte[] payload)
        {
            stream.WriteByte(kind);
            stream.Write(BitConverter.GetBytes(payload.Length), 0, 4);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        public class ReadResult
        {
            /// <summary>Populated when this was a snapshot frame.</summary>
            public FileItemDto[] SnapshotItems;
            /// <summary>Populated when this was a json frame (delta/error/etc).</summary>
            public PipeFrame JsonFrame;
        }

        /// <summary>Returns null if the stream closed cleanly before a frame arrived.</summary>
        public static ReadResult ReadAnyFrame(Stream stream)
        {
            int kind = stream.ReadByte();
            if (kind == -1) return null;

            byte[] header = ReadExact(stream, 4);
            if (header == null) return null;
            int length = BitConverter.ToInt32(header, 0);
            // length comes straight from a 4-byte int32 header, so it can never legitimately
            // exceed int.MaxValue (~2.1GB) - the only corruption this can actually catch is a
            // negative value (a desynced/garbage header).
            if (length < 0)
                throw new InvalidDataException("Implausible pipe frame length: " + length);

            byte[] payload = ReadExact(stream, length);
            if (payload == null) return null;

            if (kind == KindSnapshot)
            {
                using (var ms = new MemoryStream(payload))
                using (var br = new BinaryReader(ms, Encoding.UTF8))
                {
                    int count = br.ReadInt32();
                    var items = new FileItemDto[count];
                    for (int i = 0; i < count; i++)
                    {
                        items[i] = new FileItemDto
                        {
                            NodeIndex = br.ReadUInt32(),
                            FullName = br.ReadString(),
                            FileName = br.ReadString(),
                            Size = br.ReadInt64(),
                            LastWriteTime = new DateTime(br.ReadInt64()),
                            IsDirectory = br.ReadBoolean()
                        };
                    }
                    return new ReadResult { SnapshotItems = items };
                }
            }
            else
            {
                string json = Encoding.UTF8.GetString(payload);
                return new ReadResult { JsonFrame = Serializer.Deserialize<PipeFrame>(json) };
            }
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) return offset == 0 ? null : throw new EndOfStreamException("Pipe closed mid-frame.");
                offset += read;
            }
            return buffer;
        }
    }
}
