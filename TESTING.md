# PhoneCam MVP Testing

## Network Baseline
### Ping / jitter
```bash
ping -n 20 <phone_or_laptop_ip>
```

### iPerf3 throughput
Laptop as server:
```bash
iperf3 -s
```

Phone as client:
```bash
iperf3 -c <laptop_ip> -t 20
```

UDP loss test:
```bash
iperf3 -c <laptop_ip> -u -b 10M -t 20
```

## Streaming Tests
1. Start Windows tray app, click **Start** and **Show Video**.
2. On Android, enter laptop IP and click **Start stream**.
3. Observe:
   - FPS (target ~30)
   - bitrate in kbps
   - packet loss (tray log)
   - preview on phone

### Metrics to capture
- End‑to‑end latency (timestamp overlay or clap test)
- FPS stability over 60s
- Bitrate stability and drops
- UDP loss/out‑of‑order

## Troubleshooting Checklist
- Ensure laptop/phone are on same subnet (192.168.137.x or 192.168.1.x).
- Check Windows firewall for UDP 39010 / TCP 39000.
- Verify phone has camera + mic permission + notifications enabled.
- If FFmpeg download fails, confirm internet access and retry launch.
- If no video: ensure H.264 is Annex‑B (start codes) and SPS/PPS are sent at start.
