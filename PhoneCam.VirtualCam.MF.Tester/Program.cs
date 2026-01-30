using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

const uint Magic = 0x464D4350; // 'PCMF'
const uint Version = 1;
const uint PixelFormatArgb32 = 1;
const int HeaderSize = 64;
const int Width = 1280;
const int Height = 720;
const int BytesPerPixel = 4;
const int Stride = Width * BytesPerPixel;
const int BufferBytes = Stride * Height;
const string MapName = "Global\\PhoneCam.VirtualCam.FrameBuffer";
const string EventName = "Global\\PhoneCam.VirtualCam.FrameReady";

using var mmf = MemoryMappedFile.CreateOrOpen(MapName, HeaderSize + (BufferBytes * 2L));
using var accessor = mmf.CreateViewAccessor();
using var frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

unsafe
{
    byte* basePtr = null;
    accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

    WriteUInt32(basePtr, 0, Magic);
    WriteUInt32(basePtr, 4, Version);
    WriteUInt32(basePtr, 8, Width);
    WriteUInt32(basePtr, 12, Height);
    WriteUInt32(basePtr, 16, Stride);
    WriteUInt32(basePtr, 20, PixelFormatArgb32);
    WriteUInt32(basePtr, 40, BufferBytes);
    WriteUInt64(basePtr, 48, (ulong)Stopwatch.Frequency);

    ulong frameId = 0;
    int active = 0;

    Console.WriteLine("Writing test pattern to shared memory... Press Ctrl+C to stop.");
    while (true)
    {
        active ^= 1;
        byte* dst = basePtr + HeaderSize + (active * BufferBytes);

        int offset = (int)(frameId % Width);
        for (int y = 0; y < Height; y++)
        {
            byte* row = dst + (y * Stride);
            for (int x = 0; x < Width; x++)
            {
                byte r = (byte)((x + offset) % 256);
                byte g = (byte)((y + offset) % 256);
                byte b = (byte)((x + y + offset) % 256);
                row[x * 4 + 0] = b;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = r;
                row[x * 4 + 3] = 0xFF;
            }
        }

        frameId++;
        WriteUInt64(basePtr, 24, frameId);
        WriteInt64(basePtr, 32, Stopwatch.GetTimestamp());
        WriteUInt32(basePtr, 44, (uint)active);
        frameEvent.Set();

        Thread.Sleep(33);
    }
}

static unsafe void WriteUInt32(byte* ptr, int offset, uint value) => Unsafe.WriteUnaligned(ptr + offset, value);
static unsafe void WriteUInt64(byte* ptr, int offset, ulong value) => Unsafe.WriteUnaligned(ptr + offset, value);
static unsafe void WriteInt64(byte* ptr, int offset, long value) => Unsafe.WriteUnaligned(ptr + offset, value);
