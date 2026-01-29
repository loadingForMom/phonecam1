using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneCam.Tray
{
    internal sealed class VirtualCamPipeServer : IAsyncDisposable
    {
        // Protocol (byte stream, little-endian):
        // Client -> Server:
        //   1 byte: cmd
        //     0x01 = GetLatestFrame
        //
        // Server -> Client response (for cmd 0x01):
        //   4 bytes: width   (int32)
        //   4 bytes: height  (int32)
        //   4 bytes: stride  (int32)
        //   4 bytes: length  (int32) payload length in bytes
        //   N bytes: payload raw BGR24 bytes (stride * height), or 0 bytes if no frame
        private const byte CmdGetLatestFrame = 0x01;

        private readonly string _pipeName;
        private readonly VirtualCamFrameHub _frameHub;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _acceptLoopTask;
        private int _started;

        public VirtualCamPipeServer(string pipeName, VirtualCamFrameHub frameHub)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("Pipe name must be non-empty.", nameof(pipeName));
            _pipeName = pipeName;
            _frameHub = frameHub ?? throw new ArgumentNullException(nameof(frameHub));
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref _started, 0) == 0)
                return;

            try
            {
                _cts.Cancel();
            }
            catch
            {
                // ignore
            }

            var t = _acceptLoopTask;
            if (t != null)
            {
                try { await t.ConfigureAwait(false); }
                catch { /* ignore */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = CreateServerStream();
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    _ = Task.Run(() => ClientLoopAsync(pipe, ct), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    try { pipe?.Dispose(); } catch { /* ignore */ }
                    break;
                }
                catch
                {
                    try { pipe?.Dispose(); } catch { /* ignore */ }
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
        }

        private NamedPipeServerStream CreateServerStream()
        {
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        }

        private async Task ClientLoopAsync(NamedPipeServerStream pipe, CancellationToken serverCt)
        {
            using (pipe)
            {
                var ct = serverCt;
                var oneByte = new byte[1];

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    try
                    {
                        var read = await ReadExactAsync(pipe, oneByte, 0, 1, ct).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        var cmd = oneByte[0];
                        if (cmd == CmdGetLatestFrame)
                        {
                            await WriteLatestFrameAsync(pipe, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            // Unknown cmd: close connection
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        private async Task WriteLatestFrameAsync(Stream stream, CancellationToken ct)
        {
            // VirtualCamFrameHub already stores a tightly-packed BGR24 buffer.
            // Acquire it via a reference-counted lease to avoid extra allocations/copies.
            var lease = _frameHub.AcquireLatestFrameLease();
            if (lease is null || lease.Length <= 0)
            {
                Span<byte> header = stackalloc byte[16];
                header.Clear();

                byte[] hdr = ArrayPool<byte>.Shared.Rent(16);
                try
                {
                    header.CopyTo(hdr);
                    await stream.WriteAsync(hdr, 0, 16, ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(hdr);
                }

                await stream.FlushAsync(ct).ConfigureAwait(false);
                return;
            }

            using (lease)
            {
                int width = lease.Width;
                int height = lease.Height;
                int stride = checked(width * 3); // BGR24 tightly-packed
                int length = lease.Length;

                Span<byte> header = stackalloc byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(0, 4), width);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), height);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), stride);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), length);

                byte[] hdr = ArrayPool<byte>.Shared.Rent(16);
                try
                {
                    header.CopyTo(hdr);
                    await stream.WriteAsync(hdr, 0, 16, ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(hdr);
                }

                // Write payload
                await stream.WriteAsync(lease.Data, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        private static async Task<int> ReadExactAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await s.ReadAsync(buffer, offset + total, count - total, ct).ConfigureAwait(false);
                if (n == 0)
                    return 0;
                total += n;
            }
            return total;
        }
    }
}
