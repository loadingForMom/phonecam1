# PhoneCam Virtual Camera (DirectShow) — Setup

## What was added

- **PhoneCam.Tray** now includes a `VirtualCamStreamer` that forwards decoded frames over TCP (`127.0.0.1:51111`).
- **PhoneCam.VirtualCam.Filter** now listens on TCP and publishes frames as a DirectShow capture device (`1280x720 @ 30fps, RGB24/BGR`).
- **PhoneCam.VirtualCam.RegisterTool** registers/unregisters the filter in the **Video Capture Sources** category.
- `install.ps1` / `uninstall.ps1` automate COM registration + category registration.

## Build

Build with Visual Studio (recommended):

1. Set configuration **Release** and platform **x64**
2. Build projects:
   - `PhoneCam.VirtualCam.Filter`
   - `PhoneCam.VirtualCam.RegisterTool`
   - `PhoneCam.Tray`

## Install/Register

Run **PowerShell as Administrator** in the repo root:

```powershell
./install.ps1 -Configuration Release
```

## Use

1. Start `PhoneCam.Tray`
2. Click `Start`
3. Toggle `Virtual Camera: On`
4. Open any app that lists webcams and select **PhoneCam Virtual Camera**

## Uninstall/Unregister

```powershell
./uninstall.ps1 -Configuration Release
```

## Bitness gotcha (важно)

DirectShow capture devices are visible **only to apps of the same bitness**:
- 64-bit filter → visible to 64-bit apps
- 32-bit filter → visible to 32-bit apps

If you need both:
- build an **x86** variant of `PhoneCam.VirtualCam.Filter` and `RegisterTool`
- run RegAsm from `C:\Windows\Microsoft.NET\Framework\v4.0.30319\RegAsm.exe` for the x86 DLL
- register category with the x86 RegisterTool
