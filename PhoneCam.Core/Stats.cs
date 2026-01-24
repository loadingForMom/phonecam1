using System;

namespace PhoneCam.Core;

public readonly record struct MediaStatsSnapshot(
    DateTime TimestampUtc,
    long TotalPackets,
    long TotalBytes,
    long TotalLoss,
    double PacketsPerSec,
    double BytesPerSec,
    double LossPerSec
);

public sealed class MediaStats
{
    private long _totalPackets;
    private long _totalBytes;
    private long _totalLoss;

    private DateTime _lastTs = DateTime.UtcNow;
    private long _lastPackets;
    private long _lastBytes;
    private long _lastLoss;

    public void OnPacket(int bytes)
    {
        _totalPackets++;
        _totalBytes += bytes;
    }

    public void OnLoss(int lossDelta)
    {
        if (lossDelta > 0) _totalLoss += lossDelta;
    }

    public MediaStatsSnapshot SnapshotAndUpdate()
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastTs).TotalSeconds;
        if (dt <= 0) dt = 1;

        var tp = _totalPackets;
        var tb = _totalBytes;
        var tl = _totalLoss;

        var pps = (tp - _lastPackets) / dt;
        var bps = (tb - _lastBytes) / dt;
        var lps = (tl - _lastLoss) / dt;

        _lastTs = now;
        _lastPackets = tp;
        _lastBytes = tb;
        _lastLoss = tl;

        return new MediaStatsSnapshot(
            TimestampUtc: now,
            TotalPackets: tp,
            TotalBytes: tb,
            TotalLoss: tl,
            PacketsPerSec: pps,
            BytesPerSec: bps,
            LossPerSec: lps
        );
    }
}