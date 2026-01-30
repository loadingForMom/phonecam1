# PhoneCam BLE Handshake (MVP)

## Roles
- **Windows laptop**: advertises GATT service + serves handshake payload.
- **Android phone**: scans, connects, reads handshake characteristic, starts streaming.

## Service + Characteristics
- **Service UUID**: `0000feed-0000-1000-8000-00805f9b34fb`
  - **Handshake Characteristic** (read): `0000feed-0001-1000-8000-00805f9b34fb`

Payload (UTF‑8):
```
pcam;ver=1;tcp=39000;udp=39010;ssid=?;psk=?;nonce=<server_nonce>
```

## Nearby Detection
- Android uses RSSI threshold with hysteresis:
  - Start connect at RSSI >= ‑65 dBm
  - Stop/ignore if RSSI <= ‑75 dBm

## Android Scaffolding
- `BleHandshakeManager` uses `BluetoothLeScanner` to scan for service UUID.
- On match, connect and read handshake characteristic (future TODO).

## Windows Scaffolding
- `BleHandshakeService` uses `GattServiceProvider` to advertise and serve read responses.
- Payload currently static string (TODO: inject SSID/PSK and server nonce).

## Hotspot Automation (Windows)
Best effort:
- Use WinRT `NetworkOperatorTetheringManager` (UWP/packaged apps).
- In classic desktop, prompt the user to enable hotspot manually.
  - MVP: detect hotspot interface and show instructions if not found.

Reference:  
https://learn.microsoft.com/en-us/uwp/api/windows.networking.networkoperators.networkoperatortetheringmanager
