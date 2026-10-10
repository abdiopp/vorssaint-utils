# Screenshot editor

Feature `screenshot` (editor part). Spec: `docs/specs/01-screenshot.md` §2.5,
§3.10, §3.13, §6.5–§6.15. Module id `screenshotEditor`.

The editor opens one dark window per capture. It has 13 tools, a contextual
style bar, backgrounds, watermarks, crop, undo, word selection on recognized
text, QR detection and the exports. Everything the canvas shows and everything
it exports goes through one Skia renderer (`Rivet.Imaging/ScreenshotEditor`).

## Where things live

| Folder | Contents |
|---|---|
| `src/Rivet.Core/ScreenshotEditor/` | Annotation model, `EditorSession` (gestures, selection, styles, crop, undo, inline-text rules, recognition state), arrow and pen geometry, hit testing, crop and zoom math, text bands/merge/runs/word selection, watermark style and geometry, tool order and tool keys, file names, QR payload rules, settings, platform interfaces (`ITextRecognizer`, `IDesktopWallpaperProvider`, `IKeyboardLayoutInfo`, `IShareSheet`) |
| `src/Rivet.Imaging/ScreenshotEditor/` | `EditorRenderer` (content, export pipeline, 1x), `AnnotationRenderer`, `BlurSampleCache` (pixelate, soft blur), `EraseFill` + `EraseCache`, `WatermarkRenderer`, `EditorCanvasPainter` (live canvas, chrome, crop loupe), `AnnotationFonts` (measure and draw with per-character fallback), `BackdropCodec`, `QrDetector` (ZXing), `PngWriter` (pHYs density), `EditorImageLoader`, `EditorSamples` |
| `src/Rivet.App/Features/ScreenshotEditor/` | `ScreenshotEditorModule`, `ScreenshotEditorService` (`IScreenshotEditor`), `EditorWindow`, `EditorCanvas`, `EditorController`, `ToolRail`, `StyleBar`, `BackdropPopover`, `WatermarkPopover`, `ToolOrderEditor` (+ Settings page), `EditorOutput`, `PinWindow`, `QrResultWindow` |
| `src/Rivet.Platform.Windows/ScreenshotEditor/` | `WindowsTextRecognizer` (Windows.Media.Ocr), `WindowsDesktopWallpapers` (IDesktopWallpaper, SPI fallback), `WindowsKeyboardLayout` (ToUnicodeEx), `WindowsShareSheet` (DataTransferManager interop), registrar |
| `src/Rivet.Platform.Fake/ScreenshotEditor/` | No OCR, no wallpapers, US layout, no share sheet |
| `src/Rivet.Core/Resources/i18n-win/screenshotEditor.en-US.json` | 38 new `win.screenshotEditor.*` strings (everything else reuses the macOS keys) |

Contributions: `IScreenshotEditor` (singleton `ScreenshotEditorService`), a
feature controller for `screenshot` that force-closes every editor and pin
when the feature is uninstalled, actions `screenshotEditor.editClipboardImage`
and `screenshotEditor.editLatest`, and the Settings page `screenshotEditor`
("Screenshot editor", category Capture). At launch, `ScreenshotDrag-<GUID>`
folders in `%TEMP%` left by an earlier run are deleted.

## Implemented

**Window and layout (§3.10.1–§3.10.3).** One window per capture with a taskbar
button. Title "Screenshot". Always dark. The client area extends into the
title bar, and only the top strip moves the window (double-click maximizes).
The initial size follows §6.5 for the display under the pointer, centred, with
the computed minimum enforced. OCR and the QR scan start after the window
opens. The layout has the stage fill with its top highlight, the brand mark,
the action cluster (Recent captures*, Discard, Undo, Redo, QR, Pin, Share,
Save split button with Save / Save As… / Add to Shelf, accent Copy), the tool
rail with shortcut badges, tooltips and a gear for "Editor tools", the info
chip (drag handle, `W × H px`, fractional `@1.5x`), the zoom chip (Fit, %,
1:1), and the bottom bar that stacks when narrow and never re-flows mid-drag.
Canvas: content including the backdrop margin, a 14-point margin, centred when
smaller, fit zoom capped at one DIP per pixel, range 0.05–3, Ctrl+wheel
(anchored at the pointer), Ctrl+=/− (×1.25 / ×0.8), Ctrl+0 fit, Ctrl+1 actual
size (one image pixel per device pixel). Card presentation: radius, hairline
border and two shadows. Live backdrop: scaled by 1 + 0.08·blur, blurred by
radius × zoom, with a card shadow outside the card only. Marks are clipped to
the image. Blur areas are drawn in an isolated layer. The base image is drawn
with nearest-neighbour sampling at 1:1 and when magnified, and mipmapped when
shrunk.

**Tools (§3.10.4).** All 13: Select, Arrow (Solid, Outline, Open,
Double-ended, Scribbly with the exact LCG and seeds), Blur (Pixelate, Blur,
Erase; strength 1–5, opening at ≥ 3; Text only), Crop, Text, Sticker (12),
Rectangle, Highlighter (42 % multiply), Pen (midpoint quadratic smoothing),
Line, Ellipse, Number (renumbered 1…n in drawing order) and Solid block.
Gesture rules: 7-point tap radius in view points (the pen counts as a tap only
if it never left the radius). A creation tool pressing on the selection edits
it. A creation-tool tap on a mark selects it and switches to Select, and only
the edge ring of area shapes counts. Drag shapes are selected when the drag
ends. A tap with a drag tool leaves nothing behind and drops its undo entry.
Taps outside the image are ignored for Text, Sticker and Number. Optional
shadows use the per-kind values from the spec; a stroked arrow casts one shadow.

**Blur rendering (§6.10, §6.11).** Samples come from the base image only.
Pixelate is an area-averaged small sample with ±9 noise, stretched
nearest-neighbour. Soft blur uses a 1/4-block sample with noise and a Gaussian
blur with σ = 0.8·block/step and clamped edges, stretched smoothly. Erase is
the content-aware fill with three passes, segment medians, inverse-distance
grid, half-cell growth and bilinear drawing; it reproduces flat backgrounds
and straight gradients. All three draw in replace mode. Text-only covers each
recognized run, or the whole area while runs are unknown or a band failed.
Samples are cached per style and level, unused ones are dropped each frame,
and the caches reset with the base image. Erase fills are cached per (integral
rect, skips text), and after 256 entries only the fills used in the last frame
are kept.

**Style bar (§3.10.5).** The groups follow the tool, or the selection while
Select is active: arrow style menu with rendered samples, sticker menu,
blur style / Text only / strength, 8 colour dots, thickness, text size (smaller,
"N pt" menu, larger), layer buttons, Shadows, Background, Watermark (accent
while drawn). The crop bar (Cancel, Crop) replaces it while cropping. Changing
a style applies to the selection as one undo step, only for attributes the
mark uses. Switching to Scribbly reseeds. A text size change re-measures. A
blur change that cannot get its sample snaps back. Selecting syncs the
controls. Last tool (Select and Crop fall back to Arrow), colour, thickness,
text size, blur style, level, text-only, arrow style, sticker and shadows are
remembered.

**Selection, layers, inline text, recognized text, QR (§3.10.6–§3.10.9).**
Eight handles with flipping, endpoint drags and moves, with one undo step on
the first real movement. Selection chrome: endpoint dots for segments, dashed
boxes, handles for resizable kinds, and the pen's bounding box (the macOS
origin-box quirk is fixed). Delete and Backspace delete, layer swaps
renumber counters, and Esc runs the chain. Inline text: a TextBox over the
mark (semibold, colour, 35 % black, accent border, placeholder "Text"). It
commits on Enter, Esc, a canvas press, any toolbar action and any tool change,
and follows the trim / new-and-empty / unchanged / one-step / emptied rules.
While it has focus, every key belongs to it. Recognized words: banded OCR
(§6.12) with seam merging, runs, an I-beam cursor, drag and tap selection,
highlights, and Ctrl+C copying words ("Text copied"; the editor stays open).
After a crop, words and runs are shifted, carried runs are merged with the
fresh recognition, and results are cached per base image for undo and redo.
QR: background ZXing scan (QR, Aztec, Data Matrix, PDF417) on open and after
every image change. The QR button opens the result panel (Copy, plus Open link
for a single http(s) URL).

**Background and watermark popovers (§3.10.10, §3.10.11).** Swatches: None, 5
presets, up to 12 saved looks (right-click Remove), the desktop wallpapers
(IDesktopWallpaper per monitor, deduplicated, existing files only, with a
badge), and Image…. Custom Solid / Gradient with wells, a 20-colour palette and
a "+" save button (normalized sliders, duplicates ignored, oldest dropped past
12). Margin / Corners / Blur sliders (Margin and Blur disabled without a
backdrop). A missing image file renders as no backdrop, and thumbnails are
≤ 220 px with a 24-entry cache. Watermark: None / Text / Image (Image with no
picture opens the file dialog), text ≤ 120 characters with 8 colours, a
picture with a middle-truncated name and Choose…, saved watermarks (click
applies with placement, right-click Remove, a dashed + tile disabled for none
or a duplicate, max 12), and placement (3 × 3 position, size, opacity 5–100 %,
rotation −90…90° shown as "N°"). Both apply live and are remembered.

**Crop, undo, closing, keys, tool shortcuts (§3.10.12–§3.10.15).** The crop
draft starts as the whole image, with grips (14·scale tolerance) snapping to
the nearest pixel, the crop loupe (72 points, 14 × 14 sample centred on the
edge, crosshair and ring, flipping inside the viewport), move clamped to the
image, new-rect drawing with tap restore, and the chrome. Apply needs ≥ 8 × 8;
it crops, shifts marks, words and runs, rebuilds samples, re-runs OCR and QR
and switches to Select. Undo and redo keep 60 snapshots of (image, marks);
backdrop, watermark and shadows are not undoable but count as dirty. Dirty
close asks "Discard this screenshot?" (Discard / Cancel). The trash button and
Ctrl+Delete close without asking. Copy, Save, Save As and Add to Shelf close
the editor; Pin, Share, drag-out and Copy text keep it open. Tool shortcuts:
the order CSV and bindings CSV are sanitized as specified. A digit (judged by
the typed character, so AZERTY's Shift+& is 1) moves the tool into that slot;
Delete clears the key and moves the tool below slot 9; other keys are checked
against the editor's reserved keys, other tools, global shortcut roles of
installed features and Windows shortcuts. Custom keys that the layout or
Caps Lock makes type a digit are suspended, not erased. Reset is available.
Tool shortcuts can be edited in the gear popover and on the Settings page.

**Exports (§3.8, §3.10.16, §6.13–§6.15).** Pipeline: base → marks → watermark
(R-aware margin) → rounded corners or backdrop (`BackdropRenderer`) → optional
1x (`screenshotDownscale`) → PNG with pHYs. Copy and Save use
`ICaptureOutput` when it is registered; otherwise Copy uses
`IClipboardService.SetImage` and Save writes `Pictures\Screenshots\Screenshot
yyyy-MM-dd at HH.mm.ss.png` (unique). Save As uses the native save dialog (PNG).
Add to Shelf uses `IShelfIntake` and is offered only while the shelf is
available. Pin exports without the backdrop but with rounded corners. Share
uses the Windows share sheet; a chosen target marks the edit exported.
Drag-out writes `%TEMP%\ScreenshotDrag-<GUID>\<name>.png` (deleted after an
hour, leftovers deleted at launch) and starts a file drag. Every export renders
off the UI thread.

**§3.13.** "Edit clipboard image": an image file on the clipboard wins over
image data, images over 60 MP are refused, and the toasts are "Copy an image
first" / "This image is too large to edit". "Edit latest screenshot": the newest
screenshot in `IRecentCaptures` whose file still exists, otherwise "Take a
screenshot first". The PNG density gives the scale.

**Pins (§3.11, local implementation).** Always-on-top borderless image with
7-point corners and a 1-point border, at natural size capped at 55 % of the
display, cascading. Drag to move, edge-drag to resize with the aspect locked,
double-click / Esc / Ctrl+W to close, Ctrl+C to copy, arrows to nudge (Shift ×12).
Menu: Copy, Save As…, Opacity 100/85/70/50 %, Ignore clicks (Alt+click recovers
through the shared mouse hook while needed), Close, Close all pins.

*The Recent captures button appears only when the capture module registers an
action for its palette (see Requests).

## Not implemented or partial

- **Temporary links** (Link menu, link sheet): out of scope (they use the macOS maintainer's server).
- **Recent captures button** uses the capture module's shared `RecentCaptureActions.ShowPalette` action and is hidden when it is not available.
- **"Edit latest screenshot" / "Edit clipboard image"** (actions and shortcuts) are the capture module's; at integration this module's duplicate actions were removed and the capture actions open this editor.
- **Touchscreen pinch** zoom. Precision-touchpad pinch works (Windows sends it as Ctrl+wheel); a one-finger touch draws.
- **Animations:** rail bounce and hover scale, card appear animation, spring transitions on the QR button and colour dots.
- **Micro QR:** ZXing.Net 0.16 has no Micro QR reader. QR, Aztec, Data Matrix and PDF417 are detected.
- **Beeps:** failures show an error toast instead (there is no shared beep service).

## Deviations from the macOS app (Windows decisions)

- **Keys:** Ctrl replaces ⌘. Redo is Ctrl+Shift+Z or Ctrl+Y. Discard is Ctrl+Delete or Ctrl+Backspace. Zoom uses Ctrl+= / Ctrl+− and numpad +/−. Reserved editor keys: Ctrl with C S Z Y P 0 1 = − numpad+ numpad− Backspace Delete W; Alt+F4; without Ctrl/Alt: Esc, Enter, Backspace, Delete. A Windows shortcut conflict shows "Windows uses this combination."
- **PNG density:** 96 × scale DPI, read back as dpi / 96 (spec §6.15 Windows option). The capture module should use the same convention (see Requests).
- **Wheel zoom:** Avalonia reports ±1 per notch, so a notch counts as 10 units (×1.14 per notch, still clamped to ±24 units as on macOS).
- **Move threshold:** a press must move 3 view points before it moves or resizes a mark (mouse-click jitter would otherwise nudge marks and add undo steps). The 7-point tap radius is unchanged.
- **Thickness glyphs** are hidden for Highlighter, Number and Solid block, where they have no effect (the spec allows this).
- **Scale label:** fractional (`@1.25x`, `@1.5x`).
- **Pen selection box:** the points' bounding box (fixes the macOS quirk).
- **Inline text field** takes focus immediately (the macOS 0.05 s delay is a workaround that is not needed here).
- **Cursors:** Windows resize cursors on handles and crop grips, the move cursor over the selection (with a creation tool) and inside the crop draft, I-beam over words, crosshair for creation tools.
- **Fonts:** Segoe UI Semibold (any other host: Helvetica Neue/Arial) with per-character fallback through the system font manager (CJK, symbols); stickers use Segoe UI Emoji colour glyphs. There is no complex-script shaping (HarfBuzz is not used), so Arabic and Indic text in text marks is drawn unshaped.
- **OCR:** Windows.Media.Ocr tries the app language, then the Windows display languages. There is no accurate/fast level, no language correction and no auto-detection. Band height is also capped by `OcrEngine.MaxImageDimension`, and wider bands are scaled down before recognition. Without an OCR language pack, word selection is unavailable and text-only blurs cover their whole area.
- **Shelf:** `IShelfIntake` takes paths and references them, so the PNG is kept in `%LOCALAPPDATA%\Rivet\ScreenshotEditor\Shelf\<GUID>\` and never deleted by this module.
- **Pins and QR panel:** implemented locally (no shared pin or QR contract exists). Pins are per virtual desktop and not persisted.
- **Window:** a taskbar button instead of the Dock icon. System caption buttons sit over the extended title strip; the action cluster keeps 140 DIPs clear of them.
- **Save fallback folder** (only without `ICaptureOutput`): `Pictures\Screenshots`, the Windows convention.

## Settings

Owned (keys as on macOS): `screenshotToolOrder`, `screenshotToolShortcuts`
(`tool=ctrl+shift:0x41,…`, Windows VK codes), `screenshotToolShortcutsEnabled`,
`screenshotLastTool`, `screenshotLastColor`, `screenshotLastStroke`,
`screenshotLastTextSize`, `screenshotLastBlurLevel`, `screenshotLastBlurStyle`,
`screenshotLastBlurTextOnly`, `screenshotLastArrowStyle`,
`screenshotLastSticker`, `screenshotAnnotationShadows`,
`screenshotBackdropStyle`, `screenshotBackdropPresets`,
`screenshotWatermarkStyle`, `screenshotWatermarkPresets`. Read only:
`screenshotDownscale` (capture page).

Backdrop and watermark values are `Setting<string>` holding JSON with the macOS
field names (`kind`, `presetID`, `colors`, `imagePath`, `padding`,
`cornerRadius`, `blur`; `kind`, `text`, `imagePath`, `color`, `anchor`, `size`,
`opacity`, `rotation`). Reading is tolerant of any key casing, numeric kinds
and double-encoded JSON. `screenshotBackdropPresets` is shared with the recorder.

## Tests

- `tests/Rivet.Core.Tests/ScreenshotEditor` (93 tests): arrow head formula and points, short arrows, solid contour and arc, stroked styles, scribbly LCG reference values and per-seed stability, pen smoothing, hit testing (segments, edge ring, counters, pens, topmost, handles, flips, endpoints), crop snapping, move and loupe, window size and fit zoom, sticker and counter metrics, text bounds, text sizes, opening blur level, OCR bands, seam merge, failed bands, runs, covering, crop shifts, word selection and copy, the recognition runner, watermark size, placement, rounded margin and rotation fit, sanitizing and JSON, presets, tool order, bindings, reserved keys, the recorder (digits, Delete, conflicts), key resolution and suspended keys, file names, QR joining and links, clipboard scale, and session behaviour (taps, closed pens, selection on drag end, select-and-switch, style sync, no thickness record on highlights, counters, text commit rules, move undo, resize flip, endpoints, scribbly reseed, crop apply and undo, 8 × 8 minimum, crop tap restore, Esc chain, carried runs, dirty state, refused samples, 60-step undo, layer buttons, serializable marks).
- `tests/Rivet.Imaging.Tests/ScreenshotEditor` (27 tests; six of them also write 8 snapshot sheets): blur levels, noise bounds, per-frame sample pruning, text-only before and after OCR, translucent replace mode, erase flat and gradient, erase cache, filled arrow silhouette, high-DPI export and 1x, backdrop compose and pin corners, PNG density, missing watermark picture, watermark corner, backdrop codec, QR detection. Snapshots: `editor-annotations*`, `editor-arrow-styles`, `editor-arrow-menu-samples`, `editor-blur-styles`, `editor-watermarks`, `editor-export-backdrops`, `editor-canvas-painter`.
- `tests/Rivet.App.Tests/ScreenshotEditor` (12 tests): editor window snapshots with the app in light and dark theme (`editor-window-light/dark`), crop mode with Enter (`editor-window-crop`), Text tool with inline typing at 150 % scale (`editor-window-text`), mouse drawing and undo/redo/digit/Esc keys, dirty close kept open, Ctrl+wheel / Ctrl+1 / Ctrl+0, service registration and uninstall, Edit clipboard image, tool keys from settings, popovers (`editor-popovers`), Settings page light and dark (`settings-screenshotEditor-*`).

## Risks

- **Rendering parity with macOS:** Segoe UI metrics and Skia text differ from Apple's. Text boxes are measured with the drawing engine, so they always fit, but sizes look slightly different.
- **OCR quality and availability** depend on installed Windows OCR language packs. Recognition is slower and weaker than Vision, and text-only blurs fall back to covering the whole area.
- **Built-in COM** (`IDesktopWallpaper` via `[ComImport]`) needs a non-trimmed, non-AOT build. Wallpapers then fall back to `SPI_GETDESKWALLPAPER`, then to none.
- **Avalonia 12.1.3:** bitmaps created with a DPI other than 96 render cropped, so the editor creates 96-DPI bitmaps and sizes images in DIPs. `ImageInterop.ToBitmap(PixelBuffer)` uses `96 × Scale` and is affected for others too.
- **Drag-out** starts the drag after the export finishes. If the button is released first, the drag ends immediately (the temp file is still cleaned after an hour).
- **Huge captures** (8K and up): the first use of a blur level builds its sample during the next frame on Avalonia's render thread (area average, tens of milliseconds), or on the UI thread when a style change on a selected blur area validates it. Exports, OCR, QR and file decoding run in the background. "Edit clipboard image" reads the clipboard on the UI thread (its owner window lives there), so a very large clipboard bitmap decodes there. The live backdrop plate is capped at 8192 px per side.
- **Threading:** the canvas snapshots an immutable frame on the UI thread and draws it on the render thread. Replaced sample caches are disposed at the start of the next frame, and the canvas caches are freed two seconds after the window closes.
- **Extended title bar:** the 140-DIP caption reservation assumes the standard three caption buttons at 100–200 %.
- **The `ForFeature` workaround** on the Settings page (see Requests) is a hack until the registry gets a primary-page flag.

## Manual test checklist (Windows PC)

1. Open an editor (capture with "Edit" as the default action, or run "Edit clipboard image" after copying an image in File Explorer and again after copying image data in Paint). Check: dark window centred on the display under the pointer, taskbar button, title-strip drag, double-click maximizes, caption buttons do not cover the action cluster.
2. At 100 %, 125 %, 150 % and 200 % scaling (and on a mixed-DPI pair of monitors), check the info chip (`@1.25x`), that Ctrl+1 shows 100 % with one image pixel per screen pixel, and that Fit never enlarges past 100 %.
3. Draw every tool. Arrows: switch styles from the menu (rendered samples); Scribbly differs per arrow. Pen: a closed loop survives, a tiny scribble near the start does not. Text: type, Enter; edit by tapping; empty it to delete. Sticker: pick from the menu; changing it with a sticker selected changes that sticker. Number: delete one and reorder (renumbers without gaps).
4. With a creation tool, press on the selected mark: it moves/resizes. Tap another mark: it is selected and the tool switches to Select. Tap inside a rectangle with the Text tool: a new text, not a selection.
5. Blur: Pixelate / Blur / Erase at levels 1, 3 and 5. Text only: before OCR finishes the whole area is covered; after OCR only the text lines. Erase on a flat UI background is seamless. A new editor never opens below level 3.
6. Install an OCR language (Settings › Time & language › Language & region › language options › Optical character recognition). With the Select tool, hover words (I-beam), drag across lines, press Ctrl+C, paste in Notepad: words with spaces, lines with newlines. Repeat without any OCR language: no word selection, and text-only covers whole areas.
7. Crop: drag grips (loupe follows), move the draft, draw a new rectangle, tap to restore, Enter applies, Esc cancels. Undo restores the image and marks; words re-appear.
8. Style bar: changing colour/thickness/size/arrow style on a selection is one undo step each; thickness on a highlight records nothing. Layer buttons swap and disable at the ends. Shadows toggles shadows on every mark and the watermark.
9. Background: each preset, a saved look (right-click Remove), each wallpaper tile (all monitors), Image…, custom solid and gradient with the palette, + to save (disabled for duplicates), Margin/Corners/Blur. Close and open another editor: the look is remembered.
10. Watermark: text with each colour, picture via Choose…, saved marks (+ tile, right-click Remove), all 9 positions, size, opacity, rotation (positive tilts up to the right). The mark never draws on the backdrop margin.
11. Exports: Copy (Enter / Ctrl+C) and paste into Word, Paint and Teams (transparent corners stay transparent where supported); Save (Ctrl+S) shows "Saved to …"; Save As (Ctrl+Shift+S); Add to Shelf (with the Shelf on); Pin (Ctrl+P, editor stays open); Share opens the Windows share sheet and picking an app marks the edit exported; drag the "Drag and drop" handle into File Explorer and into an email. With "Save at 1x size" on, a 200 % capture exports at half size.
12. Close a dirty editor with ×, Esc or Ctrl+W: the confirmation appears; Discard closes; the trash button and Ctrl+Delete close without asking. After any export, closing does not ask.
13. Tool shortcuts (gear and Settings › Screenshot editor): record a digit (moves the tool), Delete (moves below 9), a letter (badge shows it), Ctrl+Z (rejected: belongs to the editor), a key used by another tool (rejected with its name), Win+letter (rejected: Windows). On an AZERTY layout, Shift+& selects slot 1. Turn "Use tool shortcuts" off: digits stop working.
14. QR: capture a screen showing a QR code with a URL; the QR button appears; Open link opens the browser; a code with mailto: offers only Copy. Crop the code away: the button disappears.
15. Pins: move, resize from each edge (aspect locked), arrows/Shift+arrows, Opacity, Ignore clicks then Alt+click to recover, Close all pins.
16. Uninstall the Screenshot feature in Settings › Features with an editor and a pin open: both close without prompts.
17. Accessibility: Narrator reads the names of every icon button, colour dot and tool. Keyboard-only: Esc chain, Enter, digits.

## Requests for shared code

1. **Primary settings page for a feature.** `SettingsPageRegistry.ForFeature` picks the page with the fewest feature ids, so a one-feature "Screenshot editor" page would win the Features hub's Screenshot link over the capture page. As a workaround the page lists `FeatureIds.Screenshot` five times. Please add something like `SettingsPageDescriptor.IsPrimaryForFeature` (or an explicit `HubPageId` on `FeatureDescriptor`) and drop the repetition in `ScreenshotEditorModule`.
2. **Recent captures palette.** Expose a stable action id constant (e.g. `ActionIds.RecentCaptures = "screenshot.recentCaptures"`) or `IRecentCaptures.ShowPalette()`. The editor looks for `screenshot.recentCaptures` / `screenshot.showRecentCaptures` and hides the clock button otherwise.
3. **Shortcut roles for Edit latest / Edit clipboard image** (capture page settings `screenshotLastCaptureShortcut*`, `screenshotClipboardShortcut*`) should invoke `screenshotEditor.editLatest` / `screenshotEditor.editClipboardImage`, unless the capture module ships its own actions. In that case remove one pair so the Command Bar does not list duplicates.
4. **Shelf intake for generated files:** `IShelfIntake.AddImage(byte[] png, string fileName)` (or a "take ownership" flag) so the shelf owns its copy and the editor need not keep PNGs in `%LOCALAPPDATA%`.
5. **Settings backup hook** (e.g. `ISettingsBackupFilter` with export/import callbacks) so the watermark image path and image presets stay local and text presets travel (spec §3.10.11, §4.1).
6. **One backdrop codec:** move `Rivet.Imaging/ScreenshotEditor/BackdropCodec.cs` to `Rivet.Imaging/Backdrop/` and have the recorder use it for `screenshotBackdropPresets` (`Setting<string>`, macOS field names). The editor reads either representation, but writes the string form.
7. **PNG density convention:** the capture module should write and read 96 × scale DPI like `PngWriter`, so "Edit latest" and the editor agree on the scale.
8. **`ImageInterop.ToBitmap`**: create 96-DPI bitmaps (callers size the Image in DIPs). Bitmaps with other DPIs render cropped in Avalonia 12.1.3.
9. **Error feedback:** a shared beep (MessageBeep) or an `IHud` error convention. The spec uses beeps for failed exports; the editor shows error toasts.
10. **`ICaptureOutput.SaveAsync`:** document whether it shows the "Saved to …" toast. The editor shows it after a successful save.
11. **Shared pin and QR panel:** a `IPinService` / `IQrResultPanel` contract so the capture module's preview and the editor share one implementation (both exist locally here: `PinWindow`, `QrResultWindow`).

## Integration notes

- Merge the folders listed above plus `src/Rivet.Core/Resources/i18n-win/screenshotEditor.en-US.json` and this document. No shared file was changed.
- `ScreenshotEditorService` is registered as `IScreenshotEditor`. `OpenAsync` runs inline on the UI thread (it never deadlocks a UI-thread caller that blocks on it).
- The capture module owns `screenshotDownscale`; the editor declares the same key and type (`Setting<bool>`, default false).
- The fake recognizer reports "unavailable". Tests that need words inject their own `ITextRecognizer`.
