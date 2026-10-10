# Capture tools (`capture` module)

Screenshots (selector, outputs, quick preview, pins, recent captures, scrolling
capture), **Copy text from screen** (OCR and QR) and the **colour picker**.
Feature ids: `screenshot`, `screenOCR`, `colorPicker`. Spec: `docs/specs/01-screenshot.md`
(everything except the editor internals of §3.10), spec 05 §6 (shortcuts),
spec 02 §3.3 (how recording uses the selector).

| Area | Code |
|---|---|
| Module, UI, flows | `src/Rivet.App/Features/Capture/` |
| Platform-neutral logic, settings, platform interfaces | `src/Rivet.Core/Capture/` |
| Overlay renderer, scrolling matcher/stitcher, QR, PNG density | `src/Rivet.Imaging/Capture/` |
| Windows implementations | `src/Rivet.Platform.Windows/Capture/` (own `NativeMethods.txt`, `CaptureRegistrar`) |
| Fakes (synthetic desktop with windows, text and a QR code) | `src/Rivet.Platform.Fake/Capture/` |
| Strings | `src/Rivet.Core/Resources/i18n-win/capture.en-US.json` |
| Tests | `tests/Rivet.Core.Tests/Capture`, `tests/Rivet.Imaging.Tests/Capture`, `tests/Rivet.App.Tests/Capture` |

## Contracts this module implements

* **`ICaptureSelector`** → `CaptureCoordinator`. `SelectAsync(request)` shows the
  unified selector on `request.Tool`. `AllowToolSwitching` shows the 1–4 mode
  palette; the digit keys switch between the installed tools either way, as in
  every chooser (§1: "digit keys switch modes even when the palette is hidden").
  The caller applies its own "show capture menu" preference (the recorder passes
  `AllowToolSwitching = !fromShortcut || recorderShowCaptureMenuOnShortcut`).
  While a recording runs, a request for another tool opens only without the
  palette and offers only that tool (§3.3 step 2); the recorder's own `Recording`
  request is never held back. The result
  is returned only when the confirmed tool is the requested one; if the user
  switched tools, this module handles the result itself (screenshot pipeline,
  OCR, colour copy) and `null` is returned. For `Recording` the bounds are a
  *region* (§3.1): whole pixels, even width and height, at least 32 px, inside
  the display, global physical pixels; `WindowHandle` is set for a clicked window;
  `FrozenImage` is null. For `Screenshot`/`Text`, `FrozenImage` holds the pixels
  (with `Scale` = display DPI scale). For `Color`, `PickedColor` is 0xAARRGGBB.
* **`ICaptureOutput`** → `CaptureOutputService`. `SaveAsync` saves a PNG with the
  folder, dated-subfolder and file-name settings (§3.8.1, §6.20), honours
  "Save at 1x size", consumes the `%#` number and gives it back on failure; it
  returns a record describing the saved file (`Id` = `existingRecordId` when
  given). It does **not** add a history entry: screenshots are recorded in
  recent captures when they are taken (§3.7 step 4). `Copy` writes PNG + DIBV5
  + the file (CF_HDROP) + a source marker in one clipboard session and returns
  immediately (the work runs in the background; failures beep). `windowTitle`
  is accepted but not used in names (the macOS naming has no window token).
* **`IRecentCaptures`** → `RecentCapturesStore` (§3.12). `Add` accepts
  recordings/GIFs (path kept, never copied; a re-save at the same path replaces
  the older entry; `ThumbnailPath`, when given, is copied into the cache) and
  screenshots given by file (copied in). `Items` lists newest first. `Changed`
  is raised on the UI thread.

Actions (`<feature>.<verb>`): `screenshot.capture`, `screenshot.fullScreen`,
`screenshot.scrolling`, `screenshot.editLatest`, `screenshot.editClipboard`,
`screenshot.closePins`, `captures.recent`, `screenOCR.capture`,
`screenOCR.openLanguageSettings`, `colorPicker.pick`.
Shortcut roles: `screenshot`, `screenshotFullScreen`, `screenshotLastCapture`,
`screenshotClipboard`, `recentCaptures`, `screenshotPrintScreen`, `screenOCR`,
`colorPicker`. Panel tiles: `screenshot`, `recentCaptures` (hosted), `screenOCR`,
`colorPicker`. Settings pages: `screenshot`, `screenOCR`, `colorPicker` (category Capture).

## Implemented

**Selector (§3.4)**
* One borderless, top-most overlay per monitor, placed over the monitor's exact
  physical rectangle (`SetWindowPos`, per-monitor DPI v2), tool window, kept out
  of every capture (`WDA_EXCLUDEFROMCAPTURE`). Mixed-DPI and negative virtual
  coordinates work (tested with a 150 % monitor left of a 100 % one).
* Frozen mode photographs every display first (the overlay shows the still under
  a 22 % dim); live mode is transparent (18 % dim). A display whose photograph
  failed gets no overlay; none at all → "The screen could not be captured".
* Drag to select (Shift square, Alt from the centre, Space held moves the
  selection), clamped to one display, 4-DIP click threshold, 2-DIP minimum,
  size badge in display pixels (the snapped even size for recording).
* Window picking: `EnumWindows` z-order, `DWMWA_EXTENDED_FRAME_BOUNDS`, skips
  invisible, minimized, cloaked, tool, shell and transparent windows, focus-border
  "decoration" windows (§6.22), our protected surfaces, and our own windows when
  "Hide app windows" is on (pins also while recording). Hover highlight per spec.
* Full screen pill (Screenshot only, pointer display, hidden during a drag).
* Keyboard (§3.4.7) through the shared low-level hook, so it works without focus:
  Esc, Enter/keypad Enter, Space, R (last region, app-session memory, dashed
  ghost outline), 1–4 tool switching (re-photographing when the source policy
  differs, generation-guarded, never resuming on stale pixels), S (scrolling),
  Z (loupe), C (copy colour without ending, "✓ value" for 1.4 s), arrows (nudge
  one device pixel, Shift = 10, crossing displays). Single keys are ignored with
  Ctrl/Alt/Win. A right click cancels too (Windows habit).
* Loupe (§3.4.8, §6.3): odd sample square, nearest-neighbour, grid from 6-DIP
  cells, reticle, info bar (swatch, value in the copy format, coordinates),
  Fast / Step-by-step wheel zoom (Alt swaps, fractional deltas = touchpad),
  remembered zoom, live-mode snapshot taken when the loupe turns on.
* Hint bar (§3.4.3): mode palette with digit badges, subtitle and key chips,
  the recording audio row (System sound / Microphone, writing the recorder's
  `recorderSystemAudio`/`recorderMicrophone`) with its space always reserved;
  the standalone plate for the scrolling selector. Recording appears only when
  `screenRecorder` is installed.
* Recorder interplay (§3.3 step 2, spec 02 §3.1): while the recorder is
  busy (recording, starting, counting down or writing the take), the chooser
  opens only from another tool's own shortcut with "show capture menu" off, and
  then offers only that tool, so Recording can't be picked from a menu and stop
  the take. Recording picked in a chooser another tool opened goes to the
  recorder's `IScreenRecorder.RecordSelectionAsync` (it runs its checks and
  countdown); without it, the region is kept for 5 s and the recorder's toggle
  action is invoked, whose `SelectAsync(Recording)` then gets the region back
  without a second selection. Both go through `RecorderLink`.

**Pixels (§3.5)** — `WindowsScreenCapturer`
* Displays: Windows.Graphics.Capture (one frame, free-threaded pool, shared
  D3D11 device with multithread protection) where it is border-free (Windows 11,
  `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` + `IsBorderRequired = false`)
  or needed (HDR); GDI `BitBlt` (+ `CAPTUREBLT`, cursor drawn with `DrawIconEx`)
  otherwise, and as the fallback everywhere. Windows 10 therefore never flashes
  the yellow capture border for display captures.
* HDR: DXGI `IDXGIOutput6` colour space detects advanced colour; such monitors
  are captured in FP16 scRGB and tone-mapped to sRGB with the monitor's SDR white
  level (`DISPLAYCONFIG_SDR_WHITE_LEVEL`), so SDR content looks as on screen and
  HDR highlights clip. BitBlt is the fallback.
* Windows: own pixels even when covered — WGC `CreateForWindow` first on
  Windows 11, `PrintWindow(PW_RENDERFULLCONTENT)` first on Windows 10 (2.5 s
  timeout, blank results rejected), then the other, then the frame cropped from
  the screen. Attached dialogs (same process or owned, in front, inside the
  frame) are captured too and drawn over the window (macOS "composite B").
* "Hide app windows": `WDA_EXCLUDEFROMCAPTURE` is set on our visible windows for
  the duration of a capture and removed afterwards (only windows that did not
  carry it). Workflow surfaces carry it permanently.
* Scrolling frames: repeated `BitBlt` of the region (cheap, no border).

**After capture (§3.7, §3.8)** — `CaptureRouter`, `CaptureOutputService`
* Latest-capture store (while "Edit latest screenshot" is on), recent captures,
  automatic copy (dropped when the clipboard changed meanwhile or a newer capture
  superseded it), default action Ask / Save / Save & Copy / Copy / Edit (Edit
  falls back to Save when no `IScreenshotEditor` is registered), automatic shelf
  copy through `IShelfIntake` (its own PNG in `cache\Shelf Screenshots`),
  preview decision matrix, raw outputs (only the 1x option applies).
* Save: folder (configured → Windows Screenshots known folder → Desktop →
  profile), dated subfolder that cannot escape, pattern tokens and `%#` runs,
  Windows-safe names, unique names to 9999, MAX_PATH shortening, atomic PNG write
  with pHYs DPI, toast "Saved to <folder>". Copy: cached file in
  `cache\Copied Screenshots` (pruned: 24 h, 100 files, 256 MB, never the current).
* Drag out (preview thumbnail) as a file from `%TEMP%\ScreenshotDrag-<GUID>`
  (deleted after 1 h, leftovers at launch). Windows share sheet
  (`DataTransferManagerInterop`) for the preview's Share button.

**Quick preview (§3.9)**: 350 × 210 DIPs, placement (automatic beside the
capture / corner after an action / fixed corners, display with the largest
overlap), timer paused while hovered or sharing, focus policy, Ctrl+C, Ctrl+S,
Ctrl+W, Delete/Backspace (discard: Recycle Bin + `%#` rewind), Enter/E (edit),
Esc (dismiss only), Save/Copy dimmed when the action did them, QR button after
a background scan, Pin, Share, × for persistent previews, drag out.

**Pins (§3.11)**: aspect-locked always-on-top image windows, natural size
capped at 55 % of the work area, cascade, drag to move, edge drag to resize,
Ctrl+wheel / Ctrl+plus/minus zoom and Ctrl+0 actual size, double-click/Esc/Ctrl+W
close, arrows nudge (Shift = 12 DIP), Ctrl+C copy, Ctrl+S Save As, context menu
(Copy, Save As…, Actual size, Opacity 100/85/70/50 %, Ignore clicks, Close,
Close all pins), Alt+click recovery of click-through pins (mouse hook only while needed).

**Recent captures (§3.12)**: 12 entries, screenshots within 256 MB (the newest
always kept), thumbnails ≤ 360 px, JSON index in `%LOCALAPPDATA%\<app>\cache\RecentCaptures`,
frozen (never wiped) when the index is unreadable or missing while images exist,
orphan cleanup only for `<GUID>.png` / `<GUID>-thumbnail.png`, never through
links; writes serialized so cleanup never sees a half-added capture. Palette
window (shortcut, Esc / click elsewhere hides it), hosted panel view, Restore
(reopens the preview, no automatic action) / Open (recordings), Remove, Clear.

**Other**: Edit latest screenshot / Edit clipboard image (§3.13; file beats the
image data, 60 MP limit, without the editor the image opens in the preview);
direct full-screen capture (§3.6); countdown 3/5/10 s with the drain ring
(§3.3, §6.24); scrolling capture (§3.15, §6.16 exact matching policy, SIMD
row differences, footer and side-column handling, limits, partial/limited
results, Enter/Esc via the hook, optional automatic scrolling with
`IInputHooks.SendWheel` that stops at the end of the page); Copy text from
screen (§3.16: QR first, Windows.Media.Ocr with the app language, English and
the profile languages, 2× upscaling of small text, CJK spacing fix, §6.17
joining); QR result window (§3.17); colour picker (§3.18, §6.19); Settings
pages with every option of §4.0 that applies.

## Not implemented

* **Temporary share links (§3.14)**: they upload to the macOS maintainer's
  server, which this fork must not use. No Link buttons, no "Upload latest
  screenshot" shortcut, no Temporary links settings, no withheld marker.
* The **screenshot editor** (another module). Editor-only settings
  (tool order and keys, styles, backdrop, watermark) are not on this page.
* **Micro QR** (ZXing.Net has no reader). Data Matrix and Aztec are found only
  roughly centred in a tile (ZXing limitation; a tiled second pass covers codes
  up to about a quarter of the image's short side).
* Removing the automatic shelf copy when a capture is discarded: `IShelfIntake`
  has no remove (see requests); a pending copy is still cancelled.
* §3.3 step 1 (a Recording entry stops a running take) belongs to the recorder's
  own toggle; this module never opens the chooser *for* Recording itself.
* Excluding the **editor's** windows while recording (content windows): only
  pins are known to this module (see requests).
* macOS-only parts: notch variants, permission flows, Spotlight tag, TIFF clipboard.

## Deviations from macOS (Windows adaptations)

* **Shortcuts** (all off by default, as on macOS): Ctrl+Alt+Win+4 (Screenshot),
  +3 (whole screen), +E (edit latest), +P (edit clipboard), +H (recent),
  +T (text), +C (colour). **Print Screen** is an extra opt-in role ("Use the
  Print Screen key", off by default): it registers Print Screen with
  `HotkeyOptions.OverrideSystem`, so it works when Windows 11 hands the key to
  the Snipping Tool (taken over through the shared hook while enabled; Windows
  gets it back when the option is off or the app quits). Off by default because
  taking a system key over should be the user's choice.
* Default save folder is the Windows **Screenshots** known folder
  (`Pictures\Screenshots`, where Win+Print Screen saves) instead of the Desktop.
* PNG density convention: **DPI = 96 × scale** (Windows treats 96 DPI as 100 %);
  read back as `scale = DPI / 96` (rounded to 0.01, valid 0.5–4×). The DIBV5 on
  the clipboard carries the same density.
* Colour format "SwiftUI" became **C#**: `Color.FromArgb(255, 30, 144, 255)`,
  valid with WPF, WinUI/UWP and WinForms colours; a stored `swiftui` reads as `csharp`.
* The palette's "1–4" key chip shows only when the palette is hidden (the buttons
  already show their digits); otherwise the subtitle is cut off at 620 DIPs.
* Recent captures is an accessory button under the Screenshot row that shows the
  history inside the panel, as on macOS. The recorder's row has the same button,
  hidden while recording; it opens the palette.
* Toasts use the app's shared HUD (bottom centre) rather than the macOS top
  centre; the colour toast with its swatch is a capture-owned window in the same place.
* Mac wording overridden in `capture.en-US.json` (Alt for ⌥, touchpad, Enter,
  "100 % size" for Retina). "Hide Vorssaint windows" is rebranded automatically.
* Recorder audio toggle "Mac sound" reads "System sound" (`win.capture.systemAudio`).
* OCR: Windows has no accurate/fast levels or language detection; engines are
  tried in order (app language, English, profile languages) and the first that
  finds lines wins. Without any OCR language pack the user gets a HUD, a toast
  with "Open language settings" and a status card on the Settings page.

## Risks (not verifiable without Windows)

1. **WGC interop** (`IGraphicsCaptureItemInterop` via function pointers,
   `IDirect3DDxgiInterfaceAccess`, CsWinRT marshalling) is type-checked only.
   Every WGC failure falls back to GDI, but a crash inside native code would not.
2. **Borderless access** on Windows 11 for unpackaged apps is assumed to be
   granted without a prompt; if it is refused, display captures use BitBlt and
   window captures use PrintWindow first (both border-free).
3. **HDR tone mapping** constants (SDR white level fallback 2.5 = 200 nits) need
   a check on a real HDR monitor.
4. **Click-through windows** (countdown, colour toast, pins "Ignore clicks"):
   `WS_EX_LAYERED | WS_EX_TRANSPARENT` plus `SetLayeredWindowAttributes(255)` on
   Avalonia's composition windows — verify they stay visible.
5. **Focus**: the overlay is activated with the shared `BringToFront`; keyboard
   works through the hook regardless, but a capture started from the Settings
   window or a tray click should be checked for focus-steal refusal.
6. **Clipboard from a worker thread**: images are written from a thread-pool
   thread (open/close on that thread, the host window as owner).
7. **PrintWindow on hung apps** is bounded by a timeout, but the worker thread
   stays blocked until the app answers.
8. Avalonia renders custom draw operations on its render thread: the overlay
   draws an immutable scene built on the UI thread and stills are released a
   second after the session ends.

## Manual test checklist (Windows 10 22H2 and Windows 11 23H2+)

Prepare: install Screenshot, Copy text from screen and Color picker in
Settings › Features. Turn on the shortcuts in Settings › Screenshot.

**Selector**
1. Ctrl+Alt+Win+4 → the screen freezes and dims, the hint bar is at the bottom
   centre of the pointer's monitor, "Full screen" at the top. Animations behind
   (a playing video) stay frozen.
2. Drag an area → badge shows `W × H` physical pixels; Shift makes a square, Alt
   grows from the centre, holding Space moves the selection. Release → preview
   appears next to the area.
3. Hover windows → blue highlight on the window under the pointer (not on the
   taskbar, desktop, tooltips or the app's own windows); click → that window,
   including when it is partly covered (check a browser and File Explorer, and a
   UWP app like Calculator). Windows 11: rounded corners are transparent.
4. Enter → whole monitor. "Full screen" pill → the same. Esc → nothing, focus
   returns to the previous app. Right click → cancels.
5. Z → loupe; wheel zooms (notched mouse: fast; touchpad: smooth); Alt while
   scrolling switches the mode; arrows move the pointer one pixel, Shift ten;
   C copies the colour (paste into Notepad) and the readout shows ✓.
6. R → repeats the last area; the dashed outline of the last area shows when
   "Show the last capture outline" is on.
7. 1–4 switch tools (Recording only when installed); with "Freeze" off, switching
   from Screenshot to Text re-freezes the screen without flicker of stale pixels.
8. Two monitors at **different scaling** (100 % + 150 %, one left of the primary
   so it has negative coordinates): overlays cover each monitor exactly (no gaps
   at edges, taskbar covered), the hint bar and loupe follow the pointer, a drag
   cannot cross monitors, the saved PNG of the 150 % monitor is pixel-sharp and
   reads 144 DPI in its properties.
9. Freeze off → the overlay is transparent; area capture contains no overlay.
10. Include the pointer on → the cursor is in the picture.
11. "Hide app windows" on → Settings and pins are absent from the frozen image
    and not pickable; off → they appear and can be picked (pins are excluded only
    while recording).
12. Windows 10: no yellow border flash for area/full-screen captures. Clicking a
    window may flash it only if PrintWindow failed for that app.
13. HDR monitor (Windows 11, HDR on): the frozen image and the saved PNG look like
    the screen (not washed out, not too dark).

**Outputs**
14. Default action Ask → preview with Edit/Save/Copy/Discard; Save writes
    `Pictures\Screenshots\Screenshot yyyy-MM-dd at HH.mm.ss.png`; Copy then paste
    into Paint (image), Word (image) and File Explorer (a file).
15. Default action Save / Save & Copy / Copy → toast; confirmation preview for the
    chosen duration (and none when off). Until dismissed → preview stays.
16. File name pattern `Shot %y-%mo-%d %###`, subfolder `%year\%mo` → names and
    folders as previewed in Settings; Starts at / Reset / Next work.
17. Save at 1x on a 150 % monitor → the PNG is 2/3 of the pixel size.
18. Preview: hover pauses the timer; Delete moves a saved file to the Recycle Bin;
    Esc never deletes; drag the thumbnail into a chat app or Explorer → a PNG file;
    Share opens the Windows share sheet; Pin creates a pin.
19. Copy to the clipboard automatically → every capture is on the clipboard.
20. Shelf installed and on + "Add to the shelf automatically" → captures appear on the shelf.

**Tools**
21. Pin: drag, resize from an edge (aspect kept), Ctrl+wheel zoom, Opacity menu,
    Ignore clicks then Alt+click to get it back, Esc closes, Close all pins.
22. Recent captures: Ctrl+Alt+Win+H palette; Restore reopens the preview; Remove
    deletes; Clear asks first; the panel's Recent captures tile lists the same.
23. Edit latest screenshot / Edit clipboard image (copy an image in a browser,
    then a PNG file in Explorer) → editor (or the preview without the editor
    module); "Take a screenshot first" / "Copy an image first" when there is none.
24. Capture the whole screen shortcut; Delay 3 s shows the countdown ring and
    pressing the shortcut again cancels it.
25. Scrolling capture (Settings button, or S in the chooser): select the part of a
    long web page that scrolls, scroll with the wheel, Done/Enter → one tall image
    with the fixed header/sidebar cut and a fixed footer once at the end;
    "Scroll automatically" scrolls and finishes by itself; Esc → "Cancelled."
26. Copy text from screen: select text → pasted text keeps reading order;
    "Remove line breaks" joins lines; a QR code shows the QR window (Copy / Open
    link); Japanese/Chinese text has no spaces between characters when the
    language pack is installed. Remove all OCR languages → the explanation
    toast with "Open language settings".
27. Colour picker: click picks and copies; formats HEX (with/without #), RGB, HSL, C#.

**Shortcuts and system state**
28. "Use the Print Screen key": Windows 11 with "Use the Print screen key to open
    screen capture" on → Print Screen opens our chooser instead; turn the option
    off or quit the app → the Snipping Tool gets it back.
29. Elevated window (e.g. an admin Command Prompt) is pickable and captured
    (PrintWindow may fail under UIPI; the screen-crop fallback must work).
30. Uninstall Screenshot in Features while a preview and pins are open → they
    close; the shortcuts unregister.

**With the screen recorder installed**
31. Open the Screenshot chooser, press 2, drag an area → the recording starts on
    that area after the recorder's countdown, without a second selection; the
    audio toggles under the hint bar match Settings › Screen recording.
32. While recording: the Screenshot tile and Ctrl+Alt+Win+4 (menu on) open nothing;
    turn "Show capture menu…" off for Screenshot → the shortcut opens a chooser with
    only Screenshot (2 does nothing) and the take keeps running.
33. Recorder shortcut with its "Show capture menu…" off → chooser without the
    palette; 1 still switches to Screenshot and that capture is routed as usual.

## Requests for shared code

Done at merge time: `ShortcutRoleRow` no longer crashes, tile accessories exist,
`RecorderLink` calls `IScreenRecorder` directly, `StringKeyTests` only reads
literals in a localization context, and `WindowsWindowChrome` sets
`SetLayeredWindowAttributes(255, LWA_ALPHA)` when `ClickThrough` makes a window layered.

Still open:

1. **Content-window registry** (`ICaptureContentWindows { void Register(Window); IReadOnlyCollection<nint> Handles; }`)
   so editor windows are kept out of captures while recording (§3.1); pins already are.
2. **`IShelfIntake.Remove(string path)`** (or an id returned by `AddFiles`) so
   discarding a capture removes its automatic shelf copy (§3.7 step 7).
3. **`IHud` colour swatch** (`Show(message, …, swatchArgb)`) so the colour picker
   can use the shared HUD instead of its own toast window.
4. **Clipboard history**: treat the registered format `"<AppId>.CaptureSource"`
   (payload `screenshot` or `text`) as "copied by this app": store copied
   screenshots as images, not files (spec §3.8.3).
5. This module still uses its own `CaptureShortcutRow`; it can be swapped for the
   shared `ShortcutRoleRow`.
6. **Duplicate windows.** The screenshot editor ships its own `PinWindow` and
   `QrResultWindow` next to this module's `Pins/PinWindow` (with `PinManager`:
   cascade, close all, Alt+click recovery) and `Text/QrResultWindow`. They don't
   clash (different namespaces); consolidating them is a later clean-up.

## Merge notes

* "Edit latest screenshot" and "Edit clipboard image" are this module's actions
  only (`screenshot.editLatest`, `screenshot.editClipboard`); the screenshot
  editor's duplicates were removed. They open the editor through
  `IScreenshotEditor`, or the quick preview without it.
* The Recent captures palette's action id is shared as
  `Rivet.Core.Contracts.RecentCaptureActions.ShowPalette`; both editors' history
  buttons and the recorder's tile use it.
* The capture settings pages are `PrimaryFor` their features, so the Features hub
  opens them rather than the screenshot editor's page.
* `RecorderFlowTests` answer the chooser with `tests/Rivet.App.Tests/Capture/ChooserDriver.cs`.
