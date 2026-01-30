using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneCam.Tray;

/// <summary>
/// Tray-hosted IPC server that exposes the latest decoded video frame as tightly packed BGR24 bytes.
/// Multiple clients may connect and pull the latest frame on demand.
///
/// Protocol (request/response):
///  - Client sends 1 byte command: 0x01 = GetLatest
///  - Server replies:
///      [4]  Magic 'P','C','F','H'
///      [4]  Version (uint32, currently 1)
///      [4]  Width (uint32)
///      [4]  Height (uint32)
///      [4]  PixelFormat (uint32) 1 = BGR24 tightly packed
///      [8]  FrameId (int64)
///      [8]  TimestampUtcTicks (int64)
///      [4]  PayloadLength (uint32) = width*height*3 or 0 if no frame yet
///      [N]  Payload bytes (BGR24)
/// </summary>
public sealed class VirtualCamFrameHub : IDisposable
{
    public enum TransportKind
    {
        NamedPipe,
        TcpLoopback
    }

    public const string DefaultPipeName = "PhoneCam.VirtualCamFrameHub";
    public const int DefaultTcpPort = 39876;

    private const uint PixelFormatBgr24 = 1;
    private static readonly byte[] Magic = { (byte)'P', (byte)'C', (byte)'F', (byte)'H' };
    private const uint ProtocolVersion = 1;

    private readonly Action<string>? _log;
    private readonly string _pipeName;
    private readonly int _tcpPort;
    private readonly TransportKind _transport;

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _statsTask;
    private TcpListener? _tcp;

    private volatile FrameBuffer? _latest;

    // Counters
    private long _framesCaptured;
    private long _bytesCaptured;
    private long _requests;
    private long _framesServed;
    private long _bytesServed;
    private long _clientsAccepted;
    private long _clientsActive;
    private long _errors;

    public VirtualCamFrameHub(
        Action<string>? log,
        string? pipeName = null,
        TransportKind transport = TransportKind.NamedPipe,
        int tcpPort = DefaultTcpPort)
    {
        _log = log;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
        _transport = transport;
        _tcpPort = tcpPort;
    }

    public bool IsRunning => _cts is not null;

    public (long FramesCaptured, long FramesServed, long ClientsActive, long Requests, long Errors) GetCounters()
        => (Volatile.Read(ref _framesCaptured), Volatile.Read(ref _framesServed), Volatile.Read(ref _clientsActive),
            Volatile.Read(ref _requests), Volatile.Read(ref _errors));

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _acceptTask = _transport switch
        {
            TransportKind.NamedPipe => Task.Run(() => PipeAcceptLoopAsync(ct), ct),
            TransportKind.TcpLoopback => Task.Run(() => TcpAcceptLoopAsync(ct), ct),
            _ => throw new ArgumentOutOfRangeException()
        };

        _statsTask = Task.Run(() => StatsLoopAsync(ct), ct);
        Log($"VHub: started ({_transport}) pipe='{_pipeName}' tcpPort={_tcpPort}");
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts is null) return;
        _cts = null;

        try
        {
            cts.Cancel();
        }
        catch { /* ignore */ }

        try
        {
            _tcp?.Stop();
        }
        catch { /* ignore */ }
        _tcp = null;

        // Release latest frame buffer
        try
        {
            var prev = Interlocked.Exchange(ref _latest, null);
            prev?.Release();
        }
        catch { /* ignore */ }

        Log("VHub: stopping");
    }

    public void Dispose()
    {
        Stop();
        try { _acceptTask?.Dispose(); } catch { /* ignore */ }
        try { _statsTask?.Dispose(); } catch { /* ignore */ }
        _acceptTask = null;
        _statsTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Update latest frame from a decoded System.Drawing.Bitmap.
    /// Assumes PixelFormat.Format24bppRgb (BGR24 in memory). Will log and ignore other formats.
    /// </summary>
    public void UpdateFromBitmap(Bitmap bmp)
    {
        if (_cts is null) return;
        if (bmp.PixelFormat != PixelFormat.Format24bppRgb)
        {
            Log($"VHub: ignoring frame with PixelFormat={bmp.PixelFormat}");
            return;
        }

        var w = bmp.Width;
        var h = bmp.Height;
        if (w <= 0 || h <= 0) return;

        var rowBytes = checked(w * 3);
        var payloadBytes = checked(rowBytes * h);

        var frameId = Interlocked.Increment(ref _framesCaptured);
        var tsTicks = DateTime.UtcNow.Ticks;

        // Rent a buffer for this frame; it remains valid until replaced and all readers release it.
        var data = ArrayPool<byte>.Shared.Rent(payloadBytes);

        try
        {
            var rect = new Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                CopyToTightlyPackedBgr24(bd, data, payloadBytes, rowBytes, h);
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            Interlocked.Add(ref _bytesCaptured, payloadBytes);

            var next = new FrameBuffer(data, payloadBytes, w, h, frameId, tsTicks);
            data = null!; // ownership transferred

            var prev = Interlocked.Exchange(ref _latest, next);
            prev?.Release();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log("VHub: UpdateFromBitmap failed: " + ex.Message);
        }
        finally
        {
            // If we didn't transfer ownership to FrameBuffer, return to pool.
            if (data is not null)
            {
                try { ArrayPool<byte>.Shared.Return(data); } catch { /* ignore */ }
            }
        }
    }

    private static unsafe void CopyToTightlyPackedBgr24(BitmapData bd, byte[] dst, int dstLen, int rowBytes, int height)
    {
        if (dstLen < rowBytes * height) throw new ArgumentOutOfRangeException(nameof(dstLen));

        var stride = bd.Stride;
        var srcBase = (byte*)bd.Scan0;

        fixed (byte* dstBase = dst)
        {
            for (var y = 0; y < height; y++)
            {
                byte* srcRow = stride >= 0
                    ? srcBase + (nint)y * stride
                    : srcBase + (nint)(height - 1 - y) * (-stride);
                byte* dstRow = dstBase + (nint)y * rowBytes;
                Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
            }
        }
    }

    private async Task PipeAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                Interlocked.Increment(ref _clientsAccepted);
                Interlocked.Increment(ref _clientsActive);
                Log($"VHub: client connected (pipe), active={Volatile.Read(ref _clientsActive)}");

                _ = Task.Run(() => HandleStreamClientAsync(pipe, isPipe: true, ct), ct);
                pipe = null; // owned by handler
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                Log("VHub: pipe accept failed: " + ex.Message);
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { /* ignore */ }
            }
            finally
            {
                pipe?.Dispose();
            }
        }

        Log("VHub: pipe accept loop stopped");
    }

    private async Task TcpAcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            _tcp = new TcpListener(IPAddress.Loopback, _tcpPort);
            _tcp.Start();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log("VHub: TCP listener start failed: " + ex.Message);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;

                Interlocked.Increment(ref _clientsAccepted);
                Interlocked.Increment(ref _clientsActive);
                Log($"VHub: client connected (tcp), active={Volatile.Read(ref _clientsActive)}");

                _ = Task.Run(() => HandleStreamClientAsync(client.GetStream(), isPipe: false, ct, client), ct);
                client = null; // owned by handler
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                Log("VHub: TCP accept failed: " + ex.Message);
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { /* ignore */ }
            }
            finally
            {
                try { client?.Dispose(); } catch { /* ignore */ }
            }
        }

        Log("VHub: TCP accept loop stopped");
    }

    private async Task HandleStreamClientAsync(Stream stream, bool isPipe, CancellationToken ct, TcpClient? tcpClient = null)
    {
        try
        {
            // Small request loop: clients send command bytes; we respond with header+payload.
            var req = new byte[1];
            var header = new byte[40];

            while (!ct.IsCancellationRequested)
            {
                var n = await ReadExactOrEofAsync(stream, req, 0, 1, ct).ConfigureAwait(false);
                if (n == 0) break;

                if (req[0] != 0x01)
                {
                    // Unknown command: ignore
                    continue;
                }

                Interlocked.Increment(ref _requests);

                FrameBuffer? fb = null;
                try
                {
                    fb = AcquireLatestFrame();

                    uint w = 0, h = 0;
                    long frameId = 0;
                    long tsTicks = 0;
                    uint payloadLen = 0;

                    if (fb is not null)
                    {
                        w = (uint)fb.Width;
                        h = (uint)fb.Height;
                        frameId = fb.FrameId;
                        tsTicks = fb.TimestampUtcTicks;
                        payloadLen = (uint)fb.Length;
                    }

                    // Build header
                    Magic.CopyTo(header, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), ProtocolVersion);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), w);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), h);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), PixelFormatBgr24);
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20, 8), frameId);
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28, 8), tsTicks);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), payloadLen);

                    await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
                    if (fb is not null && payloadLen > 0)
                    {
                        await stream.WriteAsync(fb.Data, 0, fb.Length, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _framesServed);
                        Interlocked.Add(ref _bytesServed, fb.Length);
                    }

                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    fb?.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (IOException)
        {
            // client disconnect or broken pipe
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log("VHub: client handler error: " + ex.Message);
        }
        finally
        {
            try { stream.Dispose(); } catch { /* ignore */ }
            try { tcpClient?.Dispose(); } catch { /* ignore */ }

            Interlocked.Decrement(ref _clientsActive);
            Log($"VHub: client disconnected ({(isPipe ? "pipe" : "tcp")}), active={Volatile.Read(ref _clientsActive)}");
        }
    }

    private FrameBuffer? AcquireLatestFrame()
    {
        while (true)
        {
            var fb = Volatile.Read(ref _latest);
            if (fb is null) return null;
            if (fb.TryAddRef()) return fb;
            // It was released between read and AddRef; retry.
        }
    }

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastFrames = 0;
        long lastServed = 0;
        long lastBytes = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            var frames = Volatile.Read(ref _framesCaptured);
            var served = Volatile.Read(ref _framesServed);
            var bytes = Volatile.Read(ref _bytesServed);
            var active = Volatile.Read(ref _clientsActive);
            var req = Volatile.Read(ref _requests);
            var err = Volatile.Read(ref _errors);

            var dt = sw.Elapsed.TotalSeconds;
            sw.Restart();

            var fpsIn = (frames - lastFrames) / Math.Max(0.001, dt);
            var fpsOut = (served - lastServed) / Math.Max(0.001, dt);
            var kbpsOut = ((bytes - lastBytes) * 8.0 / 1000.0) / Math.Max(0.001, dt);
            lastFrames = frames;
            lastServed = served;
            lastBytes = bytes;

            var latest = Volatile.Read(ref _latest);
            if (latest is not null)
            {
                Log($"VHub: in={fpsIn:F1} fps, out={fpsOut:F1} fps, out={kbpsOut:F0} kbps, active={active}, req={req}, err={err}, last={latest.Width}x{latest.Height} id={latest.FrameId}");
            }
            else
            {
                Log($"VHub: in={fpsIn:F1} fps, out={fpsOut:F1} fps, out={kbpsOut:F0} kbps, active={active}, req={req}, err={err}, last=<none>");
            }
        }
    }

    private void Log(string msg)
    {
        try { _log?.Invoke(msg); } catch { /* ignore */ }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static async Task<int> ReadExactOrEofAsync(Stream s, byte[] buf, int offset, int count, CancellationToken ct)
    {
        var readTotal = 0;
        while (readTotal < count)
        {
            var n = await s.ReadAsync(buf, offset + readTotal, count - readTotal, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return readTotal == 0 ? 0 : readTotal;
            }
            readTotal += n;
        }
        return readTotal;
    }

    private sealed class FrameBuffer
    {
        public readonly byte[] Data;
        public readonly int Length;
        public readonly int Width;
        public readonly int Height;
        public readonly long FrameId;
        public readonly long TimestampUtcTicks;

        private int _refCount;

        public FrameBuffer(byte[] data, int length, int width, int height, long frameId, long timestampUtcTicks)
        {
            Data = data;
            Length = length;
            Width = width;
            Height = height;
            FrameId = frameId;
            TimestampUtcTicks = timestampUtcTicks;
            _refCount = 1; // owned by hub
        }

        public bool TryAddRef()
        {
            while (true)
            {
                var c = Volatile.Read(ref _refCount);
                if (c == 0) return false;
                if (Interlocked.CompareExchange(ref _refCount, c + 1, c) == c) return true;
            }
        }

        public void Release()
        {
            var c = Interlocked.Decrement(ref _refCount);
            if (c == 0)
            {
                try { ArrayPool<byte>.Shared.Return(Data); } catch { /* ignore */ }
            }
        }
    }
}
