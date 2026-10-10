# Spec 07: Shelf, Radial Menu, Scratchpad, Cleaning Mode, Camera Preview, Media Tools, Input Fixes, AI Agent Usage, Wallpaper, Dynamic Island

**Purpose.** A functional specification for re-implementing these Vorssaint features on Windows without reading the Swift code. It is stack-neutral: it says what the features do and what constants they use, and suggests Windows mechanisms, but it does not prescribe a UI framework.

**Source baseline.** Repository `vorssaint-utils`, branch `main`, commit `b6d8d2db` (2026-10). The source is Swift/SwiftUI/AppKit. File and symbol names are cited in §6 so an implementer can verify a number when in doubt. Behavior marked *(inferred)* follows from the code or the platform, but no comment or test states it.

**Structure.**
1. Overview
2. Feature inventory (checklist)
3. Detailed behavior per feature
4. Settings tables
5. Data and files
6. Algorithms and constants
7. macOS dependencies → Windows mapping
8. Porting notes: drop list, risks, implementation order

---

## 1. Overview

### 1.1 Scope

| # | Area | One-line description | Depth |
|---|---|---|---|
| 1 | **Shelf** | A temporary holding area. Park files, images, text and links near the cursor while dragging, then drag them out, share or open them later. | Full |
| 2 | **Radial menu** | A wheel of apps, files, links, shortcuts and app tools that appears around the pointer. Supports profiles and submenus. | Full |
| 3 | **Scratchpad** | A floating, always-on-top notes window with up to 12 tabs. Autosaved plain text, Markdown formatting helpers, Markdown preview, export to .txt. | Full |
| 4 | **Cleaning Mode** | Locks the keyboard, scrolling and system keys so the keyboard can be wiped. Shows a black screen or a small indicator; unlock with Esc ×5. | Full |
| 5 | **Camera preview** | A small floating, mirrored live camera view for checking yourself before a call. | Full |
| 6 | **Media tools** | A local workspace with four tools: compress or trim video, make GIFs, batch-convert images (resize, watermark, rename, PDF), and extract text (OCR). | Full |
| 7 | **Input fixes** | Extra click filter (mouse debounce), key debounce, mouse button shortcuts (extra buttons, side wheel, button-drag gestures), smooth scrolling, scroll-direction inverter (mouse only), Super key (hyper key with tap action), quit/close protection, and per-feature "apps to leave alone". | Full |
| 8 | **AI agent usage** | Reads local logs of Claude Code, Codex, OpenCode and GitHub Copilot CLI. Prices usage, tracks plan limits and resets, archives progress, and announces long tasks finishing. | Full |
| 9 | **Wallpaper picker** | A gallery of the system's own still wallpapers plus the user's own images and folders, applied to one or all displays. | Brief |
| 10 | **Dynamic Island** | A top-center overlay that hosts live activities and pages (music, timer, downloads, calendar, notifications, agents…). | High level |

### 1.2 Conventions used in this document

- **Units.** "pt" (macOS points) map 1:1 to Windows DIPs (device-independent pixels at 96 DPI = 100%). Scale every pt/DIP constant by the monitor's DPI scale; the app must be Per-Monitor-V2 DPI aware. "px" means real image pixels: media output sizes, watermark margins, and the smooth-scroll budget. Times are in ms or s.
- **Coordinates.** macOS screen coordinates grow upward and Windows coordinates grow downward. Positions here are written in neutral terms ("16 DIP below the pointer", "18 DIP above the bottom of the work area"). Where a formula is given, it is in Windows (y-down) form unless marked otherwise.
- **Modifier names.** ⌘ Command, ⌥ Option, ⌃ Control, ⇧ Shift. App-level ⌘ shortcuts map to Ctrl on Windows (⌘T → Ctrl+T). The macOS "free layer" ⌃⌥⌘ used for most global defaults has no clean equivalent; §8.4 recommends **Ctrl+Alt+Shift+key**.
- **"Work area" / "visible frame".** The monitor rectangle minus the menu bar and Dock on macOS, or minus the taskbar on Windows (`MONITORINFO.rcWork`).
- **Settings keys.** Every setting is listed by its raw key. Keeping the same raw keys on Windows makes cross-platform backup import possible (§8.5).

### 1.3 Shared infrastructure these features rely on

These cross-cutting mechanisms are specified by other specs; they are summarized here because every feature below depends on them.

1. **Feature availability layer** ("Features hub").
   - Each feature has an availability flag `featureAvailable.<id>` that sits above its own enable switch. An unavailable feature disappears from Settings, the tray panel and the menu, and its service tears down without starting. Making it available again restores its saved choices.
   - A **clean install** starts from the "Essential" preset (only the mixer, Keep Awake and the system monitors). Every feature in this spec therefore starts *uninstalled* on a fresh install.
   - **Upgraders** with no saved choice get the per-feature `installedByDefault` value:
     - true for shelf, radialMenu, scratchpad, cleaningMode, cameraPreview, mediaTools, mouseClickDebounce, keyboardDebounce, mouseButtonShortcuts, smoothScroll, scrollInverter, mouseNavigation, mouseAcceleration, superKey, quitWindowProtection, notch and all notch extensions except the mascot;
     - false for linearScroll, scrollHorizontal, wallpaper and notchMascot.
2. **Settings store.**
   - A key/value store with registered defaults.
   - Settings backup export/import, after which the app relaunches. Some keys are explicitly excluded from backups (noted per feature).
3. **Global shortcuts.**
   - Stored as the string `"<mods>:<keyCode>"`. `<mods>` is a subset of `control`, `option`, `shift`, `command`, joined by `+` in that fixed order; `<keyCode>` is a **macOS virtual key code**.
   - A shortcut must contain ⌃, ⌥ or ⌘ with a printable key, or be a standalone function key.
   - Registered through the Carbon hot-key API. When registration fails, the UI shows the orange text "macOS rejected this shortcut. Choose another one." (Windows: "This shortcut is unavailable. Choose another one.").
   - Windows: `RegisterHotKey` with `MOD_NOREPEAT`, with storage in a Windows-native format (VK + modifiers); see §8.5 for migrating macOS key codes.
4. **Private file store.**
   - `~/Library/Application Support/<bundle id>/`, where the bundle id is `com.vorssaint.utils` (`.dev` for development builds).
   - Directories mode 0700, files 0600, permissions reapplied on every atomic write.
   - Windows: `%LOCALAPPDATA%\Vorssaint\`. The default profile ACL is already user-only; optionally tighten to an owner-only DACL. Write to a temp file, then `ReplaceFileW` / `MoveFileExW(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)`.
5. **Input event core.**
   - All input fixes, Cleaning Mode, the Shelf's drag detection, the radial menu's mouse trigger and the island's volume keys use low-level event interception (macOS CGEventTaps). These need the Accessibility permission.
   - They are gated by "the login session is on the console", re-armed after the system disables a slow tap, and identify the app's own synthetic events by tags:
     - `0x564F5253` "VORS": smooth-scroll frames, replayed mouse presses, text-snippet typing;
     - `0x564F5248` "VORH": wheel events redirected to horizontal;
     - `0x5652535341494E54` "VRSSAINT": Quit Protection re-posts.
   - Windows: a single low-level hook thread (WH_KEYBOARD_LL + WH_MOUSE_LL) running an ordered internal pipeline; see §3.7.0 and §7.7.
6. **Overlay windows.**
   - Shelf panels, the radial wheel, Scratchpad, the camera mirror, cleaning overlays, HUDs and the island are borderless, non-activating panels. They are visible on all Spaces and over full-screen apps, kept out of window cycling, and expose an accessibility subrole that window managers ignore (a test asserts every overlay uses this base class).
   - Windows: `WS_POPUP` + `WS_EX_TOOLWINDOW` + `WS_EX_NOACTIVATE` (when they must not take focus) + `WS_EX_TOPMOST`, shown with `SW_SHOWNOACTIVATE`.
7. **Secondary launch surfaces.** These are specified elsewhere; this spec only lists which items exist and their ids.
   - Menu-bar panel (Windows: tray flyout), with per-row visibility keys `panelControl*` / `panelUtility*`.
   - Quick Launcher tiles.
   - Command Bar actions and toggles.
   - Radial-menu tool slices.
   - Launching from these surfaces typically waits 0.1–0.15 s after the surface hides.
8. **QuickToolHUD.**
   - A transient pill at the top center of the pointer's screen, 24 pt below the top.
   - Shown 1.5 s; fade in 0.12 s, fade out 0.22 s.
   - Used for "Shelf", "The file no longer exists", Scratchpad save errors, and similar messages.

### 1.4 Porting stance in one paragraph

Most of this area ports to Windows as direct re-implementations with public APIs: Scratchpad, Camera preview, Media tools (with a bundled FFmpeg recommended), the Radial menu, Agent usage, Quit protection, click/key debounce, button shortcuts and the Wallpaper picker. Some features already exist natively on Windows and should be dropped or reduced: side-button Back/Forward, linear wheel lines and touchpad scroll direction. The hard problems are:
- (a) detecting a system-wide drag for the Shelf; Windows has no global drag notification;
- (b) the Super key, which must inject real modifier key events because Windows messages carry no modifier flags, and whose ⌃⌥⇧⌘ default collides with the Windows "Office key";
- (c) telling a mouse wheel from a Precision Touchpad in a low-level hook, needed for smooth scrolling and inversion;
- (d) Cleaning Mode cannot block Ctrl+Alt+Del, Win+L or firmware keys;
- (e) the Dynamic Island loses its "free" menu-bar real estate and collides with title bars and Snap Layouts at the top center.

§8 ranks these risks and proposes an implementation order.

---

## 2. Feature inventory (checklist)

Each line is a user-visible capability with a Windows verdict tag:

| Tag | Meaning |
|---|---|
| **[P]** | Port as is (direct re-implementation) |
| **[R]** | Port with a redesign (mechanism differs; behavior to be decided) |
| **[N]** | Already native on Windows; drop or keep as a thin wrapper |
| **[D]** | Drop (macOS-only) |
| **[L]** | Later or optional |

### 2.1 Shelf
- [ ] [P] Hub feature plus master switch "Temporary area for dragging files" (off by default).
- [ ] [P] Global toggle shortcut (default ⌃⌥⌘D → suggest Ctrl+Alt+Shift+D); shows a "rejected" message when registration fails.
- [ ] [R] "Add the file manager selection with the shortcut". Reads the Explorer selection via `IShellWindows`; no consent prompt needed.
- [ ] [R] Shake the mouse during a content drag to summon the shelf at the pointer: ≥ 3 reversals and > 220 DIP of travel within 0.5 s, 1 s cooldown.
- [ ] [R] Docked drop zone near the tray icon or at the top center. Appears during any content drag; a 150 ms dwell expands it to the card; it stays as a pill with a count while items exist.
- [ ] [R] Edge peek. A drag dwelling 150 ms within 200 DIP of an outer left or right edge shows one third of the card; it retreats beyond 330 DIP.
- [ ] [P] Other entry points: Settings "Open now", tray menu "Open shelf", tray panel row, Command Bar "Shelf" and "Keep it on the shelf", radial "Shelf" slice.
- [ ] [P] Card UI. 304 DIP wide, tile grid with 3 columns, header (title/selection count, leaf badge, keep-open pin, close), footer (hint, Share, Clear/Remove selected), empty state "Drag items here". Auto-hides only while empty (5 s, then a 0.22 s fade). Movable.
- [ ] [P] Accepts files and folders (by reference), virtual files (copied), GIF and image data (stored as GIF/PNG), links, and plain text up to 200,000 characters.
- [ ] [P] Piles: a multi-item drop becomes one pile; dropping onto a tile merges; dragging a tile onto a tile stacks. Expand/collapse, count badge, "first +N" title.
- [ ] [P] Multi-selection: click, Shift-range, Ctrl+A, Esc.
- [ ] [P] Drag-out of a tile, a pile or the selection, with a stacked drag image. After an accepted drop: remove the dragged items (default on; pinned items stay) and optionally close.
- [ ] [P] Per-item pins; hover ✕ to remove; context menu Pin/Open/Open With/Edit (screenshot editor)/Share/Show in Explorer.
- [ ] [R] Share via Windows Share (DataTransferManager; Nearby Share instead of AirDrop).
- [ ] [P] Tooltips after 1 s.
- [ ] [P] Real thumbnails for images and videos.
- [ ] [R] Healing of moved files (bookmark → file ID / shell link). Items on unmounted drives are kept.
- [ ] [P] Persistence across restarts, capped at 200 leaves.
- [ ] [P] "Automatic exceptions": apps whose drags never auto-open the shelf.
- [ ] [P] Virtual-file alerts: "Couldn't add attachment", "Shelf is full".
- [ ] [P] Integrations: screenshot auto-add and "Add to Shelf" in the editor, Command Bar, radial menu, island Files page.

### 2.2 Radial menu
- [ ] [P] Master switch "Use the radial menu" (off by default); multiple profiles, each with a name, one of 12 colors, items and triggers.
- [ ] [P] Per-profile global shortcut (first profile ⌃⌥⌘Space → suggest Ctrl+Alt+Shift+Space).
- [ ] [R] Per-profile mouse-button trigger. Back/Forward (XBUTTON1/2) are reliable; buttons 6+ only through vendor software or HID.
- [ ] [L] Four-finger trackpad tap trigger. Windows has no public API; map it through Windows' own touchpad gesture settings.
- [ ] [P] Opening behaviors: "Press or hold" (default), "Press to open", "Hold to select". Placement at the pointer or at the screen center.
- [ ] [P] Up to 12 slices per wheel, selected by direction (40 DIP dead zone, 8 DIP arming travel, no outer limit). Release, click, Enter or digits 1–9 run a slice; arrow keys rotate; Esc steps back or closes.
- [ ] [P] One submenu level.
- [ ] [P] Item kinds: app, file/folder, link, keyboard shortcut, Vorssaint tool (15), Quick Toggle (8), Window Layout (41), media key (3), Now Playing card, submenu.
- [ ] [P] Automatic labels and icons, a custom name, a custom symbol (92-symbol grid; needs a Fluent icon mapping), website favicon fetch.
- [ ] [R] Now Playing slice and card via GlobalSystemMediaTransportControlsSessionManager.
- [ ] [P] Animations (fade/scale, chip bloom, sweeping wedge); Reduce Motion support.
- [ ] [P] Settings: profiles from 6 presets, duplicate/delete/rename/color, shortcut recorder with conflict detection, mouse-button picker with a live "Button test", visual canvas editor (hover, click to edit, drag to swap, context menu), list editor, item editor sheet, "Try it", "Reset".

### 2.3 Scratchpad
- [ ] [P] Floating, always-on-top, resizable pad (380×300 initial, 280×220 minimum), centered at 42% from the top of the pointer's monitor; position remembered for the session.
- [ ] [P] Up to 12 tabs: auto names "Scratchpad N", rename (40 characters), close with confirmation when non-empty. The last tab can't be closed.
- [ ] [P] Autosave 0.8 s after typing plus immediate saves; atomic write with read-back verification; never overwrites unreadable notes; save-failure banner and HUD.
- [ ] [P] Auto-clear after a day, week or month without edits (checked when the pad opens).
- [ ] [P] Plain-text editor (no smart substitutions), text size 10–22, undo/redo, find (Ctrl+F, F3), move lines (Alt+Up/Down).
- [ ] [P] Nine Markdown helper marks, each a toggle: bold, italic, strikethrough, heading ×3, bullet, numbered, quote, code, link.
- [ ] [P] Markdown preview (CommonMark + strikethrough, clickable links).
- [ ] [P] Copy all, Clear (undoable), Save as .txt (`<tab> <yyyy-MM-dd>.txt`).
- [ ] [P] Pin (keep open) vs close on outside click; Esc closes; background opacity slider.
- [ ] [P] Optional global hotkey (off by default; ⌃⌥⌘N → suggest Ctrl+Alt+Shift+N) with a smart toggle.
- [ ] [D] Dynamic Island scratchpad page (unless the island is ported).

### 2.4 Cleaning Mode
- [ ] [P] Lock now from the tray, Settings, Quick Launcher, Command Bar or radial menu. No hotkey and no timeout.
- [ ] [R] Swallow every key, modifier, media key, scroll and touchpad gesture; keep pointer movement and clicks. (Windows can't block Ctrl+Alt+Del, Win+L, or firmware/OEM keys.)
- [ ] [P] Black screen on every monitor (default), or a corner indicator ("Keep screen visible"), with 5 progress dots and an "Unlock" button.
- [ ] [P] Unlock with Esc ×5, each press within 6 s of the previous; any other key resets; auto-repeat is ignored.
- [ ] [P] Mouse-release gate: teardown waits up to 5 s for buttons held during the lock to be released, never synthesizing a release.
- [ ] [P] Pauses conflicting input features (key debounce, click filter, middle-click, navigation, button shortcuts, radial menu) and restores them afterwards.
- [ ] [P] Follows display changes; ends on user switch; fails open if the filter dies.

### 2.5 Camera preview
- [ ] [P] Fixed 320×240 rounded mirror, 48 DIP below the top of the pointer's monitor, centered; mirrored, aspect-fill.
- [ ] [P] Optional hotkey toggle (off by default; ⌃⌥⌘W → suggest Ctrl+Alt+Shift+W) plus tray, Settings, launcher, Command Bar and radial entries.
- [ ] [P] Camera picker on hover (≥ 2 cameras); remembers an explicit pick; hot-plug fallback; "No camera detected".
- [ ] [R] Permission and denied state (Windows privacy settings), start-failure and retry state, exclusive-use handling.
- [ ] [P] Closes on Esc, an outside click, another app activating, or the hotkey. The camera is released immediately.

### 2.6 Media tools
- [ ] [P] One workspace with the tools Video / GIF / Image / Text, hosted in Settings, the tray panel and the Quick Launcher (island optional).
- [ ] [P] Input by file dialog or drop; automatic collision-free outputs next to the source, or a chosen destination.
- [ ] [R] Video "Resolution" mode with Low/Medium/High plus Size (macOS presets need a calibrated Windows ladder).
- [ ] [P] Video "File size" mode (1–512 MB): bitrate planner plus up to 3 encode passes.
- [ ] [P] GIF: trim, FPS 1–30, longest edge 160–1600, loop toggle, ≤ 300 frames, file-size mode with up to 4 passes.
- [ ] [P] Image batch: JPEG/PNG/HEIC/PDF; quality; resize modes (none, max side, width, height, custom stretch/fit/fill); background; strip metadata; watermark (text and/or logo, 5 positions, opacity, margin, scale); rename tokens; "Converted" subfolder; keep the modified date; profiles; Web/Social/Docs presets; live preview.
- [ ] [P] OCR to TXT plus copyable text (Accurate/Fast; app language plus English).
- [ ] [P] Progress, cancel, result cards (size delta, "Show", "Copy text", "Copy summary", "Run again").
- [ ] [L] "Edit" handoff to the screen-recorder editor; island "Optimize media" and "Create ZIP".

### 2.7 Input fixes
- [ ] [P] Extra click filter: left/right/middle buttons, 5–100 ms window (default 25), never delays healthy clicks.
- [ ] [R] Key debounce: 0–500 ms global window (default 5) plus per-key overrides; auto-repeat, modifiers and key-ups are never filtered.
- [ ] [P] Mouse button shortcuts: XBUTTON1/2 and horizontal-wheel left/right → one key combination, fired on press, with a capture flow.
- [ ] [R] Button-drag gesture: hold the bound button and drag 220 DIP for the previous/next virtual desktop, up 150 DIP for Task View. A short click still clicks.
- [ ] [N] Side buttons Back/Forward (native on Windows; drop).
- [ ] [R] Smooth scrolling: speed 20–100 (default 40), Response 0–100 (default 65), Coast 0–100 (default 0); mouse wheels only; Ctrl+wheel passes.
- [ ] [N/R] Linear scrolling: Windows is already linear; either drop it or wrap `SPI_SETWHEELSCROLLLINES`.
- [ ] [P] Scroll sideways while holding a modifier (Shift/Alt/Ctrl/Win).
- [ ] [R] Scroll direction inverter, vertical and horizontal, mouse only (touchpad discrimination is the main risk).
- [ ] [R] Disable mouse acceleration (global `SPI_SETMOUSE` on Windows, not per device).
- [ ] [R] Super key: Caps Lock or a right modifier holds a modifier set (Windows default Ctrl+Alt+Shift; the Office key issue). Solo action: Escape / Caps Lock / next input source (hold ≥ 500 ms for Caps Lock). Pauses while listed apps run.
- [ ] [R] Quit/close protection: two slots (Alt+F4 or Ctrl+Q; Ctrl+W or Ctrl+F4), modes hold (default 800 ms), double press (600 ms) or extra modifier (Shift); per-app scope; HUD pill with a progress bar.
- [ ] [P] "Apps to leave alone": per-feature app lists (smooth scroll, linear, direction, button shortcuts; Super key while running).

### 2.8 AI agent usage
- [ ] [P] Per-agent toggles: Claude, Codex, OpenCode, GitHub Copilot CLI; "Found/Not found".
- [ ] [P] Incremental readers: Claude JSONL, Codex JSONL, OpenCode SQLite, Copilot events.jsonl. Dedup, merge, 91-day horizon.
- [ ] [P] Pricing from the bundled or daily-downloaded price list: cache write 5 min/1 h, cache read, fast tier, US-only, long-context premium, web searches; cache savings.
- [ ] [P] Plan recognition (Claude `~/.claude.json`, Codex `plan_type`).
- [ ] [R] Claude plan limits from the desktop app's history file (may not exist on Windows) and a 5-hour estimate from local activity.
- [ ] [P] Codex limits from logs; banked resets via `codex app-server` (count, expiry, "Use a reset" with idempotency).
- [ ] [P] Live "working" detection; "<Agent> finished" notice for tasks ≥ 60 s; near-limit warning at 80%; "Limit renewed"; daily budget notice.
- [ ] [P] Cards: Limits, Spending (Today/7 days/30 days, plan multiple "N×"), Now, Trend, Models, Projects, Activity heatmap (13 weeks), Resets. Reorderable and hideable.
- [ ] [R] Closed-island strip and resting "AI limits" ring (needs a host surface on Windows).
- [ ] [P] Archive for fast resume; price refresh daily (6 h retry after a failure).

### 2.9 Wallpaper (opt-in)
- [ ] [P] Gallery of system stills plus the user's images and folders; filter All/Your pictures/System; 24 per page.
- [ ] [P] Apply with fill to all monitors or to the monitor under the pointer.
- [ ] [P] Add image(s), add folder, remove, hide folder images, "Open Wallpaper settings".
- [ ] [D] macOS "all Spaces" store patch (decide whether per-virtual-desktop is needed).

### 2.10 Dynamic Island (high level)
- [ ] [R] Top-center overlay container: states closed/compact/peek/expanded, springs, hover dwell 0.25 s, click/hotkey/scroll to open, sections with Explore and floating buttons, notices with priorities.
- [ ] [R] Simulated notch shapes (capsule default) for every monitor on Windows.
- [ ] [P] v1 modules: Music (+ Lyrics), Timer/Pomodoro, Downloads, Keep Awake, volume/mic pop-ups, battery notices, Controls subset.
- [ ] [L] v1.1 modules: Calendar (Graph/ICS), Files shelf, accessories, Mixer, AI Agents.
- [ ] [L] v2 modules: Notifications mirror (package identity), Watch, live equalizer, Companion.
- [ ] [D] Up Next queue, lock-screen island, keyboard-light pop-up, AirPlay, Apple Events playback fallback, Mission Control concealment.

---

## 3. Detailed behavior per feature

### 3.1 Shelf

#### 3.1.1 Purpose and surfaces

The Shelf is a temporary holding area. Users drop files, images, text or links on it, and later drag them out into any app, share them, open them or reveal them.

It has three presentations that share one item store:

1. **Classic floating card.** Opens at the pointer from the global shortcut, a mouse shake during a drag, menu and Command Bar actions, the radial menu, or an *edge peek* during a drag.
2. **Docked shelf.** A small pill under the app's menu-bar icon (Windows: near the tray icon), or a badge at the top center of the screen.
   - It appears by itself whenever content is being dragged anywhere.
   - It expands into the full card when the pointer dwells near it.
   - It stays visible as a pill with an item count while the shelf holds items.
3. **Dynamic Island "Files" page.** When the island is enabled with its Files section and `notchShelf` is true, the island page replaces both surfaces above (see §3.10).

Storage rules:
- Files the user drops are kept **by reference**: the path plus a macOS bookmark, so moved or renamed files can be found again.
- Pasted image/GIF data, received file promises (virtual files) and generated screenshots are **copied** into a private store.
- Everything persists across relaunches. There is a hard cap of **200 leaf items**.

Permissions: none, apart from Finder Automation for one optional shortcut variant. Drag detection uses a passive global mouse monitor.

#### 3.1.2 Enablement and routing

- **Gates.** The feature must be available (`featureAvailable.shelf`) and `shelfEnabled` must be true. `shelfEnabled` is off by default; its label is "Temporary area for dragging files". Every public entry point re-checks both.
- **Sync.** `syncWithPreferences()` runs at launch, on the master toggle, and on island setting changes. It:
  1. reloads the exclusion list;
  2. hides the classic and docked panels if the island routes the shelf;
  3. if enabled: registers the hot key and starts the drag monitor;
  4. if not enabled: cancels pending Finder-selection reads and virtual-file deliveries, unregisters the hot key, stops the drag monitor and hides both panels;
  5. re-evaluates the docked shelf.
- **Turning the shelf off never deletes items.** They come back when it is turned on again.
- **Drag monitor.** Runs only while `shelfEnabled` is on and at least one of these is true: shake, drop zone, edge peek, or the island's drag reveal.

#### 3.1.3 Entry points

| Entry point | Condition | Action |
|---|---|---|
| Global hot key (default ⌃⌥⌘D; Windows: Ctrl+Alt+Shift+D) | `shelfEnabled` and `shelfShortcutEnabled` | Toggle: if the classic card is visible, hide it, else summon it at the pointer (the island toggles its Files page instead) |
| Hot key while Finder (Windows: Explorer) is frontmost | also `shelfShortcutAddsFinderSelection` | Adds the file manager's current selection, then summons (§3.1.4) |
| Mouse shake during a content drag | `shelfShakeToOpen`, source app not excluded | Summon at the pointer |
| Any content drag | `shelfDropZoneEnabled`, source not excluded, classic card not visible | Docked pill appears; dwelling near it expands it to the card |
| Drag dwelling near an outer left/right screen edge | `shelfEdgeDragEnabled`, source not excluded | Edge peek (§3.1.8) |
| Settings "Open now" | shelf enabled | Summon |
| Tray/status menu "Open shelf" | shelf available and enabled | Expand the docked shelf (summons the classic card if docking is off) |
| Tray panel row accessory "Open shelf (N)" | enabled and N > 0 | Close panel, expand docked |
| Command Bar "Shelf" | available (shows "needs setup" when disabled) | Summon |
| Command Bar "Keep it on the shelf" | enabled; acts on the selected text | Adds the selected text without touching the clipboard; HUD "Shelf" (beep on failure) |
| Radial-menu "Shelf" slice | available **and** `shelfEnabled` | Summon |
| Pill/badge click | — | Expand docked |

#### 3.1.4 "Add the Finder selection" shortcut variant

1. If the option is off, or the file manager is not frontmost, the shortcut just toggles.
2. Otherwise, take a *ticket*. A newer press supersedes older tickets; Clear all and disabling the shelf invalidate them.
3. Read the selection off the UI thread. On macOS this uses AppleScript and needs Automation consent. On Windows use `IShellWindows`: find the foreground Explorer window, then `IFolderView2::GetSelection`, or `SWC_DESKTOP` for the desktop. No consent is needed on Windows.
4. Resolve the ticket exactly once:
   - **Discard** if the ticket is stale or the feature is no longer allowed.
   - **Toggle** if none of the selected files is new (compared by standardized path against shelf files, piles included).
   - **Add** the new files otherwise. Several files form one pile. If they don't fit the capacity, beep and toggle instead. After a successful add, summon.

#### 3.1.5 Global drag detection (what counts as a "content drag")

**macOS mechanism (for reference).**
- A passive global monitor of left mouse down, dragged and up events. It needs no permission and only sees events delivered to *other* apps.
- On mouse-down the monitor records a baseline change count of the system *drag pasteboard*, plus the source app: the owner of the window under the press, else the frontmost app.
- A gesture is a **content drag** only if, during the gesture, the drag pasteboard's change count moved off the baseline **and** the pasteboard holds at least one droppable type: file URL, file promise, image (PNG/TIFF/GIF/any image), URL, or plain text.
- Retained data from a previous drag proves nothing, because the drag pasteboard keeps old data forever.
- Special cases:
  - Presses in the Dock use a "resting" baseline (Dock stacks publish before the mouse-down is seen).
  - On macOS 27, title-bar window moves never deliver a mouse-down, so the first dragged event opens the gesture and re-baselines. This prevents a window move from being misread as a content drag.
- A **watchdog** timer (every 0.15 s, only during a drag) checks the physical left-button state, because a mouse-up can be swallowed by the drag machinery. If the button is up, the gesture ends. Otherwise it advances the dock and edge dwell timers, so a pointer that stops moving still completes its dwell.

**Windows requirement.** Windows has no global drag notification and no global drag clipboard, so the predicate cannot be reproduced exactly. The required behavior and recommended design:

1. A WH_MOUSE_LL hook tracks left button down, move and up (respect `SM_SWAPBUTTON`). A "potential drag" starts once the pointer moves more than `SM_CXDRAG`/`SM_CYDRAG` with the button held.
2. Suppress window moves and resizes using `SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART/END)`. This is the equivalent of the macOS guard above.
3. **Confirm content with the app's own OLE drop targets.** While a potential drag is in progress, show *sensor* windows registered with `RegisterDragDrop`:
   - the pill or badge, which doubles as the docked target;
   - thin strips (2–4 px) at the outer left and right screen edges, for edge peek.
   
   A real OLE drag produces `IDropTarget::DragEnter` with an inspectable `IDataObject`; that is the confirmation. Remove the sensors on button-up.
   - Sensors must stay thin, so they never steal drops from the apps underneath.
   - `WS_EX_TRANSPARENT` windows do not receive OLE drops.
4. **Semantics change.** Shake, dock and edge can no longer be gated on content *before* anything is shown. Show the card or pill speculatively. If no `DragEnter` arrives before button-up and the shelf is empty, hide it immediately.
5. **Source app for exclusions:** `WindowFromPoint` at the press, then `GetAncestor(GA_ROOT)`, `GetWindowThreadProcessId` and `QueryFullProcessImageNameW`. Fall back to `GetForegroundWindow`.
6. Automatic opens (shake, dock, edge) also require `allowsAutomaticOpen(source, exclusions)`. An unknown or empty source is allowed. Dock and edge also require that no internal shelf drag is in progress.
7. **Own drags.** Drags started from the shelf's own tiles must never trigger automatic opens. On macOS the global monitor never sees them, and the baseline is re-captured after them.

#### 3.1.6 Shake gesture

The shake is horizontal only and involves no modifier key. On each dragged event:

1. Append (timestamp, pointer x) and drop samples older than 0.5 s. Do nothing below 5 samples.
2. Over consecutive samples, sum travel = Σ|dx|. A sample's direction is +1 if dx > 6 pt, −1 if dx < −6 pt, else 0. Each non-zero direction that differs from the previous non-zero direction counts as a reversal.
3. Trigger when **all** of these hold:
   - reversals ≥ 3;
   - travel > 220 pt;
   - more than 1.0 s since the last shake summon;
   - the drag is a content drag;
   - the source app is not excluded.
   
   On trigger: record the time, clear the samples, and summon on the next UI-loop turn.

#### 3.1.7 Classic floating card

**Window.**
- Borderless, non-activating, floating/topmost, transparent background.
- Becomes key only after a click inside it, so Esc and ⌘A work. It never activates the app.
- Visible on all Spaces and over full-screen apps; excluded from window cycling and window managers.
- The whole window is also a drop target.

**Layout** (DIP):

| Element | Spec |
|---|---|
| Card | Width 304; padding 14; vertical spacing 11; corner radius 18; HUD blur material; border 1 white @12%; 2 accent while a drop hovers (0.15 s ease-out); item changes animate 0.18 s ease-out |
| Header | Tray glyph (12 semibold, secondary); title "Shelf" or "%d selected" (12 semibold, one line, middle truncation); leaf-count capsule badge (11 bold) when non-empty |
| Pin button | 30 circle. Tooltip "Keep open" / "Allow closing after use"; accent when pinned |
| Close button | 30 circle, ✕, tooltip "Close" |
| Tile area | Fixed height 188; vertical scrolling |
| Empty state | Dashed rounded rect (radius 12, 1.5 line, dash 6/5, secondary @40%), down-arrow glyph 21, text "Drag items here" (12) |
| Footer (only with items) | Min height 30. Hint "Click to select. Drag out to use or right-click for more actions." (10, tertiary). Share button 42×28 (disabled at 40% when the scope has no file). Trash button 42×28, red: "Clear all" with no selection, "Remove selected" with a selection |

Total height is about 257 DIP when empty and about 298 with items.

**Moving the card.** Dragging these regions moves the window, and they also accept drops: the header title region, a 208×55 strip at the top-left, the empty-state area, and blank space in the tile area. Tiles start item drags instead. On Windows, return `HTCAPTION` from `WM_NCHITTEST` for those regions.

**Summon position.**
- left = pointer.x − w/2; top = pointer.y + 16 (the card hangs 16 DIP below the pointer).
- Clamp to the work area of the pointer's monitor with 8 DIP margins.
- Show without activation at alpha 1, then evaluate auto-hide.

**Refit.** After every item change while visible, resize to fit the content with the **top-left corner fixed**, without animation.

**Auto-hide.**
- The card is *held open* while **any** of these is true:
  - the keep-open pin is set;
  - the shelf is **not empty**;
  - the pointer is inside the card;
  - a drop is hovering over it;
  - an interaction is in progress (tile drag or window move);
  - an edge peek is in progress.
- Only when none is true does a 5 s timer start (0.5 s tolerance). When it fires, the pointer position is re-checked, then a 0.22 s linear alpha fade runs at 60 Hz. Any hold condition appearing mid-fade cancels the fade and restores alpha 1.
- **In practice the card auto-hides only while empty**, for example after a shake that ended without a drop.

**Keep-open pin.** Session-only. Pinning cancels auto-hide; hiding the card resets the pin.

**Close button.** If `shelfClearOnClose` is set, run Clear all first (pinned items survive), then hide.

**Hide.** Reset auto-hide, unpin, order out, hide any tooltip, and let the docked shelf return.

**One shelf at a time.** While the classic card is visible the docked shelf is hidden.

**Keyboard** (only once the card is key):
- Esc with no modifiers clears the selection and the range anchor.
- ⌘A (Windows Ctrl+A) selects all visible tiles.
- Nothing else is handled: no Delete, arrows, Space/Quick Look or Return.

#### 3.1.8 Docked shelf (pill, badge, card)

**Visibility.** The docked shelf shows when **all** of these hold:
- the docked feature is on: not routed to the island, available, `shelfEnabled` and `shelfDropZoneEnabled`;
- the classic card is not visible;
- at least one of: leaf count > 0, a drag is active, or a *forced open* (an explicit open while empty).

Emptying the shelf clears the forced-open flag.

**Collapsed "pill"** (menu-bar/tray placement):
- Brand mark 15 at 85% opacity. After a successful drop it turns into a green check for 0.9 s.
- Leaf count (12.5 semibold, monospaced digits, only when > 0).
- Down chevron 9 (55% opacity; 100% on hover or drop hover).
- Padding 12 horizontal (11 when empty) × 8 vertical; HUD blur; radius 13.
- Border: 1 white @12%, or 2 accent while a drop hovers.
- While a drop hovers: scales to 1.06 with an accent glow (radius 12). Otherwise a black 16% shadow (radius 7, y offset 3).
- 8 DIP transparent margin around it.
- Click expands. Tooltip "Open now".

**Collapsed "badge"** (top-center placement):
- Solid plate: white 0.10 in dark mode, 0.96 in light mode; radius 13.
- Brand mark 18 (or green check), the text "Shelf" (max width 150), a count capsule and a chevron.
- Accessible as button "Shelf" with the count as its value.

**Expanded.** The same card as §3.1.7, except:
- no pin button;
- an up chevron "Collapse" replaces Close; it collapses without clearing;
- a faint brand watermark (128 wide, 5–8% opacity, rotated −8°, bottom-right);
- the move strip is 246 wide.

**Placement.**
- **Top center** applies only if the stored value is exactly `topCenter` **and** the Dynamic Island is off; otherwise use the tray placement.
- **Screen:** the one containing the status-item (tray icon) rectangle, else the screen under the pointer.
- **Tray placement:**
  - Center x on the icon. With no trustworthy anchor, use x = work.right − w − 12.
  - Clamp x to [work.left + 8, work.right − w − 8].
  - macOS hangs it 4 pt below the menu bar. On Windows with a bottom taskbar, sit it 4 DIP above the taskbar (bottom = work.bottom − 4).
- **Top center:** x = work.center − w/2; top = work.top + 4 (macOS keeps it below the camera housing).
- The anchored edge stays fixed as the content grows or shrinks.
- **Anchor trust.** macOS uses the status-item frame only if its midY lies within 48 pt of the top of a screen it intersects. Windows: use `Shell_NotifyIconGetRect`. If the icon is hidden in the overflow flyout, fall back to the work-area corner.

**Drag behavior.**
- On each qualifying drag event: cancel any pending end, mark the drag active (the pill appears collapsed, even when empty), update proximity, start the watchdog.
- **Pill → card:** the pointer must stay continuously inside the *trigger frame* for ≥ 150 ms.
  - Trigger frame = the docked window frame padded by 16 on all sides, unioned with the tray-icon frame padded 16 horizontally (tray placement only).
  - Without a window frame yet, an estimated 72×32 pill 4 DIP from the anchor is used.
- **Card → pill:** when the pointer leaves the card frame padded by 32.
- **Drag end** (button up): wait 0.15 s so a landing drop can claim it. Then mark the drag inactive and collapse (unless the catch check is showing). It hides if empty.
- **Catch** (a drop landed on the docked window): show the check for 0.9 s, collapse, clear forced-open. The card closes back to the pill right after a drop.
- **Explicit expand** (tray menu, panel accessory, pill click):
  - If docking is off, summon the classic card.
  - Otherwise hide the classic card, set forced-open if empty, and expand.
- **Explicit collapse:** hide the tooltip, collapse, drop forced-open if empty. Any content drag that ends also collapses an explicitly expanded card.

#### 3.1.9 Edge peek

1. **Match.** Screens are ordered by distance from the pointer. For each screen whose frame padded by 200 contains the pointer:
   - **left match** if pointer.x ≤ frame.left + 200 **and** pointer.x ≥ work.left (not inside a side-docked taskbar/Dock margin) **and** no other screen contains the point 201 DIP to the left of the edge at the same y. A seam between two monitors is never an edge.
   - **right match** is symmetric.
   - Top and bottom edges are never used.
2. **Dwell.** The same match (edge plus screen) must persist ≥ 150 ms. A different match restarts the dwell; no match resets it.
3. **Peek.**
   - The classic card is shown with only round(w/3) (≈ 101 DIP) on screen: left = work.left − w + w/3 for the left edge, work.right − w/3 for the right.
   - Vertically it is centered on the pointer and clamped to the work area with 8 DIP margins.
   - No auto-hide runs while peeking.
4. **Retreat.** If the pointer is no longer within 330 DIP of that edge, or is outside the screen's vertical span ±330, retract immediately.
5. **Release without a drop:** retract after 0.15 s.
6. **Drop lands:** the peek graduates to an ordinary card and slides (animated) flush to the usable edge: left = work.left + 8, or right edge = work.right − 8. It keeps its y (clamped). Because it now holds items, it stays open.

#### 3.1.10 Dropping onto the shelf

**Drop targets.** The card window (fallback), the move handles, the empty state, blank tile space (all of these add new items), each tile (merge into it), and the island Files page when routed. During an internal tile drag every non-tile target refuses, so a tile cannot be dropped back onto empty shelf space.

**Hover acceptance.** Accept if the shelf is available and enabled and the data can create an item:
1. a virtual file / file promise, with at least 1 free slot;
2. else file paths;
3. else GIF;
4. else an image;
5. else a non-file URL;
6. else non-blank text.

Capacity is *not* checked at hover time for non-promise drops (quirk: the drop then fails silently). The operation offered is Copy.

**Drop.** Must be accepted synchronously.
- Virtual files take the asynchronous path (§3.1.11).
- Otherwise parse into items. One item is appended alone; **more than one becomes a single pile**.
- If capacity would be exceeded, nothing is added, already-written owned payloads are deleted, and the drop reports failure (no message).

**Parsing precedence** per dragged item; the first match wins:

| Priority | Content | Representation |
|---|---|---|
| 1 | File paths (de-duplicated by standardized path) | One **file** item per path, **by reference**; title = file name; a bookmark is created now |
| 2 | GIF data | Written to the store as `<UUID>.gif`; file item titled "GIF" |
| 3 | Any other image data | Converted to PNG, written as `<UUID>.png`; file item titled "Image" |
| 4 | First non-file URL | **link** item; title = host (or the full URL if there is no host); link icon |
| 5 | Plain text | **text** item; truncated to 200,000 chars; whitespace-only rejected; title = first line of the trimmed text, max 48 chars |

Rich text or HTML without a plain-text flavor is ignored. On Windows, when a browser offers a virtual `.url` shortcut file *and* `UniformResourceLocatorW`, prefer the URL, so the result is a link item as on macOS.

**Thumbnails.**
- Image files (except RAW) get a 64 DIP thumbnail inline.
- RAW images, videos and restored items first show the system icon, drawn at 20 DIP. A 64 DIP thumbnail is then decoded off-thread and patched in under the same id.
- Video thumbnails use the frame at min(1 s, 10% of duration).

**After an add.** The new last leaf scrolls into view (or its outermost collapsed pile), with a 0.2 s animation. This also cancels the fade and graduates an edge peek.

**Merging onto a tile.**
- **External drop on a tile:**
  - New leaves are flattened; existing + new must be ≤ 200.
  - If the target is a pile, the leaves are appended and the pile title becomes "‹first child title› +‹N−1›".
  - If the target is a single item, it becomes a pile [target + new leaves].
  - **Quirk:** the new pile reuses the target's id. See §8.3.
- **Tile onto tile (internal, offered as Move):**
  - The target must not be dragged, and none of its descendants may be dragged.
  - The sources are removed from the tree first (piles dissolve as needed), then merged.
  - Merged drags skip the post-drop removal and close logic.

#### 3.1.11 Virtual files (macOS "file promises")

These arrive from apps that drag content that isn't a file yet: mail attachments, browser images, photo libraries. The flow:

1. **Read the drop.** Read the promise receivers. The other items in the same drop ("companions") are parsed normally and keep their position in the drag.
2. **Check capacity.** Free slots = 200 − current leaves − companion leaves. Refuse the drop if there are no receivers, if receivers > free slots, or if a merge target no longer exists.
3. **Create a private incoming folder.** `temp/VorssaintShelf/<bundle id>/<UUID>` (mode 0700). If that fails, show the alert "Couldn't add attachment" / "The file never finished saving to the shelf." and refuse.
4. **Request delivery** of every receiver into the incoming folder on a serial worker. Expected callbacks = Σ max(1, names listed by each receiver). If expected > free slots, cancel and refuse.
5. **Each successful callback.** Copy the delivered file into `ShelfFiles/<UUID>/<original name>`, preserving the exact name. Count it as a failure if any of these holds:
   - the delivered path, after resolving symlinks, is outside the incoming folder;
   - its name is ".", ".." or empty;
   - it is a symlink.
   
   Directories are copied recursively. A URL that comes with an error is ignored, even if a partial file exists.
6. **When all callbacks have arrived:**
   - sort the copies by receiver order, then by name order, so the source order survives out-of-order completion;
   - delete the incoming folder;
   - deliver on the UI thread, unless the transfer was cancelled (cancel wins even over a queued completion).
7. **On the UI thread:** discard everything if the shelf was disabled or the merge target vanished.
   - If the files no longer fit, discard and alert "Shelf is full" / "The attachment finished saving but there is no room left on the shelf."
   - Otherwise rebuild the drop in the original drag order (companions and received files interleaved by position) and append it, or merge it into the target.
   - If some callbacks failed, alert "Couldn't add attachment".
8. **Alerts** have a single OK button. They appear as a sheet on whichever shelf window is visible (classic, then docked, then island), otherwise as an app-modal alert, so a background failure doesn't steal focus.
9. **Cancellation.** Clear all, disabling the shelf, and removing a merge target all cancel pending transfers.
10. **Timing.** "Interaction noted" and the dock catch check fire at *drop* time, not at delivery time.

**Windows equivalent.** Read `CFSTR_FILEDESCRIPTORW` + `CFSTR_FILECONTENTS` (per index: `IStream`, `HGLOBAL` or `IStorage`). Use `IDataObjectAsyncCapability` to extract asynchronously on a worker thread. Apply the same containment, name-preservation, ordering, capacity-recheck and cancel rules.

#### 3.1.12 Item model, piles, pins, selection, capacity

- **Item fields.**
  - `id`: UUID.
  - `payload`: file(path) | text(string) | link(URL) | batch([items]).
  - `title`, `icon`, `isImage`, `hasContentThumbnail`.
  - `bookmark`: files only.
  - Equality is by id, but a tile must redraw when a same-id item's content changes (thumbnail patched in, path healed).
- **Leaf count.** 1 per leaf; the sum over a pile. Badges, pill count and capacity all use leaf counts.
- **Capacity** `canAdd(existing, new)`: existing ≥ 0, new > 0, existing ≤ 200 − new.
- **Pile dissolve rules** on removal:
  - an emptied pile disappears;
  - a pile left with one child is replaced by that child, which **inherits the pile's pin**;
  - otherwise the pile is rebuilt and its title recomputed.
- **Expansion.** Session-only. An expanded pile's children are drawn inline right after it, depth-first. Collapsing a pile deselects its descendants.
- **Selection.** A set of ids plus an anchor.
  - Click toggles one id and sets the anchor.
  - Shift-click unions the visible range between the anchor and the clicked tile, inclusive, in either direction.
  - ⌘A unions all visible tiles. Esc clears both.
  - After every removal, the selection, anchor, expansion set and pin set are pruned to surviving ids.
- **Per-item pins.** Persisted. *Protected* = pinned ids plus every id inside a pinned pile. Protected items survive drag-out removal and Clear all, but not the tile ✕ or Remove selected.

#### 3.1.13 Tiles

- **Grid.**
  - Tile 78×88; spacing 10; inset 4; content width ≥ 276.
  - Columns = max(1, ⌊(width − 8 + 10) / 88⌋), which is 3 in the 304-wide card. Rows = ⌈n / columns⌉.
  - Scrolls vertically. (Island variant: horizontal flow; rows = max(1, ⌊(height − 8 + 10) / 98⌋).)
- **Tile look** (origin top-left):
  - Corner radius 10. When selected: accent @16% fill and a 2 accent border. The same border shows while a merge drop hovers.
  - Icon well at (7,6), 64×50, radius 8, white @6%. Image inset 4 for real thumbnails, 13×8 for generic icons.
  - Label at (3,59), 72×24: 10 pt, centered, middle-truncated, max 2 lines.
  - **Piles:** two back plates offset +3/+6 (white @5.5% and @3.5%); count badge at (50,39), 22×15 (9 bold, white on accent); expand chevron at (4,4), 17.
  - Selection check badge at (4,4), or (4,22) on piles.
  - Pin badge at (58,4); on hover it is replaced by a ✕ remove button.
- **Mouse.**
  - Mouse-down notes the interaction, makes the window key and hides the tooltip.
  - Moving > 4 DIP starts an item drag.
  - Mouse-up without a drag:
    - on a pile with click count ≥ 2: toggle expansion;
    - with Shift: extend the selection;
    - otherwise: toggle selection.
  - The first click works even on a non-key window.
- **Tooltips.** A custom click-through panel: 11 pt text, wrapped at max width 280, 6 margins, system tooltip material.
  - Shown 1.0 s after the pointer enters a tile, if its window is still visible. Placed 6 below the tile's left edge, flipped above when near the screen bottom, clamped to the work area.
  - Hidden on exit, mouse-down, context menu, removal, panel hide or collapse, and edge retract.
  - Text by kind:

    | Kind | Tooltip text |
    |---|---|
    | file | the name, then a second line with the localized file kind |
    | text | the full trimmed text, capped at 500 chars + "…" |
    | link | the full URL, capped at 500 chars + "…" |
    | pile | "N items: a images, b files, c notes, d links", listing only non-zero kinds, singular/plural per count |

- **Context menu.** Scope = the selection if the clicked tile is selected, else the clicked tile. Piles are flattened; only living file leaves count. Entries:
  1. "Pin" / "Unpin" (always).
  2. If the scope has files, after a separator:
     - "Open";
     - "Open With" ▸ — apps that can open **every** file, sorted by name, max 40, 16 DIP icons; disabled if none;
     - "Edit" — only when the Screenshot feature is available and the scope is exactly one image; opens the screenshot editor;
     - "Share" ▸ — the system share menu;
     - a separator, then "Reveal in Finder" (Windows: "Show in Explorer").
  
  Notes and links get only Pin/Unpin.

#### 3.1.14 Drag-out

1. **Candidates.** The selection if the grabbed tile is selected, else the grabbed tile. Flatten to unique leaves.
2. **Living check.** A file leaf whose path is missing gets one chance to heal via its bookmark, resolved without UI and without mounting volumes. A healed tile updates in place. Dead leaves are excluded.
3. **All dead.**
   - Show the HUD "The file no longer exists" for 1.5 s, and remove the dead leaves.
   - Exception: a file under an unmounted volume is kept (`/Volumes/<name>` missing; Windows: the drive or UNC root is inaccessible).
   - No drag starts.
4. **Drag session.** One drag item per leaf: file → file path; text → plain string; link → URL.
   - Use a stacked drag image with a count badge. Windows: `IDragSourceHelper`, `SHCreateDataObject` from PIDLs for files.
   - Begin an interaction, which keeps the card open.
5. **Allowed operations.**
   - Inside the app: Move.
   - Outside: **Copy + Move** if `shelfRemoveAfterDrop` is on and no dragged leaf is protected; otherwise **Copy only**, so a destination can never move away a file the shelf keeps.
   - With the defaults, dropping a shelf file into a folder on the same volume **moves the original**, exactly like dragging from Finder/Explorer. Dropping it on the Recycle Bin would delete it.
6. **Completion.** Accepted = the final operation is not "none". On Windows: `DRAGDROP_S_DROP`, and also check `CFSTR_PERFORMEDDROPEFFECT`, because Explorer's optimized move reports `DROPEFFECT_NONE`.
   - Always clear the drag state and end the interaction.
   - If accepted and not a merge:
     - remove the dragged non-protected ids when `shelfRemoveAfterDrop` is on;
     - when `shelfCloseAfterDrop` is on and the source surface is not pinned, close the surface the drag came from: the island only if it is still on Files, the classic card only if it was the source, the docked card only if it was the source.
7. **Owned payloads** (the shelf's own copies) of removed items are deleted **10 minutes** later. User files are never deleted by the shelf.

#### 3.1.15 Share, Open, Reveal, Remove, Clear

- **Share button** (card footer and island).
  - Scope: the selection, or everything if nothing is selected, flattened to living files. If the scope had file leaves but all are dead, use the dead-drag handling.
  - Opens the system share picker anchored below the button. The app is activated only *after* a service is chosen.
  - Text and links cannot be shared.
  - Windows: `IDataTransferManagerInterop::ShowShareUIForWindow` with `DataPackage.SetStorageItems`. Nearby Share replaces AirDrop.
- **Open:** each file in its default app. **Open With:** all files in the chosen app. **Reveal:** select all scope files in the file manager (`SHOpenFolderAndSelectItems`, grouped per folder).
- **Tile ✕:** removes that item **even if pinned**. On a pile, the whole pile goes.
- **Trash with no selection** ("Clear all"): cancels pending selection reads and virtual-file deliveries, then removes every leaf that is not protected. (The island version asks "Clear all" / "Cancel" first.)
- **Trash with a selection** ("Remove selected"): removes the selected ids, **including pinned ones**.
- **Close button** clears only with "Clear when closed". Docked collapse, auto-hide, shortcut toggle and close-after-drop always keep items.

#### 3.1.16 Settings page (English labels)

- **Master section.**
  - Toggle "Temporary area for dragging files".
  - Caption "A floating spot to gather files, images and text, then drag them anywhere later."
  - Badge "Requires no permissions." shown unless the shortcut and its Finder-selection variant are both on.
- **"How to use"** (three steps):
  1. "Open it with the shortcut, or by shaking the mouse during a drag."
  2. "Drop files, images, links or text onto it to hold them."
  3. "Drag each item back out to any app when you need it."
- **"Where things open"** (only if the island shows Files): segmented "Dynamic Island" / "Separate window", bound to `notchShelf`.
- **Triggers.**
  - "Shelf shortcut" toggle and shortcut recorder. Recording suspends the live hot key.
  - "Add the Finder selection with the shortcut". Caption: "With Finder in front, the shortcut opens the shelf with the selected files already in it. With nothing selected, it opens as usual." Note: "The first time, macOS asks for permission to control Finder." (drop on Windows)
  - "Open by shaking the mouse while dragging". Caption: "Shake the pointer quickly while holding an item to summon it near the cursor."
  - "Keep dragged files in the menu bar" (Windows: "…near the tray icon"), with a caption describing the pill.
  - "Position" picker: "Below the menu bar icon" / "Top center of the screen". Disabled while the island is on, with the note "The Dynamic Island keeps the top center while it is on."
  - "Open near a screen edge". Caption: "Drag a file toward the screen edge to peek the shelf in. Drop it there, or pull back and it retreats."
  - Button "Open now".
- **"After use".**
  - "Close after dropping into another app": "Closes the shelf when the destination accepts the items. The pin in the panel keeps it open."
  - "Remove items after dropping": "Items accepted by another app leave the shelf. Turn this off to keep a copy there. Pinned items always stay."
  - "Clear when closed": "Empties the shelf only when you click its close button. Automatic hiding and collapsing keep the items."
- **"Automatic exceptions".**
  - App list: icon, name, remove button; "No apps added." when empty.
  - "Add app…" opens an installed-app picker.
  - Caption: "Shake and the menu bar drop zone stay off for drags started in these apps. The shortcut and Open now still work." (Edge peek and the island reveal honor the list too.)

#### 3.1.17 Integrations

- **Screenshots** (`screenshotAddToShelf`, default off). Each capture that does not open straight in the editor is added once, as an owned copy in the store. Discarding the capture's preview removes it again.
- **Screenshot editor** "Add to Shelf": adds the edited PNG under a capture-style name, shows HUD "Shelf" and closes the editor.
- **Tile "Edit"** opens a shelved image in the screenshot editor.
- **Command Bar** ("Shelf", "Keep it on the shelf") and the **radial menu** "Shelf" slice.
- **Dynamic Island:** the Files page (same items in a horizontal strip), the media-optimize drop chooser, Downloads rows' "Files" button, and media-tool and ZIP outputs added automatically.
- The **clipboard history does not integrate** with the shelf.

### 3.2 Radial menu

#### 3.2.1 Purpose

The radial menu is a ring of up to **12 user-defined actions** (a "wheel") that appears around the pointer, or at the screen center. The user points in a direction to highlight a slice, then releases the trigger or clicks to run it.

- A slice runs one of these: an app, a file or folder, a link, a synthesized keyboard shortcut, a media key, a "Now Playing" card, another Vorssaint tool, a Quick Toggle, a Window Layout action, or a one-level submenu.
- The user can define several independent wheels ("profiles"). Each profile has its own color, items and triggers.
- **A profile is chosen only by the trigger used to open it.** Nothing switches profiles automatically, for example per frontmost app.
- Resources while idle: one global hotkey per profile, a pre-built hidden window, and one low-level mouse hook only if some profile uses a mouse button. Every other input monitor exists only while a wheel is on screen.

#### 3.2.2 Lifecycle

- `syncWithPreferences()` is the only reconfiguration point. It runs at launch, when the feature is installed or uninstalled, when the Accessibility grant changes, when any setting is saved, and when Cleaning Mode ends.
- **Disabled or unavailable:** unregister all hotkeys, remove the mouse hook, end any session, release the window.
- **Enabled:**
  1. Decode the profiles (§5.2).
  2. Re-register one hotkey per profile with a valid shortcut. Hotkey id = 1700 + profile index. Any refusal sets a "registration failed" flag that Settings shows.
  3. Sync the mouse hook.
  4. Pre-build the hidden window and lay it out once, so the first open is instant.
- Cleaning Mode and self-uninstall *suspend* the feature.
- When the login session resigns (fast user switching), an open wheel ends and the mouse hook is released. Both are rebuilt when the session returns, because the hook is the only thing that sees a held button released.

#### 3.2.3 Triggers

| Trigger | Can be held? | Notes |
|---|---|---|
| Per-profile global keyboard shortcut (first profile default ⌃⌥⌘Space; Windows suggestion Ctrl+Alt+Shift+Space) | Yes, if the shortcut has ≥ 1 modifier and the mode is not "press" | Press events only, never auto-repeat. Pressing the same profile's shortcut while its wheel is open closes it. Another profile's shortcut closes the current wheel and opens that profile. |
| Extra mouse button (Back = 3, Forward = 4, buttons 5–31) | Yes, unless the mode is "press" | Each button belongs to at most one profile. Both halves of every click on a claimed button are always swallowed. A down while a wheel is open and not in its hold phase closes the wheel. A down during a held keyboard session is swallowed and ignored. |
| Four-finger trackpad tap (one profile at most) | No (always sticky) | Read by the Middle Click feature's multitouch reader. Same wheel open → close; other wheel open → switch. |
| Settings "Try it" | No | Works even when the feature is off or the profile has no trigger. |

There is no menu item, Command Bar entry, gesture or per-app exception list for opening a wheel.

#### 3.2.4 Opening behaviors and the hold/release logic

The mode is read **once per session at open**, so a mid-session settings change never produces a mixed gesture.

| Trigger ↓ / Mode → | "Press or hold" (default `pressOrHold`) | "Press to open" (`press`) | "Hold to select" (`hold`) |
|---|---|---|---|
| Shortcut with modifiers | Held. Releasing any required modifier runs the highlighted slice. If nothing is highlighted, the wheel stays open (sticky). | Sticky; release ignored | Held. Release runs the highlighted slice; if nothing is highlighted, the wheel closes. |
| Shortcut without modifiers (bare F1–F20) | Sticky | Sticky | Sticky (release can't be detected) |
| Mouse button | Held. Releasing the same button runs the highlighted slice; nothing highlighted → sticky | Sticky; the up is swallowed and ignored; the next down closes | Held. Release runs the highlighted slice; nothing highlighted → close |
| Trackpad tap / Try it | Sticky | Sticky | Sticky |

- **There is no time threshold.** Hold versus press is decided only by whether a slice is highlighted at release.
  - A quick press releases before the pointer has moved 8 DIP, so nothing is highlighted: the wheel stays open in press-or-hold, or closes in hold.
  - In hold mode, the "cancel" gesture is to move back into the center dead zone and release.
- **Keyboard release detection:**
  - Every modifier change during the session ends the hold once not all of the shortcut's modifiers are still down. Extra modifiers are tolerated.
  - Fallback: every pointer move during the hold phase re-checks the modifier state, because the release can land before the monitor is installed. Windows: also check `GetAsyncKeyState` immediately after installing the hooks.
  - With the Super key (§3.7.6), the hold persists while either the physical modifiers or the Super key is held. Super key release ends the hold; a Super key cancellation ends the session.
- **Leaving the hold phase early** ("sticky phase"): selecting a submenu, or pressing an arrow key in press-or-hold or press mode. In hold mode, arrows keep release-to-run.

#### 3.2.5 Session start

1. **Filter items.**
   - Drop tool slices whose tool isn't runnable.
   - Drop Quick Toggle slices if that feature is unavailable.
   - Drop Window Layout slices if that feature is unavailable.
   - Filter submenus recursively and drop any submenu left empty.
   - If nothing remains: system beep, no wheel.
2. If a previous wheel is still fading out, finish its cleanup now.
3. If any level contains a Now Playing slice, start a Now Playing refresh (state "loading"). Otherwise the state is "nothing playing".
4. Compute the hold phase. Record the session shortcut, hold button and Super-key usage. Reset the stack, trail, highlight and arming. Record the pointer position at open.
5. **Placement.**
   - "At the pointer": center on the pointer. "At the screen center": center of the work area of the screen under the pointer.
   - Clamp the center to stay 200 DIP inside the work area, when the work area is larger than 400×400.
6. Show the 400×400 window centered there: alpha 0, shown without activating, given keyboard focus, then fade in. Install the session monitors.
7. On the next UI turn, flip to "visible" to start the grow and bloom animations.

#### 3.2.6 Geometry and hit testing

All geometry constants are in §6.2. Rules:

- **Angles.** Angle = atan2(dx, dyUp), normalized to [0, 2π). Here dyUp is positive toward the top of the screen; on Windows, dyUp = center.y − cursor.y. 0 is 12 o'clock and angles grow clockwise.
- **Slice math.** With n slices, step = 2π/n. Slice i is centered at i·step and covers [i·step − step/2, i·step + step/2). Slice 0 is always straight up.
- **Highlight from the pointer.** Nothing is highlighted inside the 40 DIP dead zone. Outside it there is **no outer limit** (marking-menu style): pointing far beyond the disc still highlights the slice in that direction.
- **Arming.** Pointer moves change nothing until the pointer has traveled **8 DIP** from where the wheel opened (or where a submenu was entered). This prevents a center-opened wheel from firing on a stale direction, and stops trackpad tremor from erasing a keyboard-chosen highlight. Once armed, every move recomputes the highlight and overrides any arrow-key choice.
- **Clicks on the window.** The whole 400×400 square is clickable. By distance from the center:
  - more than 150 → close;
  - less than 40 → step back (close at the root, leave a submenu);
  - otherwise → run the highlighted slice. If nothing is highlighted, nothing happens; if the highlight came from the keyboard, that slice runs.
- **Default starter wheel** (6 slices, 60° apart, clockwise from 12 o'clock): Play/Pause, Next track, Screenshot, `~/Downloads`, Color picker, Previous track.

#### 3.2.7 Input during a session

- **Pointer tracking.** Plain moves, left-button drags and extra-button drags. Right-button drags are not tracked.
- **Keyboard.** The wheel takes keyboard focus without activating the app (on Windows this must come from a session-scoped keyboard hook; see §7.2).
  - **Esc:** step back (close at the root).
  - **Return / keypad Enter:** run the highlighted slice (always consumed).
  - **Left/Up:** rotate counter-clockwise. **Right/Down:** rotate clockwise. With no highlight, Right/Down picks slice 0 and Left/Up picks the last slice. Rotation wraps. In press-or-hold and press modes, arrows also switch to the sticky phase.
  - **Digits 1–9:** run that slice if it exists. Modifiers are ignored, so digits work while the chord is still held. Slices 10–12 have no digit.
  - Other keys are not handled.
- **Running a slice.**
  - A **submenu** pushes its children, adds its name to the trail, clears the highlight, enters the sticky phase, and re-arms the pointer from its current position. This prevents a double-click from firing the child that sits under the parent's direction.
  - A **leaf** closes the wheel first, then runs (§3.2.9).

#### 3.2.8 Submenus and dismissal

- **Depth.** At most root + 1 level. Deeper submenus are dropped by cleanup. A submenu holds ≤ 12 items.
- **Hub inside a submenu.** With nothing highlighted, the hub shows a back chevron over the submenu name ("Submenu" if unnamed). Clicking the hub or pressing Esc goes back.
- **Dismissal paths:**
  - running a leaf;
  - Esc or a hub click at the root, or a click beyond radius 150;
  - any click outside the window (frame inflated by 2 DIP) in any app; the click is **not** consumed and still reaches its target;
  - another app becoming active;
  - the same trigger again;
  - a hold-mode release with nothing highlighted;
  - the feature being disabled or suspended, Cleaning Mode, or the session resigning.
- **Close mechanics.**
  - Remove all monitors, then fade.
  - Keyboard focus returns to the underlying app on the first frame of the fade, so typing goes there immediately. Mouse input is ignored during the fade.
  - After the fade, hide and reset. A token guards against a stale fade completion hiding a window that a new summon has reclaimed.

#### 3.2.9 Item kinds and activation

| Kind | Payload | Activation | Failure |
|---|---|---|---|
| `app` | path to the app (`~` allowed) | Launch or activate | beep |
| `file` | file or folder path (`~` allowed, e.g. `~/Downloads`) | Open with the default handler if it exists | beep if missing |
| `url` | normalized URL (§6.2) | Open with the default handler (any scheme) | silent if unparsable |
| `shortcut` | shortcut storage string | Needs the input-injection permission. Wait until no Cmd/Opt/Shift/Ctrl is physically down (poll every 15 ms, at most 100 tries ≈ 1.5 s, then post anyway), wait 60 ms, post key-down with the modifiers, then key-up 40 ms later. Posted at the hardware level so system-wide shortcuts see it. | permission prompt once, then beep |
| `media` playPause / nextTrack / previousTrack | id | Same modifier wait, then post the system media key (down and up) | as above |
| `media` nowPlaying | — | Show the Now Playing card at the wheel center (§3.2.11); needs no permission | nothing if not playing |
| `tool` | tool id (15 tools) | After 0.15 s (so the wheel is gone before any screen capture), run the tool | only offered if runnable |
| `quickToggle` | toggle id (8) | After 0.15 s, run the toggle | — |
| `windowLayout` | layout action id (41) | After 0.15 s, apply it to the window that was active behind the wheel; the wheel never activates | beep |
| `submenu` | — | Navigate | — |

**Tools.** Each label is the feature's title and runs if that feature is available:
- Screenshot
- Screen recording (start or stop)
- Color picker
- Copy text from screen
- Mute microphone
- Clipboard
- Quick panel
- Camera preview
- Scratchpad
- Shelf (also needs `shelfEnabled`)
- Cleaner, Uninstaller, App updates (each opens Settings at that page; App updates also starts a check)
- Cleaning Mode (always runnable)
- Keep awake (toggle)

**Quick toggles.** Labels are dynamic and read from the current system state:
- "Switch to dark/light mode"
- "Empty the Trash" (shows its own confirmation)
- "Eject all disks"
- "Show/Hide hidden files"
- "Hide/Show desktop icons"
- "Lock the screen"
- "Turn off the display"
- "Start the screen saver"

**Window Layout actions** (41 ids):
- halves: left, right, top, bottom, center;
- vertical thirds: left, center, right, plus left/right/center two-thirds;
- horizontal thirds: top, middle, bottom, plus top/bottom two-thirds;
- horizontal quarters (4) and vertical quarters (4);
- sixths (6);
- corners (4);
- maximize, margin-maximize, full screen, center, previous display, next display, restore.

**Automatic labels.** A non-empty custom name always wins. Otherwise:

| Kind | Automatic label |
|---|---|
| app / file | display name of the path |
| url | host |
| shortcut | symbol form, e.g. "⌃⌥⌘Space"; "Press a shortcut" when unset |
| tool / toggle / layout | the feature's title |
| media | "Play or pause", "Previous track", "Next track" |
| Now Playing | "Title⏎Artist" when playing; "Nothing playing"; "Now Playing" while loading |
| submenu | "Submenu" |

**Chip icon precedence:**
1. Now Playing: the playing app's icon at 34 DIP, else a music-note symbol.
2. A decodable custom image (and no custom symbol), at 34 DIP.
3. The real app or file icon, for app/file kinds with no custom symbol, at 34 DIP.
4. Otherwise the symbol (custom or automatic) at 20 DIP semibold, white when highlighted.

Icons and display names are cached per path for the process lifetime, so the wheel never touches the disk while tracking the pointer. A dead network mount would otherwise stall it.

#### 3.2.10 Appearance

- **Window.** Transparent, borderless, non-activating, above normal and floating windows. No OS shadow; the disc draws its own. Shown on all desktops and over full-screen apps; hidden from window managers.
- **Disc.** 300 DIP translucent material with a tint (white 68% light / black 42% dark), a gradient rim, a hairline border and a soft shadow (black 22% light / 55% dark, blur 24, offset 8 down).
- **Slice guides.** Hairlines at slice boundaries (when n > 1).
- **Chips.** 52 DIP circles on the 112 radius ring.
  - Normal: white 88% fill (light) or white 14% (dark).
  - Highlighted: filled with the profile color, scaled to 1.14, with a colored glow.
- **Wedge.** A radial gradient of the profile color under the highlighted slice. It moves along the shortest arc.
- **Hub** (76 DIP), showing one of:
  - the highlighted item's name (11 pt semibold, up to 3 lines);
  - inside a submenu: a back chevron over the submenu name;
  - otherwise the brand mark.
  
  Faces cross-fade in 0.14 s.
- **Profile colors** (12): accent, blue, purple, pink, red, orange, yellow, green, mint, cyan, indigo, graphite. Light-mode RGB values are in §6.2.
- **Motion** (all disabled by Reduce Motion; exact springs in §6.2):
  - Open: fade 0.18 s and scale 0.9 → 1.
  - Chips "bloom" out of the hub clockwise, staggered within 0.17 s.
  - Close: fade and scale 0.13 s; chips fold back.
  - The wedge springs between slices.
- **Accessibility.** Increase Contrast strengthens borders. Reduce Transparency uses a solid tint.

#### 3.2.11 Now Playing slice and card

- **Data.**
  - macOS: a background helper reads the private MediaRemote framework. It runs as perl loading a bundled library, because since macOS 15.4 MediaRemote only answers Apple-signed processes. The helper prints one JSON line, with a 2 s timeout.
  - Fields: title, artist, album, artwork (base64), pid, bundle id, isPlaying (fallback: playback rate > 0).
  - Only a *playing* session produces a snapshot. Text is trimmed, stripped of control characters and capped at 300 chars. Artwork ≤ 12 MiB.
  - Windows: `GlobalSystemMediaTransportControlsSessionManager.GetCurrentSession()` with `TryGetMediaPropertiesAsync` (Title, Artist, AlbumTitle, Thumbnail), `PlaybackStatus == Playing` and `SourceAppUserModelId`. This runs in-process; no helper is needed.
- **Selecting the slice.**
  - Playing: show the card now.
  - Loading: show the card when the reply arrives, if the reply says playing.
  - Nothing playing: nothing happens.
- **Card.**
  - A non-activating overlay with a shadow, centered on the wheel center and clamped to the work area with 12 DIP margins. Fades in 0.12 s.
  - 320 DIP wide, rounded 16, translucent.
  - Contents: 76×76 artwork (or the app icon on a dark tile); title (13 semibold, 2 lines; falls back to the app name); album and artist; a footer "Open %@" with the app icon.
  - Closed by any click outside it, by any app activation, or by the next wheel session.
- **Clicking the card** brings the player forward:
  - Find the running app by bundle id/AUMID, else by pid.
  - Ignore background helpers.
  - Unhide, then activate with all windows. If it has no visible window, reopen it quietly (like a Dock click).
  - If the app isn't running but is installed, launch it.
  - Windows: resolve the AUMID to a window and use `ShowWindow(SW_RESTORE)` + `SetForegroundWindow`, or activate by AUMID.

#### 3.2.12 Profiles and presets

- **Fields:** id, name (empty displays "General"), color, shortcut, mouseButton, items, preset, trackpadTap.
- **Trigger exclusivity.**
  - A shortcut already used by another profile is refused.
  - Assigning a mouse button removes it from other profiles; cleanup keeps the first holder.
  - The trackpad tap is exclusive across profiles.
- **Duplicate** copies everything with a new id and the name "<name> 2", and clears all triggers.
- **Presets** (items in slot order from 12 o'clock):

  | Preset | Color | Items |
  |---|---|---|
  | General | accent | Play/Pause, Next, Screenshot, `~/Downloads`, Color picker, Previous |
  | Media | purple | Play/Pause, Next, Now Playing, Previous |
  | Tools | cyan | Screenshot, Color picker, Screen OCR, Screen recorder, Mic mute, Scratchpad |
  | Window layout | orange | Maximize, Right half, Bottom half, Left half, Top half |
  | Quick toggles | mint | Dark mode, Desktop icons, Hidden files, Lock screen, Empty Trash |
  | Blank | graphite | none |

- **New profile names.** "General N" (N = profile count + 1); other presets use their own title. New profiles have no triggers.
- **"Reset"** (canvas header, root level only) restores the preset items. The preset is the stored id, else matched from the profile name.

#### 3.2.13 Settings page (English labels)

**Section "Radial menu":**
- "Use the radial menu". Caption: "Opens a wheel of your favorite actions around the pointer".
- "Opening behavior": "Press or hold" / "Press to open" / "Hold to select". Caption: "Press or hold keeps the current adaptive gesture. Press stays open; hold runs the highlighted action on release."
- "Opens": "At the pointer" / "At the screen center".
- "Try it".
- An orange note when a hotkey registration failed.

**Section "Profiles":**
- Picker "Profile".
- "+" menu ("Add profile") listing the 6 presets.
- Duplicate.
- Delete. Disabled when only one profile exists. Confirmation: "Delete “%@”?" / "Its actions, shortcut, mouse button and four-finger tap will be removed. This can’t be undone." with "Delete profile" / "Cancel".
- "Profile name" (placeholder "General"; saved on every keystroke).
- "Color": 12 swatches.
- **"Shortcut" recorder:**
  - Esc cancels; Delete clears.
  - A valid shortcut needs Ctrl, Opt or Cmd, or a bare F1–F20.
  - Conflicts are checked against other wheels, all app shortcut roles, Window Layout shortcuts and Command Bar shortcuts → "This shortcut is already used by %@."
  - "Nothing was captured. macOS or another app already uses that combination. Try another one."
- **"Mouse button":**
  - Options: "Off", "Back side button", "Forward side button", "Button 6"…"Button 32".
  - Captions explain that only extra buttons work, and that Back/Forward stop navigating while assigned.
- **"Button test"** row: live feedback.
  - "Press the button now" / "Vorssaint sees this button" / "That was a different button" / "Vorssaint cannot watch the mouse right now".
  - Hint about vendor mouse software remapping buttons.
- **"Open with a four-finger tap":** exclusive across profiles; warns if Middle Click already uses four-finger taps.
- When a profile has no trigger: "Picking a profile here only edits it. Give this one a shortcut, a mouse button or the four-finger tap below to open it."

**Section "Actions":**
- **Visual canvas** (330 DIP tall, dark stage, 250 DIP wheel, 44 DIP chips on a 92 radius ring, 68 DIP hub):
  - Hover highlights a chip and shows its name in the hub.
  - Click (≤ 6 DIP of movement) opens the editor.
  - Drag (> 6 DIP) **swaps** two slots, picked by angle.
  - Context menu: Edit, "Edit actions" for submenus, Remove.
  - Hub click: back (in a submenu) or add (when empty).
  - "Reset" button with confirmation.
- "Add action" while fewer than 12 items.
- "Show as list" editor: drag rows to *move* (not swap), context menu "Remove", "‹ Back" row inside submenus.
- Footer "A wheel holds up to 12 actions." at the limit.

**Item editor sheet:**
- "Action" kind picker: "Open an app", "Open a file or folder", "Open a link", "Press a shortcut", "Vorssaint tool", "Quick toggles", "Window layout", "Media control", "Submenu" (root only).
- Per-kind target editor:
  - app: chooser;
  - file or folder: chooser;
  - link: field (placeholder "example.com") + "Fetch Website Icon" (spinner "Fetching icon…", results "Icon downloaded" / "Could not find a website icon"; disclaimer "Connects to the website once to download its icon. Saved locally.");
  - shortcut: recorder;
  - tool, toggle, layout and media: pickers.
- "Name" (placeholder "Automatic").
- "Icon" grid: "Automatic" + 92 curated symbols.
- "Enter a valid link." for invalid links.
- Buttons "Remove" / "Cancel" / "Save". Save is enabled only for a valid target, by the same rule as cleanup.

**Other surfaces:**
- Tray panel row "Radial menu" ("Your favorite actions on a wheel"), with the master switch and "Manage the menu".
- A read-only row on the Keyboard Shortcuts page listing all profile shortcuts.

#### 3.2.14 Interop and guards

- **Claimed mouse buttons.** Mouse navigation (Back/Forward), mouse-button shortcuts and the Settings window yield any button a wheel claims. The mouse-button-shortcut capture refuses it with "That button already opens the radial menu. Pick another one, or free it there first."
- **Claim finality.** Once a button-down is claimed, both down and up are swallowed, even if the full profile decode then finds nothing. The app underneath never gets half a click.
- **Without the permission:** the mouse trigger and trackpad tap don't work; key, media and layout slices prompt once, then beep. Shortcut-opened wheels with app, file, link, tool or toggle slices work without permission.
- **Robust decoding.** Corrupt profile data never crashes. Invalid parts are dropped individually (§5.2).
- **Not handled:** secure input, display reconfiguration during a session, per-app exceptions.

### 3.3 Scratchpad

#### 3.3.1 Purpose

A floating, always-on-top notes window for short-lived text.

- **Tabs.** Up to **12** named tabs ("pads") of **plain text**, all stored in one small JSON document.
- **Saving.** The document saves 0.8 s after the last keystroke, and immediately after every tab action, mark, clear, export, close and quit.
- **Lazy, safe load.** Notes are read only when the pad first opens. If an existing file can't be read, the pad refuses to save anything, so it can never overwrite unreadable data.
- **Markdown.** A formatting row types Markdown syntax for you, and a read-only preview renders a Markdown subset on demand.
- **Closing.** The pad closes on Esc and, by default, on any click outside it. A pin keeps it open while the user works elsewhere.
- **Auto-clear** (optional) empties tabs left unedited for a day, a week or a month.

#### 3.3.2 Entry points

| Entry | Action |
|---|---|
| Global hotkey (`scratchpadShortcutEnabled`, default **off**; default ⌃⌥⌘N, Windows suggestion Ctrl+Alt+Shift+N) | Smart toggle (below) |
| Tray panel › Utilities row "Scratchpad" ("Quick notes in separate tabs"; shows the hotkey) | Close panel, then show after 0.15 s |
| Settings › Quick Tools › Scratchpad › "Open scratchpad" | Show |
| Quick Launcher tile, Command Bar "Scratchpad", radial tool slice | Hide that surface, show after 0.15 s |
| Dynamic Island page / control tile | Opens the island page instead (macOS only) |

- **Smart toggle.**
  - Does nothing while a dialog of the pad is up.
  - Pad visible and focused → hide.
  - Pad visible but unfocused → focus it.
  - Pad hidden → show it.
- **Island routing.** `show()`/`toggle()` first check the island. If the island is on with its Scratchpad page and `notchScratchpad` is true, the island page opens and any floating pad hides. Windows: drop this routing.

#### 3.3.3 Show and hide

**Show** (requires the feature installed and no dialog open):
1. If already visible: focus it and move the caret to the end of the text.
2. Otherwise reset the preview to off and the format row to collapsed, and set pinned = NOT `scratchpadCloseOnClickOutside`.
3. Load the document (§3.3.9). On failure, show the HUD "Your notes could not be opened. They were left unchanged." and do **not** open.
4. Create the window if needed and install the key and outside-click monitors.
5. If the remembered frame doesn't intersect any monitor's work area, re-center it.
6. Show without activating other windows, take keyboard focus, caret to the end, and fade alpha 0 → 1 over 0.13 s.

**Hide:**
1. Flush the pending save. If saving still fails, show the HUD "Your notes could not be saved. Copy them elsewhere before quitting."
2. Remove the monitors and hide (no fade-out).
3. Reset pinned, preview and dialog state.

**Hide triggers:**
- Esc;
- ⌘W (Ctrl+W) with a single tab;
- the header close button;
- an outside click, unless pinned or a dialog is up;
- the hotkey while focused;
- the island taking over;
- feature uninstall (which also discards the window and its remembered frame);
- app quit;
- self-uninstall.

#### 3.3.4 Window

- **Kind.** Borderless, resizable, floating (topmost), stays visible when the app is inactive, all desktops, over full-screen apps. Hidden from window managers and window cycling. Window title "Vorssaint".
- **Focus.** It must receive typing. On macOS it becomes key without activating the app. On Windows it must become the foreground window; `SetForegroundWindow` is allowed right after the hotkey or a tray click.
- **Size.** Initial 380×300 DIP; minimum 280×220.
- **Resize zones.** Custom hit zones: 6 DIP edge bands and 12 DIP corners. Windows: `WM_NCHITTEST`; minimum via `WM_GETMINMAXINFO`.
- **Move.** Only by dragging the 34 DIP header strip. Dragging in the text selects text.
- **First position** (on the monitor under the pointer, using its work area):
  - horizontally centered;
  - vertically top = work.top + 0.42 × (work.height − h);
  - clamped 16 DIP inside every edge.
- **Position memory.** Position and size survive hide/show for the app session only; they are not saved to disk.
- **Appearance.**
  - Rounded card (radius 14) with a 1 DIP white border @12% and a window shadow.
  - Background: HUD blur material, plus the system window color at opacity `scratchpadBackgroundOpacity` (0 = fully translucent).
  - Follows light/dark mode.

#### 3.3.5 Layout (top to bottom)

1. **Header (34).**
   - "Scratchpad" (12 semibold, secondary color, 12 left inset). This is the drag handle.
   - Pin toggle: tooltip "Keep open" when unpinned, "Close when I click outside" when pinned; accent color when pinned.
   - Close button, tooltip "Close". Hit targets 22×22.
2. **Tab bar (32, divider below).**
   - Tabs scroll horizontally; the selected tab auto-centers (0.15 s).
   - "+" button, tooltip "New scratchpad". At 12 tabs it is disabled, tooltip "You can keep up to 12 scratchpads".
   - "…" menu: "Rename scratchpad" / "Close scratchpad" (Close disabled with one tab).
   - **Tab:** 11 pt text (semibold when selected), width 46–130, height 24. Selected tab: accent @16% fill, radius 6.
   - A small ✕ ("Close scratchpad") appears on the selected and hovered tab when there are ≥ 2 tabs.
   - Right-click a tab: Rename / Close.
3. **Save-failure banner** (only while the last write failed): warning icon + "Your notes could not be saved. Copy them elsewhere before quitting."
4. **Editor** (fills the rest).
   - Placeholder "Type anything. It saves by itself." exactly at the first line.
   - In preview, the editor stays alive but hidden and ignores clicks; the preview is laid over it.
   - **Invariant:** the editor is never recreated, so undo history survives preview.
5. **Format row (30):** only when "Formatting" is expanded and preview is off.
6. **Footer (36):**
   - Formatting chip.
   - Preview toggle ("Show formatting" / "Edit text").
   - Copy all. Shows a green check and "Copied" for 1.2 s.
   - Save as file.
   - Spacer.
   - Clear.
   - Preview, Copy, Save and Clear are disabled (50% opacity) when the tab is empty. Formatting stays enabled, so an empty pad can start a heading.

#### 3.3.6 Tabs

- **Order and selection.** An ordered list with stable UUIDs; list order is tab order. The selected tab id is persisted, so the pad reopens on the last used tab.
- **Create** ("+" or Ctrl+T): append at the end, select it, focus it. Named with the next free "Scratchpad N" (algorithm in §6.3).
- **Select:** save first, then switch; caret to the end of that tab's text.
- **Rename:**
  - Dialog "Rename scratchpad" with buttons Cancel / Save.
  - Cleaning: every whitespace/newline run → one space, trimmed, cut to 40 characters.
  - An empty result leaves the name unchanged.
- **Close** (tab ✕, "…" menu, right-click, Ctrl+W):
  - An empty tab closes immediately.
  - A tab with text asks "Close scratchpad" / "Delete “<name>” and everything in it?" with Cancel and "Close scratchpad" (destructive).
  - The last tab can't be closed; Ctrl+W hides the pad instead.
  - After closing the selected tab, the tab now at the same index becomes selected (or the new last tab).
- **Write-first rule.** Every tab operation writes the new document first and takes effect only if the write succeeded (test-asserted).
- **Not supported:** restoring a closed tab, reordering, duplicating, word or character counts, rich text, multiple windows.

#### 3.3.7 Editor behavior

- **Plain text only.** Rich text, pasted styles, images, smart quotes and dashes, text replacement, autocorrect, link detection and data detection are all **off**.
- **Look.** System font at `scratchpadTextSize` (10–22); inset 7×2 plus 5 line padding; soft wrap; vertical scrollbar.
- **Undo.**
  - Native undo/redo. A formatting mark, Clear and a line move are each a single undo step.
  - Replacing the text programmatically (open, tab switch, auto-clear, settings restore) **wipes** undo history, so the user can never undo into another tab's text.
- **Move lines** (Alt+Up / Alt+Down; macOS ⌥↑/⌥↓):
  - Only with Alt as the sole modifier, the editor focused, and no IME composition in progress.
  - Swaps the block of lines the selection touches with the line above or below.
  - Line endings stay with their lines, and the selection follows the moved text.
  - At the first or last line the key falls through to default behavior.
  - Undo and redo restore both text and selection. Swapping two identical lines only moves the caret and adds no undo step.
- **Find.**
  - Ctrl+F opens a find bar: incremental search, highlighted matches, match counter, Replace optional.
  - F3 / Shift+F3 (macOS ⌘G / ⇧⌘G) go to the next/previous match. If the search field has focus, it keeps it.
  - Esc in the find bar closes the bar; the next Esc hides the pad.
  - Entering preview closes the find bar. A find shortcut pressed in preview leaves preview first.

**Keyboard shortcuts** (only when the event targets the pad):

| macOS | Windows | Action |
|---|---|---|
| Esc | Esc | Hide. Goes to the IME while composing, and to the find bar while it has focus. |
| ⌘T | Ctrl+T | New tab; swallowed and ignored at the 12-tab limit |
| ⌘W | Ctrl+W | Close the selected tab (with confirmation); hide when only one tab |
| ⌘F | Ctrl+F | Find bar |
| ⌘G / ⇧⌘G | F3 / Shift+F3 (optionally Ctrl+G) | Next / previous match |
| ⌥↑ / ⌥↓ | Alt+Up / Alt+Down | Move lines |
| ⌘Z / ⇧⌘Z | Ctrl+Z / Ctrl+Y | Undo / redo |

- Pad shortcuts are ignored while a dialog is up.
- Keys are matched on the **character the layout produces** (lower-cased), not the physical key. Caps Lock therefore still works, and on AZERTY the physical "W" position (which types Z) doesn't close a tab.

#### 3.3.8 Formatting marks and preview

**The nine marks** (tooltips): "Bold" `**`, "Italic" `*`, "Strikethrough" `~~`, "Heading" (shown as "H"), "Bulleted list" `- `, "Numbered list" `1. `, "Quote" `> `, "Code" `` ` ``, "Link" `[text](url)`. Buttons are 26×26.

General rules:
- Not available in preview.
- Any IME composition is committed first.
- Each mark is **one replacement, one undo step**, followed by an immediate save.
- Every mark is a **toggle**: a second click removes it.
- Selections are UTF-16 offsets clamped to the text, so a mark never lands inside a surrogate pair.

**Inline marks** (bold, strikethrough, code):
1. Trim whitespace from the selection ends, unless the selection is only whitespace. "hello " → "**hello** ".
2. If the marker is already present at the selection's edges, inside or just outside, remove it.
   - If the inner text contains further markers, and their count is even, none is escaped with `\`, and there is no other markup, the spans are joined under one pair: "**one** and **two**" → "**one and two**".
   - Otherwise the text is left alone.
3. Otherwise wrap the selection. The words stay selected. With an empty selection, the caret lands between the markers.

**Italic** counts runs of `*`, because `*` is half of bold's `**`:
- An odd run of stars on both sides means italic is present; remove one star from each side.
- Inner `***` → leave alone (the boundary is ambiguous).
- Examples: "**note**" + Italic → "***note***" → Italic again → "**note**".

**Link:**
- If the selection or caret is strictly inside an existing `[label](address)`, the link is replaced by its label, which is selected.
- Brackets are matched by depth. A character preceded by an odd number of backslashes is escaped, so addresses like "a_(b_(c))" and "a\)b" work.
- Inside an image (`![…]`) nothing happens.
- Otherwise the selection becomes `[sel](url)`, with "url" selected for typing over.

**Line marks** (heading cycles `# ` → `## ` → `### ` → off; bullet `- `; quote `> `; numbered `1. `, `2. `… renumbered in order):
- Applied to every line the selection touches. Leading spaces and tabs (nesting) are kept.
- Blank lines are skipped, except in a completely empty pad, where the click starts the first line: "" + Heading → "# ".
- The current step is the prefix every non-blank line already has. For numbered lines, any digits followed by ". ".
- An existing line mark is **replaced**, not stacked: "- item" + Heading → "# item"; bullets → Numbered gives "1. one\n2. two".
- Lines are split on "\n" only; the selection shifts by the prefix lengths.

**Preview** (read-only, selectable, links clickable):
- **Parser.** CommonMark plus GFM strikethrough. Tables are parsed but not laid out (each cell becomes its own paragraph) *(inferred)*. If parsing fails, the raw text is shown as one paragraph.
- **Line breaks.** Single newlines inside a paragraph render as spaces (CommonMark soft breaks) *(inferred)*.
- **Rendering:**

  | Element | Rendering |
  |---|---|
  | Heading | Semibold. H1 = body size + 5, H2 = +3, H3–H6 = +1 |
  | Bullet item | "• " prefix (secondary color), 2-space indent per nesting level |
  | Ordered item | "N. " prefix, same indent rule |
  | Quote | "▏ " prefix, secondary color |
  | Code block | Monospaced, body size − 1, background 7% of the text color, trailing newlines trimmed |
  | Thematic break | 24 × "─" |
  | Inline code | Monospaced at max(9, size − 1) on a 7% background |
  | Links | Accent color; a click opens the URL with the default handler (any scheme; consider limiting to http(s)/mailto on Windows) |
  | Images | Alt text only *(inferred)* |

- **Spacing.** Line spacing 2. Blocks are separated by a blank line; consecutive blocks inside the same list or quote by a single newline.
- **Behavior.**
  - Disabled when the tab is empty. Clearing the tab exits preview.
  - Entering preview collapses the format row and removes focus from the editor; leaving preview refocuses it.

#### 3.3.9 Copy, Clear, Export, persistence

- **Copy all.** Puts the tab text on the clipboard as plain text, with a "source = Vorssaint" marker (Windows: a registered custom clipboard format) so the app's own clipboard history doesn't misattribute it.
- **Clear.** No confirmation. Replaces the text through the editor, so one undo restores it. Exits preview and saves.
- **Save as file:**
  1. Requires a non-empty tab and no open dialog.
  2. Save first, then open a save dialog: plain text, can create folders.
  3. Suggested name `"<tab name> <yyyy-MM-dd>.txt"`: tab name cleaned as in renaming, "/" and ":" → "-", local date.
  4. While the dialog is up, the pad can't be dismissed, a second export can't open, and the hotkey is ignored.
  5. On OK, write the **requesting tab's text as it is at confirmation**, as UTF-8 (no BOM), atomically. If that tab was closed meanwhile, or the write fails, show the HUD "The file could not be saved". Cancel never writes.
  6. Focus then returns to the pad if it is still visible.
  7. Only `.txt` is offered: no Markdown, HTML or PDF export.
- **Load** (on demand: pad open, island page, settings export):
  - If an in-memory document differs from the last saved one (an earlier save failed), do not reload; retry the save instead.
  - Otherwise read, in priority order:
    1. `Scratchpad.json`. A decode error ("", "{broken", "{}") or a read error other than "missing" **throws**.
    2. The legacy preference blob.
    3. The legacy `Scratchpad.txt` (it becomes tab 1, with modifiedAt = the file's mtime).
    4. A fresh document with one empty "Scratchpad 1".
  - Then clean the document:
    - keep the first 12 tabs;
    - drop repeated ids;
    - clean names (an empty name gets the next default name);
    - drop the modifiedAt of empty tabs;
    - fix a selection pointing at a missing tab;
    - an empty tab list → a fresh document.
  - Apply auto-clear, then save. Only after a **verified** save are the legacy sources deleted.
- **Any load failure** leaves every source byte untouched and blocks saving until a later load succeeds (test-asserted).
- **Save:**
  1. Skip if the document equals the last saved one.
  2. Otherwise create the folder (owner-only), write atomically (temp file + rename), then **read the file back and compare bytes**.
  3. On failure: `saveFailed` (banner shown, edits kept in memory, every later change retries).
- **Autosave.** Each text change updates the tab's text and modifiedAt (cleared when the text becomes empty) and restarts a 0.8 s trailing timer. Up to 0.8 s of typing can be lost in a crash.
- **Auto-clear (retention).** Evaluated only at load (no timer). A tab is cleared when a period is chosen and now − modifiedAt > the period (strictly). A missing or future timestamp never clears. Tab and name are kept.
- **Settings import** cancels any pending save and drops the in-memory document before the relaunch.
- **Notes are never part of the settings backup.**

#### 3.3.10 Outside-click dismissal and focus

- Applies when not pinned and no dialog is up. Watches left, right and other mouse-downs, both in the app's own other windows and in other apps.
- A click counts as outside only if it falls outside the window frame enlarged by 2 DIP (so the resize edge still belongs to the pad), and not on the on-screen keyboard (Windows: osk.exe / touch keyboard).
- There is **no hide on app deactivation** (for example Alt+Tab); the pad keeps floating.
- Pinning lasts only while the pad is shown. Changing the setting while it is open re-derives pinned.

#### 3.3.11 Settings (English labels)

Settings › Quick Tools › "Scratchpad":
- "Open scratchpad".
- "Clear on its own": "Never" / "After a day unused" / "After a week unused" / "After a month unused". Caption: "The pad empties itself once the text goes that long without edits."
- "Close when I click outside".
- "Text size" slider (10–22, step 1).
- "Pad background" slider (0–1, step 0.05, "Translucent"…"Opaque").
- "Global shortcut" toggle and recorder, with the rejection warning.
- Hub description: "Floating pads for short-lived notes".

#### 3.3.12 Dynamic Island integration (macOS; drop on Windows)

The same document can be edited in the island's Scratchpad page:
- capsule tabs, a toolbar with Formatting, "+", Preview, Copy, and a "…" menu (Rename / Close / Save as file / Clear / Open scratchpad);
- white text on dark; "Copied" for 1.6 s;
- the same shortcuts apply; Ctrl+W on the last tab collapses the island.

Possible gap: a quit while only the island page was used may skip the final flush. **Flush unconditionally on exit** in the port.

### 3.4 Cleaning Mode

#### 3.4.1 Purpose

Cleaning Mode locks the keyboard so it can be wiped without typing anything.

- **What is blocked.** A high-priority input filter swallows every key, modifier, media/brightness/power key, scroll and trackpad gesture event before the system or any app sees it.
- **What still works.** Pointer movement and clicks.
- **Screen.** Every display is covered with a **black screen** (default) or, optionally, a **small corner indicator**. Both show live progress toward the unlock gesture and an "Unlock" button.
- **Unlock.** Press **Escape 5 times in a row**, each press within **6 s** of the previous; any other key resets the count. Or click Unlock.
- **Permission.** Requires the input-interception permission. It refuses to lock without it, so there is always a way back.

#### 3.4.2 Entry points

| Entry | Detail |
|---|---|
| Tray panel › Utilities "Cleaning Mode" | Caption "Locks the keyboard so you can clean safely.", or "Permission required: Accessibility" with "Grant access". Closes the panel first, then activates. |
| Status/tray menu "Cleaning Mode" | Activates directly |
| Settings › Quick Tools › "Cleaning Mode" | Button "Lock keyboard now", caption, toggle "Keep screen visible" with its caption |
| Quick Launcher tile | Hides the launcher, activates after 0.1 s |
| Command Bar "Cleaning Mode" | "Needs permission · Return asks" when missing; activates after 0.1 s |
| Radial tool slice | Activates after 0.15 s |

There is **no hotkey** and no automatic timeout. Hub description: "Lock keyboard and screen for cleaning".

#### 3.4.3 Activation

1. If already active, do nothing.
2. If the permission is missing, show the alert "Accessibility needed" / "To lock the keyboard safely, Vorssaint needs Accessibility permission. Grant it in System Settings and try again." with "Open System Settings…" and "Cancel". Do not lock.
3. Reset the mouse-release gate and **install the input filter first**. If it can't be created, abort silently: the keyboard is never locked without its unlock path.
4. Pause conflicting features, restoring them afterwards:

   | Feature | Why it is paused |
   |---|---|
   | Key Debounce | Would eat the repeated Esc presses |
   | Mouse Click Debounce | Pointer input shouldn't be altered during the lock |
   | Middle-click emulation | Stray three-finger contacts while wiping the trackpad |
   | Mouse Navigation | Side buttons would post synthetic keys under the lock |
   | Mouse Button Shortcuts | Same |
   | Radial Menu | No stray wheel over the overlay |

5. Reset the counter, mark active, watch display changes, show the overlays.

#### 3.4.4 What is blocked

Every intercepted event except mouse button down/up is swallowed (test-pinned). Pointer movement and drags are never intercepted in the first place.

| Input | macOS behavior |
|---|---|
| Key down/up, every key including Esc | Swallowed. Key-downs feed the counter (auto-repeat ignored); key-ups are ignored. |
| Modifier changes (Shift, Ctrl, Option, Cmd, Fn, Caps Lock) | Swallowed. Both press and release reset the counter. |
| System keys: volume, mute, brightness, keyboard light, play/next/previous, eject, power-key press | Swallowed. Their key-downs reset the counter; key-ups and repeats don't. |
| Scroll wheel and trackpad scrolling (incl. momentum) | Swallowed |
| Trackpad gesture events (pinch, rotate, swipe) | Swallowed. Whether system multi-finger swipes are also stopped is not established. |
| Mouse buttons (left/right/other) | **Passed through**; tracked by the release gate |
| Pointer movement and drags | Not intercepted |
| Force shutdown (holding power), Touch ID | Out of reach |
| Keys while another app has secure input on *(inferred)* | Not seen by the filter, so not blocked; the code doesn't check |

Esc presses are counted but still swallowed, so no app ever receives them.

#### 3.4.5 Unlock gesture and teardown

**Counter** (unlock key = Esc, threshold 5, window 6.0 s inclusive, monotonic clock):
- Auto-repeat is ignored completely: it neither counts nor resets.
- Any other key-down, or any modifier down or up, sets progress to 0 and forgets the last time.
- An Esc within 6.0 s of the previous Esc increments progress; otherwise progress restarts at 1.
- Progress ≥ 5 → unlock request.
- The window was 2 s originally; it was widened because slow pressers couldn't unlock (issue #697). A test pins 6.0.

**Unlock request** (Esc ×5 or the Unlock button):
1. Start a 5 s deadline.
2. If no mouse button pressed during the lock is still held, tear down on the **next UI-loop turn**. This lets the Unlock button's click finish before its window is removed.
3. Otherwise wait for the real release of every such button, including presses that start during the wait.
4. When the deadline fires, stop waiting and tear down anyway.
5. **Never synthesize a mouse release** and never read global button state (test-asserted).

**Teardown:**
- Cancel the deadline and remove the filter.
- Stop watching displays and hide all overlays.
- Reset the counter and mark inactive.
- Resume the paused features; each re-reads its current preferences and permission.

#### 3.4.6 Overlays

- **Windows.** One borderless, non-activating overlay per monitor, covering the full monitor (menu bar/taskbar included), at the highest overlay level: above the menu bar, Dock and full-screen apps.
- The first click lands even though the overlay is never focused, so Unlock works on one click.
- **Display changes.** Overlays exactly matching an existing monitor are reused (no flash), new ones are added, extra ones removed. With no monitors at all, one 800×600 overlay is used.
- **Black mode** (default). Opaque black; every click lands on the overlay, so apps are unreachable. A centered column (spacing 18, padding 44, max width 460):
  - keyboard symbol 48 DIP, white;
  - "Keyboard locked for cleaning" (23 semibold, white);
  - "Press Escape 5 times to unlock" (15, white 75%);
  - **5 dots** 12×12, spacing 11: white for completed presses, white 22% otherwise, 0.15 s animation;
  - "Unlock" button: white capsule, black 14 semibold text, minimum width 130;
  - "Your mouse and trackpad still work" (12, white 50%).
- **Indicator mode** ("Keep screen visible"):
  - The full-screen overlay is transparent; clicks on transparent areas fall through to apps. Keys and scrolling stay blocked.
  - A card at the top-right of every monitor, 40 DIP from the top and 24 from the right:
    - keyboard icon in a 28 DIP circle;
    - "Keyboard locked for cleaning" (13 semibold);
    - a subtitle with 5 small dots (6×6, accent color for completed presses);
    - a small "Unlock" button.
  - Card styling: HUD material, radius 16.
  - The mode setting is read live.
  - Windows recommendation: implement this mode as a small topmost HUD per monitor rather than a full-screen transparent window.

#### 3.4.7 Safety exits and lifecycle

- **Fast user switching.** The mode ends immediately (bypassing the gate). Paused features resume when the session becomes active again.
- **Filter disabled by the OS** (timeout or user input):
  - If the session is active and the permission is still granted: re-arm the filter, forget tracked presses, and finish a pending unlock.
  - Otherwise: **fail open** and tear down.
- **Self-uninstall / permission reset.** The filter is removed *before* any other filter is suspended and before the permission is reset (test-asserted).
- **App quit.** Process exit removes the filter.
- Uninstalling the feature while it is active is not handled.

#### 3.4.8 What a Windows port can and cannot block

| Input | Low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) |
|---|---|
| Ordinary keys, Esc, F-keys, numpad, Enter, Tab | Can block |
| Shift, Ctrl, Alt, Win, Caps/Num/Scroll Lock | Can block; swallowing the key-down also prevents the lock toggle *(inferred)* |
| Alt+Tab, Win, Win+letter, Ctrl+Esc, Alt+F4, Ctrl+Shift+Esc, PrtScn, Win+Shift+S, Win+G, language switch | Can block |
| **Ctrl+Alt+Del** (secure attention sequence) | **Cannot** block |
| **Win+L** | **Cannot** block with hooks (only via the DisableLockWorkstation policy) |
| Volume/media keys delivered as virtual keys | Can block |
| Laptop brightness, keyboard backlight, Fn combos (firmware/ACPI/WMI), vendor HID buttons | Often **cannot** |
| Power button, lid, ACPI sleep | Cannot (VK_SLEEP may be blockable) |
| Mouse wheel and horizontal wheel | Can block (let movement and clicks pass) |
| Precision-touchpad scrolling via DirectManipulation, 3/4-finger shell gestures, touch/pen edge swipes | Not reliably visible to hooks. In black mode the overlay absorbs touches anyway. |
| Input aimed at **elevated** windows | Not delivered to a non-elevated hook (UIPI). Keep the overlay in the foreground and re-take foreground on `EVENT_SYSTEM_FOREGROUND`, or run with uiAccess. |
| Secure desktop (UAC prompt, lock screen) | Never hooked |

- **Do not use BlockInput.** It also blocks the mouse, which breaks the Unlock button and the "mouse still works" promise. It cannot observe the Esc ×5 gesture, it is cancelled by Ctrl+Alt+Del, and it effectively needs elevation.
- **Hook timeout.** LowLevelHooksTimeout applies and Windows silently removes a hook that times out. Keep the callback minimal and add a watchdog that re-installs the hook while the mode is active.
- **Auto-repeat.** Low-level hooks get no repeat flag. Treat a key-down for a key already held as a repeat: keep a held-key set and clear it on activation.
- **Display the limitation in the UI**, for example: "Ctrl+Alt+Del and Win+L still work."

### 3.5 Camera preview

#### 3.5.1 Purpose

A quick mirror for checking yourself before a call.

- **Window.** A small 320×240 rounded floating window showing the live camera image, horizontally flipped. It sits at the top center of the screen under the pointer, near where a laptop camera usually is.
- **Camera lifetime.** The capture session exists only while the window is on screen. The camera light and all capture resources stop the moment it closes.
- **Closing.** Esc, a click anywhere else, or another app becoming active (for example, joining the meeting).
- **Multiple cameras.** With more than one camera, a picker appears on hover.
- **Robustness.** Handles permission, hot-plugging and start failures with explicit states.
- **No recording.** Nothing is ever recorded or saved, and the app persists no camera state.

#### 3.5.2 Entry points

| Entry | Behavior |
|---|---|
| Hotkey (`cameraPreviewShortcutEnabled`, default **off**; default ⌃⌥⌘W, Windows suggestion Ctrl+Alt+Shift+W) | **Toggle**: hide if shown, else show |
| Tray panel row "Camera preview" | Caption "Check how you look before a call", or "Permission required: Camera" with "Open System Settings…" when denied. Closes the panel, then shows after 0.15 s. |
| Settings › Quick Tools › "Camera preview" | "Open preview"; global shortcut toggle + recorder; the rejection warning; a "Camera / Not granted" row when denied |
| Quick Launcher tile, Command Bar "Camera preview", radial tool slice | Show after 0.15 s. The Command Bar shows "Needs permission · Return asks" until granted. |
| Dynamic Island Camera page | macOS only: the island embeds the same view |

`show()` is not a toggle; if the window is already visible it does nothing. Hub description: "Opens a floating mirror with your camera".

#### 3.5.3 Show flow

1. Require the feature to be installed and this login session to be active.
2. If the island camera page is enabled, present there and stop (macOS only).
3. If already visible, do nothing. Stop any capture running in the island.
4. Build the window once and install the dismissal monitors.
5. **Position** (on every show; there is no position memory):
   - horizontally centered on the pointer's monitor;
   - top = work.top + 48 DIP;
   - left clamped 16 DIP from the monitor sides;
   - top clamped so that bottom ≤ work.bottom − 16.
6. Fade alpha 0 → 1 over 0.13 s, take keyboard focus (so Esc works), and start capture.

#### 3.5.4 Window

- **Kind.** Borderless, non-activating but focusable, floating/topmost, stays up when the app is inactive, all desktops, over full screen, hidden from window cycling, with a shadow.
- **Move.** Drag anywhere on it.
- **Resize.** Not resizable; fixed 320×240.
- **Shape.** One shape only: rounded rectangle, radius 14, 1 DIP white border @14%, black fill.
- **Controls.** Always drawn in dark appearance. **No close button** on the floating mirror.
- **Image.** Aspect-fill (cropped), always mirrored, at medium capture quality.

#### 3.5.5 States

| State | Shown |
|---|---|
| idle, waitingPermission, starting | Small white spinner |
| running | Live preview (mirrored, aspect-fill) |
| denied (also "restricted") | Camera-off icon + "Camera access for Vorssaint is turned off in System Settings." + "Open System Settings…" |
| unavailable | Camera-off icon + "The camera could not start. Try opening it again." + "Open camera" (retry; only from this state) |
| noCamera | Camera icon + "No camera detected" |

Status layout: icon 26 DIP at white 55%; caption text at white 80%, centered, padding 28.

**Transitions:**
- idle → waitingPermission → starting or denied;
- starting → running, unavailable or noCamera;
- running → unavailable on an error or interruption;
- running/starting → noCamera when the last camera leaves;
- noCamera → starting when a camera appears;
- unavailable → retry, which re-checks permission and starts again;
- any state → idle on hide.

#### 3.5.6 Cameras

- **Discovery.** Built-in, external (USB/UVC) and macOS Continuity Camera devices.
- **Initial pick.** The remembered camera if present, else the first one found. On macOS the memory is the system's per-app preferred camera; on Windows add an app setting (§4.5) written only on an explicit pick.
- **Picker.**
  - Shown only while running, with the pointer over the window (fade 0.15 s) and ≥ 2 cameras.
  - Placed bottom-center, 10 DIP inset, in a black 55% capsule: camera icon, current name, chevron. If the name doesn't fit, the label drops to the icon only.
  - The menu lists cameras by name, with a check on the current one.
  - Choosing a camera swaps the input on the running session, without a restart, and remembers it. An automatic fallback is never remembered.
- **Hot-plug** (observed only while shown):
  - If the selected camera disappears while running or starting: switch to the first remaining camera, or stop and show "No camera detected".
  - In "No camera detected", a newly appearing camera starts capture.

#### 3.5.7 Permission and dismissal

- **Permission.**
  - Granted → start.
  - Not yet asked → waitingPermission plus the system prompt. Usage text: "Vorssaint shows your camera only in the preview window you open, so you can check how you look before a call. Nothing is recorded or leaves your Mac."
  - While waiting for the answer, outside clicks and app activations do **not** dismiss.
  - After the answer: force alpha to 1 (fixes a mirror stuck mid-fade) and ignore app activations for **1.0 s**, because the previous app regains focus. Granted → start; refused → denied.
  - Camera is the only permission used. The feature never auto-starts and is not offered during onboarding.
- **Dismissal** (all suspended in waitingPermission; no fade-out):
  - Esc while focused;
  - a mouse-down outside the frame enlarged by 2 DIP, in any app, except on the on-screen keyboard;
  - any other app becoming active, outside the 1 s grace;
  - the hotkey.
- **Capture lifecycle.**
  - All session changes run on one serial worker; a start always stops any previous session first.
  - Runtime errors and interruptions → unavailable.
  - **Hide:** invalidate the capture generation (late callbacks are dropped), cancel queued configuration, remove monitors and device observers, stop and remove inputs, hide, and reset to idle.
  - Feature uninstall and self-uninstall also hide.
- **One capture at a time.** The island and the floating mirror never capture simultaneously; ownership transfers between them.

#### 3.5.8 Windows specifics

- **Exclusive use.** Windows cameras are often single-client.
  - Try `MediaCaptureSharingMode.ExclusiveControl`. If another app is streaming (e.g. `MF_E_VIDEO_RECORDING_DEVICE_LOCKED`), fall back to `SharedReadOnly`, which can view alongside but cannot choose a format.
  - On failure → "unavailable" with retry.
  - A meeting app may pre-empt the camera (`MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED`) → "unavailable".
- **Release promptly.** Disposing the capture on hide, together with hide-on-foreground-change, is what frees the camera for the meeting app.
- **Privacy settings.** Settings › Privacy & security › Camera has three switches: device access, "Let apps access your camera" and "Let desktop apps access your camera".
  - Unpackaged desktop apps get **no prompt**. Detect denial from `E_ACCESSDENIED` / `UnauthorizedAccessException` on initialization (or `DeviceAccessInformation`), and show the denied state with a link to `ms-settings:privacy-webcam`.
  - A privacy toggle or hardware shutter mid-stream → denied/unavailable state (`CameraStreamStateChanged`: BlockedForPrivacy / Shutdown).
- **Sleep and resume.** Hide on suspend, or treat the post-resume error as unavailable.
- **No Continuity Camera.** Phone Link cameras appear as ordinary devices.

### 3.6 Media tools

#### 3.6.1 Purpose and hosts

The **Media** workspace is one view with four tools, chosen with a segmented control: **Video**, **GIF**, **Image**, **Text**. The header reads "Media" with the note "Local. No network."

- **Video** trims and compresses a clip to MP4 (H.264/AAC). It has two sizing modes: "Resolution" or "File size".
- **GIF** turns a trimmed clip into an animated GIF.
- **Image** converts one image or a batch: format, quality, resize, background, metadata, watermark, rename pattern, saved profiles, quick presets, PDF output.
- **Text** runs OCR on one image, writes a TXT file and shows copyable text.

All work runs locally on one background worker, **one job at a time per service instance**. Every output is written to a temp file first and then moved into place atomically, so a failed or cancelled job never damages an existing file.

**Hosts** (the same view in four places):

1. **Tray panel** › Utilities tile "Media" ("Compress videos, convert and process images, make GIFs and extract text locally."). It replaces the list with a compact workspace; the header ✕ returns to the list. While shown, outside clicks don't dismiss the panel, so files can be dragged in.
2. **Quick Launcher** tile "Media" (inline compact workspace; Esc returns to the grid).
3. **Settings › "Media"** page: full-size workspace, no close button. Reachable from search keywords: PDF, GIF, PNG, JPEG, convert, resize, watermark, rename, profile, fit, fill, crop, "Convert to PDF", "Copy text from screen".
4. **Dynamic Island Files page** (macOS):
   - a drag-time chooser "Files" | "Optimize media";
   - a shelf "Media" (wand) button offering the compatible tools plus "Create ZIP";
   - results are added to the shelf automatically;
   - "Return to media" resumes a hidden session.

Not provided: no dedicated hotkey, no Explorer/Finder context-menu entry, no file association, no Command Bar action that runs a tool directly.

The Video tool also has an **"Edit"** button that hands one clip to the Screen Recorder's editor (specified elsewhere).

Jobs keep running after the popover or launcher closes. Reopening shows live progress or the last result, because the service is shared.

#### 3.6.2 Workspace layout

From top to bottom:

1. **Header.** "Media" (12 pt compact / 16 pt regular, semibold), the note "Local. No network.", and a close ✕ (22×22, tooltip "Cancel") when hosted.
2. **Tool picker** (segmented).
3. **Scrolling content.**
   - Height cap: 430 DIP in compact hosts; fills the page in Settings; in the island it reports its intrinsic height, which sets the island height (rounded up to a whole DIP).
   - Spacing: 10/14 (compact/regular) between header, picker and content; 9/12 between cards.
   - Cards: 10 padding, radius 10 (island: 12 padding, radius 18).
   - **File card.**
     - Input button (min height 52/62): icon, title, hint "Drop a file here or click to choose one."
     - Title is "Choose file", the file name, or "%d files selected" (Image batches).
     - A clear ✕ (tooltip "Cancel") when inputs exist.
     - Drop highlight: accent 16% fill and 70% stroke.
     - **Output row:** "Output", then the name or "Automatic", then a "Destination" button (folder icon). Disabled with no input or while running.
     - Image batches only: checkbox "Save in “Converted” subfolder".
   - **Options card** (per tool, below).
   - **Action row.**
     - Primary button: "Compress video" / "Make GIF" / "Process image" (or "Convert to PDF" when the format is PDF) / "Extract text". Disabled with no input or while running.
     - Video only: "Edit" (needs exactly one input; spinner while importing).
     - "Cancel" while running.
   - **Status card:**
     - progress: "Processing" + integer percent + a bar;
     - result: "Done" + "Saved as X" or a batch summary;
     - size line "A to B" in 1000-based units, then "N saved" / "N larger", plus "The converted file came out larger than the original." when the output grew;
     - up to 3 per-item failures "<input>: <error>";
     - an OCR text box (5/8 lines, monospaced, selectable; "No text found." when empty);
     - buttons "Show" (reveal outputs, selected), "Copy text" (OCR), "Copy summary" (batch), "Run again";
     - error and cancelled states (§3.6.9).

**Compression control** (shared by Video and Image):
- Title "Compression": three equal buttons "Low", "Medium", "High" (28/32 high) that set quality **0.88 / 0.68 / 0.28**.
- The highlighted button is the level nearest the stored quality.
- Description below: "High quality, large file size" / "Balanced quality and file size" / "Low quality, small file size".

#### 3.6.3 Common flow

1. **Choose input.**
   - The open dialog filters by tool: Video and GIF accept movies; Image and Text accept images. Multi-select is allowed only for Image; no folders.
   - Dropped files keep their drop order.
   - Files that don't fit the tool are filtered out. If nothing matches, or a non-Image tool gets any mismatching file, the state becomes failed with "Format not supported by macOS." (Windows wording needed).
   - Image silently keeps only the images; non-Image tools keep only the first file.
2. **On a new input:**
   - cancel any pending "Edit" import;
   - Image: read the first image's display size (EXIF orientation applied);
   - recompute the default output and clear any manual destination;
   - reset the trim to 0/0;
   - Video/GIF: load the duration asynchronously;
   - clear messages and cancel the service.
   - The input button is **not** disabled while running: choosing a new file **cancels the running job**. Clearing input or switching tools also cancels.
   - Switching tools keeps the current inputs even if they don't fit; running then fails with the tool's normal error.
3. **Duration defaults.** When the duration arrives (rounded to 0.1 s), end = duration and start = 0. A late reply for a superseded input or tool is ignored. When the view reappears, a lookup that already succeeded isn't repeated, so an edited trim survives.
4. **Destination.**
   - "Destination" opens a save dialog restricted to the output type (MP4 / GIF / JPEG/PNG/HEIC/PDF / TXT), prefilled with the current or default folder and name. The OS asks before replacing.
   - Image batches get a folder chooser instead.
   - With a manual single-image output, changing the format swaps the extension (numbered if taken).
   - Without a manual choice, every image option change recomputes the default name live.
5. **Run.**
   - With no input: "Choose a file first."
   - Constants the UI passes: video keepAudio = true and codec = H.264 (the stored fps/codec/keep-audio settings are unused); GIF quality 0.74; OCR language correction on.
6. **State machine.**
   - `idle → running(progress 0–1) → completed(result) | failed(error) | cancelled`.
   - `cancel()` publishes cancelled immediately.
   - Each job has an id, and updates from a stale job are dropped.
   - Starting a new job cancels the previous one: sets its flag, terminates its child process, cancels the OCR request.
7. **Closing a host** cancels only the duration lookup and the "Edit" import, never the export.

#### 3.6.4 Video tool

- **Input.** One movie file. Audio-only files fail with "This file has no video track."
- **Trim.** "Start" / "End" fields in seconds (one decimal, minimum 0). At run time:
  - start is clamped to [0, duration];
  - end ≤ 0 or non-finite means the full duration, else end is clamped to [start, duration];
  - a resulting length of 0 fails with the (misleading) unsupported-format message.
- **Sizing mode "Resolution"** (macOS uses Apple's `avconvert` presets):
  - **Compression levels:**
    - **Low** (quality 0.88, i.e. ≥ 0.82): "highest quality" preset at the source resolution, multi-pass. "Size" has no effect.
    - **Medium** (0.68): picks the smallest preset box ≥ L, where L is the longest side of the source scaled to "Size", rounded even and floored to a multiple of 16. Boxes: L ≤ 640 → 640×480; ≤ 960 → 960×540; ≤ 1280 → 1280×720; ≤ 1920 → 1920×1080; else the highest-quality preset. So "Size" 1600 on a 4K source yields 1920×1080.
    - **High** (0.28, i.e. < 0.4): Apple's "low quality" preset, a very small resolution. "Size" has no effect.
    - Stored values 0.4–0.58 (unreachable from the UI) map to the "medium quality" preset.
  - "Size" stepper: 640…3840 in steps of 320 (default 1280).
  - Output is H.264/AAC MP4 with fast start (index at the front), and privacy metadata (e.g. location) stripped. Audio is always kept.
  - Progress is *estimated*: every 80 ms, min(0.95, elapsed / max(1 s, 0.75 × trim length)); 1.0 on success.
  - The tool's log keeps the last 8,000 characters. A failure reports the log text, or "avconvert failed.".
- **Sizing mode "File size"** ("Target size" 1–512 MB, hint "Resolution adapts to stay under the limit."):
  - Target bytes = MB × 1,000,000.
  - A bitrate planner (§6.6) computes the video bitrate, audio bitrate (64 or 128 kbps) and output size. If no plan is possible: "Target size too small for this clip. Trim it or raise the limit."
  - **Encode:**
    - H.264 High profile (auto level) at the planned average bitrate, with the source frame rate as a hint (rounded, 1–60, default 30);
    - maximum keyframe interval 4 s; B-frames allowed;
    - frames scaled aspect-fill to the planned even size;
    - all audio tracks mixed to stereo AAC at 48 kHz;
    - MP4 with fast start;
    - the source rotation written as track metadata (frames encoded in natural orientation);
    - no source metadata copied.
  - **Passes.** After a pass, if bytes ≤ target, done. Otherwise compute a retry scale (§6.6) and re-encode. **At most 3 passes**; if they run out or can't shrink further, delete the partial file and fail with "too small".
  - **Progress** = appended frames / expected frames, capped at 0.99. It restarts from 0 each pass, and the UI updates only when the integer percent changes.
- **"Edit" handoff.**
  - Copies the single clip into a private take folder (`<app data>/Recordings/Take-<UUID>/take.mov`), only if ≥ 500,000,000 bytes would remain free.
  - Opens the Screen Recorder editor.
  - The take folder is deleted if the editor fails to open or the import is superseded.

#### 3.6.5 GIF tool

- **Input.** One movie file. Trim as for Video.
- **"Resolution" mode.**
  - FPS slider 1–30 (default 12).
  - "Width" stepper 160–1600, step 80 (default 720). Despite the label it caps the **longest edge**: frames are scaled so the longest side ≤ width, with even sides, never upscaled.
- **"File size" mode** (1–512 MB, default 10).
  - Start values: width = source display width clamped 160–1600; fps = max(6, min(15, ⌊300 / duration⌋)).
  - After each pass, if the file is too big, reduce **frames first, then pixels** (§6.6). At most **4 encodes**; if they run out or nothing can shrink → "too small".
- **Encoding.**
  - Frame count = ⌈duration × fps⌉, which must be 1–300. Otherwise: "A GIF can be up to %d seconds long", where %d = max(1, ⌊300 / fps⌋): 25 s at 12 fps, 10 s at 30 fps.
  - Frame i is taken at min(end, start + i/fps), **exact** (zero tolerance), with rotation applied, and redrawn at high interpolation as 8-bit RGBA.
  - Per-frame delay 1/fps, stored in 10 ms units (12 fps plays at ≈ 0.08 s per frame).
  - "Loop GIF" on → loop count 0 (forever). Off → no loop block (plays once).
  - No palette or dither options; the platform GIF encoder quantizes to 256 colors.
  - Progress = frames written / frame count, restarting each pass.

#### 3.6.6 Image tool

- **Inputs.** Any image type except PDF; the first frame or page only. SVG passes the filter but fails to decode. Multiple files are allowed.
- **Per-file pipeline:**
  1. Read the size with EXIF orientation (orientations 5–8 swap width and height).
  2. Compute the target size from the resize mode. Check it is safe: each side 1–20,000 px and area ≤ 67,108,864 px (8192²). Otherwise: "These dimensions are too large to process safely. Choose a smaller size."
  3. Decode downsampled to the needed size, orientation applied (§6.6); recompute and re-check the target.
  4. Render onto an 8-bit RGBA canvas in device RGB. The source ICC profile is **not** kept, and 16-bit/HDR content is flattened.
  5. Background:
     - Transparent: no fill, but JPEG and PDF are forced to white.
     - White or Black: that fill.
  6. Draw the image at high interpolation:
     - most modes: the full canvas;
     - Custom "Fit": an aspect-fit rect centered (letterbox shows the background);
     - Custom "Fill": an aspect-fill rect centered (cropped).
     
     Width, Height and Custom modes **can upscale**; "Max side" never does.
  7. Draw the watermark (§6.6 geometry).
  8. Encode:
     - **JPEG** (`.jpg`) and **HEIC** (`.heic`) at quality q (clamped 0.1–1).
     - **PNG** lossless (quality ignored, alpha kept).
     - **PDF**: the canvas encoded as JPEG at q, embedded in a one-page PDF whose page size equals the pixel size in points.
     - **Metadata.** "Remove metadata" **off** → copy all source properties (EXIF, GPS, IPTC, TIFF, DPI…) except orientation, with pixel size updated. **On** (the default) → copy nothing. The toggle is hidden for PDF.
  9. Commit atomically and clear the hidden attribute. If "Keep original modified date" is on, set the output's mtime to the input's.
- **Resize options:**
  - "No change".
  - "Max side" ("Size" 64–20,000, step 128, default 1600): scale = min(1, max ÷ longest side).
  - "Width" (1–20,000, step 64, default 1600): height proportional.
  - "Height" (step 64, default 1200): width proportional.
  - "Custom": Width × Height with "Stretch" (default), "Fit" or "Fill".
  - Sizes round to the nearest integer, minimum 1; they are not forced even.
- **Watermark** (Image only):
  - Kinds "Off" / "Text" / "Logo" / "Text + logo". Text counts only if non-empty after trimming; logo only when a path is set.
  - "Choose logo" picker. The logo row shows the file name or "No logo", with a clear ✕. If a logo kind is active and the file can't be decoded (decoded at most 2048 px), the **whole run** fails with "No logo".
  - Position "Top left" / "Top right" / "Center" / "Bottom left" / "Bottom right" (default bottom right).
  - "Opacity" 10–100%, step 5 (default 45%).
  - "Margin" 0–2000 px, step 8 (default 32). Quirk: a stored 0 becomes 32.
  - "Scale" 5–80%, step 1 (default 18%). It applies to the logo only.
  - Single line only: no tiling, rotation, color or font choice. Text is white with a dark shadow.
- **Rename pattern** ("Rename"; placeholder `{name}-{index:03}`; a "{…}" menu inserts tokens):
  - Tokens: {name}, {index}, {index:03}, {counter}, {date}, {time}, {datetime}, {width}, {height}, {format}.
  - Also accepted: {ext}, and {index:0N} / {counter:0N} for N = 2–6.
  - An empty pattern means {name}. Unknown tokens stay as literal text.
  - **Date and time tokens are UTC**. For a single image, the default name is computed when the input or options change, not at run time.
- **Profiles** (under "More options"):
  - Picker with "No profile" plus profile names. Selecting one applies all image options, including the logo path.
  - Trash button "Delete profile".
  - A "Modified" indicator when the current options differ from the selected profile.
  - "Profile name" field; "Update" overwrites the selected profile (renaming it if a name was typed); "Save new" appends with the typed name or "Profile %d" and selects it.
  - On load and save: blank names → "Profile <position>"; duplicate or blank ids get new UUIDs.
  - Quirk: one malformed profile hides all profiles.
- **Quick presets** (each replaces every image option and turns the watermark off):

  | Preset | Format | Quality | Resize | Strip | Rename | Background | Keep date |
  |---|---|---|---|---|---|---|---|
  | Web | JPEG | 0.72 | Max side 1600 | on | `{name}-web` | transparent | off |
  | Social | PNG | 0.82 | Max side 2048 | on | `{name}-social` | transparent | off |
  | Docs | PDF | 0.70 | Max side 1600 | on | automatic | white | on |

- **Preview.**
  - A thumbnail (96/128 DIP) in a frame shaped to the output aspect, inside 108×74 or 136×92.
  - Background follows the format: JPEG/PDF show white, or black if Black is chosen.
  - A watermark overlay at the chosen corner, scaled to the preview.
  - Labels "Preview" and "Output: <name>".
- **Batch.**
  - Files run sequentially in input order.
  - Output folder = the chosen folder, or by default **the first input's folder**, plus "Converted" if checked (created if missing).
  - Names come from the pattern with index = position (from 1) and the real output size, made unique against the disk and names already used in this run.
  - A failing item is recorded and the batch continues. With a single input, the failure ends the run. If every item fails, the first failure is shown.
  - Progress = items done / total. The summary counts original bytes of successful items only.

#### 3.6.7 Text (OCR) tool

- **Input.** One image within the same safety limits.
  - The image is decoded **without** applying EXIF orientation, so a rotated phone photo is read sideways (a likely bug: apply orientation on Windows).
- **Mode.** "OCR": "Accurate" (default) or "Fast". Language correction is always on.
- **Languages.** The app language plus English (§6.6 map), filtered to what the engine supports; if none remain, the engine default. No auto-detect, and no QR/barcode reading (that belongs to the separate Screen OCR feature).
- **Output.** The best candidate per recognized line, joined with "\n", written as UTF-8 TXT (no BOM) through the same temp-file commit. An empty file is written when nothing is found.
- **Progress.** 0 → 0.2 before recognition → done. Cancel cancels the request.

#### 3.6.8 Output naming and file safety

| Tool | Default output (in the input's folder) |
|---|---|
| Video | `<base>-compressed.mp4` |
| GIF | `<base>.gif` |
| Image (single) | `<pattern>.<jpg/png/heic/pdf>` |
| Image (batch) | folder = first input's folder (optionally `/Converted`); files `<pattern>.<ext>` |
| Text | `<base>-text.txt` |

- **`<base>`** = the input name without extension, trimmed, leading dots removed; "Output" if nothing remains. `.Clip.mov` → `Clip.gif`, `.mov` → `mov.gif`, `...` → `Output.gif`.
- The default image name avoids overwriting its own source: `photo.jpg` processed as JPEG becomes `photo 2.jpg`.
- **Name sanitizing.**
  - `/`, `:`, `\`, NUL, newlines and control characters → `-`.
  - Trim whitespace and leading/trailing `.`, `-` and spaces; empty → "Output".
  - Truncate on character boundaries to 255 bytes − (extension length + 1) − 4 bytes reserved for a number suffix.
  - Windows must also forbid `< > : " | ? *`, reserved device names (CON, PRN, AUX, NUL, COM1–9, LPT1–9) and trailing dots or spaces, and respect MAX_PATH unless long paths are enabled.
- **Collisions.** If the name exists, or was already used in this run, append " 2", " 3", … (unbounded), re-truncating the base so the suffix fits.
- **Commit.**
  - Each output gets a fresh temp directory **on the destination volume**, holding `Output.<ext>`.
  - The finished file is renamed over the destination (replace), or, in the island, renamed **without** replace (fails if the file exists).
  - The temp directory is always removed afterwards, which also removes partials on failure or cancel.
- **Same-file guard.** An output that is the same file as the input (same volume and file id; hard links and symlinks count) fails with "Choose a destination different from the original file."
- **Overwrite.**
  - A manually chosen file is replaced after the OS prompt.
  - "Run again" reuses the same output path: the shared service overwrites its previous result; the island fails ("File exists").
- **Hidden flag.** Outputs have it cleared unless the chosen name starts with ".".
- **Disk space.** No free-space check, except for "Edit" (≥ 500 MB must remain).

#### 3.6.9 Messages (English)

| Situation | Message |
|---|---|
| No input | "Choose a file first." |
| No video track | "This file has no video track." |
| Output equals input | "Choose a destination different from the original file." |
| Unsupported/undecodable, zero-length trim, rejected drop | "Format not supported by macOS." (rephrase for Windows) |
| Image too large | "These dimensions are too large to process safely. Choose a smaller size." |
| GIF too long | "A GIF can be up to %d seconds long" |
| Target too small | "Target size too small for this clip. Trim it or raise the limit." |
| Logo unreadable | "No logo" |
| Cancelled | "Cancelled." |
| Other | The system error text ("Video frame unavailable.", "Encoder unavailable.", "Video could not be read.", "Video could not be encoded.", the encoder log, "File exists"…) |

#### 3.6.10 Island extras and "Create ZIP" (macOS; port only with the island/shelf)

- **Drag-time chooser.** Offered only when every dragged item is a file (no text/link companions, no promises) and either all are images (any count) or exactly one is a video.
  - Two equal cards: "Files" (shelf) and "Optimize media".
  - While media work or a ZIP job runs, the right card shows "Processing" at 50% opacity and refuses the drop.
- **Create ZIP.**
  - Each selected item becomes its own `<name>.zip` (folders keep their parent folder), numbered on clashes, never overwriting. The destination may not be inside an input.
  - One item: save dialog. Several: folder chooser.
  - Hint "Each selected item is saved as a separate ZIP. Originals stay unchanged."
  - Progress "Create ZIP · n/m" with Cancel. Outputs are added to the shelf.
- **Island session.**
  - Never overwrites; the tool is held in memory.
  - Every completed output (including the OCR TXT) is added to the shelf.
  - File dialogs float one level above the island, one at a time.

### 3.7 Input fixes

This section covers eight input features built on low-level input interception. Two adjacent features share the same machinery and are covered briefly: side-button Back/Forward ("Mouse navigation") and "Disable mouse acceleration".

| Feature (hub name) | Hub description / UI title | Available by default (upgraders) |
|---|---|---|
| Extra click filter | "Extra click filter" | yes |
| Key debounce | "Debounce" / "Ignore accidental double key presses" | yes |
| Mouse button shortcuts | "Mouse button shortcuts" | yes |
| Smooth scrolling | "Smooth scrolling" / "Smooth, animated mouse scrolling" | yes |
| Linear scrolling | "Linear scrolling" | no (opt-in) |
| Scroll sideways while holding a key | "Scroll sideways while holding a key" | no (opt-in) |
| Scroll direction inverter | "Invert mouse scrolling" | yes |
| Super key | "Turns one key into the modifier combination you choose." | yes |
| Quit & close protection | "Protects ⌘Q and ⌘W from accidental presses" | yes |
| Side buttons (Back/Forward) | "Side mouse buttons go back and forward" | yes |
| Disable mouse acceleration | "Disable mouse acceleration" | yes |

#### 3.7.0 Shared input core

**Gating.** Each service runs only when **all** of these hold:
1. The feature is available.
2. Its enable switch is on.
3. The input permission is granted (macOS Accessibility). Mouse acceleration is the exception; it needs none.
4. The login session is on the console.

Each service exposes an idempotent `syncWithPreferences()`. It is called from Settings, the tray panel, the Command Bar, permission changes, session changes and the end of Cleaning Mode.

**Suspend.** `suspend()` tears a service down regardless of preferences. It is called on app quit, self-uninstall and Cleaning Mode. Cleaning Mode suspends the click filter, key debounce, navigation, button shortcuts and the radial menu, but not scrolling.

**Pipeline order on macOS** (HID-level filters run before session-level ones; within a level, head-inserted run first):

| Order | Stage | Feature(s) |
|---|---|---|
| 1 | HID head, dedicated thread | Key debounce (keys); click filter (left/right/middle buttons) |
| 1 | HID head, main thread | Side-button navigation (buttons 3/4); smooth scroll (wheel) |
| 1 | HID head | Super key mouse-button stamping |
| 2 | HID tail, "pointer" thread | Scroll inverter + linear scroll + horizontal redirect (raw wheel path) |
| 3 | Session head | Super key (keys); Quit protection (keys, main thread); button shortcuts + side wheel + Spaces drag |

**Own events.** Each stage passes events the app itself synthesized:
- smooth-scroll frames and replayed presses carry the tag `0x564F5253`;
- redirected wheel events carry `0x564F5248`;
- Quit protection re-posts carry `0x5652535341494E54`;
- otherwise matched by the source PID being the app's own.

Events injected by *other* processes are treated like hardware events: debounced, filtered and inverted.

**Mouse wheel vs touch classification** (shared by every wheel consumer):
- Discrete events are wheels.
- Continuous events with a scroll phase or momentum phase are touch (trackpad or Magic Mouse).
- Phaseless continuous events with a nonzero scroll count arriving within **1.0 s** of the last phased event are touch (the trackpad's "transition" event).
- Everything else is a wheel. This includes mice whose drivers emit continuous pixel scrolling.
- There is no per-device information, so two mice merge into one stream.

**Timestamps.** At the HID stage, hardware events carry machine ticks (125/3 ns on Apple Silicon) while software-posted events carry ns. For each event the code picks whichever reading is nearer the current uptime, saturating instead of overflowing. Without this, a 50 ms debounce window would last about 2.1 s (issue #1689). On Windows, use `QueryPerformanceCounter` taken at hook entry; `KBDLLHOOKSTRUCT.time` is ~15.6 ms granular.

**Filter-disabled recovery.** macOS disables a filter whose callback is too slow, or on certain user input.
- Each service resets its transient state.
- It re-enables the filter only if it is still wanted, the permission is still granted and the session is active. Otherwise it tears down and re-syncs on the main thread, never from inside the callback.
- A filter that fails to create during a session hand-off is retried once after 0.5 s (inverter and smooth scroll).

**Windows architecture** (recommended):
- **One** WH_KEYBOARD_LL hook and **one** WH_MOUSE_LL hook, on a dedicated high-priority thread with its own message loop, running an **ordered internal pipeline**: Debounce → Click filter → Super key → Quit protection → Button shortcuts / Spaces drag → Smooth scroll / Inverter / Horizontal redirect → other features. Windows calls hooks last-installed-first, so separate hooks can't be ordered.
- Hook callbacks must be allocation- and lock-free with no I/O. Since Windows 7, a hook that exceeds `LowLevelHooksTimeout` (at most 1 s from Windows 10 1709) is **silently removed**. Detect that with a Raw Input side channel (`RIDEV_INPUTSINK`) and reinstall, resetting all held and pending state.
- Tag injected events with `dwExtraInfo` and skip own-tagged events in every stage (`LLKHF_INJECTED` / `LLMHF_INJECTED` identify injected events in general).
- Per-feature work that needs I/O (app identity lookups, settings reads) runs on worker threads, and the hook reads lock-free snapshots.

#### 3.7.1 Extra click filter (mouse click debounce)

- **UI.** Settings › Mouse & Trackpad card "Extra click filter" ("Ignores rapid extra clicks from worn mouse buttons without slowing normal clicks.").
  - When on: stepper "Filter window" showing "N ms" (5–100, step 1, default **25 ms**), caption "A repeated click inside this interval is treated as an accidental duplicate."
  - Tray panel row and Command Bar toggle.
  - No exception list.
- **Covered buttons.** Left (0), right (1) and middle (2) only, each with independent state. Extra buttons are never filtered, so they keep their navigation, shortcut and gesture behavior. Trackpad clicks are filtered too (no device discrimination).
- **State per button:** `acceptedDown`, `suppressedDown`, `lastAcceptedUp` (ns), `lastEventTimestamp`. W = window in ns.
  - **Down:**
    1. If `acceptedDown` is set (the accepted press is still held) → suppress. A duplicate down; the press's eventual up still belongs to it.
    2. Else if `suppressedDown` is set → suppress. Extra downs before the bounce's up share that up.
    3. Else if `lastAcceptedUp` exists and 0 ≤ t − lastAcceptedUp < W (strict) → set `suppressedDown` and suppress.
    4. Else set `acceptedDown` and pass.
  - **Drag:** suppress only if `suppressedDown` is set, so an app never receives a drag without its press.
  - **Up:**
    - If `suppressedDown` is set → clear it and suppress.
    - Else if `acceptedDown` is not set → pass. This is an unmatched up (e.g. after recovery); fail open.
    - Else clear `acceptedDown`, set `lastAcceptedUp = t` and pass.
  - A timestamp earlier than the button's previous one resets that button first.
  - While disabled, each button's state is erased on its next event and everything passes, so a pending suppressed up passes too.
- **Consequences.**
  - The window is measured from the last *accepted* up, so a chain of bounces can't extend it. A boundary press at exactly W is accepted.
  - Both the extra down and its paired up are swallowed, together with drags in between.
  - Healthy clicks are **never delayed**: there are no timers and no delayed events (test-asserted).
  - Real double-clicks (80–200 ms gaps) pass at the 25 ms default. At the 100 ms maximum, fast double-clicks can be eaten.
  - Contact noise mid-press isn't repaired. A spurious up passes; the re-closing down counts as a bounce, so the rest of that physical press is swallowed.
- **Lifecycle.** Every sync resets all state. Sleep resets state, and wake rebuilds the filter. It runs on its own thread.
- **Windows.** Implement in WH_MOUSE_LL for WM_LBUTTON*/WM_RBUTTON*/WM_MBUTTON*.
  - **Never swallow WM_MOUSEMOVE**; with the down swallowed, apps just see moves.
  - Ignore XBUTTONs.
  - LL hooks never see `*DBLCLK` messages; the OS derives double-clicks from the surviving events.
  - Use QPC timestamps.

#### 3.7.2 Key debounce

- **UI.** Settings page "Debounce":
  - Toggle "Filter duplicate keys", caption "Filters very fast duplicate key presses.", and a green "Filter active" when the filter is actually running.
  - Stepper "Global window": **0–500 ms**, step 1, default **5 ms**. 0 = accept everything.
  - Section "Specific keys": "Per-key values override the global window. Use 0 ms to stop filtering a key."
    - A key picker (52 common keys), a ms stepper (0–500, starts at 5) and "Add key". Adding an existing key overwrites it.
    - A list sorted by key label, each row with a stepper and a trash button ("Remove key"). "No specific keys configured." when empty.
  - Tray panel row with the caption "Global window: N ms" and an inline stepper.
- **Per-key state:** isDown, lastAcceptedPress, lastAcceptedRelease, lastEventTimestamp. Plus one global `lastAcceptedKeyCode`.
- **Algorithm:**
  - **Disabled:** forget this key's state; pass.
  - **Staleness:** if this key's previous event is more than **5 s** older, or the timestamp went backwards, forget this key's state (and clear `lastAcceptedKeyCode` if it pointed here).
  - **Key-up:** if down, mark up and record the release time. **Always pass.**
  - **Auto-repeat key-down:** mark down and pass. Repeats are never filtered and don't refresh `lastAcceptedPress`.
  - **Window** = the per-key override if present, else the global window. Window 0 → accept.
  - **Rule 1 (duplicate down):** the key is still marked down and now − lastAcceptedPress < window → suppress.
  - **Rule 2 (bounce after release):** a release is recorded, this key is the **most recently accepted key**, and now − release < window → suppress.
  - **Otherwise** accept: mark down, record the press, lastAccepted = this key.
- **Consequences.**
  - Comparisons are strict: a press exactly `window` after the release is accepted.
  - Rule 2 only applies when no other key was accepted since, so fast "e r e" typing is never filtered.
  - A suppressed down leaves the key "up", so its later key-up passes as an orphan: apps see down, up, up.
- **Never filtered:**
  - auto-repeat;
  - different keys;
  - key-ups;
  - **modifiers and lock keys** (macOS delivers them as flag changes, outside the filter's mask);
  - the app's own synthetic keys.
- **No** app exceptions, statistics or secure-input handling.
- **Lifecycle.** HID-level filter on its own thread. Suspended during Cleaning Mode, which needs the Esc ×5 gesture.
- **Migration.** A stored window of 30 or 10 (old defaults), with the feature off and no per-key windows, becomes 5.
- **Windows.**
  - **Exclude modifiers and lock keys explicitly** (L/R Shift, Ctrl, Alt, Win, Caps, Num, Scroll); Windows reports them as ordinary keys.
  - Windows doesn't flag auto-repeat. Treat a down-while-down that arrives at or after the keyboard repeat delay (`SPI_GETKEYBOARDDELAY`, 250–1000 ms) minus a tolerance as a repeat; otherwise apply Rule 1. The 500 ms maximum window exceeds the minimum repeat delay.
  - Store per-key overrides by **scan code** (§6.7 gives the macOS-keycode → scan-code table for imports).
  - FilterKeys "BounceKeys" (`SPI_SETFILTERKEYS`) exists natively but is one global value with accessibility side effects. Prefer the hook; optionally warn when BounceKeys is on.

#### 3.7.3 Mouse button shortcuts, side wheel, Spaces drag, side-button navigation

**Button numbering.** 0 left, 1 right, 2 middle, 3 Back, 4 Forward, 5–31 further buttons. Mappings start at 3. Side-wheel (tilt or thumb wheel) directions are stored as pseudo-buttons −2 ("Side wheel left") and −1 ("Side wheel right").

**Display names:**

| Button | Name |
|---|---|
| 3 | "Back side button" |
| 4 | "Forward side button" |
| n ≥ 5 | "Button %d" with n+1, matching mouse software (CG button 5 = "Button 6") |
| −2 / −1 | "Side wheel left" / "Side wheel right" |

Rows sort numerically: −2, −1, 3, 5, 12…

**What can be bound.** Only a **single key combination** (virtual key + modifiers). No app launch, macros or mouse actions.
- The recorder requires Ctrl/Opt/Cmd with a key, or a bare F1–F20. Errors: "Use at least Control, Option or Command with a key." / "Nothing was captured. macOS or another app already uses that combination. Try another one."
- Invalid stored entries are dropped on decode.

**Settings flow.**
- Switch "Use extra buttons as shortcuts". Caption: "Each extra button or side-wheel direction can press a key combination for you. While it has a shortcut, it stops doing what it did before."
- Empty state: "No shortcuts yet. Add a button or side-wheel direction."
- "Add a button or side wheel" starts **capture**: "Now press an extra button or move the side wheel." With "Cancel" and the hint "If nothing happens, your mouse's own software may already be using that control."
  - If the service isn't running: "Vorssaint cannot watch the mouse right now."
  - Refusals:
    - "That input cannot take a shortcut. Use an extra button or a side-wheel direction."
    - "That button already opens the radial menu. Pick another one, or free it there first."
    - "That button or direction is already on the list below."
- A captured input shows a pending row with "Set shortcut". Nothing is saved until a combination is recorded.
- A mapped button that the radial menu also claims shows "This button opens the radial menu now, so the shortcut waits."
- Turning the switch off stops capture; mappings are kept but inert.
- **During capture**, every extra-button down is reported to the UI and consumed (down, drag, up), so it can neither navigate nor fire an old mapping. The middle button is reported, then passed.

**Firing rules** (extra-button down, session stage):
1. Pass own replayed presses. While draining (below), pass everything new.
2. Capture handling as above.
3. If the app under the pointer **or** the frontmost app is on the button-shortcut exception list, pass the whole gesture.
4. If the button is the Spaces-drag button, run the drag logic below.
5. Fire if all hold: the feature is available, the switch is on (re-read on every press), the radial menu doesn't claim the button, and a mapping exists. Then mark the button consumed, post the shortcut and drop the down. Otherwise pass.
6. **Ups and drags follow their down's decision**, so a mapping edited mid-press can't split a gesture.
7. The shortcut fires **once, on press**: key down then key up immediately, with no auto-repeat while held. Nothing happens on button up.

**Posting a shortcut.** Key down/up with the virtual key and modifier flags (plus the Fn flag for F-keys, the navigation block and arrows, and the numpad flag for arrows and keypad), posted at the hardware level so global listeners see it. No Unicode string is attached; that breaks menu key-equivalent dispatch.

**Ownership with other features.** A button is *owned* by this feature when it has an active shortcut, or is the Spaces button. Navigation passes owned buttons through. **The radial menu always wins** over both.

**Drain mode.** If the filter must be rebuilt (e.g. the event mask changes because a side-wheel mapping was added) while a consumed button is held, it stays alive only to swallow that pending up, then rebuilds. After an OS disable, custody is kept only for buttons still physically down.

**Side wheel.**
- **Classification:** apply the wheel classification first.
- **Delta per axis:** continuous events use point, else fixed, else line; discrete events use fixed, else line, else point.
- **Direction:** produced only if the horizontal delta ≠ 0 and |h| > |v|, so vertical and diagonal scrolling never trigger it. Positive horizontal (macOS: leftward) → "left" (−2); negative → "right" (−1).
- **Burst gate:** events ≤ **250 ms** apart form one burst, and each event extends the burst. Within a burst each direction fires **at most once**. A gap over 250 ms, or a backwards timestamp, starts a new burst. A held, auto-repeating tilt therefore fires once until a 250 ms quiet gap.
- **Mapped and not excepted:** post on the first event of the burst and swallow the rest of the burst. Unmapped: pass.
- **Smooth scroll** leaves events claimed by a side-wheel mapping untouched.
- **Quirk:** with "Invert horizontal scrolling" on, the inverter runs earlier, so the mapping sees the flipped sign. The port should read the **raw** direction.

**Spaces drag** (issue #1012; Windows: virtual desktops):
- **Settings.**
  - Switch "Switch Spaces by dragging a button". Caption: "Hold the chosen button and drag: left or right moves one Space over, up opens Mission Control, down opens App Exposé. A short click still does what it always did."
  - "Choose a button" capture: "Now press an extra button." Refusals:
    - "That input cannot be held for a drag. Use an extra button." (side wheel, middle, out of range)
    - the radial message
    - "That button already has a shortcut. Pick another one."
  - Toggle "Spaces follow the drag": "Dragging right brings the Space on the left, the way a trackpad swipe carries it along with your fingers." (swaps left/right only).
  - An orange warning when all four system Mission Control shortcuts are disabled: "The Mission Control keyboard shortcuts are switched off in System Settings, so this gesture has nothing to ask for."
  - Turning the switch off clears the bound button.
- **Binding rule.** The feature is available, the switch is on, the button is in 3…31, it has no active shortcut, and the radial menu doesn't claim it. It works even with the shortcut switch off.
- **Flow.**
  1. On the bound button's down (after the exception check), keep a **copy** of the down, drop the original, and record the pointer origin and the follows-drag value (read once per press).
  2. Each drag of that button is dropped and fed to the tracker.
  3. The first action marks the button consumed (its up will be swallowed) and performs the mapped system command.
  4. On up:
     - If **nothing fired**, replay the held down at the release point (timestamp now, tagged), ahead of the up, then pass the up. The app sees one short click at the release point. Click-and-hold on that button is lost, because the press is delayed until release.
     - If something fired, swallow the up.
  5. A new down while a held copy exists silently replaces the stale copy.
  - No cancel gesture; dragging back doesn't undo.
- **Tracker** (pure function of pointer positions; smaller y = up):
  - Accumulate ΣX and ΣY.
  - **Axis commit** at the first firing: h = |ΣX| / 220 and v = |ΣY| / 150. Horizontal if h ≥ 1 and h ≥ v; else vertical if v ≥ 1. The axis is then fixed for the press.
  - **Vertical** fires once per press: ΣY < 0 → Mission Control (Windows: Task View); ΣY > 0 → App Exposé (no Windows equivalent).
  - **Horizontal:** within 0.35 s of the last firing, ΣX is clamped to ±220 (one step banked) and nothing fires. Otherwise, when |ΣX| ≥ 220, fire Space left (ΣX < 0) or right, subtract one step keeping the surplus, and restart the cooldown.
  - Distance only, no velocity threshold. Travel stops counting when the pointer is pinned against a screen edge.
- **Performing.** macOS reads the user's own system shortcuts (symbolic hotkeys 79/81/32/33) and posts them; a disabled shortcut does nothing. Windows: Ctrl+Win+Left/Right for desktops and Win+Tab for Task View. Scale the 220/150 thresholds by the monitor DPI scale.

**Side-button navigation** ("Use side buttons for Back and Forward"; adjacent feature):
- Buttons 3/4 press the frontmost app's Back/Forward menu command (the item with ⌘[ / ⌘]) through Accessibility, falling back to typing the keystroke.
- Automatic pass-through:
  - VMs and remote desktops (Parallels, VMware Fusion, UTM, VirtualBox, Screen Sharing, Microsoft Remote Desktop, CrossOver, Whisky, Moonlight);
  - Firefox;
  - any non-Apple app registered for both https and HTML;
  - the exception list.
- **Windows: drop this feature.** XBUTTON1/2 already produce `APPCOMMAND_BROWSER_BACKWARD/FORWARD`. Users can map XBUTTONs to Alt+Left/Right via button shortcuts if needed.

**Windows mapping notes.**
- Mappable inputs are XBUTTON1 (stored as "3"), XBUTTON2 ("4") and horizontal wheel left/right ("-2"/"-1").
- Buttons 6+ aren't delivered by the standard mouse stack (Raw Input `RAWMOUSE` defines 5 buttons); show the capture hint about vendor software.
- **Never swallow WM_MOUSEMOVE** for the drag gesture (that would freeze the cursor); swallow only the XBUTTON down/up.
- Replay a never-fired press as a tagged down+up pair.

#### 3.7.4 Smooth scrolling, linear scrolling, horizontal modifier, acceleration

**Settings.**
- "Smooth scrolling" ("Turns each mouse wheel step into a short, gentle glide. The trackpad is not affected.").
- Slider "Scrolling speed" 20–100, step 10 (default **40** px per tick).
- Disclosure "More options": "Response" 0–100 %, step 5 (default **65**), and "Coast" 0–100 %, step 5 (default **0**).
- Exception list: "The wheel keeps its plain steps in these apps, for apps that read it their own way, like 3D and design tools."
- Changes apply on the next wheel tick (response and coast are re-read every tick). No tray panel row.

**Per-event flow** (HID head):
1. Filter disabled → stop the glide; re-arm or tear down.
2. App-switcher scroll navigation or the screenshot loupe active → stop the glide; pass.
3. Own frames → pass.
4. Not a mouse wheel (trackpad, Magic Mouse, momentum) → **pass untouched**.
5. Side wheel claimed by a button shortcut → pass.
6. App excluded from smooth scroll (pointer target or source process) → pass raw. The inverter can still invert it later.
7. If the inverter runs and the app isn't excluded from scroll direction: apply inversion here (and the horizontal redirect if enabled, consuming the modifier).
8. **Control held** → pass raw (native zoom). The inverter may still flip it.
9. Compute per-axis distances (§6.7). If both are zero, pass.
10. Reset the engine and pixel carries when any of these changed since the last tick: Shift state, redirect state, or discrete-vs-continuous device type.
11. Drop an axis's carry if its direction reversed. Add the distances to the engine, remember the event's flags (Cmd/Opt survive; a consumed redirect modifier doesn't), start the frame scheduler, and **swallow** the tick.

**Distance per tick:**
- **Discrete wheel:** ticks = the fixed-point delta (fractions from high-resolution wheels), else the line delta; distance = ticks × step.
  - macOS already embeds wheel acceleration in the fixed-point delta (≈ 0.1 line per slow notch, ≈ 0.4 medium, ≈ 7.3 fast spin), so the glide inherits OS acceleration. Vorssaint adds none.
  - With Shift held on a vertical-only discrete tick, the tick moves to the horizontal axis.
- **Continuous phaseless wheel:** pixels = point delta, else fixed × 10; distance = pixels × step / 40. So the default step travels exactly the native distance. No Shift swap; apps react to the replayed Shift flag.
- **Linear scrolling** on and not excepted:
  - Discrete: lines = sign × min(|ticks|, 1) × linesPerNotch; distance = lines × step (3 × 40 = 120 px per notch by default, at any spin speed).
  - Continuous: distance = lines × 10 × step / 40 (30 px at defaults; note the 4× difference).
- Each axis is multiplied by −1 when its inversion applies.

**Engine.**
- Per axis: `remaining` and `glide`. Same-direction input accumulates; a reversal replaces that axis's tail, so the first opposite tick answers at once.
- Each frame emits an exponential-decay share of `remaining`, with a minimum speed and a coast stretch near the end (exact formula in §6.7).
- Equal elapsed time gives identical results at 60 Hz and 120 Hz (test-pinned).

**Scheduler.**
- Driven by the refresh rate of the display under the pointer, with a 1/60 s timer fallback.
- Each frame splits distance + carry into whole pixels: truncated normally, rounded to nearest on the landing frame. The carry resets when the glide lands.
- **Synthetic event:** pixel units, two axes, continuous by construction, no phases. Fields clamped to ±1,000,000. Tagged; carries the last real tick's flags; posted at HID level.
- The frame that finishes the budget stops the scheduler. Sleep stops the glide. A display change rebuilds the scheduler.

**Linear scrolling in the raw path** (used when smooth scrolling is off or the event passed it):
- Skipped for native Control-zoom.
- Discrete events: only the line field is written, trunc(lines + carry), keeping the fractional carry. This removes a high-resolution wheel's extra fraction.
- Continuous events: points = whole(lines × 10 + carry).
- The carry is dropped on reversal, while excepted, and while the feature is off.
- An event with movement that rounds to zero on both axes is **swallowed**: the fraction carries over, so four quarter-notches yield exactly 3 lines.
- Settings: "Linear scrolling" + "Lines per step" 1–10 (default 3) + exception list ("The wheel keeps the pace macOS gives it in these apps, for games and 3D tools that count the notches themselves.").

**Horizontal modifier** ("Scroll sideways while holding a key"; "Hold only the selected modifier to scroll the vertical mouse wheel horizontally. Other key combinations are unchanged."):
- Keycap picker: Shift (default), Option, Control, Command.
- Fires only when **all** hold:
  - the event's {Shift, Option, Control, Command} set equals exactly that modifier (Caps Lock etc. ignored);
  - the event is vertical-only;
  - the pointer is not over one of Vorssaint's own capture or editor windows (an own-window cache, 0.5 s snapshots).
- Line, point and fixed values move from axis 1 to axis 2 with the sign kept. The modifier is **removed** from the event, so the app can't zoom or redirect it again, and the event is tagged.
- With Control as the key, Control-wheel scrolls instead of zooming.
- Inversion afterwards uses the horizontal setting. The scroll-direction exception list also disables the redirect.

**Own sideways strips.** Inside Vorssaint's own windows, a plain vertical wheel over a horizontally-only scrolling list scrolls it sideways. Port this in the UI framework.

**Disable mouse acceleration** ("Removes pointer acceleration for connected mice. Your previous setting returns when this is turned off or Vorssaint quits."):
- macOS: per-device HID property change for mice only (not trackpads), re-applied after hotplug at 0, 0.25, 0.75, 1.5 and 2.5 s. The originals are journaled for crash recovery, restored by a guard child process if the app is killed, and restored on sleep and session resign.
- **Windows:** `SystemParametersInfo(SPI_SETMOUSE)` with acceleration off ("Enhance pointer precision" off).
  - It is **global, not per device**.
  - Apply with `SPIF_SENDCHANGE` only (not `SPIF_UPDATEINIFILE`) so it is session-scoped.
  - Restore on exit, with a journal for crash recovery.

**Windows smooth-scroll notes.**
- Express the budget in notch-relative units: 1 notch = `step` px, so the default 40 equals one 120-unit notch. Emit round(units × 3) wheel units per frame with carry, using `SendInput` with `MOUSEEVENTF_WHEEL`/`HWHEEL` and sub-120 deltas.
- Modern apps (browsers, UWP, WinUI/WPF) handle sub-120 deltas; classic Win32 controls accumulate to 120; some old apps treat every message as a full notch, which is why the exception list matters.
- Windows wheels have **no OS acceleration**. Either accept constant per-notch distance, or add an explicit, documented curve based on the inter-notch interval (a new design decision).
- **Don't swap axes on Shift.** Inject vertical events with Shift still held and let the app do its own Shift-scroll.
- Pass Ctrl+wheel raw (zoom).
- Pace frames with a high-resolution waitable timer or `DwmFlush` at the cursor monitor's refresh rate.

#### 3.7.5 Scroll direction inverter

**Settings.**
- Card "Scrolling" with "Invert vertical scrolling" and "Invert horizontal scrolling" (independent, **mouse only**).
- Badge "Inverting mouse scrolling right now" while active.
- Note "The trackpad is untouched: it keeps macOS natural scrolling."
- Exception list: "The wheel keeps the direction macOS gives it in these apps." Shown while inversion or the horizontal modifier is on.
- The tray panel row "Invert mouse scrolling" ("Reverses the mouse wheel direction.") sets both axes.
- Migration: if the horizontal key was never saved, copy the vertical value.

**Raw path** (HID tail, pointer thread). Runs while any direction feature or linear scrolling is wanted.
1. Skip own glide frames.
2. Pass touch events.
3. Direction applies when inversion or the redirect is enabled and the app isn't excepted.
4. Optional linear cap.
5. Apply direction.

**Inversion plan.**
- **Shift redirect case:** when the event is discrete, Shift is held and it is vertical-only, macOS turns it sideways above the filter. So the vertical source is flipped by the **horizontal** setting, matching what the user sees.
- Otherwise the vertical axis follows the vertical setting and a real horizontal axis follows the horizontal setting; two-axis events keep each axis independent.
- **Flipping a discrete event** negates only the line field, unless the line is 0 (a high-resolution fraction). Continuous or redirected events negate line, point and fixed.
- Control-zoom events are flipped too.

Wheels that glide are inverted inside smooth scrolling, honoring the same list; the raw path ignores the glide frames, so the two never cancel.

**Windows.**
- Implement in the mouse pipeline by negating `mouseData` for wheel events that come from a **mouse**.
- Telling a mouse from a Precision Touchpad is the **top risk**: the LL hook can't tell. Correlate with Raw Input (`RIM_TYPEMOUSE` with `RI_MOUSE_WHEEL` and a device handle; touchpads are HID digitizers, usage page 0x0D) or rely on the absence of touchpad pointer input. Prototype early.
- Precision Touchpads already have their own direction setting in Windows.
- Native alternative: `FlipFlopWheel`/`FlipFlopHScroll` = 1 under the mouse's `HKLM\SYSTEM\CurrentControlSet\Enum\HID\<VID_PID>\<instance>\Device Parameters`. It is per device and mouse-only, but needs admin and a replug, and has no per-app exceptions. Could be offered as a "native mode".
- `WM_MOUSEHWHEEL` positive = right, which is the opposite of macOS; map signs carefully.

#### 3.7.6 Super key

**Purpose.** Caps Lock, or one right-side modifier, becomes a held "virtual modifier" standing for a chosen modifier set (default all four: ⌃⌥⇧⌘). A solo tap can run an action.

**Settings.**
- Section "Super key":
  - Toggle "Use this key as the super key".
  - Picker "Key to hold": "Caps Lock", "Right ⌘", "Right ⌥", "Right ⌃", "Right ⇧".
  - Caption "Hold it and press any key. Choose one or more modifiers below."
  - Info "Keep this key set to its default action in System Settings › Keyboard › Modifier Keys."
  - A diagram: the source keycap "Hold" → four toggle keycaps ⇧ ⌃ ⌥ ⌘. A keycap is disabled if deselecting it would leave no ⌃/⌥/⌘; Shift alone is never allowed.
  - Status line, by priority:
    1. orange failure text;
    2. "Paused while a selected app is open";
    3. green "Working now".
- "A tap on its own" radio group: "Nothing" / "Turn capitals on and off" / "Switch input source; hold for Caps Lock" / "Press Escape". Caption "What a quick tap does when no other key is pressed."
- "Apps to leave alone": "While any of these apps is open, even in the background, Super Key pauses and the chosen key works normally."
- Tray panel row: "Super key", caption "<source> holds <mods>." (e.g. "Caps Lock holds ⌃⌥⇧⌘."), "Set up…".
- Failure texts:
  - "Another app's key mapping uses the selected key. Remove it in that app: quitting it is not enough."
  - "macOS refused the key mapping. Reconnect the keyboard or restart the Mac, then switch this on again."
  - "macOS would not let Vorssaint watch the keyboard. Turn Vorssaint off and on in System Settings › Privacy & Security › Accessibility, then switch this on again."

**macOS mechanism (for reference).**
- macOS can't see Caps Lock being *held*, so the app remaps the source key to **F18** for all keyboards via the system HID key-mapping table. It preserves foreign mappings and refuses on conflicts. Write-ahead markers are saved, and the table is read back to confirm.
- A session-level filter swallows F18 and ORs the configured modifier flags onto every other key event (downs and ups) while F18 is held. A HID-level filter does the same for mouse-button presses, so drag chords work.
- A guard child process removes the mapping if the app is killed. Write-ahead markers let the next launch clean up. The mapping is removed when the feature stops, pauses or the app quits.
- When starting on Caps Lock, the Caps Lock state is forced **off**.
- A raw source key arriving (e.g. a newly plugged keyboard) triggers a mapping repair, throttled to once per 3 s. Wake re-applies the mapping.

**State machine** (State: isHeld, isAlone, downTimestamp, didRepeat):
- **Trigger down:**
  - Not held and a repeat → swallow and change nothing (a stray repeat can't revive a press).
  - Not held: isAlone = no ⌃⌥⇧⌘ already held; record the timestamp if alone; didRepeat = false.
  - Held and a repeat: didRepeat = true.
  - Set held. Swallow.
- **Trigger up:**
  - wasAlone = held ∧ alone.
  - wasLong = wasAlone ∧ (up − down ≥ **500 ms**).
  - Reset.
  - Result: wasLong → solo-hold; wasAlone → solo-tap; else swallow.
- **Other key (down or up) or mouse button down:** not held → pass. Held → alone = false and **add the modifiers**.
- **Other modifier change:** if held, alone = false. Always pass.
- **Raw source key** (keyboard not mapped): swallow, attempt repair, force Caps Lock off.
- **Reset:** everything cleared (teardown, timeout, watchdog).

**Solo effects:**

| Action | Tap < 500 ms | Hold ≥ 500 ms | A repeat seen during the press |
|---|---|---|---|
| none | — | — | — |
| escape | Escape | Escape | nothing |
| capsLock | toggle Caps Lock | toggle Caps Lock | nothing |
| inputSource | next input source | toggle Caps Lock | repeats ignored |

F18 doesn't auto-repeat on macOS, so in practice Escape and Caps Lock fire after any solo press.

**Executing solo actions:**
- **Escape:** post Esc down/up with cleared flags.
- **Caps Lock:** toggle the lock state (also changes the LED).
- **Input source:** select the next enabled keyboard input source, wrapping. Unknown current → first; fewer than 2 sources → nothing. This happens synchronously, before the filter returns, so the next keystroke already uses the new source.

**Held-key watchdog.**
- On each trigger down, schedule a check after clamp(2 × keyboard repeat delay, 3 s, 30 s) (≈ 3 s typically).
- At the deadline, re-arm only if the physical key is still down, the state is still held and the filter is alive. Otherwise forget the hold, which also notifies the radial menu.

**Exceptions.** While **any listed app is running** (even in the background), the whole feature pauses (mapping removed, key works natively) and resumes when the last one exits. A process matches by its id, the id of an enclosing app bundle (helpers inherit), or its resolved executable path.

**Interop.**
- The app's own global hotkeys see the stamped modifiers. With the set ⌃⌥⌘, Super+letter therefore triggers the app's default tool shortcuts.
- Shortcut rows show "or <source> + <key>" alternatives when the shortcut's modifiers equal the super set exactly.
- The shortcut recorder records Super+key as the full modifier combination.
- The radial menu's held mode follows the Super key's hold.

**Windows redesign** (major):
- **Drop** the remap, F18 intermediate, mapping readback and the "Modifier Keys" caveat. The LL hook sees `VK_CAPITAL` and right modifiers directly as ordinary downs and ups.
- **Modifier emission.** Windows key messages carry no modifier flags; apps read `GetKeyState`. The port must **inject real modifier key-downs and key-ups** (tagged `SendInput`). Two options:
  - **Eager:** inject on source down, release on source up. Simple, and works for mouse chords. Every solo tap briefly shows apps a modifier press.
  - **Lazy:** inject on the first other key or click while held. That event must be swallowed and re-injected after the modifiers.
  - Either way, inject a **mask key** (an unassigned VK such as 0xE8) before releasing Win or Alt when nothing else was pressed. Otherwise Start opens, the menu bar activates, or the input language switches.
- **The Office key.** On Windows 10/11, Ctrl+Alt+Shift+Win is the shell "Office key". Alone it opens Microsoft 365; with W/X/P/O/T/N/D/L/Y it opens specific apps, which then never see those keys. The macOS default (all four) therefore maps badly. **Recommend a Windows default of Ctrl+Alt+Shift ("meh")**, with Win opt-in plus a warning.
- **AltGr.** Ctrl+Alt is AltGr on many European layouts (Ctrl+Alt+E types € on German). Super chords without Shift may type characters.
- **Repeat pitfall.** Windows auto-repeats a held Caps Lock or modifier. **Ignore source repeats** for `didRepeat`; otherwise Escape/Caps solo actions would never fire after the repeat delay.
- **Right Alt on AltGr layouts.** Windows injects a fake Left Ctrl (scan 0x21D) before it; swallow it when Right Alt is the source, or disallow Right Alt there.
- **Caps Lock.** Swallowing `VK_CAPITAL` prevents the toggle. Force it off at start (tagged toggle if on). Re-check after a hook loss and reinstall.
- **Stuck modifiers.** Keep the watchdog, using Raw Input physical state as ground truth. Keep a guard process whose job becomes "release injected modifiers on parent death". Release on lock, sleep and session change.
- **UIPI.** The hook doesn't see, and `SendInput` can't reach, elevated windows. A hold that starts in a normal window and ends in an elevated one can strand modifiers. Mitigate with uiAccess, or document it.
- **"Foreign mapping" analog.** Warn if the `Scancode Map` registry value remaps the source key, or if PowerToys Keyboard Manager / AutoHotkey are running.
- **Input source switching** (`WM_INPUTLANGCHANGEREQUEST` / TSF) is asynchronous on Windows; the next keystroke may still use the old layout.

#### 3.7.7 Quit and close protection

**Purpose.** Intercepts exactly **⌘Q** (quit app) and **⌘W** (close window), matched by keyboard layout, and swallows the press. The action happens only after a per-shortcut confirmation.

**Settings** ("Quit & close protection"; no tray row):
- Intro: "Configure each shortcut independently. The original action passes only after the selected confirmation." / "Protection uses Accessibility to observe only ⌘Q and ⌘W globally."
- Two sections, "⌘Q" and "⌘W", each with:
  - "Protect this shortcut" ("Other Command shortcuts continue to work normally.").
  - "Confirmation mode": "Hold to confirm" / "Double press" / "Require extra modifier".
  - Mode settings:
    - "Hold duration" 250–2000 ms, step 50 (default **800**);
    - "Double press interval" 200–1500 ms, step 50 (default **600**);
    - "Extra modifier": "Shift (⇧)" / "Option (⌥)" / "Control (⌃)" (default Shift), with a chord caption like "⇧⌘Q".
  - "Applications": "All applications" / "Selected applications only" / "All except selected applications", with an app list ("No applications selected", "Add application…").
  - "Show visual feedback".
- Configuration is re-read on every protected key press.

**Matching.** A key-down is ⌘Q or ⌘W if, in priority order:
1. the layout's Command-table character for that key is q/w;
2. else the plain typed character;
3. else the fallback key codes (Q = 12, W = 13).

This follows AZERTY, Dvorak, Cyrillic and Greek layouts. The **base chord** is ⌘ held, no ⌃/⌥/⇧, plus the matching key.

**Per-event flow:**
- Pass own re-posts.
- If the App Switcher owns keyboard input, yield and cancel anything pending.
- **Key down:**
  - Esc while pending → cancel and swallow the Esc.
  - Neither ⌘ held nor anything pending → pass (fast path).
  - A swallow state for the same shortcut, or a repeat → swallow.
  - A different shortcut pending → cancel it.
  - Not Q/W, shortcut disabled, or the app out of scope → pass.
  - A repeat with ⌘ held → swallow.
  - Otherwise run the mode logic below.
- **Key up:**
  - The swallowed shortcut's key-up → clear the swallow state, swallow.
  - The pending shortcut's key-up: hold → cancel (released too early) and swallow; double → swallow and keep waiting; extra-modifier → cancel and swallow.
- **Modifier change:** pending and ⌘ no longer held → cancel. The event always passes.

**Mode logic:**
- **Hold.** A base chord starts pending (records the event, the frontmost app's pid and the switcher generation), schedules a timer for the hold duration, shows the HUD "Hold ⌘Q to quit" / "Hold ⌘W to close" with detail "Esc cancels" and a progress bar, and swallows. When the timer fires: set the swallow state, cancel pending (hiding the HUD), and confirm with the original event.
- **Double press.** A second base chord within the interval (inclusive, on event timestamps) → confirm with this event. Otherwise (re)start pending with expiry interval + 100 ms, show "Press ⌘Q again to quit" / "Press ⌘W again to close", and swallow. ⌘ must stay held between the presses.
- **Extra modifier.**
  - The exact ⌘ + chosen modifier (and none of the other two) + key → confirm, with the extra modifier stripped from the re-post. No HUD.
  - A base chord → pending (1.5 s expiry, cancelled on key-up), HUD "Use ⇧⌘Q to quit", swallow.
  - Other chords pass.

**Cancellation:** Esc, releasing ⌘, any other key, the target app losing focus, an App Switcher session, or expiry.

**Confirmation:**
1. Abort if the switcher took input meanwhile.
2. Find the target app by the recorded pid.
3. **Quit:** ask the app to terminate politely; done if accepted. Exception: exactly `com.valvesoftware.steam` gets a keystroke instead.
4. **Close**, or terminate refused: re-post a marked copy of the key-down (extra modifier stripped) plus a marked key-up.
5. Afterwards, the swallow state eats the physical key's auto-repeats and its release.

**HUD** (non-activating, never takes focus):
- **Size:** width = max(300, label width + 24); height 48, or 56 with the progress bar.
- **Position:** bottom center of the pointer's monitor (or the App Switcher's), 18 DIP above the bottom of the work area.
- **Shape:** pill (radius = height/2), near-black (white 0.09) at 95%, 1 DIP white @16% stroke.
- **Text:** title 13 semibold white; detail 10.5 white 68%.
- **Progress** (hold mode only): track inset 24 DIP each side, 3 DIP tall, 7 DIP above the bottom. The fill grows linearly over the *remaining* hold time.
- **Animation:** no fades; appears and disappears instantly.

**App Switcher integration.** While a switcher session is open the filter yields. If protection covers the selected app, Q/W on a selected item asks for a second press within the double-press interval (whatever mode is configured), with the HUD "Press Q again to quit" / "Press W again to close".

**Edge cases.**
- No apps are excluded by default.
- A lost swallowed key-up costs one later press (eaten silently).
- An app that ignores ⌘Q (e.g. Finder) may be asked to terminate when in scope *(observation)*.
- The string "Release to confirm" is unused.

**Windows mapping.** Keep two independently configured slots:
- **"Quit / close window"** = Alt+F4, optionally Ctrl+Q.
- **"Close tab or document"** = Ctrl+W, optionally Ctrl+F4 and Ctrl+Shift+W.

There is no app-level quit on Windows; Alt+F4 is per window. Details:
- **Modifier tracking.** LL hooks only report Alt (`LLKHF_ALTDOWN`); track Ctrl/Shift/Win yourself. "⌘ released → cancel" becomes "the slot's base modifier released → cancel".
- **Alt masking.** If Alt+F4 is swallowed and then cancelled, the app sees Alt down/up alone and activates its menu bar. Inject the mask key before letting the Alt release through.
- **Confirming:** inject F4 (tagged) while Alt is still held, or `PostMessage(target, WM_SYSCOMMAND, SC_CLOSE, 0)` to the window captured at press time. Keep a per-app "keystroke vs message" list, like the Steam exception.
- **Matching.** Windows accelerators match on virtual-key codes, which already follow the layout, so match `vkCode == 'Q'/'W'` (or `VK_F4`).
- **Extra-modifier conflicts.** Ctrl+Shift+W closes the whole window in Chromium, and Alt+Shift is a language-switch hotkey. Offer Ctrl+Alt+F4 / Ctrl+Alt+W-style alternatives and warn.
- **Target safety.** Re-check the foreground window before injecting, because `SendInput` targets whatever has focus.
- **UIPI.** Elevated apps are unprotected; document it.
- **HUD:** never activate; work area of the cursor's monitor, 18 DIP above its bottom (above the taskbar).

#### 3.7.8 Apps to leave alone (per-feature mouse exceptions)

**Lists.** One independent list per feature. An app excluded from one feature is unaffected by the others.

| Scope | Key | Honored by | Match on |
|---|---|---|---|
| Smooth scroll | `smoothScrollExceptions` | smooth scroll | app under pointer, or source process |
| Linear scroll | `linearScrollExceptions` | both wheel paths | same |
| Scroll direction | `scrollInverterExceptions` | inversion + horizontal redirect | same |
| Navigation | `mouseNavigationExceptions` | side-button navigation | pointer app **or** frontmost app |
| Button shortcuts | `mouseButtonExceptions` | shortcuts, side wheel, Spaces drag | pointer app **or** frontmost app |
| Super key | `superKeyExceptions` | Super key | **any listed app running** |
| (outside this area) | focus-follows-mouse, middle click | — | — |

The click filter, key debounce and mouse acceleration have no list. A list is consulted and shown only while its feature is available and on. Quit protection uses its own scope lists (§3.7.7).

**Identity.**
- An app's bundle identifier. If it has none, its **absolute executable path with symlinks resolved** (paths are recognized by their leading "/").
- Sanitizer: ids trimmed; paths kept exactly; blanks and duplicates dropped; order preserved.

**Pointer app.**
- The owner of the frontmost on-screen window at the pointer: alpha > 0, layer 0–3 (normal windows and floating panels), not Vorssaint's own.
- If there is none, the frontmost app.

**Source tracking.** Only while a wheel feature runs with a non-empty list. Running processes are mapped to scopes; helpers inside a listed app bundle inherit its exception.

**Cache.**
- A resolved answer is reused for 0.5 s while the pointer stays in the resolved window's rectangle.
- On the high-priority pointer thread, the window server is never queried synchronously. An expired answer still serves the same window while an asynchronous refresh runs.
- **An app that can't be identified yet is treated as excluded** ("hands off"). Tests assert lookups never block on the main thread.

**UI.**
- Disclosure "Apps to leave alone" with a count, expanded if non-empty.
- Rows: icon (18), name, and the path under it for executables; a minus button "Remove".
- "Add an app…" opens a searchable picker over installed apps plus running regular apps, plus a "Choose" file chooser (bundles and executables).

**Windows.**
- Identity = normalized full image path (case-insensitive, via `GetFinalPathNameByHandle`), optionally matched by exe file name so versioned install folders keep working. Packaged apps: resolve the AUMID (ApplicationFrameHost → CoreWindow process).
- Pointer app: `WindowFromPoint` → `GetAncestor(GA_ROOT)`, skipping own and `WS_EX_TRANSPARENT` windows → `GetWindowThreadProcessId` → `QueryFullProcessImageNameW`. Frontmost: `GetForegroundWindow`.
- **Source-PID matching can't be ported**; the LL hook doesn't know the injecting process. Drop it.
- **Never resolve inside the hook.** Keep an identity cache keyed by the root HWND under the cursor, refreshed on a worker thread, and keep "unknown → hands off".
- The Super key "while running" rule needs process enumeration (Toolhelp snapshot every 1–2 s, or WMI process creation and deletion events).

### 3.8 AI agent usage tracking

#### 3.8.1 Purpose

The "AI Agents" section of the Dynamic Island reads the usage that four coding agents leave on the machine:

| Agent | Source format |
|---|---|
| **Claude Code** | JSONL transcripts |
| **Codex** | JSONL rollouts |
| **OpenCode** | SQLite database |
| **GitHub Copilot CLI** ("GitHub Copilot app") | JSONL event logs |

It also reads the plan-limit history saved by the **Claude desktop app**.

From these sources it builds:
- a priced usage history: "API value" in USD at public list prices, kept for **91 days**;
- plan limits with renewal times;
- live "working" turns;
- notices when a long task finishes, near-limit warnings, renewal notices, and a daily-budget notice.

**Privacy and footprint.**
- Everything is read in place, incrementally, on one background worker.
- Only counters, model names, times, session ids and folder names are kept. Prompts, replies and tool output are never stored.
- Only two outbound actions exist: a daily download of a public price list, and an optional short conversation with the local `codex app-server` binary (Codex "Resets" card only).
- No OS notifications, no keychain, no OAuth or usage HTTP endpoints.
- Reading progress is archived, so the next launch reads only what was appended.

The current code honors **no environment-variable overrides**: no `CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `XDG_DATA_HOME` or `COPILOT_HOME`. Every path is fixed relative to the home folder. The Windows port should add them (§8).

#### 3.8.2 Enablement and lifecycle

**The service runs only when all of these hold:**
- the island is on (`notchEnabled`);
- the feature is installed (`featureAvailable.notchAgents`);
- `notchAgentsEnabled` is on;
- the `agents` module is in the island's module list.

On Windows, decouple this from the island: let the feature run whenever its own switch is on, and show its data wherever the port puts it.

**Lifecycle operations:**

| Operation | What happens |
|---|---|
| **Sync** (on settings changes) | If disabled: stop and **delete the archive**. If the provider set changed: stop without keeping progress, then do a fresh full read. If paused: resume. Always: sync prices. |
| **Start** (on the worker) | 1. Install prices. 2. Discover logs modified within 91 days. 3. Resume from the archive if its provider set is identical. 4. Read every discovered file, checking for cancellation every 4 MiB chunk. 5. Move idle turns aside. 6. End Claude turns whose process is gone. 7. Read the Claude plan and Claude-app limits. 8. **Enable transition reporting** and baseline the limits. 9. If today's cost is already over budget, mark today as notified. 10. Start the file watcher, the 2 s poller and the network monitor. 11. Publish once ("loaded") and save progress. |
| **Pause** (display asleep, locked, island away) | Stop timers and watcher, save progress, keep data in memory |
| **Resume** | Restart, rescan incrementally |
| **Stop** | Cancel, save the archive (or delete it if the section is off), clear everything |

- Until the first publish, the page shows "Reading usage…".
- A cancelled start never publishes a partial or empty snapshot.

#### 3.8.3 Displays (what the user sees)

**AI page** (title "AI Agents"):
- Cards appear only for providers that are enabled and "seen" (they have records, limits or a live turn). The order can be changed and each card shown or hidden.
- Empty state: "No usage from Claude Code, Codex, OpenCode or GitHub Copilot yet. It appears here as soon as any of them works on this Mac."
- All cards hidden: "Choose what this page shows in Dynamic Island settings."
- The grid redraws every 15 s.
- Layout: cards pair up at widths ≥ 390. Trend and Activity are full width. Cards are 96 high, charts 118.

**Limits card** (one per Claude, Codex, OpenCode; never Copilot):
- Header: the agent mark, a plan chip, and a pulse while the agent is working.
- Up to two rows: the session window plus the most-used other window. Each row shows:
  - a label: "Session", "Week", "Week · Opus", or a duration;
  - a countdown to renewal (≥ 1 min; "h m" or "d h");
  - percent left or used, per the setting;
  - a meter with a "pace" tick at the elapsed fraction of the window.
- Tint: the agent color; **orange from 80% used, red from 95%**.
- A Claude-app reading ≥ 30 min old is dimmed to 60%.
- "Updated <relative>" appears when only one window shows and the reading is more than 10 min old.
- Without windows:
  - **Claude:** the *estimated* 5-hour block. Shows "Session", the countdown to block end, the block's cost and a dimmed elapsed meter. Tooltip "Estimated from this Mac"; link "Set up plan limits…". With no block: "No session running".
  - **OpenCode:** today's cost ("≥ $x" if partly unpriced), tokens, "N% from cache", last used.
  - **Codex:** "Limits appear after the next reply".

**Spending card:**
- Period menu "Today" / "7 days" / "30 days" (saved).
- Big total with a "≥ " prefix if anything is unpriced; label "API value".
- For 30 days: an "N×" chip per provider whose plan has a monthly price (cost ÷ monthly price, 1 decimal below 10).
- A provider split bar.
- Footer "<tokens> tokens · <rate> from cache"; tooltip "<savings> saved by the cache".

**Now card:**
- Up to 2 live turns, then a "+N" chip.
- Each row: glyph, project, a 1 s stopwatch, and "Model · <output> written · $cost".
- With nothing working: last activity per provider, or "Idle".

**Trend card:** stacked bars by provider. 24 hourly bars for Today; 7 or 30 daily bars otherwise. Bars weigh by cost when everything is priced, else by tokens.

**Models / Projects cards:** the top 3 for the period.

**Activity card:**
- A 91-day heatmap: columns are weeks, starting on the locale's first weekday.
- Cell opacity follows the quartiles of active days: 0.28 / 0.45 / 0.65 / 0.9. Empty days 0.07. Today is outlined.
- Figures: 13-week total, "Active days", "Busiest day", and a streak flame when the streak is > 1.

**Resets card** (Codex only): see §3.8.8.

**Closed-island strip:**
- Left side: marks of the working agents.
- Right side: one reading, per setting:
  - "Time": a stopwatch from the earliest live turn ("12:34", "1:02:03");
  - "Tokens written": sum of live output tokens;
  - "API value": sum of live cost;
  - "Limit": the focused window of the first working provider; falls back to Time.
- Time and Limit tick every 1 s. Wings are 44–80 DIP each. A click opens the AI page.

**Resting content "AI limits":**
- An 11 DIP ring plus a percent, for the chosen window across accounts.
- With no limits: the first seen agent mark plus today's total cost.
- Refreshes every 60 s.

**Notices** (in-island only; 5 s; priority 1; a click opens the AI page):

| Notice | Title | Detail |
|---|---|---|
| Finished | "<Agent> finished" | "4m 12s · $1.23" (cost omitted when 0) |
| Warning | "<Agent> · <Window>" | "<p>% left" or "<p>% used" |
| Renewal | "<Agent> · <Window>" | "Limit renewed" |
| Budget | "Daily budget" | "$<spent>" |

**Mascot reactions:** "ready" when an agent starts working (at most every 10 min); "celebrate" on a finish or renewal.

**Settings status of Claude plan limits** (refreshed every 30 s):
- **Fresh** (< 30 min old): green "Read from the Claude app, updated <relative>".
- **No app and no reading:** "Plan limits come from the Claude app, which is not on this Mac. Until then, the 5-hour session is estimated from Claude Code activity." plus a "Get the Claude app" button (`https://claude.ai/download`).
- **Stale:** orange "The Claude app last checked them <relative>." plus "Claude checks your limits only while its icon is in the menu bar. Turn the icon on in the Claude app settings, and your limits appear here within minutes." plus "Open Claude".

**Live preview of the strip in settings.** When nothing is working it uses an example: 754 s elapsed, 20,000 output tokens, $4.56.

#### 3.8.4 Reading pipeline

**Discovery.**
- Roots that don't exist are skipped. Roots are canonicalized (real paths).
- Claude and Codex: every `*.jsonl` under the root, recursively.
- Copilot: only `<root>/<session>/events.jsonl` exactly one level deep, skipping hidden entries. It never descends into the session's workspace checkout.
- OpenCode: only `<root>/opencode.db`. Its modified time is the newer of the db and `opencode.db-wal` mtimes.
- A file is admitted if it is a regular file modified within 91 days.
- Ordering: main logs first, then Claude subagent logs; each group by mtime ascending.

**Per-file cursor.** Offset, identity (inode / file id), a pending partial line, a discard flag, parser state, mtime and a fingerprint.
- Claude subagent files (path contains `/subagents/`; the parent is `<prefix>.jsonl`) and Codex files whose name contains `_` (side threads) don't track turns.

**Appended read.**
- **Start over from 0** (resetting parser state) if any of these hold: the identity changed; the size shrank below the offset; or the size grew but the fingerprint no longer matches (rewritten in place).
- Otherwise read from the offset in **4 MiB** chunks and split on `\n`.
- The trailing partial line is kept pending.
- A line > **32 MiB** puts the reader in discard mode until the next newline.
- Each complete line is parsed and applied in order.
- Afterwards a fingerprint is taken: FNV-1a-64 over the offset plus the first and last 4 KiB before it.

**Change detection** (three mechanisms):
1. **File watcher** on the existing roots: file-level events, 1.0 s latency. Changed paths are read; a `-wal` path maps to its db. Overflow or dropped-event flags trigger a full rescan.
2. **Poller every 2 s** (leeway 0.5 s). It checks cursors modified in the last 30 min, or that hold a turn, and rereads a file if its size, identity or existence changed (OpenCode: if its mtime advanced). In the same tick it ends settled, offline and dead-process turns. If anything changed, it checks limits and schedules a publish.

   Why poll at all: "file events report a written file only once it closes, and some agents keep their log open for the whole session." On Windows, NTFS directory metadata also lags for open files, so open a handle to get the true size.
3. **Main tick every 30 s** (tolerance 10 s):
   - sync prices; close idle turns; drop records older than 91 days; poll cursors modified within 24 h;
   - every 5 min: re-discover roots and reread `~/.claude.json`;
   - reread the Claude-app limits; check limits, then report renewals;
   - save progress every 5 min;
   - publish if the inputs changed, or if the snapshot moves with the clock.

**Publishing** is debounced by 1 s. The snapshot is handed to the UI only if it changed.

#### 3.8.5 Parsing per agent

All parsers reduce each line to entries: `usage`, `usageModel`, `limits`, `plan`, `turnBegan`, `turnContext`, `turnActive`, `turnSettled`, `turnEnded(completed, duration)`, `reset`.

**Shared number and time rules:**
- Numbers are accepted only if finite and > 0, capped at 1e12. Booleans are rejected as numbers.
- Timestamps: ISO-8601 with `Z` or `±HH:MM` (optional fraction), or Unix seconds; values > 1e11 are treated as milliseconds.

**Claude Code** (transcripts):
- **Pre-filter:** a byte search for `"type":"assistant"` or `"type":"user"`, then the JSON `type` is verified, so escaped text inside content can't match.
- **Assistant lines:**
  - Adopt `sessionId` and `cwd`. Project = the last path component of `cwd`, cut before `/.claude/worktrees/` (Windows: split on both separators).
  - If `message.usage` exists and `message.model` is non-empty and doesn't start with `<` (e.g. `<synthetic>`), emit usage:
    - input = `input_tokens`
    - cacheWrite = `cache_creation_input_tokens`
    - cacheRead = `cache_read_input_tokens`
    - output = `output_tokens`
    - reasoning = `output_tokens_details.thinking_tokens` (informational; already inside output)
    - longCacheWrite (1 h) = `cache_creation.ephemeral_1h_input_tokens`
    - fast = `usage.speed == "fast"`
    - US-only = `usage.inference_geo == "us"`
    - web searches = `usage.server_tool_use.web_search_requests`
  - **Dedup key** `claude:<message.id>:<requestId>`; if both are empty, `claude:<sessionId>:<unix seconds>`.
  - `isSidechain` lines never change the turn.
  - `stop_reason` ∈ {`end_turn`, `stop_sequence`, `max_tokens`, `refusal`} → the turn ends, *completed* unless `isApiErrorMessage` is true or the model starts with `<`.
  - Any other stop reason (`tool_use`, null…) → the turn opens or continues. Each `tool_use` block named `Bash` adds its id to the running-commands set (memory only).
- **User lines:**
  - Text starting with `[Request interrupted by user` or `<local-command-std` → the turn ends, not completed.
  - `toolEndsTurn == true` (and not a sidechain) → the turn ends, completed, at the line's timestamp.
  - Inside a turn: activity only; activity time = file mtime. A `tool_result` removes its `tool_use_id` from running commands; a prompt with no tool result clears the set.
  - Outside a turn: a non-meta, non-sidechain line opens a turn at its `timestamp`.
- **Running** = an open turn with activity in the last 10 min. **Finished** duration = end − prompt timestamp.

**Codex** (rollouts):
- **Pre-filter:** the first `"type":"…"` of the line (≤ 64 bytes) must be `token_usage_record`, `turn_context` or `session_meta`. For `event_msg`, the inner type must be `token_count`, `task_started`, `task_complete`, `turn_aborted` or `thread_settings_applied`. This keeps records quoted inside history or tool output from being read.
- **Record types:**

  | Type | Reads |
  |---|---|
  | `session_meta` | `payload.id` (session), `payload.cwd` (project) |
  | `turn_context` | `payload.model`, `payload.cwd`, `payload.service_tier` (fast = `fast` or `priority`, case-insensitive) |
  | `thread_settings_applied` | `payload.thread_settings.service_tier` |
  | `token_usage_record` | `payload.usage.{input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens, reasoning_output_tokens}`; `payload.session_id` fills the session if unset; key `codex:<response_id>` (else `codex:<session>:<unix seconds>`); marks the file as having per-response records |
  | `token_count` | `payload.rate_limits` → limits (§3.8.7); `plan_type` → plan |
  | `task_started` | `turnBegan(payload.started_at ?? line time)` |
  | `task_complete` / `turn_aborted` | Turn end at `payload.completed_at ?? line time`; completed only for `task_complete`; duration = `payload.duration_ms / 1000` |

- **Legacy fallback** (only while the file has shown no `token_usage_record`): from `token_count`'s `payload.info.total_token_usage`, if the total grew, use `info.last_token_usage`, else the difference from the previous total. Key `codex:<session>:total:<total>`.
- **Token mapping:**
  - input = `input_tokens` − cached − written
  - cacheRead = min(input, cached)
  - cacheWrite = min(input − cached, written)
  - output = `output_tokens` (includes reasoning)
- **Model** comes from the latest `turn_context`. Usage before any context is unpriced.
- **Running** = between `task_started` and its end. **Finished** uses Codex's own measured duration.

**OpenCode** (SQLite, read-only, 2000 ms busy timeout; the older JSON `storage/` folder isn't read):
- **Tables:** `session(id, directory, parent_id?)`; `message(rowid, id, session_id, time_created ms, time_updated ms, data JSON)`; optional `part(message_id, data JSON)`.
- **Query output.** The query builds one small JSON object per row:
  - from `data`: `role`, `parentID`, `modelID` / `model_id` / `model`, `tokens`, `cost`, `finish`, `time`, `path.cwd`;
  - session fields: `parent_session_id`, `directory`;
  - computed flags:
    - `tool_calls`: an assistant reply whose finish isn't `tool-calls`/`unknown` and that has a non-provider-executed `tool` part not interrupted;
    - `auto_compaction`: `summary` true and the parent has an auto `compaction` part;
    - `error`: `$.error` present and not null;
    - `writing`: `time.completed` IS NULL.
  - Message text never leaves SQL.
- **Incremental reading:**
  - The cursor offset is the last rowid read.
  - First read: binary-search the first rowid with `time_created` ≥ now − 91 days.
  - Each read re-queries "open replies" (still being written) by id, then rows with rowid > offset. Open replies are tracked as (time_updated, length(data), seen-at); unchanged ones are dropped after 24 h.
  - A tail of the last 64 (rowid, id) pairs detects reverts: resume after the newest still-present row, or restart with a `reset` if all are gone. An identity change also restarts. A reset ends every turn of that database.
- **User rows.**
  - Repeated prompt ids are ignored (about 100 remembered per session).
  - A prompt is "quiet" if the previous step settled ≥ 30 s ago, or no reply is being written and the session has been inactive for ≥ 10 min.
  - A non-quiet prompt in an open turn is activity: compaction, follow-ups, queued prompts.
  - Otherwise any open turn ends (not completed) and a new turn begins.
- **Assistant rows.**
  - Model: `model_id` ?? `modelID` ?? `model.modelID` ?? `model.id` ?? `model`.
  - Tokens: input = `tokens.input`; output = `tokens.output + tokens.reasoning`; cacheRead = `tokens.cache.read`; cacheWrite = `tokens.cache.write`.
  - **Cost:** the list price if the model is known; else the recorded `cost` ("reported", only if > 0); else nil.
  - A usage record is emitted only if tokens or the recorded cost are > 0. Key `opencode:<session>:<id>`.
- **Ending rules:**
  - An error ends the turn, not completed.
  - A finish ends the turn unless it is `tool-calls` or `unknown`, or the row has the `tool_calls` or `auto_compaction` flag.
  - With no finish, the turn ends when `time.completed` exists.
  - A row only ends the turn if it answers the active prompt.
  - A step that completes without ending emits `turnSettled`. Settled turns end silently after 30 s.
  - A reply to a newer prompt while an older reply never completed ends the task (not completed) and restarts it from the newer prompt.
- **Subagents** (sessions with `parent_id`) roll their usage into the parent's turn and never notify.

**GitHub Copilot CLI** (event log; VS Code, JetBrains and Visual Studio Copilot are **not** read):
- **Envelope** `{type, id, timestamp, agentId?, data}`, parsed by a strict top-level indexer: it rejects duplicate keys, keys > 256 bytes, nesting > 128, and anything that isn't one complete object.
- **Events:**

  | Event | Handling |
  |---|---|
  | `session.start` | `data.sessionId`, `data.selectedModel`, project from `data.context.gitRoot` / `cwd` / `repository` |
  | `session.context_changed` | project update |
  | `session.model_change` | `data.newModel` |
  | `user.message`, `assistant.turn_start` | open the turn / activity; `turn_start` remembers `data.turnId` |
  | `assistant.message` | one activity record per API call (requests = 1, no tokens, cost 0, priced as an aggregate). Dedup `(agent:<id>\|root) + (api:<apiCallId>\|message:<messageId\|envelope id>)`. The "final response" flag is set when there are no `toolRequests` and `phase` isn't `thinking`/`commentary`. |
  | `assistant.turn_end` | ends the turn (completed) only when the final-response flag is set and `turnId` matches |
  | `abort` | ends the turn, not completed |
  | `session.shutdown` | `data.modelMetrics.<model>`: `requests.count`, `tokenDetails.{input,cache_read,cache_write,output}.tokenCount` (else `usage.{inputTokens,…}`). Cumulative per session: only growth since the previous shutdown is recorded (a drop means new totals). Request counts are reconciled against counted activity. Key `copilot:<session>:<shutdown id>:<model>`; aggregate pricing. Ends any open turn. |

- `session.usage_checkpoint` (`totalPremiumRequests`) is ignored, so **premium requests are not counted**.
- Copilot never adds tokens or cost to the live turn and reports no limits.

**Merging.** A repeated dedup key merges by the **per-category maximum**, so streamed partial duplicates collapse. Billable flags merge as max (long cache write, web searches) or OR (fast, US-only, aggregate). The cost is then recomputed. OpenCode reported costs take precedence over estimates.

#### 3.8.6 Turn lifecycle and the "task finished" notice

**States:** `turns` (shown), `waiting` (quiet and hidden, can resume), `settled` (OpenCode only).

- **Begin:** open a turn keyed by file. It is ignored if a turn on that file started within 1 s (a reread log). Otherwise it replaces the existing turn silently.
- **Usage:** adds the token and cost delta to the turn (or the parent's, for subagents), counted only if the record's date ≥ turn start − 1 s. Usage brings a waiting turn back.
- **Idle:** every 30 s, a turn with no activity for **10 min** moves to waiting. Waiting turns expire **1 h** after their last activity for Claude, **6 h** for the others. A waiting turn that later ends still notifies, with its whole duration.
- **End:** emits `finished(provider, duration, cost, tokens, project)` only if **all** hold:
  - the turn was completed;
  - the initial history read has finished (history is never replayed);
  - the end is within **5 min** of now.
  
  Duration = the agent's own, or end − start. Delivery then also requires: the provider still enabled, "When a task finishes" on, and duration ≥ the minimum (default **60 s**; menu values 0/30/60/120/300).
- **Claude process registry:**
  - Reads `~/.claude/sessions/<pid>.json` (and `~/.config/claude/sessions/`): `pid`, `sessionId`, optional `pidDomain` (records with a non-`darwin` domain are skipped). Each record must be under 64 KiB.
  - A Claude turn whose log is `<sessionId>.jsonl` ends silently if its pid is dead, if its record vanished after being seen, or, at launch, if it has no record while the folder could be listed.
  - An unreadable record ends nothing.
  - Checked every 2 s while a Claude turn shows.
  - Windows: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `GetExitCodeProcess`; guard against pid reuse by comparing start times; verify the Windows `pidDomain` value.
- **Offline:** once the network has been gone for **20 s** of awake time, every Claude turn ends silently. Exceptions: the model replied since the drop (a local model), or a `Bash` command is running. Other agents are left alone. Windows: `NetworkInformation.NetworkStatusChanged`; measure with `QueryUnbiasedInterruptTime`, which excludes sleep.

#### 3.8.7 Plans, limits, warnings, budget

**Claude plan.** From `~/.claude.json` → `oauthAccount`:
- `organizationUuid` (used to match Claude-app readings);
- tier = `organizationRateLimitTier` ?? `userRateLimitTier`, combined with `organizationType`.
- Read at launch and every 5 min, reparsed only when the mtime changes.
- Plan names match by substring, first match wins: max_20x "Max 20×" $200; max_5x "Max 5×" $100; max "Max"; enterprise; team; pro "Pro" $20. Unknown plans use `organizationType` minus `claude_`, prettified.

**Claude limits from the Claude desktop app.** File `~/Library/Application Support/Claude/plan-usage-history.json`, at most 4 MiB.
- Shape: `{"version": 1|2, "samples": [{"t": <ms>, "org": "<uuid>"|null, "u": {...}}]}`. In version 1 the window keys sit beside `t`.
- Window keys:
  - `fh` = 5-hour session (300 min)
  - `sd` = week (10,080 min)
  - `so` = week · Opus
  - `sn` = week · Sonnet
  
  Values are clamped 0–100.
- Samples are filtered to the Claude Code account (`org` null or equal to `organizationUuid`).
- The latest sample is used if it is ≤ 5 min in the future and < 7 days old.
- The session window is dropped if the reading is ≥ 5 h old.
- **Session renewal:**
  1. Find the run of non-zero `fh` readings ending with the latest one (a rise of up to +1 allowed; a drop > 1 is a renewal; gaps < 5 h).
  2. Start = Claude Code's own first request in the current 5-hour block if it falls between the reading before the run and the run's first reading; else the run's first reading.
  3. Renewal = start + 5 h.
- **Weekly renewal:** find the most recent drop > 1 point; take the next **UTC hour** after the earlier sample (capped at the later sample); add 7 days until it is after the reading.
- A window is omitted when its renewal has passed, or when it has no known renewal and the reading is ≥ 1 day old.
- Recomputed against "now" every 30 s.

**Claude estimate** (no app reading):
- Uses Claude records from the last 24 h, chained into 5-hour blocks. Each block starts at the first record at or after the previous block's end.
- The current block is the one whose end is after now.

**Codex limits from logs** (`token_count.payload.rate_limits`):
- Only the main bucket is used (`limit_id` empty or `codex`); per-model buckets such as `codex_spark` are ignored.
- Slots `primary` and `secondary`: `used_percent` (clamped 0–100), `window_minutes`, `resets_at` (seconds or ms) or `resets_in_seconds` (relative to the line time).
- Window id `codex.<minutes>`. Kind by length: ≤ 720 min → session; 8,640–11,520 → weekly; else other.
- If both slots are empty: `individual_limit.remaining_percent` gives used = 100 − remaining, with `individual_limit.resets_at`, as `codex.individual` (Business accounts).
- Merging is newest-wins by observed time, whatever the source, so a log read late never overrides a newer reading from the account.
- Plan from `plan_type`, newest wins: `plus` "Plus" $20; `pro` "Pro"; others capitalized.

**Warnings.** Threshold = the setting (default **80%**; menu 50/75/80/90/95). A window warns when **all** of these hold:
- used ≥ threshold;
- a previous reading of the same window id exists;
- either the previous reading was below the threshold, or the window renewed (its renewal moved more than 60 s later).

The **first reading never warns**.

**Renewal notice:**
- A warned window whose old renewal time has passed reports "Limit renewed".
- So does a warned window whose renewal moved > 60 s later while its used % is now below the threshold. This covers a banked reset.

**Budget.** On each publish: if today's cost (local calendar day) ≥ the budget (menu: Off/5/10/25/50/100/250 USD) and today hasn't been notified, emit once.

#### 3.8.8 Codex banked resets ("Resets" card)

- **When to check.** When the card appears, if the last attempt is older than 5 min (successful, signed-out or outdated), or older than 60 s after other failures. Never while a check or a reset use is running.
- **Finding the binary** (first existing executable wins):
  1. inside installed Codex/ChatGPT apps (`<app>/Contents/Resources/codex-cli/bin/codex`, then `<app>/Contents/Resources/codex`);
  2. `~/.local/bin/codex`, `/opt/homebrew/bin/codex`, `/usr/local/bin/codex`;
  3. each absolute entry of the login shell's PATH. This is used only after "Try again" on a "missing" failure, by running the login shell once with a 10 s timeout.
  
  Windows: npm `%APPDATA%\npm\codex.cmd` (a shim; prefer the native `codex.exe` under `node_modules\@openai\codex\vendor\…`), winget/scoop installs, PATH from `HKCU\Environment` + `HKLM\…\Session Manager\Environment`, and `where codex`.
- **Process.** `codex -c features.plugins=false app-server`, run from `/` with stderr discarded.
- **Protocol.** One JSON object per line on stdin/stdout. **No `"jsonrpc"` member.** Integer ids from 1.
  1. Send `initialize` with `{clientInfo:{name:"vorssaint", title:"Vorssaint", version:<app version>}, capabilities:null}`, then the notification `{"method":"initialized"}`.
  2. `account/read` `{}`: success only when `result.account.type == "chatgpt"`; anything else (null, `apiKey`, `amazonBedrock`) means sign-in is needed.
  3. `account/rateLimits/read` `{}`. The reply must contain `rateLimitResetCredits`, else Codex is "outdated".
     - Credits: `credits[]` with a non-empty id, `status` `available` (assumed when missing) and a future `expiresAt`, sorted by expiry. Count = max(listed, `availableCount`).
     - Limits: from `rateLimitsByLimitId.codex`, or `rateLimits` when its `limitId` is the main bucket. Camel-case keys: `usedPercent`, `windowDurationMins`, `resetsAt`, `individualLimit.remainingPercent`. These are stored with source "account".
  4. **Use:** `account/rateLimitResetCredit/consume` with `{idempotencyKey:<UUID>, creditId:<soonest id>}`. Outcomes: `reset`, `nothingToReset`, `noCredit`, `alreadyRedeemed`. Then reread the limits.
- **Message handling.** Messages with `method` (server notifications and requests) are skipped and never answered. Non-JSON lines are skipped.
- **Errors:**
  - code −32601, or a message containing "unknown variant" / "method not found" → outdated;
  - "authentication" / "not logged in" → needs sign-in;
  - anything else → refused.
- **Timeouts.** One deadline per conversation: 20 s for a check, 30 s for a use. Output buffered up to 4 MiB. On end: close stdin, terminate after 2 s, kill after 1 more second.
- **Idempotency.** An unanswered use keeps its idempotency key for **10 min**. A retry within that window reuses the same key and credit, so a reset can't be spent twice.
- **Card UI.** Outcome lines stay for 60 s.
  - Count chip, plus "Next expires <relative>" (orange under 24 h).
  - "Use a reset" → confirmation "Reset the session and weekly limits now?" with "Cancel" / "Reset" → "Resetting…".
  - Outcomes: "Limits reset", "Your usage doesn't need a reset yet", "That reset was already used", "No resets available", "Couldn't use the reset".
  - Failures: "Needs the Codex app or CLI" (+ "Try again"), "Sign in to Codex with a plan to see resets", "Update Codex to use resets here", "Couldn't check resets".
- **Windows process handling.** `CreateProcess` with redirected pipes and `CREATE_NO_WINDOW`, inside a Job Object for cleanup.

#### 3.8.9 Prices

- **Bundled list:** `Resources/agent-prices.json` (`schema` 1, `updated` 2026-10-02; 24 Claude and 73 Codex models). The full table is in §6.8.
- **Cache:** `<app data>/agent-prices.json`, holding the raw downloaded bytes; its mtime is the "saved" date.
- **Remote:** `https://raw.githubusercontent.com/vorssaint/vorssaint-utils/main/Resources/agent-prices.json`.
- **Choosing a list.** The newer of bundled and cached by `updated` day; on the same day the download wins.
- **Download** only when all hold:
  - "Keep prices up to date" is on;
  - the cache is ≥ 24 h old (or missing);
  - the last attempt was ≥ 24 h ago, or ≥ 6 h ago after a failure.
  
  The request: no cookies or cache; User-Agent `Vorssaint/<version>`; 15 s request / 30 s resource timeout; HTTP 200 required; at most 256 KiB; redirects followed only to https on the same host.
- **On success:** save, install, **reprice every stored record** (OpenCode reported costs are kept), publish.
- **Validation** (any violation rejects the whole list): see §6.8. With no usable list, every record is unpriced.
- **Lookup and cost math:** §6.8.

#### 3.8.10 Summaries

- **Periods** use calendar days in the **current local time zone**, not rolling windows: Today; "7 days" = today + the previous 6 days; "30 days" = today + the previous 29.
- **Buckets:** 91 day buckets ending today, and 24 hourly buckets from local midnight. Future-dated records are left out of buckets but still count for "last activity" and "seen".
- **Breakdowns** per period:
  - totals and per-provider totals: tokens, cost, savings, requests, unpriced count;
  - models, keyed `<provider>:<display name>` so dated snapshots merge;
  - projects by folder name across agents.
  - Sorting is by cost when everything in the period is priced, else by tokens.
- **Burn rate** (last 30 min × 2) is computed but shown nowhere; it only causes periodic republishes.
- **Cache rebuilds.** The incremental cache must equal a full recompute. It rebuilds on calendar or time-zone change, provider-set change, a backward clock move, and day change.
- **Display names:**
  - Claude: drop `claude-`, dates and `v…` parts: "claude-opus-5-5" → "Opus 5.5"; "claude-3-5-sonnet-20241022" → "Sonnet 3.5".
  - GPT: "GPT-<n>" plus capitalized words: "gpt-5.1-codex-max" → "GPT-5.1 Codex Max".
  - Others are shown as-is.
- **Formatting:**
  - tokens: K/M/B, one decimal below 10, rounded down ("4.2K", "48K");
  - cost: "$12K" from 10,000, whole dollars from 100, else 2 decimals;
  - durations: 2 units;
  - clock: "m:ss" / "h:mm:ss".
- **Tints** (RGB 0–1): Claude (0.85, 0.47, 0.34); Codex (0.49, 0.60, 1.0); OpenCode (0.06, 0.73, 0.51); Copilot (0.30, 0.78, 0.68).

#### 3.8.11 Archive (resume)

The archive stores, for everything except OpenCode: records (with source files), limits, the Codex plan, open and waiting turns, and cursors with parser state.

- **Saved:** after the first read, every 5 min if reading moved on, on pause, and on stop while the section is on.
- **Deleted:** when the section is turned off or the provider set changes.
- **Resume rules:**
  - The build string and provider set must match exactly.
  - A cursor is restored only if its path was discovered now, the identity matches, and the fingerprint up to the saved offset matches.
  - A file the store knows that has no valid cursor counts as "gone"; every file sharing a record with a gone file is reread from the start.
  - Records whose source files are all gone are dropped.
  - Everything is repriced; records beyond the horizon are dropped.
  - **The result must equal reading every log from scratch** (test-asserted).

The binary format is in §5.8.

#### 3.8.12 Edge cases

| Situation | Behavior |
|---|---|
| Tool missing | Its root doesn't exist, so it's skipped; roots are rechecked every 5 min. Settings show "Not found on this Mac". |
| Malformed lines | Skipped; a partial last line waits for its newline; lines over 32 MiB are discarded. |
| Huge files | 4 MiB chunks, byte pre-filters, SQL extraction, and cancellation per chunk. |
| Clock changes | The summary rebuilds; offline time uses uptime; future Claude-app readings (> 5 min ahead) are ignored; a finish more than 5 min old doesn't notify. |
| Multiple accounts | Claude-app readings are filtered by organization. Codex keeps the newest reading and plan. Both Claude roots are merged. |
| Log deleted while running | Its turn ends silently; next launch drops what only that log held. |
| Log rewritten or truncated | Reread from the start; duplicates merge by key (max). |
| Price download fails | The last good list stays; retry after 6 h. |
| History at launch | Finishes and warnings from history are never reported (transitions are enabled only after the first read). |
| Codex double-count risk | The legacy-totals fallback assumes `token_usage_record` precedes `token_count` totals. A new-format log writing `token_count` first would double-count the first response. |

#### 3.8.13 Windows file layout (best knowledge; the code has no Windows hints except `pidDomain`)

| Tool | Likely Windows location | Confidence |
|---|---|---|
| Claude Code transcripts | `%USERPROFILE%\.claude\projects\<sanitized cwd, e.g. C--Users-me-code-app>\<sessionId>.jsonl`; subagents in `<sessionId>\subagents\` | High. Honor `CLAUDE_CONFIG_DIR`. `cwd` values contain backslashes. WSL installs live under `\\wsl.localhost\<distro>\home\<user>\.claude\projects` (optional to scan). |
| Claude Code config | `%USERPROFILE%\.claude.json` | Medium (moves under `CLAUDE_CONFIG_DIR` when set) |
| Claude Code session registry | `%USERPROFILE%\.claude\sessions\<pid>.json` | **Uncertain** whether it exists on Windows; `pidDomain` is probably `win32` |
| Claude desktop plan history | `%APPDATA%\Claude\plan-usage-history.json`, or under MSIX `%LOCALAPPDATA%\Packages\<pkg>\LocalCache\Roaming\Claude\` | **Uncertain** whether Windows writes it at all; ship the activity estimate as the default |
| Codex rollouts | `%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-<timestamp>-<uuid>.jsonl` and `archived_sessions\` | High. Honor `CODEX_HOME`. |
| Codex binary | `%APPDATA%\npm\codex.cmd` (shim), native `codex.exe` under npm's `vendor`, winget/scoop | Medium |
| OpenCode DB | `%USERPROFILE%\.local\share\opencode\opencode.db` (xdg-basedir style); possibly `%LOCALAPPDATA%\opencode` | Medium. Honor `XDG_DATA_HOME`. |
| Copilot CLI | `%USERPROFILE%\.copilot\session-state\<id>\events.jsonl` | Medium; possibly `COPILOT_HOME`/XDG overrides |
| App files | `%LOCALAPPDATA%\Vorssaint\Cache\AgentUsage\agent-usage.bin`; `%LOCALAPPDATA%\Vorssaint\agent-prices.json` | Port decision |

Path handling on Windows must be separator-aware and case-insensitive: project names, the Copilot `root\<id>\events.jsonl` check and the root-prefix "accepts" check. Agents keep logs open, so always open with full sharing (`FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`) and cope with delete+rename replacement.

### 3.9 Wallpaper picker (brief)

**Purpose.** "Pick a still wallpaper without opening System Settings." The feature is **opt-in** (`featureAvailable.wallpaper` defaults to false). It lives as a **section of the tray/menu-bar panel** ("Wallpaper"), whose visibility key is `panelShowWallpaper` (default true).

**Gallery contents.**
- **System stills.**
  - macOS source: `/System/Library/Desktop Pictures`. Includes top-level still images, plus `.madesktop` bundles resolved to their full-size `.wallpapers/<stem>/<stem>.heic`.
  - Preview image: the bundle's plist `thumbnailPath`, else `.thumbnails/<stem>.heic`, else the full image.
  - Dynamic and aerial wallpapers are not offered.
  - Scanned once, then cached for the process.
- **The user's own pictures.** Added with "Add image" (multi-select images) or "Add folder".
  - Folders are scanned recursively. Hidden files, packages/bundles and symlinks are skipped.
  - Accepted extensions: heic, jpg, jpeg, png, tif, tiff, gif, bmp, webp.
  - Entries are remembered as sandbox bookmarks. Re-adding the same path replaces the older bookmark and clears any hide.
- **Merging.** All entries are merged, de-duplicated by standardized path and sorted by title (file name without extension), case-insensitively.
- **Filter.** Segmented control "All" / "Your pictures" / "Apple", persisted in `wallpaperFilter`, default "all".
- **Paging.**
  - 24 per page, in a 3-column grid of 8 rows. Thumbnails are 54 DIP tall with 8 spacing and radius 6.
  - A "Previous" / "Next" pager.
  - Thumbnails are decoded at max 160 px with orientation applied, into an in-memory cache of 400 entries. The current and next pages are prefetched.
  - A generation token cancels stale decodes when the page or filter changes.
- **Visibility gating.** Scans run only while the gallery is visible, using viewer tokens. Closing the last open gallery cancels any scan in flight. A test asserts these lifecycle rules.

**Apply** (click a thumbnail):
1. **Target displays.** All displays if "Show on all Spaces" is on (`wallpaperApplyAllDisplays`, default true). Otherwise the display under the pointer, falling back to the first display.
2. **Cloud placeholder.** If the file is an iCloud placeholder, download it first. Poll every 250 ms with a 60 s timeout, showing "Downloading…". On failure show "Could not download the wallpaper". Thumbnails never trigger a download; they show a placeholder cell instead.
3. **Set the image** with "fill" scaling (scale proportionally, crop allowed). If a display already shows the same file, it is first cleared and the app waits 400 ms, because macOS otherwise skips the refresh.
4. **All Spaces.** When "Show on all Spaces" is on, also patch the system wallpaper store so every Space and display uses the image:
   - Store file: `~/Library/Application Support/com.apple.wallpaper/Store/Index.plist`.
   - Placement written: "Crop".
   - Before the first write, keep a one-time backup `WallpaperIndex.vorssaint-bak` in the app's data folder. It is never overwritten and is deleted on feature uninstall.
   - Restart the system wallpaper agent.
   - Unknown store layouts are refused, falling back to the current-Space apply only.
5. **Result.** "Could not set the wallpaper" appears only if both paths failed.
- **Concurrency.** A newer apply supersedes an older one. Each apply carries a token checked before every step.

**Managing sources.**
- **Remove / Done mode.** Shows removable chips: folders, unreachable sources ("Unavailable"), and file bookmarks that have no gallery cell.
- **The ✕ on an own thumbnail.** Removes a file bookmark, or *hides* that image when it came from a folder bookmark. Hidden paths are stored in `wallpaperExcludedOwnPaths`; the files on disk are untouched.
- **"Open Wallpaper settings"** opens the system wallpaper pane.
- **Feature uninstall.** Clears caches and the store backup, but keeps the bookmarks so a reinstall restores the list.

**English strings:**
- "Wallpaper"
- "All", "Your pictures", "Apple"
- "Show on all Spaces"
- "Add image", "Add folder", "Remove", "Done", "Unavailable"
- "Choose images to keep in Vorssaint’s wallpaper list"
- "Choose a folder of images to keep in Vorssaint’s wallpaper list"
- "Open Wallpaper settings"
- "No wallpapers found", "No pictures added yet", "No Apple stills found"
- "Downloading…", "Could not download the wallpaper", "Could not set the wallpaper"
- "Previous", "Next"

**Windows mapping.**
- **Setting the wallpaper.** Use the `IDesktopWallpaper` COM interface: `GetMonitorDevicePathAt`, `SetWallpaper(monitorId, path)` per monitor (or a null monitor id for all), and `SetPosition(DWPOS_FILL)`. Fall back to `SystemParametersInfo(SPI_SETDESKWALLPAPER)`.
- **Per-virtual-desktop wallpapers** on Windows 11 need the undocumented `IVirtualDesktopManagerInternal`. Verify whether `IDesktopWallpaper` changes all desktops, then decide what "all Spaces" means. Recommendation: "Apply to all monitors" only.
- **System stills.** The Windows source is `C:\Windows\Web\Wallpaper\<theme>\*`. `C:\Windows\Web\4K` holds lock screens; skip it.
- **OneDrive placeholders.** These are the iCloud analog. Check `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` or `FILE_ATTRIBUTE_OFFLINE` before thumbnailing; opening a placeholder hydrates it.
- **Thumbnails.** Use `IShellItemImageFactory`.
- **Sources.** Plain paths replace bookmarks.
- **Settings link.** `ms-settings:personalization-background`.
- **Drop** the macOS store patch and backup.

### 3.10 Dynamic Island (high level)

The island is a black (or glass) pill that merges with the MacBook camera notch, or a simulated notch on other displays. It hosts **compact live activities**, **transient notices**, and an **expanded panel of pages** (the app calls them "sections" or "modules").

It is the biggest subsystem in the app (~33K lines). This section gives an inventory and the container behavior a port needs. Per-module internals are out of scope.

#### 3.10.1 Container: window model

- **Window.** One borderless, non-activating, transparent panel with no shadow and a forced dark appearance.
  - It can take keyboard focus only while open, so typing in a section works without activating the app.
  - Level: just above the menu bar's status items. It joins all Spaces and floats over full-screen apps.
  - A private "overlay Space" keeps it from sliding during Space swipes.
  - Exposed to accessibility as "Dynamic Island"; hidden from window managers.
- **Click-through.** The window is never larger than the island itself. The only extra space is an animation envelope plus 72 DIP gutters for floating buttons when open. Transparent pixels pass clicks through, and hit-testing outside the visible silhouette returns nothing. While the island hides, it ignores the mouse so the menu bar under it works.
- **Display choice** (`notchDisplay`):

  | Value | Behavior |
  |---|---|
  | `automatic` (default) | Built-in display with a notch, else any notched display, else the main display |
  | `builtIn` | The built-in display; withdraws with the lid closed |
  | `main` | The display with the menu bar |
  | `pointer` | Follows the pointer after a 0.2 s dwell, only while closed and at rest |
  | `all` | Like `pointer`, plus a non-interactive copy of the closed island on every other display; clicking a copy moves the island there and opens it |

- **Position.**
  - Horizontally centered, top-anchored, pixel-snapped.
  - Notch geometry comes from the OS safe-area insets.
  - Height = camera height, or the menu-bar height when "Hide gap below notch" is on (default), plus manual fit values. Capped at 64.
- **Top-edge clicks.** The menu bar owns the top pixel row even above the island, so a top-edge mouse monitor opens the island on release over its activation area.
- **Full screen.**
  - Drawn over full-screen Spaces, and behaves the same there by default.
  - With "Hide content in full screen" on: a physical notch shrinks to a bare cutout; a simulated island hides entirely (it opens only via a shortcut).
  - Entering full screen collapses the island and hands volume and brightness keys back to the OS.
- **Mission Control.** The window list is polled to detect Mission Control, and the island fades out 0.14 s while it is up. **Drop on Windows.**
- **Session.** The island presents only when the session is unlocked, awake, on the console and the displays are awake. Losing any of these tears the presentation down; regaining them rebuilds it.

#### 3.10.2 States, sizes, motion

Common geometry:
- **Corners:** bottom radius min(28, 0.34·h). The top corners curve outward into the screen edge ("shoulders") with size min(14, 0.19·h).
- **Capsule:** fully round ends, radius min(h/2, 28).

| State | Trigger | Size (DIP) |
|---|---|---|
| Hidden | Hidden-until-hover at rest; simulated island when the menu-bar center isn't confirmed free; full-screen hide; suspended | ordered out / alpha 0 |
| Bare cutout | Physical notch, nothing to show | camera width × camera height |
| Resting with wings | Idle content (battery / music / AI) or the companion | camera + 2×44 per side (each side needs ≥ 44 of free menu room) |
| Hover pulse | Pointer over a closed island in click-to-open mode | ≤ +10 per side, +5 height; off with Reduce Motion |
| Compact live activity | §3.10.5 | camera + 2 wings. Wing widths: timer and Keep Awake 44–64 (80 with a download); agents 44–80; watch 56–150; calendar 72–120; downloads 56; music fits the cover and the bars, and hovering the cover shows the title (up to 240) |
| Activity picker | Hover with ≥ 2 activities | width ≥ strip + 48, ≤ 3 columns; rows of 32 |
| Notice | Transient event | camera + wings (36–240 each) |
| Notification card | Hover on a mirrored banner | min(max(400, camera + 200), expanded width) |
| Peek | "Preview on hover" | min(screen − 24, max(camera + 110, 340)) × (top + 52) |
| Drop target | File drag (option) | peek width × (top + 66); dashed rectangle, "Drop files here" |
| Expanded | Open | width 480 (Compact) / **560 (Spacious, default)** / custom 360–600 (default 440); content budget 180/264; custom max height 260–640 (default 480); width ≤ screen − 168 |

**Springs** (duration, bounce):

| | Width | Height |
|---|---|---|
| Growing | 0.44 s, 0.25 | 0.38 s, 0.22 |
| Shrinking | 0.30 s, 0 | 0.26 s, 0 |

- Overshoot ≤ 12 DIP; "settled" within 0.5.
- **Content transitions:**
  - reveal: content hidden for the first 35% of 0.45 s, then blur 12 → 0 and scale 0.86 → 1;
  - dismiss: 0.40 s;
  - page switch: 0.18 s crossfade.
  - Reduce Motion: fades only (0.2 s).

**Auto-close.**
- A hover-opened island closes after the close delay (default **0.18 s**).
- A peek closes 0.12 s after the pointer leaves.
- An island opened by click or shortcut never closes on pointer exit.
- The **pin** ("keep open") disables automatic closing.

#### 3.10.3 Opening, closing, gestures, keyboard

**Opening modes:**
- "Click to open" (default): hover only pulses.
- "Preview on hover": hover shows a peek.
- "Expand on hover": hover opens the island.
- "Hidden until hover": the window is removed until the pointer enters the closed island's rectangle.

**Hover dwell.**
- Activation **0.25 s** (0.10–1.0, step 0.05); close 0.18 s (0.10–2.0).
- Separate short passes never add up; re-entering restarts the delay.
- After an explicit close with the pointer still inside, hover is suppressed until the pointer leaves.
- Hover-opened islands don't take keyboard focus.

**Clicks.**
- An invisible activation button toggles the island.
- A calendar countdown click opens that event; a click on the capsule's music bars plays or pauses.

**Scroll/swipe gestures** (default on):
- Scroll down ≥ 16 DIP over the closed island → open.
- Scroll up ≥ 16 over the open header (or on the music page) → close.
- Horizontal trackpad swipe ≥ 40 over music → next/previous track.
- Direction lock: needs ≥ 4 DIP of travel and 1.5× dominance on one axis.
- Wheel deltas ×24.
- One action per physical gesture; momentum may finish an open or close within 0.35 s.
- Modifier keys disable gestures.

**Keyboard.**
- **There is no dedicated global hotkey for the island.** Other features route into its pages: clipboard, shelf, quick tools, scratchpad, Command Bar, capture controls, the app panel.
- Inside the island:
  - ⌘K: Explore.
  - ⌥⌘ + letter: jump to a section.
  - ⌃Tab / ⌃⇧Tab: next or previous section.
  - ⌘1–9: paste on the Clipboard page.
  - Esc steps back one level (detail → page → overlay → collapse).

**Click outside** collapses the island, unless:
- it is pinned;
- a menu, sheet or modal is open;
- a working surface is active (file chooser, permission prompt, capture preview, utility);
- the click is on the app's own status item or on the on-screen keyboard.

**Another app activating** also collapses it, except a hover-opened island with the pointer still inside and no click since opening.

**File drag and drop.** With the Files section and the shelf on, any external file drag turns the closed island into a drop target; dropping goes to the shelf (§3.1).

#### 3.10.4 Sections (pages)

**The 16 pages**, each with its ⌥⌘ letter:

| Page | Letter | Page | Letter |
|---|---|---|---|
| controls | c | system | i |
| mixer | v | tools | t |
| music | m | calendar | a |
| clipboard | b | notifications | n |
| captures | s | timer | r |
| files | f | camera | w |
| downloads | d | agents | g |
| scratchpad | p | watch | o |

- A page appears only if its feature is installed and enabled. Order and visibility come from `notchModuleOrder` / `notchHiddenModules`.
- **Explore gallery** (⌘K, the header grid button, or a floating button):
  - Tiles 92×86 with 8 gaps, about 4–5 columns, 3 visible rows.
  - One wheel notch moves one row; a search field filters tiles.
- **Floating buttons** around the open island:
  - Ø44 DIP, in 72 DIP gutters, up to 3 per side (left, right, bottom).
  - Defaults: left Explore + Timer; right Settings + Mixer; bottom Music.
  - Appear with a 0.38 s spring once the island arrives.
- **Header.** The title, plus actions revealed on hover: update, customize, clear, pin, settings, collapse.
- **Reopening.** The last page is kept in memory only; the app starts on Controls. Options: "When reopening" can be a fixed page, the app panel or Explore. "Open the visible activity" (default on) opens the page of the activity on display.
- **Settings tabs:** Layout, Content, Activity, Behavior, Companion.
  - **Layout:** a live proportional editor (drag floating buttons between sides, add/rename/remove, a corner grip for Custom size); size presets; translucency; the no-notch shape and fit; outline; notch fit.
  - **Content:** a draggable page list with show/hide toggles and per-page options.
  - **Activity:** idle content; low-battery tint; "Show over the menus"; indicator toggles; Lock Screen.
  - **Behavior:** opening mode and timings, gestures, haptics, reopening, full-screen hide, display, "Where things open", show in screenshots.
  - **Companion:** the mascot.

#### 3.10.5 Simulated notch, live activities, notices

**Simulated notch** (no camera housing; default shape `capsule`):
- **Capsule.** A pill floating *inside* the menu bar.
  - 20 DIP tall on a 24 DIP bar (margin min(2, ⌊(bar − 20)/2⌋)).
  - About 109 DIP wide at rest.
  - Closed strips grow to their content (8 DIP end padding) with per-kind caps: notification 380, notice 340, calendar 300, download 280, music 260, others 220.
  - Open, it becomes a rounded rectangle with radius 28.
  - Fit: width −40…+40, height −4…+4, distance from top 0–20.
- **Notch shape.** A black cutout 180 × bar / 32 wide (≈ 135 DIP on a 24 DIP bar).
- **Visibility rule.** At rest, a simulated island shows **only when the menu-bar center is confirmed free of menus**. This is read via Accessibility every 1 s with an 8 DIP clearance, and fails closed. It is skipped when "Show over the menus" is on (the default).

**Compact live activities**, in automatic priority order:

> timer → watch → downloads → agents → calendar → music → Keep Awake

- Music shows only while playing (if "Show music while playing" is on). Keep Awake is opt-in.
- Hovering with ≥ 2 activities shows a picker. The choice persists through gaps.
- Supported pairs ("Combine"): timer + downloads; a running timer + agents/calendar/music; calendar + downloads/agents/music.
- **Idle content** (`notchIdleContent`, default music): none, battery, music, or AI limits. The companion mascot can occupy it.

**Notices** (one at a time; equal or higher priority replaces):

| Notice | Priority | Duration |
|---|---|---|
| Volume, brightness, keyboard light, microphone | 3 | 1.6 s |
| Screenshot preview | 2 | 12 s |
| Timer finished, Watch result | 2 | 6 s |
| Power/battery, accessory | 1 | 4 s |
| AI agent event | 1 | 5 s |
| System notification banner | 1 | 3 s |
| Download finished | 0 | 6 s |
| New track | 0 | 3 s |
| Copied | 0 | 2.5 s |

- **Volume HUD replacement.** While the island can show feedback, an event filter swallows the volume keys and applies the change itself: 1/16 steps, or 1/64 with ⌥⇧. The system HUD is therefore never shown. Keys pass through to the OS while the island is hidden.

**Appearance.**
- Pure black by default, so it blends with the hardware notch.
- Optional 2 DIP outline (white 65%; orange while the compact timer shows).
- "Translucent background" when open.
- Liquid Glass on macOS 26 (open states only).
- Haptics on open, peek and gallery steps (`notchHapticFeedback`).
- Reduce Motion, Increase Contrast and Reduce Transparency are honored.

#### 3.10.6 Module inventory (one paragraph each)

Every module needs the island on (`notchEnabled`, default **off**) and its page visible. Most also have a hub flag (`featureAvailable.notch<Name>`).

1. **Music / Now Playing** (`notchShowPlayingMusic`).
   - **Compact:** cover art left of the camera, 7 equalizer bars right (tinted with the cover's average color). Hovering the cover 0.3 s shows title/artist; hovering the bars turns them into play/pause.
   - **Expanded:** large cover; title/artist; seekable timeline; previous / play-pause / next; Shuffle (if supported); a "Playback source" picker with multiple players; mute, volume and output device; Lyrics and Up Next toggles. Empty: "Nothing playing".
   - **macOS data:** private MediaRemote through a perl-hosted helper streaming JSON (since macOS 15.4). Commands go to the player; Apple Events are a fallback for players that refuse direct commands (per-app Automation consent; Shuffle only works this way).
   - **Constants:** a 1.5 s grace keeps the last song through track gaps; at most 16 players.
   - **Windows:** `GlobalSystemMediaTransportControlsSessionManager` (sessions + `SessionsChanged`, `TryGetMediaPropertiesAsync` with thumbnail, timeline, playback info, `TryTogglePlayPause` / `TrySkipNext` / `TryChangePlaybackPosition` / `TryChangeShuffleActive`). Estimate live position from `LastUpdatedTime`. Volume via `IAudioEndpointVolume`. **Port (v1).**
2. **Lyrics** (`notchLyricsEnabled` on; online lookup `notchLyricsOnline` **off**, opt-in).
   - A panel under the player with timed lines; ±0.25 s timing nudges (±10 s max); import of .lrc/.txt.
   - Source: **LRCLIB** `GET https://lrclib.net/api/get` with track, artist, album and duration (1–3600 s). Accepted only if title and artist match, album matches when present, and duration is within ±2 s. Prefers timed LRC.
   - Request: no cache or cookies, 10 s / 15 s timeouts, 128 KB cap, redirects refused.
   - **Windows: port with Music (v1).**
3. **Live equalizer** (`notchLiveEqualizer` **off**).
   - Drives the music bars from real audio: a per-process system-audio tap on the player's processes, FFT into 7 bands (50–12k Hz), 30 updates/s.
   - Needs "System Audio Recording" consent; 8 s of silence falls back to canned bars.
   - **Windows:** WASAPI per-process loopback (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, Windows 11) matched via `IAudioSessionControl2::GetProcessId`, or full-output loopback / `IAudioMeterInformation`. **v2; v1 ships canned bars.**
4. **Up Next (queue)** (`notchQueueEnabled`).
   - Upcoming songs from the player's MediaRemote queue (≤ 20), with "Play now".
   - **Windows: drop** (no queue API).
5. **Calendar** (`notchCalendarEnabled`; countdowns `notchCalendarCountdown`/`notchCalendarTimeLeft` **off**).
   - **Compact:** an opt-in countdown during the hour before a timed event starts, or before the current one ends ("m:ss · 14:30").
   - **Expanded:** "Next 7 days" agenda, month grid, week strip, "Open Calendar".
   - **macOS data:** EventKit, events only, skipping cancelled and declined. Refreshes on store, clock or day changes and at least every 15 min.
   - **Windows:** Microsoft Graph `/me/calendarView` (MSAL, Calendars.Read), plus optional Google/ICS. `Windows.ApplicationModel.Appointments` is unreliable (needs package identity; the store is fed by the retired Mail & Calendar app). **v1.1.**
6. **Notifications mirror** (`notchNotificationsEnabled`; hide-native `notchDismissNativeNotifications` off).
   - A 3 s banner per system notification (hover holds it and expands it to a card), plus an inbox of up to 50 for the session.
   - **macOS data:** **Accessibility** observation of the Notification Center process; labelled title/subtitle/body only.
   - **Windows:** `UserNotificationListener` (needs **package identity** via MSIX or a sparse package, the capability and user consent). Reads toasts only. Can remove from Action Center but **cannot suppress pop-ups** or activate toasts ("Open" launches the app by AUMID). **v2.**
7. **Timer / Pomodoro / Stopwatch** (`notchTimerEnabled`).
   - **Compact:** orange icon + time.
   - **Expanded:** mode picker, ruler minute picker, Pomodoro phases (25/5/15, long break every 4, 4 sessions by default).
   - Timer 1–180 min; alarm "Glass" every 2 s for up to 5 min; "Time is up" notice 6 s. Sessions are in memory only.
   - **Windows: port (v1)**; use a sleep-inclusive clock.
8. **Downloads** (`notchDownloadsEnabled`; folder `notchDownloadsFolderBookmark`).
   - **Compact:** an arrow with the file name plus a progress bar or spinner.
   - **Expanded:** the folder's files newest first; open / show / "Add to Files shelf".
   - **macOS data:** watches **one folder the user picks**; partial files `.crdownload` / `.download` / `.part`; Safari's published progress; the browser "download finished" broadcast. A partial with no activity for 120 s is inactive.
   - **Windows:** Downloads known folder + `ReadDirectoryChangesW`; partials `.crdownload`, `.part`, `.partial`, `.opdownload`; bytes received with an indeterminate bar (no progress publishing). **v1.**
9. **Files (Shelf in the island) and file tools.** The Shelf hosted in the island (§3.1), with the media "Optimize" chooser and Create ZIP (§3.6.10). **Windows: v1.1** (needs OLE drop targets and a top-edge drag sensor).
10. **Watch (screen-area watcher)** (`notchWatchEnabled`).
    - Pick part of any window; OCR it every 1 s while the page is open, 2 s otherwise.
    - Alert when it changes, stops changing (30 s), shows a text, or reaches a number. A change must last 3 s; Glass sound + 6 s notice.
    - Needs screen recording.
    - **Windows:** `Windows.Graphics.Capture` (works while covered, not while minimized) + `Windows.Media.Ocr`. **v2.**
11. **Companion (mascot)** (`notchMascotEnabled` **off**; not installed by default).
    - A small character beside the camera that visits, reacts to events (timer, music, agent finished, downloads, screenshots, mic, charging…), dozes, and fronts the Command Bar.
    - Styles, shapes, palettes, visit frequency.
    - **Windows: optional v2** (pure drawing).
12. **Lock screen** (`notchLockScreen` off).
    - Island plus a padlock animation, an activities line and a music player over the macOS lock screen, via a private window-server level.
    - **Windows: drop** (no supported lock-screen drawing). Optionally keep lock/unlock sounds via `WTSRegisterSessionNotification`.
13. **Accessories** (`notchAccessoriesEnabled`).
    - Bluetooth connect notices and low-battery notices (≤ 20%, recovered at 25%), up to 8 queued.
    - **Windows:** `DeviceWatcher` + GATT Battery Service 0x180F / device battery property. **v1.1.**
14. **Mac battery and power** (`notchBattery`).
    - Notices on plug/unplug/full/20%; idle content "Battery" with an amber/red tint.
    - **Windows:** `PowerManager` / `RegisterPowerSettingNotification`. **v1.**
15. **Keep Awake activity** (`notchKeepAwakeActivity` off).
    - A cup icon + time left.
    - **Windows:** `PowerSetRequest` / `SetThreadExecutionState`. **v1.**
16. **AI Agents** (`notchAgentsEnabled`). The display of §3.8. **v1.1** once the parsers are ported.
17. **Level pop-ups** (volume, brightness, keyboard light, mic; all on).
    - Replace the system HUD.
    - **Windows:** a keyboard hook swallowing `VK_VOLUME_*` + `IAudioEndpointVolume` (volume and mic in v1). Brightness keys are usually firmware-handled (observe `WmiMonitorBrightnessEvent`). Keyboard light: drop.
18. **Controls page** (default home).
    - Volume, brightness, keyboard-light sliders, a music mini player, and tiles: Mixer, Keep Awake, Timer, Calendar, Microphone, Screenshot, Recording, Speed test, App panel, Command Bar, Scratchpad. Several are hidden by default.
    - **Windows: v1 subset.**
19. **Mixer page.**
    - Per-app volume, per-app output routing.
    - **Windows:** `IAudioSessionManager2` / `ISimpleAudioVolume`; routing needs the undocumented policy-config interface. **v1.1.**
20. **System page.** CPU, GPU, memory, network, disk, power and fan cards (system-monitor spec). **Windows:** tray flyout or a later page.
21. **Clipboard page and "Copied" notice.** The clipboard history in the island; ⌘1–9 paste. **Windows:** only if that area is ported.
22. **Scratchpad page** (§3.3.12), **Camera page** (§3.5), **Captures and capture controls** (screenshot/recorder specs), **Tools / App panel / Command Bar** pages. Each follows its own feature.
23. **Infrastructure.**
    - Decorative clock: looping animations capped at 30 fps and stopped when hidden.
    - Update control in the header.
    - Copies of the island on other displays.

#### 3.10.7 Opinion: what makes sense as a Windows top-center overlay

**Recommended container behavior.**
- **The core problem.** On macOS the closed island lives in the menu bar, a strip apps never use. Windows has no such strip: the top center of a maximized window holds real UI (Office/VS Code search boxes, browser and Terminal tabs) and the **Windows 11 Snap Layouts bar**.
- **At rest: hidden** (or an optional 3–4 DIP "lip").
  - Reveal on a deliberate hover: a 2 px top band held **250 ms**.
  - Add a **configurable global hotkey** (macOS has none, since the notch is clickable).
  - Offer "Always show pill" as an option.
- **Click-through until intent.** Keep the resting and compact visuals `WS_EX_TRANSPARENT` until the dwell completes, so quick top-edge clicks still reach the tab or caption underneath.
- **Compact live activities contextually.** Show them only when the window under the pill isn't maximized or full screen. Otherwise flash them briefly on change and retract.
- **"Hide in full screen" on by default** on Windows. Detect via `SHAppBarMessage` `ABN_FULLSCREENAPP`, plus a foreground-rect == `rcMonitor` check, plus `SHQueryUserNotificationState`.
- **Monitor.** Default to the primary monitor. Keep Follow pointer (0.2 s), All monitors, and Built-in, and add an explicit monitor picker.
- **Shape.** Default to the **capsule** floating 4–6 DIP below the top edge.
- **Yield to system UI.**
  - On `EVENT_SYSTEM_MOVESIZESTART`, collapse and become fully input-transparent (or cloak with `DWMWA_CLOAK`) until `MOVESIZEEND`, so the Snap bar works.
  - Re-assert topmost on foreground changes.
  - Hang below a top-docked taskbar (Windows 10 or third-party tools).
- **Focus.** Hover-open = `WS_EX_NOACTIVATE` (Esc and shortcuts stay with the front app). Click or hotkey open = activate. Close on deactivation or an outside click (a mouse hook only while open).
- **Rendering.**
  - DirectComposition / Windows.UI.Composition with an animated path clip; the springs in §3.10.2 port as data.
  - Black by default; Acrylic only when open and transparency effects are enabled.
  - `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` for "don't show in screenshots".
- **Drop:** the frame probe, overlay Space, Mission Control concealment, AX menu-bar measurement, haptics, and the lock-screen island.

**Module verdicts.**

| Module | Compact | Expanded | macOS source | Windows API | Verdict |
|---|---|---|---|---|---|
| Music / Now Playing | ✓ | ✓ | MediaRemote (helper) | GlobalSystemMediaTransportControlsSessionManager | **Overlay v1** |
| Lyrics | — | ✓ | LRCLIB | same HTTP API | Overlay v1 |
| Live equalizer | bars | bars | process audio tap + FFT | WASAPI process loopback | v2 (canned bars in v1) |
| Up Next queue | — | ✓ | MediaRemote queue | none | **Drop** |
| Calendar | ✓ | ✓ | EventKit | Microsoft Graph / ICS | v1.1 |
| Notifications | ✓ | ✓ | Accessibility on Notification Center | UserNotificationListener (package identity) | v2 |
| Timer / Pomodoro | ✓ | ✓ | in-app | in-app | **Overlay v1** |
| Downloads | ✓ | ✓ | folder events + browser hints | ReadDirectoryChangesW + partial extensions | **Overlay v1** |
| Files / Shelf | drop target | ✓ | Shelf | OLE drag & drop | v1.1 |
| Watch | ✓ | ✓ | window capture + Vision | Windows.Graphics.Capture + Windows.Media.Ocr | v2 |
| Companion | ✓ | — | drawing | composition | optional v2 |
| Lock screen | ✓ | — | private window level | none | **Drop** |
| Accessories | notices | — | IOBluetooth | DeviceWatcher + GATT battery | v1.1 |
| Battery | notices + idle | — | IOKit power | PowerManager | **Overlay v1** |
| Keep Awake | ✓ | — | power assertions | PowerSetRequest | **Overlay v1** |
| AI Agents | ✓ | ✓ | §3.8 | §3.8 | v1.1 |
| Volume/mic pop-ups | ✓ | — | event filter + CoreAudio | LL keyboard hook + IAudioEndpointVolume | **Overlay v1** (brightness partial; keyboard light dropped) |
| Controls | — | ✓ | various | per feature | v1 subset |
| Mixer | — | ✓ | CoreAudio | audio sessions (+ undocumented routing) | v1.1 |
| System, Clipboard, Scratchpad, Camera, Captures, Tools | — | ✓ | other features | per feature | follow those features |

**Suggested Windows v1 overlay:** Music (+ Lyrics, new-track notice), Timer/Pomodoro, Downloads, Keep Awake, volume/mic pop-ups, battery notices, and a Controls subset. All of these use public, unpackaged Win32/WinRT APIs with no consent prompts.

**Main risks:**
- Only apps that publish a Windows media session appear in Music, and some update their position rarely.
- The native Windows volume flyout and toasts can't be suppressed with public APIs. `IAudioEndpointVolume` changes don't trigger the flyout, but hardware keys handled by OEM utilities may.
- The notification listener needs package identity.

---

## 4. Settings tables

**Common conventions**
- In every table below, the raw key equals the key shown.
- Every feature also has an availability flag, `featureAvailable.<id>` (Bool). Its default comes from §1.3 for upgraders; a clean install defaults it to false.
- Values marked "sanitized" are corrected on read: replaced by the default, or clamped where the table says so.
- Shortcut values use the format `"<mods>:<macOS keyCode>"` (§1.3). On Windows, store VK codes instead; see §8.5 for the conversion.
- "Backup: no" means the key is never exported in a settings backup.

### 4.1 Shelf

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `shelfEnabled` | Bool | false (not registered) | — | Master switch |
| `shelfShortcutEnabled` | Bool | true | — | Register the global hot key |
| `shelfShortcut` | String | `control+option+command:2` (⌃⌥⌘D) | shortcut format; invalid → default | Toggle hot key |
| `shelfShortcutAddsFinderSelection` | Bool | false | — | Shortcut adds the file manager's selection |
| `shelfShakeToOpen` | Bool | true | — | Shake during a drag summons the shelf |
| `shelfDropZoneEnabled` | Bool | true | — | Docked pill near the tray icon |
| `shelfDockPlacement` | String | `menuBar` | `menuBar`, `topCenter` (ignored while the island is on); other → menuBar | Docked placement |
| `shelfEdgeDragEnabled` | Bool | false | — | Edge peek |
| `shelfCloseAfterDrop` | Bool | false | — | Close after an accepted external drop (the pin overrides) |
| `shelfRemoveAfterDrop` | Bool | true | — | Remove dragged items after an accepted drop; also decides whether Copy+Move or Copy-only is offered |
| `shelfClearOnClose` | Bool | false | — | The close button clears unpinned items |
| `shelfAutomaticExclusions` | [String] | [] | trimmed, de-duplicated, order kept | Source apps whose drags never auto-open the shelf |
| `shelfItems` | Data (JSON) | absent | ≤ 200 leaves (§5.1) | The saved shelf. Backup: **no** |
| `panelControlShelf` | Bool | true | — | Tray panel row visible |
| `screenshotAddToShelf` | Bool | false | — | Auto-add screenshots |
| `notchShelf` | Bool | true | — | Island Files page hosts the shelf (macOS) |
| `notchDragReveal` | Bool | true | — | Island shows a drop target during drags (macOS) |

### 4.2 Radial menu

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `radialMenuEnabled` | Bool | false | — | Master switch |
| `radialMenuAtPointer` | Bool | true | true = at the pointer, false = at the screen center | Placement |
| `radialMenuActivationMode` | String | `pressOrHold` | `pressOrHold`, `press`, `hold`; other → pressOrHold | Opening behavior |
| `radialMenuProfiles` | Data (UTF-8 JSON array) | absent | §5.2 | All wheels. Absent → migrated from the legacy keys below |
| `radialMenuShortcut` | String | `control+option+command:49` (⌃⌥⌘Space) | shortcut format | Legacy seed for the first profile; also the shortcut-role key |
| `radialMenuMouseButton` | String | `off` | `off`, `back`, `forward`, `button:N` (N 3–31); other → off | Legacy seed for the first profile's mouse button |
| `radialMenuItems` | Data (JSON array) | absent | §5.2 | Legacy seed items. Absent → starter wheel; present but empty stays empty |
| `panelControlRadialMenu` | Bool | true | — | Tray panel row visible |
| (shared) `middleClickEnabled`, `middleClickTapFingers` | Bool, Int | false, 0 | fingers 0/3/4 | The four-finger tap is unavailable to the wheel while Middle Click uses 4 fingers |

Per-profile fields inside the JSON:

| Field | Values |
|---|---|
| name | ≤ 60 characters |
| color | 12 values |
| shortcut | shortcut format or "" |
| mouseButton | trigger format |
| trackpadTap | Bool |
| preset | preset id |
| items | ≤ 12 per level, depth ≤ 2 |

### 4.3 Scratchpad

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `scratchpadShortcutEnabled` | Bool | false | — | Register the hotkey |
| `scratchpadShortcut` | String | `control+option+command:45` (⌃⌥⌘N) | shortcut format | Smart-toggle hotkey |
| `scratchpadRetention` | String | `never` | `never`, `day`, `week`, `month`; other → never | Auto-clear period |
| `scratchpadCloseOnClickOutside` | Bool | true | — | Hide on outside click; the initial pin state is the inverse |
| `scratchpadBackgroundOpacity` | Double | 0.0 | clamped 0…1; NaN/±∞ → 1.0; slider step 0.05 | Opaque fill over the blur |
| `scratchpadTextSize` | Double | 13 | rounded, clamped 10…22; NaN → 13 | Editor and preview body size (pt) |
| `scratchpadDocument` | Data | not registered | legacy only | Old storage; migrated, then deleted. Backup: **no** |
| `panelUtilityScratchpad` | Bool | true | — | Tray panel row visible |
| `notchScratchpad` | Bool | true | — | Island takes the scratchpad (macOS) |
| `notchScratchpadControlHidden` | Bool | false | — | One-time island migration marker (macOS) |

The notes themselves (`Scratchpad.json`) are **never** backed up.

### 4.4 Cleaning Mode

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `cleaningModeKeepScreenVisible` | Bool | false | — | Corner indicator instead of a black screen (read live) |
| `panelUtilityCleaning` | Bool | true | — | Tray panel row visible |

The gesture constants (Esc, 5 presses, 6 s, 5 s release wait) are not user settings.

### 4.5 Camera preview

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `cameraPreviewShortcutEnabled` | Bool | false | — | Register the hotkey |
| `cameraPreviewShortcut` | String | `control+option+command:13` (⌃⌥⌘W) | shortcut format | Toggle hotkey |
| `panelUtilityCameraPreview` | Bool | true | — | Tray panel row visible |
| `notchCameraEnabled` | Bool | true (older island setups migrated to false) | — | Island camera page (macOS) |
| *(new on Windows)* `cameraPreviewDeviceId` | String | "" | a device id | The explicitly picked camera. On macOS this is the system's per-app preferred camera |

### 4.6 Media tools

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `mediaLastTool` | String | `videoCompressor` | `videoCompressor`, `gifMaker`, `imageCompressor`, `textExtractor`; other → video | Last tool (not written by the island) |
| `mediaVideoStart` / `mediaVideoEnd` | Double (s) | 0 / 0 | end ≤ 0 means full length; clamped at run time | Video trim (reset on new input) |
| `mediaVideoQuality` | Double | 0.68 | the UI writes 0.88 / 0.68 / 0.28; clamped 0.1–1; non-finite → 0.7 | Compression level (resolution mode) |
| `mediaVideoMaxDimension` | Int | 1280 | 640–3840 step 320 | "Size" (affects only Medium) |
| `mediaVideoSizing` | String | `resolution` | `resolution`, `targetSize` | Sizing mode |
| `mediaVideoTargetMegabytes` | Int | 20 | 1–512 | Target size (× 1,000,000 bytes) |
| `mediaVideoFPS`, `mediaVideoKeepAudio`, `mediaVideoCodec` | Double, Bool, String | 30, true, `h264` | — | Registered but **unused** (fps follows the source, audio is kept, H.264) |
| `mediaGIFStart` / `mediaGIFEnd` | Double | 0 / 0 | as for video | GIF trim |
| `mediaGIFQuality` | Double | 0.74 | — | **Unused** (the UI passes 0.74) |
| `mediaGIFWidth` | Int | 720 | 160–1600 step 80 | Longest-edge cap |
| `mediaGIFFPS` | Double | 12 | 1–30, rounded; invalid → 12 | Frame rate |
| `mediaGIFLoops` | Bool | true | — | Loop forever, or play once |
| `mediaGIFSizing` | String | `resolution` | `resolution`, `targetSize` | Sizing mode |
| `mediaGIFTargetMegabytes` | Int | 10 | 1–512 | GIF target |
| `mediaImageQuality` | Double | 0.72 | clamped 0.1–1 | Lossy quality |
| `mediaImageMaxDimension` | Int | 1600 | 64–20,000 step 128; ≤ 0 → 1600 | "Max side" |
| `mediaImageFormat` | String | `jpeg` | `jpeg`, `heic`, `png`, `pdf`; other → jpeg | Output format |
| `mediaImageStripMetadata` | Bool | true | — | "Remove metadata" |
| `mediaImageResizeKind` | String | `maxDimension` | `none`, `maxDimension`, `width`, `height`, `exact` | Resize mode |
| `mediaImageResizeWidth` / `…Height` | Int | 1600 / 1200 | 1–20,000 step 64 | Width/Height and Custom sizes |
| `mediaImageExactResizeMode` | String | `stretch` | `stretch`, `fit`, `fill` | Custom behavior |
| `mediaImageWatermarkKind` | String | `off` | `off`, `text`, `logo`, `textAndLogo` | Watermark kind |
| `mediaImageWatermarkText` | String | "" | trimmed when drawn | Watermark text |
| `mediaImageWatermarkLogoPath` | String | "" | absolute path | Logo file. Backup: **no** |
| `mediaImageWatermarkPosition` | String | `bottomRight` | `topLeft`, `topRight`, `center`, `bottomLeft`, `bottomRight` | Position |
| `mediaImageWatermarkOpacity` | Double | 0.45 | 0.1–1 step 0.05 | Opacity |
| `mediaImageWatermarkMargin` | Int | 32 | 0–2000 step 8; **≤ 0 → 32** (quirk) | Margin (px) |
| `mediaImageWatermarkScale` | Double | 0.18 | 0.05–0.8 step 0.01 | Logo size as a fraction of the short side |
| `mediaImageRenamePattern` | String | "" | token pattern; "" = `{name}` | Output name |
| `mediaImageBackground` | String | `transparent` | `transparent`, `white`, `black` | Background fill |
| `mediaImagePreserveModificationDate` | Bool | false | — | "Keep original modified date" |
| `mediaImageSaveInSubfolder` | Bool | false | — | Batch output into "Converted" |
| `mediaImageProfiles` | String (JSON) | `[]` | array of profiles (§5.6) | Saved profiles. Backed up with logo paths cleared |
| `mediaImageSelectedProfileID` | String | "" | profile id | Selected profile |
| `mediaTextAccurate` | Bool | true | — | OCR Accurate vs Fast |
| `mediaTextLanguageCorrection` | Bool | true | — | **Unused** (always on) |
| `panelUtilityMedia` | Bool | true | — | Tray panel tile visible |

### 4.7 Input fixes

**Extra click filter, button shortcuts, scrolling, acceleration**

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `mouseClickDebounceEnabled` | Bool | false | — | Click filter on |
| `mouseClickDebounceWindowMs` | Int (ms) | 25 | 5–100; outside → 25 | Bounce window |
| `mouseButtonShortcutsEnabled` | Bool | false | — | Shortcut switch (re-read on every press) |
| `mouseButtonShortcuts` | [String:String] | {} | keys "3"…"31", "-2", "-1"; values in shortcut format; invalid entries dropped | Button → shortcut |
| `mouseSpacesGestureEnabled` | Bool | false | — | Button-drag desktop gesture |
| `mouseSpacesGestureButton` | Int | 0 (none) | 3–31; ignored if the button has a shortcut or the radial menu claims it | Bound button |
| `mouseSpacesGestureFollowsDrag` | Bool | false | — | Swap left and right |
| `mouseNavigationEnabled` | Bool | false | — | Side buttons Back/Forward (drop on Windows) |
| `smoothScrollEnabled` | Bool | false | — | Smooth scrolling |
| `smoothScrollStep` | Int (px/tick) | 40 | 0 → 40; clamped 20–100; UI step 10 | Speed |
| `smoothScrollResponse` | Int (%) | 65 | clamped 0–100; UI step 5 | Response time 160 → 40 ms |
| `smoothScrollCoast` | Int (%) | 0 | clamped 0–100; UI step 5 | Landing stretch up to 3× |
| `linearScrollEnabled` | Bool | false | — | Linear scrolling |
| `linearScrollLines` | Int | 3 | 0 → 3; clamped 1–10 | Lines per notch |
| `scrollInverterEnabled` | Bool | false | — | Invert vertical (mouse) |
| `scrollInverterHorizontalEnabled` | Bool | false | a migration copies the vertical value if unset | Invert horizontal (mouse) |
| `scrollHorizontalEnabled` | Bool | false | — | Modifier + wheel scrolls sideways |
| `scrollHorizontalModifier` | String | `shift` | `shift`, `option`, `control`, `command`; other → shift | Which modifier (Windows: Shift/Alt/Ctrl/Win) |
| `mouseAccelerationDisabled` | Bool | false | — | Linear pointer movement |
| `smoothScrollExceptions`, `linearScrollExceptions`, `scrollInverterExceptions`, `mouseNavigationExceptions`, `mouseButtonExceptions` | [String] | [] | sanitized list (§3.7.8) | Per-feature "Apps to leave alone" |
| `panelControlMouseScroll`, `panelControlMouseNavigation`, `panelControlMouseButtonShortcuts`, `panelControlMouseAcceleration`, `panelControlLinearScroll`, `panelControlMouseClickDebounce` | Bool | true | — | Tray panel rows visible |

**Key debounce and Super key**

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `keyboardDebounceEnabled` | Bool | false | — | Key debounce |
| `keyboardDebounceWindowMs` | Int (ms) | 5 | 0–500; out of range → 5 (**not** clamped) | Global window; 0 = off |
| `keyboardDebounceKeyWindows` | String | "" | `"keyCode:ms,…"` sorted by keyCode, e.g. `37:100,40:0`; malformed parts skipped | Per-key overrides (macOS key codes; 0 = never filter) |
| `panelControlKeyDebounce` | Bool | true | — | Tray panel row |
| `superKeyEnabled` | Bool | false | — | Super key |
| `superKeySource` | String | `capsLock` | `capsLock`, `rightCommand`, `rightOption`, `rightControl`, `rightShift`; other → capsLock | Key to hold (Windows: Right Win/Alt/Ctrl/Shift, optionally Apps) |
| `superKeyModifiers` | String | `control+option+shift+command` | tokens joined by "+" in fixed order; must contain control, option or command, else default | Modifier set (**Windows default: Ctrl+Alt+Shift**) |
| `superKeySoloAction` | String | `none` | `none`, `capsLock`, `inputSource`, `escape` | Tap action |
| `superKeyExceptions` | [String] | [] | app ids or absolute paths | Pause while any listed app runs |
| `panelControlSuperKey` | Bool | true | — | Tray panel row |
| `superKeyMappingApplied` | Bool | unregistered | machine state | Write-ahead "remap may be in place". Backup: **no** (macOS only) |
| `superKeyMappedSource` | String | unregistered | source raw value | Which remap entry to remove. Backup: **no** (macOS only) |

**Quit and close protection.** Each row exists twice: once with `Quit` and once with `Close` in the key name (shown as `{Quit\|Close}`).

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `quitProtection{Quit\|Close}Enabled` | Bool | false | — | Protect ⌘Q / ⌘W (Windows slots: Alt+F4 / Ctrl+W) |
| `quitProtection{Quit\|Close}Mode` | String | `hold` | `hold`, `doublePress`, `extraModifier`; other → hold | Confirmation mode |
| `quitProtection{Quit\|Close}HoldDurationMs` | Double | 800 | **clamped** 250–2000; non-finite → 800 | Hold time |
| `quitProtection{Quit\|Close}DoubleIntervalMs` | Double | 600 | clamped 200–1500; non-finite → 600 | Second-press window |
| `quitProtection{Quit\|Close}ExtraModifier` | String | `shift` | `shift`, `option`, `control` | Extra modifier |
| `quitProtection{Quit\|Close}Scope` | String | `all` | `all`, `selectedOnly`, `allExceptSelected` | How the list applies |
| `quitProtection{Quit\|Close}Exceptions` | [String] | [] | app ids, sorted, de-duplicated | Allow-list or deny-list |
| `quitProtection{Quit\|Close}ShowFeedback` | Bool | true | — | Show the HUD |

### 4.8 AI agent usage

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `notchAgentsEnabled` | Bool | true (false for users with an older island setup) | — | The AI Agents feature (docs say "off until you turn it on": decide) |
| `notchAgentsClaude` / `…Codex` / `…OpenCode` / `…Copilot` | Bool | true | missing → true; the last enabled toggle can't be turned off | Per-agent reading |
| `notchAgentsCardOrder` | String | "" | comma list from `limits,spend,live,trend,models,projects,activity,resets`; unknown/duplicates ignored; missing cards appended | Card order |
| `notchAgentsHiddenCards` | String | "" | comma list, written sorted | Hidden cards |
| `notchAgentsPeriod` | String | `today` | `today`, `week`, `month` | Period for Spend, Trend, Models and Projects |
| `notchAgentsLimitDisplay` | String | `remaining` | `remaining`, `used` | "Show limits as" Left or Used |
| `notchAgentsLimitFocus` | String | `mostUsed` | `mostUsed`, `session`, `weekly` | Which limit the closed island shows |
| `notchAgentsLiveActivity` | Bool | true | — | "Show it in the closed Dynamic Island" |
| `notchAgentsReadout` | String | `elapsed` | `elapsed`, `tokens`, `cost`, `limit` | "Beside the camera": Time / Tokens written / API value / Limit |
| `notchAgentsFinishAlert` | Bool | true | — | "When a task finishes" |
| `notchAgentsFinishMinimum` | Double (s) | 60 | menu 0, 30, 60, 120, 300; clamped 0–3600 | "For tasks longer than" |
| `notchAgentsLimitAlert` | Bool | true | — | "Near a plan limit" (also gates renewal notices) |
| `notchAgentsLimitThreshold` | Double (%) | 80 | menu 50, 75, 80, 90, 95; clamped 1–100 | "Warn at … used" |
| `notchAgentsDailyBudget` | Double (USD) | 0 (off) | menu 0, 5, 10, 25, 50, 100, 250; ≤ 0 → off | "Daily API value budget" |
| `notchAgentsPriceUpdates` | Bool | true | — | "Keep prices up to date" |
| (related) `notchIdleContent` | String | `music` | `none`, `battery`, `music`, `agents` | `agents` = "AI limits" at rest |

### 4.9 Wallpaper

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `wallpaperApplyAllDisplays` | Bool | true | — | UI "Show on all Spaces": all displays plus (macOS) all Spaces |
| `wallpaperFilter` | String | `all` | `all`, `own`, `apple` | Gallery filter |
| `wallpaperOwnBookmarks` | [Data] | [] | sandbox bookmarks (Windows: [String] paths) | User images and folders |
| `wallpaperExcludedOwnPaths` | [String] | [] | sorted paths | Folder images hidden from the gallery |
| `panelShowWallpaper` | Bool | true | — | Panel section visible |

### 4.10 Dynamic Island (main keys only)

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `notchEnabled` | Bool | false | — | Master switch |
| `notchDisplay` | String | `automatic` | `automatic`, `builtIn`, `main`, `pointer`, `all` | Which display |
| `notchSilhouette` | String | `capsule` | `capsule`, `notch` | Shape without a camera |
| `notchCapsuleFitWidth` / `…Height` / `…Drop` | Double | 0 / 0 / 0 | −40…40 (even) / −4…4 / 0…20 | Capsule fit |
| `notchCameraFitWidth` / `…Height` | Double | 0 / 0 | −10…10 / −6…6 | Physical-notch fit |
| `notchHideMenuBarGap` | Bool | true | — | Cover down to the menu-bar bottom |
| `notchOutlineEnabled` | Bool | false | — | Outline |
| `notchSize` | String | `spacious` | `compact`, `spacious`, `custom` | Expanded size |
| `notchCustomWidth` / `notchCustomHeight` | Double | 440 / 480 | 360–600 / 260–640 | Custom size |
| `notchOpenOnHover` / `notchHoverExpands` / `notchHideUntilHover` | Bool | false / true / false | — | Opening mode |
| `notchHoverDelay` / `notchCloseDelay` | Double (s) | 0.25 / 0.18 | 0.10–1.0 / 0.10–2.0 | Dwell times |
| `notchGesturesEnabled` | Bool | true | — | Scroll/swipe gestures |
| `notchHapticFeedback` | Bool | true | — | Haptics |
| `notchHideInFullscreen` | Bool | false (**recommend true on Windows**) | — | Hide in full screen |
| `notchCoversMenus` | Bool | true | — | "Show over the menus" |
| `notchShowInCaptures` | Bool | true | — | Include in screenshots |
| `notchLiquidGlassEnabled` / `notchTranslucentBackground` | Bool | false / false | — | Glass or blur when open |
| `notchIdleContent` | String | `music` | `none`, `battery`, `music`, `agents` | Resting content |
| `notchShowPlayingMusic` / `notchKeepAwakeActivity` | Bool | true / false | — | Activities |
| `notchOpensActivity` | Bool | true | — | Open on the visible activity |
| `notchReturnHome` / `notchHomeModule` | Bool / String | false / `controls` | module ids | Reopening destination |
| `notchModuleOrder` / `notchHiddenModules` | CSV | "" / "" | module ids | Pages |
| `notchQuickAccessLayout` | Data (JSON) | empty → default layout | — | Floating buttons |
| Per-module enable keys (`notchVolume`, `notchBrightness`, `notchMicrophone`, `notchBattery`, `notchClipboard`, `notchCapture`, `notchTrackChange`, `notchAccessoriesEnabled`, `notchNotificationsEnabled`, `notchTimerEnabled`, `notchDownloadsEnabled`, `notchAgentsEnabled`, `notchWatchEnabled`, …) | Bool | mostly true | — | Notices and pages |
| `notchLockScreen` / `notchLockSounds` | Bool | false / false | — | Lock-screen island (drop on Windows) |
| `notchMascotEnabled` | Bool | false | — | Companion |

---

## 5. Data and files

**Path conventions.**
- `<app data>` means `~/Library/Application Support/com.vorssaint.utils/` on macOS (`.dev` suffix for dev builds). On Windows it becomes `%LOCALAPPDATA%\Vorssaint\`.
- `<cache>` means `~/Library/Caches/com.vorssaint.utils/` on macOS and `%LOCALAPPDATA%\Vorssaint\Cache\` on Windows.
- All app-written files are owner-only: directories 0700 and files 0600 on macOS. Every write is atomic (temp file, then rename).

### 5.1 Shelf

**Item store.** The items live in the settings key `shelfItems` as a JSON array. Writes are coalesced to one per UI-loop cycle and encoded on a background queue. **There is no flush on quit**, so the last change can be lost; the port should flush synchronously on exit.

Each entry has these fields:

| Field | Type | Notes |
|---|---|---|
| `id` | UUID string | If missing on read, a new one is generated |
| `kind` | `"file"` / `"text"` / `"link"` / `"batch"` | Required. An unknown kind drops only that entry |
| `title` | string | Missing → "". Rebuilt from the payload on restore if empty |
| `text` | string | kind text |
| `url` | absolute URL string | kind link |
| `path` | absolute file path | kind file |
| `bookmark` | base64 | kind file, optional. macOS bookmark; on Windows, the file ID or a serialized shell link |
| `children` | array of entries | kind batch (a pile) |
| `pinned` | `true` | Omitted when unpinned |

**Load outcomes.** A repository test enforces that only one decoder exists.

| Stored value | Outcome |
|---|---|
| Absent | `items([])` |
| Not a JSON array, or a non-empty array where nothing decodes | `unreadable` |
| Every stored entry (counted recursively) decodes | `items` |
| Otherwise | `partial` |

**Sanitize on restore.** Up to 200 leaves, nesting depth below 4.
- **file**: keep it if the path exists, or if it lives on an unmounted volume. Otherwise heal it via the bookmark (new path, new title), or drop it.
- **text**: drop if blank; truncate to 200,000 characters.
- **link**: requires a parsable URL whose scheme is not `file:`.
- **batch**: sanitize the children. Zero children → drop. One child → replace the pile with that child, which inherits the pin.

**Merging with items added during the restore.** Restored items go *before* anything added while the restore was running. The total stays capped at 200; restored items that don't fit are dropped whole. The orphan sweep runs only after an `items` load.

**Directories.**

| Location | Contents |
|---|---|
| `<app data>/ShelfFiles/` | Pasted images as `<UUID>.png`; GIFs as `<UUID>.gif`; received virtual files and generated screenshots as `<UUID>/<original name>`. Generated names must be plain file names: not ".", not "..", no "/" |
| `temp/VorssaintShelf/<bundle id>/<UUID>/` | Incoming folders for virtual files, deleted after each transfer. Also the fallback store when app data isn't writable (session-only) |
| `temp/VorssaintShelf/*.png` | Legacy files, deleted at launch |

**Ownership and cleanup.**
- Only files inside the store or temp directories are "owned", and only owned files are ever deleted. User files are referenced, never touched.
- Rejected additions are deleted immediately, unless a live item references the same path.
- Owned files of removed items are deleted **10 minutes** later. This timer runs in-process only.
- **Launch sweep.** Deletes store and temp entries that no live item references and that were modified before launch. A folder is kept while any descendant is still referenced.
- Discarding a virtual file removes only a UUID-named folder directly inside the store, never a caller-supplied path.

**Limits.** 200 leaves; 200,000 characters per text item. Files are referenced, so there is no per-file size limit; virtual files are copied whole.

### 5.2 Radial menu

**Storage.** Settings key `radialMenuProfiles`: binary data holding UTF-8 JSON. Nothing else is persisted. Icon and name caches live in memory only.

**Profile object.** Every key is optional when reading.

| Field | Type / values | Default |
|---|---|---|
| `id` | UUID string | new random id |
| `name` | string | "" |
| `color` | `accent` \| `blue` \| `purple` \| `pink` \| `red` \| `orange` \| `yellow` \| `green` \| `mint` \| `cyan` \| `indigo` \| `graphite` | `accent` |
| `shortcut` | shortcut string | "" |
| `mouseButton` | `off` \| `back` \| `forward` \| `button:N` (N 3–31) | `off` |
| `items` | array of items | [] |
| `preset` | `general` \| `media` \| `tools` \| `windowLayout` \| `quickToggles` \| `blank` | omitted when unknown |
| `trackpadTap` | bool | false |

**Item object.**

| Field | Type / values | Notes |
|---|---|---|
| `id` | UUID string | new if missing |
| `kind` | `app` \| `file` \| `url` \| `shortcut` \| `tool` \| `quickToggle` \| `windowLayout` \| `media` \| `submenu` | **required** |
| `name` | string | "" = automatic |
| `symbolName` | symbol name | "" = automatic |
| `payload` | string | per kind, below |
| `customIconData` | base64 PNG | optional, ≤ 64 KiB, 64×64 |
| `children` | array of items | submenus only |

**Payload by kind.**
- `app`, `file`: absolute path; may start with `~`.
- `url`: normalized URL (§6.2).
- `shortcut`: shortcut string.
- `tool`, `quickToggle`, `windowLayout`, `media`: an id string. Media ids are `playPause`, `previousTrack`, `nextTrack`, `nowPlaying`.
- `submenu`: payload unused.

**Reading** (lenient; there is no version field).
- Profiles are decoded one by one. A profile with wrong field types, or an invalid color or id, is dropped alone. An item with an unknown `kind` is dropped alone.
- If the stored value is a JSON array, clean it. Cleaning always yields at least one profile.
- If the value is absent or not an array, **migrate the legacy keys** into one "General" profile. Until that profile is saved, every read produces a new random id, so the port must **persist the seed immediately**.

**Cleanup**, applied on every read and write.
- *Profiles:* drop duplicate ids; trim names to 60 characters; drop invalid shortcuts; a mouse button already used by an earlier profile becomes `off`; an empty list becomes one General profile with ⌃⌥⌘Space and the 6 starter items.
- *Items, per level:*
  - At most 12 items; duplicate ids dropped.
  - Names trimmed to 60 characters; payloads trimmed.
  - Dropped when invalid: empty path, a link that won't normalize, an unparsable shortcut, or an unknown tool/toggle/layout/media id.
  - Custom icons over 65,536 bytes, or that fail to decode, are removed.
  - Submenus are allowed only at depth 0; non-submenu items lose any children.

**Backup.** All radial keys are exported, the profiles as base64 data.

### 5.3 Scratchpad

**File.** `<app data>/Scratchpad.json` holds all tabs in one file. It is written atomically with permissions reapplied, then **read back and byte-compared**.

| Field | Type | Notes |
|---|---|---|
| `pads` | array | First 12 kept on load; array order is tab order |
| `pads[].id` | string | UUID, uppercase canonical form |
| `pads[].name` | string | ≤ 40 characters after cleaning |
| `pads[].text` | string | No length limit |
| `pads[].modifiedAt` | number, optional | **Seconds since 2001-01-01T00:00:00Z** (Apple reference date; Unix = value + 978,307,200). Omitted when null; null whenever text is empty |
| `selectedID` | string | UUID; falls back to the first tab if missing from `pads` |

- **Strictness.** A missing `pads` or `selectedID` fails the whole load: `{}` fails. Extra keys are ignored.
- **Epoch.** On Windows, either keep the 2001 epoch for cross-platform compatibility or switch to Unix seconds and convert on import. Decide once.
- **Legacy sources** (migrated, then deleted only after a verified save): `<app data>/Scratchpad.txt` (UTF-8, single tab) and the settings key `scratchpadDocument`.
- **Exports.** Saved to a user-chosen path as UTF-8 without BOM, written atomically.
- **Cleanup.** None besides auto-clear, which empties text in place when the pad opens. No versions or backups are kept.

### 5.4 Cleaning Mode

Nothing persisted besides `cleaningModeKeepScreenVisible`. No logs or state files.

### 5.5 Camera preview

Nothing persisted. No frames are stored or recorded. On macOS the camera choice is kept by the OS; on Windows add `cameraPreviewDeviceId`.

### 5.6 Media tools

**Outputs, names and temp handling** are covered in §3.6.8. The temp directory is created on the destination volume and always removed afterwards. A crash can leave it behind for the OS to clean up.

**Image profiles.** Settings key `mediaImageProfiles` holds a JSON array:

```
[{id, name, options: {quality, maxDimension, format, stripMetadata,
  resizeMode: {kind, maxDimension, width, height, exactMode},
  watermark: {kind, text, logoPath, position, opacity, margin, scale},
  renamePattern: {rawValue}, background, preserveModificationDate}}]
```

- Only `resizeMode` decodes leniently: a missing `exactMode` becomes `stretch`.
- Any other missing field fails the **whole array**, and the UI then shows no profiles. This is a known quirk; decode leniently in the port.
- In backups, logo paths are cleared. A logo-only watermark becomes off; "Text + logo" becomes text, or off when the text is empty.

**"Edit" take folder.** `<app data>/Recordings/Take-<UUID>/take.mov`, mode 0700. It exists only while its editor is open.

### 5.7 Input fixes

- **Click filter, key debounce, button shortcuts, scrolling, quit protection:** nothing besides settings. All state is in memory.
- **Mouse acceleration recovery journal:** `<app data>/MouseAccelerationRecovery.json`, written atomically with mode 0600.
  - Contents: `{bootTime, entries:[{registryID, identity:{vendorID, productID, locationID, transport, physicalUniqueID, serialNumber}, key, original:{rawValue, isBoolean}}]}`.
  - A journal from a previous boot is deleted. Duplicate or zero registry ids, or unknown keys, make it invalid. The file is deleted when no entries remain.
  - A **guard child process** waits on a pipe. When the app dies, the pipe closes and the guard re-runs the app binary in cleanup mode to restore the originals.
  - **Windows:** journal the original `SPI_GETMOUSE` values (threshold 1, threshold 2, acceleration) and the speed. Restore them at the next start, or have a guard helper that waits on the parent's process handle.
- **Super key (macOS):** the system's volatile per-keyboard HID key-mapping table, where only the entry `[source → F18]` is ever added or removed. Plus the two write-ahead markers in settings and a temporary guard process. Nothing goes to disk. **Windows:** no remap. Keep a guard that releases injected modifiers if the app dies.

### 5.8 AI agent usage

**Files read on macOS.** Paths are fixed and no environment-variable overrides are applied. The Windows layout is in §3.8.13.

| Agent | Path | What is read |
|---|---|---|
| Claude Code | `~/.claude/projects/**/*.jsonl`, `~/.config/claude/projects/**/*.jsonl` | Transcripts. Subagents live at `<…>/<session>/subagents/*.jsonl` |
| Claude Code | `~/.claude/sessions/<pid>.json`, `~/.config/claude/sessions/<pid>.json` | `pid`, `sessionId`, `pidDomain` (< 64 KiB) |
| Claude Code | `~/.claude.json` | `oauthAccount.{organizationUuid, organizationType, organizationRateLimitTier, userRateLimitTier}` |
| Claude app | `~/Library/Application Support/Claude/plan-usage-history.json` (≤ 4 MiB) | `version`, `samples[].{t, org, u.{fh, sd, so, sn}}` |
| Codex | `~/.codex/sessions/**/*.jsonl`, `~/.codex/archived_sessions/**/*.jsonl` | Rollouts |
| OpenCode | `~/.local/share/opencode/opencode.db` (plus the `-wal` file's mtime) | SQL, read-only |
| Copilot CLI | `~/.copilot/session-state/<id>/events.jsonl` | Events |

**Files written.**

| File | Format | Retention |
|---|---|---|
| `<cache>/AgentUsage/agent-usage.bin` | Binary archive (below) | Rewritten on save. Deleted when the section is turned off or the provider set changes. An OS cache purge only causes a full reread |
| `<app data>/agent-prices.json` | The exact downloaded JSON | Replaced on each successful download. Its mtime is the "saved" time |

**Archive format, version 2.**
- **Layout:** the magic bytes `VAUA`, then a header, then a body, then an 8-byte little-endian **FNV-1a-64 checksum** of header + body.
- **Header:** format number (zigzag varint), string-table count, then each string as a length varint followed by UTF-8 bytes.
- **Body, in order:**
  1. Build string, which must equal `<version>-<build>` plus an optional ` <commit>`.
  2. Providers: sorted list.
  3. Records: key; provider, date, model, project, session, requests, 5 token counts, optional cost, savings; a flag "billable == recorded", followed by billable tokens when they differ; isAggregate, longCacheWrite, fast, domestic, webSearches; source paths.
  4. Limits: provider; windows (id, kind, optional minutes, optional scope, usedPercent, optional resetsAt); observedAt; source (0 log, 1 Claude app, 2 account).
  5. Optional Codex plan and the time it was observed.
  6. Turns, then waiting turns: id, provider, started, lastActivity, model, project, tokens, cost.
  7. Cursors: path, provider, offset (uvarint), inode (uvarint), discarding, modified, parser state, fingerprint (uvarint).
- **Encodings:** strings are varint indexes into the string table. Bools are exactly 0 or 1. Doubles are 8 bytes little-endian and must be finite. Dates are seconds since 2001-01-01.
- **Rejected on decode** (a test flips every single bit): counts larger than the remaining bytes, negative counts, duplicate dictionary keys, trailing bytes. Any failure reads as "no archive".
- **Never stored:** anything from OpenCode, reported costs, parent sessions, running commands, the process registry, settle state.
- **Windows:** the format can be kept as is, but switching to Unix-epoch dates is simpler; the archive isn't shared across platforms anyway.

### 5.9 Wallpaper

- **Settings:** bookmarks (macOS sandbox bookmarks; Windows plain paths) and hidden folder images.
- **Thumbnails:** memory only, 400 entries, cleared on uninstall.
- **macOS only:** a one-time backup `<app data>/WallpaperIndex.vorssaint-bak` of the system wallpaper store `~/Library/Application Support/com.apple.wallpaper/Store/Index.plist`. Never overwritten; deleted on feature uninstall. Drop on Windows.

### 5.10 Dynamic Island

- **Settings only**, with a few small per-module values:
  - the Downloads folder bookmark;
  - excluded calendar ids;
  - per-event countdown picks (not backed up);
  - quick-access layout JSON;
  - module order and hidden-module CSVs.
- **Not persisted:** lyrics (one song in memory, never saved), notifications (session memory only), timer sessions (memory only).

---

## 6. Algorithms and constants

Source paths below are relative to `Sources/Vorssaint/`. Units are DIP, ms and s unless stated otherwise.

### 6.1 Shelf

| Constant | Value | Source |
|---|---|---|
| Shake window / minimum samples | 0.5 s / 5 | `Services/Shelf/ShelfService.swift` → `handleDrag` |
| Shake direction threshold | \|dx\| > 6 | `handleDrag` |
| Shake reversals / travel / cooldown | ≥ 3 / > 220 / 1.0 s | `handleDrag` (`lastSummon`) |
| Drag watchdog | every 0.15 s (tolerance 0.05), only during a drag | `startDockedWatchdog` |
| Dock dwell / trigger margin / retreat margin | 0.15 s / 16 / 32 | `ShelfSupport.swift` → `ShelfDockDragSupport` |
| Fallback pill estimate | 72×32, 4 from the anchor | `ShelfDockDragSupport.triggerFrame` |
| Dock drag-end grace / catch tick | 0.15 s / 0.9 s | `endDockedDrag`, `dockDidAccept` |
| Dock top inset / side clamp / no-anchor inset | 4 / 8 / 12 | `ShelfDockPlacement.frame` |
| Status-item trust band | 48 | `StatusItemAnchorSupport.menuBarBand` |
| Edge trigger / retreat / dwell | 200 / 330 / 0.15 s (test-pinned) | `ShelfEdgeDragSupport` |
| Edge neighbor probe | distance + 1, frames padded 1 | `ShelfEdgeDragSupport.match` / `hasNeighbor` |
| Peek on-screen width / margins / end grace | round(w/3) / 8 / 0.15 s | `positionEdgePeek`, `revealEdgePeek`, `endEdgePeekDrag` |
| Auto-hide delay / fade | 5 s (tolerance 0.5) / 0.22 s at 60 Hz | `autoHideDelay`, `fadeOutAndHide` |
| Summon offset / clamp | 16 below the pointer / 8 | `ShelfService.position` |
| Owned-payload retirement | 600 s | `retireOwnedPayloads` |
| Max leaves / text length / restore depth | 200 / 200,000 / 4 | `ShelfPersistenceSupport` |
| Text title length | 48 chars | `textItem` |
| Thumbnail / generic icon | 64 / 20 (pixels = max(16, ⌈pt × scale⌉)) | `ImageThumbnailer` |
| Video thumbnail time | min(1 s, 10% of duration), zero tolerance | `VideoThumbnailer` |
| Card width / tile area / padding / spacing / radius | 304 / 188 / 14 / 11 / 18 | `UI/Shelf/ShelfView.swift` |
| Tile / spacing / inset / min content width | 78×88 / 10 / 4 / 276 | `UI/Shelf/ShelfTilesView.swift` |
| Drag start threshold | > 4 | `ShelfTileView.mouseDragged` |
| Open With cap | 40 apps | `ShelfTileView.menu(for:)` |
| Tooltip delay / max width / font / text cap | 1.0 s / 280 / 11 / 500 chars | `UI/Shelf/ShelfTooltipPopover.swift` |
| HUD | 1.5 s; fade in 0.12 s / out 0.22 s | `QuickToolHUD.show` |
| Hot key id | signature 'VUSH' (0x56555348), id 2 | `registerHotkey` |

**Tile grid columns** = max(1, ⌊(width − 8 + 10) / 88⌋).

**Test-pinned placement examples** (macOS coordinates, 1440-wide screen):
- tray icon at x 1200 → frame (1124, 906, 180×40);
- top center → (666, 906);
- no icon → minX 1320;
- clamp → maxX 1504.

**Test-pinned grid examples:** index 3 at (4, 102); index 6 at (4, 200).

**State machines:**
1. **Gesture.** Idle → (mouse-down, or first drag event) Open with a baseline → (mouse-up, or the watchdog sees the button up) Idle.
2. **Dock.** Hidden ↔ Pill ↔ Card.
   - During a drag: Pill → 150 ms inside the trigger → Card → outside the retreat → Pill.
   - At drag end: 0.15 s → Pill (has items) or Hidden.
3. **Edge.** None → Dwelling (150 ms) → Peeking → Revealed (on drop) or Retracted (pull back > 330, or release + 0.15 s).
4. **Card.** Hidden → Visible. Held ↔ Idle (5 s) → Fading (0.22 s; any hold condition aborts it) → Hidden.
5. **Finder-selection ticket.** Resolved exactly once: discard, toggle or add.
6. **Virtual-file transfer.** Created → Receiving → Finished → Delivered, or Discarded if cancelled.

### 6.2 Radial menu

**Geometry** (`Services/RadialMenu/RadialMenuSupport.swift` → `RadialMenuLayout`):

| Constant | Value |
|---|---|
| Window | 400×400 |
| Disc diameter | 300 |
| Chip ring radius / chip size | 112 / 52 |
| Hub diameter | 76 |
| Dead zone | 40 |
| Highlight wedge radii | 40–146 |
| Slice guide hairlines | 1 wide, radius 47–143, at angles 2π(i + 0.5)/n |
| Arming travel | 8 |
| Click beyond radius → close | > 150 |
| Outside-click tolerance | frame + 2 |
| Max items per level / max depth | 12 / 2 (root + 1) |
| Name cap | 60 chars |
| Hotkey ids | 1700 + profile index; signature 'VUQT' (0x56555154) |

**Slice math.**
- angle = atan2(dx, dyUp), mapped to [0, 2π).
- step = 2π/n.
- index = clamp(⌊((angle + step/2) mod 2π) / step⌋, 0, n−1).
- No slice inside the dead zone; no outer limit.
- Chip i sits at (112·sin(2πi/n), 112·cos(2πi/n)) with y up.
- Keyboard rotation: ((cur + δ) mod n + n) mod n, where cur = highlight ?? (δ > 0 ? −1 : 0).
- The wedge takes the shortest path: old + remainder(new − old, 2π).
- Test examples:
  - n = 4: angles 0 / π/2 / π / 3π/2 give slices 0 / 1 / 2 / 3.
  - n = 12: 2π−0.01 → 0; 2π−0.3 → 11.
  - dx = 10 → none; dx = 50 → slice 1 of 4.

**Actions.**

| Action | Value |
|---|---|
| Delay for tools, toggles, layout | 0.15 s |
| Modifier-release wait | poll every 15 ms, ≤ 100 polls (≈ 1.5 s), then +60 ms |
| Synthetic key-up delay | 40 ms |
| Media key codes | play 16, next 19, previous 20 (system-defined subtype 8; down 0xA00, up 0xB00) |
| Windows equivalent | `VK_MEDIA_PLAY_PAUSE` / `VK_MEDIA_NEXT_TRACK` / `VK_MEDIA_PREV_TRACK` |

**Motion** (all disabled by Reduce Motion):

| Element | Behavior |
|---|---|
| Window | fade in 0.18 s ease-out; fade out 0.13 s ease-in |
| Wheel scale | 0.9 ↔ 1; opening spring (response 0.27, damping 0.86); closing 0.13 s ease-in |
| Chip bloom | spring (0.36, 0.75); delay 0.17·i/(n−1) s; starts at scale 0.35, opacity 0, radius 8 |
| Chip fold | 0.1 s ease-in, delay 0.25 × the bloom delay |
| Highlight chip | scale 1.14, spring (0.24, 0.72); glow radius 13, offset 4, 45% profile color |
| Wedge | moves with spring (0.2, 0.86); shows and hides in 0.12 s |
| Hub cross-fade | 0.14 s, scale 0.92 |

**Spring equivalents** for non-SwiftUI animation (mass 1; stiffness = (2π/response)²; damping = 2·ζ·(2π/response)):

| Spring | Stiffness | Damping | Settles (2%) in |
|---|---|---|---|
| Open | 541.5 | 40.0 | ≈ 0.20 s |
| Bloom | 304.6 | 26.2 | ≈ 0.31 s |
| Highlight | 685.4 | 37.7 | ≈ 0.21 s |
| Wedge | 987.0 | 54.0 | ≈ 0.15 s |
| Canvas swap (0.25, 0.8) | 631.7 | 40.2 | ≈ 0.20 s |

**Disc and chip colors.**

| Element | Light | Dark |
|---|---|---|
| Disc tint | white 68% | black 42% |
| Disc rim | 1.2 gradient, white 95%→12% | white 30%→4% |
| Disc border | 0.8, black 9% | white 11% |
| Disc border, Increase Contrast | 24% | 28% |
| Disc shadow | black 22%, blur 24, y 8 | black 55%, blur 24, y 8 |
| Chip fill | white 88% | white 14% |
| Wedge gradient | profile color 5% at r 40 → 20% at r 150 | 8% → 30% |

**Light-mode profile colors** (RGB):

| Color | RGB |
|---|---|
| blue | (0, .48, 1) |
| purple | (.58, .20, .85) |
| pink | (.88, .16, .45) |
| red | (.85, .18, .18) |
| orange | (.95, .45, 0) |
| yellow | (.85, .65, 0) |
| green | (.18, .65, .25) |
| mint | (0, .68, .60) |
| cyan | (.15, .65, .85) |
| indigo | (.35, .35, .85) |
| graphite | white 0.40 (light) / 0.65 (dark) |

In dark mode the system colors are used.

**Now Playing** (`RadialNowPlayingService.swift`):

| Constant | Value |
|---|---|
| Reply timeout | 2.0 s |
| Artwork cap | 12,582,912 B |
| Helper output cap | 17,825,792 B |
| Text cap | 300 chars |
| Bundle id cap | 255 chars |
| Card | 320 wide, radius 16, 12 margin, fade-in 0.12 s, 76×76 artwork |

**Favicon fetch.**
- Request `/favicon.ico` at the same origin, keeping the port and dropping path, query and fragment. Example: `https://example.com:8443/favicon.ico`.
- 5 s timeouts, no cache or cookies, `Accept: image/*`.
- Download ≤ 2 MiB; source ≤ 4096 px per side and ≤ 16,777,216 px; output a 64×64 PNG ≤ 65,536 B.

**Link normalization** (`normalizedURL`):
1. Trim. Reject if empty or if it contains a space.
2. An explicit scheme is present when the string contains `://`, or matches `letter[letters digits + - .]*:`. Exception: a digit right after the colon counts as a *port*, not a scheme, when the part before it contains a dot or is `localhost`.
3. http/https must have a host; other schemes are kept verbatim.
4. Otherwise prefix `https://` and require a host.

Examples: `example.com:8080/x` → `https://example.com:8080/x`; `tel:5551234` is kept; `localhost:3000` → `https://localhost:3000`.

**Canvas** (`UI/Settings/RadialMenuVisualCanvas.swift`):
- Stage height 330; disc 250; ring 92; hub 68; dead zone 36; chip 44.
- Click versus drag threshold: 6.
- Stage background RGB (.08, .09, .11).

**Trackpad tap** (`MiddleClickSupport`):
- 4 fingers, ≤ 0.35 s.
- Centroid movement ≤ 0.03 and spread change ≤ 0.04, in normalized pad units.
- No key-down in the last 0.5 s; no extra finger; no physical click.

**Session state machine.**

| From | Event | To |
|---|---|---|
| Idle | trigger | build the filtered wheel; if it is empty, beep and stay Idle |
| Open/Held | release (or Super-key release) | the mode's release action: run selected, stay open → Sticky, or dismiss → Closing |
| Open/Held | arrow key (press-or-hold and press modes) or submenu | Open/Sticky |
| any Open state | leaf selection or any dismissal | Closing |
| any Open state | a second trigger | Closing, possibly then Open for another profile |
| Closing | fade ends | Idle |
| Closing | new trigger | cancel the fade, finish cleanup synchronously, then Open |

### 6.3 Scratchpad

| Item | Value | Source |
|---|---|---|
| Autosave debounce | 0.8 s, trailing | `Services/QuickTools/ScratchpadService.swift` → `scheduleSave()` |
| Fade-in (no fade-out) | 0.13 s | `show()` |
| Initial / minimum size | 380×300 / 280×220 | `ensurePanel()`, `windowWillResize` |
| Resize zones | 6 edges, 12 corners | `UI/Scratchpad/ScratchpadView.swift` → `ResizeBorderOverlayView` |
| Initial position | centered horizontally; top = work.top + 0.42·(work.h − h); clamp 16 | `center(_:)` |
| Outside-click tolerance | frame + 2 | `mouseIsInside(_:)` |
| Heights: header / tabs / format row / footer | 34 / 32 / 30 / 36 | `ScratchpadView` |
| Card radius / border | 14 / 1 white @12% | `ScratchpadView` |
| Tab | width 46–130, height 24, radius 6, selected accent @16% | `tabButton` |
| "Copied" feedback | 1.2 s (fade in 0.15, out 0.2); 1.6 s in the island | footer |
| Tab limit / name length | 12 / 40 chars | `ScratchpadSupport.swift` → `ScratchpadDocument` |
| Retention | day 86,400 s; week 604,800 s; month 2,592,000 s; clear if idle > limit | `ScratchpadRetention.maxIdleInterval`, `shouldClear` |
| Text size / opacity | 10–22 (default 13) / 0–1 (default 0) | `textSizeRange`, `backgroundOpacityRange` |
| Preview sizes | headings +5/+3/+1; code −1; inline code max(9, size−1); line spacing 2; rule 24 × "─"; indent 2 spaces per level | `MarkdownPreview.rendered` |
| Editor inset | 7×2, line padding 5 | `editorInset`, `PlainTextEditor` |
| Link placeholder | "url" | `linkPlaceholder` |
| Hotkey | id 18, signature 'VUQT'; N = key code 45 | `QuickToolHotkey` |
| Launch delay from other surfaces | 0.15 s | panel, launcher, Command Bar, radial |

**Tab name algorithm** (`nextPadName`). Base = the localized "Scratchpad".
1. If neither `base` nor `base 1` is used, return `base 1`.
2. Otherwise return the first unused name among `base 2` … `base 12`.
3. Otherwise return `base <count+1>`.

An unnumbered "Scratchpad" counts as slot 1 (test-asserted).

### 6.4 Cleaning Mode

| Item | Value | Source |
|---|---|---|
| Unlock key / count / window | Esc (macOS key code 53; Windows `VK_ESCAPE`) / 5 / 6.0 s inclusive | `Services/CleaningMode/CleaningModeManager.swift` → `escapeKeyCode`, `unlockThreshold`; `CleaningUnlockCounter` |
| Release wait limit | 5 s | `CleaningMouseReleaseGate.releaseWaitLimit` |
| Filter mask | key down/up, modifier changes, scroll, mouse down/up (L/R/other), system-defined (14), gesture (29) | `eventMask` |
| Filter placement | HID level, head insert, filtering, main run loop | `installTap()` |
| System-key decode | subtype 8: key = (data1 >> 16) & 0xFFFF; state = (data1 >> 8) & 0xFF (10 down, 11 up); repeat = data1 & 1. Subtype 1 = power-key down. Synthetic code = 10000 + key | `CleaningSystemKeyEvent.decode` |
| Overlay level / fallback size | shielding level / 800×600 | `makeOverlay()` |
| Dots animation | 0.15 s ease-out | `UI/CleaningMode/CleaningOverlayView.swift` |
| Launch delays | launcher and Command Bar 0.1 s; radial 0.15 s | — |

**Counter rules.**
- Repeat → ignored.
- Other key-down, or modifier down or up → 0.
- Esc within 6 s of the previous Esc → +1; otherwise → 1.
- Count ≥ 5 → unlock.

**State machine.**
1. Inactive → Active, once the permission is OK and the filter is created.
2. Unlock request → Pending, waiting for button releases (at most 5 s).
3. Pending → teardown on the next turn → Inactive, with paused features resumed.
4. Forced paths (session resign, filter dead without permission) go straight from Active to Inactive.

### 6.5 Camera preview

| Item | Value | Source |
|---|---|---|
| Size / radius / border | 320×240 / 14 / 1 white @14% | `UI/CameraPreview/CameraPreviewView.swift`; `CameraPreviewService.ensurePanel` |
| Position | top = work.top + 48; side clamp 16; bottom ≥ 16 from work.bottom | `Services/QuickTools/CameraPreviewService.swift` → `position(_:)` |
| Fade-in / hover animation | 0.13 s / 0.15 s | `show()`, view |
| Permission grace | 1.0 s | `installMonitors(for:)` |
| Outside-click tolerance | 2 | `mouseIsInside(_:)` |
| Capture quality | medium preset (Windows: ~640×480 or the nearest format) | `startSession()` |
| Hotkey | id 16; W = key code 13 | `CameraPreviewService.hotkey` |
| Control styling | padding 10; capsule black 55%; icon 10; chevron 8 | `controls`, `cameraMenu` |

### 6.6 Media tools

**Limits** (`Services/Media/MediaSupport.swift`):

| Constant | Value |
|---|---|
| `maxImageRenderDimension` | 20,000 px |
| `maxImageRenderPixels` | 67,108,864 (8192²) |
| `maximumGIFFrames` | 300 |
| Target size | 1–512 MB, 1000-based |
| `minimumTargetVideoBitRate` | 240,000 bps |
| `targetSizeHeadroom` | 0.94 (muxing overhead and rate-control overshoot) |
| `targetBitsPerPixel` | 0.07 |
| GIF target width | 160–1600 |
| GIF target fps | ≥ 6 |
| GIF passes | ≤ 4 |
| Video target passes | ≤ 3 (`MediaVideoTargetEncoder.maximumPasses`) |
| File name bytes | 255 |

**Video bitrate plan** (`MediaSupport.videoSizePlan`). Inputs: T = target bytes, d = trim length (s), source size, frame rate, has-audio, scale s.
1. fps = round(frame rate), clamped to 1–60 (30 if invalid).
2. Audio A = 0 with no audio. Otherwise A = 64,000 bps if T·8/d < 1,200,000, else 128,000.
3. budget = ⌊T·8·0.94 / d⌋ − A.
4. Video V = int(budget · min(1, s)). If V < 240,000 → no plan ("too small").
5. affordable pixels = V / (fps · 0.07).
6. ratio = min(1, √(affordable / source pixels)).
7. floor ratio = min(1, 480 / longest edge).
8. new longest edge = round(longest · max(ratio, floor ratio)).
9. Scale down to that edge (never up); even sides, minimum 2.

Notes:
- Only V scales on retries; A stays fixed.
- **Worked example:** 20 MB, 60 s, 1920×1080 at 30 fps with audio → A = 128 k, V = 2,378,666 bps, size 1418×798.
- Smallest workable target ≈ d·(240,000 + A)/7.52 bytes, i.e. ≈ 40,426 B/s with 64 k audio (1 MB ≈ 24.7 s; 20 MB ≈ 8.2 min).

**Video retry scale** (`targetRetryScale`). Applies only if actual > target: next = current · (target/actual) · 0.94; result = min(current · 0.9, next). Test value ≈ 0.7833. Every retry is ≤ 0.9.

**GIF retry** (`gifSizePlan`). Drops frames before pixels:
1. ratio = min(0.9, target·0.94 / actual).
2. next fps = max(6, round(fps · ratio)).
3. remaining = min(1, ratio / (next fps / fps)).
4. next width = max(160, round(width · √remaining)).
5. No plan if neither width nor fps decreased.

Example: 720 px at 15 fps, 12 MB against an 8 MB target → 720 px at 9 fps.

**GIF start values (file-size mode):**
- width = source width clamped 160–1600;
- fps = max(6, min(15, ⌊300/duration⌋));
- frame count = ⌈duration·fps⌉ ∈ [1, 300];
- maximum duration = max(1, ⌊300/fps⌋).

**Sizing helpers:**
- `scaledEvenSize`: scale = min(1, max(2, M)/longest); each side rounded, made even, minimum 2. Example: 1920×1080 at M 1000 → 1000×562.
- `scaledVideoSize`: then floor each side to a multiple of 16 (minimum 16). Example: 320×180 at M 180 → 176×96.

**avconvert preset choice** (Resolution mode; quality thresholds 0.82 / 0.58 / 0.4):

| Quality | Preset |
|---|---|
| ≥ 0.82 (Low compression) | HighestQuality, multi-pass |
| Medium (0.68) | by longest side L: ≤ 640 → 640×480; ≤ 960 → 960×540; ≤ 1280 → 1280×720; ≤ 1920 → 1920×1080; else HighestQuality |
| 0.4–0.58 | MediumQuality |
| < 0.4 (High compression) | LowQuality |

- Progress estimate: every 80 ms, min(0.95, elapsed / max(1, 0.75·trim)).
- The log keeps the last 8,000 characters.
- **Windows needs a calibrated replacement**, for example FFmpeg CRF ladders measured against avconvert output on reference clips.

**Encoder settings (file-size mode):**
- H.264 High profile, auto level, average bitrate V.
- Max keyframe interval 4 s; B-frames allowed; frame-rate hint.
- AAC 48 kHz stereo at A.
- Timescale 600; fast start; rotation written as track metadata.
- Wait loops: 50 ms for asynchronous work and cancel checks; 2 ms when writer inputs are busy.

**Image decode size** (`imageDecodeMaxPixel`):
- Non-custom modes: ⌈max(target width, target height)⌉, if safe.
- Custom mode: k = min(sx, sy) for Fit, max(sx, sy) for Fill and Stretch; decode scale = min(1, max(0, k)); max pixel = ⌈longest source side · scale⌉.
- Tests: Custom Fit decodes 100; Fill and Stretch decode 1000 (1000×100 → 100×100).
- Resize examples: 1920×1080 at max side 1000 → 1000×563; width 800 on 1600×1200 → 800×600; height 500 → 667×500.

**Watermark geometry** (`MediaService.drawWatermark`, `watermarkOrigin`; macOS origin is bottom-left):
- side = min(W, H); margin m; gap = max(6, side·0.015).
- **Logo:** fits a square of max(12, side·scale), drawn with alpha = opacity.
- **Text:** system semibold font at clamp(side·0.045, 12, 96) px, white at alpha = opacity. Shadow blur max(2, size·0.14), 1 px down, black at 0.45·opacity.
- **Overflow:** if logo + gap + text is wider than W − 2m, shrink everything by (W − 2m)/width, keeping font ≥ 8 and shadow blur ≥ 1.
- maxX = max(m, W − contentW − m); maxY = max(m, H − contentH − m).
- **Positions:** top-left (m, maxY); top-right (maxX, maxY); center = each axis max(m, centered); bottom-left (m, m); bottom-right (maxX, m). Logo on the left, text after it, vertically centered.
- **Windows (y-down):** top-left = (m, m); bottom-right = (maxX, H − contentH − m).

**Rename tests:**
- `{name}-{counter:03}-{date}-{time}-{width}x{height}-{ext}` → `Photo One-007-20240101-000000-800x600-png`.
- `../bad/name` → `bad-name`.
- The 247-byte cap.

**OCR language map** (`recognitionLanguages`). Each entry is followed by "en-US":

| App language | OCR language |
|---|---|
| pt-BR | pt-BR |
| tr | tr-TR |
| ru | ru-RU |
| es | es-ES |
| de | de-DE |
| fr | fr-FR |
| it | it-IT |
| ja | ja-JP |
| ko | ko-KR |
| uk | uk-UA |
| zh-Hans | zh-Hans |
| zh-TW, zh-HK | zh-Hant |
| anything else, including **sk** | en-US only |

On Windows, use real language packs (Slovak exists there).

**Compression levels:** Low 0.88, Medium 0.68, High 0.28. **Quick presets:** see §3.6.6.

### 6.7 Input fixes

**Click filter:**

| Item | Value | Source |
|---|---|---|
| Window | default 25 ms; 5–100 (out of range → 25); accepted iff t − lastAcceptedUp ≥ W | `Core/Defaults.swift` → `defaultMouseClickDebounceWindowMs`, `allowedMouseClickDebounceWindowRange`; `Services/MouseClickDebounce/MouseClickDebounceSupport.swift` → `MouseClickDebounceState.shouldSuppress` |
| Buttons | 0, 1, 2 | `MouseClickDebounceInput.resolve` |

Test example: a 6 ms window accepts a press 6 ms after release and filters one at 5 ms.

**Key debounce:**

| Item | Value | Source |
|---|---|---|
| Window | default 5 ms; 0–500 (out of range → 5); UI step 1 | `defaultKeyboardDebounceWindowMs`, `allowedKeyboardDebounceWindowRange`, `sanitizedKeyboardDebounceWindow` |
| Stale gap | 5 s; a backwards timestamp also resets | `KeyboardDebounceState.staleStateGapNanoseconds` |
| Comparisons | strict `<` | `KeyboardDebounceState.shouldSuppress` |
| Legacy migration | 30 or 10 → 5 | `migrateLegacyKeyboardDebounceWindow` |

Test examples:
- 10 ms window: release at 40.004, press at 40.014 → accepted.
- 5 ms window: release at 45.001; press at 45.005 suppressed, 45.006 accepted.
- Per-key 100 ms overrides a global 20 ms.
- Decoding `"37:100,bad,40:0,99:999"` gives {37:100, 40:0, 99:5}.

**Per-key catalog: macOS key code → Windows scan code (Set 1)**, used to convert imported overrides:

| Group | Mappings |
|---|---|
| Letters | A 0→1E, B 11→30, C 8→2E, D 2→20, E 14→12, F 3→21, G 5→22, H 4→23, I 34→17, J 38→24, K 40→25, L 37→26, M 46→32, N 45→31, O 31→18, P 35→19, Q 12→10, R 15→13, S 1→1F, T 17→14, U 32→16, V 9→2F, W 13→11, X 7→2D, Y 16→15, Z 6→2C |
| Digits | 0 29→0B, 1 18→02, 2 19→03, 3 20→04, 4 21→05, 5 23→06, 6 22→07, 7 26→08, 8 28→09, 9 25→0A |
| Keys | Space 49→39, Return 36→1C, Tab 48→0F, Delete/Backspace 51→0E, Escape 53→01 |
| Punctuation | comma 43→33, period 47→34, slash 44→35, semicolon 41→27, apostrophe 39→28, minus 27→0C, equals 24→0D, left bracket 33→1A, right bracket 30→1B, backslash 42→2B, grave/backtick 50→29 |

Unknown codes display as "#<code>".

**Button shortcuts and Spaces drag** (`Services/MouseButtons/*`):

| Item | Value |
|---|---|
| Mappable inputs | 3…31; Back 3, Forward 4; side wheel −2 (left) / −1 (right) |
| Side-wheel burst gap | 250 ms (≤ continues the burst) — `SideWheelGestureGate.quietNanoseconds` |
| Spaces step / overview step | 220 / 150 — `MouseSpacesGestureSupport.spaceStep`, `overviewStep` |
| Repeat cooldown / bank | 0.35 s / ±220 — `spaceRepeatCooldown`, `bankedTravelLimit` |
| Axis tie-break | larger of \|ΣX\|/220 and \|ΣY\|/150; ties go horizontal — `Tracker.committedAxis` |
| macOS symbolic hotkeys | 79 left, 81 right, 32 Mission Control, 33 App windows |
| Windows | Ctrl+Win+Left/Right; Win+Tab |
| Replay marker / glide tag / redirect tag | 0x564F5253 / 0x564F5253 / 0x564F5248 |

Tracker tests:
- exactly 220 fires and 219 doesn't; −150 → Mission Control, +150 → Exposé;
- one repeat per step, never inside 0.35 s, and a fast flick banks at most one extra step;
- the axis locks after the first firing; the overview fires once per press.

**Wheel classification and linear scroll** (`Services/ScrollWheelSupport.swift`):
- Touch grace: 1.0 s.
- Points per line: 10.
- Lines per notch: 1–10, default 3.
- Linear distance:
  - discrete: lines = sign·min(|ticks|, 1)·N;
  - continuous: points = lines·10;
  - carry is kept; reversal drops the carry; an all-zero result swallows the event.
- Test: slow, medium and fast notches all give 3 lines; four quarter-notches give one notch.

**Smooth-scroll engine** (`Services/SmoothScrollSupport.swift`):

| Constant | Value |
|---|---|
| Step | 20–100, default 40 px; 0 → 40 |
| Response R | 0–100, default 65 |
| Coast C | 0–100, default 0 |
| Response time | τ₀ = 0.160 − 0.120·R/100 s (R 0 → 160 ms; 50 → 100; 65 → 82; 100 → 40) |
| Coast stretch | S = 1 + (C/100)·2·(1 − rem/glide)² when glide > rem ≥ 0, else S = 1 (maximum 3) |
| Finish threshold | if \|rem\| ≤ 1/S px, emit all of rem |
| Frame clamp | Δt = min(elapsed, 1/20 s) |
| Per-frame emission | e = min(\|rem\|, max(\|rem\|·(1 − exp(−Δt/(τ₀·S))), (60/S)·Δt)), with the sign of rem |
| Fallback timer | 1/60 s; otherwise the display refresh rate under the pointer |
| Pixel rounding | truncate plus carry; the landing frame rounds to nearest; carry dropped on reversal; carry reset when the glide lands |
| Synthetic field clamp | ±1,000,000, Int32 |
| Continuous wheels | distance = (point delta, else fixed·10) · step/40 |

**Glide timings at 60 Hz for a 40 px notch** (evaluated, not test-pinned):

| Response | Coast 0 | Coast 50 | Coast 100 | First frame |
|---|---|---|---|---|
| 0 | ≈ 400 ms (24 frames) | 583 ms | 750 ms | 3.96 px |
| 50 | ≈ 300 ms | 450 ms | 600 ms | 6.14 px |
| 65 (default) | ≈ 267 ms (16 frames) | 400 ms | 533 ms | 7.36 px |
| 100 | ≈ 167 ms | 250 ms | 333 ms | 13.63 px |

At defaults, 120 px glides in ≈ 350 ms and 400 px in ≈ 450 ms.

Test-pinned engine values:
- first frame of a 100 px glide at the default response = 18 ± 0.5 px;
- 3 px remaining → 1 px at 1/60 s and 0.5 px at 1/120 s;
- identical results at 60 Hz and 120 Hz;
- coast 0 equals the shipped curve.

**Inversion plan** (`ScrollWheelSupport.inversionPlan`, `applyDirection`):
- Discrete + Shift + vertical-only → flip by the horizontal setting.
- Otherwise each axis is flipped by its own setting.
- Discrete events: negate the line field only, unless it is 0 (then all three fields).
- Continuous or redirected events: negate line, point and fixed fields.

**Super key** (`Services/SuperKey/*`):

| Item | Value |
|---|---|
| Solo hold threshold | 500 ms; ≥ counts as a hold — `SuperKeySupport.State.soloHoldThresholdNanoseconds` |
| Trigger key (macOS) | F18: HID usage 0x70000006D; key code 79 |
| Source HID usages | Caps Lock 0x700000039; RCtrl 0x7000000E4; RShift 0x7000000E5; ROption 0x7000000E6; RCommand 0x7000000E7 |
| Source key codes | Caps 57, RCmd 54, RShift 60, ROpt 61, RCtrl 62 |
| Windows sources | `VK_CAPITAL` (scan 0x3A), `VK_RWIN`, `VK_RMENU`, `VK_RCONTROL`, `VK_RSHIFT` (optionally `VK_APPS`) |
| Repair throttle | 3 s |
| Watchdog | clamp(2 × key-repeat delay, 3 s, 30 s); fallback 3 s |
| Guard stop | wait 6 s, then SIGTERM + 0.5 s, then SIGKILL + 0.5 s |
| Mapping tool | `hidutil` with a 5 s timeout and a 4 MiB output cap (macOS only) |

**Quit and close protection** (`Core/QuitProtectionSupport.swift`, `Services/QuitProtection/QuitProtectionService.swift`, `UI/QuitProtection/QuitProtectionHUD.swift`):

| Item | Value |
|---|---|
| Hold | 250–2000 ms, default 800 |
| Double press | 200–1500 ms, default 600; inclusive (≤) on event timestamps |
| Double-press pending expiry | interval + 100 ms |
| Extra-modifier pending expiry | 1.5 s |
| App Switcher second-press expiry | interval (no +100 ms) |
| Esc | key code 53 |
| Fallback key codes | Q 12, W 13 |
| Re-post marker | 0x5652535341494E54 |
| Keystroke-instead-of-terminate list | exactly `com.valvesoftware.steam` |
| HUD | min width 300; height 48 (+8 with progress); inset 12; 18 above the work-area bottom; title 13 semibold; detail 10.5 @68%; track inset 24, 3 tall, radius 1.5, 7 above the bottom; fill white 0.09 @0.95; stroke white @0.16 |

Clamp tests: hold 100 → 250, 3000 → 2000, NaN → 800; interval 100 → 200, 3000 → 1500. A double press at exactly 1.5 s confirms; at 1.5 s + 1 ns it doesn't.

Quit protection state machine (states: Idle, Pending(shortcut, mode, event, pid, generation), Swallowing(shortcut)):

| From | Event | To |
|---|---|---|
| Idle | base chord (hold mode) | Pending(hold), timer started |
| Pending(hold) | timer fires | Swallowing, confirm |
| Pending(hold) | key-up, ⌘ release, other key, Esc, app switch, switcher | Idle |
| Idle | base chord (double mode) | Pending(double), expiry interval + 100 ms |
| Pending(double) | second base chord within the interval | Swallowing, confirm |
| Pending(double) | second base chord after the interval | Pending, restarted |
| Pending(double) | key-up | Pending (unchanged) |
| Pending(double) | ⌘ release, other key, Esc, app switch, expiry, switcher | Idle |
| Idle | exact extra-modifier chord | Swallowing, confirm (modifier stripped) |
| Idle | base chord (extra mode) | Pending(extra), 1.5 s |
| Pending(extra) | key-up, ⌘ release, Esc, other key, expiry | Idle |
| Swallowing | same shortcut or repeat | swallow (stays Swallowing) |
| Swallowing | that key's key-up | swallow, then Idle |

**Exceptions** (`Services/MouseExceptions/MouseAppExceptionSupport.swift`):
- Cache lifetime 0.5 s; window layers 0–3.
- An unknown app is treated as excluded.
- Lookups never block the main thread (tests use a 0.5 s budget).

### 6.8 AI agent usage

**Timing** (`Services/AgentUsage/AgentUsageService.swift`, `AgentUsageStore.swift`, `Services/Notch/NotchAgentSupport.swift`):

| Constant | Value |
|---|---|
| History horizon | 91 days |
| Main tick | 30 s (tolerance 10) |
| Poll | 2 s (leeway 0.5); window 30 min; tick also polls cursors ≤ 24 h old |
| Publish debounce | 1 s |
| Save interval / root recheck | 5 min / 5 min |
| Watcher latency | 1.0 s |
| `lateEnd` | 300 s |
| `offlineGrace` | 20 s |
| `resumeWindow` | 3600 s (Claude) / 21,600 s (others) |
| `idleTurn` | 600 s |
| OpenCode settle | 30 s |
| Agent notice | 5 s, priority 1 |
| Mascot agent-start interval | 600 s |
| Strip and Limit tick / page redraw / resting refresh / settings status refresh | 1 s / 15 s / 60 s / 30 s |

**I/O:**

| Constant | Value |
|---|---|
| Chunk size | 4 MiB |
| Maximum line | 32 MiB |
| Fingerprint | FNV-1a-64 (offset basis 0xCBF29CE484222325, prime 0x100000001B3) over the offset plus the first and last 4096 bytes before it |
| OpenCode | tail 64 rows; open replies abandoned after 86,400 s; busy timeout 2000 ms |
| Claude session records | < 65,536 B |
| Copilot envelope | keys ≤ 256 B; nesting ≤ 128 |
| Numbers | finite, > 0, capped at 1e12 |
| Timestamps | values > 1e11 are milliseconds |
| Codex type sniff | ≤ 64 B |

**Dedup keys** are listed in §3.8.5. A repeated key merges by per-category max.

**Model-name normalization** (`AgentPricing.normalized`):
1. Lowercase and trim.
2. Cut everything before `claude-` if present.
3. For `claude-*`, replace `.` with `-`. Copilot writes `claude-sonnet-4.5`.
4. Keep only what follows the last `/`.
5. Cut at the first `@` or `[`.

**Lookup** (`AgentPricing.price`):
- `claude-*` ids use the Claude table; everything else (Codex, OpenCode, Copilot GPT models) uses the Codex table.
- A family matches at a word boundary: the next character must be `-`, `_` or `:`, followed by no digits or by ≥ 4 digits (a date).
- The longest matching family wins.
- **Unpriced** if the rest of the id contains a sibling word: mini, nano, pro, lite, research, search, audio, realtime, transcribe, tts, cyber, image.
- Unknown versions are **unpriced**, never given the previous version's price. Tests: `claude-opus-5-6`, `gpt-6-sol-2` and `gpt-7` are unpriced; `claude-opus-4-1-20250805` is priced at $15.

**Cost** (USD per million tokens; `AgentPricing.cost`):
- An aggregate record with no tokens → cost 0. An unknown model → cost nil, savings 0.
- m = fastMultiplier (if fast) × 1.1 (if US-only).
- inRate = outRate = m.
- If the record is not an aggregate and prompt = input + cacheWrite + cacheRead is **strictly greater than** `longContext.above`, then inRate ×= inputMultiplier and outRate ×= outputMultiplier.
- long = min(longCacheWrite, cacheWrite).

The cost formula:

> cost = [ input·in + (cacheWrite − long)·cw + long·cwLong + cacheRead·cr ]·inRate/1e6 + output·out·outRate/1e6 + webSearches·0.01

> savings = cacheRead · max(0, in − cr) · inRate / 1e6

- Reasoning tokens are not billed separately. Copilot shutdown aggregates never get the long-context premium.
- **Test anchors:**
  - Opus 5.5 with 1000 input, 4000 five-minute writes, 6000 one-hour writes, 100,000 reads and 2000 output = **$0.132**.
  - gpt-6-astra, 300 k input + 1000 output, fast + long context = (300k·10·4 + 1000·50·3)/1e6.
  - Exactly 272,000 input gets no premium ($2.72).
  - opus-5, 1M input, fast = $10; opus-4-7 = $5 (no fast multiplier); US-only opus-5 = $5.50.
  - sonnet-4-5, 250 k, long context = $1.5225.
  - 3 web searches = $0.03.

**Price-list validation** (any violation rejects the whole list):

| Field | Rule |
|---|---|
| File | ≤ 256 KiB; `schema` == 1; `updated` YYYY-MM-DD, years 2024–2100, a valid date |
| Tables | 1–500 unique models each |
| Model ids | `[a-z0-9._-]`, 1–64 chars; Claude ids must start `claude-`, Codex ids must not |
| `input`, `output`, `cacheRead` | 0–1000, required |
| `cacheWrite` | default = input |
| `cacheWriteLong` | default = cacheWrite |
| `fastMultiplier` | 1–10, default 1 |
| `longContext` | {above 1,000–10,000,000; inputMultiplier 1–10; outputMultiplier 1–10} |
| Plans | ≤ 50; `match` ≤ 40 chars; `name` 1–24 chars; `monthly` 0–100,000 optional |
| `claude.webSearch` | 0–1 |
| `claude.usOnlyMultiplier` | 1–3 |
| Types | booleans are rejected as numbers; extra fields are ignored |

**Bundled price table** (`Resources/agent-prices.json`, updated 2026-10-02). Rates are $ per million tokens.

*Claude* — input / output / cache read / cache write 5 m / cache write 1 h. Web search $0.01 each; US-only ×1.1.

| Models | Rates | Extras |
|---|---|---|
| fable-5-1, mythos-5-1 | 10 / 50 / 0.25 / 12.5 / 20 | — |
| fable-5, mythos-5 | 10 / 50 / 1 / 12.5 / 20 | — |
| mythos-preview | 25 / 125 / 2.5 / 31.25 / 50 | — |
| opus-5-5 | 4 / 20 / 0.2 / 5 / 8 | fast ×2 |
| opus-5, opus-4-8 | 5 / 25 / 0.5 / 6.25 / 10 | fast ×2 |
| opus-4-7, opus-4-6, opus-4-5 | 5 / 25 / 0.5 / 6.25 / 10 | — |
| opus-4-1, opus-4, 3-opus | 15 / 75 / 1.5 / 18.75 / 30 | — |
| sonnet-5-5, sonnet-5 | 2 / 10 / 0.2 / 2.5 / 4 | — |
| sonnet-4-6, 3-7-sonnet, 3-5-sonnet | 3 / 15 / 0.3 / 3.75 / 6 | — |
| sonnet-4-5, sonnet-4 | 3 / 15 / 0.3 / 3.75 / 6 | long context > 200,000: input ×2, output ×1.5 |
| haiku-4-5 | 1 / 5 / 0.1 / 1.25 / 2 | — |
| 3-5-haiku | 0.8 / 4 / 0.08 / 1 / 1.6 | — |
| 3-haiku | 0.25 / 1.25 / 0.03 / 0.3 / 0.5 | — |

*Claude plans* (substring match on tier + type, first match wins):

| Match | Name | Monthly |
|---|---|---|
| max_20x | "Max 20×" | $200 |
| max_5x | "Max 5×" | $100 |
| max | "Max" | — |
| enterprise | "Enterprise" | — |
| team | "Team" | — |
| pro | "Pro" | $20 |

*Codex* — input / output / cache read; cache write = input unless shown as "cw". **LC** = long context > 272,000 tokens (input ×2, output ×1.5).

| Models | Rates | Extras |
|---|---|---|
| gpt-6.1-sol | 2 / 10 / 0.1 | cw 2.5, fast ×2, LC |
| gpt-6-astra | 10 / 50 / 1 | cw 12.5, fast ×2, LC |
| gpt-6-sol | 2 / 10 / 0.2 | cw 2.5, fast ×2, LC |
| gpt-6-luna | 0.1 / 0.5 / 0.01 | cw 0.125, fast ×2, LC |
| gpt-5.6-sol, gpt-5.6, gpt-daybreak-blue-latest | 4 / 20 / 0.4 | cw 5, fast ×2, LC |
| gpt-5.6-terra | 2 / 12 / 0.2 | cw 2.5, fast ×2, LC |
| gpt-5.6-luna | 0.2 / 1.2 / 0.02 | cw 0.25, fast ×2, LC |
| gpt-5.6-cyber, gpt-daybreak-red-latest | 12.5 / 75 / 1.25 | cw 15.625, LC |
| gpt-5.5 | 5 / 30 / 0.5 | fast ×2.5, LC |
| gpt-5.5-pro, gpt-5.4-pro | 30 / 180 / 30 | LC |
| gpt-5.5-cyber | 12.5 / 75 / 1.25 | — |
| gpt-5.4 | 2.5 / 15 / 0.25 | fast ×2, LC |
| gpt-5.4-mini | 0.75 / 4.5 / 0.075 | fast ×2 |
| gpt-5.4-nano | 0.2 / 1.25 / 0.02 | — |
| gpt-5.3-codex | 1.75 / 14 / 0.175 | fast ×2 |
| gpt-5.3-chat-latest | 1.75 / 14 / 0.175 | — |
| gpt-5.2 | 1.75 / 14 / 0.175 | fast ×2 |
| gpt-5.2-codex, gpt-5.2-chat-latest | 1.75 / 14 / 0.175 | — |
| gpt-5.2-pro | 21 / 168 / 21 | — |
| gpt-5.1-codex-mini | 0.25 / 2 / 0.025 | — |
| gpt-5.1-codex-max, gpt-5.1-codex, gpt-5.1-chat-latest | 1.25 / 10 / 0.125 | — |
| gpt-5.1 | 1.25 / 10 / 0.125 | fast ×2 |
| gpt-5-codex, gpt-5-chat-latest, gpt-5-search-api | 1.25 / 10 / 0.125 | — |
| gpt-5-mini | 0.25 / 2 / 0.025 | fast ×1.8 |
| gpt-5-nano | 0.05 / 0.4 / 0.005 | — |
| gpt-5-pro | 15 / 120 / 15 | — |
| gpt-5 | 1.25 / 10 / 0.125 | fast ×2 |
| gpt-rosalind-research | 5 / 25 / 0.5 | — |
| chat-latest | 5 / 30 / 0.5 | — |
| codex-mini-latest | 1.5 / 6 / 0.375 | — |
| computer-use-preview | 3 / 12 / 3 | — |
| gpt-4.5-preview | 75 / 150 / 37.5 | — |
| gpt-4.1 | 2 / 8 / 0.5 | fast ×1.75 |
| gpt-4.1-mini | 0.4 / 1.6 / 0.1 | fast ×1.75 |
| gpt-4.1-nano | 0.1 / 0.4 / 0.025 | fast ×2 |
| gpt-4o | 2.5 / 10 / 1.25 | fast ×1.7 |
| gpt-4o-2024-05-13 | 5 / 15 / 5 | fast ×1.75 |
| gpt-4o-mini | 0.15 / 0.6 / 0.075 | fast ×1.666667 |
| gpt-4o-search-preview | 2.5 / 10 / 2.5 | — |
| gpt-4o-mini-search-preview | 0.15 / 0.6 / 0.15 | — |
| chatgpt-4o-latest | 5 / 15 / 5 | — |
| o1, o1-preview | 15 / 60 / 7.5 | — |
| o1-pro | 150 / 600 / 150 | — |
| o1-mini, o3-mini | 1.1 / 4.4 / 0.55 | — |
| o3-pro | 20 / 80 / 20 | — |
| o3-deep-research | 10 / 40 / 2.5 | — |
| o3 | 2 / 8 / 0.5 | fast ×1.75 |
| o4-mini | 1.1 / 4.4 / 0.275 | fast ×1.818182 |
| o4-mini-deep-research | 2 / 8 / 0.5 | — |
| gpt-4-turbo-2024-04-09, gpt-4-turbo, gpt-4-turbo-preview, gpt-4-0125-preview, gpt-4-1106-vision-preview | 10 / 30 / 10 | — |
| gpt-4-0613, gpt-4 | 30 / 60 / 30 | — |
| gpt-3.5-turbo, gpt-3.5-turbo-0125 | 0.5 / 1.5 / 0.5 | — |
| gpt-3.5-turbo-1106 | 1 / 2 / 1 | — |
| gpt-3.5-turbo-instruct | 1.5 / 2 / 1.5 | — |
| davinci-002 | 2 / 2 / 2 | — |
| babbage-002 | 0.4 / 0.4 / 0.4 | — |

*Codex plans* (exact match): `plus` → "Plus" $20; `pro` → "Pro" (no monthly price). Others are capitalized.

**Limits math:**
- `current`: a window whose renewal has passed shows 0% used and no renewal date.
- `pace` = (now − (resetsAt − length)) / length, only while the window is running.
- `binding` window: the most used; on a tie, the later renewal.
- Crossing tolerance: 60 s.
- Claude-app freshness 30 min; future tolerance 300 s; maximum age 7 days; weekly renewals land on UTC hours.
- Codex window kind: ≤ 720 min session; 8,640–11,520 weekly; otherwise other.
- Tints: orange at ≥ 0.80 used, red at ≥ 0.95.

**Formatting** (`AgentFormat`):

| Value | Format |
|---|---|
| Tokens | < 1000 as-is; then K, M, B with 1 decimal below 10, rounded down ("4.2K", "48K", "1.2M") |
| Cost | ≥ 10,000 → "$12K"; ≥ 100 → whole dollars; else 2 decimals |
| Percent | 0 decimals |
| Duration | 2 units (d h, h m, or m s) |
| Clock | "m:ss" / "h:mm:ss" |

**Animation:** glyph breathing period 1.2 s, pulse 1.6 s, capped at 30 fps, disabled by Reduce Motion.

### 6.9 Wallpaper

| Item | Value | Source |
|---|---|---|
| Page size / columns / thumb height / spacing / radius | 24 / 3 / 54 / 8 / 6 | `Services/Wallpaper/WallpaperSupport.swift` → `pageSize`; `UI/MenuPanel/WallpaperSection.swift` |
| Thumbnail max pixel / cache | 160 / 400 entries | `WallpaperThumbnailCache` |
| iCloud download poll / timeout | 250 ms / 60 s | `WallpaperService.ensureLocalFile` |
| Same-image re-set pause | 400 ms | `setDesktopImage` |
| Extensions | heic jpg jpeg png tif tiff gif bmp webp | `imageExtensions` |
| Page math | pageCount = ⌈n/24⌉ (0 for none); page clamped 1…pages | `pageCount`, `clampedPage`, `pageSlice` |

### 6.10 Dynamic Island

| Item | Value | Source |
|---|---|---|
| Hover / close delay | 0.25 s (0.1–1) / 0.18 s (0.1–2) | `Services/Notch/NotchSupport.swift` → `defaultHoverDelay`, `defaultCloseDelay` |
| Pointer-follow dwell / display-change debounce | 0.2 s / 0.1 s | `Services/Notch/NotchService.swift` → `pointerFollowDelay` |
| Mission Control polling (drop) | 0.25 s / 0.08 s; probe ≤ 0.5 s; 3 readings; 0.14 s fade | `Services/Notch/NotchWindowHost.swift` |
| Springs | grow W 0.44/0.25, H 0.38/0.22; shrink W 0.30/0, H 0.26/0; value change 0.5 s; overshoot 12; settle 0.5 | `NotchMotion` |
| Radii | min(28, 0.34h); shoulder min(14, 0.19h); capsule min(h/2, 28) | `NotchLayout` |
| Layout | header 36, spacing 12, bottom 16, inset 28, content budgets 180/264, vertical page 320 | `NotchLayout` |
| Widths | 480 / 560 / custom 360–600 (default 440); max height 260–640 (default 480) | `NotchSize` |
| Peek / resting wing / hover pulse | max(camera + 110, 340) × (top + 52) / 44 / ≤ 10 per side, +5 height | `peek`, `restingWingWidth`, `NotchHoverEmphasis` |
| Floating buttons | Ø44, gap 12, gutter 72, row 54, ≤ 3 per side; spring 0.38 s, bounce 0.12; withdraw 0.16 s | `NotchQuickAccessLayout` |
| Gestures | open/close 16; track 40; direction lock 4 and 1.5×; wheel ×24; momentum 0.35 s | `Services/Notch/NotchGestureSupport.swift` |
| Gallery | tiles 92×86, gap 8, steps 24/94, row animation 0.3 s | `Services/Notch/NotchSectionPaging.swift` |
| Simulated notch | width 180 × bar/32; capsule margin 2; resting aspect 5 | `NotchGeometry` |
| Menu-bar measurement | every 1 s (±0.2), AX timeout 0.15 s, clearance 8 | `Services/Notch/NotchMenuBarSpace.swift` |
| Notices | priority and duration table in §3.10.5 | `NotchEvent` |
| Volume steps | 1/16; 1/64 with ⌥⇧ | `Services/Audio/PreciseVolumeRollerService.swift` |

---

## 7. macOS dependencies → Windows mapping

### 7.0 Cross-cutting

| macOS | Purpose | Windows |
|---|---|---|
| Borderless non-activating `NSPanel` (all Spaces, full-screen auxiliary, out of window cycling, accessibility subrole "unknown") | Every overlay | `WS_POPUP` + `WS_EX_TOOLWINDOW` + `WS_EX_TOPMOST` (+ `WS_EX_NOACTIVATE` when focus must not move; `MA_NOACTIVATE` on `WM_MOUSEACTIVATE`); `SW_SHOWNOACTIVATE`. Virtual desktops: there is no public "show on all desktops", so re-show on the current desktop when summoned. Topmost windows don't cover exclusive full-screen games. |
| `NSVisualEffectView` HUD and tooltip materials, Liquid Glass | Translucency | `DWMWA_SYSTEMBACKDROP_TYPE` (Acrylic / Transient) for rectangular windows; Composition `HostBackdropBrush` clipped to a shape for discs and pills; solid fallback. Honor the transparency setting (`UISettings.AdvancedEffectsEnabled`). |
| Reduce Motion / Reduce Transparency / Increase Contrast | Accessibility | `SPI_GETCLIENTAREAANIMATION` / `UISettings.AnimationsEnabled`; transparency effects setting; `SPI_GETHIGHCONTRAST` |
| Carbon `RegisterEventHotKey` | Global hotkeys | `RegisterHotKey` + `MOD_NOREPEAT`; handle `ERROR_HOTKEY_ALREADY_REGISTERED`; unregister while a shortcut recorder listens |
| CGEventTap (HID or session level, head or tail, active) | Input filters | One `WH_KEYBOARD_LL` + one `WH_MOUSE_LL` on a dedicated thread with an ordered pipeline (§3.7.0) |
| `tapDisabledByTimeout` + re-enable | Filter health | No notification. A slow hook is silently removed after `LowLevelHooksTimeout` (≤ 1 s). Watchdog via Raw Input `RIDEV_INPUTSINK`; reinstall and reset state. |
| `eventSourceUserData` tags, source PID | Own-event detection | `dwExtraInfo` tag on `SendInput`; `LLKHF_INJECTED` / `LLMHF_INJECTED`. The injecting process is unknown, so drop source-PID logic. |
| `AXIsProcessTrusted` / Accessibility prompt | Permission gate | Not needed for hooks or `SendInput`. Instead handle UIPI: no hooks or injection into elevated windows unless the app runs elevated or has signed `uiAccess` under Program Files. The secure desktop is never reachable. |
| `SessionActivity` (console flag, resign/active) | Hand input back on user switch | `WTSRegisterSessionNotification` (`WTS_CONSOLE_DISCONNECT/CONNECT`, `WTS_SESSION_LOCK/UNLOCK`) |
| Sleep/wake notifications | Reset state | `WM_POWERBROADCAST` (`PBT_APMSUSPEND`, `PBT_APMRESUMEAUTOMATIC`); `RegisterPowerSettingNotification(GUID_CONSOLE_DISPLAY_STATE)` |
| `NSScreen.visibleFrame`, mouse location | Placement | `GetCursorPos`, `MonitorFromPoint(MONITOR_DEFAULTTONEAREST)`, `GetMonitorInfo().rcWork`; Per-Monitor-V2 DPI; `WM_DPICHANGED`, `WM_DISPLAYCHANGE` |
| `NSWorkspace.frontmostApplication`, activation notifications | Frontmost app | `GetForegroundWindow` + `GetWindowThreadProcessId`; `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)` |
| Bundle identifier as app identity | Exceptions and scopes | Normalized full image path (`QueryFullProcessImageNameW`, `GetFinalPathNameByHandleW`, case-insensitive); packaged apps by AUMID; optional exe-name matching |
| `NSEvent` local/global monitors (outside clicks) | Dismissal | `WH_MOUSE_LL` installed only while a surface is open, or hit tests on `EVENT_SYSTEM_FOREGROUND` |
| `NSWorkspace.open`, `openApplication`, `activateFileViewerSelecting` | Open, launch, reveal | `ShellExecuteExW`; `IApplicationActivationManager` / `shell:AppsFolder\<AUMID>`; `SHOpenFolderAndSelectItems` |
| `NSWorkspace.icon(forFile:)` | Icons | `IShellItemImageFactory::GetImage`, `SHGetFileInfoW` |
| `NSOpenPanel` / `NSSavePanel` | Dialogs | `IFileOpenDialog` (`FOS_ALLOWMULTISELECT`, `FOS_PICKFOLDERS`) / `IFileSaveDialog` (`FOS_OVERWRITEPROMPT`, file types) |
| `NSPasteboard` + source marker | Copy | `CF_UNICODETEXT` + a registered custom clipboard format holding the app id |
| `UserDefaults` | Settings | JSON file in `%APPDATA%\Vorssaint\` or `HKCU\Software\Vorssaint`; keep raw key names |
| Application Support, POSIX modes 0700/0600 | Private files | `%LOCALAPPDATA%\Vorssaint\` (default user-only ACL); atomic `ReplaceFileW` / `MoveFileExW` |
| SF Symbols | Icons everywhere | Not licensable on Windows. Map stored symbol names to Segoe Fluent Icons / Fluent UI System Icons with a mapping table. |
| `NSSound.beep` | Feedback | `MessageBeep(MB_OK)` |
| `NSHapticFeedbackManager` | Haptics | No public general API; no-op |

### 7.1 Shelf

| macOS | Purpose | Windows |
|---|---|---|
| Global monitor (left down/drag/up) | Gestures, shake, dwell | `WH_MOUSE_LL` (respect `SM_SWAPBUTTON`) |
| Drag pasteboard change count + types | Is content being dragged? | **No equivalent.** Use temporary OLE drop-target "sensor" windows plus `IDropTarget::DragEnter` confirmation (§3.1.5) |
| `CGEventSource.buttonState` | Watchdog | `GetAsyncKeyState(VK_LBUTTON)` (swap-aware) |
| Window under the pointer, owner PID | Source app for exclusions | `WindowFromPoint` → `GetAncestor(GA_ROOT)` → PID → image path |
| `NSDraggingDestination` | Drop targets | `RegisterDragDrop` + `IDropTarget` (STA, `OleInitialize`); `IDropTargetHelper` + `CFSTR_DROPDESCRIPTION` |
| File URLs, `NSFilenamesPboardType` | Files in | `CF_HDROP` (`DragQueryFileW`); `CFSTR_SHELLIDLIST` for non-filesystem shell items |
| `NSFilePromiseReceiver` | Virtual files | `CFSTR_FILEDESCRIPTORW` + `CFSTR_FILECONTENTS` (`IStream` / `HGLOBAL` / `IStorage` per index), `IDataObjectAsyncCapability` |
| URL / plain-text / GIF / PNG / TIFF flavors | Links, text, images | `CFSTR_INETURLW` ("UniformResourceLocatorW"), `CF_UNICODETEXT`, registered "PNG" / "GIF", `CF_DIBV5`; encode PNG with WIC |
| `NSDraggingSource` / `beginDraggingSession` | Drag out | `SHDoDragDrop` / `DoDragDrop` with `SHCreateDataObject` (PIDLs); `IDragSourceHelper` for a stacked image with a count; `DROPEFFECT_COPY` (\| `MOVE`) |
| Drag-ended operation | Accepted? | `DRAGDROP_S_DROP` and `CFSTR_PERFORMEDDROPEFFECT` (Explorer's optimized move reports NONE) |
| URL bookmarks (no UI, no mount) | Heal moved files | NTFS file ID + volume (`GetFileInformationByHandleEx(FileIdInfo)`, `OpenFileById`, `GetFinalPathNameByHandleW`) or a persisted `IShellLink` + `Resolve(SLR_NO_UI)` |
| `/Volumes/<name>` unmounted check | Keep items on absent drives | Root (drive letter or UNC) not accessible → keep; root present but file missing → gone |
| ImageIO / AVAssetImageGenerator thumbnails | Thumbnails | `IShellItemImageFactory(SIIGBF_THUMBNAILONLY)`, or WIC |
| UTType conformance, localized kind | Classification, tooltips | `AssocGetPerceivedType`, `SHGetFileInfoW(SHGFI_TYPENAME)` |
| `NSSharingServicePicker` | Share/AirDrop | `IDataTransferManagerInterop::ShowShareUIForWindow` + `DataPackage.SetStorageItems` |
| `urlsForApplications(toOpen:)` | Open With | `SHAssocEnumHandlers` per extension, intersected across files; `IAssocHandler::Invoke` |
| AppleScript Finder selection | Shortcut variant | `IShellWindows` → foreground Explorer → `IFolderView2::GetSelection` (`SWC_DESKTOP` for the desktop) |
| `NSStatusItem` frame | Dock anchor | `Shell_NotifyIconGetRect`; taskbar edge from `SHAppBarMessage(ABM_GETTASKBARPOS)` |
| Custom tooltip panel | Tooltips | Tooltip control (`TTS_ALWAYSTIP`), initial delay 1000 ms, max width 280 DIP |
| `window.performDrag` | Move the card by its background | `WM_NCHITTEST` → `HTCAPTION` for handle regions |

### 7.2 Radial menu

| macOS | Purpose | Windows |
|---|---|---|
| Carbon hotkeys (press only) | Per-profile triggers | `RegisterHotKey` + `MOD_NOREPEAT` |
| Session monitors (keys, flags, moves, clicks) | In-session input | Session-scoped `WH_KEYBOARD_LL` (swallow Esc/Enter/arrows/1–9; watch modifier key-ups, confirm with `GetAsyncKeyState` right after install) and `WH_MOUSE_LL` (observe, don't swallow) |
| Non-activating panel that takes key focus | Keys without stealing focus | No equivalent. Keep `WS_EX_NOACTIVATE` and use the hook. Capture the previous foreground HWND for Window Layout and shortcut slices. Swallowed keys while Alt is held can trigger the target's menu bar, so use a mask key. |
| Active tap on other-button down/up | Mouse trigger | `WH_MOUSE_LL` swallowing `WM_XBUTTONDOWN/UP` (XBUTTON1 = 3, XBUTTON2 = 4); buttons 5+ need Raw Input/HID or vendor software |
| `CGEvent` keyboard posting with flags | Shortcut slices | `SendInput` (modifier downs, key down, 40 ms, key up, modifier ups; `KEYEVENTF_EXTENDEDKEY` for navigation keys); translate macOS key codes to VK |
| System-defined media key events | Media slices | `SendInput` `VK_MEDIA_*`, or SMTC `TryTogglePlayPauseAsync` / `TrySkipNextAsync` / `TrySkipPreviousAsync` |
| MediaRemote via perl helper | Now Playing | `GlobalSystemMediaTransportControlsSessionManager` (in-process, 2 s timeout) |
| `NSRunningApplication` activation hand-off | Open the player | Resolve the AUMID to an HWND (`EnumWindows`, `GetApplicationUserModelId`); `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` (allowed after the user's click); else activate by AUMID |
| MultitouchSupport (private) | Four-finger tap | No public API. Precision Touchpad HID (usage page 0x0D) is complex; recommend binding Windows' own "four-finger tap → custom shortcut" to the wheel hotkey. |
| URLSession / ImageIO | Favicon | `HttpClient` (manual same-origin redirects, 5 s, 2 MiB cap) + WIC (frame size check, cubic scaler, PNG encoder) |
| SwiftUI springs | Motion | DirectComposition / Windows.UI.Composition `SpringScalarNaturalMotionAnimation` (DampingRatio, Period) or explicit stiffness/damping (§6.2) |

### 7.3 Scratchpad

| macOS | Purpose | Windows |
|---|---|---|
| `NSTextView` + `NSTextFinder` + `NSUndoManager` | Editor, find, undo | WinUI `RichEditBox` in plain-text mode (undo grouping) / WPF `TextBox` (`BeginChange`/`EndChange`) / AvalonEdit. Build a find/replace bar for WinUI. Normalize line endings to `\n` in the model (WinUI stores `\r`, WPF `\r\n`) and translate offsets (UTF-16 offsets match .NET strings). |
| `AttributedString(markdown:)` | Preview | Markdig (CommonMark + `UseEmphasisExtras` for strikethrough) or cmark-gfm; render to RichTextBlock or FlowDocument per §3.3.8 |
| Key panel without activation | Typing | Must become the foreground window; `SetForegroundWindow` right after the hotkey or tray click; restore the previous foreground on hide |
| Accessibility-keyboard check | Don't dismiss on its key clicks | Skip outside-click dismissal for osk.exe / touch-keyboard windows |
| Atomic write + read-back | Crash safety | Temp file, `FlushFileBuffers`, `ReplaceFileW`, read back and compare |

### 7.4 Cleaning Mode

| macOS | Purpose | Windows |
|---|---|---|
| HID-level filtering tap | Swallow input | `WH_KEYBOARD_LL` + `WH_MOUSE_LL` (swallow keys and wheel; pass moves and clicks). **Not** `BlockInput`. |
| System-defined events (media, brightness, power) | Swallow system keys | `VK_VOLUME_*`, `VK_MEDIA_*`, `VK_BROWSER_*`, `VK_LAUNCH_*`, `VK_SLEEP` in the hook. Firmware brightness and Fn keys usually can't be blocked. |
| Gesture events | Swallow touchpad gestures | Not reliably available; the black overlay absorbs touch |
| Shielding-level overlay per screen | Black screen | Topmost popup per monitor sized to `rcMonitor`; re-take foreground on `EVENT_SYSTEM_FOREGROUND` (UIPI) |
| Cannot block | — | Ctrl+Alt+Del (SAS), Win+L, power button, secure desktop. Tell the user in the UI. |

### 7.5 Camera preview

| macOS | Purpose | Windows |
|---|---|---|
| `AVCaptureSession` / device input / preview layer | Capture and preview | `MediaCapture` + `MediaFrameReader`, or `MediaPlayerElement` via `MediaSource.CreateFromMediaFrameSource` (WinUI 3 has no CaptureElement); or Media Foundation `IMFSourceReader` + D3D11/D2D. Choose ≈ 640×480. |
| Discovery session | Camera list | `DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)` / `MediaFrameSourceGroup` / `MFEnumDeviceSources` |
| Connect/disconnect notifications | Hot-plug | `DeviceWatcher(VideoCapture)`, only while visible |
| Authorization status/request | Permission | Privacy settings (no prompt for unpackaged apps). Detect `E_ACCESSDENIED` / `UnauthorizedAccessException`; `ms-settings:privacy-webcam` |
| Runtime error / interruption | Error state | `MediaCapture.Failed`, `CameraStreamStateChanged` (BlockedForPrivacy, Shutdown); `MF_E_VIDEO_RECORDING_DEVICE_LOCKED` / `_PREEMPTED` |
| Preview mirroring | Flip | Horizontal scale −1 on the visual; UniformToFill |
| System preferred camera | Remember the choice | App setting `cameraPreviewDeviceId` |

### 7.6 Media tools

| macOS | Purpose | Windows |
|---|---|---|
| `/usr/bin/avconvert` presets | Resolution-mode video | **Bundled FFmpeg** child process: `-ss/-t`, `scale=…:force_original_aspect_ratio=decrease`, `libx264` or `h264_mf`, AAC, `-movflags +faststart`, `-map_metadata -1`, 2-pass for "Low", real progress via `-progress pipe:1`. Alternatives: `Windows.Media.Transcoding.MediaTranscoder` with `MediaEncodingProfile.CreateMp4(HD720p/HD1080p…)`, or the MF Transcode API. |
| AVAssetReader/Writer (H.264 High, AAC, fast start) | File-size mode | `IMFSourceReader` + `IMFSinkWriter` (`eAVEncH264VProfile_High`, `CODECAPI_AVEncCommonMeanBitRate`, GOP = 4 × fps, `MF_MPEG4SINK_MOOV_BEFORE_MDAT`), or FFmpeg ABR/2-pass. **The Microsoft AAC MFT only allows 96/128/160/192 kbps**, so the 64 kbps tier becomes 96 kbps (the planner and its tests change) or use another encoder. |
| AVURLAsset metadata | Duration, size, rotation, fps | MF `MF_PD_DURATION`, `MF_MT_FRAME_SIZE`, `MF_MT_FRAME_RATE`, `MF_MT_VIDEO_ROTATION`; or ffprobe |
| AVAssetImageGenerator | GIF frames | FFmpeg sequential decode with an `fps` filter (far faster than a seek per frame); or MF Source Reader seeks |
| ImageIO GIF encoder | GIF | FFmpeg `palettegen` (stats_mode=diff) + `paletteuse`, `-loop 0` (forever) / `-loop -1` (once); or WIC GIF encoder (`IWICPalette`, `/grctlext/Delay`, `/appext` NETSCAPE2.0); or gifski (AGPL-3.0, compatible with GPL-3.0) |
| ImageIO decode/encode + metadata | Image tool | WIC decoders + `IWICBitmapFlipRotator` (apply `System.Photo.Orientation`) + `IWICBitmapScaler` (HighQualityCubic); JPEG/PNG encoders; HEIC encode only when the HEIF + HEVC extensions exist (probe, otherwise hide HEIC); WebP decode only; metadata via `IWICMetadataQueryWriter` or exiv2 |
| Core Graphics PDF | PDF output | Hand-written one-page PDF with a DCTDecode image (JPEG passthrough), or PDFsharp / libharu |
| AppKit drawing (text, shadow) | Canvas, watermark | Direct2D on a WIC bitmap + DirectWrite (Segoe UI 600) + the D2D Shadow effect (std dev ≈ blur / 2); or SkiaSharp |
| Vision `VNRecognizeTextRequest` | OCR | `Windows.Media.Ocr.OcrEngine` (`TryCreateFromLanguage`, `AvailableRecognizerLanguages`, `RecognizeAsync(SoftwareBitmap)`; language packs are optional features; downscale to `OcrEngine.MaxImageDimension`; one language per engine). Alternatives: Tesseract, PaddleOCR/ONNX. No Accurate/Fast distinction. |
| `rename` / `renamex_np(RENAME_EXCL)` | Atomic commit, no-replace | `MoveFileExW(MOVEFILE_REPLACE_EXISTING \| MOVEFILE_WRITE_THROUGH)` / `MoveFileExW` without replace (`ERROR_ALREADY_EXISTS`); retry on sharing violations (antivirus) |
| `stat` dev+inode | Same-file guard | `GetFileInformationByHandle` (volume serial + file index) / `FILE_ID_INFO` |
| `UF_HIDDEN`, mtime | Visibility, keep date | Clear `FILE_ATTRIBUTE_HIDDEN`; `SetFileTime` |
| `ditto` | ZIP | `System.IO.Compression` / libzip |
| Free-space query | "Edit" import guard | `GetDiskFreeSpaceExW` |
| ByteCountFormatter (decimal) | Sizes | Format 1000-based units yourself (`StrFormatByteSize` is 1024-based) |

### 7.7 Input fixes

| macOS | Purpose | Windows |
|---|---|---|
| Mouse button events 0–31 | Click filter, shortcuts | `WM_LBUTTON*` / `WM_RBUTTON*` / `WM_MBUTTON*` / `WM_XBUTTON*` (HIWORD mouseData = XBUTTON1/2). Buttons 6+ need vendor software or HID. LL hooks never see `*DBLCLK`. |
| Scroll fields (continuous, phase, momentum, line/point/fixed deltas) | Classification, smoothing, inversion | `WM_MOUSEWHEEL` / `WM_MOUSEHWHEEL` (signed delta, `WHEEL_DELTA` = 120, sub-120 high-resolution deltas); no phases, no device. HWHEEL positive = right (opposite of AppKit). |
| Mouse vs trackpad by phase heuristics | Leave touchpads alone | **Hard.** Correlate with Raw Input (`RIM_TYPEMOUSE` wheel with a device handle; Precision Touchpads are HID digitizers, usage page 0x0D), or use the absence of touchpad input. Prototype first. |
| `CGEvent(scrollWheelEvent2Source:.pixel)` | Smooth-scroll frames | `SendInput` `MOUSEEVENTF_WHEEL`/`HWHEEL` with sub-120 deltas, tagged |
| CADisplayLink / Timer | Frame pacing | High-resolution waitable timer or `DwmFlush` at the cursor monitor's refresh rate; QPC elapsed time |
| Symbolic hotkeys 79/81/32/33 | Spaces / Mission Control | Ctrl+Win+Left/Right, Win+Tab (fixed shell shortcuts); no App Exposé |
| `tapPostEvent` replay before the up | Short-click replay | Inject a tagged down+up pair after swallowing both |
| `NSEvent.pressedMouseButtons` | Physical state | `GetAsyncKeyState(VK_XBUTTON1/2)` |
| AX menu traversal (Back/Forward) | Navigation | Not needed: XBUTTONs produce `APPCOMMAND_BROWSER_BACKWARD/FORWARD` |
| IOHID acceleration per mouse | Disable acceleration | `SystemParametersInfo(SPI_GETMOUSE/SPI_SETMOUSE)` acceleration = 0 (global), `SPIF_SENDCHANGE` only; journal and restore |
| (inverter alternative) | Native inversion | `FlipFlopWheel` / `FlipFlopHScroll` in the device's `Device Parameters` (admin, replug) |
| Key events (no repeat flag in hooks) | Debounce, Super key, Quit protection | `KBDLLHOOKSTRUCT` (vkCode, scanCode, flags); infer repeats from the held-key set |
| `CGEvent` flag stamping | Super key modifiers | Inject real modifier downs/ups (`SendInput`); mask key 0xE8 before releasing Win/Alt alone |
| `hidutil` UserKeyMapping (Caps → F18) | Holdable Caps Lock | Not needed (the hook sees `VK_CAPITAL` downs and ups); optional `Scancode Map` mode (HKLM, admin, reboot) |
| IOKit modifier lock state | Caps Lock state/LED | `SendInput` `VK_CAPITAL` (tagged); read `GetKeyState(VK_CAPITAL) & 1` |
| Text Input Sources | Next input source | `GetKeyboardLayoutList` + `PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, 0, hkl)`; TSF `ITfInputProcessorProfileMgr::ActivateProfile` for IMEs (asynchronous) |
| `CGEventSource.keyState` | Physical state for the watchdog | Raw Input make/break (LL hooks can't block Raw Input) |
| `NSRunningApplication.terminate()` | Polite quit | No app-level quit: inject the confirmed keystroke, or `PostMessage(hwnd, WM_SYSCOMMAND, SC_CLOSE, 0)` |
| Layout Command-table character | Q/W matching | Virtual-key codes already follow the layout; match vkCode |
| Running-applications list | Super key exceptions | Toolhelp snapshot every 1–2 s, or WMI `__InstanceCreationEvent` / `__InstanceDeletionEvent` |
| `CGWindowListCopyWindowInfo` | Pointer app | `WindowFromPoint` + root + PID (skip own and `WS_EX_TRANSPARENT`); ApplicationFrameHost → CoreWindow for UWP |
| Mach timestamps | Timing | QPC at hook entry |

### 7.8 AI agent usage

| macOS | Purpose | Windows |
|---|---|---|
| FSEvents (file-level, 1 s latency) | Live changes | `ReadDirectoryChangesW` / `FileSystemWatcher` with subdirectories; rescan on overflow. Keep the 2 s / 30 s polling, and read true sizes through an open handle. |
| `stat` (inode, size, mtime), `O_NONBLOCK`, `realpath` | Identity, fingerprints | `GetFileInformationByHandle(Ex)` (file id + volume serial); open with `FILE_SHARE_READ \| WRITE \| DELETE`; `GetFinalPathNameByHandle`; regular files only (skip reparse points and devices) |
| `kill(pid, 0)` | Claude process liveness | `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `GetExitCodeProcess == STILL_ACTIVE`; access denied means alive; guard against PID reuse with the start time |
| `NWPathMonitor` + uptime | Offline detection | `NetworkInformation.NetworkStatusChanged` / `INetworkListManager`; `QueryUnbiasedInterruptTime` (excludes sleep) |
| System SQLite (JSON1) | OpenCode | Bundled SQLite with JSON1; read-only open of a WAL database (the `-shm` and `-wal` files must be accessible) |
| URLSession (ephemeral, redirect delegate, cap) | Price list | `HttpClient` without auto-redirect (manual same-host https), 256 KiB cap, no cookies |
| `Process` + pipes, SIGTERM/SIGKILL | `codex app-server` | `CreateProcess` with redirected pipes, `CREATE_NO_WINDOW`, Job Object; resolve `.cmd` shims or call `codex.exe` |
| Login-shell PATH dump | Find codex | `HKCU\Environment` + `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment` PATH; `where codex` |
| `NSWorkspace` app lookup by bundle id | Agent marks, Claude/Codex apps | Registry App Paths / uninstall keys / AppX package lookup; icons extracted from the executables, or bundled glyphs |

### 7.9 Wallpaper

| macOS | Purpose | Windows |
|---|---|---|
| `NSWorkspace.setDesktopImageURL(options: fill)` | Apply | `IDesktopWallpaper::SetWallpaper(monitorId, path)` + `SetPosition(DWPOS_FILL)`; fallback `SystemParametersInfo(SPI_SETDESKWALLPAPER)` |
| WallpaperAgent `Index.plist` patch + `killall` | All Spaces | Drop. Per-virtual-desktop needs the undocumented `IVirtualDesktopManagerInternal`; verify `IDesktopWallpaper` behavior on Windows 11 desktops. |
| `/System/Library/Desktop Pictures` | System stills | `C:\Windows\Web\Wallpaper\<theme>\*` |
| iCloud placeholder download | Cloud files | OneDrive Files On-Demand: check `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` / `OFFLINE`; opening hydrates the file |
| Security-scoped bookmarks | Sources | Plain paths |
| `x-apple.systempreferences:` | Settings link | `ms-settings:personalization-background` |

### 7.10 Dynamic Island

| macOS | Purpose | Windows |
|---|---|---|
| `NSPanel` at statusBar+1, overlay Space, `canJoinAllSpaces` | Window | `WS_POPUP` + `WS_EX_TOOLWINDOW` + `WS_EX_NOACTIVATE` + `WS_EX_TOPMOST`; re-assert topmost on foreground change. Above the Start menu/taskbar needs `uiAccess`. |
| Alpha hit-testing, `ignoresMouseEvents` | Click-through | Layered window (`UpdateLayeredWindow`; alpha-0 pixels pass clicks), or DirectComposition (`WS_EX_NOREDIRECTIONBITMAP`) + `SetWindowRgn`, or toggle `WS_EX_TRANSPARENT` by cursor position |
| CAShapeLayer mask + keyframed springs | Morphing silhouette | `CompositionPathGeometry` clip + spring or keyframe animations; Direct2D fill |
| `safeAreaInsets`, `auxiliaryTopLeft/RightArea` | Notch geometry | None; always simulated |
| `CGSCopyManagedDisplaySpaces` | Full-screen detection | Appbar `ABN_FULLSCREENAPP` + foreground rect == `rcMonitor` (excluding Progman/WorkerW) + `SHQueryUserNotificationState` |
| AX menu-bar measurement | Avoid covering menus | No global menu bar. Hide while the window under the pill is maximized; optionally UI Automation |
| `NSTrackingArea` + monitors | Hover | `TrackMouseEvent(TME_LEAVE)` on the visible island; Raw Input or `GetCursorPos` polling (20–60 Hz) only while waiting; `WH_MOUSE_LL` only while open |
| Media-key CGEventTap | Volume HUD replacement | `WH_KEYBOARD_LL` swallowing `VK_VOLUME_*` + `IAudioEndpointVolume` |
| `sharingType = .none` | Hide from captures | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` |
| Lock-screen private Space | Lock-screen island | Not possible; drop |
| Session notifications | Teardown | `WTSRegisterSessionNotification`, `RegisterPowerSettingNotification`, `SPI_GETSCREENSAVERRUNNING` |
| Window moves (no conflict on macOS) | Snap Layouts conflict | `EVENT_SYSTEM_MOVESIZESTART/END`: collapse and become input-transparent, or cloak with `DWMWA_CLOAK` |

---

## 8. Porting notes

### 8.1 Drop list (macOS-only or native on Windows)

| Item | Why |
|---|---|
| Side-button Back/Forward ("Mouse navigation") | XBUTTON1/2 already map to `APPCOMMAND_BROWSER_BACKWARD/FORWARD`; users can bind Alt+Left/Right via button shortcuts if an app ignores app commands |
| Linear scrolling (or reduce it to a `SPI_SETWHEELSCROLLLINES` wrapper) | Windows wheels are already unaccelerated at N lines per notch |
| Touchpad scroll-direction handling | Windows has its own Precision Touchpad direction setting; the inverter must stay mouse-only |
| Super key remap machinery: `hidutil`, F18 intermediate, mapping guard, "Modifier Keys" caveat, foreign-mapping checks | The LL hook sees Caps Lock and right modifiers directly |
| Accessibility permission UI (prompts, badges, "Grant access") | Hooks need no permission; replace with UIPI/elevation notes |
| Finder Automation consent for the Shelf | Explorer selection via COM needs no consent |
| Dock-specific drag baselines and the macOS 27 window-server workaround | Replace with move/size WinEvents |
| MediaRemote perl/dylib helper; Apple Events playback fallback; Up Next queue; AirPlay routing | Use SMTC; there is no Windows queue API |
| Wallpaper store patch + WallpaperAgent restart + backup | No equivalent; per-desktop wallpapers are undocumented |
| Island: overlay Space, Mission Control concealment and frame probe, AX menu-bar measurement, lock-screen island, keyboard-light pop-up, haptics, Liquid Glass | No equivalents |
| Legacy-format migrations (scratchpad `.txt`/preference blob, radial legacy keys, keyboard-debounce 30/10 → 5) | Needed only when importing old macOS backups |
| Media: avconvert-specific code paths, HEVC presets, the unused settings `mediaVideoFPS`/`KeepAudio`/`Codec`/`mediaGIFQuality`/`mediaTextLanguageCorrection` | Keep the keys only if old backups must import cleanly |

### 8.2 Risks, ranked

| # | Risk | Affects | Mitigation |
|---|---|---|---|
| 1 | **No system-wide drag notification or global drag clipboard.** The Shelf's shake, docked pill, edge peek and the island drop target all depend on knowing that *content* is being dragged *before* anything is shown. | Shelf, island Files | WH_MOUSE_LL potential-drag detection, move/size WinEvent suppression, thin OLE "sensor" drop targets that confirm via `DragEnter`, speculative show plus immediate hide when nothing confirms. Prototype early and accept changed semantics. |
| 2 | **Mouse wheel vs Precision Touchpad can't be told apart in WH_MOUSE_LL**, and LL hooks are silently removed when slow. Raw-Input consumers (games, 3D tools) see both original and injected events. | Smooth scroll, inverter, horizontal modifier, click filter | Raw Input correlation prototype; a hook watchdog with reinstall; keep per-app exception lists prominent; optional native `FlipFlopWheel` mode for inversion. |
| 3 | **Super key needs real modifier injection** (Windows messages carry no flags). Ctrl+Alt+Shift+Win is the shell **Office key**, Ctrl+Alt is AltGr, Windows auto-repeats the held source key, and elevated windows can strand injected modifiers. | Super key, radial hold, shortcut recorder | Default to Ctrl+Alt+Shift; mask key 0xE8 before releasing Win/Alt alone; ignore source repeats; watchdog using Raw Input; a guard process that releases modifiers; document UIPI. |
| 4 | **Agent log formats are undocumented and drift**, and the Windows layout is partly unknown. Does the Claude desktop write `plan-usage-history.json` on Windows? Does `~/.claude/sessions` exist, and what is `pidDomain`? Codex ships as an npm `.cmd` shim; the app-server reset methods are private. | AI agent usage | Tolerant readers (unknown → skip), version-gated files, env-var overrides (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `XDG_DATA_HOME`), the local 5-hour estimate as the default Claude source, feature-detect `rateLimitResetCredits`. |
| 5 | **Top-center real estate.** No notch or menu bar: the island covers title bars, tabs and search boxes, and collides with the Windows 11 Snap Layouts bar. Notifications need package identity; SMTC coverage varies by app; system flyouts can't be suppressed. | Dynamic Island | Hidden-until-hover with a dwell, click-through until intent, yield during move/size loops, hide in full screen by default, ship the v1 module subset (§3.10.7). |
| 6 | **Cleaning Mode can't be a full lock.** Ctrl+Alt+Del, Win+L, firmware brightness/Fn keys, the power button and input to elevated windows escape the hooks; BlockInput is unusable. | Cleaning Mode | Hooks plus a foreground-reasserting overlay; state the limits in the UI; keep fail-open safety. |
| 7 | **Video presets and codecs.** avconvert presets have no Windows equivalent (bitrates unpublished). HEVC iPhone clips may not decode without the paid extension. The Microsoft AAC encoder only allows 96/128/160/192 kbps. FFmpeg licensing and size (GPL x264 vs LGPL builds, patents). | Media tools | Bundle FFmpeg for decoding; calibrate a CRF/bitrate ladder against macOS output; raise the 64 kbps audio tier to 96 or use another AAC encoder; legal review. |
| 8 | **Focus model.** macOS panels type without activating the app; Windows windows must activate, or keys must come through hooks. Alt-swallowing triggers menu bars. | Radial menu, Scratchpad, camera, island | Radial: session keyboard hook + `WS_EX_NOACTIVATE` + captured foreground HWND. Scratchpad, camera: activate with `SetForegroundWindow` after the hotkey; mask keys when Alt is involved. |

### 8.3 Quirks in the Swift code: decide deliberately

**Shelf**
1. Merging onto a single tile **reuses that tile's id for the new pile**. Pile and first child then select and pin together, and the child's ✕ or drag-out removes the whole pile. Recommend minting a new pile id.
2. Partial or unreadable store protection is incomplete: a partial restore re-persists only the readable subset, and later mutations overwrite an unreadable blob. Preserve unknown entries verbatim, or don't write back.
3. No flush on quit. Flush synchronously.
4. Hover accepts drops when the shelf is full (non-promise); the drop then fails silently.
5. Dead code: `toggleDocked()` and the string "Drop here".

**Scratchpad**
- Quitting while only the island page was used can skip the final save. Flush unconditionally on exit.
- Preview links open any URL scheme. Consider limiting to http(s)/mailto.

**Media**
- Watermark margin 0 becomes 32.
- Island "Run again" fails because the output exists.
- A zero-length trim reports "Format not supported".
- An unreadable logo fails the whole run with "No logo".
- Batch output goes to the *first* input's folder.
- `{date}`/`{time}` tokens are UTC and frozen when the name is computed.
- Switching tools keeps incompatible inputs.
- A new input cancels a running job.
- The preview thumbnail is re-decoded on every redraw.
- One malformed profile hides all profiles.
- Width/Height/Custom modes upscale while "Max side" doesn't.
- **OCR ignores EXIF orientation** (likely bug).
- Source ICC profiles are dropped (everything becomes 8-bit device RGB).

**Input fixes**
- The side-wheel mapping sees the *inverted* sign when horizontal inversion is on. Read the raw direction instead.
- The Spaces-drag button loses click-and-hold semantics, because the press is delayed until release.
- The click filter doesn't repair mid-press contact noise.
- Quit protection: a lost swallowed key-up eats one later press. Confirming ⌘Q terminates apps that ignore ⌘Q (e.g. Finder) when they are in scope. The string "Release to confirm" is unused.
- Key debounce: a suppressed key-down still lets its key-up through (apps see down, up, up).

**Radial**
- A failed hotkey registration shows the generic "Use at least Control, Option or Command with a key." Say "taken" instead.
- The canvas context-menu "Edit" item reuses the page title.

**AI agents**
- `notchAgentsEnabled` defaults to true, but docs/PRIVACY.md says it is "off until you turn it on". Choose one.
- The Codex legacy-totals fallback can double-count when `token_count` precedes the first `token_usage_record`.
- The burn rate is computed but never shown.

### 8.4 Suggested Windows default shortcuts

| Feature | macOS default | Windows suggestion | Notes |
|---|---|---|---|
| Shelf | ⌃⌥⌘D | Ctrl+Alt+Shift+D | Win+Ctrl+D creates a virtual desktop |
| Radial menu (first profile) | ⌃⌥⌘Space | Ctrl+Alt+Shift+Space | Avoid Win+Space (input switcher) |
| Scratchpad | ⌃⌥⌘N | Ctrl+Alt+Shift+N | — |
| Camera preview | ⌃⌥⌘W | Ctrl+Alt+Shift+W | — |
| Island (new on Windows) | none | e.g. Ctrl+Alt+Shift+I | Windows has no notch to click |
| In-app: Scratchpad and island pages | ⌘T/⌘W/⌘F/⌘G, ⌥↑/⌥↓, ⌘K, ⌥⌘+letter, ⌃Tab | Ctrl+T/Ctrl+W/Ctrl+F/F3, Alt+↑/↓, Ctrl+K, Ctrl+Alt+letter (check AltGr), Ctrl+Tab | — |

General rules:
- Avoid **bare Ctrl+Alt+letter** for global hotkeys: it is AltGr on many European layouts (Ctrl+Alt+E = €, Ctrl+Alt+Q = @ on German).
- Avoid **Ctrl+Alt+Shift+Win+letter**: the Office key (W/X/P/O/T/N/D/L/Y open Microsoft 365 apps).

### 8.5 Settings backups and cross-platform import

- **Keep raw keys.** Raw keys can stay identical, but several value formats are macOS-specific:
  - Shortcut strings carry **macOS virtual key codes**. Map them to Windows VKs on import (letters, digits, F-keys, arrows, Space 49, Return 36, Tab 48, Delete 51, Escape 53…; see §6.7 for scan codes). Map modifiers control → Ctrl, option → Alt, shift → Shift, command → Win (or Ctrl, decided per feature).
  - Per-key debounce overrides use macOS key codes. Convert them to scan codes with the §6.7 table.
  - **App identities** (bundle ids) in exception lists, shelf exclusions, quit-protection scopes and Super key exceptions won't match anything on Windows. Drop or flag them on import.
- **Epochs.** Scratchpad `modifiedAt` and the agent archive use the 2001-01-01 epoch. Convert, or keep consistently.
- **Excluded from backups**, keep it that way:
  - `shelfItems`;
  - Scratchpad notes;
  - `mediaImageWatermarkLogoPath` (profiles are exported with logo paths cleared);
  - Super key machine-state markers;
  - per-event calendar countdown picks.

### 8.6 Decisions the spec owner must make

1. Shelf: accept "speculative show" semantics for automatic opens (§3.1.5), or limit the Windows v1 to hotkey + tray pill + edge strips.
2. Shelf drag-out of mixed selections (files + notes/links): materialize notes as `.txt` and links as `.url` temp files in `CF_HDROP`, or drop the non-file items.
3. Super key: default modifiers (recommend Ctrl+Alt+Shift), eager vs lazy injection, an optional `Scancode Map` mode, and whether to ship `uiAccess`.
4. Quit protection: which shortcuts fill the two slots, and whether confirmation re-injects the keystroke or posts `SC_CLOSE`.
5. Smooth scrolling: add an explicit Windows acceleration curve, or keep constant distance per notch.
6. Media: FFmpeg vs Media Foundation, the video quality ladder, the AAC bitrate floor, HEIC availability.
7. Agent usage: where it is displayed without the island (tray flyout page?), `notchAgentsEnabled` default, which Windows paths to scan (WSL?).
8. Dynamic Island: whether to build it at all in v1. If yes, default to hidden-until-hover plus a hotkey on the primary monitor.
9. App identity format for all exception lists: full exe path, exe name, or AUMID.
10. Wallpaper: whether "all virtual desktops" is required.

### 8.7 Test invariants a port must keep (summary)

The Swift test suites pin the following; port them as unit tests first.

- **Shelf**
  - Content-drag predicate and exclusions.
  - Close/remove-after-drag truth table; copy vs move rule.
  - Promise ordering, containment and cancellation.
  - Persistence round-trip and the four load outcomes; capacity `canAdd(199,1)` true, `(200,1)` and `(199,2)` false.
  - Edge 200/330/0.15 s; dock 0.15 s/16/32.
  - Placement frames; grid frames.
  - Tooltip texts and the 500 cap; pile breakdown plurals.
- **Radial**
  - Slice math and the dead zone.
  - Starter wheel, presets 6/4/6/5/5/0.
  - Cleanup and link-normalization examples.
  - `startsHeld` / release-action tables.
  - Button trigger round-trip 3–31 (`button:2` and `button:32` → off).
  - Claim finality.
  - Delete-profile contract.
  - Now Playing parsing (last non-blank line, error key).
- **Scratchpad**
  - Every mark example: toggling, joins, italic star runs, links with nested parens and escapes, line-mark cycling and replacement.
  - Store safety: unreadable bytes untouched, legacy deleted only after a verified save, write-first tab operations.
  - Retention boundaries.
  - The layout-aware shortcut table.
  - The editor survives preview; Clear undo.
- **Cleaning Mode**
  - Counter: 5 presses, other keys and modifiers reset, repeats ignored, 6 s window.
  - Release gate rules; swallow everything except mouse buttons.
  - Teardown before other filters on uninstall.
- **Camera preview**: shortcut defaults; camera is the only permission and is requested on demand; cancel token idempotent.
- **Media**
  - Resize and even-size arithmetic.
  - Safety limits: 9000×100 allowed, 9000×9000 rejected, 20001×1 rejected.
  - Rename tokens.
  - Video plan: 20 MB/60 s/1080p projects to 18–20 MB; 1 MB/120 s fails; 2 MB/60 s with no audio → 480×270.
  - Retry ≈ 0.7833 and ≤ 0.9.
  - GIF limits: 25 s at 12 fps allowed, 25.01 s rejected; GIF retries 15 → 9 fps.
  - Naming with " 2" suffixes and 255-byte caps; same-file guard; atomic commit with and without replace.
  - The OCR language map.
- **Input**
  - Click filter: all state-machine cases, boundary = W accepted, no timers.
  - Key debounce examples (§6.7).
  - Side-wheel burst gate; Spaces tracker thresholds.
  - Smooth-scroll engine: first frame ≈ 18 px for 100 px, 60/120 Hz equivalence, coast rules.
  - Linear-scroll carry; horizontal-modifier exactness.
  - Exceptions: unknown → excluded, never blocking.
  - Super key: state machine (500 ms boundary), solo-effect table, input-source cycling.
  - Quit protection: clamps, inclusive double-press edge, scope truth table, layout matching, HUD texts and geometry.
- **AI agents**
  - Pricing anchors (§6.8); price-list validation; repricing.
  - Per-agent parsing and dedup.
  - Turn rules: late 5 min, idle 10 min, offline 20 s, process registry.
  - Codex windows and plans.
  - Archive: round-trip, every single-bit flip rejected, resume equals a fresh read.
  - Summary cache equals a full recompute across clock and time-zone changes.
- **Wallpaper**: gallery lifecycle tokens, page math, target-screen choice.

### 8.8 Implementation order

1. **Phase 0: foundations**, shared by everything in this spec.
   - Settings store with raw keys and backup (de)serialization.
   - Shortcut model (VK-based) + `RegisterHotKey` + recorder.
   - Private file store.
   - Overlay window base class (tool, no-activate, topmost, DPI, acrylic or solid).
   - HUD.
   - **Input core:** one LL hook thread with an ordered pipeline, tagging, QPC timing, a watchdog and reinstall, session and power handling, a lock-free settings snapshot, and an app-identity cache with the exceptions picker.
2. **Phase 1: low-risk, high-value.**
   - Scratchpad.
   - Camera preview.
   - Click filter and key debounce: pure state machines that validate the input core.
   - Quit/close protection: two slots plus the HUD.
   - Cleaning Mode: hooks plus overlays, with the limitation notice.
   - Media tools: Image and OCR first (WIC + Windows.Media.Ocr), then video file-size mode, then the calibrated resolution ladder, then GIF.
3. **Phase 2: interaction features.**
   - Radial menu: data model, geometry, session hooks, actions, Now Playing via SMTC, settings canvas.
   - Mouse button shortcuts and the desktop-drag gesture.
   - Shelf core: card, OLE drop and drag-out, persistence, tray-anchored pill, hotkey, Explorer selection, virtual files, share.
4. **Phase 3: high-risk features.**
   - Raw Input touchpad-discrimination prototype → scroll inverter → horizontal modifier → smooth scrolling.
   - Super key: injection strategy, masking, watchdog and guard.
   - Shelf automatic drag detection: sensors, shake, dock proximity, edge peek.
   - AI agent usage: pricing and parsers with test vectors → cursor reader → turn lifecycle and notices → archive → Codex resets → Claude registry/offline handling, once the Windows layout is verified. Give it a host surface; the tray flyout works if the island is postponed.
5. **Phase 4: optional.**
   - Dynamic Island container and v1 modules (§3.10.7), then v1.1 and v2 modules.
   - Wallpaper picker (opt-in).
   - Mouse-acceleration toggle (`SPI_SETMOUSE`).

