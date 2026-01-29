using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
            // IMPORTANT: We avoid per-frame heap allocations:
            // - Header is stackalloc'd
            // - Pixel buffer is rented from ArrayPool
            Bitmap? bmp = null;
            try
            {
                // Expected API: VirtualCamFrameHub provides latest Bitmap (BGR24).
                // If your hub method/property name differs, update ONLY this line.
                bmp = _frameHub.GetLatestFrame();
            }
            catch
            {
                bmp = null;
            }

            if (bmp == null)
            {
                Span<byte> header = stackalloc byte[16];
                header.Clear();
                await stream.WriteAsync(header.ToArray(), 0, header.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return;
            }

            BitmapData? data = null;
            byte[]? rented = null;
            int width = 0, height = 0, stride = 0, length = 0;

            try
            {
                width = bmp.Width;
                height = bmp.Height;

                // Ensure BGR24
                data = bmp.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);

                stride = data.Stride;
                length = checked(Math.Abs(stride) * height);

                rented = ArrayPool<byte>.Shared.Rent(length);
                IntPtr src = data.Scan0;

                // Copy row-by-row in case of negative stride
                if (stride > 0)
                {
                    Marshal.Copy(src, rented, 0, length);
                }
                else
                {
                    // bottom-up
                    int absStride = -stride;
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr rowPtr = IntPtr.Add(src, y * absStride);
                        Marshal.Copy(rowPtr, rented, y * absStride, absStride);
                    }
                    stride = absStride;
                    length = checked(stride * height);
                }

                Span<byte> header = stackalloc byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(0, 4), width);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), height);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), stride);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), length);

                // Write header (stack) without allocating: use small pooled buffer
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

                await stream.WriteAsync(rented, 0, length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (data != null)
                {
                    try { bmp.UnlockBits(data); } catch { /* ignore */ }
                }
                if (rented != null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
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
