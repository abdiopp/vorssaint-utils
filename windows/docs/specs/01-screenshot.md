# Spec 01: Screenshot capture, screenshot editor and related capture tools

**Area:** Unified screen-capture selector, screenshot capture (area, window, display, scrolling), quick preview, annotation editor, backgrounds and watermarks, pinned captures, recent captures, temporary share links, Copy text from screen (OCR), QR reading, color picker.
**Source of truth:** Vorssaint for macOS (Swift/SwiftUI/AppKit), files under `Sources/Vorssaint/Services/QuickTools/` (`Screenshot*.swift`, `ScreenCaptureService.swift`, `ScreenTextService.swift`, `ColorSamplerService.swift`, `BarcodeDetector.swift`, `QRResultController.swift`, `RecentCapture*.swift`, `QuickToolHUD.swift`, `QuickToolHotkey.swift`, `QuickToolsSupport.swift`, `WindowActivationPolicy.swift`), `Sources/Vorssaint/UI/Screenshot/*`, the related settings and menu-panel views, `Core/ColorValue.swift`, `Core/Defaults.swift`, `Core/ScreenshotStrings.swift`, `Core/RecentCaptureStrings.swift` and the corresponding tests.
**Audience:** A developer building the Windows version without reading the Swift code. The spec doesn't assume a Windows UI stack.

Conventions used here:

- "pt" means logical points (macOS) and should map to DIPs (device-independent pixels) on Windows. "px" means physical image or display pixels.
- **scale** is the capture's pixels per point (1 or 2 on macOS, and 1.0, 1.25, 1.5, 1.75, 2.0 and so on for Windows DPI). Wherever a constant is written as `N*scale`, it's N points expressed in image pixels.
- "Image space" means a top-left-origin coordinate system measured in pixels of the captured image. All annotation geometry lives in image space.
- ⌘ = Command, ⌥ = Option, ⌃ = Control, ⇧ = Shift. Section 8 proposes Windows equivalents. Where the macOS key is quoted, the behavior is what's specified.
- **[Mac-only]** marks behavior tied to macOS hardware or chrome (the notch "Dynamic Island", Spaces, Spotlight metadata) that can be dropped or redesigned.
- **[Win decision]** marks a point where the Windows port has to pick a product behavior that has no direct macOS counterpart.

---

## 1. Overview

Vorssaint gives you one capture surface for four tools: Screenshot, Screen recording, Copy text from screen (OCR) and Color picker. You switch between them with the number keys 1 to 4 or a small mode palette. The surface optionally freezes every display, then lets you drag an area, click a window or press Return for the whole display, with a pixel magnifier, size badges and window highlighting. A finished screenshot goes through a configurable after-capture pipeline: automatic copy, a default action (ask, save, save and copy, copy or edit), recent-capture history, an optional shelf, and a floating quick preview with Edit, Save, Copy, Discard, Pin, Share and Link actions. The annotation editor has 13 tools (arrows in 5 styles, shapes, pen, highlighter, text, numbered steps, stickers, a blur, pixelate or erase tool with a "text only" mode driven by on-device OCR, solid redaction, and crop). It also offers backgrounds (gradients, solid colors, wallpapers, images, margin, corners, blur, drop shadow), watermarks, layers, undo and export, all through one shared renderer. Related tools include pinned floating captures, scrolling capture (pixel-polling stitcher), 1, 6 or 24-hour expiring links on a Vorssaint-run service, OCR to the clipboard, QR detection with a confirmation panel, and a color picker with HEX, RGB, HSL and SwiftUI output.

---

## 2. Feature inventory

### 2.1 Capture surface ("Screen capture" chooser)
- [ ] One full-screen overlay per display, above all other windows. Only one capture session can be on screen at a time.
- [ ] Four modes: **Screenshot**, **Screen recording** (hand-off to the recorder feature), **Copy text from screen**, **Color picker**. Uninstalled features don't appear.
- [ ] Mode palette (buttons with digit badges 1–4). Digit keys switch modes even when the palette is hidden.
- [ ] Each tool has its own optional global shortcut that opens the chooser already on that tool, with a per-tool option "Show capture menu when using keyboard shortcut".
- [ ] Freeze the screen while selecting (default on). When off, the overlay is transparent and live.
- [ ] Drag to select an area, with live size badge in pixels. ⇧ makes it square, ⌥ grows it from the center, and holding Space while dragging moves the selection.
- [ ] Click a window to capture it, with hover highlight on the window under the pointer.
- [ ] Return captures the whole display under the pointer. A "Full screen" pill button does the same.
- [ ] R repeats the last captured area (session memory, same display). A dashed outline of the last area is shown (optional).
- [ ] Pixel magnifier (loupe): Z toggles it, the wheel zooms (fast or stepped, ⌥ swaps), arrow keys nudge the pointer by 1 device pixel (⇧ = 10), and C copies the color under the pointer. The readout shows a swatch, the color value and pixel coordinates.
- [ ] S toggles scrolling-capture mode (Screenshot mode only).
- [ ] Esc cancels, including when the overlay doesn't have focus.
- [ ] Optional countdown (3, 5 or 10 s) before the surface appears (Screenshot mode).
- [ ] Includes or excludes the pointer (Screenshot setting). Hides or shows the app's own windows (setting).
- [ ] A recording in progress blocks the chooser, except for a tool's direct shortcut with the menu turned off.

### 2.2 Screenshot capture
- [ ] Area (from the frozen image or a live capture), window (the window's own buffer, including attached sheets and dialogs), whole display.
- [ ] Direct "Capture the whole screen" shortcut with no overlay (display under the pointer).
- [ ] Scrolling capture: select an area, scroll with any input, Done or Return stitches the result. Fixed footers and side columns are handled, and there are safety limits.
- [ ] Capture delay applies to the chooser and the direct full-screen capture.

### 2.3 After-capture pipeline
- [ ] Copy to the clipboard automatically (optional, silent).
- [ ] Default action: Ask each time, Save, Save & Copy, Copy, Edit.
- [ ] Confirmation preview after a successful automatic action (on or off, duration 1, 2, 3, 5 or 10 s, or until dismissed). A recovery preview (12 s) appears after a failed or partial action.
- [ ] Add to the shelf automatically (optional, needs the Shelf feature).
- [ ] Every screenshot is recorded in Recent captures.
- [ ] The latest capture is stored on disk for the "Edit latest screenshot" and "Upload latest screenshot" shortcuts.

### 2.4 Quick preview (floating thumbnail)
- [ ] Thumbnail (click to edit, drag out as a PNG). Actions: Discard (trash), QR (when a code is detected), Save, Copy, temporary Link (split button with durations), Edit, Share (system share sheet) and Pin.
- [ ] Position: Automatic (beside the capture, or the bottom-right corner when an action already ran), or a fixed corner. The display is chosen by where most of the capture lies.
- [ ] Auto-dismiss timer pauses while hovered, during sharing or while the share sheet is open.
- [ ] Keyboard: ⌘C, ⌘S, ⌘⌫ or ⌫ for discard (moves a saved file to the Trash), Return or E for edit, Esc to dismiss, ⌘W to close.
- [ ] Optional focus-taking on appear (default on).

### 2.5 Editor
- [ ] Tools: Select, Arrow, Blur (styles: Pixelate, Blur, Erase, plus "Text only"), Crop, Text, Sticker, Rectangle, Highlighter, Pen, Line, Ellipse, Number (counter), Solid block (redact).
- [ ] 8 colors, 3 thicknesses, 12 text sizes, 5 arrow styles, 12 stickers, 5 blur strengths, annotation drop shadows on or off.
- [ ] Select, move, resize with 8 handles, drag arrow and line endpoints, delete, bring forward and send backward.
- [ ] Inline text editing.
- [ ] Crop with pixel-snapped edges and an edge magnifier.
- [ ] Undo and redo (60 steps). Closing an unexported edited capture asks for confirmation.
- [ ] Zoom: fit, 1:1, ⌘+ and ⌘−, ⌃-scroll, pinch, with a percentage readout.
- [ ] On-device OCR of the capture: select recognized words on the canvas and copy them as text (⌘C). It also drives "Text only" blurring.
- [ ] QR code detection with a result panel.
- [ ] Background: none, 5 gradient presets, custom solid or 2-color gradient (20-color palette), saved custom backgrounds (12 max), current desktop wallpapers, any image. Sliders for Margin, Corners and Blur. Drop shadow under the capture card.
- [ ] Watermark: text (8 colors) or image, 9 positions, Size, Opacity, Rotation, saved watermarks (12 max).
- [ ] Outputs: Copy (Return or ⌘C), Save (⌘S), Save As… (⌘⇧S), Add to Shelf, Pin (⌘P), system Share, temporary Link (1, 6 or 24 h), drag-out handle, Discard (trash, ⌘⌫).
- [ ] Customizable tool order (the first 9 get digit shortcuts) and custom per-tool keys, with conflict checks.
- [ ] Remembers the last tool and style settings across captures.
- [ ] Opens from: a capture, Recent captures, the "Edit latest screenshot" shortcut, the "Edit clipboard image" shortcut, Shelf "Edit image", and clipboard-history "edit image".

### 2.6 Related tools
- [ ] **Pinned captures**: floating always-on-top images. Drag to move, aspect-locked resize, arrow-key nudge, double-click or Esc to close, a right-click menu (Copy, Save As…, Opacity 100/85/70/50 %, Ignore clicks, Close, Close all pins), and ⌥-click to recover a click-through pin.
- [ ] **Recent captures**: the last 12 screenshots and recordings (screenshots limited to 256 MB in total), as a palette window, a menu-panel page and a global shortcut. Restore reopens the quick preview, and Open launches a recording.
- [ ] **Temporary links**: upload the rendered PNG to `https://screenshots.vorssaint.com` with an expiry of 1, 6 or 24 h. The link is copied, can be deleted early, and is listed under Shared links in Settings. A Privacy sheet explains the service. The "Upload latest screenshot" shortcut is opt-in.
- [ ] **Copy text from screen**: select an area. A QR code wins if detected (shown in a result panel). Otherwise the text is recognized on device and copied (option: remove line breaks).
- [ ] **QR result panel**: shows the decoded payload with Copy, plus Open link for a single http or https URL.
- [ ] **Color picker**: the chooser in Color mode with the loupe forced on. Click or Return copies the value. C copies without ending the session. Formats are HEX (optionally without "#"), RGB, HSL and SwiftUI.
- [ ] **HUD toasts**: small top-of-screen confirmations (copied, saved, link copied, and so on), a countdown ring, and the scrolling-capture control bar.

### 2.7 Entry points
- [ ] Global shortcuts (all off by default; see §4.1).
- [ ] Menu bar panel tiles: Screenshot (with a "Recent captures" accessory), Copy text from screen, Color picker.
- [ ] Command Bar commands: Screenshot, Scrolling screenshot, Recent captures, Copy text from screen, Color picker.
- [ ] Quick Launcher items, radial menu items, and notch controls ([Mac-only] notch).
- [ ] Settings page buttons: Capture now, Scrolling capture, Copy text from screen, Pick color.

---

## 3. Detailed behavior

### 3.1 Core concepts

**Capture object.** Every screenshot that moves through the system is a triple:

| Field | Meaning |
|---|---|
| `image` | Bitmap (RGBA, sRGB). Exact pixels of what was captured. |
| `scale` | Pixels per point of the source display (for example 2 on Retina). Drives 1x export, stroke weights, text sizes, PNG DPI and the editor's "natural" size. |
| `anchorRect` | The captured area in global screen coordinates. It's used only to place the quick preview next to the area. It's zero for captures that don't come from the screen (clipboard image, file, latest-capture store). |

**Region object** (for the recorder and scrolling capture): `displayID`, optional `windowID` (when a window was clicked), `pixelRect` (top-left origin, in display pixels, snapped to **even** width and height with a minimum side of 32 px, shrunk rather than shifted to stay inside the display), `anchorRect` (global points, derived from the snapped pixels) and `scale`.

**Own-window classes.** The app distinguishes two kinds of its own windows when it captures:

- *Workflow surfaces* are always kept out of captured pixels: the selection overlays, the countdown HUD, the toast HUD, the scrolling-capture control bar, the quick preview, and the notch chrome during a selection.
- *Content windows* are editor windows and pinned captures. They're kept out only when **Hide Vorssaint windows** is on (default), or always when the recording tool is selected.

When **Hide Vorssaint windows** is on, **every** window of the app (including Settings, the menu panel and so on) is excluded from pixels and from window picking. When it's off, the app's ordinary windows show up in captures and can be clicked as targets, but workflow surfaces never can.

**Single session.** A process-wide flag marks "a capture surface is on screen". Every entry point refuses to start a second one. Calling the same entry point during a countdown cancels the countdown. If a session object is destroyed without finishing, the flag is cleared (fail open).

### 3.2 Entry points

| Entry point | What it does |
|---|---|
| Screenshot tool shortcut (default ⌃⌥⌘4, off) | Opens the chooser on Screenshot. |
| Recording tool shortcut (⌃⌥⌘5, off) | Opens the chooser on Recording. If a recording is running or counting down, it stops or cancels it instead. |
| Copy text from screen shortcut (⌃⌥⌘T, off) | Opens the chooser on Text. |
| Color picker shortcut (⌃⌥⌘C, off) | Opens the chooser on Color. |
| Capture the whole screen (⌃⌥⌘3, off) | Captures the display under the pointer directly, with no chooser (§3.6). |
| Edit latest screenshot (⌃⌥⌘E, off) | Opens the latest screenshot in the editor (§3.13). |
| Edit clipboard image (⌃⌥⌘P, off) | Opens the clipboard image in the editor (§3.13). |
| Upload latest screenshot (⌃⌥⌘U, off; needs temporary links on) | Uploads the latest screenshot and copies the link (§3.14). |
| Recent captures (⌃⌥⌘H, off) | Shows the history palette (§3.12). |
| Menu bar panel tile "Screenshot" | Closes the panel, waits 0.2 s, then opens the chooser on Screenshot. The tile has a "Recent captures" accessory button that shows the history inside the panel. The tile caption reads "Permission required: Screen Recording" when that permission is missing. |
| Panel tile "Copy text from screen" | Closes the panel, waits 0.2 s, opens the chooser on Text. |
| Panel tile "Color picker" | Closes the panel, waits 0.15 s, opens the chooser on Color. |
| Command Bar "Screenshot" / "Scrolling screenshot" / "Recent captures" / "Copy text from screen" / "Color picker" | Runs after a 0.15 s pause (0.1 s for Recent captures) so the bar can leave the screen first. "Scrolling screenshot" starts the standalone scrolling selector (§3.15). The bar flags "Scrolling screenshot" as needing permission unless both Screen Recording and Accessibility are granted [Mac-only]. |
| Quick Launcher and radial menu items | Hide themselves, wait 0.15 s, then open the chooser on the matching tool. |
| Settings page buttons | "Capture now" (chooser on Screenshot), "Scrolling capture", "Copy text from screen", "Pick color". |
| Shelf item "Edit image", clipboard history "edit image" | Open that image in the editor. A file that isn't an image beeps. |
| Notch controls [Mac-only] | Screenshot tile opens the chooser. |

Every tool button and command (anything that isn't a shortcut) always shows the mode palette.

### 3.3 Starting a capture: gating and countdown

When any chooser entry is invoked with a preferred tool `T` (or none):

1. If `T` is Recording and the recorder has an active recording, countdown or pending start, stop or cancel it and return.
2. If a recording is active, continue only when `T` is given, isn't Recording, and was invoked **from its shortcut with "show capture menu" turned off**. In that case the chooser offers only `T`. Otherwise do nothing.
3. If a countdown is running, cancel it and return. (Pressing the shortcut again cancels the countdown.)
4. If a session is already on screen, do nothing.
5. Available tools are the installed features, in the fixed order Screenshot, Recording, Text, Color. If there are none, do nothing. The selected tool is `T` if available, otherwise Screenshot if available, otherwise the first one.
6. Permission: on macOS, Screen Recording permission is required. Without it, Color falls back to the system's native color sampler (no permission needed), and every other tool triggers the system permission request. [Win decision: Windows needs no permission for desktop capture, so this step goes away.]
7. Recording needs extra preconditions checked by the recorder (permissions, free disk space). A failure aborts.
8. **Show capture menu**: always true from buttons. From a shortcut it uses the tool's own preference (default true).
9. **Delay** applies only when the selected tool is Screenshot: `screenshotDelay` ∈ {0, 3, 5, 10} s (any other stored value counts as 0). With a delay, show the countdown HUD (§3.19) once per second, then open the selector. The delay happens **before** the overlay appears, so menus and hover states can be staged and then frozen.

The standalone screenshot paths (direct full-screen capture and standalone scrolling capture) follow the same rules: invoking either of them during a running scrolling capture finishes that capture (§3.15), a running session blocks, a countdown is cancelled by a repeat press, and a missing permission triggers a request.

### 3.4 The unified capture selector

#### 3.4.1 Overlay windows
- One borderless, non-activating overlay window per connected display, covering the display's full frame (including the menu bar or taskbar area). It sits at the highest practical level (above menus, the Dock and full-screen windows), appears on the current desktop, and isn't listed in window cycling.
- The overlay accepts the first click without needing to be activated first, so the first drag always draws.
- With **freeze on**, every display is photographed first (§3.5.1). The overlay is opaque and shows its display's still image scaled to fill it. The overlay window is drawn once before it's shown so it never flashes black. A display whose photograph failed gets no overlay. If no display succeeded, the session fails and shows "The screen could not be captured".
- With **freeze off**, the overlay is transparent over the live desktop.
- The cursor is a crosshair over the overlay. Keyboard focus goes to the overlay under the pointer (except in notch mode). When the session ends, the cursor returns to the arrow.

#### 3.4.2 Modes and capture policies
The selector has an internal *mode*: **image** (return pixels), **geometry** (return a Region, used by the recorder, scrolling capture and other features) and **color** (return one color).

| Selected tool | Mode | Freeze | Include pointer | Hide all own windows | Keep editors and pins out |
|---|---|---|---|---|---|
| Screenshot | image | setting `screenshotFreeze` (default on) | setting `screenshotIncludePointer` (default off) | setting `screenshotHideVorssaintWindows` (default on) | same as hide setting |
| Recording | geometry | always on | off | **always off** | **always on** |
| Copy text from screen | image | always on | off | hide setting | same as hide setting |
| Color picker | color | always on | off | hide setting | same as hide setting |

Two tools *share a source* when freeze, include-pointer, hide-own-windows and keep-content-windows-out all match. In that case switching between them reuses the current photographs.

#### 3.4.3 Chooser UI (hint bar)
The hint bar sits at the **bottom center** of each display, 32 pt above the bottom edge. Its width is `min(620, max(280, displayWidth − 32))` (680 for the standalone variant). Its height is 146 pt with the mode palette, 82 pt without, and 72 pt for the standalone variant. It appears only on the display that holds the pointer, and it's hidden for the whole duration of a drag and once a capture is pending. It stays visible when system chrome (Dock, menu bar) takes the pointer while it's still on the display.

Rows, top to bottom:

1. **Mode palette** (only when "show capture menu" applies): a container with 4 pt padding, 14 pt corner radius, material background and a hairline border. It holds one button per available tool, each 116 × 48 pt with a 10 pt radius. Each button shows a 19 × 19 digit badge (`1`–`4`, accent fill when selected), the tool icon (camera viewfinder, record circle, text viewfinder, eyedropper) and the tool title ("Screenshot", "Screen recording", "Copy text from screen", "Color picker"). The selected button has an accent background at 16 % with an accent border at 48 %. Hover shows a primary-color wash at 8 % (3.5 % idle). The tooltip reads "Title (digit)". Selection animates (ease-out 0.16 s). To its right is an **esc** hint ("× esc"), 56 pt tall.
2. **Contextual capsule** (30 pt tall): a subtitle followed by key hints. When the palette is hidden, the esc hint (30 pt) moves to this row.
   - Subtitle per tool:
     - Screenshot: "Drag to select an area · Click a window to capture it · S toggles scrolling" (the last part reads "Scrolling on" when scrolling is enabled).
     - Recording: "Choose what to record · Drag to select an area · Click a window to capture it".
     - Text: "Select an area of the screen and the recognized text is copied, ready to paste."
     - Color: "Grab the color of any pixel on screen and copy it in your favorite format."
   - Key hints (icon plus key label in small chips): `1–4` (keyboard icon) always. For every tool except Color: `↩` (full screen), `S` or `S on` (Screenshot only, when scrolling is supported), `Z` or `Z on` (loupe), `C` (only while the loupe is on), `R` (only when a repeat region is available).
3. **Recording audio row**: toggle buttons "Mac sound" (system audio) and "Microphone". They're visible and interactive only for Recording, but their space is **always reserved** (invisible) so the bar never changes height when you switch modes. These toggles write the recorder's audio preferences.

**Standalone variant** (used by the standalone scrolling capture and other features): a single 72 pt plate (16 pt radius) with a viewfinder icon, a title (the *purpose* string, for example "Scrolling screenshot", or "Drag to select an area" when there's no purpose), a subtitle, and key hints `↩` (hidden in scrolling or region-only mode), `S` (when offered), `Z`/`Z on`, `C` (loupe on), `R` (repeat available) and `esc`. Subtitle: "Drag only the part of the page that moves." in region-only or scrolling mode. Otherwise it's "Drag to select an area · Click a window to capture it" when a purpose is set, or "Click a window to capture it" when not. The scrolling hint is appended when scrolling is offered. The standalone variant lets clicks pass through it to the overlay.

Background treatment: each chrome element uses a translucent material plus an opaque "contrast plate" (white at 50 % in light mode, black at 55 % in dark mode, none when the OS has reduced transparency turned on), because the overlay is opaque when frozen and a live blur would show the wrong pixels.

#### 3.4.4 Pointer interaction
- **Mouse down** starts a drag at the point and sets "selection in progress", which hides the hint bar and the full-screen pill.
- **Drag** updates the rectangle from the origin to the current point:
  - ⇧ held: square, with side `max(|dx|, |dy|)` in the drag's direction.
  - ⌥ held: the origin is the **center** and the rect grows symmetrically.
  - **Space held during a drag**: the selection *moves* by the pointer delta instead of resizing, and the origin moves with it. Releasing Space resumes resizing.
  - The selection is clamped to the display (a selection can't span displays).
- **Mouse up**:
  - Color mode: picks the color at the release point (§3.18). Dragging in color mode only moves the loupe.
  - If the pointer moved less than **4 pt** in both axes, it's a **click**. If window clicks are allowed (not color mode, not region-only, not scrolling mode) and a window is under the point, that window is captured. Otherwise nothing happens and the selection is cleared.
  - If the selection is smaller than 2 × 2 pt, it's cleared (no capture).
  - Otherwise the region is confirmed.
- **Wheel**: when the loupe is on, zooms the loupe (§3.4.8). Otherwise it's ignored.
- After a capture starts ("capture pending"), and after the session ends, every remaining pointer event is ignored. Multi-finger gestures can deliver late releases.

#### 3.4.5 Window picking and highlight
- At presentation (and after every source refresh), the app takes a snapshot of on-screen windows in **front-to-back order**: ordinary application windows only (not menus, Dock or desktop elements), opacity above 0.01, at least 40 × 40 pt. Windows that the visibility policy forbids are filtered out (§3.1), along with **decoration windows**, meaning untitled windows from another process that tightly frame their z-order neighbor, such as focus borders (§6.22).
- Hit test: the **first** window in front-to-back order whose frame contains the point wins.
- While not dragging, with no selection and window clicks allowed, the window under the pointer is highlighted. The dim is cut out over the window's rectangle, and a rounded rectangle (window frame inset 1.25 pt, radius 9) is filled with blue `rgb(0.35, 0.62, 1.0)` at 14 % and stroked with the same blue at 95 %, 2.5 pt wide. The highlight is suppressed while the pointer is over the Full screen pill. In notch mode it appears only after the pointer has moved at least once.

#### 3.4.6 Full-screen pill
- A capsule button labeled "Full screen" (icon: filled inset rectangle; tooltip "Capture the whole screen"). It's at least 32 pt tall, with 14 pt horizontal padding and a material background. Its border is 12 % of the primary color, rising to 34 % and 1.5 pt on hover, and hover scales it to 1.02 (ease-out 0.12 s). Shadow: black 20 %, radius 12, y 5.
- Position: top center of the display, at `max(topChromeHeight, notchControlsHeight) + 12` pt from the top. `topChromeHeight` is the menu bar height or safe-area inset. [Win: the top inset of the work area, for example a top-docked taskbar.]
- It's available only for **Screenshot** (standalone or unified), when not region-only, when scrolling mode is off, and when the source isn't refreshing. It's visible only on the pointer's display, and hidden during a drag or a pending capture.
- Clicking it captures that whole display (same as Return).

#### 3.4.7 Keyboard (while the chooser is up)
Keys are matched by **typed character, or the physical US-layout key position as a fallback**, and are ignored when ⌘, ⌃ or ⌥ is held (except Esc, Return and Space handling).

| Key | Action |
|---|---|
| Esc | Cancel the session. Also caught globally, so it works even if the overlay isn't the key window. |
| Return / keypad Enter | Color mode: pick the color under the pointer (not while dragging). Otherwise, when window clicks are allowed: capture the whole display under the pointer (geometry mode returns a whole-display Region). |
| Space (held during a drag) | Move the selection. Release resumes resizing. |
| R | Repeat the last region: immediately confirm the same rectangle on the display it was taken on, even if the pointer is on another display. It's offered only if that display is still present and the mode isn't Color. |
| 1 / 2 / 3 / 4 | Switch to Screenshot / Recording / Text / Color, if that tool is available in this session. |
| S | Toggle scrolling capture (only in Screenshot mode, image mode, when supported). |
| Z | Toggle the loupe. |
| C | Copy the color under the pointer in the configured format **without ending the session** (loupe on, not dragging). The loupe readout shows "✓ value" for 1.4 s. |
| ← → ↑ ↓ | Nudge the pointer by one device pixel, or 10 with ⇧ (loupe on, not dragging). It may cross onto an adjacent display, and stops at the edge only if no display contains the target. |

While a source refresh is pending (§3.4.10), no confirmation (region, window, full screen, repeat, color) is accepted.

#### 3.4.8 Pixel magnifier (loupe)
- **Turning it on**: Z toggles it. It's forced on in Color mode, and starts on when "Start selection with the magnifier on" is set. Switching tools sets it to on for Color and off for everything else.
- **Initial zoom**: the remembered last zoom if "Remember the magnifier's last zoom" is on, otherwise the default zoom setting (0.5×, 1×, 2× or 4×; default 1×). It's clamped to [0.5, 13/3 ≈ 4.33]. The current zoom is persisted on every change.
- **Pixel source**: frozen mode samples the frozen still. Live mode takes a fresh capture of all displays (excluding the app's windows) every time the loupe turns on and samples that snapshot. It's re-captured on the next toggle, and C copies from the same snapshot that's drawn.
- **Placement**: one block made of a 132 × 132 pt magnifier plus a 6 pt gap plus a 24 pt info bar. Default position is 16 pt to the right of and above the pointer (block bottom edge 16 pt above the pointer). It flips to the left of the pointer if it would cross the right edge (8 pt inset), flips below if it would cross the top, and is clamped to the display with an 8 pt inset. It's drawn only on the pointer's display, and hidden while Space-panning. During a drag it follows the drag point.
- **Content**: a square of source pixels with an **odd** side (so there's a true center pixel), drawn with nearest-neighbor scaling into the frame (radius 9). Side length: `odd(13 / zoom)` with a minimum of 3, which gives 27 px at 0.5×, 13 at 1×, 7 at 2×, and 3 at 4× and above. Near image edges the square slides inward instead of shrinking.
- **Grid**: thin black lines (28 % opacity, 1 pt) between source pixels, drawn when cells are at least 6 pt (sample side ≤ 22).
- **Reticle**: a square ring hugging the target pixel (pixel cell inset by −1 pt), plus four arms from the frame edges to the ring. It's drawn as a 3 pt black line at 76 % with a 1 pt white line at 92 % on top. The frame border is white at 95 %, 1.5 pt.
- **Info bar**: black at 72 %, radius 7, at least the magnifier's width and clamped to the display. Contents: a 12 × 12 swatch of the target pixel (white 85 % border), then `VALUE   x, y` in 10.5 pt semibold monospaced digits. VALUE is the color in the configured copy format and `x, y` are the pixel's coordinates in display pixels. After C it shows `✓ VALUE` for 1.4 s.
- **Wheel zoom** (scroll up = zoom in):
  - *Fast* (default): ×1.15 per event. A plain (non-continuous) mouse-wheel notch applies the factor 3 times. Continuous devices such as trackpads apply it once per packet.
  - *Step by step*: each notch changes the sample side by exactly 2 px (one visible level), so every level is reachable and reversible.
  - Holding ⌥ temporarily swaps fast and stepped.
  - Wheel delta source preference: high-resolution fixed-point delta if non-zero, then line delta, then the pixel scrolling delta (some mice put sub-notch movement only in the fixed-point field).
- **Nudge** step in points = `(⇧ ? 10 : 1) / max(displayScale, 1)`, which is exactly one device pixel.

#### 3.4.9 Last region ("ghost" and R)
- When a region is confirmed (drag or R), the app remembers `(displayID, rect in overlay coordinates)` for the rest of the **app session** (not persisted).
- If "Show the last capture outline" is on (default), the overlay of that display draws the remembered rectangle as a dashed white outline (65 %, 1 pt, dash 5/4) whenever nothing is being dragged or selected.
- R confirms it (see §3.4.7). The hint chip `R` is shown only when this would work.

#### 3.4.10 Switching tools mid-session (source refresh)
Changing the tool (digit key or palette) keeps the overlays on screen:

1. Compute the new policy (§3.4.2). If it doesn't share a source with the current one, block input ("source refresh pending"), then re-photograph all displays (if freeze) in the background, or switch to live. Old pixels stay visible until the new ones arrive.
2. When the new photographs arrive, they must cover **every** display that has an overlay. Otherwise the session ends as failed (it never resumes on stale pixels). The pickable window list is rebuilt. Late results from an older refresh are discarded (generation counter).
3. Scrolling mode resets to off. The loupe is set on for Color and off otherwise. The repeat-region availability is recomputed. Any in-progress drag is cancelled and the hint bar is rebuilt.

#### 3.4.11 Multi-display behavior
- Every display gets its own overlay and its own frozen still (captured at that display's native scale).
- A selection lives on one display. Size badges use that display's scale.
- The hint bar, full-screen pill and loupe appear only on the display under the pointer and follow it as it moves between displays.
- Return captures the display under the pointer. R targets the display the region came from.
- Nudging can cross displays.

#### 3.4.12 Confirmations and outcomes

| Gesture | image mode (Screenshot, Text) | geometry mode (Recording, scrolling) | color mode |
|---|---|---|---|
| Drag region | Frozen: crop the still to the region's pixel rect (view rect mapped to pixels, rounded **outward**, clamped). Live: hide overlays, wait 120 ms, capture the display, crop. | Region (snapped even pixels). If scrolling mode is on, a "scrolling region". | – |
| Click window | Capture that window (§3.5.3), live, even in freeze mode. | Region of the window's frame with its windowID. | – |
| Return / Full screen pill | Frozen: the whole still. Live: hide, wait 120 ms, capture. | Region of the whole display. | Pick color at the pointer. |
| R | Same as drag with the remembered rect. | Same. | – |
| Click / release | – | – | Color at the point. |
| Esc | Cancelled (no message). | Cancelled. | Cancelled. |
| Failure | "The screen could not be captured". | Recorder failure message for recording. | Same failure toast. |

The result goes to the selected tool's service. Screenshot goes to the routing pipeline (§3.7), Text to OCR (§3.16), Color to the color copy (§3.18), Recording to the recorder (out of scope, with the audio toggles), and a scrolling region to scrolling capture (§3.15). A result whose tool's feature was uninstalled in the meantime is dropped. If the set of installed tools changes while a chooser or countdown is up, it's cancelled.

#### 3.4.13 Visual constants (overlay drawing)
- **Dim** (black): frozen 0.22, live 0.18. Notch mode: 0 until a drag starts, then 0.18.
- **Selection cutout**: the dim is drawn over the whole display with an even-odd hole shaped like the selection, as a rounded rect with radius 8.
- **Selection chrome**: an outer rounded rect (selection inset −1.5 pt, radius 9) stroked 3 pt in blue `rgb(0.18, 0.55, 1.0)` at 98 %, with a glow (shadow blur 9, same blue at 55 %). An inner rounded rect (inset −0.5, radius 8) is stroked 1 pt white at 95 %.
- **Size badge**: `W × H` (the "×" multiplication sign) in display pixels (selection pt × display scale, rounded), 11 pt semibold monospaced digits, white, on black 72 % (radius 5, padding 7 × 3 pt). It's centered under the selection at `maxY + 10`, clamped 6 pt from the display edges.
- **Ghost**: dashed white 65 %, 1 pt, dash [5, 4].

#### 3.4.14 Notch variant [Mac-only]
On notched MacBooks with the app's "Dynamic Island" feature, the chooser controls can render inside the notch island instead of the bottom hint bar (setting `notchCaptureControls`, default on, when the notch feature and its "captures" module are enabled). Differences: no initial dim, window highlight only after the pointer moves, the full-screen pill is pushed below the island's current height, and the island owns keyboard focus. Text fields and focused controls in the island keep Return, while Space still moves a drag and Esc always cancels. **Windows recommendation: drop it, and keep the bottom hint bar.**

### 3.5 Pixel acquisition

#### 3.5.1 Display capture
- Captures one display at its native pixel size (`points × scale`, rounded) in **sRGB**, with the pointer included only when the policy says so.
- It excludes the app's own windows according to §3.1. The set of windows to exclude is resolved against the same window snapshot the capture uses, so stale IDs can't hide another app's window. (Protected IDs are intersected with the app's real windows.)
- "All displays" captures each display in turn. A display that fails is simply missing from the result.

#### 3.5.2 Region capture (used by scrolling)
A capture configuration is prepared **once** for a display and pixel rect: the source rect is in points, the output size equals the pixel rect, the pointer flag is set, and windows are excluded per policy. Re-running it returns new frames cheaply. The pixel rect is clamped to the display and made integral, and an empty rect fails.

#### 3.5.3 Window capture
Goal: the window's **own** crisp pixels (no neighbors bleeding in, rounded corners transparent), even when it's partially covered.

1. **Attached sheets and dialogs**: from the on-screen window list (front to back, ordinary windows, opacity > 0.01), find windows of the **same application** that are **in front of** the clicked window and lie **entirely within** its frame. Those are sheets, alerts and modal dialogs. If there are any:
   - Optionally narrow them using the accessibility tree. Candidates the accessibility tree positively identifies as standard or full-screen windows are removed, and unknown candidates are kept. If the app gives no window map that includes the target, the geometric plan stands. If no candidate survives, there's no composite.
   - **Composite A** (one display): capture the display with **only** the clicked window and its attachments included, then crop to the clicked window's frame. (The crop picks between the window's on-screen placement and a "packed in the top-left" placement by comparing alpha coverage, to work around an OS quirk.) This needs the frame to intersect exactly one display.
   - **Composite B** (fallback, spanning displays): capture each window's own buffer, require every layer to cover its frame **whole** at one common scale (±1 px), recapture clipped layers through an independent-window capture, and draw them back to front on a canvas the size of the clicked window. The scale is that of the clicked window's buffer, or the finest display scale if it must be recaptured. The reported capture scale is the scale actually used, which can differ from the clicked display's.
2. **Single window**: capture the window's own buffer through the window server (best resolution, ignoring the global clip). If the buffer's aspect ratio matches the window's frame within 8 %, use it. Otherwise (a window running off-screen gets a clipped buffer) capture it through the independent-window route at `frame × scale` with best resolution and no cursor, falling back to the clipped buffer if that fails.
3. `anchorRect` is the window's frame. The result reports its own scale.

#### 3.5.4 Live-mode hide
In live mode (freeze off) every region, repeat or full-display confirmation orders all overlays out, waits **120 ms**, captures the whole display, then crops it, so the overlay never appears in the picture. Window clicks don't need this, because the window's own buffer never contains the overlays.

### 3.6 Direct "Capture the whole screen" shortcut
- It goes through the standalone gating (§3.3), including the delay countdown.
- It closes any open quick preview, picks the display containing the pointer (or the main display), and captures it with the include-pointer and hide-windows settings. There's no overlay and no chooser.
- The result is routed like any screenshot with `anchorRect` = that display's frame. A failure shows "The screen could not be captured".

### 3.7 After-capture routing (every finished screenshot)

Every screenshot, whether area, window, display, direct full screen or scrolling, goes through these steps in order:

1. **Becomes the "latest capture"**. A pending "upload latest" of the previous capture loses its claim (its link is revoked when it arrives), and the "withheld" marker is cleared. If "Edit latest screenshot" or "Upload latest screenshot" is enabled, the capture is written in the background to the latest-capture store (§5).
2. [Mac-only] The notch mascot plays a "flash" reaction. (No shutter sound plays anywhere. Failures use the system beep.)
3. Any open quick preview closes.
4. The capture is **recorded in Recent captures** (full PNG plus thumbnail, in the background). Only screenshots are recorded here. OCR and color picks aren't. Recordings are added by the recorder.
5. If **Copy to the clipboard automatically** is on: render (raw pixels plus optional 1x downscale) and copy in the background (§3.8.3). The copy is **discarded** if the clipboard changed in the meantime (for example, the default action already copied), if a newer capture superseded it, or if the feature was turned off. Success is silent. A failure beeps.
6. **Default action** (`screenshotDefaultAction`):
   - **Edit**: open the editor (§3.10) and stop here. No preview, and no automatic shelf (the editor's Add to Shelf takes the finished picture).
   - **Ask each time** (empty value): no automatic action.
   - **Save**: save directly (§3.8.1). The toast reads "Saved to <folder name>".
   - **Save & Copy**: save, then copy the saved file. The toast reads "Saved to <folder> and copied", or "Saved to <folder>" if the clipboard write failed. The copy result is reported honestly, so the Copy button stays enabled if it failed.
   - **Copy**: copy (§3.8.3). The toast reads "Screenshot copied".
7. **Auto shelf**: if "Add to the shelf automatically" is on, and the Shelf feature is installed and switched on, add a PNG copy to the shelf. If the capture was saved, the saved file's bytes and name are reused (nothing is encoded twice). Otherwise it's encoded in the background with the default name. The shelf gets its own copy, not the saved file, which could later be moved or deleted. Each capture owns its task, so a fast second capture doesn't cancel the first. Discarding a capture from its preview removes its shelf item, or cancels its pending encode, but only for that capture. A shelf refusal or an encoding failure beeps.
8. **Preview decision**:

| Default action | Result | Preview |
|---|---|---|
| Ask each time | – | Shown, auto-dismiss after **12 s** (the "recovery" timer). |
| Edit | – | Not shown (the editor opens). |
| Save / Save & Copy / Copy | Fully succeeded | If "Show confirmation preview" (default on): shown for the confirmation duration (1, 2, **3** default, 5 or 10 s, or "Until dismissed" = no timer). If off: nothing. |
| Save / Save & Copy / Copy | Failed or partial | Shown with the 12 s recovery timer, so the failed half can be retried. |

A stored duration that isn't a number counts as the default 3 s, not "until dismissed".

**Direct outputs are raw.** Save, copy, shelf and share started from the routing pipeline or the quick preview use the capture's **raw pixels**: no annotations, backdrop, corner rounding or watermark. Only the "Save at 1x size" downscale applies. Backdrops and watermarks apply only to exports from the editor.

### 3.8 Output operations

#### 3.8.1 Save (direct and from the editor)
1. **Folder**: the configured folder (`screenshotSaveFolder`, with `~` expanded) if it exists and is a directory. Otherwise the user's Desktop, and the home folder as a last resort.
2. **Subfolder pattern** (optional): expand the date tokens (§6.20), split on "/", and drop empty, "." and ".." components so the path can't escape the base folder. Create the subfolder tree. If creation fails, save in the base folder rather than lose the capture.
3. **File name**:
   - Empty pattern: `Screenshot yyyy-MM-dd at HH.mm.ss.png` (localized prefix, POSIX locale, 24-hour clock, dots instead of colons), for example `Screenshot 2026-10-09 at 14.05.33.png`.
   - Pattern set (trimmed): expand the date tokens and `%#` runs (zero-padded number), replace "/" and ":" with "-", and append `.png`. If the pattern contains `%#`, read `screenshotFileNumberNext`, use it, and persist `next + 1`.
   - **Unique name**: if the name exists, try "name 2.png", "name 3.png" and so on up to 9999.
4. Write the PNG atomically (§6.15 for encoding). On macOS, the file is also tagged as a screen capture in extended attributes, which lets Spotlight and the app's Cleaner treat it like a system screenshot. [Win: drop.]
5. **Failure**: beep. If a `%#` number was consumed, give it back, but only if nothing else advanced the sequence since (that is, only if `next == consumed + 1`).
6. Toast: "Saved to <last path component of the containing folder>".

#### 3.8.2 Save As… (editor and pins)
A native save dialog, PNG only, with the default dated name as the suggestion. It writes the current export. In the editor it's attached to the editor window and closes the editor on success. A failure beeps. There's no toast and no screen-capture tag.

#### 3.8.3 Copy
1. Render the export and encode it as PNG.
2. Write it to the private cache folder `…/Copied Screenshots/<default dated name>.png` (unique name, owner-only permissions, folder refuses to be a symlink, max 256 MB per file). This lets paste targets that read a file URL later still find the file.
3. Put **one clipboard item** with: the file URL, the PNG bytes, and TIFF bytes (whose resolution encodes the point size, so a paste lands at on-screen size). Mark the item with the app's "source" type (the bundle ID), which the clipboard-history feature uses to recognize copied screenshots and store them as images rather than files.
4. After a successful copy, **prune** the copied-files folder: delete files older than 24 h, then keep the newest files within 100 files and 256 MB in total. The just-published file is never deleted. A stale render finishing late can't evict the published file.
5. Failure (render, write or clipboard): beep. The file is removed if the clipboard write failed.

#### 3.8.4 Drag out
- The quick-preview thumbnail and the editor's drag handle are drag sources. When a drag starts, a full-resolution PNG is written to `<temp>/ScreenshotDrag-<UUID>/<default dated name>.png`. Each drag has its own folder, so two drags in the same second don't collide. It's offered as a file to the drop target.
- Each such folder is deleted **1 hour** later. Leftover `ScreenshotDrag-<UUID>` folders are deleted at app launch (only folders matching that exact pattern, never through symlinks).
- From the preview the drag uses the raw capture at full resolution. The preview drag **doesn't** apply the 1x downscale. From the editor it uses the editor export (with the downscale preference).

#### 3.8.5 System share sheet
The quick preview's share button and the editor's share button write a temporary export file the same way as a drag (1-hour cleanup) and open the OS share picker anchored to the button. The preview's file is the raw capture **with** the 1x downscale preference. The editor's file is the full editor export. In the preview, choosing a target closes the preview, and cancelling resumes the dismissal timer. In the editor, choosing a target marks the edit as exported. A failure to write the file beeps.

#### 3.8.6 Add to Shelf (editor)
The Save menu offers "Add to Shelf" only while the Shelf feature is installed and enabled. It hands the PNG, under the default dated name, to the Shelf, shows the toast "Shelf" (tray icon) and closes the editor. A refusal beeps.

#### 3.8.7 Pin
The preview pins the **raw** capture at full resolution. The editor pins the current export **without the backdrop**, but with annotations, watermark and corner rounding (rounded corners become transparent) and the 1x preference. Pinning from the editor keeps the editor open and marks the edit as exported. See §3.11.

### 3.9 Quick preview (floating thumbnail)

#### 3.9.1 Window
- A borderless, non-activating, transparent window with a shadow, at status-bar level (above ordinary and floating windows), shown on all desktops and excluded from window cycling. It doesn't activate the app.
- Size: **350 × 210 pt**, or **350 × 268 pt** while a shared-link row is shown (animated resize). The background is a material rounded rect (radius 16) with a hairline border at 10 %. Padding is 10 pt and the stack spacing is 10.
- **Focus**: if the presentation is *timed* and "Focus the preview automatically" is on (default), the preview becomes the key window on appear, so ⌘C and ⌘S work immediately. A persistent ("until dismissed") preview never takes focus. Any mouse-down on the preview makes it key (button clicks still work). Hovering never takes focus.

#### 3.9.2 Placement
- **Display**: the one containing the largest part of `anchorRect`. In a tie, the display holding the pointer wins. If none qualifies (the display was disconnected, or the anchor is zero), it falls back to the pointer's display. It uses that display's *visible frame* (excluding the menu bar or taskbar and Dock).
- **Position setting**: Automatic (default), Top left, Top right, Bottom left, Bottom right.
  - Fixed corners: 16 pt inset from the visible frame's corner.
  - **Automatic when the default action isn't "Ask each time"**: treated as Bottom right. The preview is just a confirmation, so it sits quietly in the corner.
  - **Automatic with "Ask each time"**: beside the capture (§6.4). To the right of the area with a 14 pt gap, else to the left, else at the pointer. Vertically centered on the area, else below it, else above it, else centered on the pointer. Then clamped inside the visible frame with a 10 pt inset.

#### 3.9.3 Content (top to bottom)
1. **Thumbnail button**: the capture scaled to fit 320 × 138 pt (high-quality downsample to at most 1200 px on the long side), over black 12 % with radius 9 and a hairline border. Click = **Edit**. Drag = drag-out (§3.8.4).
   - Top-left overlay: a circular "×" button (24 pt, material). It's **shown only for persistent previews** ("until dismissed"). It dismisses the preview (tooltip "Done (⎋)").
   - Top-right overlay: two circular 24 pt buttons, **Share** (system share sheet) and **Pin** ("Pin to screen").
2. **Shared link row** (only after a link was created and couldn't be copied, see §3.14.5): link icon, URL (middle-truncated, selectable), "Expires in X" (relative time), **Copy link** button, **Delete now** button (spinner while deleting). Height 48, radius 9, background at 5.5 %.
3. **Action bar** (left to right): **Trash** (Discard, ⌫), **QR** (only once a code was found, appearing with a spring animation), **Save** (⌘S), **Copy** (⌘C), **Link** (only if temporary links are allowed and no link exists yet: a split button whose main click creates a link with the default expiry and whose menu offers "For 1 hour", "For 6 hours" and "For 24 hours", with a spinner while uploading), a spacer, then **Edit** (prominent, ⏎).
   - Save and Copy are **disabled at 40 % opacity** if the automatic action already did them. Their keyboard shortcuts respect that too, so work is never done twice.

#### 3.9.4 Behavior
- **Edit**: close the preview, then on the next event-loop turn open the editor with the raw capture (exactly once).
- **Save / Copy / Pin**: run (§3.8). Success closes the preview. Failure keeps it open and restarts the timer.
- **Discard**: if the capture was saved (by the default action or the preview's Save), move that file to the **Trash** (not a permanent delete) and give back its `%#` number if possible. Remove its auto-shelf item or cancel its pending encode. Mark the latest capture as "withheld" so "Upload latest" will never publish it, persisted across launches. Then close. Discarding a preview reopened from Recent captures affects nothing else.
- **QR**: close the preview and open the QR result panel (§3.17). The QR scan runs once, in the background, on the full-resolution capture, and is silent when nothing is found.
- **Link**: see §3.14.5.
- **Auto-dismiss**: a timer of the presentation's duration (none when persistent) starts on appear. Hovering the preview cancels it, and leaving restarts the full duration. The timer is also paused while the share sheet is open, while a link is being created and while a link is being deleted. After a link row is shown, the duration becomes **30 s** (still none if persistent). After the link is deleted, it returns to the base duration.
- **Keyboard** (when the preview is the key window and no text field or sheet is active):

| Key | Action |
|---|---|
| ⌘W (by typed letter, or physical key for non-Latin layouts; no other modifier) | Close |
| ⌘C | Copy |
| ⌘S | Save |
| ⌘⌫ / ⌘⌦ | Discard |
| Return, keypad Enter, E | Edit |
| ⌫ / ⌦ | Discard |
| Esc | **Dismiss only**. Never discards, because a discard can delete a file the toast just announced as saved. |

#### 3.9.5 Reopened previews
"Restore" in Recent captures (§3.12) reopens the same preview with no default action, no completed actions and the 12 s timer, without repeating any automatic copy or save. Its Discard doesn't trash anything, doesn't touch the shelf and doesn't withhold the latest capture.

#### 3.9.6 Notch-embedded preview [Mac-only]
With the notch feature routing capture events, the preview is embedded in the notch island instead, with an icon-only action bar (Trash, QR, Save, Copy, Share, Link menu, Pin, Edit). **Windows: drop.**

### 3.10 Screenshot editor

#### 3.10.1 Window
- One editor window **per capture**. Several can be open at once. While any editor is open, the app becomes a regular foreground app (Dock icon and app switcher entry). It goes back to accessory mode when the last one closes. [Win: give the editor a taskbar button.]
- Title "Screenshot", hidden. A transparent title bar with full-size content. Closable, minimizable, resizable. **Forced dark appearance.** Dragging the content doesn't move the window, only the title strip does. It follows the user to the active desktop.
- **Initial size** (content area, for the display under the pointer, visible frame size `V`; see §6.5): fits the image plus chrome (96 × 140 pt), between a minimum of roughly 76 % × 72 % of `V` (bounded 760–980 × 560–680 pt and never larger than `V`) and a maximum of 90 % × 88 % of `V`. The window is centered, activates the app, and becomes key.
- The minimum content size is enforced at the computed minimum.
- After the window appears (next event-loop turn): start **text recognition** (§3.10.8) and the **QR scan** (§3.10.9) in the background.

#### 3.10.2 Layout
The whole window is one dark stage: fill `white 0.115` (≈ #1D1D1D) with a subtle top highlight (white 3.5 % → clear, top to bottom). Over it:

- **Top band** (38 pt tall, 10 pt from the top, 12 pt horizontal padding): the app's brand mark centered (40 pt wide, light gray), and the **action cluster** on the right.
- **Middle row**: the **tool rail** (vertical, scrollable when short, 10 pt from the left) and the **canvas area**, which fills the rest.
- **Bottom row** (12 pt horizontal padding, 6 pt top, 10 pt bottom): **info chip** on the left, **contextual style bar** in the center, **zoom chip** on the right. When the window is too narrow, the style bar goes on its own row above the two chips.

**Action cluster** (material, radius 12, padding 9 × 6, items separated by thin dividers), left to right:
1. **Recent captures** (clock icon) opens the history palette.
2. **Discard** (trash) closes the editor **without** confirmation.
3. **Undo**, **Redo** (disabled when unavailable).
4. **QR** (only when a code was detected; scale and fade transition) opens the QR result panel. The editor stays open.
5. **Pin** ("Pin to screen (⌘P)").
6. **Share** opens the system share sheet.
7. **Link** menu (only when temporary links are allowed): "For 1 hour", "For 6 hours", "For 24 hours". It shows a spinner while uploading (§3.14.6).
8. **Save** split button. The main click saves. The menu offers Save, Save As…, and Add to Shelf (only when the Shelf is installed and on). Tooltip "⌘S".
9. **Copy** (prominent button, tooltip "⏎").

Every action first commits any text being edited.

**Tool rail** (material, radius 15, padding 5, shadow): one 33 × 29 pt button per tool, in the configured order (§3.10.15). The active tool is accent-tinted at 22 % with an accent icon, and plays a bounce animation when selected. Hover shows a primary 8 % wash and scales to 1.06. A small badge in the top-right corner shows the tool's shortcut (custom key or position digit 1–9), at 0.9 opacity when hovered or active and 0.55 otherwise. Tooltip: "Tool name  (key)". Below a divider, a **gear** button opens the **Editor tools** popover (order and shortcuts, §3.10.15).

Tool icons and titles:

| Tool id | Title | Icon idea |
|---|---|---|
| select | Select | cursor arrow |
| arrow | Arrow | arrow up-right |
| pixelate | **Blur** | particles/mosaic |
| crop | Crop | crop |
| text | Text | "Aa" text format |
| sticker | Sticker | smiling face |
| rect | Rectangle | rectangle |
| highlight | Highlighter | highlighter |
| freehand | Pen | scribble |
| line | Line | diagonal line |
| ellipse | Ellipse | circle |
| counter | Number | "1" in a circle |
| redact | Solid block | filled rectangle |

**Info chip** (material capsule): the **drag-out handle** (icon "document with up arrow", label "Drag and drop") followed by `W × H px` and, when scale > 1, `  @Nx` (integer scale). [Win: show fractional scales, for example "@1.5x".] Dragging the handle exports the full editor result as a PNG file (§3.8.4) and marks the edit as exported.

**Zoom chip** (material, radius 9): **Fit** button (active when in fit mode, ⌘0), a percentage label (`zoom × scale × 100 %`), and a **1:1** button (active at actual size, ⌘1). Tooltip "⌃ scroll · ⌘+ ⌘-".

#### 3.10.3 Canvas, zoom and scrolling
- The canvas shows the **content** = image plus backdrop padding on each side (§6.13). It sits in a 2-D scroll region with a 14 pt margin and is centered when smaller than the region.
- **Zoom** = view points per image pixel. Fit mode (default): `min(fitScale, 1)` where `fitScale = min((W − 28) / contentW, (H − 28) / contentH)` (each available dimension at least 80). Small captures grow up to 1 view point per image pixel (200 % for a 2× capture). Exported pixels are never affected.
- Zoom range 0.05 … 3. ⌘+ multiplies by 1.25, ⌘− by 0.8, ⌘0 returns to fit, and ⌘1 gives "actual size" (`1 / scale`, a 100 % label). ⌃ + vertical scroll multiplies by `1 + clamp(deltaY, −24, 24) × 0.014`. A trackpad pinch multiplies the zoom that was current at the start of the gesture. Pinch and scroll start from the zoom actually on screen, even in fit mode.
- During a pointer drag, the canvas keeps the layout it had when the drag started, so a selection change that alters the bottom bar can't move the canvas under a still cursor.
- **Card presentation**: the canvas is clipped to a rounded rect. The radius is 6 pt when a backdrop is shown, otherwise `max(4, cornerRadiusPx × zoom)` (live preview of the Corners slider). It has a hairline border at 14 %, two shadows (black 30 % radius 24 y 10, and black 18 % radius 3 y 1), and an appear animation (scale 0.965 → 1 with opacity 0 → 1, spring).
- **Live backdrop**: when shown, the backdrop fill is drawn behind, scaled by `1 + blur × 0.08` and blurred by `backdropBlurRadius × zoom`. Then the image is drawn inside the card with the card's corner radius and a card shadow (offset 5 pt down, blur 18, black 38 %, drawn only outside the card). Annotations are clipped to the image area (they never spill onto the margin, matching export).
- When any blur area exists, image and annotations are drawn in an isolated layer, so "replace" blending can't punch through to the backdrop or shadow.
- **Cursor**: an open hand over the selected annotation while a creation tool is active, a crosshair for creation tools, an I-beam over recognized words with the Select tool, and the arrow otherwise.

#### 3.10.4 Tools

Common rules:
- A **tap** is a press whose end point is within **7 view points** of its start, measured in view points so it doesn't depend on zoom. For the **Pen** only, a tap means the pointer *never* left that radius, so a closed loop returning to its start still draws.
- With a creation tool active, pressing **on the currently selected annotation** (its body, a resize handle, or an endpoint within `12*scale` px) edits that annotation (move or resize) instead of creating a new one. Pressing elsewhere deselects and creates.
- A **tap with a creation tool on an existing annotation** selects it **and switches to the Select tool**. Text enters inline editing. For area shapes (rect, ellipse, highlight, blur, redact), only a ring near the edge counts for this tap test, so their interior stays free for placing a new text, sticker or counter.
- Shapes created by dragging become **selected when the drag ends**. A tap with a drag tool leaves nothing behind: the draft is removed together with its undo entry, and whatever is under the cursor gets selected.
- Taps for Text, Sticker and Counter outside the image bounds are ignored.
- Every new annotation takes the current color, thickness, text size, arrow style, blur style, level and text-only values.

| Tool | Create gesture | Geometry stored | Rendering (in image pixels, `w = thickness × scale`) |
|---|---|---|---|
| **Select** | – | – | Selects, moves, resizes (§3.10.6). Drag over recognized words selects text (§3.10.8). |
| **Arrow** | Drag tail → tip | two points | Style dependent (§6.6). Color, thickness, arrow style. |
| **Line** | Drag | two points | Straight stroke, width `w`, round caps. |
| **Rectangle** | Drag (no modifier constraints) | rect | Stroked rect, width `w`, round joins and caps. |
| **Ellipse** | Drag | rect | Stroked ellipse inscribed in the rect. |
| **Pen** | Drag (every pointer sample appended) | polyline | Smoothed with quadratic curves through midpoints (§6.7), width `w`, round joins and caps. |
| **Highlighter** | Drag | rect | Rect filled with the color at **42 % alpha** in **multiply** blend mode. |
| **Solid block** (redact) | Drag | rect | Opaque fill in the color. |
| **Blur** (pixelate) | Drag | rect plus style, level, text-only | Replaces the pixels in the area (§3.10.4.1). |
| **Text** | Tap | text box rect at the tap point (top-left) | System font, **semibold**, size `textSize × scale` px, drawn 2 px right of the box origin. Enters inline editing immediately. |
| **Sticker** | Tap | square centered at the tap, kept inside the image | Emoji glyph sized `max(10*scale, min(w, h) × 0.82)`, centered (§6.9). |
| **Number** (counter) | Tap | zero-size rect at the tap point (center) | A filled circle in the color, `d = max(22, shortSide / 24)` px. White ring at 90 %, width `max(1.5, d × 0.05)`, inset 1. Bold number, font size `d × 0.52`, white (near-black `rgb(0.09, 0.09, 0.11)` on white circles). Numbers are always 1…n in **drawing order**. Creating, deleting or reordering renumbers them all, so there are never gaps. |
| **Crop** | Drag handles, move, or draw a new rect | crop draft | §3.10.12 |

**Optional annotation shadow** ("Shadows" toggle in the style bar, default off, remembered): rectangles, ellipses, lines, arrows, pens, counters and the watermark get a shadow 1 × scale px down, blur `3 × scale`, black 38 %. Text uses black 55 %, blur `2.5 × scale`, 1 × scale down. Stickers use black 45 %, blur `3 × scale`, 1 × scale down. Highlighter, Solid block and Blur have no shadow. A stroked arrow casts one shadow for the whole path, so the head never shades its own shaft.

##### 3.10.4.1 Blur tool (styles, strength, text only)
- **Style** (menu "Blur style"): **Pixelate** (default), **Blur** (soft Gaussian), **Erase** (fills the area from its surroundings).
- **Strength** (slider "Blur strength", 5 steps; hidden for Erase): level 1 (lightest) … 5 (heaviest), default 3. The slider has low and high icons on each side and is 84 pt wide. New editors start at `max(remembered level, 3)`, so a capture never *starts* at a light level that could leave text readable.
- **Text only** (toggle button with icon and label): when on, the area covers only the **recognized text runs** that intersect it (§6.12), each clipped to the area, and leaves the rest of the picture untouched. While recognition hasn't finished (or a recognition band failed), the **whole area** is covered, so an early export never leaks text.
- Rendering rules (§6.10, §6.11):
  - Pixelate draws a **noisy low-resolution mosaic of the whole base image** with nearest-neighbor scaling, clipped to the area, in **replace** mode (no alpha blending, so translucent captures can't show glyphs through).
  - Blur draws a small, noised, Gaussian-blurred sample stretched smoothly, in replace mode.
  - Erase draws a smooth patch interpolated from medians of the pixels just outside the area's edges. Other text runs are skipped while sampling, and the result is cached.
  - Samples are built from the **base image only**, so annotations drawn earlier under a blur area are covered too.
  - One sample is kept per (style, level) in use. Samples are rebuilt when the base image changes (crop, undo).

##### 3.10.4.2 Arrow styles (menu "Arrow style")
Each menu row shows a rendered sample produced by the real renderer (36 × 16 pt template image). The styles are **Solid** (filled silhouette, default), **Outline** (shaft plus closed triangular head outline), **Open** (shaft plus chevron head), **Double-ended** (chevron heads at both ends) and **Scribbly** (hand-drawn wobble, seeded per arrow, so redraws and exports are stable and each new scribbly arrow differs). Geometry is in §6.6.

##### 3.10.4.3 Stickers (menu "Sticker")
12 emoji: ✅ check (default), ❌ cross, ⭐️ star, ❤️ heart, 👍 thumbs up, 👎 thumbs down, 😀 smile, 😂 laugh, 🎉 party, 🔥 fire, ⚠️ warning, 👀 eyes. The menu shows each glyph, with a checkmark on the current one. The label shows the current glyph and "Sticker". Picking a sticker while a sticker annotation is selected changes that annotation (undoable).

#### 3.10.5 Style bar (contextual, bottom center)
A material capsule (padding 12 × 8) whose groups depend on the active tool, or on the selected annotation when the Select tool is active:

| Group | Shown when |
|---|---|
| Arrow style menu | Arrow tool, or a selected arrow |
| Sticker menu | Sticker tool, or a selected sticker |
| Blur style menu, Text only, Strength slider (not for Erase) | Blur tool, or a selected blur area |
| 8 color dots | Arrow, Line, Rectangle, Ellipse, Pen, Highlighter, Text, Number, Solid block tools, or Select with a selected annotation that isn't a sticker or blur area |
| Text size control (instead of thickness) | Text tool, or a selected text |
| 3 thickness glyphs | When color dots are shown and the text size control isn't. Note: this also shows for Highlighter, Number and Solid block, where thickness has no visual effect (port quirk; may be hidden). |
| Send backward, Bring forward | Whenever more than one annotation exists (excluding a shape still being drawn). Each is disabled at its end of the order or when nothing is selected. |
| Shadows toggle | Always |
| Background button | Always (opens the backdrop popover, §3.10.10) |
| Watermark button | Always (opens the watermark popover, §3.10.11). Accent-tinted while a watermark is actually drawn. |

When the Crop tool has a draft, the style bar is replaced by the **crop bar**: Cancel and **Crop** (prominent, ⏎).

**Colors** (sRGB, shared by on-screen rendering and export):

| id | Components (r, g, b) | ≈ Hex |
|---|---|---|
| red (default) | 0.93, 0.26, 0.21 | #ED4236 |
| orange | 1.00, 0.58, 0.00 | #FF9400 |
| yellow | 1.00, 0.80, 0.00 | #FFCC00 |
| green | 0.20, 0.78, 0.35 | #33C759 |
| blue | 0.04, 0.52, 1.00 | #0A85FF |
| purple | 0.69, 0.32, 0.87 | #B052DE |
| black | 0.09, 0.09, 0.11 | #17171C |
| white | 1.00, 1.00, 1.00 | #FFFFFF |

Color dots are 17 pt circles. The selected one gets a 23 pt ring at 90 % primary and scales to 1.05 (spring).

**Thickness** ("Thickness"): small = 2, **medium = 4** (default), large = 7 points at 1x (× scale in pixels). The glyphs are capsules 15 pt wide and 1.8, 3.4 and 5.4 pt tall. The selected one is accent-colored with an accent 18 % background.

**Text size** ("Font size"): presets **10, 12, 14, 16, 19, 24, 28, 36, 48, 64, 72, 96** pt at 1x. Default **19**. A "smaller" button and a "larger" button step through the presets (each disabled at its end), and a menu labeled "N pt" jumps to any preset. A stored value of 0 means 19, and anything outside the range is clamped to [10, 96].

**Applying styles to a selection**: changing color, thickness, text size or arrow style while an annotation is selected changes that annotation as one undoable step, but only for attributes that annotation uses, so picking a highlight never records a thickness edit. Changing to Scribbly gives the arrow a new random seed. Changing text size re-measures the text box. Changing blur style, level or text-only on a selected blur area ensures the needed sample exists first, and if it can't be built, the controls snap back. **Selecting** an annotation syncs the controls to the annotation's values, only for the attributes it uses (the rest stay as defaults for the next new mark).

All of these choices are **remembered** across editors and launches: last tool (Select and Crop are never restored; the fallback is Arrow), color, thickness, text size, blur level (raised to at least 3 on open), blur style, text-only, arrow style, sticker, shadows.

#### 3.10.6 Selection, move, resize, layers, delete
- **Select tool, press**:
  1. If an annotation is selected and the press is on one of its **resize handles** (tolerance `12*scale` px; for rect-like shapes and stickers), start resizing.
  2. If the selected annotation is an arrow or line and the press is within `12*scale` px of an **endpoint**, drag that endpoint.
  3. Otherwise **hit test** (§6.8), topmost first. A hit selects the annotation (syncing controls) and prepares a move. A miss over a **recognized word** starts a text selection. A miss elsewhere deselects.
- **Drag**: the first movement records one undo step. A move translates the rect or all points by the drag delta (pens move as a whole). A resize uses the 8 handles (corners and edge midpoints). Dragging past the opposite edge flips the rect instead of producing a negative size. Endpoints move individually. There's no snapping, no aspect lock and no modifier constraints in the editor.
- **Release with a tap** (Select tool, nothing dragged): selects what's under the point. A text annotation enters inline editing directly. On a word, it selects that word. On empty space, it clears the text selection.
- **Selection chrome** (drawn on the canvas in image space, blue `rgb(0.04, 0.52, 1.0)` at 90 %, 1.5 × scale px, dashed 4/3 × scale):
  - Arrow and line: white 8 × scale px circles at both endpoints (stroked blue). No box.
  - Counter: a dashed box around its circle, inset −3.
  - Everything else: a dashed box around the rect, inset −3 × scale. For resizable kinds (rect, ellipse, highlight, blur, redact, sticker) there are 8 white handle dots (7 × scale px) with blue strokes.
  - Pen strokes and text: dashed box only (move-only). **Mac quirk**: a pen annotation never gets a `rect` (it stores only points), so its dashed box is drawn around `(0,0)` (a tiny square at the image's top-left) instead of around the stroke. The port should draw the points' bounding box.
- **Delete / Backspace** deletes the selected annotation (undoable) and renumbers counters.
- **Layers**: Bring forward and Send backward swap the annotation with its neighbor in drawing order (one step each, undoable, counters renumbered).
- **Esc** clears the selection (see §3.10.14 for the full Esc chain).

#### 3.10.7 Inline text editing
- A single-line plain text field overlays the annotation at its position: placeholder "Text", semibold system font at `max(11, textSize × scale × zoom)` view points in the annotation's color, minimum width 130, padding 6 × 3, background black 35 %, radius 6, accent border at 70 %. It gets focus automatically (after a 0.05 s delay). While editing, the canvas doesn't draw that annotation, so only the live field is visible.
- **Commit** happens on Return, Esc, any canvas press, any toolbar action and any tool change:
  - Text is trimmed of whitespace.
  - A **new** text that ends up empty is removed together with all the undo entries it created (as if it never existed).
  - Unchanged text: no-op.
  - Editing an existing text records one undo step. An existing text emptied to nothing is deleted.
  - The box is re-measured (`textBounds`, §6.9).
- While a text field has focus, **every key belongs to it**. The editor's shortcuts are suspended.

#### 3.10.8 Recognized text on the canvas
- When an editor opens, and again after each crop, undo or redo that changes the base image, the base image is OCR'd **in horizontal bands** (§6.12) with the accurate recognizer, language correction and automatic language detection.
- Results are a list of **words** (text, bounding box in image pixels, line index) and **text runs** (padded rectangles used by Text-only blur), or `nil` runs if any band failed or recognition hasn't finished.
- With the **Select tool**, hovering a word shows an I-beam. Pressing on a word and dragging selects every word whose box intersects the drag rectangle (grown by 1 px, so a hairline drag still selects). A tap selects one word. Selected words are highlighted with blue `rgb(0.04, 0.52, 1.0)` at 32 % in rounded rects (box inset −1.5 × scale, radius 2 × scale).
- **⌘C with words selected** copies them as plain text (spaces between words on a line, newlines between lines, in reading order) and shows "Text copied". **The editor stays open.** Esc clears the word selection. Switching away from Select clears it too.
- After a crop, words and runs are shifted into the cropped image's coordinates, and those that fall outside are dropped. The cropped image's runs **carry over** and are merged with its fresh recognition, since recognition can miss a line the crop cut through. Carried runs survive undo and redo back to that image.

#### 3.10.9 QR in the editor
After the window appears, and after every base-image change, the base image is scanned for 2-D codes (§3.17). If one is found, the **QR** button appears in the action cluster (spring animation). Clicking it opens the QR result panel over the editor. A cropped-out code stops being offered.

#### 3.10.10 Background (backdrop) popover
A popover 292 pt wide, opened from "Background" in the style bar. Everything applies **live** and is **remembered** for the next capture.

1. **Swatch grid** (4 columns, 38 pt tall tiles, radius 8; the selected one has a 2.5 pt accent border; matching ignores slider values):
   - **None** (slash icon).
   - **Presets** (diagonal 2-stop gradients, top-left → bottom-right):

     | id | Start | End |
     |---|---|---|
     | ocean | 0.20, 0.47, 0.96 (#3378F5) | 0.45, 0.83, 0.98 (#73D4FA) |
     | sunset | 0.99, 0.36, 0.42 (#FC5C6B) | 1.00, 0.75, 0.35 (#FFBF59) |
     | forest | 0.07, 0.56, 0.43 (#128F6E) | 0.62, 0.87, 0.50 (#9EDE80) |
     | candy | 0.66, 0.32, 0.95 (#A852F2) | 0.99, 0.56, 0.65 (#FC8FA6) |
     | graphite | 0.23, 0.25, 0.31 (#3B404F) | 0.55, 0.60, 0.70 (#8C99B3) |
   - **Saved custom backgrounds** (up to 12): right-click → "Remove".
   - **Wallpapers**: the current desktop picture of every display (deduplicated, only existing local files), with a small badge.
   - **Image…** (dashed tile): opens a file dialog for images and applies that image.
2. **Custom section**: a segmented control **Solid | Gradient**, one or two **color wells** (the active well has an accent ring; clicking a well makes it active), and a **"+" Save background** button. That button is enabled for solid, gradient and image looks, and is ignored if an identical look is already saved. A **20-color palette** (10 columns, 17 pt dots) paints the active well and applies immediately. Switching Solid/Gradient also applies. Wells start from the current custom look, or from the defaults (solid = gradient start = (0.20, 0.47, 0.96), end = (0.45, 0.83, 0.98)).
   - Palette (r, g, b): (0.96, 0.26, 0.21), (1, 0.58, 0), (1, 0.8, 0), (0.55, 0.86, 0.25), (0.2, 0.78, 0.35), (0.1, 0.74, 0.61), (0.15, 0.78, 0.85), (0.04, 0.52, 1), (0.35, 0.34, 0.84), (0.69, 0.32, 0.87), (1, 0.45, 0.66), (0.91, 0.12, 0.39), (0.55, 0.39, 0.29), (0.11, 0.16, 0.32), (0.05, 0.05, 0.06), (0.25, 0.25, 0.28), (0.55, 0.55, 0.58), (0.85, 0.85, 0.87), (1, 1, 1), (0.99, 0.93, 0.85).
3. **Sliders** (0 … 1, labels in a 64 pt column):
   - **Margin** (padding, default 0.5; disabled when there's no backdrop).
   - **Corners** (corner radius, default 0, always enabled; also rounds a capture with no backdrop into transparent corners).
   - **Blur** (backdrop blur, default 0; disabled when there's no backdrop).
- Applying any look **keeps the current slider values**. Saved presets store the look with normalized sliders (padding 0.5, corners 0.1, blur 0), so a preset is "the look, not this capture's sliders". When saved presets exceed 12, the oldest are dropped.
- An image backdrop whose file disappeared renders as **no backdrop**, quietly. Image files are loaded downscaled to at most 4096 px. Thumbnails are at most 220 px, with a 24-entry cache.
- **There are no aspect-ratio presets** (the padding is uniform on all sides) and no shadow controls (the card shadow is fixed, §6.13).

#### 3.10.11 Watermark popover
A popover 292 pt wide. It applies live, is remembered for the next capture, and is drawn **over the annotations and inside the image** (never on the backdrop margin).

- **Kind**: a segmented control **None | Text | Image**. Choosing Image with no picture yet opens the image file dialog.
- **Text**: a text field (placeholder "Watermark text", at most 120 characters, trimmed; focused automatically when empty) and 8 color dots (the editor colors; default **white**; accessible names "Red" … "White").
- **Image**: thumbnail, file name (middle-truncated) or "None", and a **Choose…** button.
- **Saved watermarks** grid (shown when a mark is on or saved ones exist; 4 columns, 38 pt tiles over a neutral gray plate): text presets are drawn in their color, image presets as a thumbnail. Click applies the preset (with its placement). Right-click → "Remove". A dashed **"+" Save watermark** tile (disabled for None or a duplicate). At most 12.
- **Placement** (disabled when None; labels in an 80 pt column):
  - **Position**: a 3 × 3 grid of small buttons, from Top left to Bottom right. Default **Bottom right**.
  - **Size**: 0 … 1, default 0.3.
  - **Opacity**: 0.05 … 1, default 0.4.
  - **Rotation**: −90 … 90°, step 1, default 0. The value is shown as "N°". Positive values tilt the mark up to the right (counter-clockwise on screen).
- Geometry is in §6.14. A picture watermark whose file is missing draws nothing.
- **Settings backups**: watermark image paths are stripped from exports. A restore keeps this machine's own image choice and image presets, while text presets travel.

#### 3.10.12 Crop
- Selecting the Crop tool creates a draft equal to the **whole image**, with 8 grips. Other tools' selection is cleared.
- **Drag a grip** (tolerance `14*scale` px): resize. Edges snap to whole pixels (each edge rounded to the nearest boundary, never grown outward). A **crop loupe** follows the grip: a 72 × 72 pt square showing a 14 × 14 px sample centered on the **edge** (an even side, so the edge falls in the middle), nearest-neighbor, with a black 72 % 1-pt crosshair, a 7 pt circle at the exact point, radius 9, white 90 % 1.5 pt border and a shadow. It's placed 14 pt to the right and above the grip, flipping to stay inside the canvas.
- **Drag inside the draft** (when the draft isn't the whole image): move the draft without resizing. It stops at the image edges.
- **Drag inside a full-image draft, or outside the current draft** (but inside the image): draw a **new** crop rectangle. A tap there restores the previous draft. A press outside the image does nothing.
- **Chrome**: everything outside the draft is darkened with black 45 %. The draft gets a white 95 % border (1.5 × scale) and white grip dots (radius 4 × scale) sitting on the edges, not pushed inward.
- **Apply**: Return or the **Crop** button. It needs at least 8 × 8 px, otherwise the tool just switches to Select. It crops the base image (undoable), shifts every annotation, OCR word and run by the crop origin, rebuilds blur samples, switches to the **Select** tool, and re-runs OCR and QR.
- **Cancel**: Esc or the **Cancel** button switches to Select and discards the draft.

#### 3.10.13 Undo and redo, dirty state, closing
- **Undo stack**: snapshots of (base image, annotation list), up to **60** (the oldest are dropped). An entry is recorded at the start of each mutation: creating a shape, the first movement of a move or resize, a style change on a selection, delete, reorder, crop, and a text edit. Any new edit clears the redo stack.
- **Undo / Redo** restore the snapshot. If the image differs, blur samples and text runs are reset, recognition restarts and the QR scan reruns. Selection, text editing and the crop draft are cleared.
- **Not part of undo**: backdrop, watermark and the shadows toggle (global preferences). They do count toward "dirty".
- **Dirty** = the image, annotations, backdrop, watermark or shadows differ from the last *clean* snapshot. The clean snapshot is taken at open and after each export: Copy, Save, Save As, Add to Shelf, Pin, link created, share target chosen, drag-out.
- **Closing**: the close button, Esc (with nothing else to cancel) or ⌘W asks when dirty: title "Discard this screenshot?", message "It was not copied or saved yet.", buttons **Discard** (destructive) and **Cancel**. The **trash button** and **⌘⌫** close without asking.
- Every final output (Copy, Save, Save As, Add to Shelf) **closes the editor**. Pin, Share, Link, Copy text and drag-out keep it open.

#### 3.10.14 Editor keyboard shortcuts
Key events belong to the editor when they target its window (or carry no window while the editor is key). They're ignored entirely while a text field is editing or while a shortcut recorder is listening.

| Key | Action |
|---|---|
| 1 – 9 (no ⌘, ⌃ or ⌥) | Select the tool in that rail position, if tool shortcuts are enabled and that tool has no custom key |
| Custom tool keys | Select that tool (exact modifier match) |
| ⌘C | If words are selected: copy them as text (editor stays open). Otherwise copy the image and close. |
| ⌘S | Save and close |
| ⌘⇧S | Save As… |
| ⌘Z / ⌘⇧Z | Undo / Redo |
| ⌘P | Pin (editor stays open) |
| ⌘⌫ / ⌘⌦ | Discard and close (no confirmation) |
| ⌘0 / ⌘1 | Fit / Actual size |
| ⌘= / ⌘− | Zoom in ×1.25 / out ×0.8 |
| ⌫ / ⌦ | Delete the selected annotation |
| Return / keypad Enter | Crop tool with a draft: apply the crop. Otherwise: **Copy and close**. |
| Esc | In order: cancel the crop draft → clear the word selection → deselect → close (with the dirty check) |
| ⌃ + scroll | Zoom |
| ⌘W, ⌘Q | Standard window and app behavior (reserved) |

**Reserved editor keys** (they can never become a tool's custom key): with ⌘ (plus any other modifiers): C, S, Z, P, 0, 1, =, −, Delete, Forward Delete, W, Q. Without ⌘, ⌃ or ⌥: Esc, Return, keypad Enter, Delete, Forward Delete.

#### 3.10.15 Tool order and tool shortcuts ("Editor tools")
Available from the rail's gear popover (340 pt wide) and in Settings:

- A toggle "Use tool shortcuts" (default on) gates both the position digits and the custom keys. The caption reads: "Click a shortcut to record a key. Digits 1–9 move the tool. Delete clears its shortcut."
- A list of all 13 tools in rail order. Each row has an icon, a title, a **reset-binding** button (shown when the tool has a custom key), a **shortcut field** (86 pt; its empty state shows the position digit for the first nine, or "None"), and **↑ / ↓** buttons that swap the tool with its neighbor.
- **Recording a key** in a tool's field:
  - **A digit 1–9** (judged by what the key *types* on the current layout, so AZERTY's Shift-& counts as 1, and Caps Lock state is honored) **moves the tool into that slot** and clears its custom key. No conflict check is needed.
  - **Delete** (no modifier) clears the binding and moves the tool **just below slot 9**, so it keeps working without a key.
  - **Any other key** (modifiers optional) becomes a **custom key**, after these checks in order:
    1. Reserved editor key → "This key belongs to the editor."
    2. Bound to another tool → "This shortcut is already used by <tool>."
    3. A global shortcut role of an enabled feature → same message with the feature's name.
    4. A window-layout action shortcut → same message with the action's name.
    5. A system shortcut → same message with "macOS". [Win: "Windows".]
    A rejected key leaves the previous binding untouched. The error is shown under the list as "<Tool> · <key>" plus the reason, in orange.
  - A custom key that the current layout or Caps Lock makes type a digit is **suspended** (inactive and not shown) without being erased, so the digit keeps its slot.
- **Reset** restores the default order and clears every custom key.
- **Default order** (= default digits): 1 Select, 2 Arrow, 3 Blur, 4 Crop, 5 Text, 6 Sticker, 7 Rectangle, 8 Highlighter, 9 Pen, then Line, Ellipse, Number, Solid block (no digit).
- Storage: an order CSV (invalid and duplicate ids dropped, tools missing from a saved list appended in canonical order) and a bindings CSV `tool=shortcut,…` (invalid, reserved or duplicate entries dropped; when a duplicate shortcut appears, one owner is kept, chosen by canonical order).

#### 3.10.16 Export actions from the editor (summary)

| Action | Export used | After |
|---|---|---|
| Copy (button, Return, ⌘C) | Full export (backdrop, watermark, 1x pref) | Toast "Screenshot copied", **close** |
| Save (⌘S, split main) | Full export | Toast "Saved to X", **close**. Failure beeps. |
| Save As… (⌘⇧S) | Full export | **Close** on success |
| Add to Shelf | Full export | Toast "Shelf", **close** |
| Pin (⌘P) | Export **without backdrop** | Stays open |
| Share (system) | Full export temp file | Stays open, marks exported if a target was chosen |
| Link (1/6/24 h) | Full export | Stays open and shows the link sheet (§3.14.6) |
| Drag handle | Full export temp file | Stays open, marked exported |
| Copy text (⌘C with words) | – | Stays open |
| Discard (trash, ⌘⌫) | – | Closes without confirmation |

### 3.11 Pinned captures

- **Creating**: Pin from the quick preview (raw capture) or the editor (export without backdrop). There's no limit on the number of pins.
- **Window**: borderless, non-activating, resizable, floating level (above normal windows), shown on all desktops, excluded from window cycling, with a shadow and a transparent background. The content is the image drawn **aspect-fit** with **7 pt rounded corners** and a 1 pt border (gray 50 % at 40 % opacity). The window's aspect ratio is **locked** to the image. Minimum size 90 × 60 pt.
- **Initial size**: the image's natural point size (`pixels / scale`), scaled down if needed so neither side exceeds **55 %** of the smaller dimension of the pointer display's visible frame, then raised to at least 90 × 60. It's centered on that visible frame. Consecutive pins **cascade**: each new pin is offset by `((count − 1) mod 5) × 26` pt to the right and down.
- **Interaction**:
  - Drag anywhere moves the pin. Edge drags resize it (aspect-locked).
  - **Double-click** closes the pin. A single click makes it key, without activating the app.
  - Keys (when key): **Esc** or **⌘W** close, **⌘C** copies (the pin's pixels, as in §3.8.3, with toast "Screenshot copied"), **arrow keys** move it by 1 pt (⇧: 12 pt).
  - **Right-click menu**: Copy · Save As… (PNG save dialog; activates the app) · separator · **Opacity** ▸ 100 % / 85 % / 70 % / 50 % (current one checked; window alpha) · **Ignore clicks** (checkbox; makes the pin click-through) · separator · Close · **Close all pins**.
  - **Recovering a click-through pin**: while any pin ignores clicks, a global mouse watcher listens for **⌥-click** inside such a pin and turns click-through off for it. The watcher exists only while needed.
- Pins are **content windows** (§3.1): they're excluded from captures only when "Hide Vorssaint windows" is on, or when recording. With hiding off, a pin can be picked as a window target.
- Pins aren't persisted. Turning the Screenshot feature off closes all of them.

### 3.12 Recent captures (history)

- **What's recorded**: every screenshot (full PNG plus a thumbnail of at most 360 px), and every saved recording (path plus thumbnail only, never a second copy of the video). Entries are listed newest first.
- **Limits**: at most **12** entries. Screenshot PNGs together may take at most **256 MB**. The newest screenshot is always kept, and older screenshots that would exceed the budget are dropped. Recording entries don't count toward bytes. A recording re-saved at the same path replaces its older entry.
- **Where it appears**:
  1. **Palette window**: from the "Recent captures" shortcut (⌃⌥⌘H, off), the editor's clock button, the recorder editor, or the Command Bar. It's a borderless floating panel with fixed content width 440 pt (padding 14, high-contrast HUD backdrop, radius 18), centered over the current key window (if visible) or the pointer display's center, and clamped 16 pt inside the visible frame. It fades in over 0.12 s and takes key focus. It **hides** on Esc, on any click outside it (in this app or another), and when another app is activated. It doesn't hide merely because the app deactivated. Its height follows the content.
  2. **Menu bar panel page**: the Screenshot tile's accessory button "Recent captures" (and the Recorder tile's) shows the same list inside the panel.
  3. [Mac-only] A notch page with a horizontal card rail.
- **Content**:
  - Header: a "Recent captures" label (clock icon), a **trash** button "Clear history" (disabled when empty) that asks for confirmation ("Clear history" destructive / Cancel), and a close button (palette only).
  - Empty state: "Take a screenshot or save a recording to find it here."
  - Rows (in a scroll view up to 300 pt tall): a 104 × 68 thumbnail (aspect-fit on black 13 %, radius 7; a placeholder photo or film icon), the kind ("Screenshot" or "Recording"), the file name for recordings (middle-truncated), the relative time ("5 min ago"), a **Restore** button (screenshot) or **Open** button (recording), and a trash button "Remove from history".
  - Entries of an uninstalled feature (Screenshot or Recorder) are hidden.
- **Actions**:
  - **Restore** (screenshot): hide the palette or close the menu panel, wait 0.12 s, then reopen the **quick preview** for that capture with its original scale and anchor (§3.9.5). If the cached file is missing or unreadable, remove the entry and beep.
  - **Open** (recording): close the panel, wait 0.12 s, open the file in its default app. A missing file removes the entry and beeps.
  - **Remove**: drop the entry. Its cached PNG and thumbnail are deleted right away by the orphan cleanup that runs whenever the index is saved.
  - **Clear**: drop every entry (recording files themselves are never deleted).
- **Robustness**: on load, entries whose files vanished are pruned and the cap is re-applied. If the index file is unreadable or corrupt, or is missing while cache images exist, **nothing is written or deleted** (history is frozen rather than wiped), and a later load retries. Orphan cleanup deletes only files named `<UUID>.png` or `<UUID>-thumbnail.png` that the index no longer references, and never follows symlinks.

### 3.13 Latest capture, "Edit latest", "Edit clipboard image", external edits

- **Latest-capture store**: the most recent screenshot, kept only while "Edit latest screenshot" **or** "Upload latest screenshot" is enabled. It's written in the background as a PNG with DPI metadata (`LatestScreenshot.png`). A newer capture supersedes any pending write. Reads return the in-memory pending capture if the write hasn't finished. When neither shortcut is enabled, the store is cleared.
- **Withheld marker** (`LatestScreenshot.withheld`, empty file): created when the latest capture is **discarded** from its preview or opened in **any editor**. "Upload latest" then refuses it (with a beep), even after a relaunch. It's cleared by the next capture.
- **Edit latest screenshot** (⌃⌥⌘E): load the store (the scale comes from the PNG's DPI, valid 0.5–4×; a missing or broken DPI means "no capture"), close any preview, and open the editor. If there's nothing to load, the toast says "Take a screenshot first".
- **Edit clipboard image** (⌃⌥⌘P): read the clipboard off the main thread. Prefer a **file URL to an image file** (a file copied in a file manager carries a small icon image too, so the file wins), else image data. Open the editor. If there's no image, the toast says "Copy an image first". The scale is inferred from the image's logical size vs pixel size (§6.23), defaulting to 1. Images over 60 megapixels are refused.
- **Shelf "Edit image" / clipboard-history "edit image"**: load the file or entry off the main thread and open the editor. A non-image beeps.
- Opening any editor marks the latest capture as withheld (its editor result is no longer "the stored original").

### 3.14 Temporary links (expiring sharing)

#### 3.14.1 Service
- Production endpoint: **`https://screenshots.vorssaint.com`** (operated by Vorssaint). Developer builds only (a separate app identity) may override it with a hidden setting that must be an `https://host` URL with no path, query, fragment or credentials. Release builds ignore the override.
- HTTP client: ephemeral session (no cookies, no cache, no stored credentials), no waiting for connectivity, request timeout 75 s, resource timeout 90 s.

#### 3.14.2 Create
- **Request**: `POST {endpoint}/v1/screenshots?expiresIn={seconds}` where seconds ∈ {3600, 21600, 86400}. Headers: `Content-Type: image/png`, `Accept: application/json`, `Cache-Control: no-store`. The body is the raw **PNG** bytes.
- **Preconditions**: the Screenshot feature is installed and "Allow temporary links" is on. The PNG is non-empty, at most **25 MiB**, and starts with the 8-byte PNG signature. The local records store can be written (checked **before** uploading, so a link can never exist without its deletion token being stored).
- **Response**: status **201** and a JSON body of at most 64 KiB:
  ```json
  { "id": "<32 chars [A-Za-z0-9_-]>", "viewPath": "/s/<id>",
    "expiresAt": "<ISO-8601, with or without fractional seconds>",
    "deleteToken": "<43 chars [A-Za-z0-9_-]>" }
  ```
  The record is valid only if the id and token match those patterns, `viewPath == "/s/" + id`, and `now < expiresAt ≤ now + 24 h 5 min`.
- **Errors**: a network error, or status 429, 503 or 507, means "unavailable". Any other non-201 status means "rejected". A malformed body means "invalid response". All of these show the toast "The link could not be created" and beep.
- **Link URL**: `{endpoint}/s/{id}`.
- If persisting the new record fails after a successful upload, the app immediately DELETEs the link remotely. If even that fails, it keeps the record in memory for this session.

#### 3.14.3 Delete
- `DELETE {endpoint}/v1/screenshots/{id}` with `Authorization: Bearer {deleteToken}` and `Accept: application/json`. Success is **204 or 404**. A link that has already expired isn't deleted remotely, and its record is just removed. A local cleanup error after a successful remote delete isn't reported as a failure.
- Toasts: "Link deleted", or "The link could not be deleted" (with a beep).

#### 3.14.4 Local records
- File `…/TemporaryScreenshotLinks/records.json` in the app's private application-support folder (owner-only). It holds a JSON array of `{id, endpoint, expiresAt, deleteToken}`. Records are deduplicated by id (keeping the latest expiry), pruned once expired, and sorted by expiry.
- The list refreshes at the next expiry plus 0.1 s, and also on **system wake** (timers don't run during sleep).
- Settings → Temporary links → **Shared links (N)** opens a sheet (560 × 360 pt) listing active links: URL (selectable, middle-truncated), "Expires <relative>", **Copy link**, **Open** (browser), **Delete now** (spinner). The empty state reads "No active links".

#### 3.14.5 From the quick preview
- **Link** main click: share with the **default expiry** (setting "Default link expiry", default 1 h). The menu offers a specific duration. The upload uses the raw capture with the 1x preference.
- On success the link is **copied and the preview closes**, with the toast "Link copied". If copying fails, the link row is shown in the preview (so you can copy it manually), the preview grows to 268 pt, the timer becomes 30 s, and the toast says "Link created, but copying failed. Copy it from Temporary links in Settings."
- If the preview closed, the feature was turned off, or links were disabled before the upload finished, the new link is **revoked** (DELETE) instead of being delivered.
- A link row's Delete removes the link and returns the preview to normal.

#### 3.14.6 From the editor
- Link menu → duration. The full editor export is rendered to PNG and uploaded. The window must still be open, and links still allowed, both **before** transmitting and **after** the server answers. Otherwise the link is revoked.
- On success, the edit is marked exported and a **sheet** is shown (480 pt): "Shared links" title with **Done** (default button), the URL box (selectable) with "Expires <relative>", **Copy link** (⌘C, toast "Link copied") and **Delete now** (destructive; on success the toast "Link deleted" appears and the sheet closes, and on failure an alert reads "The link could not be deleted"). The link **isn't** auto-copied here.

#### 3.14.7 "Upload latest screenshot" shortcut (⌃⌥⌘U)
Registered only while the shortcut **and** "Allow temporary links" are both on. When pressed:
1. If a quick preview is open, act on **it**: copy its existing link, or create one with the default expiry.
2. Else, if any editor is open, or the latest capture is withheld: **beep** and stop.
3. Ignore duplicate presses while the same capture's upload is pending.
4. If a previous upload of this same capture produced a link whose copy failed, and the link is still active, copy that link again without re-uploading.
5. Else load the latest-capture store ("Take a screenshot first" if empty), show "Creating link…", and upload with the default expiry. On arrival, if a newer capture happened meanwhile, revoke the link. Otherwise copy it ("Link copied", or the copy-failed message, remembering the link for a retry).
- Turning the shortcut or links off invalidates pending uploads (their links are revoked on arrival) and clears retries.

#### 3.14.8 Privacy wording (shown in Settings → Temporary links → **Privacy**, a 560 × 390 sheet with three paragraphs)
- *Data*: "Vorssaint sends only the rendered image and the expiration you choose. It does not send your name, account, device identifier or MAC address. On this Mac, it keeps only the link, expiration and private deletion token while the link is active."
- *Storage*: "Network providers and the service temporarily process your public IP to prevent abuse. The image and its link metadata are permanently deleted when you delete the link or its time ends. The service does not create screenshot backups."
- *Access*: "Anyone with the link can view, download, save or redistribute the image. Active links are available to the service operator for abuse moderation. Share only with people you trust."
- Additional server facts from the app's privacy document: the uploaded PNG is decoded and **rebuilt without embedded metadata**. The image and link metadata are permanently deleted at expiry or deletion. The client IP is held in memory for at most 24 h for abuse prevention. Private moderation stores the active link, not another copy of the image, and removes it when the link ends. [Win: replace "this Mac" with "this PC".]
- Settings caption: "Choose 1, 6 or 24 hours when sharing. The image is deleted automatically."

### 3.15 Scrolling capture

#### 3.15.1 Starting
- **From the chooser**: in Screenshot mode press **S** ("S on" in the hints). From then on only a **drag** works (window clicks and Return are disabled; the subtitle reads "Drag only the part of the page that moves."). Confirming the drag starts scrolling capture on that region. Pressing S again turns the mode off. Switching tools resets it.
- **Standalone** (Settings "Scrolling capture" button, Command Bar "Scrolling screenshot"): a standalone **live** (unfrozen) selector in geometry mode titled "Scrolling screenshot". A drag, a window click (the window's rectangle) or Return (the whole display) all start scrolling capture on that region.
- The region is snapped to even pixels with a minimum of 32 px. The pointer is never included in frames.

#### 3.15.2 During
- A **control bar** appears at the top center of the pointer display (24 pt below the visible frame's top, material, radius 10): an icon (a spinner once finishing), the message "Scroll with your mouse or trackpad. Press Enter or choose Done.", the current stitched height "N px", and the buttons **Cancel** (Esc) and **Done** (prominent, Return). The bar takes key focus so Return and Esc don't reach the page, while mouse and scroll input go to the page underneath. The bar is excluded from frames.
- The user scrolls the content by any means (wheel, trackpad, scrollbar drag, keyboard). The app **polls pixels**; it doesn't watch input events.
- **Finish**: Done, Return, or invoking the standalone "Scrolling capture" action or the "Capture the whole screen" shortcut again (both finish the running capture instead of starting a new one). **Cancel** gives the toast "Cancelled." and produces no image. Quirk: the unified chooser shortcuts aren't blocked by a running scrolling capture, so the port should block or finish it there too.

#### 3.15.3 Algorithm summary (details in §6.16)
1. Prepare one region capture configuration (the visibility policy is read once at start).
2. Take the first frame and trim fully transparent outer rows and columns. Convert it to a **grayscale sample** 32 px wide (or the image width if smaller) and full height (rows 1:1).
3. Every **90 ms**, capture a frame, sample it, and compare it with the **last accepted** sample:
   - **unchanged** means no movement.
   - **forward advance by `a` rows** means append the `a` newly revealed rows (cropped to the "moving" columns, above a fixed footer if one was detected).
   - **backward** movement is ignored (no duplication). Forward movement later continues from the furthest accepted frame.
   - **unmatched** (content changed in a way no unique overlap explains) sets a "had unmatched content" flag. Nothing is appended.
4. The first forward advance establishes the **content columns** (only the columns that scroll survive in the final image, so fixed sidebars are cut off) and the **fixed footer** (bottom rows identical across frames are kept once and appended at the very end).
5. On finish: one final frame is taken. If scrolling hasn't settled, the app waits up to 0.85 s for it to settle (0.22 s without change). Then all strips are stitched top to bottom.
6. **Results**:
   - *Success*: routed as a normal capture (scale and anchor of the region).
   - *Partial*: unmatched content at finish, or a frame-capture failure mid-way. The completed part is routed, plus the toast "The page changed. The completed part was kept."
   - *Limited*: 120 s, 512 strips, or a 60 Mpx total or retained-pixel limit. The completed part is routed, plus the toast "Capture stopped at the safe limit".
   - *Cancelled*: the toast "Cancelled."
   - *Failed* (no first frame, or an internal inconsistency): the toast "The screen could not be captured".
   - Done without any scrolling returns the first frame unchanged.

### 3.16 Copy text from screen (OCR tool)

1. The chooser opens in **Text** mode (always frozen). You select an area, click a window, or press Return for the full display.
2. In the background:
   - If "Read QR codes" is on (default) and a 2-D code is found in the image, the **QR result panel** opens (§3.17) and nothing is copied automatically.
   - Otherwise, text recognition runs with the **accurate** model, language correction on, automatic language detection on, and preferred languages mapped from the app language (for example German → `de-DE, en-US`, Chinese Traditional → `zh-Hant, en-US`, default `en-US`, filtered to supported ones). If that yields **no lines**, it retries with the **fast** model (no auto-detection, same preferred languages).
   - Lines are joined in reading order (§6.17). With "Remove line breaks" on, the lines are joined into one paragraph.
3. If text was found, it's **copied as plain text** (marked with the app's source type) with the toast "Text copied". Otherwise the toast says "No text found".
- The captured image isn't saved anywhere and is discarded immediately. A newer OCR run supersedes an older one (generation counter).
- Settings: "Copy text from screen" button; caption "Select an area of the screen and the recognized text is copied, ready to paste."; **Remove line breaks** ("Removes line breaks so copied text pastes as one paragraph.", default off); **Read QR codes** ("If the area has a QR code, its content is shown to copy or open.", default on).

### 3.17 QR reading and the result panel

- **Detection**: 2-D matrix symbologies only: **QR, Micro QR, Aztec, Data Matrix, PDF417**. 1-D barcodes are excluded because they fire falsely on striped UI. Every code with a non-empty string payload counts. Several codes are joined with newlines in reading order (§6.18). An **openable URL** is offered only when exactly one code was found and its payload is a single http or https URL with a host and no whitespace. Any other scheme (mailto, tel, custom apps) is never opened.
- **Used by**: the OCR tool (when enabled), the quick preview (background scan; QR button), and the editor (background scan; QR button).
- **Panel**: a non-activating panel that can take key focus, at status-bar level, on all desktops, transient, with a shadow. It's 320 pt wide, material, radius 16, with a hairline border and 16 pt padding:
  - Header: a QR icon (accent) and "QR code".
  - The payload in a scrollable, selectable text box (max height 132 pt, 12.5 pt text, background at 5 %, radius 8).
  - Buttons: with a URL, **Copy** (bordered) and **Open link** (prominent, default). Without a URL, **Copy** (prominent, default).
  - Placement: centered horizontally under the pointer, 18 pt below it, clamped 12 pt inside the pointer display's visible frame. Fade-in 0.12 s, and it becomes key.
  - **Esc** closes it. **Any click outside** (in this app or another) closes it.
  - **Copy** puts the payload on the clipboard (source-marked), closes the panel and shows "QR code copied". **Open link** closes the panel and opens the URL in the default browser.
  - Showing a new result replaces any open panel.

### 3.18 Color picker

- **Start**: the chooser opens in **Color** mode (frozen, loupe forced on). The subtitle reads "Grab the color of any pixel on screen and copy it in your favorite format." The hints are only `1–4` and esc (no ↩, S, Z, C or R chips).
- **Pick**: a mouse **release** anywhere (dragging just moves the loupe), or **Return**, picks the pixel under the pointer: the coordinate is floored and clamped, and read from the frozen still (or the loupe snapshot). The chooser closes, the value is **copied** as text (source-marked), and a toast shows a **color swatch** with the value.
- **C** copies the current pixel without closing (see §3.4.7), so a palette can be read off one frozen screen.
- **Formats** (`colorPickerFormat`; no alpha):

| Format | Example |
|---|---|
| HEX (default) | `#1E90FF` (uppercase). With "Copy without the # prefix": `1E90FF`. |
| RGB | `rgb(30, 144, 255)` |
| HSL | `hsl(210, 100%, 56%)` (integers, rounded) |
| SwiftUI | `Color(red: 0.118, green: 0.565, blue: 1.000)` (3 decimals, always a dot as the decimal separator) |

- Components are clamped to 0…1 and converted to sRGB before formatting. HSL math is in §6.19.
- **Fallback without screen-capture permission** [Mac-only]: the system's native color sampler is used and copies the same way. [Win: not needed. The custom picker always works.]
- The clipboard-history feature (if on) records picked colors like any copied text.
- Settings: "Pick color" button, caption, "Copied format" segmented control (HEX | RGB | HSL | SwiftUI), and "Copy without the # prefix" (shown only for HEX; default off).

### 3.19 HUDs and toasts

- **Toast**: a click-through, non-activating panel at status-bar level, at the **top center** of the pointer display's visible frame, 24 pt below its top. Material, radius 10, padding 14 × 9. It shows an accent-colored icon (or an 18 pt color swatch) and a message (12 pt semibold, at most 2 lines, tail-truncated, at most 360 pt wide). It fades in over 0.12 s, stays **1.5 s**, and fades out over 0.22 s. A newer toast replaces the current one.
- **Countdown**: an 82 pt material circle (no window shadow) showing the remaining seconds (32 pt bold rounded, monospaced digits), with a hairline ring and an **accent progress arc** (3 pt, round caps) that empties over 0.92 s each second (trim from 4 % to 4 % + 92 % × progress, starting at 12 o'clock). One HUD per second.
- **Scrolling control bar**: §3.15.2.
- **Message catalog** (English):

| Situation | Message |
|---|---|
| Capture failed | "The screen could not be captured" |
| Copied image | "Screenshot copied" |
| Saved | "Saved to %@" (folder name) |
| Saved and copied | "Saved to %@ and copied" |
| Added to shelf (editor) | "Shelf" |
| Latest missing | "Take a screenshot first" |
| Clipboard image missing | "Copy an image first" |
| Creating link | "Creating link…" |
| Link copied | "Link copied" |
| Link failed | "The link could not be created" |
| Link copy failed | "Link created, but copying failed. Copy it from Temporary links in Settings." |
| Link deleted | "Link deleted" |
| Delete failed | "The link could not be deleted" |
| Scrolling partial | "The page changed. The completed part was kept." |
| Scrolling limited | "Capture stopped at the safe limit" |
| Cancelled | "Cancelled." |
| OCR copied | "Text copied" |
| OCR nothing | "No text found" |
| QR copied | "QR code copied" |
| Color picked | the formatted value with a swatch |
| Recording failed (from the chooser) | "The screen could not be recorded" |

---

## 4. Settings

### 4.0 Settings UI layout ("Screen capture" page)
The page has a **tool picker** at the top (Screenshot | Screen recording | Copy text from screen | Color picker). It shows as segments, falling back to a dropdown when the segments don't fit. Only installed tools are shown, and the picker is hidden when only one is installed. Selecting a tool changes the options below. The top section always shows:
- **The selected tool's own shortcut**: an enable toggle, a shortcut recorder, and "Show capture menu when using keyboard shortcut" (disabled while the shortcut is off). The orange text "macOS rejected this shortcut. Choose another one." appears if registration failed. [Win: "Windows rejected…" / "already in use".]
- **Recent captures** shortcut (enable toggle and recorder), shown while Screenshot or Recorder is installed.

**Screenshot** sections:
1. *Screenshot*: [Capture now] [Scrolling capture] buttons; the caption "Capture an area, window or the whole screen"; **Capture the whole screen** toggle and shortcut; **Edit latest screenshot** toggle and shortcut; **Edit clipboard image** toggle and shortcut; a Screen Recording permission row when missing [Mac-only].
2. *Capture*: **Freeze the screen while selecting** with the caption "The picture stops while you choose the area, so nothing moves away or changes during the selection."; **Hide Vorssaint windows**; **Delay** segmented (Off, 3 s, 5 s, 10 s); **Include the pointer**; **Show the last capture outline**. Then a **More options** disclosure containing:
   - **Start selection with the magnifier on**
   - **Remember the magnifier's last zoom**
   - **Default magnifier zoom** (0.5×, 1×, 2×, 4×; hidden while remembering)
   - **Wheel zoom**: Fast | Step by step, with the caption "Hold ⌥ to temporarily use the other mode."
   - **Preview position** (Automatic, Top left, Top right, Bottom left, Bottom right)
   - **Focus the preview automatically**, with the caption "Shortcuts work the moment the preview appears, but the keyboard leaves the app you were using until it closes."
   - **Default action** (Ask each time, Save, Save & Copy, Copy, Edit). The caption "Runs automatically right after capture." is shown unless Edit is selected. For Save, Save & Copy and Copy it also shows **Show confirmation preview** and, when on, **Confirmation duration** (1 s, 2 s, 3 s, 5 s, 10 s, Until dismissed), with the caption "After a successful automatic action, keep the preview available for editing or discarding."
3. *Output*:
   - **Copy to the clipboard automatically**, with the caption "Every capture goes to the clipboard as soon as it is taken, ready to paste. Saving a file stays a separate choice." This toggle reads **on** when either it or the default action copies. Turning it off also strips the copy half from the default action (Copy becomes Ask each time, and Save & Copy becomes Save).
   - **Add to the shelf automatically** (disabled while the Shelf is off, when it reads off but keeps its stored value), with the caption "Every capture also goes to the shelf, ready to drag into any app. One that opens in the editor goes there with Add to Shelf. Works while the shelf is on."
   - **Save to**: the folder display name (Desktop by default), a reset "×" when custom, and **Choose…** (folder dialog, can create folders).
   - **Subfolder pattern**: a text field (150 pt) with a live expansion preview, and the caption "Optional. Creates dated subfolders inside the folder above using %y, %year, %mo, %month, %d, %h, %mi and %s, for example %y-%mo for 24-03."
   - **File name**: a text field with a live preview (`<expanded>.png`, or the default name when empty), and the caption "Optional. Overrides the default name using the same … tokens, plus %# for an auto-incrementing number (%## pads to 2 digits, %### to 3, and so on)." When the pattern contains `%#`, a row appears: **Starts at** (number field and stepper, 0–999,999; changing it resets the next number), **Reset** (next = start), and "Next: N".
   - **Save at 1x size**, with the caption "Retina captures are saved at half their pixel size, which makes smaller files." [Win: reword for DPI scaling.]
4. *Editor tools*: tool order and shortcuts (§3.10.15).
5. *Temporary links*: **Allow temporary links**. When on: **Upload latest screenshot** toggle and shortcut, **Default link expiry** (For 1 hour, For 6 hours, For 24 hours), and the caption. Then a **Privacy** button (sheet) and **Shared links (N)** (shown when links exist).

**Copy text from screen** section: §3.16. **Color picker** section: §3.18.

### 4.1 Settings keys
Types: Bool, Int, Double, String. Shortcut values are stored as `"<mods joined by +>:<keycode>"` with mods ∈ control, option, shift, command, using macOS virtual key codes. [Win: store Windows VK codes and modifiers in your own format. Values aren't portable.]

| Key | Type | Default | Values / range | Meaning |
|---|---|---|---|---|
| `screenshotShortcutEnabled` | Bool | false | | Screenshot tool's own shortcut on |
| `screenshotShortcut` | String | ⌃⌥⌘4 | shortcut | Opens the chooser on Screenshot |
| `screenshotShowCaptureMenuOnShortcut` | Bool | true | | Show the mode palette when opened by that shortcut |
| `recorderShortcutEnabled` | Bool | false | | Recording tool shortcut on |
| `recorderShortcut` | String | ⌃⌥⌘5 | | Opens the chooser on Recording (stops an active recording) |
| `recorderShowCaptureMenuOnShortcut` | Bool | true | | |
| `screenOCRShortcutEnabled` | Bool | false | | Text tool shortcut on |
| `screenOCRShortcut` | String | ⌃⌥⌘T | | Opens the chooser on Text |
| `screenOCRShowCaptureMenuOnShortcut` | Bool | true | | |
| `colorPickerShortcutEnabled` | Bool | false | | Color tool shortcut on |
| `colorPickerShortcut` | String | ⌃⌥⌘C | | Opens the chooser on Color |
| `colorPickerShowCaptureMenuOnShortcut` | Bool | true | | |
| `screenshotFullScreenShortcutEnabled` | Bool | false | | Direct full-screen capture shortcut on |
| `screenshotFullScreenShortcut` | String | ⌃⌥⌘3 | | |
| `screenshotLastCaptureShortcutEnabled` | Bool | false | | "Edit latest screenshot" on (also keeps the latest-capture store) |
| `screenshotLastCaptureShortcut` | String | ⌃⌥⌘E | | |
| `screenshotClipboardShortcutEnabled` | Bool | false | | "Edit clipboard image" on |
| `screenshotClipboardShortcut` | String | ⌃⌥⌘P | | |
| `screenshotUploadShortcutEnabled` | Bool | false | | "Upload latest screenshot" on (needs `screenshotSharingEnabled`) |
| `screenshotUploadShortcut` | String | ⌃⌥⌘U | | |
| `recentCapturesShortcutEnabled` | Bool | false | | Recent captures palette shortcut on (Screenshot or Recorder installed) |
| `recentCapturesShortcut` | String | ⌃⌥⌘H | | |
| `screenshotFreeze` | Bool | true | | Freeze displays during selection (Screenshot tool) |
| `screenshotHideVorssaintWindows` | Bool | true | | Exclude all app windows from captures and picking |
| `screenshotDelay` | Int | 0 | 0, 3, 5, 10 (others mean 0) | Countdown seconds before the Screenshot chooser or full-screen capture |
| `screenshotIncludePointer` | Bool | false | | Pointer in captured pixels (Screenshot tool) |
| `screenshotShowLastRegion` | Bool | true | | Dashed outline of the last area |
| `screenshotLoupeStartsOn` | Bool | false | | Loupe on when the chooser opens |
| `screenshotLoupeRememberZoom` | Bool | false | | Start at the last zoom instead of the default |
| `screenshotLoupeDefaultZoom` | Double | 1.0 | 0.5, 1, 2, 4 (clamped to 0.5…4.33) | Default loupe zoom |
| `screenshotLoupeLastZoom` | Double | 1.0 | 0.5…4.33 | Last zoom (machine-local, not in backups) |
| `screenshotLoupeSteppedZoomByDefault` | Bool | false | false = Fast, true = Step by step | Wheel zoom mode (⌥ swaps) |
| `screenshotDefaultAction` | String | "" | "" (Ask each time), save, saveAndCopy, copy, edit (unknown means "") | After-capture action |
| `screenshotPreviewEnabled` | Bool | true | | Confirmation preview after a successful automatic action |
| `screenshotPreviewDuration` | Int | 3 | 1, 2, 3, 5, 10, 0 (until dismissed; invalid means 3) | Confirmation duration in seconds |
| `screenshotPreviewPosition` | String | "" | "" (automatic), topLeft, topRight, bottomLeft, bottomRight | Preview placement |
| `screenshotPreviewTakesFocus` | Bool | true | | Timed previews take keyboard focus on appear |
| `screenshotCopyToClipboard` | Bool | false | | Automatic background copy of every capture |
| `screenshotAddToShelf` | Bool | false | | Automatic shelf add (effective only while the Shelf is installed and on) |
| `screenshotSaveFolder` | String | "" | absolute path or "~/…"; empty means Desktop | Save folder (machine-local, not in backups) |
| `screenshotSaveSubfolder` | String | "" | token pattern | Dated subfolder pattern |
| `screenshotFileNamePattern` | String | "" | token pattern, empty means default name | File name pattern |
| `screenshotFileNumberStart` | Int | 1 | 0…999,999 | `%#` sequence start |
| `screenshotFileNumberNext` | Int | 1 | ≥ 0 | Next `%#` number (advanced on use, rewound on failure or discard) |
| `screenshotDownscale` | Bool | false | | Save at 1x (halve Retina exports) |
| `screenshotToolOrder` | String | `select,arrow,pixelate,crop,text,sticker,rect,highlight,freehand,line,ellipse,counter,redact` | CSV of tool ids | Rail order (= digit mapping 1–9) |
| `screenshotToolShortcuts` | String | "" | `tool=shortcut,…` | Custom per-tool keys (modifiers optional) |
| `screenshotToolShortcutsEnabled` | Bool | true | | Digit and custom tool keys active |
| `screenshotLastTool` | String | arrow | tool id (select and crop are never restored) | Remembered tool |
| `screenshotLastColor` | String | red | red, orange, yellow, green, blue, purple, black, white | Remembered color |
| `screenshotLastStroke` | String | medium | small, medium, large | Remembered thickness |
| `screenshotLastTextSize` | Int | 19 | presets 10…96 (0 means 19, clamped) | Remembered text size |
| `screenshotLastBlurLevel` | Int | 3 | 1…5 (opened at ≥ 3) | Remembered blur strength |
| `screenshotLastBlurStyle` | String | pixelate | pixelate, blur, erase | Remembered blur style |
| `screenshotLastBlurTextOnly` | Bool | false | | Remembered "Text only" |
| `screenshotLastArrowStyle` | String | filled | filled, outline, open, doubleEnded, scribbly | Remembered arrow style |
| `screenshotLastSticker` | String | check | check, cross, star, heart, thumbsUp, thumbsDown, smile, laugh, party, fire, warning, eyes | Remembered sticker |
| `screenshotAnnotationShadows` | Bool | false | | Drop shadows on annotations and watermark |
| `screenshotBackdropStyle` | String (JSON) | "" (none) | BackdropStyle (§5.3) | Current background |
| `screenshotBackdropPresets` | String (JSON) | "[]" | ≤ 12 BackdropStyle | Saved backgrounds |
| `screenshotWatermarkStyle` | String (JSON) | "" (none) | WatermarkStyle (§5.3) | Current watermark (image path stripped from backups) |
| `screenshotWatermarkPresets` | String (JSON) | "[]" | ≤ 12 WatermarkStyle | Saved watermarks (image presets not in backups) |
| `screenshotSharingEnabled` | Bool | true | | Allow temporary links |
| `screenshotUploadDuration` | Int | 3600 | 3600, 21600, 86400 (invalid means 3600) | Default link expiry (seconds) |
| `screenshotSharingDeveloperEndpoint` | String | – | https host URL | Developer builds only, never in backups |
| `screenshotOpenEditorDirectly` | Bool | false | legacy | Migrated once to `screenshotDefaultAction = edit` if no action was set |
| `screenOCRRemoveLineBreaks` | Bool | false | | OCR joins lines into one paragraph |
| `screenOCRDetectQRCodes` | Bool | true | | OCR tool checks for QR first |
| `colorPickerFormat` | String | hex | hex, rgb, hsl, swiftui (unknown means hex) | Color copy format |
| `colorPickerBareHex` | Bool | false | | HEX without "#" |
| `panelUtilityScreenshot` | Bool | true | | Menu panel Screenshot tile visible |
| `panelUtilityScreenOCR` | Bool | true | | Menu panel OCR tile visible |
| `panelUtilityColorPicker` | Bool | true | | Menu panel color picker tile visible |
| `recorderSystemAudio`, `recorderMicrophone` | Bool | (recorder) | | Written by the chooser's audio toggles (recorder spec) |
| `notchCaptureControls` | Bool | true | | [Mac-only] Chooser controls in the notch |
| `unifiedScreenCaptureShortcutMigrated`, `restoredScreenCaptureShortcutsMigrated`, `orphanedCaptureShortcutMigrated` | Bool | false | | One-time migrations of the old shared shortcut (not in backups). Not needed on Windows. |

Feature installation (`featureAvailable.screenshot`, `.screenRecorder`, `.screenOCR`, `.colorPicker`) belongs to the features-hub spec. Uninstalling Screenshot unregisters its shortcuts, clears the latest-capture store, cancels countdowns, selection and scrolling, revokes pending uploads, and closes the preview, every editor and every pin.

**Backups**: all keys above travel in settings backups except the save folder, the last loupe zoom, the developer endpoint and the migration flags. Watermark image paths and image presets are local-only: on restore, this machine's own image choice and image presets are kept, and text presets are imported (text presets are trimmed to make room so local image presets are never evicted).

---

## 5. Data and files

All paths below use `<Caches>` = the per-user cache folder for the app (macOS `~/Library/Caches/<bundle id>`), `<AppSupport>` = the per-user application data folder (macOS `~/Library/Application Support/<bundle id>`), and `<Temp>` = the per-user temporary folder. [Win: suggest `%LOCALAPPDATA%\Vorssaint\Cache`, `%LOCALAPPDATA%\Vorssaint` and `%TEMP%`.]

### 5.1 Files

| Path | Format | Lifetime / retention | Notes |
|---|---|---|---|
| `<SaveFolder>/<subfolder>/<name>.png` | PNG with DPI = 72 × scale (§6.15) | Permanent (user's) | Default name `Screenshot yyyy-MM-dd at HH.mm.ss.png`. On macOS it also gets the "is screen capture" xattr. |
| `<Caches>/Copied Screenshots/<name>.png` | PNG | Pruned on each copy: older than 24 h, or beyond 100 files or 256 MB (newest kept) | Folder 0700, files 0600, symlinked folder refused. Lets paste targets read the file later. |
| `<Caches>/RecentCaptures/history.json` | JSON array of entries (sorted keys) | Up to 12 entries, screenshots ≤ 256 MB | Folder 0700, files 0600. |
| `<Caches>/RecentCaptures/<UUID>.png` | PNG with DPI | With its entry | Full screenshot |
| `<Caches>/RecentCaptures/<UUID>-thumbnail.png` | PNG (≤ 360 px long side, 72 DPI) | With its entry | For screenshots and recordings |
| `<Caches>/LatestScreenshot.png` | PNG with DPI | Replaced by every capture; cleared when both latest-capture shortcuts are off or Screenshot is uninstalled | For Edit latest and Upload latest |
| `<Caches>/LatestScreenshot.withheld` | Empty marker file | Present after a discard or editor-open of the latest capture; removed by the next capture | Blocks Upload latest across relaunches |
| `<AppSupport>/TemporaryScreenshotLinks/records.json` | JSON array `{id, endpoint, expiresAt, deleteToken}` | Expired records pruned | Owner-only. Holds **secrets** (delete tokens). [Win: consider DPAPI.] |
| `<Temp>/ScreenshotDrag-<UUID>/<name>.png` | PNG | Deleted after 1 h; leftovers deleted at launch | Drag-out and system share payloads |
| Backdrop image / watermark image | User files (any image type) | Referenced by absolute path in settings | Never copied. A missing file means quietly none. |

### 5.2 Recent-capture entry (JSON)
```json
{ "id": "UUID", "kind": "screenshot" | "recording", "createdAt": <date>,
  "screenshotName": "UUID.png" | null, "recordingPath": "/abs/path.mov" | null,
  "thumbnailName": "UUID-thumbnail.png" | null, "scale": 2.0 | null,
  "anchorX": .., "anchorY": .., "anchorWidth": .., "anchorHeight": .. }
```
Names must be plain file names (no path separators). Anything else is treated as missing.

### 5.3 Style JSON schemas
**BackdropStyle**
```json
{ "kind": "none" | "preset" | "solid" | "gradient" | "image",
  "presetID": "ocean|sunset|forest|candy|graphite" (preset only),
  "colors": [[r,g,b]] (solid: 1, gradient: 2; components 0…1),
  "imagePath": "/abs/path" (image only),
  "padding": 0…1 (default 0.5), "cornerRadius": 0…1 (default 0), "blur": 0…1 (default 0; may be absent in old data) }
```
Sanitizing: sliders are clamped (a non-finite padding becomes 0.5, a non-finite corner 0.1, a non-finite blur 0). An invalid preset id, a wrong color count, non-finite colors, or an empty image path **demote the kind to none** (colors are clamped component-wise). Undecodable JSON means the default (none).

**WatermarkStyle**
```json
{ "kind": "none" | "text" | "image", "text": "≤120 chars, trimmed",
  "imagePath": "/abs/path" | null, "color": "red…white" (default "white"),
  "anchor": "topLeading|top|topTrailing|leading|center|trailing|bottomLeading|bottom|bottomTrailing" (default bottomTrailing),
  "size": 0…1 (0.3), "opacity": 0.05…1 (0.4), "rotation": -90…90 (0) }
```
Sanitizing: text is trimmed and capped. An unknown color becomes white. Non-finite sliders become their defaults. Text kind with empty text, or image kind without a path, demotes to none (the other fields are kept).

**Presets**: arrays of the above. They're sanitized, `none` entries are dropped, and the **last 12** are kept.

### 5.4 In-memory only
- The last region (display and rect) for R and the ghost outline: app session only.
- Pins, open editors (including their undo history) and quick previews: never persisted.

### 5.5 Privacy-relevant facts
- Nothing leaves the machine except when the user creates a temporary link (§3.14). OCR, QR and color run on device.
- The copied-files cache and recent captures are private, bounded, local caches.

---

## 6. Algorithms and constants

Pseudo-code notation: `round` = round half away from zero, `floor` and `ceil` as usual, `a/b` on integers = integer division, `clamp(x, lo, hi)`.

### 6.1 Coordinates and pixel mapping
- On macOS, global window-list rectangles use a top-left origin, while UI coordinates use a bottom-left origin anchored on the primary display. Conversions: `cocoaY = primaryHeight − rect.maxY`. Overlay views are **flipped** (top-left origin per display): `viewX = x − screen.minX`, `viewY = screen.maxY − rect.maxY`. [Win: with per-monitor DPI awareness v2, the virtual desktop uses top-left origin physical pixels everywhere, so these flips disappear. Only the DIP ↔ pixel conversion per monitor remains.]
- **View rect → image pixel rect**: `sx = imageW / viewW`, `sy = imageH / viewH`. Scale each edge, then make it integral by flooring the min edges and ceiling the max edges (rounding outward), then clamp to the image.
- **View point → image point** (loupe): `(x / viewW × imageW, y / viewH × imageH)`, each clamped to `[0, size]`. The pixel read uses `floor` of these, clamped to `[0, size − 1]`.
- **Selection badge**: `round(selectionPt × displayScale)` for each side.

### 6.2 Selection rectangle (overlay drag)
```
dx = cur.x − origin.x ; dy = cur.y − origin.y
if square: s = max(|dx|, |dy|); dx = sign(dx)·s; dy = sign(dy)·s   (sign(0) treated as positive)
if fromCenter: rect = (origin.x − |dx|, origin.y − |dy|, 2|dx|, 2|dy|)
else:          rect = (min(origin.x, origin.x+dx), min(origin.y, origin.y+dy), |dx|, |dy|)
rect = rect ∩ displayBounds
click  ⇔ |Δx| < 4 and |Δy| < 4 (points)
minimum region = 2 × 2 points (else ignored)
```
Space-move: `selection.origin += Δpointer; origin += Δpointer`, then clamp.

### 6.3 Loupe math
```
BASE = 13 ; MIN_SIDE = 3 ; MIN_ZOOM = 0.5 ; MAX_ZOOM = BASE / MIN_SIDE (= 4.333…)
FRAME = 132 pt ; INFO_H = 24 ; INFO_GAP = 6 ; GAP = 16 ; INSET = 8
DEFAULT_ZOOMS = [0.5, 1, 2, 4]

sampleSide(z) = max(3, 2·floor((BASE / clamp(z)) / 2) + 1)          // odd
gridVisible   = sampleSide > 0 and FRAME / sampleSide ≥ 6

fastZoom(z, d):   if d == 0: return clamp(z)
                  f = d > 0 ? 1.15 : 1/1.15
                  n = (plain wheel, i.e. not continuous) ? 3 : 1
                  return clamp(z · f^n)       // clamp after each step
steppedZoom(z, d): side = sampleSide(z); widest = sampleSide(MIN_ZOOM) (= 27)
                  target = d > 0 ? max(3, side − 2) : min(widest, side + 2)
                  if target == side: return clamp(z)
                  if target == widest: return MIN_ZOOM
                  return clamp(BASE / target)
useStepped = steppedByDefault XOR optionHeld
wheelDelta = fixedPointDelta ≠ 0 ? fixedPointDelta : (lineDelta ≠ 0 ? lineDelta : pixelDelta)
initialZoom = clamp(rememberLast ? lastZoom : defaultZoom)   (non-finite → 1)

sampleRect(p, imgSize, side, centredOnPixel = true):
    side = max(1, floor(side)); even = side % 2 == 0
    if centredOnPixel and even: side −= 1      // capture loupe: odd, centred on a pixel
    if !centredOnPixel and !even: side += 1    // crop loupe: even, centred on an edge
    w = min(side, floor(imgW)); h = min(side, floor(imgH))
    x = centred ? floor(p.x) − floor(w/2) : floor(p.x − w/2)   (same for y)
    return (clamp(x, 0, imgW − w), clamp(y, 0, imgH − h), w, h)

targetPixelRect(p, src, frame):   col = clamp(floor(p.x), src.minX, src.maxX − 1); row likewise
    cell = frame.size / src.size ; rect = frame.origin + (col − src.minX, row − src.minY)·cell, size cell
    ring = rect inset by −1 pt ; arms run from each frame edge to the ring

frame placement (flipped, block = FRAME × (FRAME + INFO_GAP + INFO_H)):
    o = (p.x + GAP, p.y − blockH − GAP)                  // above-right
    if o.x + blockW > maxX − INSET: o.x = p.x − blockW − GAP
    if o.y < minY + INSET:          o.y = p.y + GAP
    clamp o into [INSET, size − block − INSET]
nudge step (points) = (shift ? 10 : 1) / max(displayScale, 1)
```
Out-of-display nudge target: clamp to the current display, x ∈ [minX, maxX − 1px], y ∈ [minY + 1px, maxY].

### 6.4 Quick preview placement
```
visible = visibleFrame of display with max overlap(anchorRect) (tie → display containing pointer; none → pointer display)
if position ≠ automatic:   usable = visible inset 16
    topLeft  → (usable.minX, usable.maxY − h)    topRight    → (usable.maxX − w, usable.maxY − h)
    bottomLeft → (usable.minX, usable.minY)      bottomRight → (usable.maxX − w, usable.minY)
    (coordinates bottom-left origin; each min/max guarded so it never passes the opposite edge)
else (automatic; treated as bottomRight when a default action is set): usable = visible inset 10; gap = 14
    x = anchor.maxX + gap;  if x + w > usable.maxX: x = anchor.minX − w − gap
    if x < usable.minX or x + w > usable.maxX: x = pointer.x + gap
    y = anchor.midY − h/2
    if y out of usable: y = anchor.minY − h − gap (if ≥ usable.minY) else anchor.maxY + gap (if fits) else pointer.y − h/2
    clamp x, y into usable
size = 350 × 210 (268 with link row)
```

### 6.5 Editor window size and canvas fit
```
V = visible size of pointer display
minW = min(V.w, min(980, max(760, 0.76·V.w)));  minH = min(V.h, min(680, max(560, 0.72·V.h)))
maxW = max(minW, 0.90·V.w);  maxH = max(minH, 0.88·V.h)
chrome = (96, 140)
fit = min(1, (maxW − 96)/imgPtW, (maxH − 140)/imgPtH)        // imgPt = pixels / scale
content = clamp(imgPt·fit + chrome, min, max)  → window centred

canvas zoom (view pt per image px):
   override if set (range 0.05…3), else min(1, min((availW − 28)/contentPxW, (availH − 28)/contentPxH)),
   with avail dims each ≥ 80 after removing 2×14 margin; contentPx = image + 2·padding
zoom label = round(zoom · scale · 100) %;  "actual size" = 1/scale
⌃scroll factor = 1 + clamp(deltaY, −24, 24) · 0.014 ;  ⌘+ ×1.25 ; ⌘− ×0.8
view → image point: (v.x / zoom − padding, v.y / zoom − padding)
```

### 6.6 Arrow geometry
All values are in image pixels, `w = strokeWidth × scale`.
```
θ = atan2(tip.y − tail.y, tip.x − tail.x);  dist = |tip − tail|
L = min( max(10, 3.4w),  max(0.72·dist, min(1.2w, dist)) )     // head length
φ = π/7
left  = tip − L·(cos(θ − φ), sin(θ − φ))
right = tip − L·(cos(θ + φ), sin(θ + φ))
base  = (left + right)/2 ;  n = (−sin θ, cos θ)·w/2
```
- **Solid** (filled, one closed contour, nonzero fill): `tail+n → base+n → left → tip → right → base−n → tail−n →` a semicircular arc of radius `w/2` around `tail` (from `θ−π/2` to `θ+π/2`, passing behind the tail) `→ close`. The shaft, round tail and head are one contour, so there are no winding cutouts.
- **Outline**: subpath `tail → base`, plus closed triangle `left → tip → right → left`. Stroke `w`, round caps and joins.
- **Open**: `tail → tip`, plus `left → tip → right`.
- **Double-ended**: Open, plus a head computed from `tip → tail` (`tailLeft → tail → tailRight`).
- **Scribbly**: three polylines (shaft `tail → base`, wing `left → tip`, wing `right → tip`) from `rough()`:
  ```
  rough(a, b, n, wobble, dir?, perp?): for i in 0…n: p = i/n
      if i == 0 or i == n: point = a + (b−a)·p
      else: e = sin(π·p); s = rng()·wobble·e; f = rng()·wobble·0.28·e
            point = a + (b−a)·p + perp·s + dir·f
      (dir/perp default to the segment's own unit direction and its left normal)
  shaft: n = clamp(ceil(dist / max(10, 3w)), 4, 24); wobble = min(max(1, 0.35w), 0.025·dist); dir/perp = arrow's
  wings: n = 3; wobble = min(max(0.8, 0.22w), 0.035·dist)
  order of RNG draws: shaft interior points (side, then forward), then left wing, then right wing
  rng: state = (seed == 0 ? 0x9E3779B97F4A7C15 : seed)
       next(): state = state·2862933555777941757 + 3037000493  (mod 2^64)
               return (state / (2^64 − 1))·2 − 1     // as double in [−1, 1]
  ```
  Each new scribbly arrow (and each switch to scribbly) gets a random 64-bit seed stored on the annotation. The menu samples use the fixed seed `0x5343524942424C59`.
- Stroked styles draw shaft and heads as **one path**, so a drop shadow is cast once.

### 6.7 Pen smoothing
```
move(p0); for i in 1..<n: quadCurve(to: mid(p[i−1], p[i]), control: p[i−1]); line(to: p[n−1])
```
Every pointer sample is kept (no decimation). A stroke needs at least 2 points to draw.

### 6.8 Hit testing (editor)
`tol = 10 × scale` px. Annotations are tested topmost first (reverse drawing order):

| Kind | Hit if |
|---|---|
| arrow, line | distance(point, segment tail→tip) ≤ `tol + strokeWidth × scale / 2` (straight segment even for scribbly) |
| pen | any stored point within `tol` |
| counter | distance to center ≤ `counterDiameter/2 + 4 × scale` |
| rect, ellipse, highlight, blur, redact | inside `rect` grown by `tol/2`. For **creation taps**, additionally *not* inside `rect` shrunk by `tol` (edge ring only), unless the shrunk rect is empty. |
| text, sticker | inside `rect` grown by `tol/2` |

- Segment distance: project onto the segment with `t = clamp(((p−a)·(b−a)) / |b−a|², 0, 1)`, then take the Euclidean distance. A degenerate segment means the distance to `a`.
- Handle hit: `|handle.x − p.x| ≤ tol_h and |handle.y − p.y| ≤ tol_h`, where `tol_h = 12 × scale` (annotations) or `14 × scale` (crop). Handles in order: TL, T, TR, R, BR, B, BL, L.
- Endpoint hit: Euclidean distance < `12 × scale`.
- Word hit: word rect grown by `2 × scale`.
- Resizing: set the edges touched by the handle to the point, then normalize (`min`, `abs`) so the rect flips instead of going negative.
- Moving a crop: `x = clamp(r.minX + dx, b.minX, b.maxX − r.w)` (same for y). If the rect is larger than the bounds, intersect instead.
- Crop snapping: `minX, minY, maxX, maxY` each rounded to the nearest integer, then intersected with the image.
- New crop selection starts when `bounds ∋ p and (draft == bounds or draft ∌ p)`.

### 6.9 Counters, stickers, text
```
counterDiameter = max(22, min(imgW, imgH) / 24)                 // image px (scale factor 1)
   circle centred on rect.mid; ring width = max(1.5, 0.05·d), inset 1; label font = bold, 0.52·d
stickerSide(img, scale): short = min(W, H); minimum = min(52·scale, 0.45·short)
   side = max(1, max(minimum, min(0.16·short, 128·scale)))
stickerRect(center, side, bounds): s = min(max(1, side), min(bounds.w, bounds.h)); square centred at the tap, moved inside bounds
   glyph font size = max(10·scale, 0.82·min(rect.w, rect.h)), centred
textBounds(text, origin, size, scale): font = system semibold at size·scale px
   m = measure(text.isEmpty ? " " : text); rect = (origin, ceil(m.w) + 4, ceil(m.h)); drawn at (minX + 2, minY)
```

### 6.10 Blur samples (pixelate and soft blur)
```
factor(level) = {1: 0.4, 2: 0.65, 3: 1.0, 4: 1.5, 5: 2.2}[clamp(level, 1, 5)]
block = max(2, round( max(10, floor(min(W, H) / 55)) · factor ))

Pixelate sample: sw = max(1, W / block); sh = max(1, H / block)
   draw base image into sw×sh with medium-quality (averaging) downscale
   noise(): for each sample pixel, n = (randomByte mod 19) − 9  ∈ [−9, 9]; add n to R, G, B; clamp 0…255; alpha untouched
   render: stretch the sample to W×H with nearest-neighbour, clip to the area (∩ each text run in text-only mode), REPLACE blend
Soft blur sample: step = max(1, block / 4); sw = max(1, W / step); sh = max(1, H / step)
   downscale (medium), noise(), then Gaussian blur radius r = 0.8 · block / step (in sample px; ≈3.2)
   with edge-clamped input, cropped back to the sample extent
   render: stretch to W×H with high-quality (bilinear+) interpolation, clip, REPLACE blend
```
Noise is fresh on every build, so the same text never samples to the same values twice. Samples are cached per level and per style, and dropped when unused or when the base image changes. Pixelation keeps only the small sample, not a full-size mosaic.

### 6.11 Erase (content-aware fill from the surroundings)
For each run (the whole area, or each text run in text-only mode):
```
area = integral(run) ∩ imageBounds          (skip if < 1 px)
band = clamp(round(0.12·min(area.w, area.h)), 2, 8)
color space = capture's own RGB space if drawable, else sRGB; pixels read as premultiplied RGBA
Attempt passes until at least one side succeeds:
   pass 1: strips OUTSIDE each edge (thickness band), skipping pixels inside other text runs
   pass 2: strips OUTSIDE, no skipping
   pass 3: strips INSIDE each edge (thickness min(band, area.w, area.h))   // area as big as the image
Side strip (for edge E ∈ top, bottom, left, right), visible part only (∩ image; < 1 px → side missing):
   length = along-edge size; depth = across size
   segments = clamp(length / max(12, 4·band), 1, 48)            (integer)
   for each segment: up to 32 samples spread along it and across the depth
        (sample k: along = start + k·span/attempts; across = k mod depth), skipping text-run pixels
        median per channel (exact value if all samples equal); segments with no samples copy the nearest found
   offset = outside ? depth/2 : 0
Grid: cell = max(4, max(area.w, area.h) / 128); cols = ceil(area.w / cell) + 1; rows = ceil(area.h / cell) + 1
   node x_i = i/(cols−1)·area.w ; y_j = j/(rows−1)·area.h
   side colour at a node = linear interpolation between neighbouring segment medians:
        place = clamp(pos/length·count − 0.5, 0, count − 1)
   weights: top = 1/max(0.5, y + off_top); bottom = 1/max(0.5, (h − y) + off_bottom);
            left = 1/max(0.5, x + off_left); right = 1/max(0.5, (w − x) + off_right); missing side → 0
   colour = Σ(weight·sideColour)/Σweight  (alpha first, colour channels clamped ≤ alpha: premultiplied)
Draw the cols×rows patch into area grown by half a cell (cellW = area.w/(cols−1), cellH = area.h/(rows−1)),
   bilinear interpolation, clipped to run ∩ annotation rect ∩ image, REPLACE blend
```
A flat background is reproduced exactly, and so is a straight gradient. Fills are cached per (integral rect, skipsText) for the current base image and text runs. The cache is cleared when either changes, and after 256 entries it keeps only the fills used in the last redraw.

### 6.12 Text runs and on-canvas recognition
**Bands** (so a tall capture is never one huge request):
```
maxTilePixels = 12,000,000
tileH = min(H, max(512, min(4096, maxTilePixels / W)))
overlap = (tileH < H) ? (min(256, tileH / 4) rounded down to even) : 0
y = 0; repeat: bandH = min(tileH, H − y); last = (y + bandH ≥ H)
   owned = [ (y == 0 ? 0 : y + overlap/2), (last ? H : y + bandH − overlap/2) )
   emit (rect (0, y, W, bandH), owned); if last stop; y += bandH − overlap
```
**Per band**: recognize lines (accurate, language correction, auto language). For each line take the top candidate. A line without a candidate becomes one box-less word "" with the line box. Otherwise split the candidate's text on spaces and get each word's box from the recognizer (it may be absent, in which case `rect = nil`). Line indices continue across bands. Boxes are converted to image pixels (`y = bandY + (1 − maxY)·bandH`, since Vision boxes are normalized with a bottom-left origin).

**Merging across seams** (for each adjacent pair upper/lower; shared rows = `[lower.minY, upper.maxY)`; seam = `upper.owned.upperBound`):
- Candidates = words with boxes that intersect the shared rows, in both bands.
- For each upper word, the best unpaired lower word by `score = share + (sameText ? 1 : 0)`, where `share = area(∩) / min(areas)`. It qualifies if `sameText and ∩height ≥ min(heights)/2`, or if `share ≥ 0.5`.
- Pair them. If the average of the two midYs is above the seam, drop the lower copy. Otherwise drop the upper copy.
- **Kept words** are assigned to the band whose owned rows contain their midY, and concatenated band by band (this preserves line order for copying).
- **Runs** use every kept word, plus dropped duplicates whose midY lies within a quarter of the shared rows past the band's owned range (both guesses stay covered), plus line boxes of box-less words.
- Runs are `nil` unless every band was read successfully.

**Runs from words**:
```
for words with w>0, h>0 in order: if same line as current run:
    h = max(run.h, word.h); gap = max(word.minX − run.maxX, run.minX − word.maxX, 0)
    vOverlap = min(run.maxY, word.maxY) − max(run.minY, word.minY)
    if gap ≤ 1.5·h and vOverlap ≥ min(run.h, word.h)/2: run = run ∪ word; continue
  close run; start new
padded run = run grown by (max(1, 0.6·h), max(1, 0.3·h)) on each side
text-only area covers: runs == nil ? [area] : runs that intersect area (each clipped to area)
after crop: runs offset by −crop.origin, keep those intersecting the new bounds (nil stays nil)
```
**Word selection**: `selectionRect(anchor, current)` grown by 1 px; select the indices whose word rect intersects it. Copy joins the selected words in index order, with a space within a line and `\n` between lines.

### 6.13 Backdrop rendering (export)
```
short = min(W, H)                                   // image px, before padding
P = max(24, round(short · (0.035 + 0.14·padding)))  // padding slider 0…1
R = round(cornerRadius · short · 0.2)               // corners slider 0…1
canvas = (W + 2P) × (H + 2P)
fill: solid colour | 2-stop linear gradient from canvas top-left (colour 0) to bottom-right (colour 1)
      | image aspect-fill centred (high-quality)
if blur > 0: blur the filled plate, radius = blur · min(canvasW, canvasH) · 0.035, edges clamped
card shadow: offset 6·scale px DOWN, blur 22·scale, black 40 %; painted by filling the card's rounded rect
             while clipped to (canvas − card) with even-odd, so no plate appears under translucent captures
card: flattened image drawn at (P, P) clipped to rounded rect radius R
no backdrop and R > 0: flattened image clipped to rounded rect → transparent corners
```
The live canvas approximates the same thing at display scale: a shadow 5 pt down, blur 18, black 38 %, and the live backdrop blurred by `radius × zoom` and scaled by `1 + 0.08·blur` to hide edge fade. Both use the same padding and radius formulas.

### 6.14 Watermark
```
fontPx  = max(8, round(short · (0.02 + 0.14·size)))      // text: system semibold, colour from palette
imageW  = max(1, round(W · (0.05 + 0.45·size))); imageH = imageW · srcH/srcW
content = measured text size (ceil) or image size
rad = rotation·π/180
bounds = (|cw·cos| + |ch·sin|, |cw·sin| + |ch·cos|)
r  = clamp(cornerRadius R, 0, short/2)
roundedInset = r > 0 ? ceil(r·(1 − √0.5)) + 1 : 0
margin = min(0.45·short, max(0.05·short, roundedInset))
avail = (W − 2·margin, H − 2·margin)
fit = min(1, avail.w/bounds.w, avail.h/bounds.h)
fitted = bounds·fit
(ux, uy) = anchor unit point (TL=(0,0), T=(0.5,0), TR=(1,0), L=(0,0.5), C=(0.5,0.5), R=(1,0.5), BL=(0,1), B=(0.5,1), BR=(1,1))
center = (margin + (avail.w − fitted.w)·ux + fitted.w/2,  margin + (avail.h − fitted.h)·uy + fitted.h/2)
draw: one transparency layer with global alpha = opacity (and the annotation shadow if enabled);
      translate(center); rotate so positive degrees turn COUNTER-clockwise on screen; scale(fit);
      draw content centred at origin
```
It's drawn after the annotations and clipped to the image (never on the backdrop margin).

### 6.15 Export pipeline, encoding and 1x
1. Create a `W × H` RGBA bitmap (8-bit, premultiplied). Draw the base image, then annotations in drawing order, then the watermark (passing `R` for its margin).
2. No backdrop: round the corners if `R > 0`. With a backdrop: compose it as in §6.13.
3. If "Save at 1x size" is on and `scale > 1`: resample the **whole result** (including the backdrop) to `(round(w/scale), round(h/scale))` with high-quality interpolation. The reported scale becomes 1.
4. **PNG**: embed the physical resolution as **DPI = 72 × scale** on both axes (a 2× capture is 144 DPI). [Win decision: Windows treats 96 DPI as 100 %. Write `96 × scale` and read back `dpi / 96`, or keep 72-based values for macOS interoperability. Pick one convention and apply it to the latest-capture store too.]
5. **Clipboard TIFF** (macOS): the bitmap's point size is `pixels / scale`. [Win: use DIBV5 with alpha plus a registered "PNG" format plus a file drop. DIB resolution fields may carry DPI.]
6. Reading a stored PNG back: `scale = dpi / 72`, accepted only if it's within 0.5…4. Otherwise the file is rejected (latest-capture store) or treated as missing.

### 6.16 Scrolling capture (exact matching policy)
**Sampling**: frame → trim fully transparent outer rows and columns (top, bottom, then left, right; nothing left means failure) → grayscale 8-bit `sw × H` with `sw = min(32, W)`, horizontally resampled only (rows stay 1:1, so integer scrolls stay integer).

**Difference**: `diff(A, B, cols)` = mean |A − B| over rows `[H/24, H − H/24)` and `cols`. **Stable** ⇔ same size and `diff ≤ 1.5`.

**Transition(prev, cur, contentCols = all if unknown)**:
```
require same size, H ≥ 24, valid cols
if stable(prev, cur, cols): return END
minA = max(2, round(0.01·H)); maxA = min(H − 8, round(0.88·H)); if minA > maxA: UNMATCHED
tileW = max(2, sw / 8); tiles = consecutive cols chunks of tileW (a trailing chunk < 2 wide is dropped unless it's the only one)
moving = tiles with diff(prev, cur, tile) > 1.5; if none: UNMATCHED
req = max(8, min(28, H / 12)); edge = max(2, H / 10)
for a in minA…maxA, for dir in [forward, backward]:
   D[r][c] = |cur[r + (backward ? a : 0)][c] − prev[r + (forward ? a : 0)][c]|, r ∈ [0, H − a)
   match(C): rows r ∈ [edge, H − a − edge) (need H − a − edge > edge)
       rowSum[r] = Σ_{c∈C} D[r][c]; row matches ⇔ rowSum ≤ 8·|C|
       longest = longest streak of matching rows; matching = count; compared = row count
       difference = Σ rowSum / (compared · |C|)
       supports ⇔ longest ≥ req and matching ≥ max(req, compared / 3)
   group consecutive supporting moving-tiles; one non-supporting tile may be skipped inside a group;
   a further miss closes the group; groups need ≥ (moving.count ≥ 3 ? 2 : 1) tiles
   for each group: C = first.lower ..< last.upper; require match(C) supports
   candidate(a, dir) = best group by (|C| desc, matching desc, difference asc)
sort all candidates by (|C| desc, tiles desc, longest desc, matching desc, difference asc); best = first
if best.longest < req: UNMATCHED
rival = first later candidate with dir ≠ best.dir or |a − best.a| > 2
if rival and rival.|C| ≥ best.|C| − 1 and rival.tiles ≥ best.tiles − 1 and rival.longest ≥ best.longest − 2
          and rival.matching ≥ best.matching − max(3, 3·best.tiles) and rival.difference ≤ best.difference + 0.75:
   UNMATCHED                                   // ambiguous (e.g. repeated blank bands): never invent a seam
return ADVANCED(overlap = H − best.a, dir, cols = best.C)
```
Performance note: the macOS build vectorizes `D` per offset (SIMD subtract, abs and strided row sums). On a 32 × 1340 sample a full transition takes about 0.1 s and must stay well under 1 s. Use SIMD on Windows.

**Fixed footer** (decided once, at the first forward advance): count rows from the bottom upward while `mean |prev − cur|` over the content columns is ≤ 2. `footer = count` if `count ≥ max(4, min(12, H/100))` and `count < overlap`, else 0.

**New rows** for a forward advance: `[overlap − footer, H − footer)`.
**Pixel columns** from sample columns: `lower = floor(lc · W / sw)`, `upper = ceil(uc · W / sw)`.

**Loop**:
```
SAMPLE_INTERVAL = 0.09 s ; SETTLE = 0.22 s ; FINISH_GRACE = 0.85 s
MAX_DURATION = 120 s ; MAX_STRIPS = 512 ; MAX_TOTAL_PIXELS = 60,000,000 ; MAX_RETAINED_PIXELS = 60,000,000
slices = [first]; prev = lastObserved = sample(first); hasUnmatched = false; pending = false
loop:
  if cancelled: return CANCELLED
  if finish requested (first time): finishAt = now; pending = true      // force one more frame
  if finishAt and (!pending or now − finishAt ≥ FINISH_GRACE): return complete(hasUnmatched ? PARTIAL : SUCCESS)
  if now − start ≥ MAX_DURATION or slices.count ≥ MAX_STRIPS: return complete(LIMITED)
  sleep until SAMPLE_INTERVAL since last frame
  frame → trim → sample (failure: return complete(PARTIAL))
  stable = stable(lastObserved, cur, contentCols); lastObserved = cur; if !stable: lastChange = now
  switch transition(prev, cur, contentCols):
    END: hasUnmatched = false
    FORWARD(overlap, cols):
       if contentCols unknown: map cols to pixels; detect footer;
            slices[0] = first cropped to pixel cols with footer rows removed at the bottom;
            if footer: footerSlice = cur rows [H − footer, H) at pixel cols
            contentCols = cols
       rows = [overlap − footer, H − footer)
       if total + rows.count > MAX_TOTAL_PIXELS / pixelCols.count: return complete(LIMITED)
       strip = copy of cur rows × pixel cols (own bitmap)
       if retained + strip.pixels > MAX_RETAINED_PIXELS: return complete(LIMITED)
       if footer and not first advance: footerSlice = cur bottom footer rows
       slices.append(strip); total += rows.count; prev = cur; hasUnmatched = false; report total
    BACKWARD: hasUnmatched = false          // prev stays the furthest accepted frame
    UNMATCHED: hasUnmatched = true
  pending = !stable or (now − lastChange) < SETTLE
complete(kind): stitch(slices + [footerSlice?]) → Capture(image, region.scale, region.anchorRect)
stitch: width = min slice width; stack top→bottom, each cropped to width from its left edge; nearest-neighbour
```

### 6.17 OCR text joining (Copy text from screen)
```
lines = recognized lines (text, minX, midY normalised, bottom-left origin), empty/whitespace-only dropped
sort: rowKey = (1 − midY)·50; if |rowKeyA − rowKeyB| ≥ 0.5 → by rowKey, else by minX
without "remove line breaks": join with "\n"
with it: trim each line; join with " ", except no separator when the previous text ends AND the next begins
         with a "tight script" character: U+3000–312F, U+3190–9FFF, U+F900–FAFF, U+FF01–FF9F, U+20000–2FA1F
         (CJK punctuation, kana, Han; Hangul deliberately excluded because Korean uses spaces)
```
Recognition: accurate with auto-detect, preferred languages from the app language (`pt-BR, tr-TR, ru-RU, es-ES, de-DE, fr-FR, it-IT, ja-JP, ko-KR, uk-UA, zh-Hans, zh-Hant`, each followed by `en-US`; anything else means `en-US`), filtered to supported ones. If nothing is found: fast, no auto-detect, same languages.

### 6.18 QR payloads
```
codes = detected (QR, MicroQR, Aztec, DataMatrix, PDF417) with non-empty string payload
sort by same rowKey rule on the code's box (midY, minX); drop whitespace-only; join "\n"
url = (codes.count == 1) ? openable(payload) : nil
openable(s): trimmed, non-empty, no whitespace inside, parses as URL, scheme ∈ {http, https}, host non-empty
```

### 6.19 Color formatting
```
r, g, b = clamp(sRGB components, 0, 1)
HEX:     "#%02X%02X%02X" of round(c·255)   (bare: without "#")
RGB:     "rgb(R, G, B)"  with round(c·255)
HSL:     max, min, Δ = max − min; L = (max + min)/2
         if Δ ≤ 0.000001: H = 0, S = 0
         else S = Δ / (1 − |2L − 1|);  H = 60·( max==r ? ((g − b)/Δ) mod 6 : max==g ? (b − r)/Δ + 2 : (r − g)/Δ + 4 ); if H < 0: H += 360
         "hsl(H, S%, L%)" with round(H), round(S·100), round(L·100)   (S clamped 0…1)
SwiftUI: "Color(red: %.3f, green: %.3f, blue: %.3f)"  (POSIX decimal point)
```
The sampled pixel's color is read in the capture's color space (the capture is configured as sRGB) and converted to sRGB.

### 6.20 File names and folders
```
defaultName = "<prefix> " + format(date, "yyyy-MM-dd 'at' HH.mm.ss", POSIX) + ".png"     // prefix "Screenshot"
tokens (Gregorian, local time), replaced in this order (longest first, plain string replacement):
   %year → 4-digit year      %month → English month name ("January")   %y  → 2-digit year
   %mo   → 2-digit month     %d     → 2-digit day                      %h  → 2-digit hour (00–23)
   %mi   → 2-digit minute    %s     → 2-digit second
number runs: regex %#+  → number zero-padded to (runLength − 1) digits (minimum 1), replaced from the end
fileName(pattern) = replace("/" and ":" with "-") → + ".png"
subfolder(pattern) = expand date tokens → split "/" → drop "", ".", ".." → join "/"
unique(name): name if free, else "base 2.ext", "base 3.ext", … "base 9999.ext" (then the original name)
number sequence: consume next → store next+1; rewind(consumed) only if stored next == consumed + 1
```
[Win: also replace `\ * ? " < > |`, strip trailing dots and spaces, avoid reserved device names (CON, NUL, COM1…), accept `\` as a subfolder separator, and keep paths under MAX_PATH unless long paths are enabled.]

### 6.21 Caps and pruning
- **Recent captures**: walk entries newest first, up to 12. Recordings are always kept. The first screenshot is always kept. A later screenshot is kept only if `bytesSoFar + size ≤ 256 MiB` (otherwise it's skipped, but the walk continues). Entries without a size reading are kept.
- **Copied files**: delete non-current files with mtime older than 24 h. From the rest, sorted newest first (ties by path), keep the current file plus files while `count < 100` and `bytes ≤ 256 MiB` (negative sizes count as over budget). Delete the others.

### 6.22 Window list filtering
- **Pickable**: normal-layer windows, opacity > 0.01, width and height ≥ 40 pt, front-to-back, excluding decoration windows and windows the policy forbids. Own window: pickable only if not hiding own windows **and** not a protected (workflow, or content during recording) window.
- **Decoration**: an *untitled* window W next to window X in z-order (immediately in front or behind), from a **different process**, whose margins `[X.minX − W.minX, X.minY − W.minY, W.maxX − X.maxX, W.maxY − X.maxY]` all lie within **1…32 pt** and differ from each other by **≤ 1 pt**.
- **Attached plan**: target T found in the on-screen list. Attached = windows **before** T in front-to-back order with the same owner process and a frame fully contained in T's frame. Draw order = T, then attached windows back to front. Optional accessibility confirmation (§3.5.3).
- **Layer coverage**: a buffer covers a frame if `|imgW − frame.w·scale| ≤ 1` and `|imgH − frame.h·scale| ≤ 1`. The composite scale is the largest display scale for which the clicked window's buffer covers its frame, else the finest display scale (recapture).
- **Composite placement**: each layer is drawn at `((f.minX − b.minX)·s, (b.maxY − f.maxY)·s, f.w·s, f.h·s)` in a bottom-left-origin canvas of size `b·s`.
- **Clipped single-window buffer**: rejected when `max(rx, ry)/min(rx, ry) > 1.08`, where `rx = imgW / frame.w` and `ry = imgH / frame.h`.

### 6.23 Clipboard image scale
```
h = pixelW / pointW ; v = pixelH / pointH        (all > 0, finite)
if |h − v| ≤ 0.05·max(h, v): scale = clamp-check((h + v)/2) within 0.5…4 else 1
else 1
```

### 6.24 Countdown ring
`progress = 1 − clamp(elapsed / 0.92, 0, 1)`. The arc spans `0.04 … 0.04 + 0.92·progress` of the circle, starting at 12 o'clock, and is redrawn at about 30 fps. The value is driven by time, so a delayed frame catches up instead of restarting.

---

## 7. macOS dependencies → Windows mapping

⚠ = no good equivalent, or a materially different behavior to design around.

| macOS API / mechanism | Used for | Windows equivalent | Notes |
|---|---|---|---|
| ScreenCaptureKit `SCScreenshotManager` + `SCContentFilter(display, excludingWindows)` | Display stills (frozen overlay, full screen, live captures, loupe snapshot) | **Windows.Graphics.Capture** (`GraphicsCaptureItem` from `HMONITOR` via `IGraphicsCaptureItemInterop::CreateForMonitor`, one frame from `Direct3D11CaptureFramePool`), or **DXGI Desktop Duplication** (`IDXGIOutputDuplication`, fast, gives cursor shape), or GDI `BitBlt` from the screen DC (simplest, slow at 4K) | Exclusion of the app's own windows is done **per window** with `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` (Win10 2004+). That excludes the window from **all** capture tools, including other apps' screen sharing. ⚠ It can't be decided per capture like SCK filters: toggle affinity on content windows around each capture, or briefly hide them. Workflow surfaces (overlays, HUD, preview) can carry the affinity permanently. WGC shows a yellow capture border on Windows 10. On Windows 11, `IsBorderRequired = false` needs `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` (packaged apps) or the newer capability. DDA can't apply exclusion itself, but affinity also applies to DDA and BitBlt. |
| SCK `showsCursor` | Include pointer | WGC `IsCursorCaptureEnabled`. DDA: draw the cursor shape yourself from `GetFramePointerShape`. GDI: `GetCursorInfo` + `DrawIconEx` | |
| SCK region config (`sourceRect`, output size) reused per frame | Scrolling capture frames every 90 ms | WGC monitor session kept open (free-threaded frame pool, take the latest frame, crop on GPU or CPU), or DDA `AcquireNextFrame` + crop | Keep one session for the whole scrolling run. DDA gives frames only on change, which suits polling. |
| Private `CGSHWCaptureWindowList` (window's own buffer, best resolution, ignores clip) + SCK desktop-independent window capture | Click-a-window capture of a single window, even if partly covered | WGC `CreateForWindow(hwnd)` (captures the occluded window content from DWM) or `PrintWindow(hwnd, PW_RENDERFULLCONTENT)` | ⚠ WGC window capture: Win10 yellow border, may include or exclude the DWM frame and shadow, and Windows 11 rounded corners may come back black or transparent (verify). `PrintWindow` fails for some GPU or UWP apps. Fallback: crop the window's extended frame bounds (`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`) from a monitor capture, which includes overlapping windows. |
| SCK `SCContentFilter(display, including: windows)` + alpha "packed vs placed" crop | Window plus attached sheets and dialogs (Composite A) | Enumerate **owned windows** of the target (`GetWindow(hwnd, GW_ENABLEDPOPUP)` / `GW_OWNER` relationships via `EnumWindows`), capture each with WGC or PrintWindow and composite at their positions, or crop the region from a monitor capture | Windows dialogs are separate top-level windows **owned** by the main window. "Same process + contained + in front" is a reasonable geometric rule on Windows too. The "packed" macOS 27 quirk doesn't apply. |
| `CGWindowListCopyWindowInfo` (z-order, bounds, layer, pid, alpha, title) | Pickable windows, decorations, attached plan | `EnumWindows` (top-level, z-order front→back) + `IsWindowVisible` + `DwmGetWindowAttribute(DWMWA_CLOAKED)` (skip cloaked: other virtual desktops, suspended UWP) + `DWMWA_EXTENDED_FRAME_BOUNDS` (visible bounds without the invisible resize border) + `GetWindowThreadProcessId` + `GetWindowTextLength` + `GWL_EXSTYLE` (skip `WS_EX_TOOLWINDOW`, `WS_EX_TRANSPARENT` with layered alpha 0, `WS_EX_NOREDIRECTIONBITMAP`) + `GetLayeredWindowAttributes` | "Layer 0" maps to non-topmost or topmost normal app windows. Exclude the shell (`Progman`, `WorkerW`, `Shell_TrayWnd`) unless you want taskbar picking. Consider optional child-element picking (like other Windows tools). |
| Accessibility (`AXUIElement` window subrole) | Confirm attached windows are dialogs, not standard windows | UI Automation (`IUIAutomation`, window pattern / `IsModal`), or simply the owner relationship | Optional. Owner relationships are more reliable on Windows. |
| `NSScreen` frames, `visibleFrame`, `backingScaleFactor`, `safeAreaInsets` | Overlay per display, scale, chrome inset | `EnumDisplayMonitors` + `GetMonitorInfo` (`rcMonitor`, `rcWork`) + `GetDpiForMonitor(MDT_EFFECTIVE_DPI)` → scale = dpi/96 | Declare **Per-Monitor V2** DPI awareness. ⚠ Fractional scales (125 %, 150 %, 175 %) and mixed-DPI layouts with negative virtual coordinates. |
| Borderless `NSPanel` at shielding level, non-activating, `canJoinAllSpaces`, `fullScreenAuxiliary` | Selection overlays, HUDs, preview, pins, QR panel | Layered popup windows: `WS_POPUP`, `WS_EX_TOPMOST \| WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE` (+ `WS_EX_TRANSPARENT` for click-through HUDs), `SetWindowPos(HWND_TOPMOST)` | ⚠ Covering the taskbar and other topmost windows (z-order among topmost windows, re-assert after showing). Can't cover the secure desktop (UAC), exclusive full-screen games or some full-screen video. ⚠ Keyboard focus for the overlay: a background tray app can only take the foreground when it has rights. A `WM_HOTKEY` grants them, while a tray click or timer may not, so use `AllowSetForegroundWindow` or a fallback low-level keyboard hook for Esc and Return. |
| `NSEvent` local and global monitors (keys, mouse) | Esc anywhere, pin ⌥-click recovery, QR and history click-outside dismissal | `SetWindowsHookEx(WH_KEYBOARD_LL / WH_MOUSE_LL)`, or `WM_ACTIVATE` / `WM_KILLFOCUS` for click-outside | Hooks must be unhooked promptly. Keep them only while needed (as macOS does). |
| `CGWarpMouseCursorPosition` | Loupe arrow-key nudge | `SetCursorPos` (physical pixels under PMv2) | Mouse-move messages may or may not follow, so refresh manually (as macOS does). |
| Carbon `RegisterEventHotKey` | All global shortcuts | `RegisterHotKey` (`MOD_CONTROL \| MOD_ALT \| MOD_SHIFT \| MOD_WIN \| MOD_NOREPEAT`) | ⚠ ⌃⌥⌘ defaults don't translate (see §8.4). `PrintScreen` is claimed by Snipping Tool on Windows 11 by default. Registration fails if another app owns the combination, so show the "rejected" message. |
| `NSPasteboard` (file URL + PNG + TIFF, source marker type, string) | Copy image, copy text, copy link, copy color, read clipboard image | Win32 clipboard: `CF_DIBV5` (with alpha) + registered `"PNG"` + `CF_HDROP` (file) in one `OpenClipboard` session; `CF_UNICODETEXT` for text. A custom registered format as the "source" marker. Optionally `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory` | Reading the clipboard image: prefer `CF_HDROP` image files, then `"PNG"`, then `CF_DIBV5`/`CF_DIB`. ⚠ Many Windows apps ignore PNG and take DIB (alpha may be lost). |
| Vision `VNRecognizeTextRequest` (accurate/fast, language correction, auto-detect, per-word boxes via ranges) | OCR tool, editor text selection, text-only blur, erase skipping | **Windows.Media.Ocr** `OcrEngine` (`TryCreateFromUserProfileLanguages` / `TryCreateFromLanguage`) → `OcrResult.Lines[].Words[]` with `BoundingRect` per word | ⚠ Lower accuracy, no accurate/fast levels, no language correction, no automatic language detection (one language per engine; run several or use user-profile languages), needs **installed OCR language packs**, and a max input dimension (`OcrEngine.MaxImageDimension`). Banding (§6.12) is still useful. Alternatives: Windows App SDK `TextRecognizer` (Copilot+ PCs only), Tesseract (bundled models). |
| Vision `VNDetectBarcodesRequest` (QR, MicroQR, Aztec, DataMatrix, PDF417) | QR detection | ⚠ No built-in screen barcode detector (the `Windows.Devices.PointOfService` scanner APIs need a device). Use **ZXing-C++ / ZXing.Net** (supports all five symbologies) | Third-party dependency and licensing. |
| `NSColorSampler` | Native color picker without permission | – | Not needed. Windows has no screen-capture permission, so the custom picker always works. |
| CoreGraphics (`CGContext` paths, even-odd clip, transparency layers, shadows, gradients, blend modes multiply and copy, interpolation none/medium/high) | Overlay drawing, editor canvas, export renderer, mosaics, erase patches | **Direct2D** (`ID2D1DeviceContext`: geometries, layers, Shadow and GaussianBlur effects, `D2D1_PRIMITIVE_BLEND_COPY`, Blend effect multiply) or **Skia** (SkiaSharp, or Skia C++: closest semantic match to CG including `kSrc`, `kMultiply`, drop-shadow image filter, clamp tile mode) or Win2D | ⚠ CG shadows ignore the CTM (device-space offsets), and flipped contexts are used throughout. Re-derive offsets so shadows fall downward. Use **one engine for both canvas and export** (the Mac code shares one renderer, so "what you see is what leaves"). |
| CoreImage `CIGaussianBlur` with `clampedToExtent` | Soft blur sample, backdrop blur | D2D GaussianBlur effect (`BorderMode = HARD` on a clamp-extended input), or Skia `ImageFilters::Blur` with `kClamp` | Match radius semantics: CI's radius ≈ σ. Verify visually. |
| ImageIO PNG with DPI properties; PNG read with DPI | All PNG output, latest-capture store, recent cache | **WIC** PNG encoder (`System.Image.HorizontalResolution` / `pHYs` via metadata writer), or libpng / stb | See the DPI convention decision (§6.15). |
| `NSBitmapImageRep` TIFF with point size | Clipboard TIFF | – | Not needed (DIBV5 + PNG). |
| `NSSavePanel`, `NSOpenPanel` | Save As, choose folder, choose backdrop or watermark image | `IFileSaveDialog`, `IFileOpenDialog` (`FOS_PICKFOLDERS` for folders) | |
| `NSSharingServicePicker` | System share sheet | `DataTransferManager` via `IDataTransferManagerInterop::ShowShareUIForWindow` with a `StorageFile` | Desktop (unpackaged) apps need the interop path. Completion is signaled via `TargetApplicationChosen`. |
| `NSWorkspace.desktopImageURL(for:)` | Wallpaper swatches | `IDesktopWallpaper::GetWallpaper(monitorID)` (or `SPI_GETDESKWALLPAPER`) | Spotlight or slideshow wallpapers may return transcoded cache paths. |
| `FileManager.trashItem` | Preview discard of a saved file | `IFileOperation` with `FOFX_RECYCLEONDELETE` / `SHFileOperation(FO_DELETE, FOF_ALLOWUNDO)` | Recycle Bin, not a permanent delete. |
| `setxattr com.apple.metadata:kMDItemIsScreenCapture` | Mark saved screenshots | – | Drop. |
| `NSWorkspace.didWakeNotification` | Refresh link expiry list | `WM_POWERBROADCAST` / `PBT_APMRESUMEAUTOMATIC` (or `RegisterSuspendResumeNotification`) | |
| `NSWorkspace.didActivateApplicationNotification` | Hide history palette when another app activates | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` | |
| `URLSession` ephemeral | Share upload and delete | `HttpClient` / WinHTTP with cookies, cache and credentials disabled, 75 s / 90 s timeouts | TLS only (https). |
| Caches and Application Support dirs, POSIX 0600/0700 | Caches, records | `%LOCALAPPDATA%\Vorssaint\…`; default per-user ACLs (optionally tighten); DPAPI (`CryptProtectData`) for delete tokens | Symlink and junction checks become reparse-point checks. |
| `NSAlert` | Discard confirmation, clear history | TaskDialog / ContentDialog | |
| `NSCursor` crosshair, openHand, iBeam, arrow | Overlay and canvas cursors | `IDC_CROSS`, a custom open-hand cursor (or `IDC_HAND` / `IDC_SIZEALL`), `IDC_IBEAM`, `IDC_ARROW` | |
| Activation policy accessory ↔ regular | Dock icon while editors are open | Editor windows are normal top-level windows with a taskbar button. The tray app stays windowless otherwise. | |
| Spaces (`canJoinAllSpaces`, `moveToActiveSpace`) | Pins and HUDs on all desktops | ⚠ No public API to pin a window to all virtual desktops (`IVirtualDesktopManager` can only query and move). Use tool windows (`WS_EX_TOOLWINDOW` windows tend to show on all desktops) or accept per-desktop pins. | |
| SwiftUI materials (`.regularMaterial`), HUD contrast plates | Chrome backgrounds | Mica/Acrylic (`DWMWA_SYSTEMBACKDROP_TYPE`, Win11) for windows. For in-overlay chrome, draw your own translucent fill (the overlay is opaque when frozen). | |
| Forced dark appearance (editor) | Editor | `DWMWA_USE_IMMERSIVE_DARK_MODE` plus a dark theme for the UI stack | |
| System font semibold, Apple Color Emoji | Text annotations, counters, watermark text, stickers | DirectWrite: Segoe UI Variable / Segoe UI Semibold; **Segoe UI Emoji** with color-font drawing (`D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT`) | ⚠ Different metrics and look. Text boxes must be measured with the same engine that draws. |
| `AVAssetImageGenerator` (recording thumbnails in history) | Recent captures (recordings) | Media Foundation source reader, or `StorageFile.GetThumbnailAsync` | Recorder spec. |
| Trackpad pinch (`MagnificationGesture`), `hasPreciseScrollingDeltas`, fixed-point wheel deltas | Editor zoom, loupe zoom modes | Ctrl+wheel (precision touchpads send pinch as Ctrl+wheel), `WM_MOUSEWHEEL` deltas (120 per notch; smaller deltas mean high-resolution or touchpad), `WM_GESTURE` / DirectManipulation for true pinch | Treat "delta not a multiple of 120" as continuous. |
| Keyboard layout tables (TIS), Caps Lock state | Layout-aware tool digits, "typed character" matching | `ToUnicodeEx` / `MapVirtualKeyEx` with `GetKeyboardLayout`, `WM_CHAR`, `GetKeyState(VK_CAPITAL)` | Windows VK codes are already layout-mapped for letters, but the digit row on AZERTY still has the same VK_1…VK_9 problem. |
| Notch "Dynamic Island" | Chooser controls, embedded preview, history rail, mascot flash | – | [Mac-only] Drop. |
| Screen Recording / Accessibility permissions (TCC) | Gating, permission rows, "needs permission" badges | – | Not needed (no desktop-capture permission on Windows). Keep the code path for WGC borderless access if used. |

---

## 8. Porting notes

### 8.1 What's Mac-specific and can be dropped or simplified
- The notch chooser variant, embedded preview, island history rail and mascot flash (§3.4.14, §3.9.6).
- Screen Recording and Accessibility permission flows, the native color-sampler fallback, and the "needs permission" captions.
- The Spotlight "is screen capture" xattr.
- macOS 27 "packed" window-capture crop heuristics (§3.5.3 Composite A alpha test).
- Old-shortcut migrations (`unified…`, `restored…`, `orphaned…`, `screenshotOpenEditorDirectly`).
- TIFF clipboard representation. The DIB/PNG/HDROP trio replaces it.
- `@Nx` integer labels. Show the fractional scale instead.
- Tooltips are suppressed on macOS 27 because of an OS bug, which is irrelevant on Windows. Show all tooltips.

### 8.2 Things that must not be lost
- **Shared renderer**: canvas and export must use the same drawing code and constants (§6). Blur, pixelate and erase areas use **replace** blending from **base-image samples**, and text-only areas cover the whole area until OCR finishes. These are privacy guarantees.
- **Direct outputs are raw** (§3.7). Backdrops and watermarks belong to editor exports only.
- **Esc never deletes** in the preview. Discard moves a saved file to the Recycle Bin and gives back its `%#` number.
- **Withheld latest capture**: never publish via the upload shortcut a capture that was discarded or opened in an editor, even across relaunches.
- **Link lifecycle**: preflight the records store before uploading, revoke links whose UI went away, and never keep a link without its delete token.
- **Session safety**: only one capture surface at a time, inert overlays once a capture is pending, generation counters on every async result (frozen refresh, live loupe, OCR, auto-copy, uploads).
- **Recent-capture store robustness**: never wipe history on a read error. Orphan cleanup only touches app-named files.
- **Scrolling stitcher**: "never invent a seam". Ambiguity means unmatched, and the completed part is kept.

### 8.3 Biggest risks
1. **Window capture fidelity and own-window exclusion.** macOS uses a private window-server call for crisp, occlusion-proof window buffers, plus per-capture exclusion filters and composited attached sheets. On Windows, WGC window capture has a border (Win10) or a consent requirement (borderless on Win11), DWM frame, shadow and rounded-corner quirks, no owned popups, and PrintWindow is unreliable. `WDA_EXCLUDEFROMCAPTURE` is a global per-window flag that also hides editors and pins from other apps' screen sharing.
2. **Mixed and fractional DPI multi-monitor geometry.** The Mac logic assumes per-display integer scales and simple point↔pixel math. Windows needs Per-Monitor-V2 throughout (overlays per monitor at native resolution, virtual-desktop negative coordinates, windows spanning monitors with different DPI). Re-validate the 1-device-pixel nudge, size badges, `× scale` stroke weights, the 1x downscale and the PNG DPI convention for 1.25, 1.5 and 1.75 scales.
3. **OCR and QR parity.** Windows.Media.Ocr is weaker and language-pack dependent, with no auto-detection and no accurate mode. The editor's "Text only" blur, erase-around-text and word selection all depend on word boxes. QR needs a third-party library (ZXing).
4. **Overlay z-order and focus on Windows.** Drawing above the taskbar and other topmost windows, getting keyboard focus from a tray process (foreground lock), handling Esc reliably, UAC and secure desktop, exclusive full-screen apps, virtual desktops (no public "all desktops" for pins), and non-activating previews that still take focus on click.
5. **Rendering parity of the editor.** Many exact constants depend on CoreGraphics semantics (flipped contexts, device-space shadows, transparency layers, copy and multiply blends, nearest-neighbor mosaics, CI blur radius) and on Apple fonts and emoji. Choosing Skia or Direct2D early and building a golden-image test set from the Mac build is essential. Scrolling-capture matching also needs SIMD to keep each match well under 1 s.

### 8.4 Open product decisions [Win decision]
- **Default global shortcuts.** The ⌃⌥⌘ layer has no clean Windows twin (Win-key combos are largely reserved, and Ctrl+Alt is AltGr on many layouts). Suggestions to validate: chooser = `PrintScreen` (if the user disables Snipping Tool's claim) or `Ctrl+Shift+S` / `Ctrl+Alt+Shift+4`, full screen = `Ctrl+PrintScreen`, and so on. Keep everything **off by default**, as on macOS.
- Editor modifiers: ⌘ → Ctrl (Ctrl+C, Ctrl+S, Ctrl+Shift+S, Ctrl+Z, Ctrl+Shift+Z **and** Ctrl+Y, Ctrl+P, Ctrl+Delete, Ctrl+0, Ctrl+1, Ctrl+=, Ctrl+−), ⌃+scroll → Ctrl+wheel, ⌥ → Alt (from-center drag, loupe mode swap, pin recovery click), ⇧ unchanged. Note Ctrl+P is "print" by convention on Windows.
- Default save folder: Desktop (macOS parity) vs `Pictures\Screenshots` (Windows convention).
- PNG DPI base: 96 × scale (Windows) vs 72 × scale (macOS parity) (§6.15).
- Whether to offer a JPEG option (macOS has PNG only, with no quality setting) and aspect-ratio backdrop presets (macOS has none). The spec reflects the macOS feature set: **PNG only, uniform padding**.
- Whether the temporary-link service (`screenshots.vorssaint.com`) accepts Windows clients as-is. The API is client-agnostic (plain PNG POST), but confirm abuse limits and the privacy text wording ("this PC").

### 8.5 Suggested implementation order
1. **Pure core with tests** (no UI): coordinate and selection math, file naming, tokens and number sequence, annotation model, hit testing, arrow and pen geometry, backdrop and watermark codecs and math, color formatting, recent-capture caps, copied-file pruning, OCR banding, merge and runs, scrolling matcher. The macOS tests are a ready-made acceptance list (see Appendix B).
2. **Capture engine**: monitor stills with own-window exclusion, window list and picking, window capture with fallbacks.
3. **Selector**: per-monitor overlays, frozen and live, drag, click and Return, size badge, window highlight, hint bar with mode palette, keyboard map, last region, loupe.
4. **Routing and outputs**: Save (folder, subfolder, pattern, unique), Copy (clipboard trio plus cache), toasts, auto copy, default actions.
5. **Quick preview** with placement, timers, drag-out, discard, and system share.
6. **Editor**: shared renderer first (export equals canvas), then tools, selection and handles, undo, text editing, crop, style bar, backdrop, watermark, zoom, tool order and shortcuts.
7. **OCR stack**: editor word selection, text-only blur, erase skipping, then the OCR tool. Then QR (library), the QR panel and the color picker.
8. **Pins, Recent captures, latest-capture and clipboard-image shortcuts.**
9. **Scrolling capture.**
10. **Temporary links** (client, records, preview and editor flows, Settings list, Privacy sheet).
11. **Settings page** and backups.

---

## Appendix A: English UI strings (verbatim)

**Screenshot feature**: Screenshot · Capture now · Capture an area, window or the whole screen · Drag to select an area · Click a window to capture it · Freeze the screen while selecting · The picture stops while you choose the area, so nothing moves away or changes during the selection. · Save to · Choose… · Subfolder pattern · File name · Starts at · Reset · Next: %d · Delay · Off · %d s · Include the pointer · Save at 1x size · Retina captures are saved at half their pixel size, which makes smaller files. · Editor tools · Use tool shortcuts · Click a shortcut to record a key. Digits 1–9 move the tool. Delete clears its shortcut. · This key belongs to the editor.

**Tools and styles**: Select · Arrow · Arrow style · Solid · Outline · Open · Double-ended · Scribbly · Line · Rectangle · Ellipse · Pen · Highlighter · Text · Sticker · Number · Pixelate · Solid block · Crop · (text placeholder) Text · (crop apply) Crop · Cancel · Color · Thickness · Font size · Blur strength · Blur · Blur style · Erase · Text only · Shadows · Background · None · Bring forward · Send backward · Drag and drop

**Actions**: Edit · Copy · Save · Save As… · Pin to screen · Add to Shelf · Share · Discard · Done · Discard this screenshot? · It was not copied or saved yet.

**After capture**: Default action · Runs automatically right after capture. · Ask each time · Save & Copy · Show confirmation preview · Confirmation duration · Until dismissed · After a successful automatic action, keep the preview available for editing or discarding. · Copy to the clipboard automatically · Every capture goes to the clipboard as soon as it is taken, ready to paste. Saving a file stays a separate choice. · Add to the shelf automatically · Every capture also goes to the shelf, ready to drag into any app. One that opens in the editor goes there with Add to Shelf. Works while the shelf is on. · Preview position · Automatic · Top left · Top right · Bottom left · Bottom right · Focus the preview automatically · Shortcuts work the moment the preview appears, but the keyboard leaves the app you were using until it closes. · Hide Vorssaint windows · Show the last capture outline

**Magnifier**: Start selection with the magnifier on · Remember the magnifier's last zoom · Default magnifier zoom · Wheel zoom · Fast · Step by step · Hold ⌥ to temporarily use the other mode.

**Pins**: Opacity · Ignore clicks · Close · Close all pins

**Background**: Margin · Wallpaper · Image… · Solid · Gradient · Corners · Blur · Save background · Remove · Custom

**Watermark**: Watermark · Image · Watermark text · Position · Size · Opacity · Rotation · Save watermark · Red · Orange · Yellow · Green · Blue · Purple · Black · White · Top left · Top center · Top right · Center left · Center · Center right · Bottom left · Bottom center · Bottom right

**Scrolling and other shortcuts**: Scrolling capture · Scrolling screenshot · Scroll with your mouse or trackpad. Press Enter or choose Done. · S toggles scrolling · Scrolling on · Drag only the part of the page that moves. · Capture the whole screen · Full screen · Edit latest screenshot · Edit clipboard image · Upload latest screenshot · Show capture menu when using keyboard shortcut · Screen capture

**Temporary links**: Temporary links · Allow temporary links · Default link expiry · Choose 1, 6 or 24 hours when sharing. The image is deleted automatically. · For 1 hour · For 6 hours · For 24 hours · Shared links · No active links · Expires · Delete now · Open · Copy link · Privacy · Privacy for temporary links · (three privacy paragraphs, §3.14.8)

**Recent captures**: Recent captures · Take a screenshot or save a recording to find it here. · Screenshot · Recording · Restore · Open · Remove from history · Clear history

**OCR, QR and color**: Copy text from screen · Select an area of the screen and the recognized text is copied, ready to paste. · Text copied · No text found · Remove line breaks · Removes line breaks so copied text pastes as one paragraph. · Read QR codes · If the area has a QR code, its content is shown to copy or open. · QR code copied · QR code · Copy · Open link · Color picker · Grab the color of any pixel on screen and copy it in your favorite format. · Copied format · Copy without the # prefix · Pick color · (formats) HEX · RGB · HSL · SwiftUI

**Chooser (recorder-owned labels shown in it)**: Screen recording · Choose what to record · Mac sound · Microphone · The screen could not be recorded

**Shortcut field messages (general)**: Press keys · None · Reset · Nothing was captured. macOS or another app already uses that combination. Try another one. · This shortcut is already used by %@. · macOS rejected this shortcut. Choose another one.

(Toast messages: §3.19.)

## Appendix B: Acceptance checklist distilled from the macOS tests

The macOS test suite asserts these behaviors. Each is a ready-made test case for the port:

- Selection: a drag in any direction gives a positive rect; ⇧ makes it square; ⌥ grows it from the center; under 4 pt is a click; the frontmost window wins a click; a click outside windows picks nothing; a session that's over ignores late events; a pending capture ignores input.
- Chooser: digit keys select the same modes the palette shows; uninstalled modes are hidden and the order is stable; switching to a mode with a different policy re-photographs, while same-policy modes reuse pixels; a stale or failed refresh never resumes on old pixels and closes safely; the full-screen action shows only for Screenshot, on the pointer display, and disappears when selection starts; the repeat hint shows only for a stored display that still exists and never in Color mode; Return captures the full display (Screenshot and Text), returns a whole-display region (Recording), or confirms the color (Color); the recording audio row reserves its height.
- Loupe: odd sample sides; grid only when cells ≥ 6 pt; wheel up zooms in; every stepped notch changes exactly one level and is reversible; fast mode on a plain wheel crosses the range in a few notches; ⌥ swaps modes; zoom is clamped to 0.5…4.33; a nudge is one device pixel (10 with ⇧); the highlight marks exactly the pixel the picker copies.
- Crop: edges round to the nearest pixel (not outward); moving never changes size; a drag inside a full-image draft starts a new selection; the crop loupe centers on the edge (even side).
- Editor taps: 7-pt threshold; closed pen strokes survive; a non-pen draft ending near its start is discarded; shapes are selected only when their drag ends; tapping an existing mark with a creation tool syncs controls and selects it.
- Styles: a selection only exposes the controls it uses; picking a highlight never records a thickness change; text uses its own point size; stored sizes are clamped; size buttons step and stop at the ends.
- Counters renumber without gaps after delete and reorder; layer moves swap with the neighbor and stop at the ends.
- Arrows: heads open behind the tip; a short arrow's head never passes its tail; the filled silhouette has no holes at the tail cap or head junction; five styles, with unknown styles falling back to solid; scribbly arrows are stable per seed.
- Blur: each level coarsens the mosaic (level 3 equals the legacy strength); a new capture never starts below level 3; only used levels keep samples; mixed levels export correctly; text-only covers words, or the whole area before OCR; erase reproduces flat backgrounds and gradients; translucent captures never leak glyphs; noise is ≤ 9 levels per sample; the erase cache is reused and invalidated correctly.
- OCR banding: overlapping bands, owned rows meet exactly, seam duplicates are kept once, line order is preserved, a failed band makes the runs nil.
- Backdrop and watermark: padding floor of 24 px; margin slider growth; corner radius scaling; blur scaling; JSON round-trip and sanitization (bad values demote to none); presets capped at 12; watermark placement against margins (including rotated bounds and shrink-to-fit); a missing picture draws nothing; text capped at 120.
- Export: Retina export keeps pixels and density (PNG 144 DPI); 1x halves the pixels and reports 72 DPI; TIFF density matches.
- Files: dated, colon-free PNG names; unique numbering; date and number tokens; slashes and colons become dashes; subfolders can't escape; copied-file pruning (24 h, 100 files, 256 MB) never evicts the published file; drag temp directories are unique and cleaned only when app-named.
- Preview: confirmation timing matrix (§3.7); focus policy (timed + preference → focus, persistent → none); Esc dismisses without discarding; failed Copy keeps the preview; hover pauses dismissal; an open share sheet pauses dismissal; Edit opens exactly once after the preview closes.
- Sharing: durations limited to 1, 6 and 24 h (invalid means 1 h); upload requires shortcut and sharing on; ids of 32 chars and tokens of 43 chars are validated; the expiry bound is ≤ 24 h 5 min; a developer endpoint is accepted only in developer builds and only over HTTPS; a closed preview or editor revokes undelivered links; a withheld latest capture is never published (including after relaunch); copy retries reuse the existing link.
- Recent captures: 12 entries and 256 MB, keeping the newest screenshot; a corrupt or missing index never authorizes deletion; orphan cleanup never follows symlinks; clearing keeps recording files.
- Scrolling: completes without input events; exact overlap at Retina heights in under 1 s; Done without scrolling returns the first frame; unmatched keeps the first frame or the completed part; a later failure keeps the stitched part; back-and-forth never duplicates; fixed footer and side columns are handled; transparent padding never becomes a seam; Cancel never delivers.
