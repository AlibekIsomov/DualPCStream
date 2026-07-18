using System;
using System.Buffers.Binary;

namespace DualPCStream.Shared
{
    /// <summary>
    /// Fixed binary headers for the two UDP streams. An identical copy of this
    /// file lives in both the Sender and Receiver projects - keep them in sync.
    /// All fields are little-endian.
    /// </summary>
    public static class VideoPacket
    {
        // uint frameId | ushort packetIndex | ushort totalPackets | long timestamp
        public const int HeaderSize = 16;

        // Stay well under a typical 1500-byte MTU so the OS never has to
        // IP-fragment a datagram (one lost IP fragment would kill the whole
        // datagram, and with it a big chunk of the frame).
        public const int MaxDatagram = 1400;
        public const int MaxPayload = MaxDatagram - HeaderSize;

        public static void WriteHeader(Span<byte> dest, uint frameId, ushort packetIndex, ushort totalPackets, long timestamp)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest, frameId);
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(4), packetIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(6), totalPackets);
            BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(8), timestamp);
        }

        public static (uint FrameId, ushort PacketIndex, ushort TotalPackets, long Timestamp) ParseHeader(ReadOnlySpan<byte> src) =>
            (BinaryPrimitives.ReadUInt32LittleEndian(src),
             BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4)),
             BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(6)),
             BinaryPrimitives.ReadInt64LittleEndian(src.Slice(8)));
    }

    /// <summary>
    /// Tiny connectivity handshake so the Sender can check "is a Receiver
    /// actually listening at that IP?" before streaming. The Sender fires a
    /// 4-byte ping at the video port; a running Receiver echoes a pong back
    /// to wherever the ping came from. Deliberately shorter than any real
    /// packet header so it can never be mistaken for stream data.
    /// </summary>
    public static class Probe
    {
        public static readonly byte[] Ping = { (byte)'D', (byte)'P', (byte)'C', (byte)'?' };
        public static readonly byte[] Pong = { (byte)'D', (byte)'P', (byte)'C', (byte)'!' };

        public static bool IsPing(byte[] d) => d.Length == 4 && d[0] == 'D' && d[1] == 'P' && d[2] == 'C' && d[3] == '?';
        public static bool IsPong(byte[] d) => d.Length == 4 && d[0] == 'D' && d[1] == 'P' && d[2] == 'C' && d[3] == '!';
    }

    public static class AudioPacket
    {
        // uint sequence | long timestamp
        public const int HeaderSize = 12;
        public const int MaxDatagram = 1400;
        public const int MaxPayload = MaxDatagram - HeaderSize;

        public static void WriteHeader(Span<byte> dest, uint sequence, long timestamp)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest, sequence);
            BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(4), timestamp);
        }

        public static (uint Sequence, long Timestamp) ParseHeader(ReadOnlySpan<byte> src) =>
            (BinaryPrimitives.ReadUInt32LittleEndian(src),
             BinaryPrimitives.ReadInt64LittleEndian(src.Slice(4)));
    }
}
