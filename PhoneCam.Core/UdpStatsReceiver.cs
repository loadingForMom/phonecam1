using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneCam.Core;

public readonly record struct UdpStatsSnapshot(
    DateTime TimestampUtc,
    long TotalPackets,
    long TotalBytes,
    double PacketsPerSec,
    double BytesPerSec
);

internal sealed class UdpStatsReceiver
{
    public event Action<string>? OnLog;
    public event Action<UdpStatsSnapshot>? OnStats;

    private readonly int _port;

    private long _totalPackets;
    private long _totalBytes;

    public UdpStatsReceiver(int port) => _port = port;

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;

        try
        {
            OnLog?.Invoke($"UDP stats listening on 0.0.0.0:{_port}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("UDP stats OnLog failed: " + ex);
        }

        var lastTs = DateTime.UtcNow;
        long lastPackets = 0;
        long lastBytes = 0;

        // отдельный таймер-тик раз в 1 сек для снапшотов
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                var now = DateTime.UtcNow;

                var tp = Interlocked.Read(ref _totalPackets);
                var tb = Interlocked.Read(ref _totalBytes);

                var dt = (now - lastTs).TotalSeconds;
                if (dt <= 0) dt = 1;

                var pps = (tp - lastPackets) / dt;
                var bps = (tb - lastBytes) / dt;

                lastTs = now;
                lastPackets = tp;
                lastBytes = tb;

                OnStats?.Invoke(new UdpStatsSnapshot(now, tp, tb, pps, bps));
            }
        }, ct);

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

            Interlocked.Increment(ref _totalPackets);
            Interlocked.Add(ref _totalBytes, res.Buffer.Length);
        }
    }
}
