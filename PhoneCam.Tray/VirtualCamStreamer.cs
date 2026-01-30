using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace PhoneCam.Tray;

/// <summary>
/// Streams decoded frames to the local DirectShow virtual camera filter.
///
/// Transport: TCP 127.0.0.1:51111
/// Protocol (little-endian):
///   int32 messageLengthBytes (header + payload)
///   byte[32] header
///     0..3   magic 'P''C''A''M'
///     4..5   uint16 version (1)
///     6..7   uint16 pixelFormat (1 = BGR24)
///     8..11  int32 width
///     12..15 int32 height
///     16..19 int32 strideBytes (tight packed = width*3)
///     20..27 int64 timestampUtcTicks (DateTime.UtcNow.Ticks)
///     28..31 int32 payloadLengthBytes
///   byte[payloadLengthBytes] payload (BGR24, tight-packed)
/// </summary>
public sealed class VirtualCamStreamer : IDisposable
{
    // Keep this in sync with the filter-side receiver.
    public const int Port = 51111;

    public const int TargetWidth = 1280;
    public const int TargetHeight = 720;
    public const int BytesPerPixel = 3;
    public const int TargetStride = TargetWidth * BytesPerPixel;
    public const int TargetFrameBytes = TargetStride * TargetHeight;

    private const ushort ProtocolVersion = 1;
    private const ushort PixelFormatBgr24 = 1;
    private const int HeaderBytes = 32;

    private readonly Action<string> _log;
    private readonly object _sync = new();

    private volatile bool _enabled;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private long _nextConnectAttemptTickMs;

    private readonly byte[] _prefix = new byte[4];
    private readonly byte[] _header = new byte[HeaderBytes];
    private byte[] _payload = new byte[TargetFrameBytes];

    private Bitmap? _scratch;

    public VirtualCamStreamer(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        InitHeader();
    }

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;

            if (!enabled)
            {
                CloseConnection_NoLock();
                _log("VirtualCam: disabled");
            }
            else
            {
                _nextConnectAttemptTickMs = 0;
                _log($"VirtualCam: enabled (tcp://127.0.0.1:{Port})");
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            CloseConnection_NoLock();
            _scratch?.Dispose();
            _scratch = null;
        }
    }

    /// <summary>
    /// Best-effort send of the current frame. Never throws.
    /// </summary>
    public void TrySendFrame(Bitmap decoded)
    {
        if (!_enabled || decoded is null) return;

        try
        {
            NetworkStream? stream;
            lock (_sync)
            {
                if (!_enabled) return;
                if (!EnsureConnected_NoLock()) return;
                stream = _stream;
            }

            if (stream is null) return;

            var src = PrepareSourceBitmap(decoded);
            CopyBitmapToPayload(src);

            WriteInt32LE(_header, 8, TargetWidth);
            WriteInt32LE(_header, 12, TargetHeight);
            WriteInt32LE(_header, 16, TargetStride);
            WriteInt64LE(_header, 20, DateTime.UtcNow.Ticks);
            WriteInt32LE(_header, 28, TargetFrameBytes);

            int messageLen = HeaderBytes + TargetFrameBytes;
            WriteInt32LE(_prefix, 0, messageLen);

            stream.Write(_prefix, 0, 4);
            stream.Write(_header, 0, HeaderBytes);
            stream.Write(_payload, 0, TargetFrameBytes);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                CloseConnection_NoLock();
            }
            _log("VirtualCam: send failed: " + ex.Message);
        }
    }

    private void InitHeader()
    {
        _header[0] = (byte)'P';
        _header[1] = (byte)'C';
        _header[2] = (byte)'A';
        _header[3] = (byte)'M';

        WriteUInt16LE(_header, 4, ProtocolVersion);
        WriteUInt16LE(_header, 6, PixelFormatBgr24);
    }

    private bool EnsureConnected_NoLock()
    {
        if (_client is { Connected: true } && _stream != null)
            return true;

        long nowMs = Environment.TickCount64;
        if (nowMs < _nextConnectAttemptTickMs)
            return false;

        _nextConnectAttemptTickMs = nowMs + 1000;

        try
        {
            CloseConnection_NoLock();

            var client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true,
                SendBufferSize = 1 << 20,
            };

            var ar = client.BeginConnect(IPAddress.Loopback, Port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(250))
            {
                client.Close();
                return false;
            }
            client.EndConnect(ar);

            _client = client;
            _stream = client.GetStream();

            _log("VirtualCam: connected");
            return true;
        }
        catch
        {
            CloseConnection_NoLock();
            return false;
        }
    }

    private void CloseConnection_NoLock()
    {
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }

    private Bitmap PrepareSourceBitmap(Bitmap decoded)
    {
        // Fast path: already in the target format.
        if (decoded.Width == TargetWidth && decoded.Height == TargetHeight && decoded.PixelFormat == PixelFormat.Format24bppRgb)
            return decoded;

        _scratch ??= new Bitmap(TargetWidth, TargetHeight, PixelFormat.Format24bppRgb);

        using (var g = Graphics.FromImage(_scratch))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.Low;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.SmoothingMode = SmoothingMode.None;

            g.DrawImage(decoded, new Rectangle(0, 0, TargetWidth, TargetHeight));
        }

        return _scratch;
    }

    private unsafe void CopyBitmapToPayload(Bitmap src)
    {
        // Ensure payload buffer is present (future-proof if TargetFrameBytes changes).
        if (_payload.Length != TargetFrameBytes)
            _payload = new byte[TargetFrameBytes];

        var rect = new Rectangle(0, 0, TargetWidth, TargetHeight);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int srcStride = data.Stride;
            int dstStride = TargetStride;

            byte* srcBase = (byte*)data.Scan0;
            fixed (byte* dstBase = _payload)
            {
                if (srcStride == dstStride)
                {
                    Buffer.MemoryCopy(srcBase, dstBase, TargetFrameBytes, TargetFrameBytes);
                }
                else
                {
                    // Copy line-by-line into a tightly packed payload.
                    for (int y = 0; y < TargetHeight; y++)
                    {
                        Buffer.MemoryCopy(srcBase + (y * srcStride), dstBase + (y * dstStride), dstStride, dstStride);
                    }
                }
            }
        }
        finally
        {
            src.UnlockBits(data);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt16LE(byte[] buf, int offset, ushort value)
    {
        buf[offset + 0] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt32LE(byte[] buf, int offset, int value)
    {
        unchecked
        {
            buf[offset + 0] = (byte)(value);
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt64LE(byte[] buf, int offset, long value)
    {
        unchecked
        {
            buf[offset + 0] = (byte)(value);
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
            buf[offset + 4] = (byte)(value >> 32);
            buf[offset + 5] = (byte)(value >> 40);
            buf[offset + 6] = (byte)(value >> 48);
            buf[offset + 7] = (byte)(value >> 56);
        }
    }
}
