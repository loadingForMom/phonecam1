using System;

namespace PhoneCam.VirtualCam.Filter.Ipc
{
    internal static class FrameProtocol
    {
        public const int Port = 51111;

        public const int PrefixBytes = 4;
        public const int HeaderBytes = 32;

        public const uint Magic = 0x4D414350; // 'P''C''A''M' little-endian
        public const ushort Version = 1;

        public const ushort PixelFormatBgr24 = 1;

        // Header layout (little-endian), matches PhoneCam.Tray.VirtualCamStreamer.
        //  0..3   uint32 magic
        //  4..5   uint16 version
        //  6..7   uint16 pixelFormat
        //  8..11  int32 width
        //  12..15 int32 height
        //  16..19 int32 strideBytes
        //  20..27 int64 timestampUtcTicks
        //  28..31 int32 payloadLengthBytes

        public static bool TryParseHeader(byte[] header, out FrameHeader h)
        {
            h = default;
            if (header == null || header.Length < HeaderBytes) return false;

            uint magic = ReadUInt32LE(header, 0);
            if (magic != Magic) return false;

            ushort ver = ReadUInt16LE(header, 4);
            if (ver != Version) return false;

            h.PixelFormat = ReadUInt16LE(header, 6);
            h.Width = ReadInt32LE(header, 8);
            h.Height = ReadInt32LE(header, 12);
            h.StrideBytes = ReadInt32LE(header, 16);
            h.TimestampUtcTicks = ReadInt64LE(header, 20);
            h.PayloadLengthBytes = ReadInt32LE(header, 28);
            return true;
        }

        public struct FrameHeader
        {
            public ushort PixelFormat;
            public int Width;
            public int Height;
            public int StrideBytes;
            public long TimestampUtcTicks;
            public int PayloadLengthBytes;
        }

        private static ushort ReadUInt16LE(byte[] b, int o)
        {
            unchecked
            {
                return (ushort)(b[o] | (b[o + 1] << 8));
            }
        }

        private static uint ReadUInt32LE(byte[] b, int o)
        {
            unchecked
            {
                return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
            }
        }

        private static int ReadInt32LE(byte[] b, int o)
        {
            unchecked
            {
                return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
            }
        }

        private static long ReadInt64LE(byte[] b, int o)
        {
            unchecked
            {
                uint lo = ReadUInt32LE(b, o);
                uint hi = ReadUInt32LE(b, o + 4);
                return ((long)hi << 32) | lo;
            }
        }
    }
}
