using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace PhoneCam.VirtualCam.Filter.Ipc
{
    internal sealed class IpcFrameReceiver : IDisposable
    {
        // Frame protocol (little-endian):
        //   int32 byteLength
        //   byte[byteLength] framePayload (expected fixed size for RGB24 1280x720)
        //
        // Notes:
        // - This is intentionally minimal; adapt it to your app's IPC protocol as needed.
        // - If the producer sends a different size, the receiver will ignore the frame.

        public const string DefaultPipeName = "PhoneCam.VirtualCam.FramePipe";

        private readonly FrameQueue _queue;
        private readonly int _expectedFrameSize;
        private readonly string _pipeName;

        private Thread _thread;
        private volatile bool _stop;

        public IpcFrameReceiver(FrameQueue queue, int expectedFrameSize, string pipeName = null)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _expectedFrameSize = expectedFrameSize;
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "PhoneCam.VirtualCam IPC Receiver"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            // The server pipe can block in WaitForConnection, so we rely on timeout + loop.
            if (_thread != null && !_thread.Join(1500))
            {
                // best effort
            }
            _thread = null;
        }

        private void ThreadMain()
        {
            while (!_stop)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous))
                    {
                        // Polling-style connection to allow Stop().
                        var ar = pipe.BeginWaitForConnection(null, null);
                        while (!_stop && !ar.AsyncWaitHandle.WaitOne(250))
                        {
                            // wait
                        }
                        if (_stop) return;

                        pipe.EndWaitForConnection(ar);

                        using (var br = new BinaryReader(pipe))
                        {
                            while (!_stop && pipe.IsConnected)
                            {
                                int length;
                                try
                                {
                                    length = br.ReadInt32();
                                }
                                catch (EndOfStreamException)
                                {
                                    break;
                                }

                                if (length <= 0 || length > (16 * 1024 * 1024))
                                    break;

                                var payload = br.ReadBytes(length);
                                if (payload.Length != length)
                                    break;

                                if (payload.Length == _expectedFrameSize)
                                    _queue.Enqueue(payload);
                                // else ignore
                            }
                        }
                    }
                }
                catch
                {
                    // Back off a bit on repeated failures.
                    Thread.Sleep(200);
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}