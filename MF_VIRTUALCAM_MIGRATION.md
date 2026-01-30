# PhoneCam MF Virtual Camera Migration Plan

## Scope and constraints
- Target OS: Windows 11 (build 22000+).
- Goal: replace DirectShow source filter with a Media Foundation virtual camera created via `MFCreateVirtualCamera`.
- Keep the existing frame pipeline (decode path) and only swap the camera integration layer.
- Ensure the camera is visible in modern capture apps (e.g., Telegram) and in browser capture (Chrome/Edge).

## 1) Current dependency graph and frame flow (DirectShow)

### Project references
- `PhoneCam.Tray` references `PhoneCam.Core` only; it does **not** reference the DirectShow filter project (uses IPC instead).【F:PhoneCam.Tray/PhoneCam.Tray.csproj†L1-L28】
- `PhoneCam.VirtualCam.RegisterTool` references `PhoneCam.VirtualCam.Filter`.【F:PhoneCam.VirtualCam.RegisterTool/PhoneCam.VirtualCam.RegisterTool.csproj†L1-L13】
- Solution includes the DirectShow filter and registration tool projects alongside the tray app and core library.【F:PhoneCam.slnx†L1-L9】

### Frame flow (today)
1. `PhoneCam.Tray` starts a decode loop and calls `VirtualCamStreamer.TrySendFrame(Bitmap)` when the virtual camera is enabled.【F:PhoneCam.Tray/TrayAppContext.cs†L108-L255】
2. `VirtualCamStreamer` resizes frames to 1280x720 BGR24 and streams them over TCP to `127.0.0.1:51111` using a fixed header + payload protocol.【F:PhoneCam.Tray/VirtualCamStreamer.cs†L1-L199】
3. The DirectShow filter’s `TcpFrameReceiver` listens on `127.0.0.1:51111`, parses the protocol, and writes frames into `LatestFrameBuffer`.【F:PhoneCam.VirtualCam.Filter/Ipc/TcpFrameReceiver.cs†L1-L134】
4. `VirtualCamOutputPin` pulls from `LatestFrameBuffer` and pushes samples downstream at ~30 fps using DirectShow’s `IMemInputPin.Receive` in a dedicated streaming thread.【F:PhoneCam.VirtualCam.Filter/Filter/VirtualCamOutputPin.cs†L1-L285】
5. The filter exposes a fixed 1280x720 @ 30 fps RGB24/BGR media type, and the COM class is registered under the Video Capture Sources category by the register tool.【F:PhoneCam.VirtualCam.Filter/Filter/VirtualCamSourceFilter.cs†L14-L75】【F:PhoneCam.VirtualCam.Filter/DirectShow/DirectShowRegistration.cs†L7-L49】

### Existing IPC utilities (not currently wired to the filter)
- There is a `VirtualCamFrameHub` that can serve latest-frame data over named pipe or TCP, but it is not referenced by the tray app today.【F:PhoneCam.Tray/VirtualCamFrameHub.cs†L1-L458】
- There is also an unused `VirtualCamPipeServer` wrapper around the hub (not referenced from the tray app).【F:PhoneCam.Tray/VirtualCamPipeServer.cs†L1-L203】

## 2) Proposed MF Virtual Camera architecture

### High-level components
1. **MF Media Source (COM in-proc DLL)**
   - New project (suggested name: `PhoneCam.VirtualCam.MfSource`).
   - Implements `IMFMediaSource`, `IMFMediaStream`, `IMFMediaEventGenerator`, and `IMFGetService`.
   - Exposes a single video stream with a fixed media type initially (e.g., 1280x720@30, `MFVideoFormat_RGB24` or `MFVideoFormat_ARGB32`).
   - Implements `IMFMediaSource::Start`/`Stop` and `IMFMediaStream::RequestSample` to pull the latest frame from shared memory and deliver it as `IMFSample` with timestamps.
   - Registers as a COM class for MF virtual camera creation (no DirectShow registration).

2. **Registrar / Driver App**
   - New small console/tool (suggested name: `PhoneCam.VirtualCam.MfRegistrar`).
   - Calls `MFCreateVirtualCamera` / `IMFVirtualCamera` to register/unregister the camera device.
   - Handles device-friendly name, symbolic link, and persistence settings.
   - Replaces `PhoneCam.VirtualCam.RegisterTool` and the DirectShow `install.ps1` / `uninstall.ps1` flow.

3. **Frame delivery from `PhoneCam.Tray`**
   - Preserve the existing decode pipeline (frame production stays in `TrayAppContext` + `H264FfmpegDecoder`).【F:PhoneCam.Tray/TrayAppContext.cs†L186-L255】
   - Replace the DirectShow TCP sender with a **shared-memory frame hub** to avoid a second network hop and to keep latency low.

### Proposed IPC mechanism (shared memory + event)
- **Memory-mapped file** named `Global\PhoneCam.VirtualCam.FrameHub` (or scoped to user session if global is not required).
- **Layout** (fixed header + double-buffered payload):
  - Header: magic, version, width/height/stride, pixel format enum, frame id, timestamp ticks, buffer size, active buffer index.
  - Two payload buffers for double-buffered frames (BGR24 or RGB24), sized to max expected frame.
- **Synchronization**:
  - Named auto-reset event `Global\PhoneCam.VirtualCam.FrameReady` signaled by `PhoneCam.Tray` after writing a new frame.
  - Media source uses `WaitForSingleObject` with timeout to either return a fresh frame or repeat the last frame if no new data.
- **Why this fits**: it keeps the current frame flow (tray creates BGR24 bitmaps) and only swaps the transport layer; avoids dependence on TCP while staying in-process safe for MF virtual cam host.

### Optional fallback path
- Keep the TCP protocol as a fallback for development (reusing `VirtualCamStreamer`), but the primary path should be shared memory to reduce latency and avoid port contention.

## 3) Files that are DirectShow-only and can be deleted

These are tied to the DirectShow filter or its registration tooling and should be removable after the MF virtual cam is in place:

- **DirectShow filter project**
  - `PhoneCam.VirtualCam.Filter/PhoneCam.VirtualCam.Filter.csproj`
  - `PhoneCam.VirtualCam.Filter/DirectShow/DirectShowGuids.cs`
  - `PhoneCam.VirtualCam.Filter/DirectShow/DirectShowInterop.cs`
  - `PhoneCam.VirtualCam.Filter/DirectShow/DirectShowRegistration.cs`
  - `PhoneCam.VirtualCam.Filter/Filter/VirtualCamSourceFilter.cs`
  - `PhoneCam.VirtualCam.Filter/Filter/VirtualCamOutputPin.cs`
  - `PhoneCam.VirtualCam.Filter/Ipc/FrameQueue.cs`
  - `PhoneCam.VirtualCam.Filter/Ipc/FrameProtocol.cs`
  - `PhoneCam.VirtualCam.Filter/Ipc/LatestFrameBuffer.cs`
  - `PhoneCam.VirtualCam.Filter/Ipc/TcpFrameReceiver.cs`
  - `PhoneCam.VirtualCam.Filter/Ipc/IpcFrameReceiver.cs`
  - `PhoneCam.VirtualCam.Filter/Util/FilterLog.cs`
  - `PhoneCam.VirtualCam.Filter/Properties/AssemblyInfo.cs`

- **DirectShow registration tool**
  - `PhoneCam.VirtualCam.RegisterTool/PhoneCam.VirtualCam.RegisterTool.csproj`
  - `PhoneCam.VirtualCam.RegisterTool/Program.cs`

- **DirectShow install docs/scripts**
  - `install.ps1`
  - `uninstall.ps1`
  - `VIRTUALCAM_SETUP.md`
  - `DEBUGGING_VIRTUALCAM.md`

(These should be removed only after the MF virtual cam is confirmed working.)

## 4) Migration plan (milestones + tests)

### Milestone 1 — Architecture scaffolding
- Create new MF virtual camera projects:
  - `PhoneCam.VirtualCam.MfSource` (COM in-proc DLL)
  - `PhoneCam.VirtualCam.MfRegistrar` (tool for register/unregister)
- Add a shared assembly or C++ helper for shared memory layout definitions.

**Tests / checks**
- Build new projects in x64 (Windows 11 SDK available).

### Milestone 2 — Shared memory frame hub in tray app
- Implement a `VirtualCamSharedFrameHub` in `PhoneCam.Tray`:
  - Writes BGR24 frames into memory-mapped file + signals event.
  - Reuse existing decode loop; replace `VirtualCamStreamer` usage with shared-memory writer.

**Tests / checks**
- Run tray app; verify shared memory and event appear via Process Explorer / custom test reader.

### Milestone 3 — MF media source implementation
- Implement `IMFMediaSource` + `IMFMediaStream`:
  - Fixed media type first (1280x720@30) to match existing pipeline.
  - On `RequestSample`, read the latest frame from shared memory, set timestamps, and deliver `IMFSample`.
  - Handle start/stop, stream state, and attribute propagation.

**Tests / checks**
- Run a unit test or simple MF client to instantiate the source and pull samples.

### Milestone 4 — MF virtual camera registration tool
- Implement registrar to:
  - Register camera (name + symbolic link) with `MFCreateVirtualCamera`.
  - Unregister / delete the device when requested.

**Tests / checks**
- Register and list in device manager / camera selector list.

### Milestone 5 — End-to-end verification
- Confirm camera appears in:
  - Windows Camera app
  - Telegram
  - Chrome/Edge (`getUserMedia` camera selector)

**Tests / checks**
- Manual validation in each app (video visible and updates).
- Optional: frame rate stats in tray logs.

### Milestone 6 — Cleanup
- Remove DirectShow projects, scripts, and documentation (list above).
- Update repository docs for MF virtual camera usage and troubleshooting.

## 5) Repository inspection commands
- `ls`
- `rg --files`
- `cat PhoneCam.slnx`
- `cat PhoneCam.Tray/PhoneCam.Tray.csproj`
- `cat PhoneCam.VirtualCam.Filter/PhoneCam.VirtualCam.Filter.csproj`
- `cat PhoneCam.VirtualCam.RegisterTool/PhoneCam.VirtualCam.RegisterTool.csproj`
- `sed -n '1,200p' PhoneCam.Tray/VirtualCamStreamer.cs`
- `sed -n '1,200p' PhoneCam.VirtualCam.Filter/Ipc/TcpFrameReceiver.cs`
- `sed -n '1,200p' PhoneCam.VirtualCam.Filter/Ipc/FrameProtocol.cs`
- `sed -n '1,200p' PhoneCam.VirtualCam.Filter/Filter/VirtualCamSourceFilter.cs`
- `sed -n '1,200p' PhoneCam.VirtualCam.Filter/Filter/VirtualCamOutputPin.cs`
- `sed -n '1,200p' PhoneCam.Tray/VirtualCamPipeServer.cs`
- `sed -n '1,200p' PhoneCam.Tray/VirtualCamFrameHub.cs`
- `sed -n '1,200p' PhoneCam.Tray/TrayAppContext.cs`
- `cat DEBUGGING_VIRTUALCAM.md`
- `cat VIRTUALCAM_SETUP.md`
- `sed -n '1,200p' install.ps1`
- `sed -n '1,200p' uninstall.ps1`
