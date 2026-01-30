# PhoneCam Virtual Camera (DirectShow) — Debugging & Test Checklist

## 1) Confirm the filter is COM-registered

1. Build **PhoneCam.VirtualCam.Filter** (net48, x64).
2. Run in elevated PowerShell:
   - `./install.ps1 -Configuration Release`
3. Confirm COM registration:
   - `regedit` -> `HKCR\CLSID\{3064B0F9-E26E-4301-9AC8-2BDF7C8C8A6C}`

## 2) Confirm it is in the Video Capture Sources category

Use **GraphStudioNext**:
1. `Graph` -> `Insert Filter...`
2. Expand **Video Capture Sources**
3. You should see **PhoneCam Virtual Camera**

If it does not appear:
- ensure you ran `PhoneCam.VirtualCam.RegisterTool.exe register` elevated
- ensure you used **Framework64 RegAsm** for the filter DLL
- ensure you are using a **64-bit** GraphStudioNext build

## 3) Confirm frame delivery (producer → filter)

The filter listens on TCP `127.0.0.1:51111` when loaded.
The tray app sends frames when `Virtual Camera: On` is enabled.

Quick check:
- start the tray app
- click `Start`
- toggle `Virtual Camera: On`
- open any app that lists webcams (e.g. Camera, Teams, OBS)

## 4) Filter-side logging

The filter writes logs to:
- `%LOCALAPPDATA%\PhoneCam\virtualcam_filter.log`

Typical useful messages:
- `TCP receiver listening...`
- `TCP client connected`

If you see no log entries at all:
- the filter was not instantiated (not loaded). Verify in GraphStudioNext.

## 5) Common failure modes & fixes

### A) Camera device is not visible in apps
- Bitness mismatch: a 64-bit filter is only visible to 64-bit DirectShow apps (and vice versa).
  - Fix: also build and register an **x86** version if needed.
- Filter not registered in category:
  - Fix: run `PhoneCam.VirtualCam.RegisterTool.exe register` elevated.

### B) Device shows up but is black / frozen
- Producer not connected:
  - check tray logs (`%LOCALAPPDATA%\PhoneCam\tray.log`) for `VirtualCam: connected`
  - check filter log for `TCP client connected`
- Pixel format / stride mismatch:
  - This implementation expects BGR24, tight-packed `1280x720`.
  - Fix: ensure tray stream is enabled and decoder produces frames.

### C) Wrong colors (RGB/BGR swapped)
- `System.Drawing.Imaging.PixelFormat.Format24bppRgb` is BGR layout.
  - If colors are swapped in the consumer app, you are probably converting twice.
  - Fix: keep the payload as-is (BGR) and keep the media type BI_RGB with 24bpp.

### D) Crashes when registering
- RegAsm can crash if you call FilterMapper2 registration inside `[ComRegisterFunction]`.
  - Fix: this repo intentionally keeps those callbacks empty and uses `RegisterTool`.

### E) Negotiation errors (apps can't connect)
- Some apps request different resolutions.
  - This filter advertises a fixed `1280x720@30 RGB24` type.
  - Fix (advanced): implement media type enumeration / format negotiation.

## 6) Minimal test plan

1) **Registration**
- `install.ps1` completes without errors
- GraphStudioNext shows device in Video Capture Sources

2) **Frame path**
- Start tray, toggle virtual camera On
- Open GraphStudioNext:
  - Insert capture device (PhoneCam Virtual Camera)
  - Render pin to Video Renderer
  - Observe moving video

3) **Stress**
- Toggle `Virtual Camera: On/Off` repeatedly
- Close preview window and ensure camera still works in external apps

