# PhoneCam BLE Handshake (MVP)

## Roles
- **Windows laptop**: scans for phone BLE advertisements, enables hotspot, writes SSID/PSK to the phone.
- **Android phone**: advertises GATT service + receives handshake write, then connects to Wi-Fi.

## Service + Characteristics
- **Service UUID**: `0000feed-0000-1000-8000-00805f9b34fb`
- **Handshake Characteristic** (write): `0000feed-0001-1000-8000-00805f9b34fb`

Payload (UTF‑8, written by Windows → Android):
```
<ssid>|<psk>
```

## Nearby Detection
- Windows uses BLE advertisements to detect proximity.

## Android Scaffolding
- `BleHandshakeManager` advertises the service and hosts GATT server.
- On write, parse credentials and connect to Wi‑Fi.

## Windows Scaffolding
- `BleProvisioningClient` uses `BluetoothLEAdvertisementWatcher` to scan and connect.
- Writes credentials to the phone's handshake characteristic.

## Hotspot Automation (Windows)
Best effort:
- Start hostednetwork via `netsh` (requires adapter support and privileges).

Reference:  
https://learn.microsoft.com/en-us/uwp/api/windows.networking.networkoperators.networkoperatortetheringmanager
