# PhoneCam MVP Protocol

## Ports
- TCP control: **39000**
- UDP video: **39010**
- UDP audio: **39012** (reserved for later)

## Transport Overview
- **TCP control** for handshake + configuration.
- **UDP media** for H.264 video access units (Annex‑B) fragmented into MTU‑safe packets.

## TCP Control (v1)
Line-oriented UTF‑8, `\n` delimited.

1. Server → Client  
   `CHALLENGE <nonce>`
2. Client → Server  
   `HELLO <nonce> <clientNonce> <width> <height> <fps> <bitrate>`
3. Server → Client  
   `ACK <nonce> UDP=<port>`

Optional:
- `PING` → `PONG`
- `START` → `OK START`
- `STOP` → `OK STOP`

## UDP Media Packet (v1)
Little‑endian header, **32 bytes** total.

| Offset | Size | Field | Description |
|-------:|-----:|-------|-------------|
| 0 | 4 | magic | `"PCAM"` (0x4D414350 LE) |
| 4 | 1 | version | `0x01` |
| 5 | 1 | flags | bit0 = keyframe, bit1 = codec config (SPS/PPS) |
| 6 | 2 | headerSize | `32` |
| 8 | 4 | seq | packet sequence number |
| 12 | 4 | frameId | access unit id |
| 16 | 4 | timestampMs | sender tick (wrap ok) |
| 20 | 2 | fragIndex | 0‑based |
| 22 | 2 | fragCount | total fragments |
| 24 | 4 | payloadLen | bytes after header |
| 28 | 4 | streamId | `0` = video |

**Payload**: Annex‑B H.264 bytes (start codes included).

### Fragmentation
- Target payload size: <= **~1368 bytes** (1400 MTU minus header).
- Each access unit is split into `fragCount` packets with shared `frameId`.

### Reassembly / Drop Policy
- Reassemble by `frameId` when all fragments arrive.
- Drop frame if any fragment missing.
- Out‑of‑order fragments are accepted.

## Security MVP
- Nonce/challenge in TCP handshake.
- No encryption (LAN MVP). Documented for future upgrade.
