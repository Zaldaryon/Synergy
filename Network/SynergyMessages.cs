using System;
using ProtoBuf;

namespace Synergy.Network
{
    [ProtoContract]
    public class SynergyHandshake
    {
        [ProtoMember(1)] public string Version;
        [ProtoMember(2)] public int Capabilities;

        public const int CapDeltaEncoding = 1;
    }

    /// <summary>
    /// Thin ProtoBuf wrapper for the mod UDP channel. The actual entity data is
    /// manually encoded in Data using compact varint format — zero per-field tag overhead.
    /// ProtoBuf overhead: ~3 bytes (1 tag + 2 length prefix for the byte array).
    /// </summary>
    [ProtoContract]
    public class DeltaPositionBatch
    {
        [ProtoMember(1)] public byte[] Data;
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
        public byte Flags; // bit 0 = IsAbsolute, bit 1 = Teleport
    }

    /// <summary>
    /// Compact binary encoder/decoder for entity position deltas.
    /// Format: fixed field order, no tags, varint/zigzag encoding.
    /// Matches the approach used by vanilla's Packet_EntityPositionSerializer
    /// and industry-standard game netcode (Quake 3, Source Engine).
    ///
    /// Wire format per entity (fields in fixed order):
    ///   varint   entityId
    ///   byte     flags (bit 0 = IsAbsolute, bit 1 = Teleport)
    ///   zigzag   deltaX, deltaY, deltaZ
    ///   zigzag   deltaMotionX, deltaMotionY, deltaMotionZ
    ///   varint   yaw, pitch, roll, headYaw, headPitch, bodyYaw
    ///   varint   controls, tick, positionVersion, mountControls
    ///   varint   tagsPart1, tagsPart2, tagsPart3, tagsPart4
    ///
    /// Stationary entity (all deltas zero, no tags): ~8 bytes
    /// Walking entity (small deltas): ~18 bytes
    /// Absolute entity (full position + tags): ~55 bytes
    /// </summary>
    public static class DeltaCodec
    {
        // Max bytes per entity: 23 fields × 10 bytes max varint + 1 flags byte = 231
        private const int MaxBytesPerEntity = 232;

        // ThreadStatic encode buffer — reused across calls, safe because SendPacket
        // copies the data before returning (via ProtoBuf serialize + ToArray).
        [ThreadStatic] private static byte[] t_encodeBuf;

        /// <summary>
        /// Encode entities from entries[offset..offset+count] into a compact byte array.
        /// Uses ThreadStatic buffer internally — zero intermediate allocations.
        /// The returned byte[] is a fresh allocation (required by ProtoBuf SendPacket).
        /// </summary>
        public static byte[] Encode(DeltaEntry[] entries, int offset, int count)
        {
            int maxSize = count * MaxBytesPerEntity + 5;
            var buf = t_encodeBuf;
            if (buf == null || buf.Length < maxSize)
                buf = t_encodeBuf = new byte[maxSize];

            int pos = 0;
            WriteVarint(buf, ref pos, (ulong)count);

            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                ref var e = ref entries[i];
                WriteVarint(buf, ref pos, (ulong)e.EntityId);
                buf[pos++] = e.Flags;
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

            // Must return a new array — SendPacket's ProtoBuf serializer reads it asynchronously
            var result = new byte[pos];
            Buffer.BlockCopy(buf, 0, result, 0, pos);
            return result;
        }

        /// <summary>
        /// Decode a byte array back into DeltaEntry structs.
        /// Returns the number of entries decoded. Zero allocation (writes to pre-allocated output).
        /// </summary>
        public static int Decode(byte[] data, DeltaEntry[] output)
        {
            if (data == null || data.Length == 0) return 0;

            int pos = 0;
            int count = (int)ReadVarint(data, ref pos);
            if (count > output.Length) count = output.Length;

            for (int i = 0; i < count; i++)
            {
                ref var e = ref output[i];
                e.EntityId = (long)ReadVarint(data, ref pos);
                e.Flags = data[pos++];
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
