using System;
using ProtoBuf;

namespace Synergy.Network
{
    [ProtoContract]
    public class SynergyHandshake
    {
        [ProtoMember(1)] public string Version;
        [ProtoMember(2)] public int Capabilities;

        public const int CapDeltaEncoding = 1; // legacy v1 (chained deltas) — no longer emitted
        public const int CapDeltaAck = 2;      // v2: ack-based delta (Quake 3 / Source model)
    }

    /// <summary>
    /// Thin ProtoBuf wrapper for the mod UDP channel. The actual entity data is manually encoded
    /// in Data via DeltaCodec — compact varint format, zero per-field tag overhead.
    /// </summary>
    [ProtoContract]
    public class DeltaPositionBatch
    {
        [ProtoMember(1)] public byte[] Data;
    }

    /// <summary>
    /// Client → server cumulative acknowledgement: the highest snapshot generation the client has
    /// received and decoded. Sent over the reliable handshake channel. The server advances each
    /// entity's delta baseline to the value the client held at this generation, so every delta is
    /// encoded against state the client provably has. A lost UDP packet is simply never acked, so
    /// the server keeps resending the cumulative change against the older acked base until an ack
    /// advances it — there is no permanent desync. (Quake 3 / Source / Gaffer state-sync model.)
    /// </summary>
    [ProtoContract]
    public class SynergyAck
    {
        [ProtoMember(1)] public int Generation;
    }

    /// <summary>
    /// In-memory representation of a single entity delta. NOT serialized by ProtoBuf —
    /// encoded/decoded manually via DeltaCodec for minimal wire size.
    /// </summary>
    public struct DeltaEntry
    {
        public long EntityId;
        public long DeltaX, DeltaY, DeltaZ;
        public long DeltaMotionX, DeltaMotionY, DeltaMotionZ;
        public int Yaw, Pitch, Roll, HeadYaw, HeadPitch, BodyYaw;
        public int Controls, Tick, PositionVersion, MountControls; // PositionVersion: server-side only, carried for forward compat
        public long TagsPart1, TagsPart2, TagsPart3, TagsPart4;
        public int BaseGen; // generation of the baseline this delta is encoded against (== batch gen when absolute)
        public byte Flags;  // bit 0 = IsAbsolute, bit 1 = Teleport
    }

    /// <summary>
    /// Compact binary encoder/decoder for entity position deltas.
    /// Format: fixed field order, no tags, varint/zigzag encoding.
    ///
    /// Wire format (v2): a batch is one tick's snapshot for one client.
    ///   byte     wireVersion (== WireVersion)
    ///   varint   generation        (snapshot id for this batch)
    ///   varint   count
    ///   per entity:
    ///     varint   entityId
    ///     byte     flags (bit 0 = IsAbsolute, bit 1 = Teleport)
    ///     varint   baseGenOffset   (ONLY when not absolute: generation - baseGen; the client
    ///                                reconstructs against the value it held at that generation)
    ///     zigzag   deltaX, deltaY, deltaZ
    ///     zigzag   deltaMotionX, deltaMotionY, deltaMotionZ
    ///     varint   yaw, pitch, roll, headYaw, headPitch, bodyYaw
    ///     varint   controls, tick, positionVersion, mountControls
    ///     varint   tagsPart1, tagsPart2, tagsPart3, tagsPart4
    /// </summary>
    public static class DeltaCodec
    {
        public const byte WireVersion = 2;

        // Max bytes per entity: 24 varint/zigzag fields × 10 + 1 flags = 241
        private const int MaxBytesPerEntity = 241;

        [ThreadStatic] private static byte[] t_encodeBuf;

        /// <summary>
        /// Encode entities[offset..offset+count] into a compact byte array tagged with this batch's
        /// snapshot generation. The returned byte[] is a fresh allocation (required by SendPacket).
        /// </summary>
        public static byte[] Encode(DeltaEntry[] entries, int offset, int count, int generation)
        {
            int maxSize = count * MaxBytesPerEntity + 16;
            var buf = t_encodeBuf;
            if (buf == null || buf.Length < maxSize)
                buf = t_encodeBuf = new byte[maxSize];

            int pos = 0;
            buf[pos++] = WireVersion;
            WriteVarint(buf, ref pos, (ulong)(uint)generation);
            WriteVarint(buf, ref pos, (ulong)count);

            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                ref var e = ref entries[i];
                WriteVarint(buf, ref pos, (ulong)e.EntityId);
                buf[pos++] = e.Flags;
                if ((e.Flags & FlagAbsolute) == 0)
                {
                    int baseOffset = generation - e.BaseGen;
                    if (baseOffset < 0) baseOffset = 0;
                    WriteVarint(buf, ref pos, (ulong)(uint)baseOffset);
                }
                WriteZigZag(buf, ref pos, e.DeltaX);
                WriteZigZag(buf, ref pos, e.DeltaY);
                WriteZigZag(buf, ref pos, e.DeltaZ);
                WriteZigZag(buf, ref pos, e.DeltaMotionX);
                WriteZigZag(buf, ref pos, e.DeltaMotionY);
                WriteZigZag(buf, ref pos, e.DeltaMotionZ);
                WriteVarint(buf, ref pos, (ulong)(uint)e.Yaw);
                WriteVarint(buf, ref pos, (ulong)(uint)e.Pitch);
                WriteVarint(buf, ref pos, (ulong)(uint)e.Roll);
                WriteVarint(buf, ref pos, (ulong)(uint)e.HeadYaw);
                WriteVarint(buf, ref pos, (ulong)(uint)e.HeadPitch);
                WriteVarint(buf, ref pos, (ulong)(uint)e.BodyYaw);
                WriteVarint(buf, ref pos, (ulong)(uint)e.Controls);
                WriteVarint(buf, ref pos, (ulong)(uint)e.Tick);
                WriteVarint(buf, ref pos, (ulong)(uint)e.PositionVersion);
                WriteVarint(buf, ref pos, (ulong)(uint)e.MountControls);
                WriteVarint(buf, ref pos, (ulong)e.TagsPart1);
                WriteVarint(buf, ref pos, (ulong)e.TagsPart2);
                WriteVarint(buf, ref pos, (ulong)e.TagsPart3);
                WriteVarint(buf, ref pos, (ulong)e.TagsPart4);
            }

            var result = new byte[pos];
            Buffer.BlockCopy(buf, 0, result, 0, pos);
            return result;
        }

        /// <summary>
        /// Decode a batch back into DeltaEntry structs. Returns the number of entries decoded and
        /// the batch's snapshot generation. Returns 0 (and generation 0) for an unrecognised wire
        /// version — a packet from an incompatible server is skipped safely rather than misparsed.
        /// </summary>
        public static int Decode(byte[] data, DeltaEntry[] output, out int generation)
        {
            generation = 0;
            if (data == null || data.Length == 0) return 0;

            int pos = 0;
            byte version = data[pos++];
            if (version != WireVersion) return 0;

            generation = (int)(uint)ReadVarint(data, ref pos);
            int count = (int)ReadVarint(data, ref pos);
            if (count > output.Length) count = output.Length;

            for (int i = 0; i < count; i++)
            {
                ref var e = ref output[i];
                e.EntityId = (long)ReadVarint(data, ref pos);
                e.Flags = data[pos++];
                if ((e.Flags & FlagAbsolute) == 0)
                    e.BaseGen = generation - (int)(uint)ReadVarint(data, ref pos);
                else
                    e.BaseGen = generation;
                e.DeltaX = ReadZigZag(data, ref pos);
                e.DeltaY = ReadZigZag(data, ref pos);
                e.DeltaZ = ReadZigZag(data, ref pos);
                e.DeltaMotionX = ReadZigZag(data, ref pos);
                e.DeltaMotionY = ReadZigZag(data, ref pos);
                e.DeltaMotionZ = ReadZigZag(data, ref pos);
                e.Yaw = (int)(uint)ReadVarint(data, ref pos);
                e.Pitch = (int)(uint)ReadVarint(data, ref pos);
                e.Roll = (int)(uint)ReadVarint(data, ref pos);
                e.HeadYaw = (int)(uint)ReadVarint(data, ref pos);
                e.HeadPitch = (int)(uint)ReadVarint(data, ref pos);
                e.BodyYaw = (int)(uint)ReadVarint(data, ref pos);
                e.Controls = (int)(uint)ReadVarint(data, ref pos);
                e.Tick = (int)(uint)ReadVarint(data, ref pos);
                e.PositionVersion = (int)(uint)ReadVarint(data, ref pos);
                e.MountControls = (int)(uint)ReadVarint(data, ref pos);
                e.TagsPart1 = (long)ReadVarint(data, ref pos);
                e.TagsPart2 = (long)ReadVarint(data, ref pos);
                e.TagsPart3 = (long)ReadVarint(data, ref pos);
                e.TagsPart4 = (long)ReadVarint(data, ref pos);
            }

            return count;
        }

        // --- Varint encoding (unsigned, 7 bits per byte, MSB = continuation) ---

        private static void WriteVarint(byte[] buf, ref int pos, ulong value)
        {
            while (value > 0x7F)
            {
                buf[pos++] = (byte)(value | 0x80);
                value >>= 7;
            }
            buf[pos++] = (byte)value;
        }

        private static ulong ReadVarint(byte[] buf, ref int pos)
        {
            ulong result = 0;
            int shift = 0;
            byte b;
            do
            {
                b = buf[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0 && shift < 64);
            return result;
        }

        // --- ZigZag encoding (signed → unsigned, small abs values → small varints) ---

        private static void WriteZigZag(byte[] buf, ref int pos, long value)
        {
            WriteVarint(buf, ref pos, (ulong)((value << 1) ^ (value >> 63)));
        }

        private static long ReadZigZag(byte[] buf, ref int pos)
        {
            ulong raw = ReadVarint(buf, ref pos);
            return (long)((raw >> 1) ^ (~(raw & 1) + 1));
        }

        // Flag constants
        public const byte FlagAbsolute = 1;
        public const byte FlagTeleport = 2;
    }
}
