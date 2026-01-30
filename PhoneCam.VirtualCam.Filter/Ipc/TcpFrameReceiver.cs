using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PhoneCam.VirtualCam.Filter.Ipc
{
    internal sealed class TcpFrameReceiver : IDisposable
    {
        private readonly LatestFrameBuffer _buffer;
        private readonly int _expectedPayloadBytes;
        private readonly Action<string> _log;

        private Thread _thread;
        private volatile bool _stop;
        private TcpListener _listener;

        public TcpFrameReceiver(LatestFrameBuffer buffer, int expectedPayloadBytes, Action<string> log)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _expectedPayloadBytes = expectedPayloadBytes;
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "PhoneCam.VirtualCam TCP Receiver"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _listener?.Stop(); } catch { }
            if (_thread != null && !_thread.Join(1500))
            {
                // best effort
            }
            _thread = null;
        }

        private void ThreadMain()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, FrameProtocol.Port);
                _listener.Server.NoDelay = true;
                _listener.Start();
                _log("TCP receiver listening on 127.0.0.1:" + FrameProtocol.Port);
            }
            catch (Exception ex)
            {
                _log("TCP receiver failed to start: " + ex.Message);
                return;
            }

            var prefix = new byte[FrameProtocol.PrefixBytes];
            var header = new byte[FrameProtocol.HeaderBytes];

            while (!_stop)
            {
                TcpClient client = null;
                try
                {
                    // Accept with a timeout to respect Stop()
                    var ar = _listener.BeginAcceptTcpClient(null, null);
                    while (!_stop && !ar.AsyncWaitHandle.WaitOne(250))
                    {
                        // wait
                    }
                    if (_stop) break;

                    client = _listener.EndAcceptTcpClient(ar);
                    client.NoDelay = true;
                    _log("TCP client connected");

                    using (client)
                    using (var stream = client.GetStream())
                    {
                        while (!_stop && client.Connected)
                        {
                            if (!ReadExact(stream, prefix, 0, 4)) break;
                            int messageLen = ReadInt32LE(prefix, 0);

                            if (messageLen < FrameProtocol.HeaderBytes || messageLen > (64 * 1024 * 1024))
                                break;

                            if (!ReadExact(stream, header, 0, FrameProtocol.HeaderBytes)) break;

                            if (!FrameProtocol.TryParseHeader(header, out var h))
                                break;

                            int payloadLen = h.PayloadLengthBytes;
                            if (payloadLen != messageLen - FrameProtocol.HeaderBytes)
                                break;

                            if (payloadLen <= 0 || payloadLen > (64 * 1024 * 1024))
                                break;

                            // Acquire write buffer and read payload directly into it.
                            int writeIndex;
                            var dst = _buffer.AcquireWriteBuffer(payloadLen, out writeIndex);

                            if (!ReadExact(stream, dst, 0, payloadLen))
                                break;

                            // Validate expected format. If mismatch, drop the frame (but continue).
                            if (h.PixelFormat == FrameProtocol.PixelFormatBgr24 && payloadLen == _expectedPayloadBytes)
                            {
                                _buffer.Commit(writeIndex, payloadLen, h.Width, h.Height, h.StrideBytes, h.PixelFormat, h.TimestampUtcTicks);
                            }
                            // else ignore
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log("TCP receiver error: " + ex.Message);
                }
                finally
                {
                    try { client?.Close(); } catch { }
                    if (!_stop)
                    {
                        Thread.Sleep(200);
                    }
                }
            }
        }

        private static bool ReadExact(NetworkStream stream, byte[] buf, int offset, int len)
        {
            int read;
            int total = 0;
            while (total < len)
            {
                read = stream.Read(buf, offset + total, len - total);
                if (read <= 0) return false;
                total += read;
            }
            return true;
        }

        private static int ReadInt32LE(byte[] b, int o)
        {
            unchecked
            {
                return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
