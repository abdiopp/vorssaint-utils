# Media tools (`mediaTools`)

One local workspace with four tools: Video, GIF, Image and Text (spec 07 §3.6). "Local. No network."

## Where the code is

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/MediaTools/`: settings (raw macOS keys), compression levels, image options / presets / profiles (lenient decode), sizing (target size, safety limits, EXIF orientation, draw rects), the bitrate and GIF planners with retry rules, output naming (sanitising, Windows reserved names, collision numbering, rename tokens), watermark layout, size texts and OCR language choice, the job state machine and the atomic output commit, platform interfaces |
| Imaging | `src/Rivet.Imaging/MediaTools/`: `MediaImageProcessor` (per-file pipeline on SkiaSharp), `GifEncoder` (own GIF89a encoder: global palette by median cut, Floyd–Steinberg dithering, frame differencing with a transparent index, LZW), `JpegPdfWriter` (one-page PDF with a DCTDecode image) |
| App | `src/Rivet.App/Features/MediaTools/`: `MediaToolsModule` (action, panel tile, settings page, the Media window), `MediaToolsService` (shared state and job runner), `MediaWorkspaceView` + `MediaWorkspaceView.Options.cs` |
| Windows | `src/Rivet.Platform.Windows/MediaTools/`: `WindowsVideoProbe` (Windows.Storage video properties + `MediaEncodingProfile`), `WindowsVideoTranscoder` (`Windows.Media.Transcoding.MediaTranscoder`, H.264 High + AAC MP4, hardware encoders when present), `WindowsVideoFrameReader` (Media Foundation source reader, RGB32, rotation), `WindowsOcrEngine` (`Windows.Media.Ocr`), `WindowsImageCodecs` (WIC: HEIC/HEIF and TIFF decode, HEIC encode only when the HEIF + HEVC extensions are installed, detected from the codec lists), `WindowsFileIdentity` (volume serial + 128-bit file id, hidden flag, retrying moves) |
| Fake | `src/Rivet.Platform.Fake/MediaTools/FakeMediaPlatform.cs`: video, GIF frames and OCR report "not available" (the dev build shows that state); the image tool runs everywhere |
| Tests | `tests/Rivet.Core.Tests/MediaTools/` (naming, tokens, sizing, planner, profiles vectors), `tests/Rivet.Imaging.Tests/MediaTools/` (GIF encoder round trip, image pipeline, watermark snapshot, PDF), `tests/Rivet.App.Tests/MediaTools/` (panel workspace per tool light/dark, image tool with preview and watermark light/dark, single and batch runs, settings light/dark) |

## Implemented

- Hosts: the tray panel's Utilities tile "Media" (compact, scrolls inside a 430 DIP cap, keeps the panel open so files can be dragged in), Settings › Capture › Media (full size), and a stand-alone Media window (action `mediaTools.open`, Command Bar / radial menu). All share one service, so a job keeps running when a host closes and reopening shows its progress or result.
- File card: input button and drop target (drop order kept, accent highlight), title "Choose file" / name / "%d files selected", clear ✕, output row ("Automatic" or the chosen name) with Destination (save dialog per output type, folder picker for image batches), "Save in “Converted” subfolder" for batches. A new input, clearing it or switching tools cancels a running job; switching tools keeps the inputs.
- Video: trim (Start/End seconds, one decimal), "Resolution" mode with Low/Medium/High (0.88/0.68/0.28) and Size (640–3840 step 320), "File size" mode (1–512 MB) with the bitrate planner and up to 3 passes; H.264 High + AAC MP4; "Target size too small…" when no plan fits; "Edit" hands one clip to the recording editor (private take folder, ≥ 500 MB must stay free) when that module is present.
- GIF: trim, FPS 1–30, longest edge 160–1600 step 80, loop toggle, ≤ 300 frames ("A GIF can be up to %d seconds long"), "File size" mode with up to 4 encodes (frames first, then pixels). Frames are read sequentially once for a 16-frame palette sample and once for encoding; our own encoder writes the file.
- Image: one image or a batch; JPEG/PNG/WebP/HEIC(when available)/PDF; Low/Medium/High compression (hidden for PNG); resize none / max side / width / height / custom stretch, fit, fill; background transparent/white/black (JPEG and PDF force white); "Remove metadata" (keeping it copies the EXIF block JPEG → JPEG, orientation reset); "Keep original modified date"; watermark text and/or logo with 5 positions, opacity, margin, scale; rename pattern with the token menu ({name}, {index}, {index:03}, {counter}, {date}, {time}, {datetime}, {width}, {height}, {format}, {ext}, {index:0N}); Web/Social/Docs presets; profiles under "More options" (no profile, select, delete, "Modified", name, Update, Save new); live preview (108×74 / 136×92 frame shaped to the output, background per format, watermark), re-rendered off the UI thread after 180 ms. Batches continue past failing items and report them.
- Text: OCR with `Windows.Media.Ocr` in the app language plus English (first installed recognizer), image read upright (EXIF orientation applied), UTF-8 TXT without BOM, copyable text box ("No text found.").
- Status card: progress with percent, result ("Saved as X" or the batch summary), the 1000-based size line with saved/larger and the "came out larger" caption, up to 3 per-item failures, Show (reveal), Copy text, Copy summary, Run again; failed and cancelled states.
- Output safety: automatic collision-free names next to the source (`<base>-compressed.mp4`, `<base>.gif`, `<pattern>.<ext>`, `<base>-text.txt`; `photo.jpg` → `photo 2.jpg`), Windows name rules (`< > : " | ? *`, reserved device names, trailing dots/spaces, byte-length truncation, long paths), every output written to a temp file on the destination volume and moved into place, the same-file guard by volume + file id (hard links count), hidden flag cleared.

## Not implemented

- The island extras ("Optimize media" drop chooser, "Create ZIP", automatic shelf adds) — the Dynamic Island is not ported.
- Quick Launcher host: the launcher belongs to another module; it can host `new MediaWorkspaceView(services, MediaHost.Panel)`.
- OCR "Accurate/Fast": Windows has one recognizer; the setting is kept and shown but has no effect.
- MP4 fast start (moov before mdat): `MediaTranscoder` does not expose it.

## Deviations from macOS

- Video "Resolution" mode replaces Apple's avconvert presets with an H.264 ladder (Low keeps the source size at ~0.16 bits/pixel, Medium picks 640×480 / 960×540 / 1280×720 / 1920×1080 boxes from "Size", High is a 480p-class output). The bitrates are a first calibration, not measured against macOS output.
- AAC is 96 or 128 kbps (the Microsoft encoder rejects 64 kbps), so the planner's low audio tier is 96 kbps.
- The video progress is real (transcoder progress) instead of the estimated curve.
- WebP output added (SkiaSharp encodes it); HEIC output appears only with the HEIF Image Extensions installed (a caption explains it).
- OCR applies EXIF orientation (the spec's suspected macOS bug is not copied).
- "Format not supported by macOS." → "This format isn't supported on this PC."
- The GIF encoder is our own (one global palette per file with dithering); the macOS ImageIO encoder quantises per frame.

## Risks

- HEVC iPhone clips need the "HEVC Video Extensions" (paid) to decode; without them the probe or the transcoder fails with "Video could not be read".
- Windows N editions lack Media Foundation: video, GIF and OCR show "not available" with the Media Feature Pack hint.
- `MediaTranscoder` ignores some size/bitrate requests on certain hardware encoders; the "File size" retries cover overshoots but may end with "too small".
- Large GIFs are memory-light (streaming encoder) but CPU-heavy; cancel works between frames.

## Windows manual test checklist

1. Tray panel › Utilities › Media: the compact workspace opens; drag an MP4 from Explorer onto the file card: its name shows, Start 0.0 / End = duration.
2. Video, Resolution, Medium, Size 1280: Compress video. Progress runs; "Done", "Saved as <name>-compressed.mp4", the size line; Show selects it in Explorer.
3. File size 5 MB on a 1-minute 1080p clip: the result is ≤ 5,000,000 bytes, or "Target size too small…".
4. GIF: 5 s trim at 12 fps, width 480: a looping GIF plays in a browser; turn Loop off: it plays once. A 40 s trim at 12 fps fails with "A GIF can be up to 25 seconds long".
5. Image: drop 10 photos, Web preset: 10 JPEGs named `<name>-web.jpg` next to the first photo; with the "Converted" checkbox: in a Converted subfolder. Copy summary lists them.
6. Image: watermark "Text + logo", bottom right, 45 %: the preview and the output match. Choose PDF: "Convert to PDF"; the PDF page size equals the pixel size.
7. Without the HEIF extension: HEIC is not offered and the caption says why. Install "HEIF Image Extensions" from the Store, restart: HEIC appears and works.
8. Rename pattern `{name}-{index:03}` on a batch: `a-001`, `b-002`…; `CON` as a pattern becomes a safe name.
9. Choose the input itself as the destination: "Choose a destination different from the original file."
10. Text: a screenshot with text: the TXT appears and the text box shows the lines; Copy text works. A rotated phone photo is read upright.
11. Start a long export, close the panel, open Settings › Media: the same progress continues; Cancel shows "Cancelled." and no partial file remains.
12. Video "Edit" (with the screen recorder installed): the clip opens in the recording editor.

## Requests for shared code

1. **Quick Launcher**: host the compact workspace (`new MediaWorkspaceView(services, MediaHost.Panel)`) as the launcher's "Media" tile, Esc returning to the grid.
2. Optional: a bundled FFmpeg (spec §7.6) would give fast start, exact avconvert-like presets and HEVC decoding without Store extensions; that is a packaging/licensing decision for the integrator.
