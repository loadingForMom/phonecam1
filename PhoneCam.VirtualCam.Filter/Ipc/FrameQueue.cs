using System;
using System.Collections.Concurrent;
using System.Threading;

namespace PhoneCam.VirtualCam.Filter.Ipc
{
    internal sealed class FrameQueue
    {
        private readonly ConcurrentQueue<byte[]> _queue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly int _maxFrames;

        public FrameQueue(int maxFrames)
        {
            _maxFrames = Math.Max(1, maxFrames);
        }

        public int Count => _queue.Count;

        public void Enqueue(byte[] frameBgr24)
        {
            if (frameBgr24 == null) throw new ArgumentNullException(nameof(frameBgr24));

            // Simple bounded behavior: if too many frames, drop oldest.
            while (_queue.Count >= _maxFrames && _queue.TryDequeue(out _))
            {
                // dropped
            }

            _queue.Enqueue(frameBgr24);
            _signal.Release();
        }

        public bool TryDequeue(int timeoutMs, out byte[] frame)
        {
            frame = null;
            if (!_signal.Wait(timeoutMs))
                return false;

            return _queue.TryDequeue(out frame);
        }

        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
            // Drain any outstanding signals.
            while (_signal.Wait(0)) { }
        }
    }
}