# PhoneCam Media Foundation Virtual Camera

This repository now uses a Media Foundation (MF) virtual camera backed by an in-proc COM media source and a small driver tool that calls `MFCreateVirtualCamera`.

## Requirements
- Windows 11 build 22000+.
- Visual Studio 2022 with C++ Desktop Development workload.
- Admin rights to register the COM DLL in HKLM.

## Projects
- **PhoneCam.VirtualCam.MF.Source** (C++ DLL): Media Foundation media source that outputs fixed 1280x720@30 RGB32 frames.
- **PhoneCam.VirtualCam.MF.Driver** (C++ console): Registers/unregisters the MF virtual camera using `MFCreateVirtualCamera`.
- **PhoneCam.VirtualCam.MF.Tester** (.NET console): Writes a moving color pattern into the shared memory frame buffer for validation.

## Build
1. Open `PhoneCam.slnx` in Visual Studio 2022.
2. Build **x64** configuration.
3. Ensure the following binaries exist in the build output:
   - `PhoneCam.VirtualCam.MF.Source.dll`
   - `PhoneCam.VirtualCam.MF.Driver.exe`

## Install (admin required)
Run in an elevated PowerShell from repo root:

```powershell
./install_mf_virtualcam.ps1 -Configuration Release
```

This will:
1. Register the COM media source (`regsvr32`).
2. Call `PhoneCam.VirtualCam.MF.Driver.exe register` to register the virtual camera.

## Uninstall (admin required)

```powershell
./uninstall_mf_virtualcam.ps1 -Configuration Release
```

This will:
1. Call `PhoneCam.VirtualCam.MF.Driver.exe unregister` to remove the camera.
2. Unregister the COM media source (`regsvr32 /u`).

## Testing
- Verify **Windows Camera** app lists "PhoneCam Virtual Camera".
- Verify **Telegram** camera settings list it.
- Verify **Chrome/Edge** `getUserMedia` camera selector lists it.
- For frame continuity testing, run `PhoneCam.VirtualCam.MF.Tester.exe` to publish a moving pattern into shared memory and verify the preview updates continuously.

If you see "Access denied" when starting the virtual camera, ensure you registered the COM DLL from a location accessible to the Windows Frame Server services (avoid user-profile-only directories).

## Notes
- The media source reads the latest frame from shared memory and falls back to black frames if none are available.
- Use the tester utility to validate continuity without running the tray app.
