using System;
using System.Threading;

namespace PhoneCam.VirtualCam.Filter.Ipc
{
    /// <summary>
    /// Lock-free latest-frame buffer with double buffering.
    /// Writer writes into the inactive buffer and swaps the active index atomically.
    /// Reader always sees a consistent buffer pointer.
    /// </summary>
    internal sealed class LatestFrameBuffer
    {
        private readonly object _resizeLock = new object();
        private readonly byte[][] _buffers = new byte[2][];

        private volatile int _activeIndex;
        private volatile int _length;
        private volatile bool _hasFrame;

        // Optional metadata (useful for debugging)
        private volatile int _width;
        private volatile int _height;
        private volatile int _stride;
        private volatile ushort _pixelFormat;
        private long _timestampUtcTicks;

        public LatestFrameBuffer(int initialCapacityBytes)
        {
            if (initialCapacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapacityBytes));

            _buffers[0] = new byte[initialCapacityBytes];
            _buffers[1] = new byte[initialCapacityBytes];
            _activeIndex = 0;
            _length = 0;
            _hasFrame = false;
        }

        public bool HasFrame => _hasFrame;

        public bool TryGetReadBuffer(out byte[] buffer, out int length)
        {
            if (!_hasFrame)
            {
                buffer = null;
                length = 0;
                return false;
            }

            int idx = Volatile.Read(ref _activeIndex);
            buffer = _buffers[idx];
            length = Volatile.Read(ref _length);
            return buffer != null && length > 0;
        }

        public byte[] AcquireWriteBuffer(int requiredBytes, out int writeIndex)
        {
            if (requiredBytes <= 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));

            int active = Volatile.Read(ref _activeIndex);
            writeIndex = 1 - active;
            var buf = _buffers[writeIndex];
            if (buf == null || buf.Length < requiredBytes)
            {
                lock (_resizeLock)
                {
                    buf = _buffers[writeIndex];
                    if (buf == null || buf.Length < requiredBytes)
                    {
                        _buffers[writeIndex] = buf = new byte[requiredBytes];
                    }
                }
            }
            return buf;
        }

        public void Commit(int writeIndex, int length, int width, int height, int strideBytes, ushort pixelFormat, long timestampUtcTicks)
        {
            if (writeIndex != 0 && writeIndex != 1) throw new ArgumentOutOfRangeException(nameof(writeIndex));

            // Publish metadata first, then publish index last (release).
            Volatile.Write(ref _length, length);
            Volatile.Write(ref _width, width);
            Volatile.Write(ref _height, height);
            Volatile.Write(ref _stride, strideBytes);
            Volatile.Write(ref _pixelFormat, pixelFormat);
            Interlocked.Exchange(ref _timestampUtcTicks, timestampUtcTicks);

            Volatile.Write(ref _activeIndex, writeIndex);
        }

        public (int Width, int Height, int StrideBytes, ushort PixelFormat, long TimestampUtcTicks) GetLastMeta()
        {
            return (
                Volatile.Read(ref _width),
                Volatile.Read(ref _height),
                Volatile.Read(ref _stride),
                Volatile.Read(ref _pixelFormat),
                Interlocked.Read(ref _timestampUtcTicks)
            );
        }
    }
}
