using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PhoneCam.Tray;

internal sealed unsafe class VirtualCamSharedMemoryWriter : IDisposable
{
    private const uint Magic = 0x464D4350; // 'PCMF'
    private const uint Version = 1;
    private const uint PixelFormatArgb32 = 1;

    private const int HeaderSize = 64;
    private const int TargetWidth = 1280;
    private const int TargetHeight = 720;
    private const int BytesPerPixel = 4;
    private const int TargetStride = TargetWidth * BytesPerPixel;
    private const int BufferBytes = TargetStride * TargetHeight;

    private const string MapName = "Global\\PhoneCam.VirtualCam.FrameBuffer";
    private const string EventName = "Global\\PhoneCam.VirtualCam.FrameReady";

    private readonly object _sync = new();
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private EventWaitHandle? _frameEvent;
    private byte* _basePtr;
    private bool _disposed;
    private ulong _frameId;
    private Bitmap? _scratch;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _scratch?.Dispose();
            _scratch = null;

            if (_accessor != null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _accessor.Dispose();
            }

            _mmf?.Dispose();
            _frameEvent?.Dispose();
            _accessor = null;
            _mmf = null;
            _frameEvent = null;
            _basePtr = null;
        }
    }

    public void WriteFrame(Bitmap source)
    {
        if (source == null) return;

        lock (_sync)
        {
            if (_disposed) return;
            EnsureOpen();
            if (_accessor == null || _frameEvent == null || _basePtr == null)
                return;

            var src = PrepareBitmap(source);
            var rect = new Rectangle(0, 0, TargetWidth, TargetHeight);
            var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int writeIndex = (int)(ReadUInt32(44) ^ 1u);
                byte* dstBase = _basePtr + HeaderSize + (writeIndex * BufferBytes);

                CopyBitmapToBuffer(data, dstBase);

                _frameId++;
                long qpc = Stopwatch.GetTimestamp();
                ulong qpcFreq = (ulong)Stopwatch.Frequency;

                WriteUInt32(0, Magic);
                WriteUInt32(4, Version);
                WriteUInt32(8, TargetWidth);
                WriteUInt32(12, TargetHeight);
                WriteUInt32(16, TargetStride);
                WriteUInt32(20, PixelFormatArgb32);
                WriteUInt64(24, _frameId);
                WriteInt64(32, qpc);
                WriteUInt32(40, BufferBytes);
                WriteUInt32(44, (uint)writeIndex);
                WriteUInt64(48, qpcFreq);

                _frameEvent.Set();
            }
            finally
            {
                src.UnlockBits(data);
            }
        }
    }

    private void EnsureOpen()
    {
        if (_mmf != null && _accessor != null && _frameEvent != null)
            return;

        long capacity = HeaderSize + (BufferBytes * 2L);
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, capacity, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);
        _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

        WriteUInt32(0, Magic);
        WriteUInt32(4, Version);
        WriteUInt32(8, TargetWidth);
        WriteUInt32(12, TargetHeight);
        WriteUInt32(16, TargetStride);
        WriteUInt32(20, PixelFormatArgb32);
        WriteUInt32(40, BufferBytes);
        WriteUInt32(44, 0);
        WriteUInt64(48, (ulong)Stopwatch.Frequency);
    }

    private Bitmap PrepareBitmap(Bitmap source)
    {
        if (source.Width == TargetWidth && source.Height == TargetHeight && source.PixelFormat == PixelFormat.Format32bppArgb)
            return source;

        _scratch ??= new Bitmap(TargetWidth, TargetHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(_scratch))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.DrawImage(source, new Rectangle(0, 0, TargetWidth, TargetHeight));
        }

        return _scratch;
    }

    private void CopyBitmapToBuffer(BitmapData data, byte* dstBase)
    {
        int srcStride = data.Stride;
        byte* srcBase = (byte*)data.Scan0;

        for (int y = 0; y < TargetHeight; y++)
        {
            byte* srcRow = srcStride >= 0 ? srcBase + (y * srcStride) : srcBase + ((TargetHeight - 1 - y) * -srcStride);
            byte* dstRow = dstBase + (y * TargetStride);
            Buffer.MemoryCopy(srcRow, dstRow, TargetStride, TargetStride);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadUInt32(int offset)
    {
        return Unsafe.ReadUnaligned<uint>(_basePtr + offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUInt32(int offset, uint value)
    {
        Unsafe.WriteUnaligned(_basePtr + offset, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUInt64(int offset, ulong value)
    {
        Unsafe.WriteUnaligned(_basePtr + offset, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt64(int offset, long value)
    {
        Unsafe.WriteUnaligned(_basePtr + offset, value);
    }
}
