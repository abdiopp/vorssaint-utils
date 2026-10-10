# Camera preview (`cameraPreview`)

A floating mirrored camera view for checking yourself before a call (spec 07 §3.5).

## Where the code is

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/CameraPreview/`: `CameraPreviewController` (capture lifecycle, generation counter for late callbacks, hot-plug only while shown, remembered pick vs fallback), `ICameraService`, settings and layout constants |
| App | `src/Rivet.App/Features/CameraPreview/`: `CameraPreviewModule` (actions, shortcut role, panel tile with a "Permission required" caption, settings page), `CameraPreviewService` (show/hide and dismissal), `CameraPreviewWindow`, `CameraFrameView` (mirrored aspect-fill BGRA frames) |
| Windows | `src/Rivet.Platform.Windows/CameraPreview/WindowsCameraService.cs`: `DeviceInformation(VideoCapture)` + `DeviceWatcher`, `MediaCapture` (exclusive control, falling back to shared read-only while another app streams), a colour frame source near 640×480, `MediaFrameReader` delivering BGRA frames, privacy-denied detection, `ms-settings:privacy-webcam` |
| Fake | `src/Rivet.Platform.Fake/CameraPreview/FakeCameraService.cs`: two generated "cameras" (an animated test card with colour bars, a moving disc and a clock, ~15 fps) |
| Tests | `tests/Rivet.Core.Tests/CameraPreview/`, `tests/Rivet.App.Tests/CameraPreview/` (frame, running, starting, no camera, denied, unavailable states; settings light/dark; dismissal; camera choice) |

## Implemented

- Entry points: optional shortcut role `cameraPreview` (off by default, Ctrl+Alt+Win+W, toggle); action `cameraPreview.open` for the panel tile, Command Bar, Quick Launcher and radial menu (shown 0.15 s after the launching surface hides); Settings "Open preview".
- Window: fixed 320×240 rounded (radius 14) mirror, 48 DIP below the top of the pointer's monitor work area, centred and clamped; always dark; mirrored and aspect-filled; draggable; no close button; a soft shadow.
- States: starting (spinner), running, no camera ("No camera detected"), denied (Windows privacy off: message + "Open privacy settings…"), start failure (message + "Open camera" retry); the panel tile shows "Permission required: Camera" while access is denied.
- Cameras: a picker capsule fades in on hover with two or more cameras; an explicit pick is remembered (`cameraPreviewDeviceId`, machine state); hot-plug falls back to another camera without overwriting the pick.
- Dismissal: Esc, a press outside the frame (2 DIP tolerance; not on the on-screen/touch keyboard) seen by the shared mouse hook, another window taking the foreground (after a 1 s grace), a session lock or user switch, the shortcut, uninstall. The camera is released immediately (the reader and `MediaCapture` are disposed; the camera light turns off).

## Not implemented

- Other shapes or sizes: the spec defines one fixed 320×240 frame, as on macOS.
- A mirror on/off option (the spec always mirrors).

## Deviations from macOS

- No consent prompt: unpackaged desktop apps cannot trigger one. When "Camera access" or "Let desktop apps access your camera" is off, the preview shows the denied state with a link to `ms-settings:privacy-webcam`.
- When another app uses the camera exclusively (Teams, Zoom), the preview opens in shared read-only mode when the driver allows it, else shows the start-failure state.

## Risks

- Some drivers expose only NV12/MJPG: the frame reader requests BGRA conversion from Media Foundation; a few virtual cameras may refuse it (start-failure state).
- Foreground-change dismissal needs the 1 s grace because the launching surface (tray panel, Command Bar) hands focus back just after the show.

## Windows manual test checklist

1. Settings › Capture › Camera preview › Open preview: the mirror appears near the top of the screen, mirrored (raise your right hand: it appears on the right).
2. Press Esc: it closes and the camera light turns off at once.
3. Open again; click anywhere outside: it closes. Click inside or drag it: it stays.
4. With two cameras, hover the frame: the picker capsule appears; choose the other camera; reopen: it remembers. Unplug that camera while open: it falls back.
5. Windows Settings › Privacy & security › Camera › turn off "Let desktop apps access your camera"; open the preview: the denied message and "Open privacy settings…" appear; the panel tile says "Permission required: Camera".
6. Start a Teams/Zoom call with video, then open the preview: it shows the shared stream or the "could not start" state with "Open camera".
7. Turn on the global shortcut; Ctrl+Alt+Win+W toggles it from any app.
8. Open the preview, press Win+L: after signing in it is closed and the camera is off.

## Requests for shared code

None.
