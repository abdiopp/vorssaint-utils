# 02 — Screen Recording, Recording Editor, Export and Sharing

Functional specification for the Windows port of Vorssaint. It is stack-neutral: it describes behaviour, data, formulas and constants so the feature can be rebuilt without reading the Swift sources.

Source of truth (macOS, read in full): `Sources/Vorssaint/Services/Recorder/*` (30 files), `Sources/Vorssaint/UI/Recorder/*` (4 files), `UI/Settings/ScreenRecorderSettings.swift`, `Core/RecorderStrings.swift`, `Core/RecorderExportStrings.swift`, `Core/RecorderShareStrings.swift` (English only), `Core/Defaults.swift`, `docs/recorder-export-speed.md`, `Tests/Recorder*.swift`, plus the recording-related parts of `Services/QuickTools/ScreenCaptureService.swift`, `ScreenshotSelectionController.swift`, `ScreenshotService.swift`, `ScreenshotSupport.swift` (backdrop style, blur strength, file naming), `ScreenshotCapturePolicy.swift`, `QuickToolHUD.swift`, `UI/Screenshot/ScreenshotBackdropPopover.swift`, `Services/Audio/BoostLimiter.swift`, `Core/SettingsBackupSupport.swift`.

Conventions used throughout:

- **Source time** = seconds on the master recording's own clock (pauses already removed). **Output time** = seconds of the edited video after trim and cuts, at 1x. **Export time** = output time divided by the export speed. Everything the user places on the timeline (cuts, zooms, captions, images, blurs, clicks, pointer samples) is stored in source time.
- **Normalized coordinates** = 0…1 inside the recorded picture, origin at the **top-left**, y down. (The macOS code converts to Core Image's bottom-left space only at draw time; the port should stay top-left.)
- "Point" = macOS logical point; "pixel" = physical pixel. On Windows read "point" as DIP (1/96 inch) and "pixel" as physical pixel under Per-Monitor-V2 DPI awareness.
- Quoted UI text is the exact English string from the app. Appendix A lists every recorder string.

---

## 1. Overview

The recorder captures a chosen **area, window or whole display** into an untouched **master movie** (cursor deliberately excluded, variable frame rate, one AAC track per sound source: "Mac sound" and "Microphone"), alongside side-car tracks of **pointer motion, clicks and the real cursor images** (binary) and **keystroke timestamps** (no key identities). Recording is controlled from a small floating pill (elapsed time, pause/resume, stop) that never appears in the video. When it stops, the recording opens in a **non-destructive editor** whose entire state is a small JSON document beside the master: trim, cut-outs, automatic and manual zooms (generated from clicks and typing), captions, image overlays, privacy blurs, background/margin/rounded corners/shadow/shape, a redrawn and smoothed pointer with click effects, per-track mute/volume, and an export-only speed. One compositor renders both the live preview and the export, which writes **MP4 (HEVC or H.264 + AAC)** or an **animated GIF**, copies to the clipboard, or uploads a compressed MP4 to a **temporary 1-hour or 6-hour link**. The recording's private folder lives exactly as long as its editor window.

---

## 2. Feature inventory

### Starting and stopping
- [ ] One toggle shared by every entry point: global shortcut, menu-bar panel tile, command bar, Settings button, notch tile, radial menu, quick launcher (start when idle, stop when recording, cancel during countdown/permission wait).
- [ ] Opens the shared capture chooser in **Recording** mode (mode key `2`), optionally with the 1–4 mode palette.
- [ ] Record a **dragged area**, a **clicked window**, or the **whole display under the mouse** (Return); **R** repeats the last area; Esc cancels.
- [ ] Area snapped to even physical pixels, minimum 32 px per side, clamped to the display.
- [ ] Two audio toggles under the chooser: "Mac sound", "Microphone" (write through to Settings).
- [ ] Microphone permission requested on demand; recording continues without it ("Microphone unavailable").
- [ ] Countdown Off / 3 / 5 / 10 s (default 3) with a draining-ring HUD.
- [ ] Region guide: everything outside the recorded area dimmed 30%, blue outline, click-through, not recorded.
- [ ] Recording pill: pulsing red dot, elapsed `m:ss`/`h:mm:ss`, pause/resume, stop; top-centre of the recorded display; never in the video.
- [ ] Pause/resume without restarting capture (gaps removed from the file and from every side track).
- [ ] Disk guard: refuse to start below 2 GB free; auto-stop below 500 MB (file kept).
- [ ] Unexpected capture end keeps what was recorded.
- [ ] System idle-sleep prevented while recording.
- [ ] Own chrome (pill, guide, HUD, selection overlays, editors, pins) excluded; other app windows (settings, panel) remain recordable.

### Capture content
- [ ] Video at 30 or 60 fps max (default 60), full physical resolution, BGRA/sRGB, variable frame rate (still screen costs nothing).
- [ ] Hardware cursor excluded from the video; pointer position sampled at 125 Hz; press/release of left and right buttons with exact timestamps; system "pointer hidden" state; every distinct cursor shape image (arrow, I-beam, hand, resize…) with hot spot and size; accessibility pointer scale.
- [ ] Keystroke **times** only (auto-repeat ignored, no key codes stored).
- [ ] System audio (48 kHz stereo, own process excluded) on its own track; microphone (default input) on its own track; AAC 160 kb/s each.
- [ ] Master written crash-tolerant (fragmented every 10 s).

### Delivery
- [ ] Open the editor after recording (default) or save the raw master straight to the save folder as `.mov`.
- [ ] Recordings also added to the shared "Recent captures" history.

### Editor
- [ ] Dark editor window: top action band, stage (preview), inspector (right, 272 pt), timeline band (bottom).
- [ ] Play/pause (Space or click on picture), frame-accurate scrubbing, playhead, time `m:ss / m:ss`.
- [ ] Filmstrip of 14 thumbnails with trim handles; Shift-drag to select a stretch → "Cut out"; click a cut seam to restore it.
- [ ] Click ruler showing every press.
- [ ] Zoom lane: blocks generated once from clicks (+ optional typing), add by clicking the lane, move, resize from either edge, snap, delete, per-zoom strength 1.2–3.0x, follow-pointer or hand-aimed focus.
- [ ] Text lane: captions (up to 200 chars, 1–3 lines), 9 anchor positions, 6 colours, size 3–16% of frame height, eased in/out.
- [ ] Image lane: picture overlays (private copy), 9 anchors, size 4–60% of frame width, opacity 5–100%, eased.
- [ ] Blur lane: privacy regions drawn on the picture, strength 1–5, hard on/off.
- [ ] Audio lanes (only for tracks present): waveform, volume 0–100%, remove/restore.
- [ ] Inspector tabs Look / Pointer / Zoom; context panels for the selected zoom/text/image/blur.
- [ ] Looks: Original, Smooth, Studio; user presets (save/apply/remove, max 12, include images).
- [ ] Background: none, 5 gradient presets, saved customs, desktop wallpapers, any image, custom solid/gradient; margin, corners, background blur sliders; drop shadow; shape Original / Wide 16:9 / Square / Tall 9:16 (crop when no background, grow when there is one).
- [ ] Pointer: draw on/off, smoothing None/Light/Smooth/Cinematic, size 0.5–2x, click ring on/off; automatic idle fade, press "punch", click anchoring, shake passthrough.
- [ ] Export speed 0.25–4x (presets 0.5…4x, custom 0.01 steps), preview stays 1x.
- [ ] Quality Small file / Balanced / High with live output size readout.
- [ ] Unlimited undo/redo of whole-document snapshots, drags coalesced; autosave of every change.

### Export and sharing
- [ ] Save (MP4) to the save folder, Save as… (dialog), Save as GIF, choose save folder.
- [ ] Copy (MP4 file to clipboard), Copy as GIF (GIF data + file), Copy and delete (copy then close/discard).
- [ ] Progress chip with Cancel (Esc); destination replaced atomically only after success.
- [ ] GIF: 420/600/800 px long edge, 8/12/15 fps, ≤ 300 frames, infinite loop.
- [ ] Temporary link: compress on device to < 96 MB H.264/AAC, upload, link sheet (copy/delete), list of active links in Settings, privacy sheet, expiry tracking.

### Settings
- [ ] Countdown, Mac sound, microphone, open editor, automatic zooms, save folder, quality, fps, GIF size, GIF smoothness, allow temporary links, dedicated shortcut + "show capture menu" toggle.

---

## 3. Detailed behaviour

### 3.1 Entry points and the toggle

Every entry point calls one function, `toggle()`:

| Current state | Effect of toggle |
|---|---|
| Recording (running or paused) or a session exists | Stop (§3.16). |
| Countdown pending | Cancel: countdown stops, region guide removed, nothing recorded. |
| Waiting for the microphone-permission answer | Cancel the same way. |
| A stop is still finalizing the file | Ignored (prevents starting on top of a closing file). |
| Idle | Open the shared capture chooser with Recording pre-selected. |

Entry points:
- **Dedicated global shortcut** "Screen recording" — default `Control+Option+Command+5` (stored `control+option+command:23`), **disabled by default**. Pressing it while recording stops. Setting "Show capture menu when using keyboard shortcut" (default on): when off, the chooser opens without the mode palette.
- **Menu-bar panel → Utilities tile** "Screen recording" (visible by default, setting `panelUtilityScreenRecorder`). Caption "Record an area, window or the whole screen"; while recording the caption stays visible; accessory button "Recent captures" (hidden while recording); a permission button when capture permission is missing. Click closes the panel, then toggles after 0.2 s (so the panel is not captured).
- **Command bar** entry: title "Screen recording" (idle) / "Stop recording" (recording), icon record/stop, shows the shortcut, flagged "needs permission" when capture permission is missing.
- **Settings → Screen capture → Screen recording** button "Record now" / "Stop recording", with elapsed time under it while recording.
- Notch tile, radial menu and quick launcher (macOS-only surfaces; map to whatever Windows equivalents exist).
- From any open capture chooser: press `2` or click the Recording mode.
- **Media tools → Video compressor → open in editor** imports an existing movie (§3.19).

While a recording is running, the capture chooser can only be opened by another tool's own shortcut configured to skip the mode menu; it then offers only that tool (choosing Recording from a menu would stop the take).

### 3.2 Pre-flight checks

Run when the chooser is about to open in Recording mode, and again when the area is confirmed. All must pass:

1. Feature installed (the hub can uninstall it; §3.20.9).
2. Not finishing a previous stop, no session, no countdown, not waiting on the microphone.
3. macOS: Screen Recording permission (else open the system request and abort); Accessibility permission (needed for the global key monitor; else request and abort). **Windows: neither exists; drop.**
4. Free space at the recordings location ≥ **2,000,000,000 bytes** (macOS measures "available for important usage", i.e. including purgeable space). Else a modal warning alert, app activated first: title "Not enough space to record", message "Free up some space on the disk and try again."

### 3.3 Choosing what to record (Recording mode of the shared chooser)

The chooser is shared with Screenshot (1), Text (3) and Colour (4); only recording-specific behaviour is specified here (the shared selector itself is covered by the screenshot spec).

- One full-screen overlay per display. For Recording the overlay **always freezes**: every display is photographed first (without the pointer) and the still image is shown while choosing. Vorssaint's own ordinary windows stay visible and pickable; protected windows (editors, pinned captures, overlays, HUDs) are kept out.
- Hint capsule text: "Choose what to record  ·  Drag to select an area  ·  Click a window to capture it", key hints `1–4`, `↩` (whole screen), `Z` (loupe), `R` (repeat, only when a last area exists on an available display), and an `esc` chip. With the palette visible, modes are 116×48 pt buttons showing the digit, an icon and the tool name.
- Under the hint, **audio toggles** (button-style toggles in a capsule): "Mac sound" (speaker icon) and "Microphone" (mic icon). They read and write `recorderSystemAudio` / `recorderMicrophone` immediately, so Settings and the chooser always agree. Hidden (and non-interactive) for other modes.
- Gestures:
  - **Drag** a rectangle → area recording. A badge under the selection shows `W × H` physical pixels (computed from the unsnapped selection; ideally show the snapped even size). Holding **Space** during the drag moves the selection.
  - **Click a window** → window recording (the highlighted window under the pointer).
  - **Return / keypad Enter** → the whole display under the mouse.
  - **R** → re-confirm the last selected rectangle on its display (shared with the screenshot tool).
  - **Esc** (also captured globally) → cancel, nothing happens.
- The outcome is a **Region** (resolved once, never recomputed during the recording):

| Field | Meaning |
|---|---|
| `displayID` | The display the selection was made on. |
| `windowID` | Set only for a clicked window. |
| `pixelRect` | Top-left-origin rectangle in that display's physical pixels, snapped (§6.1): whole pixels, even width/height, ≥ 32 px, inside the display (shrinks rather than shifts). |
| `anchorRect` | Same area in global logical coordinates (for placing the guide). Derived from the **snapped** pixels. |
| `scale` | Pixels per point of the display (DPI scale). |

For a window, `pixelRect` is the window's on-screen frame at click time (clamped to that display). For the whole display it is the full display.

### 3.4 After confirmation: audio decisions and permission

1. Re-run pre-flight (§3.2). Create the indicator object and immediately show the **region guide** (§3.14) for area/display recordings (not for window recordings).
2. Increment a **pending-start generation** counter. Every later asynchronous step (permission answer, countdown tick, capture start completion) checks that its generation is still current and the feature still installed; otherwise it does nothing. This is what makes cancel-during-anything safe.
3. If the microphone toggle is on:
   - permission granted → continue;
   - undetermined → mark "awaiting microphone" and show the OS prompt; on answer continue; if denied show HUD "Microphone unavailable" and continue without microphone;
   - denied/unknown → HUD "Microphone unavailable", continue without microphone.
   - Windows: there is no per-app prompt for desktop apps; probe the default capture endpoint (privacy setting "Let desktop apps access your microphone" can block it) and show the same HUD on failure.
4. Countdown (§3.5), then start (§3.6).

### 3.5 Countdown

- Length = setting `recorderCountdown` sanitized to {0, 3, 5, 10} (anything else → 0). Default **3**.
- 0 → start immediately. Otherwise, each second: show the countdown HUD with the remaining number, wait 1 s, decrement; at 0 start.
- **Countdown HUD**: non-activating floating panel, top-centre of the visible area of the screen that contains the mouse, 24 pt below its top; 82×82 pt circle with blurred-material background; the number in 32 pt bold rounded monospaced digits; a 3 pt accent-coloured ring (round caps) trimmed from 4% to 4%+92%·progress and rotated to start at 12 o'clock, draining from full to empty over 0.92 s (progress = 1 − elapsed/0.92, clamped); fades in over 0.12 s, dismissed after 0.92 s with a 0.22 s fade; re-presented for each number.
- During the countdown the region guide is visible; the pill is not yet shown. Toggle cancels.

### 3.6 Starting the recording

1. Guard generation, no session, not finishing; create a **take** (private folder, §5.2). Failure → hide guide, HUD "The screen could not be recorded".
2. Read settings: frame rate (`recorderFrameRate` sanitized to {30, 60}, else 60), system audio (`recorderSystemAudio`), microphone (`recorderMicrophone` **and** permission granted).
3. Create the session (writer, samplers). Writer creation failure → hide guide, delete take, HUD "The screen could not be recorded".
4. Show the **pill** (§3.14) on the recorded display, label "0:00". It must exist before capture starts because on macOS the capture filter can only exclude windows that already exist.
5. Wait **120 ms** so the chooser overlays have fully left the screen (first frames must show the desktop, not a fading overlay).
6. Build the exclusion list: all protected app windows (editors and pins excluded regardless of the screenshot "Hide Vorssaint windows" preference), the pill, the region guide, and the HUD panel if currently visible.
7. Start the session (§3.7–3.13 for the ordering). On failure: hide guide/pill, delete take, then:
   - permission denied → re-request capture permission (macOS);
   - disk full → the "Not enough space" alert;
   - no content / stream failed / writer failed → HUD "The screen could not be recorded".
8. On success: state = recording (not paused), elapsed shown, **system idle sleep prevented** (macOS activity "Recording the screen"; Windows `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`), a 1 s repeating timer (tolerance 0.1 s) updates the elapsed label and runs the disk check (§3.15).

Session start order (matters for sync and for cancellation):
1. Writer starts (file opened).
2. If system audio wanted: create the system-audio reader (macOS process tap; §3.8).
3. Video capture configured; **immediately before** capture starts, the shared clock origin is set to "now" and the writer session begins at time 0.
4. System-audio reader started (before the microphone, to minimize the silent head).
5. Microphone started (failure → HUD "Microphone unavailable", recording continues).
6. Pointer sampler and typing sampler started (on the UI thread on macOS because they install event monitors).

Cancellation safety contract (implement equivalently): a stop may arrive while start is suspended at any await point. Exactly one of "start failed" and "stop" owns finalizing the writer (start failure cancels the file; stop finalizes it); stop waits until start has fully unwound before it touches the writer; once stopping begins, late capture callbacks are rejected permanently; the file is finalized only after every source is stopped and all queued samples are drained.

### 3.7 Video capture

| Property | Value |
|---|---|
| Output size | Exactly `pixelRect` size (physical pixels, even). |
| Max frame rate | 30 or 60 (minimum frame interval 1/fps). |
| Pixel format / colour | 32-bit BGRA, sRGB. Highest capture resolution. Opaque. |
| Cursor | **Not captured** (redrawn later from the pointer track). |
| Shadows | Window shadows ignored (display and single-window capture). |
| Area / display | Display capture limited to the source rectangle (`pixelRect / scale`, in points). Excludes this app's windows **except** its ordinary (non-protected) windows that exist at start. |
| Window | Captures the window's own buffer (independent of position/occlusion). Output size fixed at start; if the window is resized the content is **scaled to fit**, aspect preserved. |
| Frames written | Only frames whose status is "complete" or "started" and that carry an image. Idle frames (nothing changed) are skipped → the master is **variable frame rate**; a still screen produces almost no data. |
| Timestamps | Converted from the capture clock to the host clock, then mapped through the pause clock (§6.2). |

Unexpected end of the capture stream (display disconnected, window closed, system revocation): treated as a normal **stop** — the file is closed and delivered, not discarded.

Known macOS limitation to fix on Windows: the region (and therefore the pointer normalization) is fixed at selection time. For a **window** recording the video follows the window, but the pointer track keeps using the original window rectangle, so moving the window during recording misaligns the drawn pointer and zoom focus. The Windows port should sample the window's current bounds with each pointer sample and normalize against them (accounting for scale-to-fit letterboxing).

### 3.8 System audio ("Mac sound")

- Requested format 48 kHz, 2 channels; the app's own sounds are excluded.
- macOS has two sources and a trust protocol (all macOS-specific, drop on Windows):
  - The screen-capture stream's own audio (always available).
  - A Core Audio **process tap** "stereo global tap excluding this process", read through a private aggregate device clocked by the default output (macOS 14.4+). Reason: apps whose volume Vorssaint's volume mixer adjusts were heard twice in the stream (≈30 ms echo); the tap hears each app once.
  - Because a tap without permission delivers silence instead of an error, the tap is written only when **trusted**: trust is earned when the tap heard any non-zero sample during a recording, lost when the tap was silent while the stream heard sound, unchanged when both were silent (setting `recorderSystemAudioTapVerified`, machine-local, never backed up). While untrusted, the stream's audio is written and the tap only listens.
  - The tap compensates the level its stereo mix-down loses on outputs with more than two channels (gain eased toward the measured compensation, then a peak limiter: ceiling 0.944, instant attack, 160 ms release `exp(−1000/(rate·160))`); rebuilds itself on default-output or sample-rate change; if it cannot rebuild, the rest of the recording falls back to the stream's audio.
- **Windows equivalent**: one source — WASAPI loopback. Preferred: *process loopback* with "exclude target process tree" = own PID (the equivalent of the tap; requires a recent Windows build — verify the minimum, documented as build 20348+). Fallback: endpoint loopback of the default render device (includes own sounds; acceptable, or mute own sessions while recording). Handle default-device changes by re-opening. **Loopback delivers no packets while nothing plays — synthesize silence to keep the track continuous** (the macOS writer is timestamp-driven). Downmix multichannel endpoints (5.1/7.1) to stereo with standard coefficients.

### 3.9 Microphone

- The system default audio input device, captured only while a recording asked for it.
- Each buffer's timestamp is converted from the microphone's clock to the shared host clock, then through the pause clock — so system sound, voice and picture share one timeline.
- Written to its own AAC track. Device formats are not resampled by the app; non-interleaved multichannel buffers are interleaved (same order; devices without channel labels treated as "discrete in order") and the encoder produces 48 kHz stereo AAC. The format may change mid-recording (e.g. planar↔interleaved across a pause) and must still be accepted.
- Windows: WASAPI shared-mode capture on the default capture endpoint (console role); timestamps from the capture client's QPC position.

### 3.10 Shared clock and pause/resume

- One **pause clock** per recording: an origin (host time set just before capture starts) and a list of closed pause intervals plus an optional open pause.
- `elapsed(t)` = t − origin − total paused time overlapping [origin, t]. While paused it stays frozen at the moment the pause began.
- Every media sample (start, duration) is mapped to `elapsed(start)`; it is **dropped** if it starts before the origin or overlaps any pause (an audio buffer straddling a pause edge is dropped whole to avoid overlap).
- Pointer samples, clicks and keystrokes are mapped the same way; events inside a pause are dropped (so they never become zooms).
- Pause/resume does not stop the capture streams; it only changes the mapping. Pause is accepted only when recording and not already paused; resume only when paused.
- UI while paused: pill pause button shows a play icon with tooltip "Resume recording"; red dot stops pulsing and sits at 40% opacity; elapsed label frozen. Toggle/stop still works while paused.

### 3.11 Master writer

- Container: QuickTime `.mov` (`take.mov`), fragmented every **10 s** so a crash leaves a playable file up to the last fragment.
- Video: **HEVC** preferred; **H.264 High (auto level)** if HEVC settings are not accepted. Size = region pixel size. Average bit rate = `clamp(W·H·fps·0.09, 800 000, 60 000 000)` bit/s (the "High" preset; the master is always encoded at High regardless of the export choice). Max key-frame interval 2 s. **No frame reordering (no B-frames)** for smooth scrubbing. Colour tags BT.709 (primaries, transfer, matrix). Real-time input.
- Audio: one track per enabled source, each **AAC-LC, 48 kHz, stereo, 160 kb/s**, real-time. Each audio track carries a QuickTime metadata *content identifier* `com.vorssaint.recorder.audio.system` or `com.vorssaint.recorder.audio.microphone`; readers match tracks by this tag, and untagged tracks (older takes, imported movies) are assigned in order: first unmatched → system, second → microphone.
- Session starts at source time 0. The **first video frame is forced to t = 0** (it is held over capture start-up latency); every later frame and all audio keep their true mapped times, so audio stays aligned.
- Back-pressure: a sample is not appended when the input is not ready (it is dropped by the live path); any append failure marks the writer failed.
- **Finish**: if at least one video frame was written and nothing failed: the last frame is appended once more at `elapsed(stopTime)` (so a recording ending on a still screen lasts as long as it felt), the session ends at that time, inputs are marked finished, the file is finalized. Zero frames or a failure → cancel writing and report failure.
- Sync guarantees verified by tests (synthetic writer fixture): video, system audio and microphone markers before and after a pause decode within 25 ms of each other; pointer, click and typing markers stored in their files align with the decoded video within 1 ms and with audio within 25 ms, including a delayed first video frame and a microphone whose callbacks arrive first but whose capture starts later; all tracks end on the shared timeline (elapsed at stop); a planar↔interleaved switch and a 4-channel unlabeled microphone are accepted.

### 3.12 Pointer sampler and cursor catalog

- Runs only while recording; everything is created at start and destroyed at stop.
- A dedicated high-priority thread samples at **125 Hz** (sleep for the remainder of each 8 ms slot). Each sample (if not paused):
  - `time` = pause-clock event time,
  - `point` = global pointer position relative to the region, normalized: `(p − (displayOrigin + pixelRect.origin/scale)) / (pixelRect.size/scale)`; can be outside 0…1,
  - `isPointerVisible` = whether the system is currently showing a pointer (hiding while typing or in full-screen video does not change the shape, so it is tracked separately),
  - current cursor shape identity.
- **Clicks**: a mouse-only global monitor records left/right button down/up with exact timestamps (`isDown` true/false). Right clicks count exactly like left clicks everywhere (zooms, rings, punch, anchoring). Clicks are not filtered by position: a click outside the recorded area still produces a zoom (aimed toward the nearest edge).
- **Cursor shapes**: a cheap "cursor changed" seed is checked on every sample; only when it changes are the pixels read (premultiplied BGRA, top row first, plus the hot spot and size in the bitmap's own pixels = screen points).
  - Identity = **content hash** (FNV-1a 64-bit over every bitmap byte, then width, height, hotX, hotY as 32-bit floats; §6.24), because the system's own handle changes every time the same cursor is set.
  - Animated cursors arrive as a vertical strip of frames: if `height ≥ 2·width` and `height % width == 0`, only the first square frame is kept.
  - Hot spot clamped inside the bitmap. Bitmaps > 16 MB or with unexpected formats are ignored (identity 0 → the previous identity is kept).
  - Because the system vends the 1x base image, a **sharper copy** of the same cursor is requested once per shape from the higher-resolution representations: the smallest representation at least `ceil(width·2·3.0)` px wide (big enough for the maximum 3x zoom on a 2x display), else the largest; accepted only if wider than the base and its aspect ratio matches within 0.02 (the pointer may have changed in between). Size and hot spot keep the base values.
  - The **system pointer scale** (accessibility "pointer size", clamped 1…4) is read once at start.
- At stop, shapes are packed in order of first appearance (index 0 = the cursor on screen when recording began), each sample's identity is remapped to its table index (unknown → 0), shapes are stored as PNG. The track is written to `pointer.bin` only if the recording succeeded and there is at least one sample (§5.4). (The track lives in memory during the recording, so a crash loses it even though the master survives.)
- If no real cursor image could be captured at all, the editor draws a fallback vector arrow (§6.11).
- Windows mapping: position via `GetCursorPos` (Per-Monitor-V2 aware → physical pixels; normalize by pixels, set displayScale = 1), visibility via `GetCursorInfo` (`CURSOR_SHOWING`), shapes via `GetCursorInfo.hCursor` + `GetIconInfoEx`/`DrawIconEx` into a 32-bit premultiplied bitmap (or DXGI Desktop Duplication pointer-shape data). Windows' standard cursor handles are stable, but keep content hashing for parity. Monochrome/XOR cursors (classic I-beam, inverting crosshair) cannot be represented by alpha alone: render them as a black glyph with a white outline (see risks). For the "sharper copy", request a larger rendition (e.g. `CopyImage(..., LR_COPYFROMRESOURCE)` at ≥ 6× the logical width) or upscale with a high-quality filter. Clicks via Raw Input or `WH_MOUSE_LL` (timestamp with QPC in the callback). Cursor size accessibility is already baked into the bitmap Windows returns → systemScale = 1.

### 3.13 Typing sampler

- Global and in-app key-down monitors (macOS needs Accessibility). Records only `time` (pause-clock event time) for each key-down that is **not an auto-repeat**. No key codes, characters or modifiers are kept.
- Written to `typing.json` as `{"times":[…]}` only if non-empty and the recording succeeded.
- Used only when the editor's "Keep zoomed in while typing" is on (§6.5).
- Windows: Raw Input keyboard (`RIDEV_INPUTSINK`) or `WH_KEYBOARD_LL`; the LL hook has no repeat flag, so track per-virtual-key "down" state and ignore repeats until key-up. UIPI may hide keystrokes destined for elevated windows (acceptable: only a zoom heuristic).

### 3.14 Recording indicator (pill) and region guide

**Pill** — floats above everything, on all virtual desktops, never takes focus, and is excluded from the capture.

- Size 150×32 pt, capsule shape (corner radius = height/2), fill grey 0.12 at 94% opacity, 1 pt border white at 18%, window shadow on.
- Layout (x from left): pulsing red dot 8×8 at x=12 (vertically centred); elapsed label at x=25, width 50, 13 pt semibold monospaced digits, white, centred; a 1 pt vertical divider (white 14%) at x=78.5 from y=8 to height−8; pause button 30×26 at x=82; stop button 30×26 at x=116 (red tint). Icons 11 pt semibold: pause / play, stop.
- Buttons: borderless; hover fills white 10% (pressed 18%) rounded 8 pt; pointing-hand cursor; accept the first click without activating.
- Tooltips/accessibility: pill "Recording controls", pause "Pause recording", resume "Resume recording", stop "Stop recording".
- Dot pulse: opacity 1 → 0.28, 0.85 s, auto-reverse, ease-in-out, forever; paused: no pulse, opacity 0.4.
- Position: centred horizontally on the recorded display, top edge 10 pt below the top of the display's visible area (below the menu bar). Fade in 0.18 s; fade out 0.16 s on hide.
- Elapsed label format: `m:ss` (no leading zero on minutes) below one hour, `h:mm:ss` above; negative → `0:00` (§6.25).

**Region guide** (area and display recordings only) — a click-through, non-activating overlay covering the recorded display, just below the pill's level:
- Everything except the recorded rectangle filled black at 30% (even-odd fill).
- The rectangle outlined with a 2 pt stroke, colour sRGB (0.18, 0.55, 1.0) at 95%, inset 1 pt.
- Shown from confirmation (including the countdown) until the recording ends or is cancelled. Excluded from the capture.

Windows mapping: topmost, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` windows; the guide also `WS_EX_LAYERED | WS_EX_TRANSPARENT` (click-through); exclude both from capture with `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`. Keeping a window on all virtual desktops has no public API; showing the pill on the current desktop and re-showing it on desktop switch is acceptable.

### 3.15 Live monitoring

- Every second: elapsed label updated from the pause clock; **disk check** on a background thread (one at a time; an answer that arrives for an older recording is ignored). If free space < **500,000,000 bytes** → stop with reason HUD "Recording stopped, the disk is almost full" (the file is kept and **saved directly**, the editor is not opened).

### 3.16 Stopping and finalization

Triggered by the pill's stop button, `toggle()`, auto-stop for disk, or the stream ending on its own.

1. Must run on the UI thread; ignored if no session or already finishing. Mark finishing; invalidate pending starts; hide pill and guide; stop the elapsed timer; release the sleep assertion.
2. Session stop: capture the stop time; claim finalization; wait for any in-flight start to unwind; stop system-audio reader, microphone and video stream concurrently and wait for all; drain the capture queue.
3. Update the macOS tap trust flag (skipped if the tap lost its reader during the recording).
4. Stop samplers (UI thread) → pointer and typing tracks.
5. Drain the writer queue; finish the writer (§3.11).
6. If written: write `pointer.bin` / `typing.json` (atomic) when non-empty.
7. Result: written → **deliver** (§3.17); not written → delete the take, HUD "The screen could not be recorded".
8. Run the take sweep (§3.18).

### 3.17 Delivery

- If the stop had **no reason** (normal stop) **and** "Open the editor after recording" is on → open the editor (§3.20). (If the feature cannot own an editor, fall through.)
- Otherwise **save directly**: copy `take.mov` (the raw master: VFR, HEVC/H.264, separate audio tracks, **no pointer drawn**) to the save destination as `Recording yyyy-MM-dd at HH.mm.ss.mov` (§6.23), then delete the take. On success: add to Recent captures; HUD = the stop reason if any, else "Saved to <folder name>". On failure: system beep, HUD "The screen could not be recorded", and open the editor as recovery (the take is intact).
- Note: with the editor off the user gets a file **without a cursor**, because the cursor is never in the master. (Product decision for the port: keep parity, or render a minimal "Original"-look export instead.)

### 3.18 Take lifecycle and sweep

- A **take** = folder `Take-<UUID>` under `<AppData>/Recordings/` (§5). Created owner-only.
- It lives exactly as long as its editor: closing the editor deletes the folder, saved or not.
- **Sweep** (on feature/preference sync, and after every stop) deletes folders that no open editor and no active session owns, when: the master exists and its modification date is > **24 h** old; or there is no master yet and the folder's creation date is > **1 h** old. Folders with non-UUID names are ignored. Runs off the UI thread.
- There is **no crash-recovery UI**: a take orphaned by a crash or quit is never offered back to the user; it is silently swept. (The fragmented master would allow recovery — consider adding a "recover last recording" prompt on Windows.)

### 3.19 Importing an existing video (Media tools)

- Media tools → Video compressor → open in the recorder editor: the chosen file must contain a video track; it must be a regular non-symlink file with size > 0, and free space must be ≥ size + 500 MB. It is **copied** into a new take as `take.mov` (independent of the original), then the editor opens owned by Media tools (closed if Media tools is uninstalled). Failure messages belong to Media tools ("no video", "unsupported").
- Imported takes have no pointer or typing track: the Pointer tab shows "This recording has no pointer track, so there is nothing to smooth. Zooms placed by hand still work."; the automatic-zoom toggle is hidden; manual zooms still work. Observed quirk: with no pointer samples the resampled path is all (0, 0), so a new zoom left on "Follows the pointer" aims at the **top-left corner** until the user picks a spot. Port fix: when a take has no pointer track, default follow-pointer focus to the centre (0.5, 0.5) or create new zooms pre-aimed at the centre.
- Rotated/portrait movies: the track's preferred transform is normalized so the displayed size (even) and an origin-zero transform are used everywhere.
- Frame rate: the editor and exporter use the track's nominal frame rate **snapped to {30, 60} (anything else → 60)**; see §8.3 (a 24 fps import exports at 60 fps).

### 3.20 Editor window

#### 3.20.1 Opening and window

- One editor per take; several editors can be open at once. While any editor is open the app behaves as a regular windowed app (macOS: Dock icon and Cmd-Tab; Windows: normal taskbar window). Opening activates the app and brings the window to front.
- Window: dark theme always; title "Recording" (the title text itself is hidden; the top band doubles as the title bar, leaving 76 pt on the left for the window buttons); resizable, minimizable, closable; dragging inside the content never moves the window (drags are for handles).
- Initial content size from the visible frame of the screen containing the pointer: width = `min(max(1060, 0.70·W), W − 60)`, height = `min(max(600, 0.68·H), H − 80)`, rounded; centred. Minimum content size **940 × 560**.
- Background: grey 0.115 with a top-to-bottom gradient from white 3.5% to clear.
- Layout: top band (54 pt) / middle row = stage (fills) + 1 pt divider + inspector (272 pt) / bottom band (timeline).

#### 3.20.2 Loading

1. **Document**: decode `edit.json` if it exists (unreadable → all defaults). Otherwise create defaults (§5.6) seeded from Settings: `quality ← recorderQuality`, `keepsSystemAudio ← recorderSystemAudio`, `gifSize ← recorderGIFSize`, `gifFrameRate ← recorderGIFFrameRate`, `zoomEnabled ← recorderAutomaticZoom`. After the first open, the document wins: changing Settings later never alters an existing take.
2. Decode `pointer.bin` and `typing.json` (missing or corrupt → empty).
3. Load edit presets and the shared background presets.
4. Asynchronously read the master: duration; video track display size (orientation-normalized, even); nominal frame rate snapped to {30, 60} (else 60) = **editor frame rate**; audio tracks by tag (§3.11).
5. Then, in order: start waveform extraction (background); **sanitize** the document against the duration (§5.6); **generate automatic zooms once** if `zoomsGenerated` is false (§6.5) — this is not an undo step and is persisted immediately with `zoomsGenerated = true` (even if it produced nothing); generate filmstrip thumbnails; build the playback composition (§3.20.6); build the preview compositor.
6. Until the duration is known, editing actions are no-ops.

#### 3.20.3 Top band

Height 54 pt, translucent material, bottom hairline. Left → right:

| Control | Behaviour |
|---|---|
| Brand mark (24 pt) + "Recording" (13 pt semibold) | Static. |
| Clock button | Opens the shared **Recent captures** window. Tooltip "Recent captures". |
| Divider | — |
| Undo / Redo (curved-arrow icons) | Disabled when the stack is empty. Tooltips "⌘Z", "⇧⌘Z". |
| **Presets** menu (sliders icon, "Presets") | "Original", "Smooth", "Studio" (apply a look, §3.28); if user presets exist: divider, one item per preset name (apply), submenu "Remove preset" listing them; divider; "Save current preset…". Disabled while a preset save/apply is copying images. |
| (spacer) | — |
| **Finished-file chip** | After a successful Save / Save as / Save as GIF (not after copies or share): green check + file name (11 pt, middle-truncated, max 190 pt). Click → reveal the file in the file manager. Draggable: dragging it drags the file itself (e.g. into a chat). Tooltip "Reveal in Finder" (Windows: "Show in folder"). Hidden while exporting. |
| **Export progress chip** | While exporting: linear progress bar (ideal 110 pt, shrinks first in a narrow window; label never wraps) or a spinner during upload; label "Saving…" / "Compressing for sharing…" / "Uploading securely…"; borderless accent "Cancel" link. Capsule, material background. |
| Trash button (red) | Discard (§3.36). Tooltip "Delete". |
| "Copy and delete" | §3.34. Tooltip "⌥⌘C". Disabled while exporting. |
| Split button "Copy" + chevron | Left part: Copy video (tooltip "⌘C"). Chevron menu: "Copy as GIF". Disabled while exporting. |
| Link menu (chain-link icon) | Only when "Allow temporary links" is on. Items "For 1 hour", "For 6 hours" (§3.37). Tooltip "Share". Disabled while exporting. |
| Primary **Save** split button (prominent) | Click → Save (MP4). Menu: "Save" (⌘S), "Save as…" (⇧⌘S), "Save as GIF", divider, "Save to…" (choose save folder). Disabled while exporting. Tooltip "⌘S". |

Toolbar button style: 30 pt tall, 12 pt medium text, fill white 5.5% (pressed 11%), 1 pt border white 8% (pressed 15%), corner 8 pt; compact icon buttons are 30 pt wide.

#### 3.20.4 Stage (preview)

- Rounded rectangle (corner 14 pt) filled black 72% with a 1 pt white 12% border, padded 18 pt horizontally and 14 pt vertically inside the middle row. The video is drawn **aspect-fit** inside it; letterbox bands are transparent over the black fill.
- **Click on the picture** (when not aiming/drawing): if a zoom is selected → deselect it; else if a blur is selected → deselect it; else toggle play/pause.
- **Aiming mode** (choosing a zoom focus, §3.22): the preview switches to the **raw recording** (no zoom, background or overlays) so the click lands on real pixels; a capsule hint "Click the picture to aim it" at the top; the next click sets the focus (mapped through the letterbox, §6.22); a click outside the picture keeps the old focus; either way aiming ends and the edited preview returns. Esc ends aiming.
- **Drawing a blur area** (§3.25): raw recording shown; hint capsule "Drag over what should stay hidden"; dragging (≥ 2 pt) shows a rectangle filled teal 22% with a 1.5 pt teal border following the pointer; releasing sets the area; too small (< 1% of the picture on either side) keeps the previous area; drawing ends either way. Esc ends drawing.
- Only one of aiming / drawing can be active; starting one ends the other. Both pause playback.

#### 3.20.5 Bottom band (timeline)

Translucent material, top hairline, padding 18 pt sides, 10 pt top, 16 pt bottom, rows spaced 6 pt. Each row = right-aligned label column (64 pt, 10 pt semibold, secondary) + content. Rows, top to bottom:

1. **Transport row** (no label, 4 pt extra bottom spacing), left → right:
   - Play/Pause button (bordered, 26×20 icon area). Tooltip "Space".
   - Time label `current / total`: current output time (`m:ss`, truncated) / output duration (`m:ss`, rounded); 12 pt medium monospaced, grey 0.78.
   - "Add text" (text icon) — adds a caption at the playhead (§3.23).
   - "Add image" (photo icon) — picks and adds an image at the playhead (§3.24).
   - "Blur an area" — adds a blur at the playhead and starts drawing (§3.25).
   - When a cut selection exists: prominent red **"Cut out  m:ss"** button (scissors; the length of the selection, rounded) — disabled when the cut would leave less than 0.4 s.
   - (spacer)
   - **Export speed** control (speedometer, e.g. "1×") (§3.30).
   - **Quality** menu: label "<quality name>  <W> × <H>" (§3.31).
2. **"Zoom"** row: click ruler (12 pt, §3.20.8) above the zoom lane (30 pt, §3.22).
3. **"Text"** row — only when at least one caption exists.
4. **"Image"** row — only when at least one image overlay exists.
5. **"Blur"** row — only when at least one blur exists.
6. **"Mac sound"** row — only if the master has a system-audio track (32 pt audio lane, §3.26).
7. **"Microphone"** row — only if the master has a microphone track.
8. **"Recording"** row: the filmstrip (54 pt, §3.20.7).

**All lanes, the ruler and the filmstrip share one horizontal mapping over the whole recording in source time**: `x = clamp(t / duration, 0, 1) · width`. Cut-out stretches are not removed from the axis; they are shown darkened on the filmstrip. The playhead everywhere is the current **source** time (`sourceTime(forOutput: playerTime)`).

#### 3.20.6 Playback

- The player plays the **edited composition**: only the kept ranges, joined (cut stretches are genuinely absent, not skipped), at 1x (export speed never affects preview), with per-track volumes applied, rendered through the same compositor as the export (§6.13, §6.17).
- Player clock = output time. A 30 Hz observer updates the current time. When playing and within 0.02 s of the end → pause and seek to 0. Play pressed within 0.05 s of the end → seek to 0 first.
- Seeks are frame-exact (zero tolerance).
- **Seek to a source time**: map to output time; if that moment was cut out (or outside the trim), jump to the **start** of the kept range whose start is nearest to the requested time. (Quirk: scrubbing past the trim end therefore jumps to the start of the last kept range; consider clamping to the nearest kept moment on Windows.)
- Picture-affecting changes rebuild the preview compositor after a **120 ms debounce** (so dragging a slider does not thrash); timing changes rebuild the composition; audio changes only re-apply the mix (§3.32).

#### 3.20.7 Filmstrip ("Recording" row)

- 14 thumbnails at times `(i + 0.5)/14 · duration` (middle of each slot), max 240×240, ±0.5 s tolerance, aspect-fill, edge to edge; grey 0.18 placeholder before they load. Corner radius 8 pt, clipped.
- Trimmed-away ends darkened black 62%. The kept span outlined with a 2 pt accent border (rounded 8). Each **cut** stretch darkened black 62%, with a 3 pt orange (90%) **seam** at its start (hit area ±4 pt).
- **Trim handles**: 12 pt wide accent rounded bars (corner 4) with a white grip (2 pt × 36% height) at each end of the kept span; hit area extends 6 pt around. The end handle is never drawn closer than 24 pt to the start handle. Dragging the start handle sets trim start (the trim end, if still 0, becomes the duration) and seeks to the new start; dragging the end handle sets trim end and seeks to `max(start, end − 0.05)`. Trim is sanitized (§6.4): minimum length **0.2 s**; the dragged handle gives way.
- **Drag across the strip** (≥ 2 pt): scrubs (seeks to the source time under the pointer). If **Shift** is held when the drag begins (decided on the first movement and kept for the whole drag), the drag instead defines a **cut selection** from the press point to the current point (red 28% fill, 1.5 pt red border) and still seeks so the picture follows.
- **Click** on the strip: clears the cut selection and seeks there.
- **Click a seam**: restores (deletes) the cut whose range, widened by ±0.15 s, contains the seam time (one undo step).
- The cut selection is cleared when any lane item is selected and after cutting. A selection shorter than 0.1 s is discarded.
- **Cut out** (button or Delete key while a selection exists): only if the remaining output would be ≥ 0.4 s; appends the cut, normalizes cuts (merge touching/overlapping, drop < 0.1 s), clears the selection, commits one undo step, rebuilds the composition and seeks to the cut's start.
- There is no numeric trim entry; the handles are the whole trim interaction.

#### 3.20.8 Click ruler

- 12 pt strip above the zoom lane. A 1 pt baseline (white 5%). One tick per **press** (mouse-down, left or right) at its source time: 1.5×7 pt, white 35%, bottom-aligned. A 2 pt white playhead line.
- Dragging anywhere on the ruler (from 0 pt) scrubs. The ticks double as zoom snap targets and silently explain why each automatic zoom exists.

#### 3.20.9 Closing, discarding and feature removal

- See §3.36 for discard/close. Uninstalling the screen-recorder feature closes all recorder-owned editors (their takes are deleted) and stops a running recording (it still finishes into a file). Editors opened by Media tools close when Media tools is uninstalled.

### 3.21 Timeline lanes — shared behaviour (zoom, text, image, blur)

All four lanes are the same control with a different item kind; one set of gestures:

**Appearance**
- Lane background: white 4%, corner 6 pt. Empty lane: dashed (5/4) white 22% border inset 1 pt and a centred hint (10.5 pt medium, white 40%): "Click here to add a zoom" / "Click here to add text" / "Click here to add an image" / "Click here to add a blur". (Only the zoom lane is ever shown empty; the others appear once they have an item.)
- Block rect: x from start to end, y inset 2 pt, minimum drawn width 6 pt, corner 6 pt. Fill = kind accent at 22%. Accents: zoom = system accent (blue), text = purple, image = orange, blur = teal.
- "Hold" region drawn brighter (extra accent 16%) from `start + ramp` to `end`, where ramp = `min(rampIn, duration/2)` with rampIn 0.42 s (zoom), 0.25 s (text, image), 0 (blur) — so a block shows its easing.
- Border: accent at 65%, 1 pt; **selected**: 100%, 2 pt.
- Grip bars (2 pt × 36% of height, accent 90%) inside both ends when the block is wider than 34 pt.
- Label centred, 10 pt semibold white 92%: `glyph + " " + label` when wider than 56 pt, else glyph only; omitted if it does not fit with 10 pt spare. Zoom: `"2.0×"` (one decimal) with glyph ↗ (follows pointer) or ◉ (aimed). Text: first 18 characters, glyph "T". Image: file name without extension (first 18 chars), glyph ▣. Blur: "Blur", glyph ▦.
- Hover: a 1 pt white 12% vertical line follows the pointer.
- Cursor: open hand over a block body; left-right resize over an edge zone; arrow elsewhere. Edge zone = `min(11 pt, 40% of block width)` from each end; hit test tolerates 2 pt outside the block.

**Gestures**
- **Press on a block** → it is selected immediately (all other kinds' selections and the cut selection cleared). Press on an edge zone + ≥ 3 pt horizontal travel → **resize** that edge; press on the body + ≥ 4 pt → **move** (closed-hand cursor), keeping the grab offset.
- Moving: proposed start = `snap(timeAt(x) − grabOffset)`; resizing: proposed edge = `snap(timeAt(x))`.
- **Snapping** (8 pt screen tolerance converted to time: `8/width · duration`): candidates 0, duration, the playhead, every press time, and the starts/ends of the other blocks in the same lane. Nearest candidate within tolerance wins.
- **Release on empty lane space** (press not on a block): if this lane has a selected item → deselect it (first click "puts it down"); otherwise **add a new item** of the lane's kind at the snapped time.
- Every release commits the interaction as **one undo step** (moves and resizes do not create one step per mouse move).
- **Right-click a block** → selects it and shows a context menu with "Remove".
- With the lane focused, Delete/Backspace removes the selected item (the window-level Delete handling in §3.33 normally catches it first).

**Constraints**
- Zoom blocks never overlap: moving is clamped between the previous block's end and the next block's start (length preserved); resizing is clamped likewise and to a minimum length of **0.4 s** (§6.21).
- Text/image/blur blocks may overlap each other. Move: length preserved, clamped to `[0, duration − length]`. Resize: start ∈ `[0, end − 0.4]`, end ∈ `[start + 0.4, duration]`.

### 3.22 Zooms

**Model**: a zoom segment = id, start, end (source time), amount (1.2–3.0x), optional focus (x, y normalized). No focus = **follows the pointer**.

**Automatic generation** (§6.5 for the algorithm):
- Happens **once per take**, at first open, if automatic zoom is on; afterwards zooms belong to the user and are never silently regenerated (`zoomsGenerated` flag), including the state "user deleted all zooms".
- "Zoom in on every click" toggle (Zoom tab, only with a pointer track): turning it **off** keeps the segments but disables the whole effect (`zoomEnabled = false`; segments not rendered, still on the lane). Turning it **on** re-enables, and if the lane is empty regenerates from clicks.
- "Keep zoomed in while typing" toggle: sets `zoomsOnTyping`, **regenerates all segments** from clicks (+ typing when on), forces `zoomEnabled = true`. (This replaces hand-edited zooms — undoable.)
- "Back to one per click" (link, Zoom tab, pointer track required): regenerates all segments from clicks (+ typing if enabled) at the global amount, enables zoom, clears selection. One undo step.
- Empty state (Zoom tab when the lane is empty): magnifier icon in an accent circle, "No zooms yet", "Create them from your clicks or add one on the timeline.", button "Create automatic zooms" when the clicks would produce at least one zoom, else "Add a zoom" (adds at the playhead).
- Applying a look or preset also restores automatic zooms if zoom is enabled and the lane is empty.

**Adding manually** (click on empty zoom lane, or "Add a zoom"):
- Slot (§6.21): default length **2.0 s** starting at the clicked time; if the time is inside an existing zoom, the new one starts at that zoom's end; it ends at the next zoom's start if closer; refused if less than 0.4 s of room (or the recording is ≤ 0.4 s).
- Amount = the amount last chosen for a zoom in this editor session, else the document's global amount. Sets `zoomEnabled = true`, `zoomsGenerated = true`. The new zoom is selected.

**Selected-zoom inspector** ("This zoom"):
- Back link "‹ All options" (Esc).
- "How close" slider 1.2×–3.0× (shows `%.1f×`), live while dragging, one undo step on release; double-click resets to 1.8×. Also remembered as "last zoom amount".
- "Where it looks": segmented "Follows the pointer" / "Pick a spot". Choosing "Pick a spot" enters aiming mode (§3.20.4). When aimed, a link "Click the picture to aim it" re-enters aiming. Choosing "Follows the pointer" clears the focus (and ends aiming).
- "Remove" button.

**Global zoom controls** (Zoom tab, when segments exist): "How close" slider (document `zoomAmount`, 1.2–3.0, reset 1.8). Observed behaviour: it changes the amount used for **new/regenerated** zooms and the size of the follow-pointer focus clusters (§6.7); it does **not** change the amount of existing segments (each segment has its own). Consider making it apply to all follow-pointer segments in the port, or relabel.

**Rendering**: §6.6–6.7 (smootherstep ramps 0.42 s in / 0.65 s out, spring-smoothed amount and travel, edge-band follow, exact aim).

### 3.23 Text overlays (captions)

- **Add** ("Add text" at the playhead, or click the text lane): start = `clamp(t, 0, duration − 0.4)`, end = `min(duration, start + 3.0)`, text "Your text here", anchor **bottom** (centre), size **0.06** (6% of frame height), colour **white**. Selected on creation.
- **Inspector** ("This text"): back link; multi-line text field (1–3 visible lines, placeholder "Your text here"; typing updates the preview live and lands as one undo step when the field loses focus, on submit, or when the panel disappears; Return inserts a new line); "Size" slider 3%–16% (shows `%.0f%%`, reset 6%); "Position" 3×3 grid of anchors (topLeading, top, topTrailing / leading, center, trailing / bottomLeading, bottom, bottomTrailing; selected cell accent 85%, others white 8%, 20 pt tall cells); "Colour" swatches (20 pt circles): white (1,1,1), black (0.06,0.06,0.07), accent (0.04,0.52,1.00), yellow (1.00,0.80,0.00), red (0.96,0.26,0.21), green (0.20,0.78,0.35) — selected ring accent 2.5 pt; "Remove".
- Text is clamped to 200 characters; leading/trailing whitespace trimmed when drawn; empty text draws nothing.
- Rendering §6.15. Captions sit **above everything** (not magnified by zoom, not under the background).

### 3.24 Image overlays

- **Add** ("Add image" or click the image lane): opens an image file picker (single image file). The file is copied off the UI thread into the take folder (`<take>/<UUID>/<original name>`, folder 0700, file 0600) and must decode; free space must cover size + 500 MB. Failure → HUD "Couldn’t add this image." Then: start = `clamp(t, 0, duration − 0.4)`, end = **duration** (a mark normally stays for the rest of the video), anchor **bottomTrailing**, size **0.18** (18% of frame width), opacity **1**. Selected.
- The overlay references only its private copy, so moving/deleting/replacing the original never changes the edit; undo can always restore it. If the editor closed while the copy was in flight, the copy is removed and nothing is recreated.
- **Inspector** ("This image"): back link; file name (middle-truncated); "Size" 4%–60% (reset 18%); "Opacity" 5%–100% (reset 100%); "Position" 3×3 grid; "Remove".
- Rendering §6.15. Images are drawn above the zoomed/framed picture and **below** captions, in start-time order.

### 3.25 Blur regions (privacy)

- **Add** ("Blur an area" or click the blur lane): start = `clamp(t, 0, duration − 0.4)`, end = **duration** (a thing that should be hidden usually stays on screen), area = default rect x 0.35, y 0.40, w 0.30, h 0.20 (normalized), strength **3**; selected; **drawing mode starts immediately** (§3.20.4).
- **Inspector** ("This blur"): back link; prominent "Choose the area" button (disabled while drawing); caption "Drag over what should stay hidden" while drawing, else "Hidden for as long as its block lasts on the timeline."; "Blur strength" slider 1–5, integer steps (reset 3); "Remove".
- Redrawing the area keeps the blur's strength and times.
- Hard edges in time: hidden on every frame in `[start, end]` inclusive, never faded (a blur that eases in shows what it is hiding). The area is in the recording's own pixels (so it stays on the same content regardless of shape, quality or zoom; the zoom magnifies the blur; the pointer is drawn over it).
- Rendering §6.14. Export safety: §6.14 (frames held across a cut or retimed never uncover protected content).

### 3.26 Audio lanes

One row per source present in the master ("Mac sound", "Microphone"):
- Waveform: 220 peak bars over the full source duration (peak = max |sample| across channels in each bucket, ≤ 1); bar width 68% of its slot, height `max(1, peak · (laneHeight − 8))`, centred, corner 0.7; tint blue (system) / purple (microphone) at 80%. Flat line before data loads. Dimmed to 30% when the track is removed.
- Volume slider 0…1 (92 pt), with `NN%` label; disabled when removed. Tooltip "Volume".
- Toggle button: "Remove" (x-mark icon) / "Restore" (undo-arrow icon, tinted).
- Lane background tint 11% (removed: 4%), 1 pt border tint 28% (removed 10%), corner 7.
- Changes apply immediately to the preview's audio mix (no picture rebuild). Gain can only attenuate (0–100%).

### 3.27 Inspector (right panel)

Scrollable, 272 pt wide, translucent material, padding 16/14, sections spaced 18 pt. **What it shows depends on selection, in this priority**: selected text → "This text"; selected image → "This image"; selected blur → "This blur"; selected zoom → "This zoom"; otherwise a segmented tab picker **Look | Pointer | Zoom** (tab choice is per-window state, default Look). The swap itself teaches the selection model; each context panel has a labelled "‹ All options" way back.

Section titles: 11 pt semibold, grey 0.62, uppercase, letter-spacing 0.4.

Value sliders everywhere: title left (11 pt, grey 0.72), current value right (11 pt medium monospaced, grey 0.86), small slider below; **double-click the slider resets to its default**; dragging updates live and commits one undo step on release (see §3.32 for the ones that do not coalesce).

- **Look tab**
  - "LOOK": three cards side by side — Original (rectangle icon), Smooth (pointer-with-motion icon), Studio (sparkles; preview tile shows an indigo→purple gradient). Card: 48×36 preview, title 12.5 pt semibold; selected = accent 10% fill, accent 72% 1.25 pt border, filled check badge; hover = white 7% fill. A card is "selected" when the document's look fields equal that look's values (§3.28). Click applies with a 0.16 s ease-out animation.
  - "BACKGROUND": a large button with a 26×20 swatch of the current background (gradient preview) and the text "Background" (when one is set) or "None", opening the **background popover** (§3.29). "Shape" menu picker: "Original", "Wide" (16:9), "Square" (1:1), "Tall" (9:16). When a background is set: a sub-panel (white 4%, corner 10) with sliders "Margin", "Corners", "Blur" 0–100% (each `%.0f%%`; double-click resets to margin 50%, corners 0%, blur 0%).
- **Pointer tab** (only meaningful with a pointer track; otherwise the note "This recording has no pointer track, so there is nothing to smooth. Zooms placed by hand still work.")
  - "POINTER": "Draw the pointer" switch. When on: "Smoothing" menu (None / Light / Smooth / Cinematic), "Size" slider 0.5×–2.0× (`%.2f×`, reset 1), "Mark where you click" switch (click ring).
- **Zoom tab**
  - "ZOOM": "Zoom in on every click" switch (only with a pointer track). When zoom is enabled: "Keep zoomed in while typing" switch with caption "After a click, typing keeps the automatic zoom on that spot."; then either the empty state (§3.22) or a panel with the global "How close" slider and (with a pointer track) the "Back to one per click" link.

### 3.28 Looks and edit presets

**Looks** (one click; nothing is stored about which look is active — a look is just a set of values):

| Field | Original (`raw`) | Smooth (`clean`) | Studio (`studio`) |
|---|---|---|---|
| background | none | none | preset `graphite`, padding 0.45, corner 0.35, blur 0 |
| shape (aspect) | original | original | unchanged |
| draw pointer | on | on | on |
| smoothing | none | smooth | smooth |
| click ring | off | on | on |
| zoom enabled | off | on | on |

Applying a look then restores automatic zooms if zoom is enabled and the lane is empty. Looks never touch trim, cuts, captions, images, blurs, audio, quality or export speed. "Original" still draws the pointer because the capture never contains one.

**Edit presets** (user-named, max **12**, oldest dropped):
- "Save current preset…": sheet with a 260 pt text field (placeholder "Preset name") and "Save"/"Cancel". Name trimmed; empty → ignored. If a preset with the same name exists (case- and diacritic-insensitive) it is replaced in place (keeps its id); otherwise appended.
- A preset stores: background, aspect, draw pointer, smoothing, pointer size, click ring, zoom enabled, zoom amount, and the **image overlays** (copied into a private preset image store so they survive the recording being closed).
- Applying a preset: copies its images into the current take (fresh ids and files), each spanning the **whole** video (start 0, end = duration); replaces the document's image list (an image-free preset saved by the current version clears images; legacy presets without an image list leave images alone); sets the look fields; restores automatic zooms if needed; sanitizes. Cuts, captions, blurs, audio, trim, quality and export speed are untouched. Re-applying the same preset replaces its pictures rather than duplicating them. Busy flag disables the Presets menu while copying; failure → HUD "Couldn’t add this image."
- Removing a preset (submenu "Remove preset") deletes its private image files that no remaining preset references.
- Settings backups carry presets **without** their images (local paths are stripped); restoring on the same machine keeps the matching presets' local images; another machine never gains access to local image paths.

### 3.29 Background picker (shared with the screenshot editor)

The background and the saved custom backgrounds are the **same** data the screenshot tool uses, so a look built in one tool is available in the other.

Popover (292 pt wide, opens to the side of the inspector button), in the recorder **without** its own sliders:
- 4-column swatch grid (swatches 38 pt tall, corner 8, selected = 2.5 pt accent border): **None** (slash icon); 5 **gradient presets** (top-left → bottom-right): ocean (0.20,0.47,0.96)→(0.45,0.83,0.98), sunset (0.99,0.36,0.42)→(1.00,0.75,0.35), forest (0.07,0.56,0.43)→(0.62,0.87,0.50), candy (0.66,0.32,0.95)→(0.99,0.56,0.65), graphite (0.23,0.25,0.31)→(0.55,0.60,0.70); the user's **saved customs** (context menu "Remove"); each display's current **desktop wallpaper** (deduplicated, small badge; Windows: `IDesktopWallpaper::GetWallpaper` per monitor); an **"Image…"** dashed tile (file picker).
- Custom section: segmented "Solid | Gradient"; one or two colour wells (the active well has an accent ring); a "+" button "Save background" (enabled only for solid, gradient or image kinds); a 10-column palette of 20 colours (sRGB): (0.96,0.26,0.21) (1.00,0.58,0.00) (1.00,0.80,0.00) (0.55,0.86,0.25) (0.20,0.78,0.35) (0.10,0.74,0.61) (0.15,0.78,0.85) (0.04,0.52,1.00) (0.35,0.34,0.84) (0.69,0.32,0.87) (1.00,0.45,0.66) (0.91,0.12,0.39) (0.55,0.39,0.29) (0.11,0.16,0.32) (0.05,0.05,0.06) (0.25,0.25,0.28) (0.55,0.55,0.58) (0.85,0.85,0.87) (1,1,1) (0.99,0.93,0.85). Defaults: solid (0.20,0.47,0.96); gradient (0.20,0.47,0.96)→(0.45,0.83,0.98). Picking a palette colour paints the active well and applies immediately.
- Picking any swatch keeps the current margin/corner/blur values (spring animation 0.3 s / damping 0.85). Saved customs are normalized (padding 0.5, corner 0.1, blur 0) and deduplicated by look; max **12**.
- Selection matching ignores the sliders.

### 3.30 Export speed control

- Borderless button in the transport row: speedometer icon + current speed (locale-formatted, up to 2 decimals, e.g. "1.25×" / "1,25×"). Tooltip "Export speed". Disabled while exporting.
- Popover (320 pt, padding 18): heading "Export speed"; a 4-column grid of preset buttons **0.5×, 0.75×, 1×, 1.25×, 1.5×, 2×, 3×, 4×** (the draft value's button bold); "Custom speed" stepper (step 0.01, range 0.25–4) with the value; a slider (same range/step); range labels "0.25×" … "4×"; "Export duration" = `elapsedLabel(round(outputDuration / draft))`; note "Applies to video, GIF and shared links. The editing preview stays at 1×; the original recording is unchanged."; "Cancel" and **"Done"** (default button).
- The draft is local; **Done** writes `exportSpeed = round(draft·100)/100` as **one undoable change**; Cancel discards. The preview, timeline and all source-time data are unaffected. Speed belongs to the recording's document, not to Settings or presets (looks and presets never change it).

### 3.31 Quality menu

- Borderless menu in the transport row; label = quality name + two spaces + the **video export size** for this recording and current settings, e.g. "Balanced  2940 × 1912" (canvas size including background/shape × quality scale, even; §6.12). Tooltip "Quality". A file-size estimate is deliberately not shown (the bit rate is only a ceiling; the resolution is a fact).
- Inline choices "Small file", "Balanced", "High" (document `quality`; undoable; does not rebuild the preview).
- GIF size and GIF smoothness are **not** editable in the editor; they come from Settings when the take is first opened (stored in the document).

### 3.32 Undo/redo, persistence and change classification

- The document is a value; undo keeps whole-document snapshots (tens of bytes each); **no depth limit**.
- Any assignment of a different document pushes the previous one on the undo stack, clears redo, and **persists** `edit.json` atomically.
- **Interactions** (lane drags, zoom amount slider in "This zoom", caption typing, text/image size and opacity sliders, blur strength, anchor clicks, colour clicks, adding/removing items, cuts, regenerating zooms, aiming, drawing a blur area) snapshot the document at the start, apply changes without undo/persist while in progress, and on commit push **one** undo entry (if anything changed), persist and rebuild the preview.
- Not coalesced in the macOS build (each change event is its own undo step): pointer size slider, global zoom amount slider, background margin/corners/blur sliders, audio volume sliders. The port should coalesce these per drag as well.
- Undo/redo applies the snapshot without creating new entries, persists, rebuilds what changed, and seeks to the trim start.
- After any document change: if a selected zoom/blur no longer exists, the selection is cleared (and aiming/drawing ends). Redo never revives a stale selection.
- What a change rebuilds:
  - **Timing** (trim start/end, cuts) → rebuild the playback composition (and then the preview).
  - Else **audio** (keep system/mic, gains) → re-apply the audio mix only.
  - Else **picture** (background, aspect, pointer on/smoothing/size, click ring, zoom enabled/amount/segments, cuts, texts, images, blurs) → rebuild the preview compositor (debounced 120 ms).
  - Export-only fields (quality, export speed, GIF size/fps) → persisted and undoable, nothing rebuilt.
- Preview rebuild is suppressed while aiming or drawing; it runs when they end.

### 3.33 Keyboard shortcuts (editor window key)

| Keys (macOS → suggested Windows) | Action |
|---|---|
| ⌘S → Ctrl+S | Save (MP4) — works even while typing in a caption field |
| ⇧⌘S → Ctrl+Shift+S | Save as… — works while typing |
| ⌘Z / ⇧⌘Z → Ctrl+Z / Ctrl+Shift+Z (or Ctrl+Y) | Undo / Redo |
| ⌘C → Ctrl+C | Copy video |
| ⌥⌘C → Ctrl+Alt+C | Copy and delete |
| Space | Play / pause |
| Delete / Forward Delete | In order: cut the selection → remove selected zoom → selected text → selected image → selected blur → otherwise **discard the recording** (with confirmation) |
| Esc | In order: cancel the running export → end aiming → end blur drawing → deselect zoom → text → image → blur → otherwise pass through. The inspector's "All options" links are also bound to Esc. |
| Shared-link sheet | Return = Done, ⌘C = Copy link |

While a text field has focus, every key goes to the field except ⌘S and ⇧⌘S. Keys are handled only when the event targets the editor window (or the editor is key and the event has no window).

### 3.34 Export actions

Only one export can run per editor; all export/copy/share buttons and the speed control are disabled meanwhile. Starting any export pauses playback and snapshots the current document. Every successful export (including copies) marks the editor as **"exported"** (closing it no longer asks) and adds the produced file to **Recent captures** (thumbnail = first frame, max 360 px).

| Action | Output | Destination | On success |
|---|---|---|---|
| **Save** (primary button, ⌘S, menu "Save") | MP4 video | Save folder (§4 `recorderSaveFolder`; tilde-expanded; used only if it exists and is a directory) else the Desktop (else home). Name `Recording yyyy-MM-dd at HH.mm.ss.mp4`; if taken, `… 2.mp4`, `… 3.mp4` … up to 9999. | Finished-file chip; HUD "Saved to <folder name>". Window stays open (a GIF of the same recording is a common next step). |
| **Save as…** (⇧⌘S) | MP4 | Save dialog attached to the window: file type MPEG-4, starting in the default folder with the default name; folders can be created. May target an existing file. | Same as Save. An existing file is replaced only after the new one is complete. |
| **Save as GIF** | GIF | Same folder/naming with `.gif`. | Same as Save. |
| **Save to…** | — | Folder picker (directories only, create allowed). Sets `recorderSaveFolder`. | — |
| **Copy** (⌘C, split button) | MP4 | `<user caches>/<app id>/Copied Recordings/` (created on demand; before each copy, files there modified more than 24 h ago are deleted). Same naming. | Clipboard cleared and set to the **file** (file URL), plus the Vorssaint source marker; HUD "Recording copied". No chip. Clipboard write failure → beep + "The recording could not be saved". |
| **Copy as GIF** (split menu) | GIF | Same cache folder. | The file is read back and verified to be a GIF with ≥ 1 frame **before** the clipboard is touched; then one clipboard item with the raw GIF bytes (native GIF type) **and** the file URL; source marker; HUD "Recording copied". Verification/clipboard failure → beep + "The recording could not be saved" and the previous clipboard content survives. |
| **Copy and delete** (⌥⌘C) | MP4 | Same cache folder. | As Copy, then the window closes **without confirmation** and the take is deleted. |

Failures (all actions): cancelled → nothing shown; GIF too long → HUD "A GIF can be up to %d seconds long" with `floor(300 / gifFps)` (37 / 25 / 20 s at 8 / 12 / 15 fps; measured on the **export** duration, i.e. after speed); any other failure → system beep + HUD "The recording could not be saved".

Export never modifies or retimes the master.

### 3.35 Export progress, cancel and atomic commit

- Video progress: `min(0.9, 0.9 · framesWritten / expectedFrames)` while frames are appended (`expectedFrames = max(1, round(exportDuration · fps))`), 0.95 before the file is finalized, 1.0 when done — the bar never parks near 100% while audio is still being written. GIF: `min(0.99, (i+1)/frameCount)` per frame, then 1.0. Sharing: compression progress as video, then a spinner while uploading.
- **Cancel**: the chip's "Cancel", Esc, or closing the window. The flag is read on every frame by the encoder pumps, so cancel lands within a frame. A cancel that arrives during finalization still prevents the destination from being replaced.
- **Staging**: every Save/Save as/Save as GIF/Copy writes to a hidden sibling of the destination named `.vorssaint-partial-<UUID>.<same extension>` and, only when complete and not cancelled, moves it into place (atomic replace if the destination exists, plain move otherwise). The staging file is always removed. Result: a cancelled or failed export leaves an existing file at the destination byte-for-byte untouched and nothing beside it. (Windows: set the hidden attribute on the staging file; commit with `ReplaceFileW` or `MoveFileExW(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)`.)
- GIFs are encoded fully in memory and written once at the end (so a cancel never leaves an encoder temp file in the user's folder).

### 3.36 Discard and close

- **Discard** (trash button, or Delete with nothing selected): pause; confirmation sheet (warning style): title "Delete this recording?", message "It has not been saved anywhere yet." — or, if anything was exported or copied from this editor, "Saved and copied files will stay where they are."; buttons **"Delete"** (first) and "Cancel". Delete closes the window.
- **Window close button**: a running export is cancelled first. If nothing was exported and the close was not already confirmed → the same confirmation sheet, and the close is vetoed until "Delete" is chosen. If something was exported → closes immediately.
- **On close** (always): pause, cancel export/share, **delete the take folder**, remove keyboard handling, release the app-activation hold. Files already saved or copied stay where they are.

### 3.37 Temporary links (sharing)

**Availability**: only when "Allow temporary links" (`recorderSharingEnabled`, default on) is on — the editor's link menu is hidden otherwise and the service refuses to upload.

**Flow** (editor link menu → "For 1 hour" / "For 6 hours"):
1. Phase "Compressing for sharing…". The share export is produced **only** from a take the recorder itself owns (the take folder must be exactly `<Recordings>/Take-<id>`, a real directory, not a symlink, with a regular non-empty master) and the current document — there is no generic "upload this file" path.
2. Encode an MP4 (H.264 High, AAC) into `<user caches>/<app id>/Temporary Recording Uploads/<UUID>.mp4` (folder owner-only 0700; files older than 24 h purged each time). Optimized for streaming ("fast start" / moov first). Encoding plan, size/bit-rate retry loop and limits: §6.19. Speed, trim, cuts, all overlays, blurs and the audio mix are applied exactly like a normal export.
3. Validate the artifact: inside that folder, regular file, not a symlink, 0 < size ≤ 96,000,000 bytes, exactly one video track with H.264 only, at most one audio track with AAC only, no other track types. Invalid → failure.
4. Phase "Uploading securely…" (indeterminate spinner).
5. Re-check sharing is enabled and the file is valid; build the upload URL; ensure the local link store can be written **before** uploading (else fail).
6. `POST {endpoint}/v1/recordings?expiresIn={3600|21600}` with the file as the body. Headers: `Content-Type: video/mp4`, `Accept: application/json`, `Cache-Control: no-store`, `Content-Length: <bytes>`. HTTP client: no cookies, no cache, no credential store; request timeout 180 s, resource timeout 300 s; do not wait for connectivity.
7. Response must be **201** with a JSON body ≤ 64 KiB: `{"id": "<32 chars [A-Za-z0-9_-]>", "viewPath": "/s/<id>", "expiresAt": "<ISO-8601, fractional seconds optional>", "deleteToken": "<43 chars [A-Za-z0-9_-]>"}`; `expiresAt` must be in the future and no later than now + 6 h 5 min. Any mismatch → invalid response. HTTP 429, 503, 507 → "unavailable"; other statuses → "rejected"; transport error → "unavailable".
8. Store the record `{id, endpoint, expiresAt, deleteToken}` in the local link store (§5.9). If storing fails after a successful upload, the app tries to delete the link remotely: if that delete succeeds it reports failure; if the delete also fails, it keeps the record in memory for this session and reports success (the user still gets a working, deletable link until the app quits).
9. The temporary MP4 is deleted in every case (success, failure, cancel).
10. Success → the **link sheet** opens.

Endpoint: production `https://screenshots.vorssaint.com` (shared with screenshot links). A developer override (setting `screenshotSharingDeveloperEndpoint`) is honoured only in the developer build (bundle id `com.vorssaint.utils.dev`) and only if it is `https`, has a host, no user/password, no query/fragment and an empty or "/" path. Public link URL = `{endpoint}/s/{id}`.

**Link sheet** (480 pt wide, padding 20): title "Shared links" with link icon, "Done" (Return); a card with the URL (rounded font, selectable, middle-truncated) and "Expires <relative time>" (live, e.g. "in 59 minutes"); "Copy link" (⌘C) → clipboard plain text URL (+ source marker), HUD "Link copied" (failure: beep); "Delete now" (destructive, spinner while running) → remote delete, HUD "Link deleted", sheet closes; failure → alert "The link could not be deleted". The link is **not** auto-copied when created.

**Errors**: too large (cannot reach ≤ 96 MB with acceptable quality, or the plan refuses) → beep + HUD "This recording cannot fit under 100 MB without losing too much quality."; any other failure → beep + HUD "The temporary link could not be created"; cancel → silent.

**Delete** (from the sheet or Settings): if not yet expired, `DELETE {endpoint}/v1/recordings/{id}` with `Authorization: Bearer {deleteToken}`, `Accept: application/json`; 204 or 404 = success; anything else = failure. Then the record is removed locally (if the store cannot be written, the removal is remembered in memory so the link does not reappear this session).

**Link store maintenance**: on launch, when Settings opens the section, on system wake, and at the next expiry (+0.1 s, timer re-armed each time): expired records are dropped, duplicates resolved by latest expiry, sorted by expiry. Wake matters because timers do not fire during sleep.

**Settings section "Temporary links"**: "Allow temporary links" switch; caption (when on) "Choose 1 or 6 hours. The final video is compressed on this Mac to fit under 100 MB and deleted automatically."; "Privacy" button (hand icon) → privacy sheet; when active links exist, a row "Shared links  <count>  ›" → list sheet (560×360): empty state "No active links"; each row: link icon, URL (selectable), "Expires <relative>", buttons copy, open in browser ("Open"), delete ("Delete now", spinner; only one delete at a time; failure alert "The link could not be deleted", success HUD "Link deleted").

**Privacy sheet** (560×390): title "Privacy for temporary links", "Done"; three paragraphs with icons (video, drive, people):
1. "Vorssaint sends only the final video created from this recording, including the audio you kept, and the expiration you choose. It does not send your name, account or device identifier."
2. "Network providers and the service temporarily process your public IP to prevent abuse. The video and link metadata are permanently deleted when you delete the link or its time ends. The service does not create backups."
3. "Anyone with the link can view, download, save or redistribute the video. Active links are available to the service operator for abuse moderation. Share only with people you trust."

### 3.38 HUD messages (toasts)

Small non-activating panel, top-centre of the screen with the mouse (24 pt below the top of its visible area), icon + 12 pt semibold text (max 2 lines, 360 pt), material background, fade in 0.12 s, auto-dismiss after **1.5 s** (fade 0.22 s). A HUD visible when a recording starts is excluded from the capture.

| Message | When |
|---|---|
| "The screen could not be recorded" | take/session creation failed, capture failed to start (non-permission), nothing was written, direct save failed, the chooser failed in recording mode |
| "Microphone unavailable" | mic wanted but permission denied/unknown, or the mic failed to start |
| "Recording stopped, the disk is almost full" | auto-stop below 500 MB (also used as the direct-save HUD) |
| "Saved to %@" (folder name) | direct save after recording; Save / Save as / Save as GIF |
| "Recording copied" | Copy, Copy as GIF, Copy and delete |
| "The recording could not be saved" | export or clipboard failure (with beep) |
| "A GIF can be up to %d seconds long" | GIF over the 300-frame budget |
| "Couldn’t add this image." | image import / preset image copy failed |
| "This recording cannot fit under 100 MB without losing too much quality." | share too large (with beep) |
| "The temporary link could not be created" | share failed (with beep) |
| "Link copied" / "Link deleted" | link sheet / list actions |
| Countdown HUD | §3.5 |

Alerts: "Not enough space to record" / "Free up some space on the disk and try again." (modal); "Delete this recording?" (sheet); "The link could not be deleted".

---

## 4. Settings

Settings live on the shared "Screen capture" page (tool picker at the top; Screen recording selected). Top section: "Screen recording" shortcut switch + shortcut recorder + "Show capture menu when using keyboard shortcut" (enabled only with the shortcut on) + a note if the shortcut could not be registered; plus the shared "Recent captures" shortcut rows.

Recorder section layout:
1. Header "Screen recording": button "Record now" / "Stop recording"; caption "Record an area, window or the whole screen" (or the elapsed time while recording); permission rows when missing (macOS).
2. "Countdown" segmented (Off / 3 s / 5 s / 10 s); "Record the sound of the Mac" + caption "Everything you hear goes into the recording, on its own track, so you can silence it later."; "Record the microphone" + caption "Your voice goes into its own track and stays adjustable in the editor." (turning it on with undetermined permission requests it; a permission row appears while not granted); "Open the editor after recording" + caption "The recording opens ready to trim, mute and save. Turn this off to get the file straight away."; "Add zooms automatically" + caption "Turn this off to start new recordings without zooms. You can still add them in the editor."
3. "Save to" row: current folder display name (Desktop when unset/missing), a reset (×) button when set, "Choose…" (folder picker); "More options" disclosure → "Quality" (Small file / Balanced / High) + caption "Balanced fits most uses. High keeps every detail and makes bigger files."; "Frames per second" (30 fps / 60 fps); "GIF size" (Small / Medium / Large); "GIF smoothness" (8 fps / 12 fps / 15 fps).
4. "Temporary links" section (§3.37).

| Key | Type | Default | Values / range | Meaning | In settings backup |
|---|---|---|---|---|---|
| `recorderShortcutEnabled` | bool | `false` | — | Dedicated recording shortcut active | yes |
| `recorderShortcut` | string | `control+option+command:23` (⌃⌥⌘5) | shortcut | Dedicated shortcut (opens the chooser in Recording mode / stops) | yes |
| `recorderShowCaptureMenuOnShortcut` | bool | `true` | — | Show the 1–4 mode palette when opened via the shortcut | yes |
| `recorderCountdown` | int | `3` | 0, 3, 5, 10 (else 0) | Seconds before capture starts | yes |
| `recorderQuality` | string | `balanced` | `small`, `balanced`, `high` (else balanced) | Initial export quality of **new** takes | yes |
| `recorderFrameRate` | int | `60` | 30, 60 (else 60) | Capture max fps | yes |
| `recorderSystemAudio` | bool | `true` | — | Record system audio; also initial "keep Mac sound" of new takes | yes |
| `recorderMicrophone` | bool | `false` | — | Record the microphone | yes |
| `recorderSystemAudioTapVerified` | bool | unset (false) | — | macOS-only tap trust (§3.8). Drop on Windows. | **no** |
| `recorderSaveFolder` | string (path) | `""` | path, `~` expanded; `""` = Desktop | Save destination | **no** (machine-local) |
| `recorderOpenEditor` | bool | `true` | — | Open the editor after recording (else save raw `.mov`) | yes |
| `recorderAutomaticZoom` | bool | `true` | — | New takes start with automatic zooms | yes |
| `recorderGIFSize` | string | `medium` | `small` (420), `medium` (600), `large` (800) | GIF long edge for **new** takes | yes |
| `recorderGIFFrameRate` | int | `12` | 8, 12, 15 (else 12) | GIF fps for **new** takes | yes |
| `recorderEditorPresets` | data (JSON array) | empty | ≤ 12 presets (§5.7) | User edit presets | yes, **images stripped** |
| `recorderSharingEnabled` | bool | `true` | — | Allow temporary links | yes |
| `panelUtilityScreenRecorder` | bool | `true` | — | Show the panel tile | yes |
| `screenshotBackdropPresets` | string (JSON array) | `"[]"` | ≤ 12 backdrop styles (§5.8) | Saved custom backgrounds, **shared with screenshots** | yes |
| `screenshotSharingDeveloperEndpoint` | string | unset | https origin | Developer-build-only share endpoint | **no** |

Defaults migration notes (macOS history, informative only): the dedicated recorder shortcut was once merged into a unified capture shortcut and later restored; migrations ensure the same combination is never registered twice. A new Windows install needs none of this.

---

## 5. Data and files

### 5.1 Locations

| Item | macOS location | Suggested Windows location |
|---|---|---|
| App container | `~/Library/Application Support/<bundle id>/` (dirs 0700, files 0600, re-tightened on every write) | `%LOCALAPPDATA%\Vorssaint\` (per-user ACL; not roaming) |
| Takes | `<container>/Recordings/Take-<UUID>/` | `%LOCALAPPDATA%\Vorssaint\Recordings\Take-<UUID>\` |
| Preset images | `<container>/RecorderPresetImages/<UUID>/<file>` | `%LOCALAPPDATA%\Vorssaint\RecorderPresetImages\<UUID>\<file>` |
| Link store | `<container>/TemporaryRecordingLinks/records.json` | `%LOCALAPPDATA%\Vorssaint\TemporaryRecordingLinks\records.json` (consider DPAPI for tokens) |
| Copy cache | `~/Library/Caches/<bundle id>/Copied Recordings/` | `%LOCALAPPDATA%\Vorssaint\Cache\Copied Recordings\` |
| Share staging | `~/Library/Caches/<bundle id>/Temporary Recording Uploads/` (0700) | `%LOCALAPPDATA%\Vorssaint\Cache\Temporary Recording Uploads\` |
| Default save folder | Desktop | Desktop for parity (Videos is a reasonable alternative) |

### 5.2 Take folder layout

```
Recordings/
  Take-<UUID uppercase>/          created owner-only when recording starts
    take.mov                      master movie (written during recording)
    pointer.bin                   pointer track (written at stop, only if non-empty)
    typing.json                   keystroke times (written at stop, only if non-empty)
    edit.json                     edit document (written on first persist, then on every change)
    <UUID>/<original file name>   one folder per imported overlay image (0700 / file 0600)
```

The folder name is the only index: `Take-` + UUID string; anything else in `Recordings/` is ignored. "Finished at" = the master's modification time; "created at" = the folder's creation time (used by the sweep, §3.18). Lifetime = the editor's lifetime.

### 5.3 Master movie (`take.mov`)

| Aspect | Value |
|---|---|
| Container | QuickTime MOV, movie fragments every 10 s |
| Video | HEVC (fallback H.264 High auto-level), region size (even), VFR (frames only on change), max fps 30/60, ABR `clamp(W·H·fps·0.09, 0.8, 60) Mb/s`, max key-frame interval 2 s, no B-frames, BT.709 tags |
| First frame | at t = 0 (held over start-up latency); last frame re-appended at the stop time |
| Audio | 0–2 tracks, each AAC-LC 48 kHz stereo 160 kb/s, tagged with QuickTime metadata `com.apple.quicktime.content.identifier`-style key = `com.vorssaint.recorder.audio.system` / `.microphone` |
| Cursor | never present |
| Timeline | pauses removed; all tracks share the origin |

Windows recommendation: fragmented MP4 (`take.mp4`) with **H.264** (HEVC decode needs a separately installed extension on many PCs — the editor must always be able to decode its own master), and a side-car `take.json` manifest (see §8.4) instead of in-container track tags.

### 5.4 Pointer track `pointer.bin` (binary, little-endian, version 3)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `56 52 50 54` ("VRPT") |
| 4 | 2 | version `u16` = 3 (any other version → treated as "no track") |
| 6 | 2 | reserved `u16` = 0 |
| 8 | 4 | `systemScale` f32 (accessibility pointer scale, ≥ 1; invalid → 1) |
| 12 | 4 | `displayScale` f32 (pixels per point of the recorded display; invalid → 2) |
| 16 | 4 | `sampleCount` u32 |
| 20 | 4 | `clickCount` u32 |
| 24 | 4 | `shapeCount` u32 |
| 28 | 16 × N | samples: `time` f32 (source s), `x` f32, `y` f32 (normalized, top-left, may be outside 0…1), `shapeIndex` u16, `visible` u16 (0/1) |
| … | 8 × M | clicks: `time` f32, `isDown` u32 (0/1) |
| … | variable × K | shapes: `hotX` f32, `hotY` f32 (bitmap pixels from top-left), `width` f32, `height` f32 (bitmap pixels = points), `pngLength` u32, then `pngLength` bytes of PNG |

Decoding rules: shorter than 28 bytes, wrong magic or version → empty track. Counts are capped by the bytes actually present (a truncated file keeps every whole record; declared counts never drive allocation). Records with non-finite values are skipped; shapes with zero length, non-finite hot spot or non-positive size are skipped; a shape running past the end stops shape decoding. A sample's shape index outside the table falls back to shape 0.

Windows: keep the format (version 4 if semantics change, e.g. `displayScale = 1` and pixel-sized bitmaps, plus optional per-sample window-bounds for window recordings).

### 5.5 Typing track `typing.json`

`{"times": [0.84, 1.02, 1.31, …]}` — source seconds of non-repeat key-downs; nothing else. Unreadable → empty.

### 5.6 Edit document `edit.json`

No explicit version field: every key is optional on read and falls back to the default below; unknown keys are ignored; an unreadable file opens the recording untouched. All times are **source seconds**. All rects/points are **normalized top-left**.

| Key | Type | Default (new take) | Sanitized range / rule |
|---|---|---|---|
| `trimStart` | number | 0 | §6.4 (inside recording; min length 0.2 s) |
| `trimEnd` | number | 0 (= "to the end") | §6.4; after sanitize it holds the real end |
| `quality` | string | from Settings (`balanced`) | `small` / `balanced` / `high`, else `balanced` |
| `exportSpeed` | number | 1 | 0.25…4 (non-finite or ≤ 0 → 1); UI rounds to 0.01 |
| `keepsSystemAudio` | bool | from Settings (`true`) | — |
| `gifSize` | string | from Settings (`medium`) | `small`/`medium`/`large`, else `medium` |
| `gifFrameRate` | int | from Settings (12) | 8/12/15, else 12 |
| `backdrop` | string | `""` | JSON string of a BackdropStyle (§5.8), `""` = none; broken → none |
| `aspect` | string | `original` | `original`, `wide` (16:9), `square` (1:1), `vertical` (9:16) |
| `showsPointer` | bool | true | — |
| `pointerSmoothing` | string | `smooth` | `off`, `light`, `smooth`, `cinematic`, else `smooth` |
| `pointerSize` | number | 1 | 0.5…2.0 (non-finite → 1) |
| `showsClickRing` | bool | true | — |
| `zoomEnabled` | bool | from Settings (`true`) | — |
| `zoomAmount` | number | 1.8 | 1.2…3.0 (non-finite → 1.8) |
| `zoomsOnTyping` | bool | false | — |
| `cuts` | array of `{start, end}` | [] | clamped, ≥ 0.1 s, sorted, merged when touching |
| `zoomSegments` | array of ZoomSegment | [] | clamped, amount sanitized, focus clamped 0…1, ≥ 0.4 s, sorted, overlaps resolved by moving the later start (dropped if it becomes < 0.4 s) |
| `zoomsGenerated` | bool | false | set true once automatic zooms were generated (even if none) |
| `texts` | array of TextOverlay | [] | text ≤ 200 chars, clamped times, ≥ 0.2 s, size 0.03…0.16 (non-finite → 0.06), sorted by start |
| `images` | array of ImageOverlay | [] | absolute path required (macOS: starts with `/`, no NUL), clamped times ≥ 0.2 s, size 0.04…0.6 (→ 0.18), opacity 0.05…1 (→ 1), sorted |
| `blurs` | array of BlurRegion | [] | clamped times ≥ 0.2 s, rect clamped to the picture, each side ≥ 0.01, strength 1…5, sorted |
| `keepsMicrophone` | bool | true | — |
| `systemAudioGain` | number | 1 | 0…1 (non-finite → 1) |
| `microphoneGain` | number | 1 | 0…1 (non-finite → 1) |

Nested types:

- **ZoomSegment**: `id` (UUID string), `start`, `end`, `amount` (1.2…3.0), optional `focusX`, `focusY` (0…1; both present = hand-aimed, otherwise follows the pointer).
- **Cut**: `start`, `end`.
- **TextOverlay**: `id`, `text`, `start`, `end`, `anchor` (`topLeading`, `top`, `topTrailing`, `leading`, `center`, `trailing`, `bottomLeading`, `bottom`, `bottomTrailing`), `size` (fraction of canvas height), `palette` (`white`, `black`, `accent`, `yellow`, `red`, `green`).
- **ImageOverlay**: `id`, `path` (absolute path of the private copy), `start`, `end`, `anchor` (same 9 values), `size` (fraction of canvas width), `opacity` (0.05…1).
- **BlurRegion**: `id`, `start`, `end`, `x`, `y`, `width`, `height` (normalized), `strength` (int 1…5, optional on read → 3).

Example:

```json
{
  "trimStart": 1.25, "trimEnd": 41.8, "quality": "balanced", "exportSpeed": 1.25,
  "keepsSystemAudio": true, "keepsMicrophone": true, "systemAudioGain": 0.6, "microphoneGain": 1,
  "gifSize": "medium", "gifFrameRate": 12,
  "backdrop": "{\"kind\":\"preset\",\"presetID\":\"graphite\",\"padding\":0.45,\"cornerRadius\":0.35,\"blur\":0}",
  "aspect": "wide", "showsPointer": true, "pointerSmoothing": "smooth", "pointerSize": 1.2,
  "showsClickRing": true, "zoomEnabled": true, "zoomAmount": 1.8, "zoomsOnTyping": false,
  "zoomsGenerated": true,
  "cuts": [{"start": 12.4, "end": 15.0}],
  "zoomSegments": [
    {"id": "6F1E…", "start": 2.7, "end": 5.5, "amount": 1.8},
    {"id": "A03B…", "start": 20.0, "end": 22.0, "amount": 2.2, "focusX": 0.81, "focusY": 0.12}
  ],
  "texts": [{"id": "…", "text": "Click Save", "start": 3, "end": 6, "anchor": "bottom", "size": 0.06, "palette": "white"}],
  "images": [{"id": "…", "path": "/…/Take-…/9C…/logo.png", "start": 0, "end": 41.8, "anchor": "bottomTrailing", "size": 0.18, "opacity": 1}],
  "blurs": [{"id": "…", "start": 8, "end": 41.8, "x": 0.35, "y": 0.4, "width": 0.3, "height": 0.2, "strength": 3}]
}
```

Derived predicates used by the app (keep them; they drive rebuilds): *affects picture* (backdrop, aspect, pointer fields, click ring, zoom enabled/amount/segments, cuts, texts, images, blurs), *affects timing* (cuts, trimStart, trimEnd), *affects audio* (keeps/gains). (`isEdited` exists but is unused; the close prompt depends on "exported", not on edits.)

Persistence: atomic whole-file write on every committed change. The document is never written while a drag is in progress.

### 5.7 Edit presets (`recorderEditorPresets`, JSON array, max 12)

Each: `id` (UUID), `name`, `backdrop` (string as in §5.6), `aspect`, `showsPointer`, `pointerSmoothing`, `pointerSize`, `showsClickRing`, `zoomEnabled`, `zoomAmount`, optional `images` (ImageOverlay array whose paths point into the preset image store; absent in legacy presets).

Preset image store rules: images are captured into `RecorderPresetImages/<UUID>/<original name>` (each copy validated, 0600); restoring requires every path to be inside the store (`<store>/<UUID>/<file>`, no symlinked folders) and copies them into the take (all or nothing, rolled back on failure; never recreates a closed take). Removing/replacing a preset deletes only image folders no remaining preset references.

### 5.8 Backdrop style (shared with the screenshot tool)

JSON object: `kind` (`none`, `preset`, `solid`, `gradient`, `image`), optional `presetID` (`ocean`, `sunset`, `forest`, `candy`, `graphite`), optional `colors` (solid: 1 × `[r,g,b]`; gradient: 2 × `[r,g,b]`, sRGB 0…1), optional `imagePath` (absolute), `padding` 0…1 (default 0.5), `cornerRadius` 0…1 (default 0; non-finite → 0.1), `blur` 0…1 (default 0; optional on read). Sanitizing clamps sliders and **demotes any malformed style to `none`** (unknown preset, wrong colour count, empty image path). In the edit document `backdrop = ""` means none. `screenshotBackdropPresets` is a JSON array of these, max 12, `none` entries dropped.

### 5.9 Link store `records.json`

JSON array of `{ "id": String, "endpoint": URL string, "expiresAt": Date, "deleteToken": String }`. (macOS encodes the date as seconds since 2001-01-01; use ISO-8601 or Unix seconds on Windows — the format is local only.) Written atomically, owner-only. Only unexpired records are kept.

### 5.10 Retention summary

| Data | Lifetime |
|---|---|
| Take folder | Until its editor closes; orphans swept after 24 h (master present) or 1 h (no master) |
| Overlay image copies | Inside the take folder (die with it) |
| Copy cache files | Purged (> 24 h old) at the next copy |
| Share staging MP4 | Deleted right after upload; leftovers > 24 h purged at the next share |
| Link records | Until expiry or deletion |
| Preset images | Until no preset references them |
| Recent-captures entries | Owned by the Recent captures feature |


---

## 6. Algorithms and constants

Notation: `clamp(x, a, b)`, `even(v)` = §6.1 `evenSide`, `smoothstep(x) = x²(3 − 2x)`, `smootherstep(x) = x³(10 − 15x + 6x²)`, both with x clamped to [0, 1]. `fps` = the plan's frame rate. `D` = master duration (source seconds).

### 6.1 Region snapping

```
evenSide(v):  if v not finite → 32
              r = max(32, floor(v));  return r even ? r : r − 1
evenSize(w,h) = (evenSide(w), evenSide(h))

snappedPixelRect(rect, bounds):           // bounds = display in pixels
  if rect has a non-finite field → (bounds.origin, evenSize(bounds.size))
  c = rect ∩ bounds   (empty → bounds)
  origin = floor(c.origin);  size = evenSize(c.size)
  if origin.x + size.w > bounds.maxX: origin.x = max(bounds.minX, floor(bounds.maxX − size.w))
  if origin.y + size.h > bounds.maxY: origin.y = max(bounds.minY, floor(bounds.maxY − size.h))
  size = evenSize(min(size.w, bounds.maxX − origin.x), min(size.h, bounds.maxY − origin.y))
```

Vectors: (10.7, 20.2, 641.4 × 401.9) in 2940×1912 → origin (10, 20), size 640 × 400. (2900, 1900, 400 × 400) → stays inside, each side ≥ 32. NaN x → whole display.

### 6.2 Pause clock

State: `origin` (set once, finite), closed intervals `[ps, pe)`, optional open pause `p0`.

```
excluded(lo, hi) = Σ max(0, min(hi, pe) − max(lo, ps))  +  (open ? max(0, hi − max(lo, p0)) : 0)
elapsed(t)       = t ≤ origin ? 0 : max(0, t − origin − excluded(origin, t))
overlapsPause(s, e):  instant (e ≤ s): s ∈ [ps, pe) for some interval, or (open and s ≥ p0)
                      interval:        s < pe and e > ps for some interval, or (open and e > p0)
sampleTime(start, dur) = (start < origin or overlapsPause(start, start + max(0,dur))) ? DROP : elapsed(start)
eventTime(t)           = (t < origin or overlapsPause(t, t))                          ? DROP : elapsed(t)
pause(t): only if origin set and not paused;  resume(t): closes [p0, max(p0, t))
```

Vectors (origin 0): pause at 3 → `elapsed(7) = 3`, `sampleTime(5) = DROP`; resume at 8 → `sampleTime(2) = 2`, `sampleTime(8) = 3`, `eventTime(10) = 5`, `sampleTime(2.99, 0.02) = DROP`, `sampleTime(−0.01) = DROP`; pause 12–14 → `eventTime(13) = DROP`, `elapsed(16) = 9`. A later `begin` cannot move the origin.

### 6.3 Encoder bit rate and quality presets

```
bitRate(W, H, fps, preset) = round(clamp(max(1,W) · max(1,H) · max(1,fps) · bpp, 800 000, 60 000 000))
```

| Preset | Output scale | bpp (bits/pixel/frame) | Max key-frame interval | Use |
|---|---|---|---|---|
| Small file (`small`) | 0.5 (only preset that drops pixels) | 0.05 | 10 s | export |
| Balanced (`balanced`, default) | 1 | 0.06 | 10 s | export |
| High (`high`) | 1 | 0.09 | 2 s | export **and always the master** |

Examples: 2940×1912 @ 60 → High 30.35 Mb/s, Balanced 20.24 Mb/s; Small at 1470×956 → 4.22 Mb/s; a 64×64 area still gets 0.8 Mb/s; 5K @ 60 caps at 60 Mb/s. The bit rate is a ceiling; flat screen content undershoots it heavily, so the UI shows the **resolution**, never an estimated file size.

### 6.4 Timeline: trim, cuts, clocks, export speed

```
sanitizedTrim(start, end, D):
  D ≤ 0 or non-finite → (0, 0)
  e = (end finite and end > 0) ? min(end, D) : D          // 0 means "to the end"
  s = start finite ? clamp(start, 0, D) : 0
  span = min(0.2, D)
  if e − s < span:  s' = max(0, min(s, D − span));  return (s', s' + span)
  return (s, e)

normalizeCuts: clamp each to [0, D]; drop if end − start < 0.1; sort by start;
               merge when next.start ≤ last.end (end = max)

keptRanges(trim, cuts):
  if trim length ≤ 0 → []
  cursor = trim.start
  for cut in cuts (sorted):
    s = max(cut.start, trim.start); e = min(cut.end, trim.end); if e ≤ s: continue
    if s > cursor: emit [cursor, s]
    cursor = max(cursor, e)
  if trim.end > cursor: emit [cursor, trim.end]
  drop ranges ≤ 0.001 s

outputDuration = Σ range lengths
sourceTime(forOutput o): r = max(0, o); for each range: if r ≤ len → range.lo + r; else r −= len
                          past the end → last range's end; no ranges → trim.start
outputTime(forSource s): acc = 0; for each range: if s < lo → nil (cut out / before trim)
                          if s ≤ hi → acc + (s − lo); acc += len;  after all → nil

exportSpeed v = clamp(v, 0.25, 4) (non-finite or ≤ 0 → 1)
exportTime  = outputTime / v;    editedTime(forExport T) = T · v
exportDuration = outputDuration / v
```

Vectors: trim [0, 20], cut [5, 8] → ranges [0,5], [8,20], duration 17; `sourceTime(4) = 4`, `sourceTime(6) = 9`, `outputTime(6.5) = nil`, `outputTime(12) = 9`; the two clocks are inverse wherever the moment survives. Cuts [2,6] + [5,9] → [2,9]; cut [3, 3.02] → dropped. Trim (8, 8) on 12 s → length 0.2. Speed: trim [2, 12], cut [4, 6], D = 20, v = 1.25 → kept [2,4], [6,12], output 8 s, export 6.4 s; export time 2.4 → edited 3.0 → source 7.0. Everything cut at 4x → 0.

Cut rules in the UI: a cut selection must be ≥ 0.1 s; cutting is refused if the remaining output would be < 0.4 s.

### 6.5 Automatic zoom generation (from clicks and typing)

Constants: lead-in **0.3 s**, hold **2.5 s**, merge gap **2.5 s**, ignore presses in the last **1.0 s** (the click that stopped the recording), end margin **0.8 s**, minimum length **0.9 s**, typing tail **1.4 s**, typing continuation window **8.0 s**.

```
generate(clicks, typingTimes, D, amount):
  if D ≤ 0 → []
  presses = sorted { c.time | c.isDown and c.time < D − 1.0 }        // left and right buttons
  merged = []
  for p in presses:
    seg = [max(0.001, p − 0.3), p + 2.5]
    if merged not empty and seg.start − merged.last.end ≤ 2.5: merged.last.end = max(merged.last.end, seg.end)
    else append seg
  for t in sorted finite typingTimes (only when "Keep zoomed in while typing" is on):
    i = LAST index with merged[i].start ≤ t ≤ merged[i].end + 8.0;  if none: continue
    merged[i].end = max(merged[i].end, t + 1.4)
  limit = max(0, D − 0.8)
  result = for seg in merged: seg.end = min(seg.end, limit); keep if seg.end − seg.start ≥ 0.9
  → ZoomSegment(start, end, amount = sanitized(document.zoomAmount), focus = nil)
```

Consequences: presses up to **5.3 s** apart become one continuous zoom (each segment spans 2.8 s); typing never creates a zoom by itself, only extends the zoom of the click that focused the field; the zoom begins 0.3 s **before** the click (only possible offline). Vectors: presses 2.0, 2.4, 3.1 (D 20) → one zoom starting 1.7; presses 2 and 12 → two; press 9.6 with D 10 → none; press 2 + typing 4, 5, 6 → end ≥ 7.4; press 2 + typing 9, 10 → end ≥ 11.4; typing only → none; every zoom ends ≤ D − 0.8.

### 6.6 Zoom state over time (ramps)

Ramp in **0.42 s** (shortened to half the segment if shorter), ramp out **0.65 s** after the segment's end, both **smootherstep**.

```
zoomState(t, segments):                      // segments normalized & sorted, source time
  fading = nil
  for seg in segments:
    if seg.start − 0.001 ≤ t ≤ seg.end: return state(seg, t)      // the owner wins
    if seg.end < t ≤ seg.end + 0.65:   fading = seg
  return fading ? state(fading, t) : (progress 0, amount 1, focus nil)

state(seg, t):
  rampIn = min(0.42, seg.length / 2)
  if t ≥ seg.end:                              p = smootherstep(1 − (t − seg.end) / 0.65)
  else if rampIn > 0 and t ≤ seg.start + rampIn: p = smootherstep((t − seg.start) / rampIn)
  else                                         p = 1
  return (p, seg.amount, seg.focus)
```

Two touching zooms hand over directly (the second owns its moments; the first's ramp-out is ignored), so the picture never zooms out and snaps back in. Zoom disabled → no segments are active (they stay on the lane).

### 6.7 Camera: focus, travel, viewport and spring

Computed once per plan for every output frame `i` (time `i/fps` on the edited clock; `s_i` = its source time).

1. **Amount target**: `A_i = 1 + (state.amount − 1) · state.progress`.
2. **Travel target** (where the zoomed viewport sits within its own travel range, 0…1 per axis):
   - Hand-aimed zoom (focus f, amount a): `v = 1/max(1, a)`, `span = 1 − v`; `travel = span ≤ 0.0001 ? 0.5 : clamp((f − v/2) / span, 0, 1)` — centres the viewport exactly on the chosen spot (pinned flush at edges).
   - Follow-pointer (and frames outside any zoom): `f = focus(s_i)` from the clusters below; `travel = clamp((f − 0.25) / 0.5, 0, 1)` — an **edge band** of 0.25: the view only moves while the pointer works in the middle half, and a pointer near an edge pins the view flush to it, so corners stay reachable and following stays calm.
3. **Focus clusters** (over the whole source timeline on the drawn pointer path, one point per source frame):

```
maxW = 0.5 / max(1, document.zoomAmount);   maxH = 0.7 / max(1, document.zoomAmount)
box = point[0]; started = 0; clusters = []
for i, p in points:
  g = box ∪ p
  if g.w ≤ maxW and g.h ≤ maxH: box = g
  else: clusters += (time: started, centre: box.centre); box = p; started = i / fps
clusters += (time: started, centre: box.centre)
focus(t) = centre of the last cluster with time ≤ t (the first cluster's centre before that; (0.5,0.5) if none)
```

   Box limits are a fraction of what is currently visible: 50% of the visible width and 70% of the visible height (normalized units), so the camera re-aims less readily on vertical movement than on horizontal movement — sideways travel reads as normal, vertical camera travel is what makes viewers seasick. Aiming at the centre of a group instead of the live pointer stops the picture drifting constantly. Because the whole path is known, the camera aims at the centre of a group from the moment the group begins. Note the clusters use the document's **global** zoom amount, not each segment's own amount.
4. **Spring** each of `A`, `travelX`, `travelY` independently over the output-frame sequence:

```
k = 200, m = 2.25, c = 40           // ζ = c / (2√(k·m)) ≈ 0.943, ω₀ = √(k/m) ≈ 9.43 rad/s → settles ≈ 0.45 s
value = targets[0], velocity = 0
for target in targets:
  elapsed = 0
  while elapsed < 1/fps:
    dt = min(1/240, 1/fps − elapsed)
    a = (k·(target − value) − c·velocity) / m
    velocity += a·dt;  value += velocity·dt            // semi-implicit Euler, fixed 240 Hz substeps
    elapsed += 1/240
  emit value
zoom_i = max(1, springA_i);  travel_i = (springX_i, springY_i)
```

   The spring carries velocity across re-aims and across cut seams, so a movement in progress never restarts or pops; fixed substeps make the result independent of the export frame rate. Vectors: 400 steps of 1/240 toward 1 → within 0.02; a 0→1 step at 60 fps settles > 0.9 with max ≤ 1.2.
5. **Viewport** (normalized, top-left): `origin = travel · (1 − 1/z)` per axis, visible size `1/z` → always inside the frame by construction (no clamping, no sticking at edges). Frames with `z ≤ 1.001` are not transformed. Rendering: scale the content by z about the top-left and translate by `−origin · sourceSize · z`, then crop to the source size.

### 6.8 Pointer path

1. **Resample** the 125 Hz samples onto the source frame grid (`n = max(1, round(D·fps))`, times `i/fps`): linear interpolation in time between neighbouring samples (before the first sample → first point; after the last → last point). No samples → all (0, 0); one sample → constant. Shape index and visibility use the **previous** sample (step function, never interpolated); no samples → shape 0, visible.
2. **Zero-phase smoothing** (skipped when "None"): cutoffs Light **4.0 Hz**, Smooth **2.0 Hz** (default), Cinematic **1.2 Hz**. Per axis:

```
α = 1 − exp(−2π · (cutoff · 1.553773974) · (1/fps))       // 1.553773974 = 1/√(√2 − 1) compensates the
                                                          // squared response of forward+backward passes
pad = min(n, max(1, round(3/α)))                          // repeat first/last value pad times
forward:  y = x0; for each x: y += α(x − y); x := y
backward: y = last; for each x (reversed): y += α(x − y); x := y
return the unpadded middle
```

   Vector: `onePoleAlpha(2 Hz, 1/60) = 0.18899` (unwidened). A step smoothed with α 0.28 crosses 0.5 **at** the step (no lag), never overshoots, ends untouched.
3. **Click anchoring** (a low-pass rounds corners, so the smoothed pointer would miss the button at the moment of the click): weight `w(t)` = 1 during any press→release interval (and from an unreleased last press to the end); otherwise `max over clicks within ±0.12 s of smoothstep(1 − |t − t_click| / 0.12)`; `p = smoothed + (raw − smoothed) · w`. Vectors (press 1.0, release 1.3): w(1.0) = 1, w(1.15) = 1, w(0.5) = 0, 0 < w(0.94) < 1.
4. **Shake passthrough** (shaking the mouse to find it is a gesture, not noise): for each frame, window `span = max(2, round(0.1·fps))` frames centred on it (needs ≥ 3 intervals); `travel = Σ|Δp|`, `reversals` = sign changes of Δx (zeros ignored); shaking if `travel > 0.06` (= 0.015 × 4) and `reversals ≥ 3` → that frame uses the **raw** position. Detection runs on the raw path.
5. Order: resample → smooth → anchor → shake override. Applied only when the pointer is drawn; the drawn path also feeds the focus clusters and the idle fade.

### 6.9 Pointer idle fade

Constants: idle after **2.5 s** still, fade-out **0.4 s**, fade-in **0.25 s** (completes **before** the movement starts), "still" = travel ≤ **0.0006** (normalized) per frame.

```
stillFor[0] = 0;  stillFor[i] = (|p_i − p_{i−1}| > 0.0006) ? 0 : stillFor[i−1] + 1/fps
untilMoves[n−1] = ∞;  untilMoves[i] = (stillFor[i+1] == 0) ? 0 : untilMoves[i+1] + 1/fps
waking   = untilMoves ≤ 0.25 ? 1 − untilMoves / 0.25 : 0
sleeping = stillFor ≤ 2.5 ? 0 : min(1, (stillFor − 2.5) / 0.4)
opacity  = 1 − smoothstep(max(0, sleeping − waking))
```

Vector (60 fps, parked 6 s then moving): opacity[10] > 0.95, [300] < 0.05, [360] > 0.95; always-moving → all > 0.95. Independently, frames where the system hid the pointer draw none.

### 6.10 Press punch and click ring

- **Punch**: window **0.13 s**, scale **0.8**. Weight per press at `d` released at `r`: `smoothstep((t − (d − 0.13)) / 0.13)` for `d − 0.13 ≤ t ≤ d` (the press starts before the button goes down); 1 for `d ≤ t ≤ r`; `smoothstep(1 − (t − r)/0.13)` for `r < t ≤ r + 0.13`; an unreleased last press weighs 1 to the end. `pressScale = 1 − 0.2 · min(1, max weight)`. Vectors: 0.8 at the press; 1 far away.
- **Ring**: duration **0.4 s**, radius **22** units. `progress = (t − d)/0.4` for presses with `0 ≤ t − d ≤ 0.4`, the **smallest** progress wins (a burst restarts one ring rather than stacking); none → no ring. Vectors: 0 at the click; none 1 s later.
- Ring drawing: `eased = 1 − (1 − p)³`; `unit = pointerPixelSize / 34`; `radius = 22 · unit · eased`; `lineWidth = max(0.6, 2.4 · (1 − p)) · unit`; `alpha = 0.35 · (1 − p²)`; white stroked circle; skipped when radius ≤ 1 or alpha ≤ 0.01; centred on the pointer's current drawn position; drawn under the pointer; only when "Mark where you click" and the pointer are on.

All three lookups walk the click list with a carried cursor across ascending frame times (same results as a full scan; the tests compare bit-for-bit over 5,000+ frames).

### 6.11 Pointer drawing and sizing

- Position in the source picture: `(u·W, v·H)` from the frame's source index `round(s_i·fps)` (clamped). Skip when the pointer is off, the take has no samples, the system hid it, opacity ≤ 0.01, or u/v is outside (−0.2, 1.2).
- `pointerScale = displayScale × max(1, systemScale) × pointerSize` (one bitmap pixel = one screen point on macOS). Windows with pixel-sized bitmaps: `displayScale = systemScale = 1`.
- Shape = `shapes[shapeIndex]` (fallback `shapes[0]`). Drawn size = `pointSize × pointerScale × press`; hot spot = `hotSpot × pointerScale × press`; the sprite's top-left = position − hot spot; uniform scale factor = drawn width / bitmap width (the sharper bitmap is downsampled); multiplied by the idle opacity.
- `pointerPixelSize` (ring scale) = `(shapes[0].pointSize.width or 28) × displayScale × max(1, systemScale) × pointerSize`.
- **Fallback arrow** (no shape captured): vector arrow in a 28×40 box, outline (x right, y down) (4.5,4.0) → (4.5,31.5) → (10.5,25.5) → (14.5,35.5) → (18.5,33.8) → (14.6,24.0) → (22.5,23.5) → close; stroked white 2.4 units with round joins and a shadow (0.8 units down, blur 2.2 units, black 35%), then filled black without shadow. Rasterized once at width `pointerPixelSize × 3` (cache ≤ 64 entries), drawn at width `28 × pointerScale × press` with hot spot (4.5, 4.0) scaled. (The fallback ignores the idle opacity.)

### 6.12 Canvas, card, corners, shadow, background plate

```
hasBackdrop = backdrop.kind ≠ none
margin = hasBackdrop ? clamp(backdrop.padding × 0.18, 0, 0.35) : 0        // slider 0…1 → 0…18% per side
inner  = 1 − 2·margin;  if inner ≤ 0.05 → canvas = even(source)
w, h   = srcW / inner, srcH / inner
if aspect ≠ original (r = 16/9 | 1 | 9/16):
  no backdrop (crop):  if w/h > r: w = h·r else h = w/r        // largest centred crop of the source
  backdrop (frame):    if w/h < r: w = h·r else h = w/r        // grow the short side, never crop
if max(w, h) > 3840: scale both by 3840 / max(w, h)
canvas = even(w, h)
canvasOut = even(canvas × outputScale)        // outputScale = quality scale (0.5 or 1), or the sharing plan's
card: avail = canvasOut × (1 − 2·margin)
      f = fill ? max(avail.w/srcW, avail.h/srcH) : min(…)       // fill = no backdrop and aspect ≠ original
      size = round(src × f);  origin = round((canvasOut − size) / 2)
corner = hasBackdrop ? round(clamp(cornerRadius, 0, 1) × min(card.w, card.h) × 0.09) : 0
```

Vectors: 960×640, no margin, original → 960×640; margin 0.1 → larger; 640×640 crop to wide → 16:9 within 640×640; framed wide → width > 640; 6000×4000 with margin 0.3 → long edge ≤ 3840; card in 1200×800 for 960×640 at 0.1 → centred, same aspect; square crop of 960×640 → card 960×640 at x = −160 (excess clipped equally).

**Plate** (rendered once per plan at `canvasOut`, sRGB 8-bit), only when the canvas differs from the source or there is a backdrop:
1. Fill black; then: preset → its 2-stop gradient; solid → its colour; gradient → its 2 colours; image → aspect-fill, centred (decoded ≤ 4096 px, orientation applied); none (crop case) → stays black. Gradients run top-left → bottom-right.
2. Background blur (when `blur > 0`): Gaussian, radius = `blur × min(W, H) × 0.035`, edges clamped.
3. If `corner > 0` or a backdrop exists: draw the card's rounded rect in black with a **drop shadow**: offset `min(H × 0.012, 26)` px downward, blur `min(H × 0.045, 90)` (CoreGraphics blur ≈ 2σ), black 42%.

**Mask**: white rounded rect (card, corner) on black. The framed content is blended over the plate through the mask (rounded corners cut the recording; the shadow shows around it).

Export size readout (`Quality` label) = `even(canvas × quality scale)`.

**Studio** look = graphite gradient, padding 0.45 (margin ≈ 8.1% per side, canvas ≈ 1.19× the source), corner 0.35 (≈ 3.2% of the card's short side), no background blur.

### 6.13 Frame composition (one function for preview and export)

The **plan** (built once per document change, per frame rate, per output scale) precomputes per output frame: source time, pointer position/shape/visibility/opacity, press scale, ring progress, zoom amount and travel, each text's and image's opacity, each blur's coverage; plus the plate, the mask, decoded cursor shapes and resized overlay images. A frame render is then index lookups and a few GPU composites. Frame index for a time = `clamp(round(t·fps), 0, n − 1)`.

A plan (and a compositor) exists only if at least one of: pointer drawn (and the take has samples), active zoom segments, canvas ≠ source (backdrop or shape), cuts, captions, images, blurs. Otherwise the preview plays the composition untouched and the export just orients/scales the video. **Invariant: if the document needs a compositor and it cannot be built, the export fails — it must never fall back to writing the bare recording** (that would leak blurred content).

Per frame, in this order (source pixels → canvas):
1. Take the master frame (cropped to the even source size).
2. **Blurs** (in start order) applied to the raw source picture (§6.14).
3. **Click ring**, then **pointer** sprite, drawn into the source picture (so the zoom magnifies them with the content).
4. **Zoom**: viewport transform and crop (§6.7).
5. **Fit** into the card rect (scale source size → card size, translate).
6. **Background**: blend with the plate through the rounded mask (or over the plate when there is no mask).
7. **Images** (start order) on the canvas at their anchors.
8. **Captions** (start order) on top of everything.
9. Crop to the canvas.

So: blur and pointer are zoomed; background, images and captions are not; captions are above images.

### 6.14 Privacy blur (mosaic + Gaussian)

```
rect = (x·W, y·H, w·W, h·H) in source pixels, rounded outward to whole pixels; skip if < 1 px either way
side  = min(rect.w, rect.h)
base  = side > 0 ? clamp(round(side / 3), 8, 48) : 8
block = max(2, round(base × factor(strength)))        // factor: 1 → 0.4, 2 → 0.65, 3 → 1, 4 → 1.5, 5 → 2.2
hidden = GaussianBlur( Pixellate(source with edges clamped, cell = block, grid anchored at the rect's corner),
                       radius = 0.6 × block ), cropped to rect
picture = hidden over picture
```

Blocks are sized from the **area**, not the frame: a strip around one line of text gets blocks taller than its letters; a large area is never reduced to four squares. Mosaic + blur: blocks destroy the letters, blur destroys the blocks. Edge clamping prevents transparent edges letting a sliver of the original show. Vectors: 300×24 → 8; 600×90 → 30; 900×900 → 48; 600×90 at strength 1 → 12, at 5 → 66; 300×24 at strength 1 → ≥ 2. (Core Image's pixellate takes each cell's colour from the cell; averaging the cell is an acceptable, stronger equivalent.)

**Temporal coverage**: hard `[start, end]` (inclusive) in source time. Because export frames may fall between plan samples (speed) or hold a frame across a cut seam, a blur is applied when it covers **either** `floor(t·fps)` or `ceil(t·fps)`. Required behaviour (export tests, checkerboard source): the last frame inside a blur is obscured (RMS contrast < 40) and the first frame after it is clear (> 100), at speeds 0.25, 0.5, 1, 1.37 and 4, with and without trim + cut — including a blur that ends inside a removed interval.

### 6.15 Captions and image overlays

- **Opacity**: `ramp = min(0.25, length/2)`; 0 outside `[start, end]`; `ramp ≤ 0 → 1`; `t < start + ramp → smoothstep((t − start)/ramp)`; `t > end − ramp → smoothstep((end − t)/ramp)`; else 1. Image opacity = its opacity × that. Drawn only when > 0.01. Vectors: caption 2–6 s is 0 at 1.9 and 6.1, 1 at 4, between 0 and 1 at 2.1 and 5.9; a 0.3 s caption is visible at its middle.
- **Anchor placement** (canvas W×H, content cw×ch, top-left space): `m = 0.05 × min(W, H)`; `x = m + (W − cw − 2m) × ux`, `y = m + (H − ch − 2m) × uy` with `(ux, uy)` ∈ {0, 0.5, 1}² per anchor. Vectors: 100×50 at topLeading on 1000×500 → (25, 25); bottomTrailing → (875, 425).
- **Caption raster**: system UI font, semibold, size `max(8, round(H × size))` px; palette colour; soft shadow black 55%, blur 0.18 × size, offset 0.04 × size downward; padding 0.5 × size around the measured text; multi-line kept; whitespace trimmed; rasterized once and cached by (text, size, colour), cache cleared past 48 entries. Windows: DirectWrite, "Segoe UI Variable" / "Segoe UI" Semibold.
- **Image raster**: drawn width = `W × size`, height from the image's aspect, uniformly reduced to fit `(W − 2m) × (H − 2m)`, rounded (≥ 1 px); decoded no larger than needed and redrawn at exactly that size with high-quality interpolation; own transparency kept; refused if > 20,000 px per side or > 8192² pixels; cache key = path + drawn size + file size + modification date (a replaced file is a new picture); cache budget 24 megapixels (cleared when exceeded). Unreadable → skipped. Vectors: 200×100 at 0.5 on 1000×500 → 500×250; 100×1000 at 0.6 → 45×450; every anchor keeps the whole picture inside the margins for portrait/landscape canvases and pictures.

### 6.16 Video export pipeline

1. Open the master; read the video track, duration, display size (orientation-normalized, even) and nominal frame rate → **export fps** = snapped to {30, 60} (else 60). Fail "no video" if any is missing or the trim is empty.
2. Decode the pointer track.
3. Build the **edited timeline**: for each kept range, insert that range of the video, and of each audio track where it overlaps the range at the same relative offset (a microphone that started late stays late). Audio is included only if `keepsSystemAudio || keepsMicrophone`. If speed ≠ 1, scale the whole edited timeline (video and audio together, including gaps and offsets) to `duration / speed`.
4. Output size: `even(canvas × quality scale)` (or the sharing plan size, §6.19). No compositor → `even(source × quality scale)`.
5. Build the plan at the export fps on the **unscaled** edited clock with the output scale (the background is drawn at the size it ships).
6. Produce **constant-frame-rate** output frames `k = 0, 1, …` at `T = k/fps` (export clock): edited time `t = T × speed`; source time via the kept ranges; source image = the master frame on screen at that source time (the latest frame with pts ≤ that time — VFR is resampled to CFR here, in the single decode pass the export needs anyway); compose at `t`; encode.
7. Audio: decode the timeline's audio tracks to 32-bit float PCM, apply per-track volume (`keep ? gain : 0`), mix to one stereo track; when speed ≠ 1 apply **pitch-preserving** time-stretch (macOS "spectral" algorithm); encode AAC-LC 48 kHz stereo 160 kb/s (sharing 128 kb/s). (The exported MP4 always has **one** mixed audio track; only a raw direct save keeps the separate tracks.)
8. Video encoder: HEVC if accepted, else H.264 High auto-level; bit rate per §6.3 at the **output** size and fps; key-frame interval per preset; no B-frames; MP4 container. Sharing: H.264 High only, plan bit rate, key frames every 4 s, B-frames allowed, fast-start.
9. Video and audio are fed concurrently, each as fast as its encoder accepts; cancellation is checked before every buffer.
10. Outcome: reader failure → "read failed"; writer failure → "write failed"; cancel → "cancelled"; partial output deleted; success → commit (§3.35).

Expected duration: `exportDuration` within one frame (tests use `1/60 + 0.001 s`). Audio tests at 0.25–4x: a muted system track and a microphone starting at +0.3 s with a silent gap stay aligned; 50% gain on a 0.4-amplitude sine gives RMS in [0.09, 0.18]; an 880 Hz tone stays 880 ± 70 Hz.

### 6.17 Preview pipeline

- Playback asset = the edited timeline at 1x (same construction as step 3 without scaling), always including all audio tracks; mute and gain are applied through the player's mix, not by rebuilding.
- Video goes through the same compositor at the **editor frame rate** (snapped source fps), CFR frame duration 1/fps, render size = the full (unscaled) canvas.
- Rebuilt (debounced 120 ms) on picture changes; the timeline asset is rebuilt on trim/cut changes; while aiming or drawing a blur the compositor is removed so the raw recording shows.

### 6.18 GIF export

```
fps = document gifFrameRate (8 | 12 | 15)
build the edited timeline WITHOUT audio, scaled by the export speed
D_out = its duration;  frames = max(1, floor(D_out × fps));  frames > 300 → "too long for GIF" (before decoding anything)
plan at the master's snapped fps on the unscaled edited clock (no output scale)
canvas = compositor canvas, or even(source) without a compositor
size = even(canvas × min(1, longEdge / max(canvas.w, canvas.h)))     // 420 | 600 | 800, never upscaled
for i in 0 ..< frames:
  T = min(D_out, i / fps);  image = composited frame at export time T (exact time, zero tolerance), fit into size
  append with delay = 1/fps (written as both the clamped and the unclamped delay; GIF stores centiseconds)
container: loop count 0 (forever); encoded in memory; finalized; cancel re-checked; written once
```

- Budget: max 300 frames → 37.5 s at 8 fps, 25 s at 12, 20 s at 15 (of export time). A frame that fails to decode is skipped.
- Palette/dither: the macOS build relies on the system GIF encoder (per-frame adaptive palette, encoder-default dithering) and sets nothing explicitly. Windows: per-frame 256-colour adaptive palette + error-diffusion dithering, transparency off, consistent disposal, and the NETSCAPE2.0 loop extension.
- Vectors: 2 s source (60 fps) with trim [0.5, 1.5] at 2x and 12 fps → exactly **6 frames**, all from the trimmed span; stored unclamped delay 0.08 s.

### 6.19 Share encoding plan and retries

```
plan(D_out, base = even(canvas × quality scale), sourceFps, hasAudio, s):
  fps      = min(30, max(1, sourceFps))
  bounded  = long edge limited to 1920 (even)
  audio    = hasAudio ? 128 000 : 0
  avail    = floor(90 000 000 × 8 × 0.94 / D_out) − audio            // 90 MB target, 6% container slack
  ceiling  = round(bounded.w × bounded.h × fps × 0.085)
  video    = floor(min(avail, max(800 000, ceiling)) × min(1, s))
  video < 350 000 → refuse ("too large")
  k        = min(1, sqrt((video / (fps × 0.055)) / (bounded.w × bounded.h)))  // ~0.055 bits/pixel/frame
  kMin     = min(1, 640 / max(bounded.w, bounded.h))                       // never below 640 px long edge
  size     = even(bounded × max(k, kMin))
loop up to 3 times (s starts at 1):
  encode; bytes ≤ 96 000 000 → validate → done
  s' = s × 90 000 000 / bytes × 0.94;  s = min(0.9·s, s');  (stop if not finite/positive)
fail "too large for sharing"
```

Practical limits: about **23.6 min** with audio (32 min without) before the bit rate floor refuses. Vectors: 30 s, 3840×2160 @ 60, audio → 1920×1080 @ 30, video 5.29 Mb/s, audio 128 kb/s, ≈ 20 MB; 1 h at 1080p30 with audio → refused; an oversized first pass retries below scale 1.

### 6.20 Waveform and thumbnails

- Waveform: decode the track (32-bit float interleaved) and for each frame at time `t = bufferStart + frame/sampleRate` update bucket `min(count−1, max(0, floor(t / D × count)))` with `max |sample|` over channels, clamped to 1; `count = 220`; cancellable; no decoded audio kept.
- Filmstrip: 14 thumbnails at `(i + 0.5) / 14 × D`, max 240×240, ±0.5 s tolerance.

### 6.21 Lane slotting, moving and snapping

```
slotForNewZoom(t, existing, D):
  if D ≤ 0.4 → none
  start = clamp(t, 0, D − 0.4); limit = D
  for seg in existing sorted by start:
    if seg.end ≤ start: continue
    if seg.start ≤ start: start = seg.end; continue       // standing inside a zoom → start after it
    limit = seg.start; break
  if limit − start < 0.4 → none
  return (start, min(limit, start + 2.0))

move(zoom, to s):  lower = max end of other zooms ending ≤ zoom.start (or 0)
                   upper = min start of other zooms starting ≥ zoom.end (or D)
                   start = max(lower, min(s, upper − length)); end = start + length
resize(zoom, edge, t): start = max(lower, min(t, end − 0.4))  |  end = min(upper, max(t, start + 0.4))
snap(t): candidates {0, D, playhead, every press time, other blocks' starts and ends (same lane)};
         tolerance = 8 px / laneWidth × D; nearest within tolerance wins
```

Neighbours are taken from the zoom's current position, so a drag stops at a neighbour instead of jumping over or pushing it.

### 6.22 Stage point → picture mapping (aim and blur)

```
fit = min(viewW / srcW, viewH / srcH); shown = src × fit; origin = (view − shown) / 2
u = (x − origin.x) / shown.w;  v = (y − origin.y) / shown.h       // not clamped
```

Aim requires `0 ≤ u, v ≤ 1`; blur corners are clamped by the rect normalization. Vectors: (300, 250) in 500×500 for a 1000×500 source → (0.6, 0.5); (10, 10) → v < 0 (in the letterbox); (150, 125) in 200×200 → (0.75, 0.75).

### 6.23 File naming

`"<prefix> yyyy-MM-dd 'at' HH.mm.ss.<ext>"` formatted with a fixed POSIX locale (24-hour clock), prefix = the localized "Recording" (e.g. "Gravação" in Portuguese). If the name exists in the folder: `"<base> 2.<ext>"`, `"<base> 3.<ext>"` … `"<base> 9999.<ext>"`. Valid on Windows as is (no colons).

### 6.24 Cursor shape identity

FNV-1a 64-bit: start `0xcbf29ce484222325`; for each byte `h = (h XOR byte) × 0x00000100000001B3` (wrapping); over every byte of the raw cursor buffer, then the 4 little-endian bytes of each of Float32(width), Float32(height), Float32(hotX), Float32(hotY).

### 6.25 Elapsed label

`total = max(0, s)`; `h = total / 3600`, `m = (total % 3600) / 60`, `s = total % 60`; `h > 0 ? "%d:%02d:%02d" : "%d:%02d"`. Vectors: 0 → "0:00", 7 → "0:07", 754 → "12:34", 3723 → "1:02:03", −5 → "0:00".


---

## 7. macOS dependencies → Windows mapping

| # | macOS API / mechanism | Used for | Closest Windows equivalent | Notes, gaps, flags |
|---|---|---|---|---|
| 1 | ScreenCaptureKit `SCStream` with `SCContentFilter(display:excludingApplications:exceptingWindows:)`, `sourceRect`, `showsCursor = false`, BGRA/sRGB, `minimumFrameInterval` | Area and display capture | **Windows.Graphics.Capture** (`IGraphicsCaptureItemInterop::CreateForMonitor`, `Direct3D11CaptureFramePool.CreateFreeThreaded`, B8G8R8A8) + GPU crop (`CopySubresourceRegion`) to the region; or **DXGI Desktop Duplication** (`IDXGIOutput1/5::DuplicateOutput[1]`) | WGC: cursor off via `IsCursorCaptureEnabled = false` (Win10 2004+); a yellow capture border is drawn unless `IsBorderRequired = false` (Windows 11; may need `GraphicsCaptureAccess.RequestAccessAsync(Borderless)`). DDA: no border, reports "no new image" and dirty rects, provides pointer shapes, but must run on the adapter that owns the output (hybrid-GPU laptops) and must recover from `DXGI_ERROR_ACCESS_LOST` (mode change, UAC secure desktop, exclusive full-screen). Both black out DRM content. **Flag**: pick a minimum Windows version early. |
| 2 | `SCContentFilter(desktopIndependentWindow:)` + `scalesToFit` | Window capture | WGC `CreateForWindow(HWND)` | Frames continue while occluded; none while minimized (hold the last frame); on resize `ContentSize` changes → `Recreate` the pool and scale-to-fit into the fixed output size (letterbox). `GraphicsCaptureItem.Closed` → treat as an unexpected stop (keep the file). |
| 3 | Window exclusion list fixed at stream start | Keep pill, guide, HUD, selection overlays, editors, pins out of the video | `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` (Win10 2004+), per window | Dynamic and honoured by WGC and DDA — strictly better than macOS (HUDs that appear mid-recording are excluded too). Apply to editors/pins only while recording if the screenshot tool must still see them. |
| 4 | `SCFrameStatus` complete/started vs idle | Variable-frame-rate master | WGC delivers frames only on change (`FrameArrived`); DDA `LastPresentTime == 0` = pointer-only update → skip | Timestamps: WGC `SystemRelativeTime`, DDA `LastPresentTime` are QPC-based. |
| 5 | SCStream audio (`capturesAudio`, `excludesCurrentProcessAudio`) and Core Audio process tap + private aggregate device (macOS 14.4+) | System audio without own sounds | **WASAPI process loopback** (`ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, …)` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE`, own PID); fallback endpoint loopback (`AUDCLNT_STREAMFLAGS_LOOPBACK`) | **Flag**: process loopback needs a recent build (verify; documented as 20348+). Loopback is silent-by-absence: pad silence. Re-open on default-device change (`IMMNotificationClient`). Downmix > 2 channels. The macOS tap trust/level-compensation machinery is unnecessary. |
| 6 | `AVCaptureSession` + `AVCaptureAudioDataOutput` (default input) | Microphone | WASAPI shared-mode capture on `GetDefaultAudioEndpoint(eCapture, eConsole)`, event-driven; or the Media Foundation audio capture source | Privacy switch → `E_ACCESSDENIED` → "Microphone unavailable". Use the capture client's QPC position for timestamps. |
| 7 | `CMClock` host clock, `CMSyncConvertTime`, `CACurrentMediaTime` | One timeline for video, audio, pointer, keys | `QueryPerformanceCounter` everywhere | Long recordings: watch audio-device clock drift vs QPC (resample or drift-correct to keep A/V sync). |
| 8 | `AVAssetWriter` (MOV, HEVC→H.264, AAC, fragments, real-time) | Master file | Media Foundation **`IMFSinkWriter`** with hardware MFTs (`MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`, DXGI device manager, BGRA→NV12 via the Video Processor MFT or D3D11 video processor), fragmented MP4 (`MFTranscodeContainerType_FMPEG4`); or **FFmpeg** (libavformat/libavcodec with `h264_mf`/`h264_nvenc`/`h264_qsv`/`h264_amf`) | MF has **no MOV sink and no per-track metadata** → side-car manifest (§8.4). Rate control through `ICodecAPI` (`CODECAPI_AVEncCommonRateControlMode`, `…MeanBitRate`, `CODECAPI_AVEncMPVGOPSize` in frames, `CODECAPI_AVEncMPVDefaultBPictureCount = 0`). Colour tags via `MF_MT_VIDEO_PRIMARIES`, `MF_MT_TRANSFER_FUNCTION`, `MF_MT_YUV_MATRIX` = BT.709, limited range. |
| 9 | `AVAudioConverter` (planar → interleaved) | Device-format tolerance | WASAPI shared mode gives interleaved float; MF Audio Resampler DSP for rate/channel conversion | Feed AAC with 48 kHz stereo. |
| 10 | `AVMutableComposition` (`insertTimeRange`, `scaleTimeRange`), `AVMutableAudioMix`, `AVMutableVideoComposition` with a per-frame handler, `AVPlayer` + `AVPlayerLayer` | Cut-aware preview with live compositing and per-track volume | **No direct equivalent.** (a) Custom timeline player: MF Source Reader (D3D11 hardware decode) + own clock + the shared compositor presenting into a swap chain (DirectComposition / WinUI `SwapChainPanel` / WPF `D3DImage`) + mixed audio via WASAPI/XAudio2 as master clock. (b) `Windows.Media.Editing.MediaComposition` (clips = kept ranges via `TrimTimeFromStart/End`, per-clip volume, custom `IBasicVideoEffect`, `GeneratePreviewMediaStreamSource`, `RenderToFileAsync`). (c) libmpv/FFmpeg with a render callback. | **Biggest effort.** Recommended: (a), sharing decode → compose with the export. (b) is conceptually closest but lacks speed control, gives limited source-time context to effects, and has performance/robustness issues. |
| 11 | `AVAssetReader` + `AVAssetReaderVideoCompositionOutput` / `AVAssetReaderAudioMixOutput` + `AVAssetWriter` | Export (decode → compose → encode, mixed audio) | MF Source Reader → compositor → Sink Writer; audio: Source Reader PCM → mixer → time-stretch → AAC encoder MFT; or FFmpeg | VFR → CFR resampling must be implemented explicitly (§6.16 step 6). |
| 12 | `AVAudioTimePitchAlgorithm.spectral` | Pitch-preserving export speed | Rubber Band (GPL, compatible with the app's GPL-3.0-or-later), SoundTouch (LGPL), FFmpeg `atempo` (chain below 0.5x) | No OS equivalent. Validate against the 880 Hz ± 70 test. |
| 13 | `AVAssetImageGenerator` | Filmstrip thumbnails, exact-time GIF frames | MF Source Reader seek (`SetCurrentPosition`) + decode forward to the exact frame | Thumbnails tolerate ±0.5 s; GIF frames need exact times. |
| 14 | Core Image (`CIPixellate`, `CIGaussianBlur`, `CIColorMatrix`, `CIBlendWithMask`, transforms, `clampedToExtent`) | Per-frame compositing | Direct2D effects on a D3D11 device context (Gaussian Blur with clamp border, Shadow, 2D Affine Transform/Scale, Alpha Mask/Composite, Opacity); pixellate = custom HLSL effect or point-sampled down/up-scale anchored at the rect | All in sRGB 8-bit premultiplied, like macOS. CoreImage blur "radius" ≈ σ; CoreGraphics shadow blur ≈ 2σ — match visually. |
| 15 | CoreGraphics bitmap contexts, CoreText / `NSAttributedString`, `NSShadow` | Background plate, rounded mask, cursor fallback arrow, ring sprite, captions | Direct2D geometries/brushes, DirectWrite text layouts (Segoe UI Variable / Segoe UI Semibold), WIC bitmaps | — |
| 16 | ImageIO (PNG encode/decode, thumbnail at max pixel size with orientation, GIF encode with loop count and delays) | Cursor PNGs, overlay/background decode, GIF export | WIC (PNG, scaler, metadata for EXIF orientation); GIF encoder needs 8-bpp indexed frames (`IWICPalette::InitializeFromBitmap` + `IWICFormatConverter` with error-diffusion dither) and metadata writes for `/grctlext/Delay` and the `/appext` NETSCAPE2.0 loop block; or gifski / FFmpeg palettegen+paletteuse | **Flag**: WIC's GIF path is low-level; quality depends on the palette step. |
| 17 | Private CGS calls (`CGSCurrentCursorSeed`, `CGSGetGlobalCursorData[Size]`, `CGSGetCursorScale`, `CGCursorIsVisible`) and `NSCursor.currentSystem` representations | Cursor shape catalog, visibility, scale | `GetCursorInfo` (hCursor, `CURSOR_SHOWING`, `CURSOR_SUPPRESSED`), `GetIconInfoEx` + `GetDIBits`/`DrawIconEx`, `CopyImage(LR_COPYFROMRESOURCE)` for larger renditions; or DDA `GetFramePointerShape` (MONOCHROME / COLOR / MASKED_COLOR) | Public APIs on Windows (an improvement). **Flag**: XOR/inverting cursors have no alpha representation. |
| 18 | `CGEvent(source:nil).location`, `NSEvent` global/local monitors (mouse down/up, key down, `isARepeat`) | Pointer sampling, clicks, typing times | `GetCursorPos` on a 125 Hz thread (high-resolution waitable timer); Raw Input (`RIDEV_INPUTSINK`) or `WH_MOUSE_LL` / `WH_KEYBOARD_LL` | LL hooks must return fast (`LowLevelHooksTimeout`) and have no repeat flag (track key state). |
| 19 | TCC permissions: Screen Recording, Accessibility, Microphone, Audio Capture | Gating | None for screen and input; microphone privacy switch only | Remove the permission UI and checks. |
| 20 | `NSPanel` (non-activating, `.statusBar` level, all Spaces, click-through), `NSAnimationContext`, `CABasicAnimation` | Pill, region guide, HUDs, countdown | Topmost layered tool windows (`WS_EX_TOPMOST \| WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE \| WS_EX_LAYERED`, plus `WS_EX_TRANSPARENT` for click-through), `MA_NOACTIVATE`, alpha fades via layered alpha or DirectComposition | No public API for "on all virtual desktops". |
| 21 | `NSWindow` + SwiftUI + custom AppKit lane view | Editor | Any Windows UI stack (WinUI 3, WPF, Qt, Avalonia…); `DWMWA_USE_IMMERSIVE_DARK_MODE`; custom-drawn lane control | One press decides create/select/move/resize (§3.21). |
| 22 | `NSOpenPanel` / `NSSavePanel` | Image pick, folder pick, Save as | `IFileOpenDialog` (filters; `FOS_PICKFOLDERS`), `IFileSaveDialog` (`.mp4`) | — |
| 23 | `NSPasteboard` (file URL, GIF data, plain text, source marker) | Copy, Copy as GIF, Copy link | OLE clipboard: `CF_HDROP` (+ `Preferred DropEffect`), registered `GIF` / `image/gif` with raw bytes, `CF_UNICODETEXT`; a registered `Vorssaint.Source` format as the marker | **Flag**: there is no universal animated-GIF clipboard format on Windows; most apps accept the file (`CF_HDROP`). |
| 24 | Drag the finished file chip; "Reveal in Finder" | File handoff | OLE `DoDragDrop` with `CF_HDROP`; `SHOpenFolderAndSelectItems` | — |
| 25 | `URLSession` (ephemeral, upload from file) | Temporary links | WinHTTP, `Windows.Web.Http.HttpClient`, .NET `HttpClient`, or libcurl | Stream the body from disk; no cookies/cache; 180 s / 300 s timeouts. |
| 26 | `NSWorkspace.didWakeNotification` | Re-check link expiry after sleep | `WM_POWERBROADCAST` (`PBT_APMRESUMEAUTOMATIC`) / `PowerRegisterSuspendResumeNotification` | — |
| 27 | `ProcessInfo.beginActivity(.idleSystemSleepDisabled)` | No idle sleep while recording | `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)` | Consider `ES_DISPLAY_REQUIRED` too (a sleeping display yields no frames). |
| 28 | `volumeAvailableCapacityForImportantUsage` | Disk guards | `GetDiskFreeSpaceExW` | No "purgeable" notion. |
| 29 | `FileManager.replaceItemAt`, POSIX 0700/0600 | Atomic export commit, private storage | `ReplaceFileW` / `MoveFileExW(MOVEFILE_REPLACE_EXISTING \| MOVEFILE_WRITE_THROUGH)`; per-user ACLs under `%LOCALAPPDATA%`; hidden attribute on staging files | — |
| 30 | `UserDefaults` | Settings | Registry or JSON settings (whatever the port uses) | — |
| 31 | Carbon global hot keys | Dedicated shortcut | `RegisterHotKey` | Choose a Windows default (e.g. Ctrl+Alt+Shift+5); ships disabled. |
| 32 | `NSScreen` / `CGDirectDisplayID`, points vs pixels | Regions, pill placement | `HMONITOR`, `MONITORINFOEX`, `GetDpiForMonitor`, Per-Monitor-V2 manifest | Store regions in physical pixels per monitor. |
| 33 | SF Symbols | Icons | Segoe Fluent Icons / Segoe MDL2 Assets | — |

---

## 8. Porting notes

### 8.1 macOS-specific parts to drop or replace

- Permission (TCC) flows for screen recording, accessibility and audio capture, and their Settings rows. Keep only a microphone availability check.
- The Core Audio process-tap pipeline: aggregate device, IO proc, the tap **trust** protocol (`recorderSystemAudioTapVerified`), multichannel level compensation + limiter, listener bookkeeping. Root cause (Vorssaint's own volume mixer re-rendering apps into the capture) is macOS-specific. Replace with WASAPI (process) loopback + simple stereo downmix.
- Private CGS cursor APIs and the `NSCursor` "sharper representation" trick → public Win32 cursor APIs.
- Notch capture controls, Spaces collection behaviours, Dock/Cmd-Tab activation policy, full-screen auxiliary flags.
- The static exclusion list built at capture start → per-window display affinity.
- AVFoundation's composition/player model → explicit decode → compose → present/encode.
- QuickTime track metadata tags → side-car manifest.
- The developer-bundle-id rule for the share endpoint override → a developer build flag.

### 8.2 Risks (ranked) and mitigations

1. **Preview player architecture.** Windows has nothing like `AVMutableComposition` + `AVVideoComposition` + `AVPlayer`. The editor needs frame-accurate, cut-aware scrubbing with per-frame GPU compositing and synced multi-track audio at 1x, while the export needs the same frames offline at another speed. Mitigation: build the deterministic offline renderer first (it is also the export), then wrap it in a player with a decoded-frame cache and the audio device as master clock; keep the master cheap to seek (H.264, no B-frames, 2 s GOP — already the macOS choice).
2. **Cursor fidelity.** The cursor is never in the video; the redrawn one must match the real one in shape, hot spot, size, visibility and position. Windows adds XOR/monochrome cursors (the classic I-beam), animated `.ani` cursors, per-monitor DPI cursor sizes, accessibility cursor sizes/colours, touch/pen suppression (`CURSOR_SUPPRESSED`), games that hide or clip the cursor, and windows that move during window recordings (§3.7). Mitigation: capture pixel-exact bitmaps at the recorded monitor's DPI, store per-sample window bounds, build a cursor test matrix (DPI 100–250%, mixed monitors, text fields, links, resize edges, busy cursor).
3. **Capture API constraints.** Version-dependent features (WGC cursor exclusion, border removal, display affinity exclusion, process loopback), the WGC yellow border, DDA adapter/ACCESS_LOST quirks, HDR desktops (FP16 + tone mapping), protected content, mixed-DPI regions. Mitigation: set the minimum OS (recommend Windows 10 2004+, best on Windows 11), implement one capture backend first (WGC is simplest and covers windows), keep DDA as an option for border-free display capture.
4. **Audio correctness.** Loopback delivers nothing during silence (pad it or the track shifts), device changes mid-recording, microphone format changes, audio-clock drift on long recordings, sink writers that dislike gaps, and a third-party time-stretch for export speed. Mitigation: write audio through a jitter buffer that emits continuous PCM against QPC, port the writer sync tests (§3.11) and the export audio tests (§6.16) verbatim.
5. **Encoding and containers.** No MOV sink or track tags in Media Foundation; HEVC decoding is not available on every PC without an extension (the editor must always decode its own master → H.264 master and default export, HEVC only when both encoder and decoder exist); hardware encoder limits (NV12 input, rate-control knobs, older H.264 encoders max out near 4096×2304 while 5K/6K displays exist → software fallback or capped capture size); GIF quality via WIC palettes; even dimensions everywhere. Mitigation: abstract the encoder, probe capabilities at startup, keep FFmpeg as a fallback path.

Also critical (not ranked because it is a correctness invariant): **privacy blurs must never leak** — a document that needs composition must fail rather than export bare video, and frames held across cuts or produced by retiming must stay covered (§6.13, §6.14). Port those tests first.

### 8.3 Known quirks of the macOS implementation (choose parity or fix)

1. **Export/editor frame rate** is the master's *nominal* frame rate snapped to {30, 60}, anything else → 60. Because the master is VFR, its nominal rate can depend on how much the screen changed and need not be exactly 30 or 60; whenever it does not round to exactly 30 the export runs at 60 fps, and 24/25 fps imports always export at 60 fps (frames duplicated). Fix: store the capture fps in the take manifest and use it (for imports, keep the source rate).
2. **Window recordings**: the pointer track is normalized to the window's rectangle at selection time; moving the window misaligns the pointer and zoom focus (§3.7).
3. **No pointer track** (imports): follow-pointer zooms aim at the top-left corner (§3.19).
4. The **global "How close"** slider does not change existing zooms, only new/regenerated ones and cluster sizes (§3.22).
5. Toggling **"Keep zoomed in while typing"** regenerates every zoom, discarding hand edits (undoable).
6. **Seeking past the trim end** jumps to the start of the last kept range (§3.20.6).
7. **Editor off** → the saved file is the raw master with **no cursor** (§3.17).
8. The **click ring** is centred on the pointer's current position, not where the click happened (identical unless the pointer moves during the 0.4 s ring).
9. **Clicks outside the recorded area** still create automatic zooms (aimed at the nearest edge).
10. Several sliders (pointer size, global zoom amount, background sliders, audio volume) create one undo entry per change event (§3.32).
11. **No crash recovery**: orphaned takes are swept, never offered back (§3.18); the pointer/typing tracks are lost on crash anyway (in memory until stop).
12. HUDs that appear **after** capture starts may be recorded on macOS (static exclusion); per-window affinity on Windows fixes this.
13. The selection badge shows the **unsnapped** size; the recorded size is the snapped even size.
14. The fallback arrow ignores the idle fade.
15. GIF size/smoothness can only be changed in Settings, and only affect takes opened afterwards.
16. Dead code/strings not to port as UI: `RecorderMotion.zoomProgress`, `RecorderSupport.pointerPixelSize(sourceSize:…)`, `estimatedBytesPerSecond`, `isEdited`, `toggleSound`; strings `cutHint`, `lookCaption`, `tourCaption`, `saveButton`.

### 8.4 Suggested take manifest (`take.json`, Windows)

Replaces in-container track tags and removes the frame-rate guess:

```json
{
  "version": 1,
  "appVersion": "x.y.z",
  "createdAt": "2026-10-09T10:15:00Z",
  "capture": { "kind": "area | window | display", "fps": 60,
               "monitor": { "device": "\\\\.\\DISPLAY1", "dpiScale": 1.5, "rectPx": [0, 0, 2880, 1800] },
               "regionPx": [120, 80, 1280, 720],
               "window": { "title": "…", "process": "…" } },
  "video": { "codec": "h264", "file": "take.mp4", "vfr": true },
  "audio": [ { "stream": 1, "source": "system" }, { "stream": 2, "source": "microphone" } ],
  "pointerTrack": { "file": "pointer.bin", "version": 4 },
  "typingTrack": "typing.json"
}
```

Keep `edit.json` byte-compatible with §5.6 (add `"version": 1` if desired; readers must keep tolerating missing keys), so edit presets and documents remain conceptually identical across platforms.

### 8.5 Suggested implementation order

1. **Pure model and math** (no UI, no media): edit document + sanitizers, timeline (trim/cuts/clocks/speed), zoom generation and state, camera (clusters, travel, spring), pointer path (resample, filtfilt, anchoring, shake, idle fade, punch, ring), canvas/card geometry, blur block size, overlay layout, GIF/sharing budgets, file naming, pointer-track codec. Port the unit-test vectors from §6 first (they are exact).
2. **Capture → master**: display/area capture with an encoder into fragmented MP4 (VFR, first frame at 0, last frame re-appended), pause clock, start/stop/cancel contract, disk guards, sleep prevention, take folder + sweep.
3. **Recording chrome**: chooser integration (Recording mode, audio toggles), region guide, countdown HUD, pill with pause/resume/stop, capture exclusion.
4. **Side tracks**: pointer sampler (positions, clicks, visibility, cursor shapes), typing sampler; then window capture (with per-sample bounds).
5. **Audio**: system loopback (+ silence padding, device changes), microphone, writer sync tests.
6. **Offline renderer/export**: decode → compose (plate, mask, zoom, pointer, ring, blur, overlays) → encode MP4; mixed audio with gains; export speed with time-stretch; staging + atomic commit; cancel; GIF.
7. **Editor shell**: window, top band, preview player reusing the renderer, transport, filmstrip (trim/scrub/cut), click ruler.
8. **Lanes and inspector**: zoom lane (generation, add/move/resize/snap, aim), text, image (import), blur (draw area), audio lanes; looks, presets, background picker; undo/redo with coalescing; shortcuts.
9. **Clipboard and file handoff**: copy, copy GIF, copy and delete, finished-file chip (reveal, drag), Recent captures hook.
10. **Sharing**: encoding plan + retries + validation, upload, link store, link sheet, Settings list, privacy sheet, wake/expiry refresh.
11. **Settings page** and media-tools import.

### 8.6 Test vectors worth porting verbatim

- Snapping, elapsed labels, pause-clock vectors (§6.1, §6.2, §6.25); trim/cuts/clock inversions and speed vectors (§6.4); zoom generation cases (§6.5); spring settling and overshoot bound (§6.7); filtfilt step (§6.8); idle fade (§6.9); punch/ring and the carried-cursor equivalence (§6.10); canvas/card/blur/overlay vectors (§6.12–6.15); GIF budget (300 frames, 25 s at 12 fps, 6 frames for a 1 s trim at 2x); sharing plan (30 s 4K → 1080p30 at ≈ 5.29 Mb/s; 1 h 1080p → refused); pointer-track codec round-trip and truncation tolerance (§5.4); edit-document legacy decoding (missing keys → defaults, legacy blur without strength → 3, legacy preset without images).
- Integration (synthetic media): the writer sync scenarios (§3.11); export duration within one frame at 0.25/0.5/1/1.37/4x; blur coverage just before the end and clear just after, with and without cuts; audio mute/offset/silence/gain/pitch at all speeds; cancelled GIF and cancelled retimed video leave an existing destination byte-identical with nothing beside it; GIF clipboard publication refuses non-GIF data and keeps the previous clipboard.
- Manual smoke (from `docs/recorder-export-speed.md`): record a visible timer with system audio and microphone cues; export at 1x, 1.25x, 0.5x, 1.37x and the 0.25x/4x ends; trim both ends and cut the middle with zooms, clicks, text, images and blurs on both sides; a 10 s GIF at 12 fps and 2x → 60 frames ≈ 5 s; check controls at the editor's minimum width.

---

## Appendix A — English UI strings

### A.1 Recorder strings (`RecorderFeatureStrings.enUS`)

| Key | English |
|---|---|
| pageTitle | Screen recording |
| hubDescription | Records an area, window or screen and edits it afterwards |
| panelCaption | Record an area, window or the whole screen |
| startButton | Record now |
| stopButton | Stop recording |
| fileNamePrefix | Recording |
| selectionPurpose | Choose what to record |
| indicatorTooltip | Recording controls |
| countdownLabel | Countdown |
| countdownOff | Off |
| countdownSecondsFormat | %d s |
| qualityLabel | Quality |
| qualitySmall | Small file |
| qualityBalanced | Balanced |
| qualityHigh | High |
| qualityCaption | Balanced fits most uses. High keeps every detail and makes bigger files. |
| frameRateLabel | Frames per second |
| frameRateFormat | %d fps |
| systemAudioToggle | Record the sound of the Mac |
| systemAudioCaption | Everything you hear goes into the recording, on its own track, so you can silence it later. |
| folderLabel | Save to |
| folderChoose | Choose… |
| moreOptions | More options |
| copyButton | Copy |
| copyGIFButton | Copy as GIF |
| saveButton | Save *(unused)* |
| discardButton | Delete |
| copiedHUD | Recording copied |
| savedHUDFormat | Saved to %@ |
| recordFailed | The screen could not be recorded |
| noSpaceTitle | Not enough space to record |
| noSpaceMessage | Free up some space on the disk and try again. |
| stoppedNoSpaceHUD | Recording stopped, the disk is almost full |
| shortcutLabel | Shortcut |
| editorTitle | Recording |
| saveVideoButton | Save |
| saveGIFButton | Save as GIF |
| exportingLabel | Saving… |
| cancelButton | Cancel |
| exportFailed | The recording could not be saved |
| gifTooLongFormat | A GIF can be up to %d seconds long |
| gifSizeLabel | GIF size |
| gifSizeSmall / Medium / Large | Small / Medium / Large |
| gifFrameRateLabel | GIF smoothness |
| discardTitle | Delete this recording? |
| discardMessage | It has not been saved anywhere yet. |
| discardSavedMessage | Saved and copied files will stay where they are. |
| openEditorToggle | Open the editor after recording |
| openEditorCaption | The recording opens ready to trim, mute and save. Turn this off to get the file straight away. |
| lookLabel | Look |
| lookRaw / lookClean / lookStudio | Original / Smooth / Studio |
| lookCaption | A starting point. Change anything below and it stays changed. *(unused)* |
| pointerSectionLabel | Pointer |
| pointerShowToggle | Draw the pointer |
| pointerSmoothingLabel | Smoothing |
| pointerSmoothingOff / Light / Smooth / Cinematic | None / Light / Smooth / Cinematic |
| pointerSizeLabel | Size |
| clickRingToggle | Mark where you click |
| zoomSectionLabel | Zoom |
| zoomToggle | Zoom in on every click |
| zoomAmountLabel | How close |
| backgroundSectionLabel | Background |
| shapeLabel | Shape |
| shapeOriginal / Wide / Square / Vertical | Original / Wide / Square / Tall |
| noPointerNote | This recording has no pointer track, so there is nothing to smooth. Zooms placed by hand still work. |
| zoomLaneEmptyHint | Click here to add a zoom |
| addZoomButton | Add a zoom |
| removeZoom | Remove |
| thisZoomLabel | This zoom |
| zoomWhereLabel | Where it looks |
| zoomFollowsPointer | Follows the pointer |
| zoomPickSpot | Pick a spot |
| zoomPickSpotHint | Click the picture to aim it |
| regenerateZooms | Back to one per click |
| backToOptions | All options |
| cutOutButton | Cut out |
| cutHint | Drag across the film to pick a part to remove *(unused)* |
| addTextButton | Add text |
| textLaneEmptyHint | Click here to add text |
| thisTextLabel | This text |
| textPlaceholder | Your text here |
| textContentLabel | Text |
| textSizeLabel | Size |
| textPositionLabel | Position |
| textColorLabel | Colour |
| removeText | Remove |
| copyAndDeleteButton | Copy and delete |
| saveAsButton | Save as… |
| presetsButton | Presets |
| savePreset | Save current preset… |
| presetNamePlaceholder | Preset name |
| removePreset | Remove preset |
| zoomEmptyTitle | No zooms yet |
| zoomEmptyCaption | Create them from your clicks or add one on the timeline. |
| createAutomaticZooms | Create automatic zooms |
| typingZoomToggle | Keep zoomed in while typing |
| typingZoomCaption | After a click, typing keeps the automatic zoom on that spot. |
| microphoneToggle | Record the microphone |
| microphoneCaption | Your voice goes into its own track and stays adjustable in the editor. |
| systemAudioTrackLabel | Mac sound *(Windows: e.g. "PC sound" / "System sound")* |
| microphoneTrackLabel | Microphone |
| audioVolumeLabel | Volume |
| removeAudio | Remove |
| restoreAudio | Restore |
| microphoneUnavailableHUD | Microphone unavailable |
| microphonePermissionName | Microphone |
| microphonePermissionExplain | Lets screen recordings include your voice when you turn it on. |
| automaticZoomToggle | Add zooms automatically |
| automaticZoomCaption | Turn this off to start new recordings without zooms. You can still add them in the editor. |
| pauseButton | Pause recording |
| resumeButton | Resume recording |
| blurLaneLabel | Blur |
| addBlurButton | Blur an area |
| blurLaneEmptyHint | Click here to add a blur |
| thisBlurLabel | This blur |
| blurPickArea | Choose the area |
| blurPickAreaHint | Drag over what should stay hidden |
| blurCaption | Hidden for as long as its block lasts on the timeline. |
| addImageButton | Add image |
| imageLaneLabel | Image |
| imageLaneEmptyHint | Click here to add an image |
| thisImageLabel | This image |
| imageSizeLabel | Size |
| imageOpacityLabel | Opacity |
| imagePositionLabel | Position |
| imageImportFailed | Couldn’t add this image. |

### A.2 Export speed strings (`RecorderExportStrings`, English)

| Key | English |
|---|---|
| speed | Export speed |
| custom | Custom speed |
| duration | Export duration |
| previewNote | Applies to video, GIF and shared links. The editing preview stays at 1×; the original recording is unchanged. |

### A.3 Share strings (`RecorderShareStrings.enUS`)

| Key | English |
|---|---|
| caption | Choose 1 or 6 hours. The final video is compressed on this Mac to fit under 100 MB and deleted automatically. |
| privacyData | Vorssaint sends only the final video created from this recording, including the audio you kept, and the expiration you choose. It does not send your name, account or device identifier. |
| privacyStorage | Network providers and the service temporarily process your public IP to prevent abuse. The video and link metadata are permanently deleted when you delete the link or its time ends. The service does not create backups. |
| privacyAccess | Anyone with the link can view, download, save or redistribute the video. Active links are available to the service operator for abuse moderation. Share only with people you trust. |
| compressing | Compressing for sharing… |
| uploading | Uploading securely… |
| tooLarge | This recording cannot fit under 100 MB without losing too much quality. |
| failed | The temporary link could not be created |
| tourCaption | Compress a finished recording on this Mac and share it for 1 or 6 hours. *(unused by the recorder UI)* |

### A.4 Shared strings the recorder borrows (screenshot / app strings, English)

| Key | English |
|---|---|
| screenCaptureTitle | Screen capture |
| hintDrag / hintClick | Drag to select an area / Click a window to capture it |
| showCaptureMenuOnShortcut | Show capture menu when using keyboard shortcut |
| shareSectionTitle | Temporary links |
| shareEnabledToggle | Allow temporary links |
| shareButton | Share |
| shareOneHour / shareSixHours | For 1 hour / For 6 hours |
| sharedLinksTitle | Shared links |
| sharedLinksEmpty | No active links |
| expiresLabel | Expires |
| copyLink | Copy link |
| openLink | Open |
| deleteLink | Delete now |
| sharedHUD | Link copied |
| linkDeletedHUD | Link deleted |
| deleteFailedHUD | The link could not be deleted |
| done | Done |
| sharePrivacyButton / sharePrivacyTitle | Privacy / Privacy for temporary links |
| backdropLabel / backdropNone | Background / None |
| backdropPaddingLabel / backdropCornersLabel / backdropBlurLabel | Margin / Corners / Blur |
| backdropWallpaperLabel / backdropImageButton | Wallpaper / Image… |
| backdropSolidLabel / backdropGradientLabel / backdropCustomLabel | Solid / Gradient / Custom |
| backdropSavePreset / backdropDeletePreset | Save background / Remove |
| blurStrengthLabel | Blur strength |
| recent captures title | Recent captures |
| cleanerRevealInFinder | Reveal in Finder |
| shortcutUnavailable | macOS rejected this shortcut. Choose another one. *(Windows: adapt wording)* |
| shortcut role title | Screen recording |
