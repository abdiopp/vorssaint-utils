# Recording editor (`screenRecorder`, editor part)

The non-destructive editor that opens after a screen recording: preview,
trim and cuts, zooms, captions, image overlays, privacy blurs, background and
shape, the redrawn pointer, audio tracks, looks and presets, and the MP4 and
GIF exports. Spec: `docs/specs/02-recorder.md` §2 (editor, export), §3.20–§3.38
(without §3.37 temporary links), §4, §5.6–§5.10, §6, §7–§8.

Module id `recordingEditor`; it implements `Rivet.Core.Contracts.IRecordingEditor`.
The recording engine (another module) writes the take folder and calls
`IRecordingEditor.OpenAsync(takeFolder)`.

| Folder | Contents |
|---|---|
| `src/Rivet.Core/RecordingEditor/` | edit document (§5.6) and sanitizers, backdrop schema (§5.8), timeline (§6.4), automatic zooms, zoom ramps, camera spring (§6.5–6.7), pointer path, idle fade, punch and ring (§6.8–6.10), canvas/overlay/blur geometry (§6.12–6.15), export math (§6.3, §6.18), names (§6.23, §6.25), lane math (§6.21), stage mapping (§6.22), looks and presets (§3.28, §5.7), undo history (§3.32), the per-frame plan, media interfaces, take loading, staged atomic files; `Audio/`: WAV reader, edited-timeline mixer, WSOLA time stretch, waveform |
| `src/Rivet.Imaging/RecordingEditor/` | the frame compositor (preview and export), privacy blur, cursor/caption/overlay assets, the exporter (MP4 pipeline, GIF pipeline), GIF encoder (median-cut palette, Floyd–Steinberg, LZW) and decoder, synthetic video source, sample take writer |
| `src/Rivet.Platform.Windows/RecordingEditor/` | Media Foundation Source Reader decoder, Sink Writer MP4 encoder (H.264 + AAC), WASAPI playback (NAudio `WasapiPlayer`), wallpapers (`IDesktopWallpaper`) and the alert sound, registrar |
| `src/Rivet.Platform.Fake/RecordingEditor/` | synthetic decoder (sized from `take.json`), "MP4 unavailable" encoder, no audio device, registrar |
| `src/Rivet.App/Features/RecordingEditor/` | module + `RecordingEditorService`, `EditorSession` (state, editing, presets, exports, media previews), `PlaybackController`, the window and its parts (top band, stage, inspector, timeline, lanes, filmstrip, ruler, audio lanes, background picker, speed popover) and styles |

About 14,800 lines of code plus 2,500 lines of tests: 92 Core, 35 Imaging and
12 App (headless UI) tests of this module, all passing with the rest of the suite.
UI snapshots: `tests/artifacts/snapshots/recording-*.png` (compositor contact
sheet; editor window, panels and popovers in light and dark).

## Implemented

**Opening and loading (§3.20.1–2).** One window per take (re-opening activates
it), sized from the screen under the pointer (`min(max(1060, 0.70·W), W−60)` ×
`min(max(600, 0.68·H), H−80)`, minimum 940 × 560). The take is read off the UI
thread: `take.json`, `pointer.bin` (v3 or v4, the layout version is read from
the header), `typing.json`, `edit.json` or a new document seeded from Settings
(`recorderQuality`, `recorderSystemAudio`, `recorderGIFSize`,
`recorderGIFFrameRate`, `recorderAutomaticZoom`). The master is probed for
duration and size; the editor frame rate is the manifest's capture fps snapped
to 30/60. Automatic zooms are generated once (`zoomsGenerated`), not as an undo
step, and persisted immediately.

**Edit document (§5.6).** Byte-compatible keys and value spellings, every key
optional on read, unknown keys ignored, unreadable file → defaults. Sanitizers
exactly as specified (trim §6.4, cuts merged/≥ 0.1 s, zooms clamped/≥ 0.4 s with
overlaps resolved by moving the later start, captions ≤ 200 chars/≥ 0.2 s/size
0.03–0.16, images absolute path/size/opacity, blurs rect ≥ 0.01/strength 1–5,
speed 0.25–4, gains 0–1, backdrop "broken → none"). Atomic write (temp file,
then replace) on every committed change, never during a drag. Change
classification (`AffectsPicture/Timing/Audio`) drives rebuilds.

**Algorithms (§6).** All formulas and constants ported with the spec's
rounding (half away from zero): automatic zooms (lead-in, hold, merge, typing
extension, end margin), smootherstep ramps with owner hand-over, focus
clusters (50 % × 70 % of the visible area), aimed/follow travel with the 0.25
edge band, the 240 Hz semi-implicit spring (k 200, m 2.25, c 40), resampling,
zero-phase one-pole smoothing (light/smooth/cinematic), click anchoring, shake
passthrough, idle fade, press punch and click ring (carried-cursor lookups,
tested bit-for-bit against full scans over 5,400 frames), canvas/card/corner/
shadow geometry, blur block sizing and floor/ceil temporal coverage, overlay
fades/placement/sizing, bit rates, CFR frame counts, GIF budget and sizes.

**One compositor for preview and export (§6.13).** Plate (black → preset/solid/
gradient/image background via the shared `BackdropRenderer.DrawPlate`,
background blur, card drop shadow) → source frame with the zoom viewport and
card fit, clipped to the rounded card → privacy blurs (mosaic averaged per cell,
then Gaussian 0.6 × cell with clamped edges) → click ring → pointer sprite from
the recorded cursor bitmaps (fallback vector arrow) → image overlays → captions
(Segoe UI Variable/Segoe UI Semibold, soft shadow). Frames may be decoded
smaller than the recording; everything measured in source pixels scales with
them. Every exported frame goes through the compositor, so a blur can never be
skipped; frames held across a cut or retimed stay covered (export tests at
0.25/0.5/1/1.37/4×, with and without trim + cut, including a blur ending inside
a removed interval).

**Preview (§3.20.6, §6.17).** A background thread decodes and composes at the
editor frame rate on the edited 1× clock (cut stretches are absent). It renders
at the stage's device-pixel size and decodes just large enough for the plan's
largest zoom. The audio device is the master clock while playing; frames are
dropped instead of falling behind. 120 ms debounce for picture changes,
immediate rebuild for timing changes, live gains for audio. End of playback
pauses and seeks to 0; Play near the end restarts. Aiming and blur drawing show
the raw recording.

**Decoding.** Windows: Media Foundation Source Reader with the advanced video
processor (RGB32, optional downscale with a fallback to the coded size),
display-aperture crop (1088-line coded H.264), opaque alpha, seek to the
previous key frame then decode forward copying only the shown frame, LRU frame
cache, VFR semantics (latest frame at or before the time). Development:
synthetic frames sized from the manifest.

**Audio.** The take's WAVs are read in managed code (16/24/32-bit PCM or float,
any channel count, any rate with linear resampling), mixed over the kept ranges
with per-track keep/gain (live during preview). Export applies a managed WSOLA
time stretch for speeds ≠ 1 (pitch kept: 880 Hz stays within ±70 Hz at
0.25–4×; level kept), padded/trimmed to the video length. Windows playback:
NAudio `WasapiPlayer` (shared mode; the engine converts rate/channels).

**Export (§3.34–§3.35, §6.16, §6.18).** Save (MP4) to the save folder, Save as…
(dialog), Save as GIF, Save to… (folder picker → `recorderSaveFolder`), Copy
(MP4 into `cache/Copied Recordings`, purged after 24 h, file on the clipboard),
Copy as GIF (verified as a GIF with ≥ 1 frame before the clipboard is touched),
Copy and delete. MP4: Sink Writer H.264 High (Main fallback), bit rate and key
frame interval per quality preset, B-frames 0, one mixed AAC-LC 48 kHz stereo
160 kb/s track; RGB32 input with a managed BT.709 NV12 fallback. GIF: 8/12/15
fps, 420/600/800 px long edge (never upscaled), ≤ 300 frames (checked before
decoding), per-frame adaptive palette with error diffusion (lossless when a
frame has ≤ 256 colours), NETSCAPE2.0 loop forever, encoded in memory. Progress
`min(0.9, 0.9·k/n)` → 0.95 → 1; cancel from the chip, Esc or closing the window,
checked before every frame. Staging file `.rivet-partial-<UUID>.<ext>` beside the
destination, committed with a same-folder rename (replace), removed on
cancel/failure; the destination stays byte-identical. Successful exports set
"exported", show the finished-file chip (click = Show in folder, drag = the
file) and add to `IRecentCaptures` (kind `Recording`/`Gif`, 360 px thumbnail of
the first frame) when that service exists.

**Editor window (§3.20.3–5, §3.21–§3.31).** Top band (name, recent captures,
undo/redo, Presets menu with looks, user presets, Remove preset, Save current
preset…; finished-file chip / progress chip with Cancel; Delete, Copy and
delete, Copy + Copy as GIF, Save + Save/Save as…/Save as GIF/Save to…). Stage
(click toggles play or puts a selected zoom/blur down; aiming hint and focus by
click; blur drawing by drag with the teal rectangle). Timeline: transport (play,
time `m:ss / m:ss`, Add text, Add image, Blur an area, Cut out m:ss, export
speed, quality with the export size), click ruler with scrubbing, zoom/text/
image/blur lanes (select, move, resize from either edge, snapping to 0, end,
playhead, presses and neighbours, add on empty space after a first click puts
the selection down, right-click Remove, zoom neighbours never overlap), audio
lanes (220-bar waveform, volume 0–100 %, Remove/Restore), filmstrip (14
thumbnails, trim handles that give way at 0.2 s, scrub, Shift-drag cut
selection, seam click restores a cut). Inspector: Look (three look cards,
background popover, shape, margin/corners/blur sliders), Pointer (draw,
smoothing, size, click ring; note without a pointer track), Zoom (automatic
zooms, typing, empty state, How close, Back to one per click); This zoom/text/
image/blur panels with "All options". Background popover: None, 5 gradients,
saved customs (right-click Remove, shared `screenshotBackdropPresets`, max 12,
normalized, de-duplicated), desktop wallpapers, Image…, Solid/Gradient wells, 20
colour palette, "+" Save background. Export speed popover (presets, stepper,
slider, export duration, Done = one undo step).

**Undo/redo and persistence (§3.32).** Whole-document snapshots without a depth
limit; every drag, slider, caption typing session, anchor/colour click, cut,
regeneration and aim/draw is one undo step — including the sliders macOS does
not coalesce (pointer size, global zoom amount, background sliders, volumes).
Undo/redo seek to the trim start; a selection that no longer exists is cleared.

**Looks and presets (§3.28).** Original/Smooth/Studio set only their fields and
restore automatic zooms when the lane is empty. Presets (max 12, same name
case- and accent-insensitive replaces in place) store the look and copies of the
image overlays in `%LOCALAPPDATA%\Rivet\RecorderPresetImages\<UUID>\`; applying
copies them into the take (all or nothing, spanning the whole video), removing a
preset deletes unreferenced pictures.

**Keyboard (§3.33, Windows keys).** Ctrl+S, Ctrl+Shift+S (also while typing),
Ctrl+Z, Ctrl+Shift+Z / Ctrl+Y, Ctrl+C, Ctrl+Alt+C, Space, Delete/Backspace (cut
→ selected item → delete the recording with confirmation), Esc (cancel export →
end aiming/drawing → back to all options).

**Close and discard (§3.36).** Delete asks "Delete this recording?" (message
depends on whether anything was exported); the window's close button asks too
unless something was saved or copied; closing cancels a running export, waits
for it, then deletes the take folder. Feature removal closes every editor.

**HUD messages (§3.38).** Saved to <folder>, Recording copied, The recording
could not be saved (with the alert sound), A GIF can be up to %d seconds long,
Couldn't add this image., plus "MP4 unavailable" where Media Foundation is missing.

**Development build.** The Command Bar action "Open a sample recording in the
editor" (`recordingEditor.openSample`, registered only when the platform is not
Windows) writes a synthetic take (pointer with clicks and cursor bitmaps, typing,
two tones) and opens it; the synthetic decoder draws a fake desktop.

## Not implemented

- **Temporary links** (§3.37): out of scope (they use the macOS maintainer's server); the link menu is absent.
- **HEVC** export or decode: H.264 only (always decodable without the HEVC extension; spec §5.3 recommendation).
- **Hardware decode** (DXVA with a D3D11 device manager): the source reader decodes in software; the sink writer may still pick a hardware encoder (`MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`).
- **Raw GIF bytes on the clipboard**: Copy as GIF puts the file (CF_HDROP) only; `IClipboardService` has no API for extra formats (request below).
- **Animations**: the 0.16 s look-card and 0.3 s swatch spring animations.
- **Rotation metadata** of imported videos (the engine's own masters are never rotated).
- **The recorder's Settings page rows** (quality, GIF size, save folder, …): owned by the recording engine module; this module only defines and reads the keys.
- **"Recent captures" button**: shown only when the capture module's `RecentCaptureActions.ShowPalette` action is registered and enabled.
- **Media-tools ownership** of imported takes (closing those editors when Media tools is uninstalled): `IRecordingEditor.OpenAsync` carries no owner (request below).

## Deviations from the macOS app

| macOS | Windows | Why |
|---|---|---|
| Editor always dark; title bar merged into the top band | Follows the app theme (stage always dark); standard title bar (dark title bar in dark mode) | Windows apps follow the theme; avoids custom hit-testing of a client-drawn caption |
| Global "How close" only affects new zooms and clusters (§3.22, §8.3.4) | Also moves every follow-pointer zoom (aimed zooms keep their own) | The spec suggests it; the slider otherwise appears broken |
| Seeking into a cut or past the trim jumps to the nearest range's start (§8.3.6) | Clamps to the nearest kept moment | Spec suggestion |
| No pointer track: follow zooms aim at the top-left (§8.3.3) | Aim at the centre | Spec fix |
| Editor/export fps = nominal fps snapped (§8.3.1) | Manifest capture fps (snapped), nominal fps only without a manifest | Spec fix |
| Fallback arrow ignores the idle fade (§8.3.14) | Fades like the cursor | Fix |
| Preview rendered at full canvas size | Rendered at the stage's pixel size, decoded at the size the largest zoom needs | CPU compositing performance; exports always render at full size |
| Pixellate takes each cell's colour from the cell (Core Image) | Cells average only the pixels inside the area | Spec allows averaging; never mixes in outside pixels |
| Time stretch: AVFoundation "spectral" | Managed WSOLA | No OS equivalent; passes the spec's 880 ± 70 Hz test at 0.25–4× |
| Audio lane label column 64 pt | 76 DIP ("System sound" is longer than "Mac sound") | Readability |
| Multi-line captions | Lines aligned to the anchor's side (left/centre/right) | Looks right at every anchor |
| Undo of sliders: one step per change event (§8.3.10) | One step per drag | Spec suggestion |
| Edit presets store images inline | Images in the machine-local `recorderEditorPresetImages`; the portable `recorderEditorPresets` never contains paths | Settings backups carry presets without images (§3.28) using the existing machine-state mechanism; another PC treats them like legacy presets (images left alone) |
| Close never asks for an undecodable take | Asks; Save then copies the raw master | Never lose the only copy when this PC cannot decode it (e.g. Windows N) |
| Staging file hidden throughout | Hidden after the MP4 is finalized (the GIF is written in one go) | Media Foundation's file sink cannot reopen a hidden file with CREATE_ALWAYS |
| Default save folder: Desktop | Desktop (Videos was considered) | Parity, and the engine's direct save must use the same folder |
| Ctrl+Y | Also redoes | Windows convention |

## Risks

1. **Media Foundation code is compiled but has never run** (no Windows PC was
   available). Defensive fallbacks exist for the parts most likely to differ:
   RGB32 input refused by the sink writer → managed NV12; encoder parameters
   (CODECAPI rate control, GOP, B-frames) refused → retried without; H.264 High
   refused → Main; downscaled decode refused → coded size; display aperture
   read from `MF_MT_MINIMUM_DISPLAY_APERTURE`. Verify output orientation (the
   input type sets a positive default stride), colours (BT.709) and A/V sync.
2. **Preview performance** on large masters: decode is software and compositing
   runs on the CPU (Skia). 1080p at 60 fps should play; 4K masters may drop
   frames (the preview drops rather than lags). GPU compositing would be the next step.
3. **WASAPI clock**: the device position drives the picture; if a device reports
   no position the stopwatch takes over.
4. **Long recordings**: the frame plan (camera spring, pointer path) is rebuilt
   on each committed picture change and on every trim-drag step; fine for
   minutes, may stutter for very long takes.
5. **Shared setting format**: `screenshotBackdropPresets` is read tolerantly (a
   JSON string or array, any key case) and written in the macOS form
   (`presetID`, kind strings) as the kind already stored; the screenshot editor
   must read the same spelling (request below).
6. **GIF speed**: quantization and LZW are managed code; a 300-frame 800 px GIF
   takes a few seconds (runs in the background with progress and cancel).

## Manual test checklist (Windows PC)

1. Install the build; record an area with system sound and the microphone, click
   a few times and type. The editor opens; the zoom lane shows automatic zooms;
   ticks on the ruler match the clicks.
2. Press Space: picture and sound play in sync; the time label advances; it
   stops at the end and returns to 0:00.
3. Scrub on the ruler and on the filmstrip; drag the trim handles (playhead
   follows); Shift-drag across the filmstrip → "Cut out m:ss" → Delete; play
   across the cut (the stretch is absent); click the orange seam to restore it.
4. Zoom lane: drag a block, resize both edges (stops at neighbours, ≥ 0.4 s),
   click empty space (first deselects, second adds), right-click → Remove,
   select a zoom → "Pick a spot" → click the picture → the zoom aims there.
5. Add text, type two lines, change size/position/colour; add an image (PNG with
   transparency, a JPEG with EXIF rotation); add a blur, drag the area, change
   strength; check all four look right while playing.
6. Looks: Original / Smooth / Studio; Background popover: presets, a wallpaper,
   Image…, custom solid and gradient, "+" save, right-click a custom → Remove;
   open the screenshot editor and confirm the saved custom appears there too.
7. Shape: Wide/Square/Tall with and without a background; Margin/Corners/Blur.
8. Pointer tab: smoothing levels, size, click ring, draw off.
9. Audio lanes: lower a volume while playing (immediate), Remove/Restore.
10. Export speed 1.25× → Done; Quality Small/Balanced/High (readout changes).
11. Save (Ctrl+S): HUD "Saved to Desktop"; the chip appears; click it (Explorer
    selects the file); drag the chip into a chat/mail. Play the MP4 in the Films
    & TV / Media Player app: picture, cursor, zooms, blur, captions, one mixed
    audio track; duration = edited length / speed; sound pitch unchanged at 1.25×.
12. Save as… over an existing file; cancel one export halfway with Esc: the
    existing file is unchanged and nothing else is in the folder (show hidden files).
13. Save as GIF (≤ 25 s at 12 fps; a longer one shows "A GIF can be up to 25
    seconds long"); open it in a browser: it loops.
14. Copy (Ctrl+C) → paste into File Explorer and into a chat app; Copy as GIF;
    Copy and delete (Ctrl+Alt+C) closes the editor without asking.
15. Undo/redo (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z) through a drag, a slider drag and a
    caption edit: each is one step. Close and reopen is not possible (the take is
    deleted) — instead keep the editor open, check `edit.json` updates in the take folder.
16. Presets: Save current preset… (with an image overlay), apply it in another
    recording, Remove preset; export/import a settings backup: presets come back
    without image paths in the backup file.
17. Delete (trash) → confirmation; the close button without saving asks; after a
    save it closes directly; the take folder under `%LOCALAPPDATA%\Rivet\Recordings` disappears.
18. Uninstall "Screen recording" in Features with an editor open: it closes.
19. Light and dark app themes; 100/150/200 % DPI; a 4K recording (playback
    smoothness, export time); a 30 fps recording (export at 30 fps).
20. Windows N without the Media Feature Pack (if available): the editor says the
    recording can't be played, Save keeps the raw file, GIF/MP4 explain.

## Requests for shared code

1. **`IRecordingEditor`: open takes.** The engine's sweep must skip takes owned by
   open editors (§3.18). Please add `bool IsOpen(string takeFolder)` (or
   `IReadOnlyCollection<string> OpenTakeFolders`) to the contract;
   `RecordingEditorService` already implements both.
2. **`IRecordingEditor.OpenAsync(takeFolder, owner)`** with an owner feature id
   (`screenRecorder` or `mediaTools`), so editors opened by Media tools close when
   Media tools is uninstalled (§3.20.9).
3. ~~**Recent captures window**~~: done — the shared id
   `Rivet.Core.Contracts.RecentCaptureActions.ShowPalette` is used by the editor's clock button.
4. **`IClipboardService.SetFiles(paths, extraFormats)`** (or a `SetData` taking
   named formats) so Copy as GIF can also publish the raw GIF bytes (registered
   `GIF` format), `Preferred DropEffect` and the app's source marker (so the
   clipboard history can recognise its own copies), plus a success result so a
   failed clipboard write can show "The recording could not be saved".
5. **Backdrop JSON in `Rivet.Imaging.Backdrop`**: a shared serializer for
   `BackdropStyle` with the macOS key names (`kind`, `presetID`, `colors`,
   `imagePath`, `padding`, `cornerRadius`, `blur`) used by both editors for
   `screenshotBackdropPresets` and the screenshot editor's current style. This
   module has its own reader/writer (`RecorderBackdrop`) in that format.
6. **Recorder settings in one place**: `recorderQuality`, `recorderSystemAudio`,
   `recorderAutomaticZoom`, `recorderGIFSize`, `recorderGIFFrameRate`,
   `recorderSaveFolder` are defined in `RecordingEditorSettings` with the spec's
   keys, types and defaults; the engine module probably defines the same keys.
   Same key + same type is harmless, but one shared definition would be cleaner.
   New key added here: `recorderEditorPresetImages` (string, machine state).
7. **String override overlap**: this module overrides
   `recorder.systemAudioTrackLabel` ("System sound") and `recorderExport.previewNote`
   (no shared links on Windows) in `recordingEditor.en-US.json`; if the engine
   module also overrides `recorder.systemAudioTrackLabel`, keep one (duplicates
   only log a warning).
8. **Save folder resolution**: the editor and the engine's direct save must agree
   (`RecordingFolders.SaveFolder`: the setting when it exists, else Desktop, else
   home). Consider moving it to a shared recorder helper.
9. **Window icon**: editor windows have no `Icon`; a shared app icon for secondary
   windows would make the taskbar button match the app.
