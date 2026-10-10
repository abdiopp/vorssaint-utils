# Screen recording — capture side (`screenRecorder`)

Spec: [`docs/specs/02-recorder.md`](../specs/02-recorder.md) §2, §3.1–§3.19, §4 (capture keys),
§5.1–§5.5, §6.1–§6.3, §6.23–§6.25, §7, §8. The recording **editor**, preview,
export and sharing are a separate module behind `IRecordingEditor`.

Status: complete on both targets; **the Windows engine has never run** (it was
written and compiled on macOS). Everything platform-neutral is covered by unit
and headless UI tests. The manual checklist below is the acceptance test.

## How it fits together

```
Rivet.App/Features/Recording/          UI and orchestration
  RecordingModule.cs                   action, shortcut role, tile, tray items, Settings page
  ScreenRecorderController.cs          state machine: toggle, pre-flight, chooser, mic check,
                                       countdown, start, pause, live monitoring, stop, sweep
  RecordingDelivery.cs                 editor hand-off or "save straight away" + Recent captures
  RecordingChrome.cs                   pill, region guide, countdown, alert; stop reasons
  Views/                               RecordingIndicatorView (pill), overlay windows,
                                       CountdownView, RegionGuideView, RecordingSettingsPage
Rivet.Core/Recording/Engine/           platform-neutral engine (unit-tested)
  RecordingSession.cs                  one recording: start order, cancellation contract,
                                       finalization, take.json written last
  PauseClock.cs                        shared QPC clock + pause removal (§6.2)
  Audio/                               PcmConverter, WavFileWriter, AudioTrackWriter
                                       (alignment, silence padding, drift steps)
  Pointer/                             125 Hz PointerRecorder, CursorCatalog, regions,
                                       TypingRecorder
  RecorderMath.cs                      §6.1 snapping, §6.3 bit rate, size limits, window fit,
                                       §6.23 file names, §6.24 cursor identity, §6.25 label
  RecordingBackends.cs                 platform interfaces (video, audio, cursor, system,
                                       raw exporter)
  RecorderSettings.cs, TakeSweeper.cs, TakeImport.cs, IScreenRecorder.cs
Rivet.Imaging/Recording/               cursor bitmap decoding (mono/XOR → outlined), PNGs
Rivet.Platform.Windows/Recording/      WGC + D3D11 + Media Foundation, WASAPI (NAudio),
                                       cursor shapes, power request, exporter, importer
Rivet.Platform.Fake/Recording/         synthetic video/audio/pointer for the dev build
```

Threading: the controller runs on the UI thread and never blocks it (every
capture start/stop, file operation and device query runs on the thread pool).
Video frames arrive on a Windows Graphics Capture worker thread, encoding has
its own thread, the pointer sampler has its own thread, audio arrives on
NAudio's capture threads, clicks and keys on the shared hook thread.

## Implemented

Starting and stopping (§2, §3.1–§3.6)
- One toggle (`recorder.toggle`) for the shortcut, the Utilities tile, the
  Command Bar, the Settings button and the tray: start when idle, stop when
  recording (or paused), cancel during the countdown/microphone check, ignored
  while a stop is writing the file.
- Dedicated shortcut role `screenRecorder`, default **Ctrl+Alt+Win+5**, off by
  default (`recorderShortcutEnabled`), "Show capture menu when using keyboard
  shortcut" honoured (`AllowToolSwitching`).
- Utilities tile (Order 160): "Screen recording" with the caption as tooltip;
  while recording it reads **"Stop recording"**, shows the stop icon and a live
  caption "Recording 0:42" / "Paused 0:42". The action's title follows too.
- Tray: red tint and a "Recording 0:42" tooltip line while recording; tray menu
  items "Stop recording" and "Pause recording"/"Resume recording" while active.
- Choosing what to record through `ICaptureSelector` (`CaptureTool.Recording`);
  when the service is absent, the whole monitor under the pointer. Selections
  are resolved once: area snapped to even pixels, ≥ 32 px, inside the monitor
  (§6.1); windows keep their handle (region clamped to the monitor, for the
  manifest); displays use the whole monitor.
- Pre-flight before the chooser and again after it: feature installed,
  capture/encoder available (clear messages for Windows older than 2004 and for
  "N" editions without the Media Feature Pack), ≥ 2 GB free at the take
  location (modal "Not enough space to record").
- Microphone decision: privacy switch ("Let desktop apps access your
  microphone") and device presence probed off the UI thread; on failure the HUD
  "Microphone unavailable" and the recording continues without it.
- Countdown Off/3/5/10 s (default 3), the 82 DIP draining-ring HUD on the
  screen with the pointer; region guide visible during it; toggle cancels.
- Start order and cancellation contract exactly as §3.6 (generation counter in
  the controller; one finalization owner in the session; late callbacks
  rejected once stopping begins — the clock is closed at the stop instant).
- Pill shown before capture starts, then a 120 ms wait for the chooser to leave.
- System idle sleep and display sleep prevented with a power request
  ("Recording the screen" in `powercfg /requests`), released on stop and quit.

Capture content (§3.7–§3.13)
- Video: Windows Graphics Capture for a monitor (`CreateForMonitor`) or a window
  (`CreateForWindow`), `IsCursorCaptureEnabled = false`, `IsBorderRequired =
  false` where supported (Windows 11; borderless access requested once), free-
  threaded frame pool, GPU crop with `CopySubresourceRegion`, window resizes
  scaled to fit the fixed output (letterboxed), even sizes everywhere.
- Encoder: H.264 High, no B-frames, 2 s key-frame interval, bit rate
  `clamp(W·H·fps·0.09, 0.8, 60) Mb/s`, BT.709 tags, Media Foundation sink writer
  with a DXGI device manager and hardware transforms (BGRA textures in as
  ARGB32; the sink writer's video processor converts to NV12); CPU input and
  the software encoder as fallbacks; regions over 4096 px or 9.4 MP are encoded
  scaled down.
- Variable frame rate with a ceiling of 30/60 fps: the latest picture is written
  only when it changed, timed by its capture time (QPC) through the pause clock;
  a 1 s heartbeat keeps long still stretches decodable; the first frame is forced
  to t = 0 and the last one appended again at the stop time; frames captured
  during a pause are re-timed to the resume point (no stale picture after resume).
- System audio: WASAPI **process loopback excluding this app's process tree**;
  if Windows refuses, endpoint loopback of the default output, re-opened when
  the default output changes or disappears. Microphone: default input with
  automatic stream routing, or a chosen endpoint (`recorderMicrophoneDevice`).
  48 kHz stereo 16-bit requested from the engine; any other format is folded
  (5.1/7.1 → stereo) and resampled in `PcmConverter`.
- One shared clock: packets carry the capture client's QPC position; the
  `AudioTrackWriter` pads gaps (loopback is silent-by-absence), absorbs jitter up
  to 20 ms, trims overlaps, drops packets that touch a pause, and pads every WAV
  to the recording length so all tracks start at 0 and end together.
- Pointer track (pointer.bin v4 via `PointerTrack`): 125 Hz sampler thread with
  a high-resolution waitable timer; positions normalized to the region, or to
  the window's **current** rectangle and fit for window recordings (fixes the
  macOS limitation in §3.7); `CURSOR_SHOWING`/`CURSOR_SUPPRESSED` visibility;
  left/right presses and releases from the shared mouse hook; shapes read only on
  handle changes, decoded from `GetIconInfoEx` bitmaps (alpha, masked colour,
  monochrome; inverting pixels → black with a white outline), deduplicated by
  content (§6.24), a sharper 3× rendition via `CopyImage(LR_COPYFROMRESOURCE)`,
  PNG-encoded with SkiaSharp; `DisplayScale` = the monitor's DPI scale,
  `SystemScale` = 1 (Windows bakes the accessibility size into the bitmap).
- Typing track: key-down times only, auto-repeat ignored (per-key down state),
  modifier keys alone ignored, never the keys.

Indicator, guide, monitoring (§3.14, §3.15)
- Pill: 184×32 DIP dark capsule, pulsing red dot (paused: 40 %), `m:ss`/`h:mm:ss`
  label with tabular digits, pause/resume, stop and **discard** (second click
  confirms, so nothing takes focus); tooltips and accessible names; centred
  10 DIP below the top of the recorded monitor's work area; tool window, never
  activated, topmost, excluded from capture (`WDA_EXCLUDEFROMCAPTURE`).
- Region guide (area/display): 30 % dim outside the region, 2 DIP blue outline
  inset 1 DIP, click-through, excluded from capture, sized from physical pixels
  and the window's own scaling (DPI-correct, re-fit on `ScalingChanged`).
- Every second: elapsed label (pill, tile, tray, Settings), disk check off the UI
  thread (one at a time; < 500 MB → stop, saved straight away, HUD "Recording
  stopped, the disk is almost full"); monitor removed or resized → clean stop.

Stopping and delivery (§3.16–§3.18)
- Stop: hide pill/guide, stop timers, release the power request; sources stopped
  concurrently; WAVs padded; pointer.bin and typing.json written atomically when
  non-empty; **take.json written last**.
- Delivery: normal stop (or a window closed/display changed, with a message)
  and "Open the editor after recording" on → `IRecordingEditor.OpenAsync(take)`;
  otherwise saved straight away as `Recording yyyy-MM-dd at HH.mm.ss.mp4` in the
  save folder **with its sound** (H.264 copied as is, system + microphone mixed
  into one AAC track), added to `IRecentCaptures`, HUD "Saved to Videos", take
  deleted. Save failure: beep, HUD, the editor opens on the take as recovery; with
  no editor the master is shown in File Explorer and the take is kept.
- Discard (pill): the take is deleted, nothing saved.
- Uninstalling the feature stops a recording (saved straight away) and cancels a
  pending start. Quitting while recording finishes and saves the file (bounded to
  30 s, no UI).
- Take sweep on feature sync (launch, install/uninstall) and after every stop:
  master older than 24 h (or edit.json/take.json, whichever is newest), or no
  master and folder older than 1 h; folders in use (master open) are skipped.

Settings page "Screen recording" (Capture, `screenRecording`)
- Record now / Stop recording with live elapsed time; shortcut switch, chord,
  "Show capture menu…"; Countdown; Frames per second; Show recording controls;
  Record the sound of the PC; Record the microphone; Microphone device (listed
  off the UI thread); privacy hint with "Open privacy settings" when Windows
  blocks the microphone; Open the editor after recording (warning when no editor
  is installed); Save to (folder picker + reset); note that files saved straight
  away have no pointer.

Settings keys: `recorderShortcutEnabled`, `recorderShortcut`,
`recorderShowCaptureMenuOnShortcut`, `recorderCountdown`, `recorderFrameRate`,
`recorderSystemAudio`, `recorderMicrophone`, `recorderOpenEditor`,
`recorderSaveFolder` (machine-local) — macOS keys and defaults — plus Windows
additions `recorderMicrophoneDevice` (machine-local) and `recorderShowIndicator`.

Importing a movie (§3.19)
- `ITakeImporter.ImportAsync(path)` (Windows: Media Foundation) validates (regular
  non-link file, size > 0, free space ≥ size + 500 MB), probes for a video track,
  copies the file into a new take under its own extension, decodes the first
  sound track to `system.wav` and writes take.json (source frame rate kept, no
  pointer/typing). The Media tools module calls it, then `IRecordingEditor`.

Development fake (net10.0)
- Real engine with synthetic sources: frames counted (no encoder; `take.mp4` is
  an **empty placeholder**), a quiet 440 Hz "system" tone and 220 Hz "mic" tone,
  a wandering pointer with two cursor shapes, real WAVs, pointer.bin, typing.json
  and take.json. Saving "straight away" copies the placeholder.

## Tests

- `tests/Rivet.Core.Tests/Recording` (67): spec vectors for §6.1 snapping, §6.2
  pause clock, §6.3 bit rates, §6.23 file names, §6.24 FNV-1a identity, §6.25
  labels; size limits and window fit; audio alignment (silence before the first
  packet, gaps, jitter, overlaps, pauses, trimming at stop, float/44.1 kHz/mono
  conversion, 24-bit, 5.1 fold, WAV header); pointer normalization, shape
  dedup by picture, clicks and samples dropped in pauses, window rectangles,
  125 Hz cadence; typing (auto-repeat, modifiers, pauses); catalog; session start
  order, stop during start, failed start, blocked microphone/system audio,
  capture ending on its own, discard, manifest written last; sweep; import rules.
- `tests/Rivet.Imaging.Tests/Recording` (7): monochrome/inverting/masked/alpha
  cursor decoding, outline, shape snapshots, sharper-copy rules.
- `tests/Rivet.App.Tests/Recording` (16): full flows on the fake platform (record
  → save straight away, pause, discard, window closed, feature uninstalled,
  blocked microphone, countdown cancel), the fake take's format, selection
  resolution, shortcut defaults, icons; snapshots in `tests/artifacts/snapshots/`:
  `recording-indicator*.png`, `recording-countdown.png`,
  `recording-region-guide.png`, `recording-settings-{light,dark}.png`,
  `recording-panel-while-recording.png`, `recording-cursor-ibeam.png`.

## Not implemented / out of scope

- The editor, preview, export, GIF, clipboard and temporary links (other module).
  The editor-owned settings (`recorderQuality`, `recorderAutomaticZoom`,
  `recorderGIFSize`, `recorderGIFFrameRate`, `recorderEditorPresets`,
  `recorderSharingEnabled`) are not on this page; see requests.
- The audio toggles under the chooser and "R repeats the last area": the shared
  selector's (screenshot module) UI. They read/write `recorderSystemAudio` /
  `recorderMicrophone`, defined here.
- Crash recovery of orphaned takes (parity: swept silently). Pointer and typing
  tracks live in memory until stop, so a crash loses them.
- HDR desktops: captured as SDR BGRA (Windows tone-maps); no HDR master.
- A recording thumbnail for Recent captures (`ThumbnailPath` left null).

## Deviations from the macOS app

| macOS | Windows | Why |
|---|---|---|
| `take.mov`, HEVC or H.264, audio tracks inside, fragmented every 10 s | `take.mp4` H.264 only, no audio; `system.wav`/`mic.wav`; regular (non-fragmented) MP4 | Shared take format (`TakeManifest`). H.264 always decodes on Windows (HEVC needs a Store extension). Media Foundation has no MOV sink. Regular MP4 is chosen over fMP4 for seek/duration compatibility with Media Foundation decoders; since orphaned takes are never recovered (parity), fragmenting would only add risk. Switch: `TranscodeContainerTypeGuids.Fmpeg4` in `H264Writer`. |
| Pill 150 pt: pause, stop | 184 DIP: pause, stop, **discard** (two-click confirm) | Task requirement; no dialog so focus never leaves the recorded app. |
| Save straight away = raw master (no cursor, separate audio tracks) | MP4 with sound (one mixed AAC track), still no cursor | A silent file would be useless; the pointer is only drawn by the editor (parity). |
| Default save folder: Desktop | **Videos** | Where Windows tools save recordings. |
| Capture filter fixed at start | Per-window `WDA_EXCLUDEFROMCAPTURE` | HUDs appearing mid-recording are excluded too (fixes §8.3 #12). |
| Window recordings normalize the pointer to the start rectangle | Current window rectangle + fit with every sample | Fixes §8.3 #2. |
| Process tap + trust protocol | WASAPI process loopback, endpoint loopback fallback | §3.8; no trust machinery needed. |
| Encoder failure → file cancelled | File finalized if at least one frame was written | A GPU reset should not lose the whole take. |
| Countdown on macOS fades 0.12 s in / 0.22 s out | 0.16 s both ways | Avalonia transition on the window's opacity. |
| Toggle while the chooser is open | Ignored (the chooser has Esc) | `ICaptureSelector` has no external cancel path in use. |
| Mixed-DPI cursor size | Bitmap size × (monitor DPI / system DPI) | No exact public API; see risks. |
| Selection made with another tool chosen in the palette | Dropped (logged) | The selector contract returns it to the caller; see requests. |

## Risks (verify on Windows first)

1. **Sink writer + D3D path**: BGRA DXGI surfaces into an ARGB32 input with
   hardware transforms relies on the sink writer inserting the video processor;
   colour matrix could come out BT.601 (slightly shifted colours). The writer
   falls back to CPU input and the software encoder on any setup failure (logged).
   Rate-control properties a driver rejects are dropped (logged).
2. **Texture reuse**: GPU frames are pooled and reused only when Media Foundation
   released them (COM reference count = 1); worst case more textures are made
   (cap 12) or a tick is skipped.
3. **WGC window capture size vs window rectangles**: Windows 10 may include the
   invisible resize borders; the pointer region picks the rectangle matching the
   captured size. Check pointer alignment on both OS versions.
4. **Process loopback availability**: NAudio documents 2004+, Microsoft 20348+;
   the fallback (endpoint loopback, includes this app's own sounds) is automatic.
5. **Audio/video drift on long recordings**: corrected in ≤ 20 ms steps by the
   track writer (a tiny glitch), not resampled.
6. **Cursor DPI and monochrome cursors**: scale on mixed-DPI setups is
   approximated; `CopyImage(LR_COPYFROMRESOURCE)` on cursors not loaded from a
   resource returns a stretched copy (no sharper, not worse).
7. **Very large regions** (5K, super-ultrawide): encoded scaled down; if the sink
   writer will not scale, frames are scaled on the CPU (lower frame rate).
8. **Virtual desktops**: the pill is a tool window on the current desktop.
9. **Shared `ShortcutRoleRow`** throws before attach (see requests); the page
   shows the chord read-only with a link to Keyboard shortcuts until it is fixed.

## Requests for shared code

1. **Bug — `src/Rivet.App/Controls/ShortcutRoleRow.cs`** (breaks the Keyboard
   shortcuts page as soon as any module registers a role, and any page that
   embeds the row): `(IBrush?)this.FindResource(...)` casts
   `AvaloniaProperty.UnsetValue` when the control is not attached yet. Replace
   the four casts in `Refresh()` and `ShowMessage()` with
   `this.FindResource("SuccessBrush") as IBrush ?? Brushes.Green` (etc.), or defer
   `Refresh()` to `AttachedToVisualTree`. My page falls back to a read-only chord
   until then (`RecordingSettingsPage.ShortcutEditor`).
2. **`PanelTileDescriptor`**: add `Func<string>? Title` / `Func<string?>? Icon`
   (live title). Today the module re-registers the tile (and the toggle action)
   on every state change to switch to "Stop recording". The spec's tile also has
   an accessory button "Recent captures" (hidden while recording); the descriptor
   has no accessory slot, so it is not shown.
3. **Move `IScreenRecorder` to `Rivet.Core.Contracts`** (it lives in
   `Rivet.Core.Recording.Engine` and is registered in DI). The capture selector
   (screenshot module) should (a) not open while `IScreenRecorder.IsBusy` unless
   §3.3 of the screenshot spec allows it, and (b) hand Recording selections made
   in a chooser it opened to `IScreenRecorder.RecordSelectionAsync(selection)`.
4. **`ICaptureSelector`**: when the user switches tools inside a chooser opened
   by another tool, route the result to that tool instead of returning it to the
   caller (the recorder drops non-Recording results today), or expose a router.
5. **`ActionRegistry`**: an `IsVisible` predicate (a "Pause recording" action
   would make sense in the Command Bar only while recording).
6. **`IRecordingEditor`**: `bool Owns(string takeFolder)` (or an open-takes list)
   so the sweep can skip takes an editor has open without relying on the master
   file being locked or edit.json being recent.
7. **`StringKeyTests`**: literals like `"recorder.toggle"` (an action id the
   task prescribes) are flagged as catalog keys because `recorder.` is a string
   prefix. The id is written as `"recorder" + ".toggle"`; consider skipping
   strings passed as action/role ids.
8. **Media tools**: resolve `ITakeImporter` (registered by this module's
   platform registrars) for "open in the recording editor", then call
   `IRecordingEditor.OpenAsync(folder)`; map `TakeImportFailure` to its own
   messages.
9. **Recording editor**: pointer.bin v4 shapes have `Width/Height/HotX/HotY` in
   video pixels of the recorded monitor (the PNG may be a sharper, larger
   rendition) and `DisplayScale` = the monitor's DPI scale per the shared
   `PointerTrack` doc; draw shapes at `Width × pointerSize` video pixels (do not
   multiply by `DisplayScale` again). Imported takes may use `take.mov`/`.mkv`
   (read `Video.File`); `Video.Vfr` is true for recordings.
10. Optional: the editor module could add its keys (quality, automatic zooms, GIF
    options, links) as a card on this page, as macOS shows them together.

## Windows manual test checklist

Run on **Windows 10 22H2** and **Windows 11 23H2/24H2**, at 100 % and 150 %
(and one mixed-DPI two-monitor setup). Turn on the shortcut in Settings first.

Start/stop and chrome
1. Tray → panel → Utilities → "Screen recording": the panel closes, the chooser
   opens in Recording mode (or, without the screenshot module, recording of the
   monitor under the pointer starts after the countdown).
2. Countdown 3-2-1 appears top-centre of the screen with the pointer; the region
   guide dims everything outside the selection; press the shortcut during the
   countdown → everything disappears, no take left in
   `%LOCALAPPDATA%\Rivet\Recordings`.
3. Record an area: pill top-centre of that monitor, timer counts; open the panel:
   tile reads "Stop recording" with "Recording m:ss"; tray icon is red with the
   time in its tooltip; right-click tray shows "Stop recording" and "Pause".
4. Pause/resume from the pill (dot stops pulsing at 40 %, timer frozen), stop from
   the pill, the tray, the tile and the shortcut — each works.
5. Discard: first click turns the trash red with a check and a tooltip; wait 3 s →
   reverts; click twice → nothing saved, take folder gone.
6. Neither pill, guide, countdown nor HUD appears in the video (check with
   another capture tool too); the yellow border is absent on Windows 11, present
   on Windows 10 (expected).
7. `powercfg /requests` (admin prompt) lists "Recording the screen" under DISPLAY
   and SYSTEM while recording, nothing after stop.

Video
8. 60 fps setting: move a window quickly — smooth; leave the screen still for 30 s
   — file stays small; total duration matches the timer (pauses removed).
9. 30 fps setting: frame rate in file properties ≤ 30.
10. Window recording: move the window, resize it (content letterboxed in the fixed
    size), minimize/restore (last frame held), close it → recording stops with
    "The window was closed, so the recording stopped." and is delivered.
11. Display recording, then unplug the monitor or change its resolution → clean
    stop with "The display changed…".
12. Odd-sized selection (e.g. 641×401) → file is 640×400. A 5K or 5120×1440
    monitor → file ≤ 4096 wide, plays.
13. Laptop with hybrid graphics: record the internal and the external display.
14. Open the master in the Films & TV app and VLC: plays, seeks, correct colours
    (compare against the screen; report any 601/709 shift).

Sound
15. Play music, record with "Record the sound of the PC" on: system.wav in the
    take has it. The log line "Recording … (system audio: …)" says which path was
    used ("process loopback, this app excluded" or "loopback of <device>"); with
    process loopback, sounds played by the
    app's own process are not recorded (toasts are played by Windows and are).
16. Silence at the start, sound after 5 s: system.wav starts at 0 with 5 s of
    silence; clap on camera/mic: picture and both WAVs within one frame.
17. Switch the default output device mid-recording (endpoint fallback case):
    sound continues on the new device after a short gap.
18. Microphone on, privacy "Let desktop apps access your microphone" off → HUD
    "Microphone unavailable", recording continues; Settings shows the privacy
    row with "Open privacy settings".
19. Choose a specific microphone in Settings, unplug it before recording → falls
    back to the default input (log); unplug during recording → HUD, the mic track
    is padded with silence.
20. 5.1/7.1 output device: system audio is folded to stereo.

Pointer and typing
21. Hover links (hand), text fields (I-beam), window edges (resize): take's
    pointer.bin has those shapes (editor shows them); classic I-beam in Notepad on
    a dark background is visible (black with white outline).
22. Accessibility pointer size 3 and colour: shapes are large/coloured in the take.
23. 150 % monitor: pointer drawn by the editor sits exactly on the hot spot
    (click on a 1-px target and compare).
24. Left and right clicks produce click markers; typing produces typing.json with
    times only; holding a key produces one time.

Delivery and lifecycle
25. Editor installed and "Open the editor" on → editor opens; off → file saved to
    Videos as `Recording yyyy-MM-dd at HH.mm.ss.mp4` with sound, HUD "Saved to
    Videos", appears in Recent captures, take folder deleted.
26. Save folder on a full USB stick / read-only folder → beep, HUD, editor opens on
    the take (or Explorer shows take.mp4 when no editor).
27. Fill the disk below 500 MB while recording → stops, saved straight away, HUD
    "Recording stopped, the disk is almost full". Below 2 GB → "Not enough space
    to record" before the chooser.
28. Uninstall "Screen recording" in Features while recording → saved straight away.
    Quit from the tray while recording → after exit the file is in Videos.
29. Leave a take folder older than 24 h (change its master's date) → swept at the
    next launch; a folder without master older than 1 h → swept.
30. Windows N edition without the Media Feature Pack → "Screen recording needs the
    Windows media components…" HUD, nothing starts.
31. Elevated window (e.g. Task Manager) in the recording: video fine; clicks and
    keys inside it are not in the tracks (UIPI, expected).
