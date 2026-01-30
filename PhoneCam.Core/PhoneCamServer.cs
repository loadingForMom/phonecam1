using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PhoneCam.Core;

public sealed class PhoneCamServer : IAsyncDisposable
{
    public int ControlPort { get; }
    public int UdpPort { get; }

    public ChannelReader<byte[]> Frames => _frames.Reader;

    public event Action<string>? OnLog;
    public event Action<MediaStatsSnapshot>? OnMediaStats;

    private readonly CancellationTokenSource _cts = new();
    private Task? _tcpTask;
    private Task? _udpTask;
    private Task? _statsTask;

    private readonly Channel<byte[]> _frames;

    public PhoneCamServer(int controlPort = 39000, int udpPort = 39010)
    {
        ControlPort = controlPort;
        UdpPort = udpPort;

        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(60)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest // low-latency MVP
        });
    }

    public void Start()
    {
        Log($"Starting server: TCP {ControlPort}, UDP {UdpPort}");
        var ct = _cts.Token;

        _tcpTask = Task.Run(() => RunTcpControlAsync(ct), ct);
        _udpTask = Task.Run(() => RunUdpMediaAsync(ct), ct);
        _statsTask = Task.Run(() => RunStatsLoopAsync(ct), ct);
    }

    public async Task StopAsync()
    {
        Log("Stopping server...");
        _cts.Cancel();

        try
        {
            if (_tcpTask is not null) await _tcpTask;
            if (_udpTask is not null) await _udpTask;
            if (_statsTask is not null) await _statsTask;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("Stop error: " + ex);
        }

        _frames.Writer.TryComplete();
        Log("Stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    private void Log(string s)
    {
        try
        {
            OnLog?.Invoke(s);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("OnLog failed: " + ex);
        }
    }

    // ---------- TCP CONTROL ----------
    private async Task RunTcpControlAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, ControlPort);
        try
        {
            listener.Start();
            Log($"TCP control listening on 0.0.0.0:{ControlPort}");
        }
        catch (Exception ex)
        {
            Log($"TCP listener failed: {ex}");
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log("TCP accept failed: " + ex);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Log($"TCP client connected: {remote}");

        using (client)
        using (var stream = client.GetStream())
        {
            // простой текстовый протокол: строки \n
            var buf = new byte[4096];
            var sb = new StringBuilder();

        var nonce = Guid.NewGuid().ToString("N")[..8];
        await SendLineAsync(stream, $"CHALLENGE {nonce}", ct);

            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                }
                catch (OperationCanceledException) { break; }

                if (n == 0) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                while (true)
                {
                    var s = sb.ToString();
                    var idx = s.IndexOf('\n');
                    if (idx < 0) break;

                    var line = s[..idx].Trim();
                    sb.Clear();
                    sb.Append(s[(idx + 1)..]);

                    if (line.Length == 0) continue;

                    Log($"TCP {remote}: {line}");

                    if (line.StartsWith("HELLO", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && parts[1] == nonce)
                        {
                            await SendLineAsync(stream, $"ACK {nonce} UDP={UdpPort}", ct);
                        }
                        else
                        {
                            await SendLineAsync(stream, "ERR NONCE", ct);
                        }
                    }
                    else if (line.Equals("PING", StringComparison.OrdinalIgnoreCase))
                        await SendLineAsync(stream, "PONG", ct);
                    else if (line.Equals("START", StringComparison.OrdinalIgnoreCase))
                        await SendLineAsync(stream, "OK START", ct);
                    else if (line.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                        await SendLineAsync(stream, "OK STOP", ct);
                    else
                        await SendLineAsync(stream, "OK", ct);
                }
            }
        }

        Log($"TCP client disconnected: {remote}");
    }

    private static Task SendLineAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        return stream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct).AsTask();
    }

    // ---------- UDP MEDIA ----------
    private readonly MediaStats _stats = new();
    private int _lossAcc; // loss since last snapshot (we also keep total in _stats)

    private async Task RunUdpMediaAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort));
            udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            Log($"UDP media listening on 0.0.0.0:{UdpPort}");
        }
        catch (Exception ex)
        {
            Log("UDP listener failed: " + ex);
            udp?.Dispose();
            return;
        }

        var assembler = new FrameAssembler(Log);

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("UDP receive failed: " + ex);
                continue;
            }

            _stats.OnPacket(res.Buffer.Length);

            if (assembler.TryConsumePacket(res.Buffer, out var accessUnit, out var lossDelta))
            {
                if (lossDelta > 0)
                {
                    _stats.OnLoss(lossDelta);
                    Interlocked.Add(ref _lossAcc, lossDelta);
                }

                // accessUnit: готовый H264 AU (AnnexB или то, что вы собрали)
                _frames.Writer.TryWrite(accessUnit);
            }
        }

        udp.Dispose();
    }

    private async Task RunStatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            var snap = _stats.SnapshotAndUpdate();
            OnMediaStats?.Invoke(snap);
        }
    }
}
