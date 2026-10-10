# Spec 06 — Clipboard history, Paste as plain text, Clean URL, Text snippets, Command Bar, Quick panel, Quick toggles

Windows port, functional specification (stack-neutral). Source of truth: `vorssaint-utils` @ `b6d8d2db` (main). Everything below was derived from the Swift sources and tests; no Swift knowledge is needed to implement it.

**Conventions**

- macOS modifier glyphs: ⌘ Command, ⌥ Option, ⌃ Control, ⇧ Shift. Section 7.4 proposes the Windows key for every binding.
- "Clipboard" = the OS clipboard (macOS "pasteboard"). "Change counter" = macOS `changeCount`, Windows `GetClipboardSequenceNumber()`.
- "The lane" = one serialized background worker through which *every* clipboard read/write of the whole app goes (section 3.1.1).
- "Front app" = the app that owns the foreground window before one of our panels appeared.
- Durations are seconds unless marked ms. Sizes are points on macOS; treat them as DIPs on Windows.
- Quoted strings in "double quotes" are the exact English UI text (enUS). Other languages exist (15 total) and are out of scope for this document.
- `Pn` priority tags in the inventory: **P0** core of the feature, **P1** expected parity, **P2** polish, **DROP** macOS-only / not portable.

**Contents** — 1 Overview · 2 Feature inventory · 3 Detailed behavior (3.1 shared infrastructure, 3.2 clipboard history, 3.3 auto clear, 3.4 paste as plain text, 3.5 Clean URL, 3.6 text snippets, 3.7 snippet library, 3.8 Command Bar, 3.9 Quick panel, 3.10 Quick toggles) · 4 Settings table · 5 Data & files · 6 Algorithms & data (6.11 = URL rules verbatim) · 7 macOS → Windows mapping (7.3 settings panes → `ms-settings:`, 7.4 shortcuts, 7.5 quick toggles) · 8 Porting notes (drop list, deviations, risks, order, golden tests).

---

## 1. Overview

### 1.1 What this area is

Nine user-facing capabilities that all revolve around "text and actions at the caret":

| # | Capability | One-line purpose | Default state |
|---|---|---|---|
| 1 | **Clipboard history** | Local history of copied text, images and files; searchable quick panel with pins, preview, multi-select paste | Off (opt-in) |
| 2 | **Clipboard auto clear** | Empties the OS clipboard after N seconds of stillness, on sleep, display sleep, or screen lock | All triggers off |
| 3 | **Paste as plain text** | Global shortcut that pastes the clipboard without formatting and then restores the original rich content | Off |
| 4 | **Clean URL** | Removes tracking parameters from links, automatically on copy and manually; user-editable rule tables | Automatic off |
| 5 | **Text snippets** | Typed triggers (e.g. `;email`) expand into text with date/time/clipboard variables | Off |
| 6 | **Snippet library** ("Quick snippet menu") | Hotkey opens a searchable list of snippets grouped by folder; Enter types the snippet at the caret | Off |
| 7 | **Command Bar** | A launcher field (⌥Space) that finds and runs app actions, apps, windows, menu commands, Settings pages, OS settings panes, snippets, clipboard items, emoji, folders, files, saved links/searches/scripts; answers sums, unit/color conversions, date questions | Feature present, hotkey **off** by default |
| 8 | **Quick panel** (code name QuickLauncher) | ⌃⌘V floating grid of favourite tools; editable (hide, reorder, inline options); can host small utilities inside | Hotkey **on** by default |
| 9 | **Quick toggles** | One-click system actions: dark mode, keyboard backlight, mic mute, empty Trash, eject disks, hidden files, desktop icons, lock, display off, screen saver | Feature present; no background work |

### 1.2 Non-negotiable product principles (keep on Windows)

1. **Local only.** No network calls anywhere in this area (except opening a URL the user asked to open). The Command Bar answers dates/conversions from the local calendar/locale, never from the internet.
2. **Nothing typed into the Command Bar reaches disk.** Only per-command run counts (`commandBarUsage`) are persisted. What was typed (query memory, query→result habits, last query) lives in process memory and dies with the app.
3. **Secrets.** Clipboard content explicitly marked secret by its writer is never even read. Password-looking text is skipped by default. Keystrokes typed into password fields are never buffered by the snippet engine.
4. **The UI thread never waits on the clipboard.** The clipboard owner can render data lazily and hang; all access goes through one serial worker with timeouts (issues #189, #887 in the original).
5. **Do no harm to the user's clipboard.** Any transient rewrite (plain-text paste, multi-line snippet paste) restores the previous contents unless the user copied something new meanwhile; a write that would lose data (unpreservable formats) fails open (does nothing).
6. **Confirm destructive actions** in the surface where they were started: Clear unpinned, Empty Trash, Restart/Shut down/Log out, Quit app, Force quit, Kill process(es), Uninstall.
7. **Feature hub.** Every feature here is individually installable/uninstallable (`AppFeature.isAvailable`). An unavailable feature has no hotkey, hook, timer, observer or cache alive; its rows disappear from the Command Bar, Quick panel and menu panel.
8. **Nothing polls while closed** (Command Bar, Quick panel, Quick toggles, Snippet library). The only always-on pollers in this area are the clipboard-history capture (0.8 s), the URL-cleaner watcher (0.8 s) and the auto-clear delay timer (1 s), each only while its feature is enabled.

### 1.3 Architecture (as implemented on macOS)

```
                ┌─────────────── Global hotkeys (one registry, id-routed) ───────────────┐
                │ clipboard history · paste plain · snippet library · quick panel ·       │
                │ command bar · per-row Command Bar shortcuts (ids 200+)                  │
                └──────────────────────────────────────────────────────────────────────────┘
 Clipboard lane (serial worker) ◄── ClipboardHistoryService (poll 0.8 s) ── image store / JSON file
        ▲   ▲   ▲   ▲                  ▲  ignored-apps tracker (foreground history)
        │   │   │   └ URLCleanerService (poll 0.8 s, rewrites links)
        │   │   └──── ClipboardAutoClearService (1 s delay timer + sleep/display/lock events)
        │   └──────── TransientPaste (write → paste → restore after 0.5 s)
        └──────────── PastePlainService, snippet {{clipboard}}, Command Bar answers/paste
 SyntheticInput (backspaces + Unicode keystrokes / paste)  ◄── TextSnippetService (keyboard hook thread)
                                                           ◄── SnippetLibraryService, Command Bar emoji/case rows
 Floating panels: clipboard quick panel (bottom shelf) · snippet library · command bar · quick panel
 Menu-bar panel tabs: Clipboard · Clean URL · Quick toggles   Settings pages: Clipboard · Clean URL · Snippets · Command Bar · Quick tools
```

### 1.4 Dependencies on other specs (referenced, not specified here)

Screenshot editor (edit a history image), Screen OCR (copy text out of a history image; OCR tile), Window switcher/Window layout (Command Bar "Open windows" rows and layout action rows), Kill Process, Uninstaller (+ Homebrew uninstall), Keep Awake, Mic Mute, Brightness/keyboard backlight, Mixer/Sound output switcher, Shelf, Scratchpad, Camera preview, Color picker, Cleaning mode, Media tools, Cleaner, App updates, Feedback window, Settings window/router, Dynamic Island ("notch", macOS-only — dropped), Settings backup/export.

---

## 2. Feature inventory (checklist)

### 2.1 Clipboard history
- [ ] **P0** Capture text, images (opt-out), file lists (opt-out) from the OS clipboard while enabled; 0.8 s polling on macOS → event-driven on Windows (§3.2.2).
- [ ] **P0** Dedup by content (text equality after trimming; image SHA-256; ordered file-path list).
- [ ] **P0** Pinned group + recent group; recent capped by limit (20/50/100/250/500/1000/10000/unlimited); pinned unlimited.
- [ ] **P0** Search (case/diacritic/width-insensitive, all words must match, ranked).
- [ ] **P0** Quick panel window (default ⌃⌥⌘V): horizontal strip of cards along the bottom of the screen, search field, keyboard navigation, ⌘1–⌘9 paste, click = paste into previous app.
- [ ] **P0** Paste into previous app (write → re-activate → synthetic paste).
- [ ] **P1** Multi-select ("pile"): ⌘-click, ⇧-click range, ⌘A; paste/copy/delete N; batch paste modes (files / rich text with images / joined text).
- [ ] **P1** Preview sidebar (Space / button): full selectable text, JSON pretty-print, image, file details; inline text **edit**; "Copy text" OCR for images.
- [ ] **P1** Move up/down within group, pin/unpin, delete, Clear unpinned (with confirmation that only deletes what it counted).
- [ ] **P1** Source-app attribution (icon + name on each card) and **ignored apps** list.
- [ ] **P0** Privacy: honor "concealed" marker; "Skip text that looks sensitive" heuristic (default on).
- [ ] **P1** Menu-bar (tray) "latest copy" text preview item (opt-in, 5–50 chars).
- [ ] **P1** Menu-panel (tray flyout) Clipboard tab with list, search, copy/pin/move/delete buttons.
- [ ] **P0** Persistence: JSON file + PNG image store, size budgets, crash-safe migration, flush on quit.
- [ ] **P2** Edit image in screenshot editor (depends on Screenshot spec).
- [ ] **DROP** Routing the window into the Dynamic Island (`notchClipboard*`).

### 2.2 Clipboard auto clear
- [ ] **P0** Delay trigger (5–3600 s, default 20) measured in wall-clock time since the last change.
- [ ] **P0** Clear on computer sleep, on display sleep, on screen lock (independent toggles).
- [ ] **P0** Never touches saved history entries; blanks the "latest copy" preview; never loops on its own clear.

### 2.3 Paste as plain text
- [ ] **P0** Global shortcut (default ⇧⌥⌘V); paste text without formatting; restore original clipboard 0.5 s later if unchanged.
- [ ] **P0** Media/file copies are forwarded as a normal paste (never converted).
- [ ] **P1** RTF/HTML-only clipboards are converted to text.
- [ ] **DROP/P2** Press the target app's native "Paste and Match Style" menu item (⌥⇧⌘V) when present (no Windows equivalent; see §7).

### 2.4 Clean URL
- [ ] **P0** Rule engine: 31 global names + `utm_*` prefix + 10 site rule sets (78 names), user additions per site/global, user switch-offs (stored as a diff). Rules verbatim in §6.11.
- [ ] **P0** Byte-exact deletion (never re-encode surviving parameters).
- [ ] **P0** Automatic mode: rewrite the clipboard when a copied link had trackers removed and nothing else would be lost.
- [ ] **P1** Manual cleaner (Settings page, tray tab, Quick-panel hosted view): field, Paste, Copy, status message.
- [ ] **P1** Rules editor (per-site groups, checkboxes, add site/name, site on/off switch, delete user names).
- [ ] **P1** Command Bar "Clean the copied link" action and "Clean the copied link" on selected text.

### 2.5 Text snippets
- [ ] **P0** Snippet model (name, trigger, replacement, expansion mode, enabled, ignore case, folder, show in library).
- [ ] **P0** Keyboard hook: rolling 64-char buffer, trigger match (longest wins), immediate vs after-delimiter (space/Tab/Return), backspace handling, reset rules.
- [ ] **P0** Expansion: delete trigger, type replacement (Unicode keystrokes) or paste (multi-line), re-emit the delimiter.
- [ ] **P0** Variables `{{date}}`, `{{time}}`, `{{datetime}}`, `{{clipboard}}`, `{{date:PATTERN}}`, `{{date-tz(IANA):PATTERN}}` (and time/datetime variants).
- [ ] **P1** Date/time variable builder popover (type, style, custom pattern, timezone search, preview, edit token under caret).
- [ ] **P1** Optional expansion sound (system alert sound picker, default "Tink").
- [ ] **P0** Suspend while Command Bar or Snippet library is visible; never in password fields.

### 2.6 Snippet library
- [ ] **P1** Hotkey (default ⌃⌥⌘L) panel: search, folder sections, arrows, Return, ⌘1–⌘9, Esc, "Manage snippets".
- [ ] **P1** Insert into the app that had focus (wait for modifier release, refuse in password fields / own app).

### 2.7 Command Bar
- [ ] **P0** Panel, global hotkey (default ⌥Space, off by default), field, ranked list, Esc ladder, keyboard map.
- [ ] **P0** Providers: app actions, generated feature on/off toggles, settings pages, apps, open windows, quit-app rows, menu commands of front app, OS settings panes, snippets, clipboard history, emoji (`:` prefix), standard folders, files (opt-in folders), saved links/places/searches/scripts, selection actions, answers (battery/memory/storage/date/time), calculator, unit/color conversion, date math, typed URL, power (sleep/restart/shut down/log out), Wi-Fi, kill-process & uninstall browsers.
- [ ] **P0** Ranking (fuzzy, tiers, boosts, per-kind caps, 12 rows), selection persistence, Tab completion, numeric arguments ("brightness 40"), confirmation mode.
- [ ] **P1** Category chips and drill-in, compact mode, "Try" examples, empty state.
- [ ] **P1** Row actions panel (⌘K): pin, own name (alias), own global shortcut, hide, forget usage, quit/restart/force-quit/uninstall app, show in Finder, kill variants, emoji skin tones.
- [ ] **P1** Learning: persisted usage counts; in-memory query memory and keyed query habits.
- [ ] **P1** Settings page: sources on/off, file folders + ignore patterns, saved links editor, row-shortcut list, names list, pins list, hidden list, "Forget what I use most", App shortcuts center (table).
- [ ] **P2** Drag to reposition + double-click to recenter (offset persisted), ASCII-layout switch while open, emoji skin-tone default.
- [ ] **DROP** Dynamic-Island presentation ("droplet"/"island", mascot companion).

### 2.8 Quick panel (QuickLauncher)
- [ ] **P1** Hotkey (default ⌃⌘V, on by default) floating 3-column grid of tool tiles, keyboard (arrows, Return, 1–9, Esc ladder).
- [ ] **P1** Edit mode: hide/unhide tiles, drag reorder, inline option cards (Keep awake, Mic mute, Color picker, Clipboard).
- [ ] **P1** Hosted utilities inside the panel: Window layout, Homebrew, Media, Clean URL, Uninstaller, Cleaner, Quick toggles.

### 2.9 Quick toggles
- [ ] **P1** Ten actions with per-row state (running / failed 2.4 s / needs permission), order & visibility editable, shown in tray panel tab and inside the Quick panel; mirrored as Command Bar actions.

---

## 3. Detailed behavior per capability

### 3.1 Shared infrastructure (used by several features)

#### 3.1.1 The clipboard lane (`GeneralPasteboardAccess`)

- One **serial** background worker owns every clipboard read and write in the app (history capture, URL cleaner, auto clear, transient paste, plain-text paste, Command Bar answers, manual "Paste" buttons, snippet `{{clipboard}}` in the library). Reason: the clipboard owner may render data lazily and stop answering; a reader then hangs. No UI-thread caller ever waits on the lane.
- API shapes:
  1. `run(work)` — fire and forget.
  2. `run(work) then(result)` — `result` delivered on the UI thread.
  3. `run(timeout, work(isExpired)) then(result?) didFinish(result?)` — a deadline timer on the UI thread delivers `nil` to `then` when `timeout` passes; queued work that only *starts* after the deadline is skipped; `then` is delivered at most once. `didFinish` always runs (UI thread) when the work really ends, even after a timeout — used to release admission slots and to record the change counter of a write that completed late.
- Timeouts used: history capture read 5 s; history write 5 s; auto-clear read slot freed after 5 s.
- Admission rules: at most **one** history capture read in flight (a stuck read blocks further polls, it does not pile up); at most **one** history write in flight (a second "copy" while one is pending fails immediately with a beep).
- Exception (keep or improve): the typed-trigger snippet path reads the clipboard **synchronously** (only when the replacement contains `{{clipboard}}`). On Windows do this read on the lane with a short timeout (≈300 ms) and expand to empty on timeout.

#### 3.1.2 Change counter and acceptance rule

- macOS polls `changeCount`. Windows: listen for `WM_CLIPBOARDUPDATE` and read `GetClipboardSequenceNumber()`.
- Acceptance (`ClipboardHistoryChangeCount.accepted(read, since, last)`): `since` = last known counter when the read was scheduled, `last` = last known counter now (may have been raised by one of our own writes while the read was in flight).
  - `read < since` → accept `read` (the clipboard server restarted and its counter began again).
  - `read > last` → accept `read`.
  - otherwise → nothing new (or a stale read overtaken by our own write).
- Every own write reports its resulting counter so the history can skip it (`ignoreNextChange(upTo:)` = `last = max(last, count)`): history re-copies, transient paste write and restore, auto-clear, plain-text paste.

#### 3.1.3 Synthetic paste variants

| Caller | Before the paste | Modifier wait | Paste |
|---|---|---|---|
| Clipboard quick panel | hide panel → write entry (lane) → on success re-activate remembered target app | none; posts 0.12 s after activation | ⌘V down + up, flags forced to Command only |
| Command Bar clipboard row | hide bar; refuse (beep) if own app is frontmost; write entry; wait 0.15 s | poll every 15 ms, max 100 polls (~1.5 s); still held → **beep, give up** | +60 ms; refuse (beep) if secure input is on; ⌘V |
| Transient paste (plain text, multi-line snippets) | snapshot → write text (lane) | 15 ms × max 100; still held → **post anyway** | +60 ms; `willPost` hook; ⌘V down; +40 ms; up; `didPost` hook |
| Snippet library insert / Command Bar typing (emoji, case change) | hide panel; refuse if own app frontmost; library also waits 0.15 s first | 15 ms × max 100 → beep, give up | +60 ms; refuse if secure input; type text (§3.1.5) |

"Secure input" = macOS Secure Event Input (a password field has focus). Windows replacement: see §7 (UI Automation `IsPassword`, elevated targets).

#### 3.1.4 Transient paste (`TransientPaste`) — write, paste, restore

1. Preconditions: UI thread; not already performing one (no overlap) — otherwise return *false* immediately (callers fall back; see snippets).
2. On the lane: read counter `c0`. Snapshot = the *pending* snapshot of a previous transient paste if the counter still equals that paste's counter (so two quick transient pastes restore the user's original content, not the intermediate text); otherwise copy **every item × every type's raw bytes**. If any advertised type cannot be read → fail (do nothing). If the counter changed during the snapshot → fail.
3. Clear the clipboard; write the text as plain string. If that write fails → write the snapshot back, fail.
4. Read counter `c1`; tell history to ignore up to `c1`; remember `pendingRestore = (snapshot, c1)`.
5. UI thread: modifier wait (§3.1.3), `willPostShortcut()` (snippets post their backspaces here; plain-text paste temporarily unregisters its own hotkey if the user bound it to plain ⌘V), ⌘V down, 40 ms, ⌘V up, `didPostShortcut()`.
6. Restore after **0.5 s**: on the lane, if the counter still equals `c1` → clear and write the snapshot back; tell history to ignore the restore's counter. If the user copied something else meanwhile, leave it.
7. A new transient paste cancels a scheduled restore (and reuses its snapshot as in step 2).
- Variant `pasteCurrentContents` (used for media in plain-text paste): no snapshot, no write — modifier wait + ⌘V only.

#### 3.1.5 Typing text at the caret (`TextSnippetService.postExpansion`)

Single routine used by snippet expansion, the snippet library, Command Bar emoji rows and case-change rows.

- Inputs: `deleteCount`, `text`, optional trailing key (+ modifier flags), `trailingText` (the delimiter character typed), failure key.
- All synthetic events carry a **marker** value (macOS `eventSourceUserData = OwnKeyEvent.textSnippetMarker`) so the app's own keyboard hook ignores them. Windows: `KEYBDINPUT.dwExtraInfo = <magic>`.
- If `text` contains any line-break character (`\n`, `\r`, U+2028, U+0085, …) → **paste path**: payload = `text + trailingText` with every `\r` in the trailing text turned into `\n`; uses Transient paste; the `deleteCount` backspaces are posted in `willPostShortcut` (right before ⌘V); on transient-paste failure the original failure key is re-posted (so the user's keystroke is not lost).
- Else → **typed path**: post `deleteCount` × Backspace (down/up), then the text as Unicode keystroke events in chunks of at most **20 UTF-16 code units**, never splitting a surrogate pair across chunks; then the trailing key (down/up with its original flags). Clipboard untouched.

#### 3.1.6 Floating panels

| Panel | Size | Placement (macOS bottom-left coords; convert) | Dismissed by |
|---|---|---|---|
| Clipboard quick panel | width = visibleWidth − 32; height = min(318, visibleHeight − 16) | screen with the pointer; x = minX+16; bottom 8 above the work-area bottom; recomputed on every open | Esc (if no multi-selection), click outside, another app activated, paste/copy of an entry, hotkey toggle |
| Snippet library | 460 wide; list area ≤ 330 tall; height fits content (re-fit on query change keeping top edge) | centered horizontally; origin.y = minY + (H − h)·0.62 (i.e. 38 % of the free space above it) | Esc, click outside, another app activated, insertion |
| Command Bar | 560 wide; list ≤ 452 tall; re-fits per keystroke keeping top edge and center | top edge at minY + 0.72·H (28 % below the top) + user drag offset; clamped 16 inside the visible frame | Esc ladder, click outside, another app activated, running a row (unless the row keeps the bar open) |
| Quick panel | 420 wide; height fits content (hosted utility area 470 tall) | same 0.62 rule as the library | Esc ladder; click outside / app activation **only** for the bare grid (not in edit mode, no hosted utility) |

Common traits: floating level (always on top), visible on every Space and over full-screen apps, not in window cycling, no Dock/taskbar presence, borderless with rounded HUD backdrop (except the clipboard panel: titled-but-hidden title bar, draggable by that strip). On macOS they are *non-activating key panels*: they receive keyboard input while the target app stays active and keeps its caret/selection. Clicks on the macOS Accessibility Keyboard never count as "outside" (Windows analogue: On-Screen Keyboard / touch keyboard windows). While a text field is composing (IME marked text) Return/arrows/Esc belong to the IME in every panel.

#### 3.1.7 Global hotkeys

| Feature | Default (macOS) | Registered when | Storage key |
|---|---|---|---|
| Clipboard history quick panel | ⌃⌥⌘V | history available ∧ `clipboardHistoryEnabled` ∧ `clipboardHistoryShortcutEnabled` (default true) | `clipboardHistoryShortcut` |
| Paste as plain text | ⇧⌥⌘V | available ∧ `pastePlainEnabled` | `pastePlainShortcut` |
| Quick panel | ⌃⌘V | available ∧ `quickLauncherShortcutEnabled` (default **true**) | `quickLauncherShortcut` |
| Snippet library | ⌃⌥⌘L | snippets available ∧ `snippetLibraryEnabled` | `snippetLibraryShortcut` |
| Command Bar | ⌥Space | available ∧ `commandBarShortcutEnabled` (default **false**) | `commandBarShortcut` |
| Command Bar per-row shortcuts | user-chosen, ≤ 64 | always while the Command Bar feature is available | `commandBarRowShortcuts` |

- One process-wide handler routes presses by id (quick tools use ids 10, 14, 19, 20; rows 200+n).
- Registration refused by the OS → `shortcutRegistrationFailed`; Settings shows **"macOS rejected this shortcut. Choose another one."** (orange) — Windows wording: "Windows rejected this shortcut. Choose another one."
- While any shortcut-recorder field is listening, **all** these hotkeys are unregistered so the user can record the very combination; next sync re-registers.
- Storage format: `"<mods>:<keycode>"`, mods joined with `+` in the order `control`, `option`, `shift`, `command`; keycode = macOS virtual key code (e.g. `control+option+command:9` for ⌃⌥⌘V, `option:49` for ⌥Space). Windows must use its own key codes (§5.3).

#### 3.1.8 HUD toast (`QuickToolHUD`)

Small floating toast: SF Symbol + one-line message, visible **1.5 s**, fades out in 0.22 s; a newer toast replaces the current one. Used for "Text copied", copied answers, cleaned-link reports, feature toggles from the Command Bar, missing folders, etc.

#### 3.1.9 Permissions and failure feedback

macOS needs **Accessibility** for synthetic keystrokes and for reading other apps' UI. Pattern everywhere: the first refused attempt per launch shows the system prompt; later attempts just beep. Rows that need a missing permission show "Needs permission · Return asks" in orange and still run (running triggers the prompt). Windows needs no permission for `SendInput`/hooks/UI Automation, so these troubles mostly disappear; keep the mechanism for the cases in §7 (elevated windows).

---

### 3.2 Clipboard history

#### 3.2.1 Entry model

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | Stable identity (survives re-copy, pin, edit). |
| `text` | string | Content for text entries; empty for image/files entries. |
| `copiedAt` | timestamp | Last time this content was copied or reused. |
| `pinnedAt` | timestamp? | Non-nil ⇔ pinned. |
| `kind` | `text` \| `image` \| `files` | Missing in old data ⇒ `text`. |
| `filePaths` | [string] | Absolute, standardized paths, in copy order (files entries). |
| `imageFile` | string? | `"<UUID>.png"` inside the image store (image entries). |
| `imageHash` | string? | Lower-case hex SHA-256 of the PNG bytes (dedup). |
| `imageWidth`, `imageHeight` | int? | Pixel dimensions. |
| `sourceBundleID` | string? | App the copy came from, when known (Windows: app identity, §5.4). |

Derived display values:
- `preview`: text → first **2,000** characters, `\n` and `\t` → space, trimmed (if trimming empties it, the raw prefix), plus "…" when cut; image → `"W×H"`; files → file names joined by `", "`.
- `cardPreview`: like `preview` but keeps line breaks (tabs → spaces).
- `color`: text entries whose *entire* text parses as a color value (§6.8) show a color swatch.
- `menuBarText(max)`: image → `"Image · W×H"`; else `preview` with every newline kind (CR, LF, U+2028…) folded into spaces; cut to `max` chars + "…".
- `searchableText(imageLabel)`: text → text; image → `"Image png W×H"`; files → file names joined by space, prefixed by `"Image "` if any path has an image extension (png, jpg, jpeg, heic, heif, tiff, tif, gif, webp, bmp, ico, icns, svg, avif).

#### 3.2.2 Capture pipeline

1. **When**: every 0.8 s (tolerance 0.25) while running; also immediately on start, when the quick panel opens, and when it closes. Windows: on `WM_CLIPBOARDUPDATE` (debounce ~50–100 ms: apps write in several steps), plus the same open/close checks.
2. **Start = baseline**: the first read after (re)start reads the current content but **does not** record it; it only looks for a saved entry with identical content and sets *latest copy* (§3.2.12) to it (or nothing).
3. **Read** (on the lane) only if the counter changed:
   1. If the clipboard advertises the *concealed* type (`org.nspasteboard.ConcealedType`) → read nothing at all.
   2. If "Also save copied images and files" is on:
      - File URLs present (1 to **100** files; more than 100 ⇒ not a files entry, fall through) → if it is exactly one file and that file lives in the Screenshot feature's own "copied files" temp folder ⇒ record the clipboard's PNG as an **image** instead (no PNG ⇒ record nothing); otherwise record **files**.
      - Else image: PNG data (≤ **16 MiB**) or else TIFF (≤ **64 MiB** raw) converted to PNG (result ≤ 16 MiB); must decode with width, height > 0.
   3. Else **text** via the preference rule (§3.2.3).
   4. Also read the writer's *source mark* (`org.nspasteboard.source`) and whether the copy came from another device (`com.apple.is-remote-clipboard`).
4. Apply the acceptance rule (§3.1.2). On acceptance: set *latest copy* to none (anything not recorded must not keep advertising the old entry).
5. **Exclusion & attribution** (§3.2.9). Excluded ⇒ stop.
6. **Promote**:
   - Text: trim whitespace/newlines at both ends; drop if empty; drop if > **1,000,000** characters; if "Skip text that looks sensitive" is on and the text looks sensitive (§6.13) ⇒ drop.
   - Dedup: an existing entry of the same kind with the same content (text equal; image hash equal; file-path arrays equal including order) is removed and re-inserted with the **same id and pin**, `copiedAt = now`, and the new source app (unless the copy was made inside the history panel itself, which keeps the old source).
   - New image ⇒ write PNG to the image store (`<UUID>.png`); store failure ⇒ nothing recorded.
   - Insert position: pinned entry ⇒ index 0 (top of the pinned group); otherwise the top of the recent group.
   - Set *latest copy* = this entry; emit a "captured" event; normalize (pinned first), trim to limit (§3.2.6), save.

#### 3.2.3 Text preference rule (`ClipboardHistoryPasteboardText.preferredText`)

Inputs: `plain` = plain-text flavor (trimmed; empty ⇒ none); `webURL` = first http/https URL with a host found in the URL flavors (URL objects, `public.url`, `NSURLPboardType`).
1. If `plain` is itself a single web URL (no whitespace anywhere, http/https, has host) ⇒ return the normalized URL string.
2. If there is no usable `webURL` ⇒ return `plain` (possibly none).
3. If there is no `plain` ⇒ return `webURL`.
4. If `plain` contains no whitespace and (`plain` starts with `//`, or equals `webURL` minus its scheme with or without the leading slashes) ⇒ return `webURL` (restores a dropped scheme).
5. Otherwise return `plain` (e.g. several links separated by newline/tab/space stay exactly as copied).

#### 3.2.4 Order, pinning, moving

- The list is always **pinned group first**, then recent. Pinned order is manual (a newly pinned entry goes to the top of the pinned group); recent order is newest first.
- **Pin**: move to index 0 with `pinnedAt = now`. **Unpin**: clear `pinnedAt`, insert at top of recent. A pin (or an edit of a pinned entry) is **refused** if the pinned entries would no longer fit the 96 MiB encoded file (§3.2.6) — the list reverts.
- **Reuse** (an entry pasted/copied from the history): recent entries move to the top of the recent group in the order they were pasted; **pinned entries keep their place** (so ⌘1–⌘9 positions stay stable); `copiedAt = now` for all of them. One single list update for the whole batch.
- **Move up / down**: swap with the neighbour in the same group; disabled at a group edge and whenever a search query is active.

#### 3.2.5 Delete and Clear unpinned

- Delete removes the entry (and from the multi-selection); image files are swept after the next save.
- **Clear unpinned**: the button captures the IDs of *all current recent (unpinned) entries* (regardless of any search filter); a confirmation asks **"Clear unpinned (N)?"** with message **"Pinned items stay, and so does anything copied after this. This can’t be undone."**, buttons "Cancel" / "Clear unpinned" (destructive). Only the counted IDs are deleted (a copy made while the dialog was open survives). Hiding the quick panel dismisses an open confirmation first.

#### 3.2.6 Limits and retention

- **Recent limit** (`clipboardHistoryLimit`): 20, **50** (default), 100, 250, 500, 1,000, 10,000, or 0 = Unlimited; any other stored value ⇒ 50. Applies to unpinned entries only; pinned entries are never trimmed by count. Changing the limit trims immediately.
- **Text byte budget**: total UTF-8 text of retained entries ≤ **64 MiB**; pinned entries are accounted first, then recent newest-first; an entry that does not fit is *skipped* (older smaller ones may still fit).
- **Encoded file budget**: the JSON file is ≤ **96 MiB**; the encoder writes pinned first, then recent, skipping any entry whose encoding would exceed the budget; if nothing changed meanwhile, the in-memory list is replaced by what was actually written.
- No time-based expiry exists (no "delete after N days").

#### 3.2.7 Search

- All whitespace-separated query words must occur (substring) in the folded searchable text (§6.1, §6.2); ranked by score; ties keep history order. Empty query ⇒ the whole list in history order.
- Image entries are found by the localized word "Image", "png" and their dimensions; file entries by file names (and "Image" if they include an image file).
- Highlighting: every occurrence of every token (case/diacritic/width-insensitive) in the first 500 characters of the displayed text is drawn semibold in the accent color.
- Performance contract: folded text per entry is cached by entry id and invalidated on change; the last (query, list-version) result is memoized.

#### 3.2.8 Quick panel (history window) — UI

- **Toolbar**: search field "Search copied text" (focused on open), "Preview" toggle button (disabled when nothing is selected and the preview is closed), close button.
- **Body**: a horizontal strip of cards. No query ⇒ pinned cards, a vertical divider, recent cards. With query ⇒ ranked matches, no divider. Empty states: "No saved text" (empty history) / "No results". Strip is eager up to 300 cards, lazy beyond. Each open scrolls back to the start.
- **Card** (184 × 210, corner 12):
  - Header (34 tall): leading marker — batch-selected ⇒ filled check; hovered ⇒ empty circle; else source-app icon; single image file ⇒ photo glyph (pin glyph if pinned); other files ⇒ the file's icon; else kind glyph (doc / photo / folder) or a pin if pinned. Then the source app's name. Trailing on hover: "Edit" (image entries, when the Screenshot feature exists), "Copy" (copy only), "…" menu; not hovered and pinned with an icon on the left ⇒ a pin glyph.
  - Body: text ≤ 7 lines with optional color swatch; image thumbnail + "Image · W×H"; single image file ⇒ thumbnail, file name, "Image · W×H"; files ⇒ name (or "N files") + names list ≤ 5 lines; tooltip = full path list.
  - Footer (26 tall): copy time (short time style) and a "⌘N" badge on the first nine results.
  - Selected (keyboard) or batch-selected ⇒ accent border 2.5 and tinted fill.
- **Context menu** (right-click or "…"): "Paste", "Copy", —, "Pin"/"Unpin", "Move up", "Move down", —, "Delete item".
- **Mouse**: click ⇒ paste (if the clicked card is batch-selected, paste the batch); ⌘-click ⇒ toggle in batch; ⇧-click ⇒ add the range from the anchor (highlighted row) to the clicked card. Hover highlights; the preview follows a hovered card only after the pointer rests **120 ms** (not while editing); hover is ignored until the pointer actually moves after keyboard navigation.
- **Footer**: with a batch ⇒ "Paste N" (primary), "Copy N", "Delete N" (destructive), "Clear selection"; without ⇒ "Clear unpinned" (disabled when no recent entries) and the total entry count.
- **Preview sidebar** (280 wide, toggled by the button or Space; open/closed state persisted in `clipboardHistoryQuickPreview`): see §3.2.10.

**Keyboard map (panel has focus)** — keys are matched by typed character for ⌘C/⌘A (layout-independent):

| Key | Condition | Action |
|---|---|---|
| Esc | batch non-empty | clear batch |
| Esc | otherwise | hide panel |
| Space (no modifier) | a selection is visible (after arrow navigation) | toggle preview sidebar; otherwise Space types into search |
| Return / keypad Enter | no modifier | paste batch, else the highlighted card, else the first match |
| ⌘Return | — | toggle highlighted card in batch |
| ⇧Return | — | copy only (batch or highlighted) |
| ⌘C | batch non-empty | copy only (otherwise belongs to the search field) |
| ⌘A | batch non-empty **or** query empty | add every visible card to the batch |
| ⌥P | — | pin/unpin highlighted |
| ⌥⌫ / ⌥⌦ | — | delete batch, else highlighted (highlight stays at the same position) |
| ⌘⌫ / ⌘⌦ | batch non-empty | delete batch |
| ↓, → (no modifier), ⌃N | — | next card (first press only reveals the selection) |
| ↑, ← (no modifier), ⌃P | — | previous card |
| ⌘1 … ⌘9 | — | paste the Nth visible result |
| anything else | — | goes to the search field |

When the preview's multi-line editor has focus, or any field is composing, keys belong to that field.

**Open**: remember the paste target (front app if it is a regular app and not Vorssaint; else none), new presentation id, clear query/batch/selection, run one capture (a copy made just before opening keeps its real source), show, focus search. **Hide**: run one capture marked "panel closing" (copies made inside the panel are attributed to the panel), dismiss any open confirmation, hide, clear batch.

#### 3.2.9 Source attribution and ignored apps (`ClipboardIgnoredApps`)

The clipboard does not say who copied; macOS infers it.
- While history runs, keep `candidates` = every app that became frontmost since the last check, seeded with the current front app at each check.
- On every accepted check (called once per check, on the UI thread):
  - `mark` = the writer's source mark, trimmed; empty or > 255 bytes ⇒ none.
  - `fromHistoryPanel` = no mark ∧ (the panel held the keyboard at the previous check ∨ holds it now).
  - Guessed app = the single candidate if exactly one app held the front and the copy is not from the panel; else none.
  - Named app = the mark (none if the mark is Vorssaint's own id) or else the guess.
  - **Excluded** if any candidate is in the ignored list, or the mark is in the ignored list.
  - Source = none if the copy came from another device; else named app.
  - Then reset `candidates` to the current front app; remember whether the panel held the keys (false if closing).
- Vorssaint's own writes (Command Bar answers, OCR text, URL-cleaner copies, color values) carry Vorssaint's source mark ⇒ recorded with no app.
- Ignored-apps list (`clipboardHistoryIgnoredApps`, bundle ids): Settings row "Apps to skip" with "Add an app…" (app picker), per-row "Remove", caption "Nothing you copy in these apps is saved to the history." Values trimmed and de-duplicated on load.
- **Windows**: `GetClipboardOwner()` (+ `GetWindowThreadProcessId`) names the writer directly and replaces most of this heuristic; keep the foreground-history fallback for owner-less writes, keep the "ignored if any candidate matches" safety, and keep the own-write mark (§7.2).

#### 3.2.10 Preview sidebar and editing

- Header "Preview" + close. Footer: pin glyph (if pinned), copy time, then buttons: "Edit" (text entries), "Copy text" (OCR; image or single image file, when the Screen OCR feature exists; disabled while recognizing), "Copy" (copy only, primary).
- Content: text ⇒ read-only selectable text view (fast for huge text); if the text is a JSON object/array ≤ 256 KiB it is re-laid out (§6.14) and shown monospaced 11.5 pt, computed off the UI thread. Image ⇒ thumbnail + "Image · W×H". Single file ⇒ (image: thumbnail, "Image · W×H · size") or (icon 32 + name + size) then the full path, monospaced, selectable. Several files ⇒ "N files", a list (icon or 20 px thumbnail + name), then all paths monospaced.
- **Edit** (text only): inline multi-line editor; "Cancel" / "Save"; Save enabled only if the draft is storable (≤ 1,000,000 chars, not all whitespace) and different. Saving changes the stored text only (the OS clipboard is not touched; *latest copy* is cleared if it pointed to this entry); refused if the edit makes pinned entries overflow the file budget. Esc cancels. Selecting another entry cancels editing.
- **OCR copy**: recognizes text in the picture at full resolution unless > 24,000,000 px (then scaled to that area), using the Screen OCR feature's languages and its "remove line breaks" option; result copied to the clipboard (and recorded like any copy) with HUD "Text copied" / "No text found"; dropped if the clipboard changed meanwhile.

#### 3.2.11 Paste / copy semantics

- Single entry writes: text ⇒ plain string; image ⇒ PNG + TIFF (TIFF for targets that only take TIFF); files ⇒ file URLs of the paths that still exist. **Stale content aborts the write** (image file missing, no file exists) so the user's clipboard stays intact; then a beep.
- Batch (2+ entries; taken in *history order*, not click order):
  - all files ⇒ one file list (all paths; missing ones dropped);
  - any image ⇒ rich text: in order, text parts + `"\n"`, files as their paths joined by `"\n"` + `"\n"`, images as embedded PNG attachments + `"\n"`; plain-text fallback = the text parts joined by `"\n"`; any missing image aborts;
  - otherwise ⇒ texts joined by `"\n"` (files contribute their paths joined by `"\n"`).
- After a successful write: reuse reordering (§3.2.4); *latest copy* = the entry (single) or none (batch).
- **Paste** flow: target = remembered front app; hide panel; write; on failure beep; success ⇒ no target: silent copy only; target quit ⇒ beep; else activate target, then (if trusted) post ⌘V after 0.12 s.
- **Copy only**: hide; write; beep on failure.

#### 3.2.12 "Latest copy" and the menu-bar preview

- *Latest copy* = the saved entry believed to be on the OS clipboard now. Set by: a recorded capture, a successful single-entry copy from history, the start-up baseline match. Cleared by: any accepted change that was not recorded, auto clear, stopping the history, a batch copy, deleting/trimming that entry, editing its text.
- **Menu-bar preview** (opt-in, `clipboardHistoryMenuBarPreview`, length 5–50 chars, default 20): a separate status item next to the app icon showing `latestCopy.menuBarText(length)`; hidden (not removed) while there is no latest copy; tooltip = first 200 characters; left click toggles the quick panel, right click shows the app menu. Exists only while the feature is enabled and running. Windows: a second notification-area icon cannot show text; use a small always-on-top "chip" near the tray or a tooltip on the main tray icon (§7).

#### 3.2.13 Menu-panel (tray flyout) Clipboard tab

Header "Clipboard" + close. Card: checkbox "Save clipboard history" (toggles capture), caption ("Stores copied text so you can reuse it later. Everything stays local and can be cleared anytime." / when off: "Enable history to start saving copied text."), "History shortcut: ⌃⌥⌘V" (when on), search field "Search copied text" (disabled when empty), trash button (Clear unpinned with confirmation), "open window" button. List (≤ 260 tall, lazy): "Pinned" label; preview (3 lines + swatch / 110×40 thumbnail + "Image · W×H" / file name or "N files"); buttons ↑ ↓ (disabled when searching or at edge), pin/unpin, edit image, "Copy" (copy only; turns into "Copied" ✓ once the write landed; ignores the second click of a double click), delete; copy time. The list scrolls to an entry after copying it (it moved to the top). The tab keeps the flyout open while shown.

#### 3.2.14 Lifecycle

- `sync`: available ∧ enabled ⇒ start (timer, ignored-app tracking, baseline) + hotkey; otherwise stop (timer off, tracking off, *latest copy* none) and unregister the hotkey. The stored history stays readable while capture is off (the Command Bar and the panels still show saved items).
- Persistence & image store: §5.1–5.2. Flush synchronously on quit.

---

### 3.3 Clipboard auto clear

Independent of capture: works with history off (it is gated only by the Clipboard feature being installed). The Settings section is never disabled by the capture toggle. It **never touches saved history entries** — only the OS clipboard. Caption: "Clears the system clipboard only. Items already saved stay in the history."

**Delay trigger** ("Auto clear clipboard with a delay of [N] seconds", default off, N = 20, clamped to 5…3600 — a typed 4 becomes 5, not the default):
1. On enable/launch: `lastChangeDate = now`, read counter → `lastChangeCount` (whatever is already on the clipboard gets a full delay).
2. Every 1 s (tolerance 0.2): read counter on the lane (one read in flight; a read that does not answer within 5 s frees the slot; its late answer is ignored). Decide:
   - counter ≠ `lastChangeCount` ⇒ note change: `lastChangeCount = counter`, `lastChangeDate = now`;
   - counter = `lastClearedChangeCount` ⇒ wait (our own clear never re-triggers);
   - `now − lastChangeDate ≥ delay` ⇒ clear (expecting this counter);
   - else wait.
   Delay is re-read on every tick (no restart needed). Wall-clock time ⇒ a machine that slept past the delay clears on the first tick after wake.

**Event triggers** (each its own toggle and observer, default off): "Clear clipboard on computer sleep" (system will sleep), "Clear clipboard on display sleep" (displays did sleep), "Clear clipboard on screen lock" (screen locked). Each clears immediately with no expected counter.

**Clear operation**: snapshot `alreadyCleared` and a configuration generation; on the lane: abort if the configuration changed since queueing, the feature is unavailable, or that trigger's toggle is now off; read counter; abort if counter = `alreadyCleared` (nothing new since our last clear — locking fires display sleep and lock together); abort if an expected counter was given and differs (a new copy arrived — it deserves its own full delay); clear → new counter; on UI: `lastChangeCount = lastClearedChangeCount = counter`, `lastChangeDate = now`, history ignores this counter and blanks *latest copy*.

---

### 3.4 Paste as plain text

- Toggle "Paste as plain text" + caption "Pastes what you copied without colors, fonts or formatting. The original stays on the clipboard." + shortcut row (default ⇧⌥⌘V) + accessibility permission row.
- Press (no Accessibility ⇒ prompt once per launch, then beep):
  1. On the lane, compute `plain`:
     - If any clipboard type is a file, promised file, image, movie/video, audio, file URL or PDF (`NSFilenamesPboardType`, `NSFileContentsPboardType`, `com.apple.NSFilePromiseItemMetaData`, `Apple files promise pasteboard type`, `com.apple.pasteboard.promised-file-url`, or any type conforming to image/movie/video/audio/file-url/pdf) ⇒ `plain = none` (those copies also advertise a name or URL as text; it is not what the user wants).
     - Else plain string; else text of the RTF flavor; else text of the HTML flavor; else none.
  2. `plain` non-empty and the front app has an **enabled menu item whose key equivalent is exactly ⌥⇧⌘V** (found by walking the app's menu bar to depth 3, ≤ 600 elements, 0.35 s per-element timeout; apps without one are cached per process id, cache cleared past 64 entries) ⇒ press that item (the app's own "Paste and Match Style"). Done.
  3. `plain` non-empty ⇒ Transient paste (§3.1.4) of `plain`.
  4. Otherwise ⇒ forward a normal paste without touching the clipboard (`pasteCurrentContents`).
  - If the user's own shortcut **is plain ⌘V**, the hotkey is unregistered for the moment the synthetic ⌘V is posted and re-registered right after (otherwise it would trigger itself).

---

### 3.5 Clean URL

#### 3.5.1 Engine contract

`clean(text, rules) → {url, removed[]} | none` (algorithm and complete rule data: §6.11). `none` = not a single http(s) link. `removed` lists decoded names in the order the link carried them, each once.

Outcome → message (shared by every surface so they never drift):

| Outcome | Condition | Message |
|---|---|---|
| notAURL | result is none | "Paste a valid URL." |
| unchanged | nothing removed and url == trimmed input | "Nothing to clean." |
| rewritten | nothing removed but url differs (defensive; not produced today) | "URL cleaned." |
| removed(names) | ≥ 1 removed | "Removed %@" with names joined by ", " |
| after Copy | the copied url equals the current result | "Copied." |

#### 3.5.2 Automatic mode (`URLCleanerService`)

Toggle "Clean URLs as you copy them", caption "Removes tracking parameters from a link the moment it reaches the clipboard.", note "Local. No network.", status "Active now" plus (Settings only) the last removal, e.g. "Removed utm_source, fbclid".

Watcher: every 0.8 s (tolerance 0.25) while enabled; baseline counter on start; one poll in flight, cancellable. When the counter changed, in **one lane transaction**:
1. All clipboard types must "survive a rewrite" (§6.11.4) — no files, images, promises, documents, concealed/transient marks.
2. Exactly **one** clipboard item.
3. Text = plain string, else the URL flavor's string.
4. `clean` must remove at least one name (a link with nothing to remove is never rewritten, because rewriting drops the copy's other flavors).
5. If an HTML flavor exists: raw HTML ≤ 64 KiB; drop `<head>`, `<style>`, `<script>`, `<title>` blocks; collect every `href` target (double-quoted, single-quoted or unquoted; `&amp;` → `&`), each normalized as an address (scheme and host lower-cased, empty path → `/`); visible text = remaining markup with tags removed, `&amp;` → `&`, trimmed. Continue only if the lower-cased raw HTML contains none of `<img <video <audio <picture <svg <iframe <object <embed`, every href target equals the link's address, and (at least one href exists, or the visible text is empty, or the visible text equals the link). Otherwise leave the copy alone.
6. Read the source mark; abort if cancelled or the counter moved since step 1.
7. Rewrite: clear; write the cleaned URL as plain string **and** as URL flavor; re-write the original source mark if any; re-add the "came from another device" marker if the original had it. Record the new counter; publish `lastCleaned`/`lastRemoved`.

Manual copies (Copy buttons, Command Bar) cancel a pending poll, write string + URL flavor + Vorssaint's own source mark, and record their counter so the watcher does not process them again.

*Observed interaction:* the history poller and the URL watcher run independently on the same lane, so the history can record the tracked link and then the cleaned one. On Windows, run the cleaner first inside the same clipboard-update handler and let the history see only the final content (recommended improvement).

#### 3.5.3 Manual cleaner (Settings page, tray-panel tab, Quick-panel hosted view)

- "Clean now" section: field "Paste a URL" with an ✕ "Clear field" button; "Paste" (reads the clipboard's plain string through the lane into the field); "Copy" (primary, disabled without a result).
- Result recomputed on every render from the field and the rules in force (a rule edited in Settings updates an open tray tab immediately): cleaned URL in monospace (≤ 3 lines, middle truncation, selectable) + message (§3.5.1); empty field ⇒ "The clean URL appears here".
- The tray tab also carries the automatic toggle, its caption and "Active now"; it keeps the flyout open while shown.

#### 3.5.4 Rules editor (Settings → "Cleaning rules")

- One disclosure row per group: **"All sites"** first, then every site (built-in, user-added, or present only in the switch-off list) alphabetically. Row label: site, enabled count ("1 parameter" / "%d parameters"), and a site switch (− "Turn off every rule for this site" when anything is on; + "Turn on every rule for this site" when all are off).
- Expanded: two-column grid of checkboxes — built-in names (the global group shows **`utm_*`** first, then the non-`utm_` globals sorted) followed by the user's names (sorted, each with a "Delete name" button); then an add row "Parameter name" + "Add" and the caption "Write the name to the left of the = , like utm_source. A name that matches takes that one parameter out of the link and leaves the rest as it was."
- "Add a site" disclosure: fields "example.com" and "Parameter name" + "Add" (both must be valid). A site arrives with its first name.
- Captions: "A site attaches these parameters to its own share links to track where the link came from. Switched on, a name is removed when a link is cleaned; switched off, it stays. Names you add can be deleted." and "The list covers a site’s different share paths (the web page, the app, a live room), which is why it is long; a real link usually carries only two to four of them."
- Edits: unchecking adds `site|name` to the switch-off list, checking removes it; adding a name removes any switch-off for it and adds it (global ⇒ custom list, site ⇒ `site|name`); the site switch sets the switch-off list for that site to *every* listed name (off) or removes the site's switch-off entry entirely (on); deleting a user name removes it from both the additions and the switch-offs.
- Validation: parameter name = trimmed, lower-cased, non-empty, without whitespace, `|`, `,`, `&`, `=`. Site key = trimmed, lower-cased, `scheme://` removed, cut at the first `/ ? #`, leading `www.` removed; must contain a dot, not start/end with a dot, no whitespace, `|` or `,`.

#### 3.5.5 Other entry points

- Command Bar action "Clean the copied link" (`action.cleanURL`, keywords "Clean URL"): reads the clipboard text (lane); HUD "Paste a valid URL." / "Nothing to clean." / "URL cleaned." / "Removed …"; copies the result when changed.
- Command Bar selection row `selection.cleanLink` (only when the selected text is a link that would change): copies the cleaned link, HUD with the removed names.
- Quick-panel tile "Clean URL" hosts the manual cleaner.

---

### 3.6 Text snippets (typed-trigger expansion)

#### 3.6.1 Model and editor

| Field | Default | Rules |
|---|---|---|
| `id` | new UUID | persisted, stable |
| `name` | "" | trimmed of spaces on save; placeholder "Personal email" |
| `trigger` | "" | all whitespace removed, max **40** chars; ≥ **2** chars ("The trigger needs at least 2 characters."); unique ("Another snippet already uses this trigger." — compared case-insensitively if either snippet ignores case); placeholder ";email" |
| `replacement` | "" | must be non-empty; placeholder "myemail@example.com" |
| `expansion` | `afterDelimiter` | `immediate` ("Right away") or `afterDelimiter` ("After space, Tab or Return") |
| `enabled` | true | per-row switch in the list |
| `ignoresCase` | false | "Ignore capitalization" |
| `folder` | "" | trimmed; "" = no folder; suggestions menu lists existing folder names; placeholder "Work" |
| `showsInLibrary` | true | "Show in the quick menu" |

Decoding tolerates missing `ignoresCase`/`folder`/`showsInLibrary` (old data ⇒ false / "" / true).

Editor sheet: Name, Trigger (monospace), Expand picker, Ignore capitalization, Folder (+ suggestions), Show in the quick menu, Text (plain multi-line editor, min height 76) with a button "Insert date/time" (or "Edit date/time" when the caret is inside a date token) opening the builder (§3.6.4); hints "Variables: {{date}}, {{time}}, {{datetime}}, {{clipboard}}" and "A format after a colon picks how they look, like {{date:yyyy-MM-dd}}, or use the date/time button above. A -tz(...) part sets the timezone, like {{date-tz(America/New_York):yyyy-MM-dd}}." Buttons "Delete" (existing snippets), "Cancel", "Save" (disabled while trigger too short, duplicate, or text empty).

Settings page ("Text snippets"): toggle "Expand snippets while typing" + caption "Type a trigger anywhere and it becomes its text. Everything stays on this Mac."; permission row; (when on) toggle "Play a sound when a typed trigger expands" + "A short system sound plays each time a typed trigger expands." + picker "Sound" (§3.6.5); section "Quick snippet menu" (§3.7); secure-input explanation row while some app holds secure input; snippet list (row = name or trigger, trigger chip, folder label, one-line preview with newlines as spaces, mode label, enable switch; empty text "No snippets yet. Add the first one."; "Add snippet"); footer: variables hint, "They become the date, the time and the copied text at the moment of expansion.", and the format caption. Every edit persists immediately and re-syncs the engine and the library.

#### 3.6.2 Expansion engine

Runs only while: snippets feature available ∧ "Expand snippets while typing" on ∧ at least one enabled snippet ∧ (macOS) Accessibility granted ∧ the user's session is active (fast user switching). Otherwise no hook, no observers, no cache. Enabled snippets are pre-split into `immediate` and `afterDelimiter` lists at every sync.

Keyboard hook on a dedicated high-priority thread (never on the UI thread), listening to key-down and left/right mouse-down. Per event, in order:
1. Hook disabled by the OS (timeout/user input) ⇒ re-enable if still allowed, else re-sync.
2. Mouse down ⇒ reset buffer (the caret moved), unless the click lands on the Accessibility Keyboard.
3. Event carries our marker ⇒ pass through.
4. Secure input on (password field) ⇒ reset buffer, pass.
5. Snippet library or Command Bar visible ⇒ reset, pass.
6. ⌘ or ⌃ held ⇒ reset (shortcuts are not text), pass.
7. Backspace ⇒ drop the buffer's last character, pass. ←→↑↓, Esc, Home, End, Page Up, Page Down, Forward Delete ⇒ reset, pass.
8. Typed string = the layout-aware Unicode characters of the key (≤ 4 UTF-16 units); empty ⇒ pass.
9. **Delimiter** (first char is space, Tab, CR or LF): find the best `afterDelimiter` match for the buffer; buffer = "" in all cases. Match ⇒ expand with `deleteCount = trigger.count`, trailing key = this key + its modifier flags, `trailingText` = the typed delimiter; if the expansion was accepted the original event is **swallowed** (the delimiter is re-emitted after the replacement), else passed.
10. **Other character**: buffer = last **64** chars of (buffer + typed); find the best `immediate` match; match ⇒ buffer = "", expand with `deleteCount = trigger.count − typed.count` (the final keystroke is swallowed, so it never reached the app), no trailing key, failure key = this key + flags; swallowed if accepted.
- Another app becoming frontmost resets the buffer (except the Accessibility Keyboard itself).
- **Matching**: among enabled snippets of the mode with a non-empty trigger, those whose trigger completes the buffer — exact suffix, or (ignore case) the last `trigger.count` characters compared case-insensitively; the **longest trigger wins** (`;email2` beats `;email`).
- **Expansion**: optional sound cue (§3.6.5, played asynchronously after the replacement goes out); compute the text (§6.12; the clipboard is read only if the replacement contains `{{clipboard}}`); run `postExpansion` (§3.1.5). Multi-line replacements use the paste path and keep the delimiter in the same paste (`"\r"` delimiter becomes `"\n"`). If the transient paste refuses (busy), the original keystroke passes through so typing is never lost.

#### 3.6.3 Variables

`{{date}}` (medium date style), `{{time}}` (short time style), `{{datetime}}` (`"<date> <time>"`), `{{clipboard}}` (plain clipboard text or empty), and pattern variants `{{date:P}}`, `{{time:P}}`, `{{datetime:P}}`, `{{date-tz(Zone):P}}`, `{{time-tz(Zone):P}}`, `{{datetime-tz(Zone):P}}`. Exact grammar, order and edge cases: §6.12.

#### 3.6.4 Date/time variable builder (popover, 340 wide)

- Opens in **insert** mode (defaults: Type = DateTime, Style = ISO 8601, no timezone) or **edit** mode when the caret is inside an existing recognized date token (Type/timezone from the token; Style = the first of ISO 8601, Short, Medium, Long, Full whose pattern *currently* equals the token's pattern, else Custom with the pattern prefilled).
- Controls: segmented "Type" (Date / Time / DateTime); "Style" (Short, Medium, Long, Full, ISO 8601, Custom); note for named styles: "A named style saves the format your Mac’s region uses right now."; "Timezone": current value or "Device default", clear button "Clear timezone", search field "Search timezones" with live ✓ "Valid timezone" / ✕ "Unrecognized timezone" (shown only when the query resolves to one zone or matches none), scrolling list of matches (row height 22, list ≤ 140 tall); "Pattern" field for Custom; "Preview" of the current date; buttons Cancel and "Insert" / "Update" (disabled for Custom with an empty pattern).
- Pattern resolution: Custom ⇒ the typed pattern; ISO 8601 ⇒ date `yyyy-MM-dd`, time `HH:mm:ssXXX`, datetime `yyyy-MM-dd'T'HH:mm:ssXXX`; Short/Medium/Long/Full ⇒ the locale's pattern for that style *at this moment* (frozen into the token). en_US examples: Short date `M/d/yy`, time `h:mm a`; Medium date `MMM d, y`, time `h:mm:ss a`, datetime `MMM d, y 'at' h:mm:ss a`; Long date `MMMM d, y`, time `h:mm:ss a z`; Full date `EEEE, MMMM d, y`, time `h:mm:ss a zzzz`. (macOS ICU puts U+202F NARROW NO-BREAK SPACE before `a`; patterns stored in tokens can contain it.)
- Token text: `{{<kind>[-tz(<IANA id>)]:<pattern>}}`; inserted at the selection (or replacing the edited token) through the editor so undo works and the caret lands after the token.
- Timezone search: query normalized (lower-case, `_` → space); matches = every known IANA identifier containing it, plus identifiers of matching abbreviations (Foundation's abbreviation table, e.g. `PST`→`America/Los_Angeles`, full table in §6.12.3); sorted by rank (0 exact identifier, 1 city equals query, 2 city starts with query, 3 identifier starts with query, 4 other) then alphabetically; uncapped. A query "resolves" if it equals an identifier, equals an abbreviation, or yields exactly one match; Return/submit adopts the resolved zone. Raw UTC offsets are never accepted.

#### 3.6.5 Expansion sound

- Picker lists the system alert sounds (`/System/Library/Sounds/*.aiff`, names without extension, sorted by localized display name); if the folder is unreadable, the classic list: Basso, Blow, Bottle, Frog, Funk, Glass, Hero, Morse, Ping, Pop, Purr, Sosumi, Submarine, Tink. Default "Tink". A stored name that no longer exists resolves to "Tink", else the first available; the picker shows an extra row "Sound unavailable" for it.
- Played as a system *alert* sound (alert volume, alert output device, honors "flash screen" accessibility). Only typed triggers play it (library insertions do not). Changing the picker plays a preview.

---

### 3.7 Snippet library ("Quick snippet menu")

- Settings: toggle "Open snippets from a menu", caption "The shortcut opens a searchable menu. Picking a snippet types it right where your cursor is.", shortcut row (default ⌃⌥⌘L), failure message.
- Panel (460 wide, HUD backdrop, corner 22): search bar (magnifier, field "Search snippets" focused on every open, ✕ clear), list (≤ 330 tall): folder sections (folder glyph + name header) then loose snippets; each row: name (or trigger) 13 pt, trigger chip (monospace), one-line preview (newlines → spaces), ↩ glyph on the selected row; footer "↩ inserts · esc closes" and "Manage snippets" (opens Settings → Text snippets).
- Content: enabled snippets with "Show in the quick menu" on, filtered by the query (trimmed; matches name, trigger, text or folder, case- and diacritic-insensitive "contains"); sections = folders sorted with localized natural order, then the loose group last; stored order inside each group.
- States: nothing eligible ⇒ "Nothing to show yet. Add snippets, or turn on “Show in the quick menu” for the ones you use most." + "Manage snippets"; nothing matches ⇒ "No snippet matches the search."
- Keys: Esc hide; Return/keypad Enter insert selected; ↑/↓ move (clamped, no wrap); ⌘1…⌘9 insert the Nth row (plain digits type into search); hover selects; click inserts. Query change selects the first row. While visible the typed-trigger engine is suspended.
- **Insert**: hide; if Vorssaint itself is frontmost ⇒ beep, stop; (macOS) no Accessibility ⇒ prompt once then beep; expand variables now (clipboard read only if needed); after 0.15 s wait for modifier release (15 ms × ≤ 100, else beep); +60 ms; refuse (beep) in a password field; `postExpansion(deleteCount: 0)`. No sound.
- Dismissed by Esc, click outside, another app activated, insertion, turning the toggle off.

---

### 3.8 Command Bar

A single field that finds and runs everything. Opened by its global hotkey (default ⌥Space, **disabled by default** — the user enables "Global shortcut to open the bar"), by "Open the bar now" in Settings, or from the menu panel. It never activates Vorssaint on macOS, so actions that type or paste land where the caret already is (§7.1 for Windows).

#### 3.8.1 Lifecycle

**Open**:
1. Show the (pre-built, pre-laid-out) panel invisibly and give it the keyboard *first*, so keys typed right after the hotkey are not lost (they are queued and replayed into the field once it exists).
2. Suspend the snippet engine; start a new *presentation id* (resets script runner, file search, row index, rows, mode = search, saved query, completion state, category, peek, pointer baseline).
3. Reload preference caches (pins, aliases, hidden, disabled sources, usage, row shortcuts, compact mode, file scopes); query = ""; optionally switch to an ASCII keyboard layout (§3.8.13); show at the remembered position.
4. Next UI-loop turn ("home hydration", only if this presentation is still current): rebuild the catalog and the running-app rows, then start background loads, each guarded by the presentation id (a result arriving for an older presentation is discarded or re-requested): Finder-automation status, boot-volume free space, Wi-Fi power, battery + memory, installed apps (every open), OS settings panes (once per launch and language), open windows (≤ once per 4 s), menu commands of the front app (cached per app 8 s), selected text, running processes, app selected in Finder.
- While home is still loading, the list is empty (and a compact bar starts collapsed).

**Close** (`hide`): resume snippets; cancel any deferred row shortcut; reset script runner and file search; end shortcut capture if active; restore the borrowed keyboard layout on the next UI-loop turn; hide; reset an uninstall review that is not actually removing; mode = search; drop selection-, kill- and Finder-selection rows; clear the query (the last query is kept in memory only and is not reused); clear the index.

Dismissed by: Esc from home, the hotkey again, a click outside, another app becoming active, running a row (unless that row keeps the bar open), ⌘, (opens Settings → Command Bar).

#### 3.8.2 Layout

- **Field row**: brand mark (doubles as drag handle, tooltip "Drag to move · Double-click to recenter"); in naming/argument modes a capsule with the target row's title; field (16 pt, autocorrect off) with placeholder "Type what you want to do" (argument mode: "0 to 100"; naming mode: "The name you call it"); ✕ clear button when non-empty; compact-mode hints "↓ Suggestions  Esc".
- Optional orange warning line (uninstall flow).
- **Chips row** (only on the browse list or inside a category): "All", then the available categories (§3.8.5).
- **List**: section headings (uppercase 9 pt) above the rows they name; up to 14 rows sized to content, more ⇒ fixed 452 tall with scroll indicator; "Try" example chips above the home list: `100 km to mi`, `2+2*3`, the localized word for battery (only on machines with an internal battery), `fire` — clicking one fills the field.
- **Row**: icon (glyph on a tinted plate, or app/file icon 28 px, clipboard thumbnail, or color swatch); green "live" dot when active (running app, feature on); title (matched letters in accent bold; answer rows 17 pt rounded semibold); subtitle (or orange "Turn on %@ in Settings" / "Needs permission · Return asks"); right side: "⌘N" badges while ⌘ is held (first 9 rows), else the answer value chip, else the row's shortcut (own row shortcut › built-in feature shortcut › the menu item's shortcut); ↩ glyph on the selected row (space reserved on all rows).
- **Empty state**: faint mark, "Nothing here by that name.", button "See suggestions" (clears query and category; Return does the same).
- **Footer** (hidden in compact home): keyboard glyph + the bar's hotkey; "⌘K Actions" when the selected row has actions; `⌃P ⌃N ↑↓` (+ `←→` when chips are walkable); "⇥ Continue" when the calculator answer is selected; ↩; Esc.

#### 3.8.3 Modes

| Mode | Entered by | Shows | Return | Esc |
|---|---|---|---|---|
| search | default | list | run selected row (none selected + text ⇒ clear text) | ladder (§3.8.4) |
| argument(row) | running a row with a required numeric range and no number typed | card: row + "Return applies · Esc goes back"; field cleared, placeholder "0 to 100" | parse digits (optional `%`, ≤ 4 digits), clamp to range, run; invalid ⇒ beep | back to search with the saved query |
| confirm(row) | running a row with a confirmation prompt | red-tinted card: prompt (e.g. "Empty the Trash?"), "Return confirms · Esc cancels", buttons "Cancel" / "Confirm" | run | back; **typing any other key also cancels** (so a destructive Return is never left armed under a new search) |
| actions(row) | ⌘K | "ACTIONS" list (§3.8.8) | run action | back to search with the saved query |
| naming(row) | action "Give it your own name" | field = current alias | commit (empty clears) | back to actions |
| capturingShortcut(row) | action "Give it a shortcut" | card + current binding + "Press the keys you want · Delete clears it · Esc goes back" | Return is a combination like any other | back to actions |
| uninstallReview / uninstallHomebrewConfirm | uninstall rows | leftover-files checklist (Uninstaller spec) | remove / confirm / done | back (does not stop a removal in progress) |

#### 3.8.4 Keyboard map

| Key | Action |
|---|---|
| Esc | search mode: non-empty query ⇒ clear it; else in a category ⇒ leave it; else peeked list ⇒ collapse; else close. Other modes: step back (table above). |
| Return / keypad Enter | run selected (mode-dependent) |
| ⌘Return | "Show in Finder" for rows that are real files/apps/folders; any other row ⇒ behaves like Return |
| ↑ / ⌃P | previous row (wraps) / previous action |
| ↓ / ⌃N | compact home not yet peeked ⇒ peek (show list); else next row (wraps) / next action |
| ← / → | only when the field is empty in search mode: walk category chips (wraps, "All" first); otherwise caret movement |
| Tab | complete: calculator answer ⇒ replace the field with a reusable number (§6.6.5); other non-answer rows ⇒ replace the field with the row's title (emoji rows: their name, with `:` kept when the query started with `:`); learning still remembers the pre-completion text |
| ⌘1…⌘9 | run the Nth row (plain digits type) |
| ⌘K | open actions for the selected row |
| ⌘P | pin/unpin the selected row (if pinnable) |
| ⌘, | close and open Settings → Command Bar |
| ⌘A ⌘C ⌘X ⌘V | standard editing in the field |
| ⌘Q ⌘W ⌘M ⌘H | swallowed (must not quit/close/hide the app behind the bar) |
| holding ⌘ | shows "⌘1…⌘9" badges |
| any other key | goes to the field (if a confirmation is up it is cancelled first; naming warning cleared) |

IME composition always wins (keys pass to the field). Mouse: click runs that row *by identity* (a background reload between click and run must not run a different row); hovering selects only after the pointer actually moves (a row sliding under a resting pointer does not steal the selection).

#### 3.8.5 Home (empty field), categories, compact mode

- **Home list** (not compact, no category): (1) "SELECTED · <first 44 chars of the selection>" with the selection rows (§3.8.7 P15) if text was selected; (2) "PINNED" — pinned rows that are currently offerable (catalog rows — actions, toggles, settings pages, snippets, links, folders, answers — plus apps, OS settings panes and windows; not hidden, source enabled) in pin order; (3) "SUGGESTIONS" — up to `7 − pins` rows from the same offerable set: most-used first (count desc, then most recent), then fill-ins from the curated list `action.screenshot`, `action.scrollingScreenshot`, `action.keepAwake`, `action.screenOCR`, `action.clipboardWindow`, `action.colorPicker`, `action.darkMode`, `action.snippetLibrary`; (4) every remaining **catalog** row grouped under its area heading (the row's subtitle; answers under "Answer", links under "Your shortcut", snippets under "Insert snippet", folders under "Folder"; empty subtitle ⇒ "Everything it can do"), groups in first-appearance order, **max 12 rows per group**, rows drop the subtitle that repeats the heading; apps, windows and OS panes are not grouped here (search finds them); (5) "PASTE FROM HISTORY" — the last 6 clipboard entries (if the Clipboard source is on).
- **Categories (chips)**, fixed order: Actions, Apps, Clipboard, Windows, Menus ("Menu command"), Settings pages, System Settings panes, Snippets, Emoji, Folders, Your shortcuts. A chip appears when its source is enabled and has content (Apps and OS panes: always; Windows: window features installed and permission; Menus: permission and the front app is not Vorssaint; others: at least one non-hidden row). Kill Process and Uninstall categories are reached only through their browse rows.
- **Inside a category**: heading "<Category> · <count>"; rows = all rows of that kind (clipboard: up to 60 most recent), used rows first (count desc, recency desc), unused keep catalog order; typing filters *only* that category (ranked, max 40, no answers, no per-kind caps; clipboard search uses the history search with limit 40).
- **Compact mode** ("Compact mode" — "Bar opens without suggestions. Results appear as you type."): empty field shows the field alone (no list, chips, divider or footer, and the catalog walk for chips is skipped); ↓ "peeks" the list for this opening; Esc collapses it again.

#### 3.8.6 Typed search pipeline (search mode, non-empty trimmed query, no category)

1. **Emoji scope**: query starting with `:` ⇒ search only emoji (the rest after `:`, trimmed; empty ⇒ first 40 emoji in catalog order); ranked; max 40. (Emoji never appear in normal search: > 1,000 rows.)
2. **Answer** (only if the "Sums and conversions" source is on): first of calculator (§6.6) → unit conversion (§6.7) → color conversion "`<color> to <format>`" (§6.8) → a lone color value (preview) → date question (§6.9). Shown as the first row, title = result, subtitle "Return copies" (calculator with auto-closed brackets: "<expression with closers> · Return copies"). Return copies the formatted result (HUD shows it).
3. **Typed URL**: a web address typed alone ⇒ row "action.openURL" (title = text, subtitle "Open in browser"), placed right after the answer.
4. **Script answer**: if the query names a saved script (§3.8.10) and its cached output exists ⇒ answer row (title = output, "Return copies"); otherwise schedule the script (debounced) and keep searching. Every *other* script row the query names is removed from the pool (only the longest matching name is eligible, so Return cannot run a different file than the one answering); once the answer exists, the winner's own row is removed too (the answer stands in for it).
5. **Files**: if the Files source is on and folders are configured ⇒ cached results for this exact query, else schedule a search (debounced 0.12 s; the list refreshes when results land).
6. **Numeric argument split**: if the last whitespace token is digits (optional `%`) and at least 2 tokens exist, and some numeric command (brightness, volume) matches the remaining text ⇒ rank with the text only (`brilho 40` ⇒ "brilho"); otherwise the full query is used (`code 1234` stays a search).
7. **Pool**: selection rows + Finder-selection uninstall row + catalog + apps + OS settings panes + windows; + quit rows **only if** a query token (≥ 2 chars) prefixes or is prefixed by a word of "Quit %@" (any language: the verb is the format minus `%@`; CJK by containment); + menu commands **only if** the effective query has ≥ 2 characters; + up to 4 matching clipboard entries (searched with the full text, digits included); + file rows. Remove hidden rows and rows whose source is switched off. (Kill-process rows never join typed search; uninstall-app rows only in their category.)
8. **Rank** (§6.3): candidates scored against folded title (saved searches/scripts: against the whole query once the query names them) and keywords (+ the user's alias). Then reorder a feature's own rows into its slots (main command, presets, settings page) — §6.3.4.
9. **Assemble**: [answer unless it is a lone color] [typed URL] [script answer] then ranked rows, skipping `answer.*` rows unless the first query token has ≥ 3 characters and the answer's folded title starts with it; per-kind caps: `app.` 5, `window.` 4, `quit.` 3, `menu.` 5, `emoji.` 6, `settings.` 4, `macsettings.` 4, `clipboard.` 4, `snippet.` 4, `file.` 4, `toggle.` 5 (actions uncapped); stop at **12** rows. A lone color preview is inserted at index 0, or 1 if a row's title already contains the typed text (e.g. `#1234` issue numbers), dropping the 13th row.
10. Duplicate ids are removed (first kept). **Selection**: a changed query always selects row 0; a list rebuilt by a background load keeps the selected row by id.

#### 3.8.7 Result providers

ID prefix ⇒ source (used for the sources switches, caps, pins and aliases). Rows without one of these prefixes belong to **Actions** (always on).

| # | Source (switch label) | Row id | Content, title, behavior | Usage counted |
|---|---|---|---|---|
| P1 | Actions ("Vorssaint actions", always on) | `action.*`, `toggle.*`, `uninstall.browse`, `uninstall.finder`, `emoji.browse`, `kill.browse` | Feature actions (table below); one generated **on/off row per installed feature that has exactly one enable switch**: "Turn on %@" / "Turn off %@", subtitle = feature group, keywords = feature name, live dot when on, run = flip the switch, apply, HUD with the feature name (special pairs: `toggle.scrollInverter.vertical` "Invert vertical scrolling", `toggle.scrollInverter.horizontal` "Invert horizontal scrolling", `toggle.mouseButtonShortcuts`, `toggle.mouseButtonShortcuts.spacesGesture`) | yes |
| P2 | Apps | `app.<path>`; stable key `app.bundle.<bundle id>` or `app.<path>` | Every installed app except Vorssaint (scanned on every open, off the UI thread; previous list stays until the new one lands). Title = display name; subtitle "App"; keywords = alternate names + on-disk name (if different) + pinyin of Han titles (joined + initials); live dot if running; Return opens or activates; reveal path = bundle; ⌘K app actions | yes |
| P3 | Menus ("Menu commands of the app in front") | `menu.<index>.<title>` | Enabled leaf menu items of the front app (not Vorssaint, regular app): walk depth ≤ 4, ≤ 400 items, skip top-level menus titled "Apple", "Window", "Help"; dedupe identical path+title; subtitle "App › Menu › Submenu" (a name is never repeated right after itself); keywords "Menu command", app name, path; shows the item's shortcut; Return presses the item | no |
| P4 | Windows ("Open windows") | `window.<id>` | Titled windows of running apps (requires a window feature installed + permission; rows without a title or whose title equals the app name are skipped); subtitle = app name; Return activates that exact window after 0.1 s | no |
| P5 | Quit an app | `quit.<bundle id or pid>` | One per running regular app (first instance per bundle id), title "Quit %@", confirmation "Quit %@?"; run = bring the app forward, then ask it to quit 0.12 s later (apps may still show their own save dialog) | yes (if it has a bundle id) |
| P6 | Uninstall (category only) | `uninstall.<app id>`; stable `uninstall.bundle.<id>` | Apps the uninstaller accepts; Return opens the inline leftover review (Uninstaller spec). Requires "uninstallerCommandBarEnabled" (default off) | yes |
| P7 | Settings pages | `settings.<page>`, `settings.feature.<feature>`, `settings.setting.<anchor>` | Vorssaint's own Settings pages, feature entries and individual settings (same index as the Settings search) for installed, visible pages; pages already reachable by an action (General, Cleaner, Uninstaller, App updates) are not repeated; subtitle "Vorssaint Settings"; Return opens Settings there | yes |
| P8 | System Settings panes | `macsettings.<pane bundle id>` | OS settings panes discovered at runtime (§3.8.12); subtitle "System Settings panes"; keywords = the pane's localized search terms; Return opens the pane | yes |
| P9 | Snippets | `snippet.<uuid>` | Enabled snippets **with a name**; subtitle "Insert snippet"; keywords = trigger + folder; Return types it at the caret (library insertion path) | yes |
| P10 | Clipboard history | `clipboard.<uuid>` | Matching history entries (typed: top 4; home: last 6; category: up to 60 / search 40). Available if capture is on **or** saved items exist. Title = preview (images: "Image png W×H"); subtitle "Paste from history"; icon = thumbnail/file icon. Return: hide, refuse if Vorssaint is frontmost, write the entry, wait 0.15 s, modifier wait, paste (§3.1.3). Not pinnable, nameable or learned | no |
| P11 | Emoji | `emoji.<emoji>` | §6.10; only with `:` prefix or in the Emoji category; title "<glyph>  <unicode name>", rank text = name; Return types the glyph (with the default skin tone) at the caret | yes |
| P12 | Folders | `folder.<path>` | Fixed set: Downloads, Documents, Desktop, Movies, Pictures, Music, home folder, /Applications (display names); subtitle "Folder"; Return opens in the file manager; reveal path | yes |
| P13 | Answers ("Answers about this Mac") | `answer.battery`, `answer.memory`, `answer.storage`, `answer.date`, `answer.time` | Battery: "Battery", subtitle "charging"/"on power"/"Battery", value "NN%"; Memory (if memory monitor installed): "Memory" (or the app-memory metric name), "%@ of %@ in use", value "NN%"; Storage: "Storage", "%@ available of %@", value free %; Date: "Today", subtitle full date, value short date; Time: "Time now", value medium time. Return copies the value (memory copies "used / total", storage copies the free amount). In typed search only when the first token (≥ 3 chars) prefixes the title | no |
| P14 | Sums and conversions | `math.result`, `units.result`, `color.result`, `color.preview`, `date.result` | §3.8.6 step 2 | no |
| P15 | What is selected | `selection.*` | Rows acting on the text selected in the front app when the bar opened (≤ 20,000 chars, trimmed; read via accessibility): "Copy it"; "Use it in the search" (puts the text in the field, bar stays open); "Clean the copied link" (if it is a link that would change); "UPPERCASE" / "lowercase" / "Title Case" (only if ≤ 5,000 chars and the case would change; types the converted text over the selection); "Keep it on the shelf" (if Shelf enabled); "Count it" (subtitle "Words: %d, characters: %d", value = characters, Return copies the subtitle). Also `selection.uninstall` "Uninstall %@…" for a single app selected in the Finder's Applications folder | copy/case/clean/shelf: yes; search/count: no |
| P16 | Your shortcuts (saved links) | `link.<uuid>` | §3.8.10 | yes |
| P17 | Files | `file.<path>` | §3.8.11; title = file name, subtitle = folder (home abbreviated to `~`); Return opens (missing ⇒ HUD with the name); ⌘Return reveals | no |
| P18 | Kill Process (category only) | `kill.<pid>` | Every non-protected running process: title name, subtitle "PID %d", keywords = path, confirmation "Kill %@?"; Return = polite kill; ⌘K: Force Kill / Kill All “name” / Kill Process Tree / Restart. Requires "killProcessCommandBarEnabled" (default on) | no |

**Feature action rows (P1)** — shown only when the owning feature is installed; shortcuts shown only while they actually fire; "Needs permission" when the macOS permission is missing:

| Row id | Title (enUS) | Behavior |
|---|---|---|
| `action.screenshot` | "Screenshot" | capture after 0.15 s |
| `action.scrollingScreenshot` | "Scrolling screenshot" | after 0.15 s |
| `action.screenRecorder` | "Screen recording" / "Stop recording" while recording | toggle after 0.15 s |
| `action.recentCaptures` | "Recent captures" (keywords "Screenshot", "Recording") | open its window after 0.1 s |
| `action.screenOCR` | "Copy text from screen" | after 0.15 s |
| `action.colorPicker` | "Color picker" | after 0.15 s |
| `action.clipboardWindow` | "Clipboard" | open quick panel after 0.1 s; *needs setup* (opens Clipboard settings) when capture is off and nothing is saved |
| `action.clipboardClearRecent` | "Clear unpinned" (keywords "Clear recent", "Recent") | confirm "Clear unpinned (N)?" — N counted when listed; deletes exactly those |
| `action.snippetLibrary` | "Quick snippet menu" | open after 0.15 s |
| `action.scratchpad`, `action.cameraPreview` | "Scratchpad", "Camera preview" | open after 0.15 s |
| `action.shelf` | "Shelf" | summon; needs setup when Shelf is off |
| `action.pastePlain` | "Paste as plain text" | run after 0.15 s |
| `action.cleaningMode` | "Cleaning Mode" | after 0.1 s |
| `action.keepAwake` | "Enable keep awake" / "Disable keep awake" | toggle; live when active |
| `action.keepAwake.15/30/60/120/240/480` | "Keep awake for 15 minutes" … "Keep awake for 8 hours" | activate for that duration |
| `action.micMute` | "Mute microphone" / "Unmute microphone" | toggle |
| `action.brightness` | "Display brightness" (subtitle "0 to 100", keywords Displays page title + "screen") | numeric 0–100 on the display under the pointer; needs setup (Energy page) when brightness control is off |
| `action.volume` | "Volume" ("0 to 100") | numeric; sets system output volume; HUD "Volume N%" |
| `action.soundMute` | "Mute the sound" / "Turn the sound back on" | toggle output mute |
| `action.soundOutput.<uid>` | device name; subtitle "Current output" / "Switch sound output" | make default output |
| `action.darkMode` | "Switch to dark mode" / "Switch to light mode" | Quick toggle |
| `action.lockScreen`, `action.displayOff`, `action.screenSaver` | "Lock the screen", "Turn off the display", "Start the screen saver" | after 0.15 s |
| `action.ejectDisks` | "Eject all disks" | Quick toggle |
| `action.hiddenFiles` | "Show hidden files" / "Hide hidden files" | Quick toggle |
| `action.desktopIcons` | "Hide desktop icons" / "Show desktop icons" | Quick toggle |
| `action.emptyTrash` | "Empty the Trash" | confirm "Empty the Trash?" then empty (no second dialog) |
| `action.cleanURL` | "Clean the copied link" | §3.5.5 |
| `action.layout.<action>` | window layout action titles | Window layout spec (after 0.15 s; beep on failure) |
| `action.appUpdates` | "Check for app updates" | start check + open its Settings page |
| `action.cleaner`, `action.uninstaller` | "Cleaner", "Uninstaller" | open Settings page |
| `uninstall.browse` | "Uninstall Application" | open the Uninstall category (keeps bar open; no usage) |
| `uninstall.finder` | "Uninstall app selected in Finder" | read the Finder selection and start the review (keeps bar open) |
| `action.quickLauncher` | "Quick panel" | open after 0.15 s |
| `emoji.browse` | "Emoji" | open the Emoji category (keeps bar open) |
| `kill.browse` | "Kill Process" ("Browse & Kill") | open the Kill Process category |
| `action.feedback.bug` / `.feature` | "Report a bug" / "Suggest a feature" ("Send feedback") | open feedback window |
| `action.restartApp` | "Restart Vorssaint" | relaunch the app |
| `action.power.sleep` | "Sleep" | sleep now |
| `action.power.restart` / `.shutDown` / `.logOut` | "Restart" / "Shut down" / "Log out" | confirm "Restart the Mac?" / "Shut down the Mac?" / "Log out?" then do it |
| `action.wifi` | "Turn Wi-Fi on" / "Turn Wi-Fi off" (keywords "wifi wi-fi") | only if a Wi-Fi interface exists; set radio power in background |
| `action.openSettings` | "Open Settings" | open Settings (General) |
| `action.openURL` | the typed address | open in the default browser |

#### 3.8.8 Row actions (⌘K)

Available when the selected row is not an answer, is in the index (clipboard and calculator rows are not) and has at least one action. Built fresh on open, in this order:
1. Installed-app rows: if running — "Quit %@", "Restart %@" (quit, wait for termination ≤ 60 s, relaunch), "Force Quit %@…" (destructive; native alert "Force quit %@? Unsaved changes will be lost." with "Force Quit %@" / "Cancel"); if the uninstaller accepts it — "Uninstall %@…" (opens Settings → Uninstaller with that app).
2. "Show in Finder" for rows with a file location (apps, folders, files, saved places).
3. Kill rows (not protected): "Force Kill" (alert "Force Kill %@?" + path), "Kill All “%@”" (alert "Kill all “%@” processes?"), "Kill Process Tree" (alert "Kill %@ and all its child processes?" + path), "Restart" (if restartable).
4. Emoji that accept a skin tone (single-scalar modifier bases, not the legacy family 👪): the five *other* tones as one-off insertions (title = the toned glyph); usage recorded on the base emoji; the default tone is unchanged.
5. Pinnable rows: "Pin to the top" / "Unpin".
6. Nameable rows: "Give it your own name" / "Change the name it answers to"; "Give it a shortcut" / "Change the shortcut"; if bound, "Take the shortcut off".
7. Always: "Never show this" (hide).
8. Rows that count usage: "Forget how often I use this" (removes usage, query memory and habits for that row).

Pinnable sources: actions, apps, windows, settings pages, OS panes, snippets, folders, links, answers, calculator (not menus, quit, uninstall, clipboard, emoji, selection, files, kill). Nameable (alias + row shortcut): actions, apps, quit, settings pages, OS panes, snippets, emoji, folders, answers, calculator, links (not menus, windows, clipboard, selection, files, kill, uninstall).

#### 3.8.9 Personalization

- **Pins** (`commandBarPins`): ordered stable keys, max 30 (oldest dropped); toggling appends/removes; ⌘P or the action; on a typed query a pin is only a tie-breaker (+60); on the empty bar pins lead in pin order. Settings list hides pins of uninstalled hub features but keeps app/folder pins whose target is temporarily absent.
- **Aliases** (`commandBarAliases`): `{stableKey: alias}`, alias trimmed, ≤ 60 chars, several words allowed (each word finds the row). Saving refuses an alias sharing any word with another row's alias ("%@ already answers to that"). Ranking: exact word match ⇒ priority 2400, prefix ⇒ 1100 (§6.3).
- **Hidden rows** (`commandBarHidden`): set of stable keys never listed anywhere (search, home, categories, chip content). A hidden row's own global shortcut still fires, except a direct-run script (suppressed while hidden).
- **Sources** (`commandBarDisabledSources`): any source except Actions can be switched off; a switched-off source is excluded everywhere (and its background scan is skipped, e.g. OS panes).
- **Row shortcuts** (`commandBarRowShortcuts`): `{stableKey: shortcut}`, ≤ **64**, must include a modifier (a bare key would steal that key from every app); assigning a combination already used by another row **moves** it; refused if it conflicts with another Vorssaint feature shortcut or a window-layout shortcut; a combination the OS already owns is refused in the bar but Settings offers to take it over (macOS-specific; drop). Rows the OS refused are listed with "macOS rejected this shortcut…" in Settings. Pressing a row shortcut:
  1. Rebuild that row fresh (never a stale closure). App row not yet scanned this session ⇒ scan, then run once if the binding is unchanged. OS-pane row ⇒ open the bar and run it once panes are loaded.
  2. A saved **script** with "Run from its global shortcut without opening the bar" ⇒ run it silently (no argument; beep on non-zero exit) unless the row is hidden or Links is switched off.
  3. A row that needs a prompt (confirmation, required number, needs setup, uninstall) ⇒ open the bar and run it there.
  4. An app row whose app is frontmost, not hidden, **and owns the front-most window** ⇒ hide that app (second press puts it away); otherwise open/activate it.
  5. Else run it (closing the bar if open). Usage is recorded but query learning is not (nothing was typed).
- **App shortcuts center** (Settings button "App shortcuts", caption "Open apps with their own shortcuts, even when the bar is closed. Set search names and pin favorites here."): 780×560 sheet with search, filter "All" / "Pinned" / "With shortcuts", sortable table — App (icon + name), Alias (text field "The name you call it", saved on submit/blur, ≤ 60 chars, conflict message), Shortcut (recorder "Record…", clear button, warning icon when refused), Pinned (pin toggle). One row per stable key (several copies of an app share one binding). Footer message area + "Done".
- **Position**: drag the mark to move; on drag end the offset from the default spot is saved as `"dx,dy"` (rounded ints; zero ⇒ removed); the bar is always clamped 16 inside the visible frame; double-click the mark (or "Recenter the bar" in Settings) resets with a short slide. Offsets, not absolute positions, so a disconnected monitor never strands the bar.

#### 3.8.10 Saved shortcuts — links, places, searches, scripts (`commandBarLinks`)

Model `{id, name, kind: link|place|script, destination, runsWithoutArgument=false, runsDirectly=false}`; ≤ **60** saved; entries with an empty name or destination are dropped on decode. Placeholders (same spelling in every language): `{query}` (what is typed after the name), `{clipboard}`, `{selection}` (selected text when the bar opened), `{date}` (short date). A destination containing `{query}` is a **search**.

- Row: title = name; subtitle "Your shortcut" (link/place), "Type what to look for after the name" (search), "Type what to send after the name" (script) or "Runs on its own, or type what to send" (script that runs bare); keywords "Your shortcut"; searches and scripts **keep the bar open** and are ranked against the whole query once it names them (`gh vorssaint` still ranks the `gh` row first).
- Name matching: `trailingArgument(query, name)` — folded query must start with folded name + space; the argument is taken from the **original** text after the name's word count (keeps case/accents; any Unicode whitespace separates, incl. U+3000). `ghost` does not match `gh`; `gh` alone has no argument.
- **Link/place run**: argument = trailing text; a search with no argument puts "<name> " in the field and stays open; otherwise hide, read the clipboard only if `{clipboard}` is present (lane), expand placeholders — for links each value is percent-encoded with only `A–Z a–z 0–9 - . _ ~` left unescaped (so `+` and `&` are escaped); for places values are inserted raw — then open: link without a scheme ⇒ `https://` prefixed; place ⇒ path with `~` expanded; missing place ⇒ HUD with the name.
- **Script**: an executable file. The query `<name> <argument>` runs it **when typing pauses** (debounce 0.25 s) with the argument as its single argv entry; scripts marked "Also run when its name is typed on its own" also run for the exact bare name with an empty argument. Output (stdout + stderr merged, ≤ 64 KiB, trimmed) is cached per (script, argument) for the current opening and shown as an answer row; Return copies it (HUD) and closes. Empty output with exit 0 ⇒ nothing shown; non-zero exit with no output ⇒ "Couldn’t run this file". Return on the script row: no argument and not marked to run bare ⇒ put "<name> " in the field; a cached answer ⇒ copy it and close; otherwise run immediately (bar stays open until the answer row appears). A query that no longer names the script cancels its pending run; a run started by an older opening never publishes. Process timeout 5 s (then terminate, kill after 0.5 s). The longest matching script name wins.
- Editor (sheet "Your shortcuts"): segmented "Site or link" / "Folder or file" / "Script", Name, "Where it goes" (+ "Choose…" file picker for place/script; picking fills an empty name with the file name), placeholder chips (link/place) with meanings "what you type", "what you copied", "what is selected", "today"; script: hint "Choose an executable file. Type its name followed by what you want to send. It runs when you pause and shows the result here." and toggles "Also run when its name is typed on its own", "Run from its global shortcut without opening the bar"; Cancel / Save (needs name and destination; both trimmed).

#### 3.8.11 File search

- Off until the user adds folders (`commandBarFileScopes`, one tilde-abbreviated path per line; empty = no file search at all — never falls back to home). Adding the home folder means "every non-hidden folder in home except Library". Only existing, non-package directories are searched.
- Query (≥ 2 chars): every whitespace-separated word must appear in the **file name**, any order, case- and diacritic-insensitive (`*word*` with `\ " * ?` escaped literally). Max 1,000 candidates from the index; results sorted by file name (localized, case-insensitive) then path.
- Never offered: hidden files or anything inside a hidden folder, anything inside a package/bundle, names matching an ignore pattern. Built-in ignores (always): `node_modules`, `.git`, `DerivedData`, `Pods`, `.build`, `vendor`; user ignores (`commandBarFileIgnores`): a whole folder/file name (case-insensitive, never a partial name), `*.ext` or `.ext` (extension). Max 200 rows survive, 4 shown per typed list.
- Debounce 0.12 s; only the newest query may refresh the list; per-opening cache; the running index query is cancelled when the text moves on or the bar closes. No permission is requested and nothing is indexed by Vorssaint.
- Settings: "Search your files" — caption "Name the folders to look in and the bar finds files by name as you type. Nothing is indexed and no permission is asked for: it uses the Mac’s own search and offers only what you can already see in Finder.", "No folders yet, so the bar looks for no files.", folder list with "Remove", "Add a folder" (multi-select folder picker), "More options" → ignore list ("Names never worth showing: a whole folder or file name, or an extension written as *.log.", field "A folder or file name", "Add").

#### 3.8.12 OS settings panes (macOS discovery)

Scans `/System/Library/ExtensionKit/Extensions/*.appex` once per launch (and per UI language) off the UI thread; a pane qualifies when its Info.plist has `EXAppExtensionAttributes.SettingsExtensionAttributes.allowsXAppleSystemPreferencesURLScheme == true`. Name = localized `CFBundleDisplayName` › `CFBundleDisplayName` › `CFBundleName` › file name. Keywords = up to 160 distinct terms (each ≤ 40 chars) from the pane's `<lang>.lproj/*.searchTerms` (falls back to `en`, then to the legacy pref-pane named by `legacyPrefPaneBundleName`): for every top-level group (sorted by key), each `localizableStrings` entry contributes its `title` then its comma-separated `index` words. Opened with `x-apple.systempreferences:<bundle id>`. The 51 panes present on the reference Mac and their proposed Windows targets are in §7.3.

#### 3.8.13 Other options

- **ASCII layout** ("Switch to an ABC layout while the bar is open", default off): on open, if the current keyboard layout is not ASCII-capable, select the first enabled ASCII layout and remember the previous one; restore it on close (on the next UI-loop turn) and at app termination; a refused restore stays pending for the next close.
- **Emoji skin tone** (`commandBarEmojiSkinTone`: "" yellow default, `light`, `mediumLight`, `medium`, `mediumDark`, `dark` = U+1F3FB…U+1F3FF): applied to every emoji that accepts a tone; segmented picker with ✋ swatches in Settings (shown while the Emoji source is on).
- **Privacy note** shown in Settings: "Search runs on this Mac with no account or cloud, and what you type leaves it only through sites you open or scripts you add."

#### 3.8.14 Settings page ("Command Bar")

Buttons "Open the bar now" / "Recenter the bar" (disabled without a custom position); one explanatory block (drag caption, "One shortcut opens a field over whatever you are doing. Type a few letters, press Return and it happens. Nothing you type is saved.", privacy note, "Try" chips: `100 km to mi`, `2+2*3`, `brightness 40`, `fire`, `battery`); "Compact mode"; Emoji "Skin tone"; "Global shortcut to open the bar" + recorder + failure text; secure-input row; "More options" → ASCII layout toggle. Then sections: App shortcuts; "What the bar searches" (one toggle per source with its glyph; Actions disabled; footer "Turn off what you never want to see. Your own actions always stay."); "Search your files"; "Your shortcuts" (list with Edit/Remove, "Add shortcut", empty "Nothing saved yet. Add a site, folder, search or script you use every day."); "Rows with their own shortcut" (binding + row title + refused warning + Remove; empty "No row has its own shortcut yet. Open the actions on any row to give it one."); "Names you gave" (alias + title + Remove; empty "Nothing named yet. Open the bar, pick a row and press the actions key."); "Pinned" (Remove; "Nothing pinned yet."); "Never shown" (Remove = unhide; "Nothing hidden.") + button "Forget what I use most" (clears usage counts, query memory and habits).

---

### 3.9 Quick panel (code name QuickLauncher)

Floating grid of favourite tools, summoned with ⌃⌘V (enabled by default), from the menu panel, the Command Bar ("Quick panel"), or Settings ("Open quick panel"). Settings caption: "A floating panel with your favorite tools, summoned by a shortcut from anywhere." and "Use the tune button to choose, hide and drag the tools around."

**Tiles** (default order = this order; a tile exists only while its feature is installed):

| Id | Title (enUS) | Activation | Live state |
|---|---|---|---|
| `keepAwake` | "Keep awake" | toggle keep awake; panel stays | green dot + bolt.fill when active |
| `cleaner` | "Cleaner" | hosted utility | — |
| `toggles` | "Quick toggles" | hosted Quick toggles list (§3.10) | — |
| `micMute` | "Mute microphone" / "Unmute microphone" | toggle; panel stays | red icon + dot when muted |
| `screenOCR` | "Copy text from screen" | hide, +0.15 s capture | — |
| `colorPicker` | "Color picker" | hide, +0.15 s pick | — |
| `clipboard` | "Clipboard" | hide, +0.1 s open the clipboard quick panel | — |
| `windowLayout` | "Window layout" | hosted utility | — |
| `cleaning` | "Cleaning Mode" | hide, +0.1 s activate | — |
| `homebrew` | "Homebrew" | hosted utility | — |
| `media` | "Media" | hosted utility | — |
| `urlCleaner` | "Clean URL" | hosted manual cleaner (§3.5.3) | — |
| `uninstaller` | "Uninstaller" | hosted utility | — |
| `screenshot` | "Screenshot" | hide, +0.15 s capture | — |
| `screenRecorder` | "Screen recording" / "Stop recording" | hide, +0.15 s toggle | dot while recording |
| `cameraPreview` | "Camera preview" | (island variant dropped) hide, +0.15 s show | — |
| `scratchpad` | "Scratchpad" | hide, +0.15 s show | — |

- **Layout** (420 wide, padding 16, HUD backdrop, corner 22): header = centered brand mark, left ✕ close (or ‹ back while a utility is hosted), right edit toggle (sliders glyph; checkmark while editing; tooltip = the edit hint). Grid of 3 flexible columns, spacing 10. Tile: 46×46 rounded plate with glyph (active ⇒ accent tint; selected/hover ⇒ darker), number badge 1–9 on the first nine tiles (top-left), green dot for live state (top-right), title up to 2 lines (space reserved). Selected tile: tinted background + accent border. Footer: keyboard glyph + hotkey … "Esc". Empty state: "All tools are hidden. Use the tune button to add them back."
- **Hosted utility**: replaces the grid inside the same panel (scrollable area 470 tall); its own close button and Esc return to the grid. While hosted (or in edit mode) the panel behaves like a small working window: it survives its own dialogs, admin prompts and clicks in other apps (e.g. dragging a file into Media).
- **Edit mode**: tiles show a red ⊖ ("Hide from panel"); tiles with options show a gear and tapping the tile opens an inline **options card** under the grid: Keep awake → "Keep going with the lid closed" toggle (disabled during its setup) + "Default duration" picker (15 minutes … 8 hours, "Indefinite"); Mic mute → "Show in the menu bar while muted"; Clipboard → "Save clipboard history" (+ starts/stops capture) and, when on, the "Limit" picker; Color picker → "Copied format" segmented HEX/RGB/HSL/SwiftUI and, for HEX, "Copy without the # prefix". Drag tiles to reorder (order persisted). An "ADD BACK" tray lists hidden tiles as + chips. Hiding the tile whose options card is open closes the card.
- **Keyboard** (panel key): Esc ⇒ (IME composing passes) close hosted utility › close options card › leave edit mode › hide. Other keys only when not editing, no utility hosted and no ⌘/⌃/⌥ held: Return/keypad Enter activates the selected tile; arrows move by one cell, **clamped, no wrap** (← at column 0 and → at the last column stay put; ↑/↓ by 3); digits 1–9 activate that tile directly. Hover selects.
- **Open**: refresh availability, new presentation id, leave edit mode, close options, select the first tile (Return works immediately), place (§3.1.6), fade in 0.13 s. **Hide**: leave edit mode, close options and any hosted utility. The panel re-fits to content (keeping top edge and centre, clamped 16 inside the screen) when a utility opens/closes or edit mode toggles.
- Persistence: order `quickLauncherItemOrder` (comma list; unknown ids dropped, missing appended), hidden `quickLauncherHiddenItems` (sorted comma list).

---

### 3.10 Quick toggles

On-demand system actions (nothing observes or polls). Shown as a section of the tray panel (collapsible, with edit mode: drag handles + per-row visibility switches + "reset" restoring default order and all rows visible) and hosted inside the Quick panel (no editing). Settings section "Quick toggles" (caption "One-click system actions in the menu bar panel and in the quick panel.") also holds the dark-mode button, the keyboard-light switch + level slider (0–100 %), and the "Excluded drives" list for eject.

| # | Id | Title (enUS) | Idle caption | Behavior (macOS) |
|---|---|---|---|---|
| 1 | `darkMode` | "Switch to dark mode" / "Switch to light mode" (from the current appearance) | "Changes the appearance of the whole system." | Flip the system light/dark appearance instantly (private WindowServer call); unavailable ⇒ failed |
| 2 | `keyboardLight` | "Keyboard light" (switch row) | "Turns the keyboard backlight on or off." | Hidden when the hardware/OS does not report a keyboard backlight (Brightness spec) |
| 3 | `micMute` | "Mute microphone" / "Unmute microphone" | "Cuts the Mac’s microphone with a click or shortcut, across every app." | Toggle (Mic Mute spec); shows the mic-mute shortcut when enabled; gated by the Mic Mute feature |
| 4 | `emptyTrash` | "Empty the Trash" | "Removes everything from the Trash." | Native warning alert "Empty the Trash?" / "All items in the Trash will be removed. This cannot be undone." / buttons "Empty the Trash", "Cancel" (Return/Esc); then tell the Finder to empty the Trash. Denied Automation ⇒ *needs permission* (caption "Permission required: Finder automation" + button "Open System Settings…") |
| 5 | `ejectDisks` | "Eject all disks" | "Safely ejects every external disk." — or "No external disk ready to eject." / "Ejecting…" / "Could not eject" | Unmount + eject every qualifying volume in the background; a volume that disappears because a sibling volume's eject removed its drive is not a failure; failure only if a volume is still mounted |
| 6 | `hiddenFiles` | "Show hidden files" / "Hide hidden files" | "The Finder restarts to apply it." (always visible) | Write Finder pref `AppleShowAllFiles` (default false), restart the Finder |
| 7 | `desktopIcons` | "Hide desktop icons" / "Show desktop icons" | same | Write Finder pref `CreateDesktop` (default true), restart the Finder |
| 8 | `lockScreen` | "Lock the screen" | "Asks for the password to come back." | Close the hosting surface, +0.15 s, lock immediately (fallback: start the screen saver) |
| 9 | `displayOff` | "Turn off the display" | "The Mac keeps running with the display off." | Close surface, +0.15 s, display sleep now |
| 10 | `screenSaver` | "Start the screen saver" | "Starts right away, on every display." | Close surface, +0.15 s, start the screen saver |

- **Row state machine**: `running` (row disabled; repeated clicks ignored) → success clears immediately (the row's new title is the feedback) / `failed` shows "Could not complete." for **2.4 s** / `needsPermission` persists until the grant is detected (re-checked off the UI thread each time the list appears).
- **Eject qualification** (`shouldOfferEject`): local ∧ not the boot/root volume ∧ (not on an internal bus ∨ removable media ∨ ejectable media) ∧ not hidden ∧ not excluded. Exclusion entries (`diskEjectExcludedVolumes`, "Excluded drives", "Add drive…", "Other drive name…", caption "Drives in this list are never unmounted when using Eject all disks.") match case-insensitively by volume name, volume UUID, full mount path, or the mount folder's name.
- **Finder restart** (macOS only): with Finder Automation granted ⇒ polite quit, wait up to 3 s (30 × 0.1 s) for exit, relaunch without activating; otherwise (or if it won't quit) force-restart. Finder flag values are read tolerantly (bool, number, "YES"/"TRUE"/"1", "NO"/"FALSE"/"0"; else the default).
- The same actions appear as Command Bar rows (§3.8.7), where Empty Trash confirms inline instead of with the alert.
- Visibility keys (default all true) and order key: §4.

---

## 4. Settings table

All keys are per-user preferences (macOS `UserDefaults`, domain `com.vorssaint.utils`). "Backup" = included in the Settings export/import file. Shortcut values use the macOS format of §3.1.7 (Windows: own format, §5.3).

| Key | Type | Default | Range / values | Meaning | Backup |
|---|---|---|---|---|---|
| `clipboardHistoryEnabled` | bool | false | | Capture clipboard history | Y |
| `clipboardHistoryLimit` | int | 50 | 20, 50, 100, 250, 500, 1000, 10000, 0 (= unlimited); else 50 | Max unpinned entries | Y |
| `clipboardHistorySkipSensitive` | bool | true | | Skip password-looking text (§6.13) | Y |
| `clipboardHistoryIncludeImagesFiles` | bool | true | | Also capture images and file lists | Y |
| `clipboardHistoryIgnoredApps` | [string] | [] | bundle ids (Windows: app identities) | Never record copies from these apps | Y |
| `clipboardHistoryQuickPreview` | bool | false | | Preview sidebar open in the quick panel | Y |
| `clipboardHistoryMenuBarPreview` | bool | false | | Show latest copy next to the tray icon | Y |
| `clipboardHistoryMenuBarPreviewLength` | int | 20 | clamp 5…50 | Characters in that preview | Y |
| `clipboardHistoryShortcutEnabled` | bool | true | | Quick-panel hotkey on | Y |
| `clipboardHistoryShortcut` | shortcut | ⌃⌥⌘V (`control+option+command:9`) | | Quick-panel hotkey | Y |
| `clipboardHistoryEntries` | data | — | legacy | Old in-preferences history blob; migrated to the file and deleted | N |
| `clipboardAutoClearOnDelay` | bool | false | | Delay trigger | Y |
| `clipboardAutoClearDelaySeconds` | int | 20 | clamp 5…3600 | Seconds of stillness before clearing | Y |
| `clipboardAutoClearOnSleep` | bool | false | | Clear on computer sleep | Y |
| `clipboardAutoClearOnDisplaySleep` | bool | false | | Clear on display sleep | Y |
| `clipboardAutoClearOnScreenLock` | bool | false | | Clear on screen lock | Y |
| `panelUtilityClipboard` | bool | true | | Clipboard tab in the tray panel ("Show in panel"; works with capture off) | Y |
| `pastePlainEnabled` | bool | false | | Paste-as-plain-text hotkey on | Y |
| `pastePlainShortcut` | shortcut | ⇧⌥⌘V (`option+shift+command:9`) | | Hotkey | Y |
| `urlCleanerEnabled` | bool | false | | Automatic link cleaning | Y |
| `urlCleanerCustomParameters` | string | "" | names separated by `,` or newline; lower-cased | Global names added by the user | Y |
| `urlCleanerSiteParameters` | string | "" | `host\|name` tokens separated by `,`/newline | Site names added by the user | Y |
| `urlCleanerDisabledParameters` | string | "" | `host\|name` tokens; host "" = all sites; `\|utm_*` = the utm prefix | Built-in or user names switched off | Y |
| `panelUtilityURLCleaner` | bool | true | | Clean URL tab in the tray panel | Y |
| `textSnippetsEnabled` | bool | false | | Typed-trigger expansion | Y |
| `textSnippets` | data (JSON) | none | [TextSnippet], §5.5 | The snippets | Y |
| `snippetLibraryEnabled` | bool | false | | Library hotkey on | Y |
| `snippetLibraryShortcut` | shortcut | ⌃⌥⌘L (`control+option+command:37`) | | Library hotkey | Y |
| `snippetSoundEnabled` | bool | false | | Play sound on typed expansion | Y |
| `snippetSoundName` | string | "Tink" | system alert sound name | Sound | Y |
| `commandBarShortcutEnabled` | bool | **false** | | Command Bar hotkey on | Y |
| `commandBarShortcut` | shortcut | ⌥Space (`option:49`) | | Command Bar hotkey | Y |
| `commandBarCompactMode` | bool | false | | Empty field shows nothing else | Y |
| `commandBarASCIILayoutEnabled` | bool | false | | Borrow an ASCII layout while open | Y |
| `commandBarDisabledSources` | string | "" | comma list of source ids (§6.3.6); `actions` ignored | Sources switched off | Y |
| `commandBarAliases` | string (JSON) | "" | `{stableKey: alias≤60}` | Names the user gave rows | Y |
| `commandBarPins` | string | "" | newline list of stable keys, ≤ 30, ordered | Pinned rows | Y |
| `commandBarHidden` | string | "" | newline list of stable keys | Never-shown rows | Y |
| `commandBarLinks` | data (JSON) | none | [CommandBarLink] ≤ 60, §5.6 | Saved links/places/searches/scripts | Y |
| `commandBarRowShortcuts` | string (JSON) | none | `{stableKey: shortcut}` ≤ 64 | Per-row global hotkeys | Y |
| `commandBarPositionOffset` | string | "" | `"dx,dy"` integers | Drag offset from the default spot | Y |
| `commandBarEmojiSkinTone` | string | "" | "", light, mediumLight, medium, mediumDark, dark | Default emoji tone | Y |
| `commandBarFileScopes` | string | "" | newline list of `~`-abbreviated folders | Folders the file search looks in (empty = off) | **N** (machine-specific) |
| `commandBarFileIgnores` | string | "" | newline list of names / `*.ext` / `.ext` | Extra ignore patterns | Y |
| `commandBarUsage` | string (JSON) | none | `{rowId: {count≤999, lastUsed}}`, ≤ 200 ids | Run counts (never queries) | **N** |
| `commandBarQueryHabits` | string | — | legacy | Deleted at every launch | N |
| `panelUtilityCommandBar` | bool | true | | Command Bar entry in the tray panel | Y |
| `uninstallerCommandBarEnabled` | bool | false | | Uninstall rows/browse in the bar (Uninstaller spec) | Y |
| `killProcessCommandBarEnabled` | bool | true | | Kill Process rows/browse in the bar | Y |
| `quickLauncherShortcutEnabled` | bool | **true** | | Quick panel hotkey on | Y |
| `quickLauncherShortcut` | shortcut | ⌃⌘V (`control+command:9`) | | Quick panel hotkey | Y |
| `quickLauncherItemOrder` | string | (enum order) | comma list of tile ids | Tile order | Y |
| `quickLauncherHiddenItems` | string | "" | sorted comma list | Hidden tiles | Y |
| `panelUtilityQuickLauncher` | bool | true | | Quick panel entry in the tray panel | Y |
| `panelToggleOrder` | string | (enum order) | comma list of toggle ids | Quick toggles order | Y |
| `panelToggleDarkMode`, `panelToggleKeyboardLight`, `panelUtilityMicMute`, `panelToggleEmptyTrash`, `panelToggleEjectDisks`, `panelToggleHiddenFiles`, `panelToggleDesktopIcons`, `panelToggleLockScreen`, `panelToggleDisplayOff`, `panelToggleScreenSaver` | bool | true each | | Per-toggle visibility (note: mic mute's key is literally `panelUtilityMicMute`) | Y |
| `diskEjectExcludedVolumes` | [string] | [] | names / UUIDs / mount paths (case-insensitive) | Drives never ejected | Y |
| `notchClipboard`, `notchClipboardWindow`, `notchCommandBar`, `notchCommandBarStyle` | bool/string | true/true/true/"droplet" | | Dynamic Island routing | DROP |
| `finderPasteImageAsFile` | bool | false | | Shown on the Clipboard page; belongs to the Finder cut/paste spec ("Paste copied images as files") | Y |

Order sanitizing rule (all `…Order` keys): split by `,`, keep known ids once in stored order, then append every known id that was missing (new features join at the end).

---

## 5. Data & files

### 5.1 Clipboard history file

- Location (macOS): `~/Library/Application Support/com.vorssaint.utils/ClipboardHistory.json`. Windows proposal: `%LOCALAPPDATA%\Vorssaint\ClipboardHistory.json` (local, never roaming; consider DPAPI `CryptProtectData` at rest — improvement, not in the original).
- Directory created owner-only (0700), file written atomically and owner-only (0600) on every write (Windows: inherit the per-user ACL of `%LOCALAPPDATA%`, no Everyone/Users ACEs).
- Format: a JSON **array** of entries, pinned entries first, then recent (newest first). Keys: `id` (UUID upper-case string), `text`, `copiedAt`, `pinnedAt` (optional), `kind` (`"text"|"image"|"files"`), `filePaths` ([string]), `imageFile` (optional), `imageHash` (optional), `imageWidth`/`imageHeight` (optional ints), `sourceBundleID` (optional). Dates use the Swift default encoding: **seconds since 2001‑01‑01T00:00:00Z** as a JSON number. Missing `id` ⇒ new UUID; missing `copiedAt` ⇒ now; missing `kind` ⇒ text.
- Writes are coalesced to one per UI-loop turn, encoded and written on a background serial queue (writes land in mutation order). After a successful write the image store is swept on the UI thread against the live list.
- Load: ignore a file that is a symlink, not a regular file, or > 96 MiB; decode failure ⇒ start empty. If no file but a legacy preferences blob exists ⇒ load it and migrate immediately (the blob is removed only after the file write succeeded). If both exist ⇒ the file wins and the blob is deleted. After load: normalize order, trim, sweep orphan images.
- **Quit**: flush synchronously (run any scheduled save now and wait for the queue) so the last action (often a privacy Clear) is durable.

### 5.2 Image store

- Folder `ClipboardImages/` beside the JSON; one `<UUID>.png` per image entry (PNG bytes exactly as captured or converted). Deleted when no entry references it (swept after every save and at launch).
- Thumbnails for lists: downsampled to max 480 px (file-image thumbnails also at 64 px for multi-file previews), decoded off the UI thread (2 concurrent), cached (count 120, ~48 MB cost limit), cancelled when a row scrolls away; placeholders keep the aspect ratio. File icons cached (120).

### 5.3 Settings storage on Windows

- Keep the same key names and value semantics (they appear in backups). Suggested store: one JSON settings file in `%APPDATA%\Vorssaint\settings.json` (or `HKCU\Software\Vorssaint`), with typed defaults identical to §4.
- **Shortcuts**: store Windows virtual-key codes, e.g. `"ctrl+alt+win:0x56"`; never reuse macOS key codes. For importing a macOS backup, translate the four modifier tokens (`command→ctrl`, `option→alt`, `control→win`, `shift→shift` — or the user's chosen mapping) and map macOS ANSI key codes to VKs (V=9→0x56, L=37→0x4C, Space=49→0x20, letters/digits per the kVK table), and re-validate (bare keys refused for row shortcuts).

### 5.4 App identity on Windows (replaces bundle ids)

Used by ignored apps, clipboard source attribution, Command Bar app rows (stable key `app.bundle.<id>`), quit rows, row shortcuts and pins: packaged apps ⇒ AppUserModelID; Win32 apps ⇒ the AUMID if set, else the normalized full executable path (lower-cased, `\` separators). Display name and icon resolved from the Start-menu shortcut / package manifest / file version info.

### 5.5 Snippets JSON (`textSnippets`)

Array of `{"id": UUID, "name": string, "trigger": string, "replacement": string, "expansion": "immediate"|"afterDelimiter", "enabled": bool, "ignoresCase": bool, "folder": string, "showsInLibrary": bool}`. Unknown/invalid data ⇒ empty list. Replacements may contain ICU date patterns and IANA zone ids (§6.12) — preserve them byte-for-byte across platforms.

### 5.6 Command Bar JSON values

- `commandBarLinks`: array of `{"id", "name", "kind": "link"|"place"|"script", "destination", "runsWithoutArgument", "runsDirectly"}` (each key optional on decode; empty name/destination dropped; ≤ 60 encoded).
- `commandBarUsage`: `{"<rowId>": {"count": int ≤ 999, "lastUsed": seconds since **1970** (Unix)}}`; when > 200 ids, the least recently used are dropped. Keyed by **row id** (not stable key).
- `commandBarAliases`: `{"<stableKey>": "<alias>"}` (empty values dropped on decode). `commandBarRowShortcuts`: `{"<stableKey>": "<shortcut storage string>"}`.
- Pins/hidden/file scopes/file ignores: newline-separated, trimmed, de-duplicated. Disabled sources: comma-separated, sorted on write. Position: `"dx,dy"` rounded integers in macOS points (Windows: DIPs, y sign inverted — positive dy = moved up).

### 5.7 In-memory only (never written)

Command Bar query memory, keyed query habits (per-process random 256-bit HMAC key), the last query, the selected text read on open, script outputs, file-search results, menu/window/process lists, catalog and its folded index. Snippet typing buffer (≤ 64 chars). Transient-paste snapshot (≤ 0.5 s).

### 5.8 Retention summary

| Data | Retention |
|---|---|
| Recent clipboard entries | until count limit / byte budgets / Delete / Clear unpinned; no age expiry |
| Pinned entries | until unpinned or deleted |
| Image files | as long as their entry exists |
| OS clipboard content | auto clear (if enabled) |
| Command Bar usage | until "Forget how often I use this" / "Forget what I use most"; capped at 200 rows |
| Everything typed into the bar | process lifetime only |

---

## 6. Algorithms & data

### 6.1 Text folding (search normalization)

Both searches are **locale-independent** (a Turkish locale must not turn `I` into dotless `ı`).

- **Clipboard fold** `normC(s)`: fold case + diacritics + width (Unicode-wise, no locale) → lower-case → `\n` and `\t` → space → trim whitespace/newlines. Runs of spaces are *not* collapsed.
- **Command Bar fold** `normB(s)`: remove invisible format characters (Unicode general category Cf with code point ≥ U+00AD: LRM/RLM, ZWJ/ZWNJ, soft hyphen, …) → fold case + diacritics + width → lower-case → split on **any** Unicode whitespace (incl. U+3000) and join with single spaces.
- Windows implementation: NFKD → drop combining marks (Mn) → full Unicode case fold → lower-case → (NFC). NFKD also folds ligatures/superscripts that Apple's width folding keeps; acceptable, but use one shared function for every search, the URL-link name matcher and the unit/date tokenizers so behavior is consistent.

### 6.2 Clipboard search ranking (`ClipboardHistorySearch`)

Input: folded query `q`, tokens `T` = `q` split on whitespace. For each entry with folded searchable text `x` and its word set `W` (= `x` split on whitespace):
1. Keep only if every token is a substring of `x`.
2. `score = (pinned ? 30 : 0) + (x == q ? 1200 : 0) + (x.hasPrefix(q) ? 900 : 0) + (x.contains(q) ? 700 : 0)` (these three add up; an exact match also counts as prefix and contains) `+ Σ_t (t ∈ W ? 140 : (some w ∈ W starts with t ? 80 : 40))`.
3. Sort by score desc, then original history position asc.

Example: entries "Deploy checklist final", "Final database deploy plan"; query "deploy final" ⇒ both match, ranked [0, 1].

### 6.3 Command Bar ranking (`CommandBarSearch`)

#### 6.3.1 Token score (best per token over the words of `haystack = title + " " + keywords`, all folded)

| Match | Points | Condition |
|---|---|---|
| a word equals the token | **140** | (returns immediately) |
| a word starts with the token | **80** | |
| token is a substring of the haystack | **44** | |
| token is an in-order subsequence of one word | **24** | token ≥ 3 chars and not all digits |
| token equals a word with one adjacent pair swapped | **16** | token ≥ 3 chars, same length, not all digits |
| token within one edit of a word (substitution, insertion, deletion or adjacent swap) | **16** | token ≥ 4 chars, not all digits |
| none of the above | **reject the row** | |

#### 6.3.2 Whole-query bonus and tier (folded title `t`, folded keywords `k`, folded query `q`)

- Bonus (first that applies): `t == q` +1200; `t.hasPrefix(q)` +900; `t.contains(q)` +700; `k.contains(q)` +350.
- Tier: 5 exact title, 4 title prefix, 3 title contains, 2 keywords contain, 1 otherwise.
- `base = Σ token scores + bonus`.

#### 6.3.3 Candidate inputs (typed search pool)

- Title text: `normB(matchTitle ?? title)`; for saved searches/scripts: `normB(query)` when the query names the row (`trailingArgument ≠ nil`), else the name.
- Keywords text: `normB(keywords)` + `" " + normB(alias)` when the user named the row.
- `priority = max(aliasHit, habitBoost)` where `aliasHit` = **2400** if a word of the alias equals the whole folded query, **1100** if a word starts with it, else 0; `habitBoost` = §6.4.3 (only rows that count usage).
- `boost = usageBoost(§6.4.1) [rows counting usage] + (row is live/running ? 20 : 0) + aliasHit + queryMemoryBoost(§6.4.2, 0–3) [rows counting usage] + rankBias(source) + (pinned ? 60 : 0)`.
- `rankBias`: menus −80, files −40, settings pages −40, apps +80, everything else 0.
- Inside a category and for `:` emoji searches the candidates carry only `priority = habitBoost` (no alias, usage, live, bias or pin boosts); the same sort applies.

#### 6.3.4 Ordering

1. Sort matching candidates by `priority` desc → `tier` desc → `base + boost` desc → original pool position asc.
2. **Feature ordering** (`featureOrdered`): among rows with priority 0, rows that belong to the same feature keep the *set of positions* they already occupy but are reordered inside it by role: main command (turn 0: `toggle.<f>[.x]` or `action.<f>`), presets (turn 1: `action.<f>.<x>`), its settings page (turn 2: `settings.feature.<f>` or `settings.<f>` without a further dot); equal turns keep ranked order. Nothing else moves.
3. Assemble with caps and the 12-row limit (§3.8.6 step 9).

#### 6.3.5 Fuzzy helper definitions

- *Subsequence*: every character of the token appears in the word in order.
- *Adjacent transposition*: equal lengths and exactly one neighbouring pair differs by being swapped (`brilho`/`birlho`).
- *Within one edit*: lengths differ by ≤ 1 and a single substitution, insertion, deletion or adjacent swap turns one into the other (identical strings also qualify).

#### 6.3.6 Source ids (persisted in `commandBarDisabledSources`, never change)

`actions`, `apps`, `menus`, `windows`, `quitApps`, `uninstallApps`, `settingsPages`, `macSettings`, `snippets`, `clipboard`, `emoji`, `folders`, `answers`, `calculator`, `selection`, `links`, `files`, `killProcess`. Row → source by id prefix: `app.` apps, `menu.` menus, `window.` windows, `quit.` quitApps, `uninstall.` uninstallApps, `settings.` settingsPages, `macsettings.` macSettings, `snippet.` snippets, `clipboard.` clipboard, `emoji.` emoji, `folder.` folders, `answer.` answers, `selection.` selection, `link.` links, `file.` files, `kill.` killProcess; anything else ⇒ actions. The Actions category additionally lists `emoji.browse`, `kill.browse`, `uninstall.browse`, `uninstall.finder` (only while their target source is on).

#### 6.3.7 Highlighting

For each query token (empty when showing suggestions, for answers, and for the numeric part of `verb 40`; for `:` queries the part after the colon): fold the title per character (keeping positions), find the first occurrence that starts at a word start (position 0 or after a space), else the first occurrence anywhere; mark those character positions accent-bold. Fuzzy matches are never highlighted.

### 6.4 Learning

#### 6.4.1 Usage (persisted, `commandBarUsage`)

- On every run of a row with `countsUsage`: `count = min(count + 1, 999)`, `lastUsed = now` (Unix seconds). More than 200 ids ⇒ drop the least recently used.
- `usageBoost(use) = min(count, 40) × w`, where `w` = 12 if used < 2 h ago, 8 if < 48 h, 5 if < 14 days, else 2. (Max 480 — always below a literal text hit's bonuses.)
- Home suggestions: used ids sorted by count desc then lastUsed desc, then curated fill-ins, ≤ `7 − pins`. Category browse: used first (count, then recency), unused keep catalog order.
- Rows that never count usage: menus, windows, clipboard, files, kill, answers, calculator/answer rows, typed URL, script answers, "Use it in the search", "Count it", uninstall browse/finder rows.

#### 6.4.2 Query memory (session only, `CommandBarQueryMemory`)

- Recorded only when a usage-counting row runs **from the visible bar**: for the typed query (or the text before Tab completion; for argument/actions modes the query saved before entering them; a leading `:` is stripped).
- Prefixes = the folded query's prefixes of length 1…12, each trimmed, unique.
- Per prefix: `{rowId: (count ≤ 99, step)}`, ≤ 4 rows (evict lowest count, then oldest step); ≤ 60 prefixes (evict the prefix whose newest step is oldest). `step` = a counter incremented per recorded choice.
- `boost(q, row) = min(count, 3)` for the exact folded query `q` (0 if never chosen) — a tie-breaker only.

#### 6.4.3 Query habits (session only, keyed digests)

- `prepare(q)`: folded query cut to 24 chars; for each prefix length L = 1…n: digest = first 12 bytes (hex) of HMAC-SHA256(key, prefix), specificity = L. `key` = 32 random bytes generated once per process (never stored; a legacy persisted store is deleted at launch). Incremental: typing one more character only hashes the new prefix.
- Record on run (usage-counting rows, non-empty learning query): for every prefix digest, `choices[rowId]` count +1 (≤ 999), lastUsed = now; ≤ 4 choices per digest (drop least recently used); ≤ 320 digests (drop the digest whose latest use is oldest).
- `habitBoost(row) = max over prefixes of min(usageBoost(use) × 5 + specificity × 6, 720)`. Because it lands in **priority**, a row chosen for these letters earlier in the session leads the list even over a better textual match.
- "Forget how often I use this" removes the row from usage, query memory and habits; "Forget what I use most" clears all three.

### 6.5 Numeric arguments

- `splitTrailingNumber(q)`: tokens by space; ≥ 2 tokens and the last token is digits with an optional trailing `%` ⇒ `(text = other tokens joined by space, number)`; else `(trimmed q, none)`.
- The split is used only if some catalog row with a numeric range matches `text` (§3.8.6 step 6).
- Running a numeric row: typed number ⇒ clamp into range and run; none and optional ⇒ run without; none and required ⇒ argument mode. `argumentValue(text)`: trim, optional trailing `%`, 1–4 digits only, clamp into range.

### 6.6 Calculator (`CommandBarMath`)

#### 6.6.1 Gates (any failure ⇒ "not a sum", the text stays a search)

1. Trim; at most one `=` and only as the last character (dropped); result non-empty and ≤ **120** characters.
2. Not date/time-shaped: splitting by one separator kind among `-`, `/`, `:` gives 2–3 groups of 1–4 digits each (groups trimmed) ⇒ refuse if the separator is `:` (time) or there are 3 groups (date). `100-50` (two groups with `-`) is allowed.
3. Tokenization succeeds (only the symbols and words below).
4. It is a calculation: contains an explicit operator (`+ - * / ^` or an *of*-word), or a function, or an implicit product (number/constant/`)`/`]` immediately followed by constant/function/`(`/`[`). A lone number, a lone percentage (`50%`) or a lone constant is not.
5. Brackets: a closer must match the innermost opener's kind; surplus closers ⇒ refuse; missing closers are appended virtually (innermost first) and reported (`([2+3` ⇒ `])`).
6. Parse consumes every token; nesting depth < 32; every intermediate value finite.

#### 6.6.2 Tokens

- Whitespace ignored. Numbers per §6.6.4. `π` ⇒ π.
- Letter runs are read as lower-case words (`log` may absorb following digits: `log10`): `x` ⇒ ×; `pi` ⇒ π; `e` ⇒ Euler's number; functions `sqrt abs sin cos tan asin acos atan ln log log10 exp floor ceil round` (`log10` = `log` = base 10); *of*-words `of de da do von di del dal`; any other word ⇒ not math (`hello`, `1password`, `volume 20`, `e-mail`).
- Symbols: `+`; `-` or U+2212 `−`; `*` or U+00D7 `×`; `/` or U+00F7 `÷`; `^`; `%`; `(` `)` `[` `]`. Anything else ⇒ not math.

#### 6.6.3 Grammar (precedence low → high)

```
expression := term { ("+" | "-") term }
term       := factor { ("*" | "/" | of-word | <implicit>) factor }
factor     := ("+" | "-") factor | power          ; sign covers the power: -2^2 = -4
power      := primary [ "^" factor ]              ; right-associative: 2^3^2 = 512
primary    := ( number | constant | bracketed | function bracketed ) [ "%" ]
bracketed  := "(" expression ")" | "[" expression "]"
```
- `%` after a primary: value ÷ 100, remembering the written percentage `p`. In `a + b%` / `a − b%` the percentage is relative: `a ± a·p/100` (`480+15%` = 552, `480-15%` = 408). An *of*-word requires its left operand to be a percentage and multiplies (`20% of 480` = 96). In `*` it is a fraction (`200*10%` = 20).
- `/` by zero ⇒ not a sum. Functions require a bracketed argument; domain errors ⇒ not a sum: `sqrt(x<0)`, `asin/acos` outside [−1, 1], `ln/log(x≤0)`; non-finite results ⇒ not a sum. Trig uses radians. `round` = half away from zero.

#### 6.6.4 Number reading (locale-aware)

- `decimal` = locale decimal separator; `alternate` = the locale grouping separator if it is `.` or `,` and differs from `decimal`, else the other of `.`/`,`; `groupingOnly` = the locale grouping separator when it is something else (space, NBSP, NNBSP, apostrophe…). Number characters: digits, `decimal`, `alternate`, `groupingOnly` (the last only when a digit follows).
- If `groupingOnly` occurs: the integer part (before the first `decimal`/`alternate`) must look like grouping (first group 1–3 digits not starting with 0, every later group exactly 3) and no `groupingOnly` may follow the integer part ⇒ strip them; else not a number.
- Optional ASCII exponent right after: `e|E [+|-] digits`.
- Both `decimal` and `alternate` present ⇒ the one that appears **last** is the decimal point, the other is grouping. Only `alternate` present ⇒ grouping if it looks like grouping, else a decimal point. Only `decimal` ⇒ decimal point. At most one decimal point; value must be finite.
- Examples — en_US (decimal `.`, grouping `,`): `1,000+1` ⇒ "1,001". pt_BR (decimal `,`, grouping `.`): `1.234,5 + 1` ⇒ "1.235,5"; `1,5*2` ⇒ 3; `1.500+1` ⇒ 1501 (three digits after the grouping mark ⇒ thousands). pt_PT/fr_FR (grouping NBSP/NNBSP): `1.5+1` ⇒ 2.5, `1 234,5+1` ⇒ 1235.5, `1 5+1` ⇒ not a number. Everywhere: `0,125*8` ⇒ 1 (thousands never start with 0).

#### 6.6.5 Result, formatting, reuse

- Round to **10 significant digits** (removes binary noise: `0.1+0.2` ⇒ 0.3).
- Format with the locale: if `|x| ≥ 1e12` or `|x| < 1e-6` (x ≠ 0) ⇒ scientific, ≤ 8 significant digits, exponent symbol `e` (`1/1000000000` ⇒ "1e-9", `2^100` ⇒ "1.2676506e30"); else decimal with grouping, 0–8 fraction digits. −0 prints as 0.
- Row: title = formatted value, subtitle "Return copies" (with virtual closers: "<expression><closers> · Return copies"); Return copies the formatted value.
- **Tab** (reuse): shortest round-trip decimal of the stored value, trailing `.0` dropped, `.` replaced by the locale decimal separator, negative values wrapped in parentheses (`(-4)` so `(-4)^2` = 16).

### 6.7 Unit conversion (`CommandBarUnits`)

- **Tokenize**: `normB(input)` split on spaces; a token starting with a digit, `-`, `.` or `,` is split into its leading run of `[0-9.,-]` and the rest (`100km` ⇒ `100`, `km`).
- **Parse**: 3–8 tokens. Scan positions from `count−2` down to 1 for a *conversion word*: `to in into as em para pra en a nach zu à su -> > →`. Right side must be exactly one known unit; left side exactly `[number, unit]`; same family; result finite ⇒ answer (first success from the right; so in `5 in to cm` the last `to` is the verb and `in` is inches). Otherwise not a conversion (`minutes to read the article`, `safari to dock`, `100 km` stay searches).
- **Number**: like §6.6.4 but simpler: `alternate` = locale grouping if it is `.`/`,` and ≠ decimal, else the other of `.`/`,`; both present ⇒ last one is decimal; only alternate ⇒ grouping iff groups look like thousands (first 1–3 digits, not starting with 0, rest exactly 3; leading `-` ignored); characters must then be digits, one `.`, `-`.
- **Lexicon** (matched exactly after folding; never fuzzy):

| Family | Names | Unit | Factor to base (Foundation, as shipped) |
|---|---|---|---|
| Temperature (base K) | `c celsius °c centigrade` | °C | K = °C + 273.15 |
| | `f fahrenheit °f` | °F | K = °F × 0.55555555555556 + 255.37222222222428 |
| | `k kelvin` | K | 1 |
| Length (base m) | `mm millimeter millimeters milimetro milimetros` | mm | 0.001 |
| | `cm centimeter centimeters centimetro centimetros` | cm | 0.01 |
| | `m meter meters metro metros metre metres` | m | 1 |
| | `km kilometer kilometers quilometro quilometros` | km | 1000 |
| | `in inch inches polegada polegadas "` | in | 0.0254 |
| | `ft foot feet pe pes` | ft | 0.3048 |
| | `yd yard yards jarda jardas` | yd | 0.9144 |
| | `mi mile miles milha milhas` | mi | 1609.344 |
| Mass (base kg) | `mg milligram milligrams` | mg | 0.000001 |
| | `g gram grams grama gramas` | g | 0.001 |
| | `kg kilogram kilograms quilo quilos` | kg | 1 |
| | `t ton tons tonne tonelada toneladas` | t (metric) | 1000 |
| | `oz ounce ounces onca oncas` | oz | 0.0283495 |
| | `lb lbs pound pounds libra libras` | lb | 0.453592 |
| Data (base byte) | `b byte bytes` | B | 1 |
| | `kb kilobyte kilobytes` | kB | 10³ |
| | `mb megabyte megabytes` | MB | 10⁶ |
| | `gb gigabyte gigabytes` | GB | 10⁹ |
| | `tb terabyte terabytes` | TB | 10¹² |
| | `kib kibibyte kibibytes` | KiB | 2¹⁰ |
| | `mib mebibyte mebibytes` | MiB | 2²⁰ |
| | `gib gibibyte gibibytes` | GiB | 2³⁰ |
| | `tib tebibyte tebibytes` | TiB | 2⁴⁰ |
| | `bit bits` | bit | 0.125 |
| Duration (base s) | `ms millisecond milliseconds` | ms | 0.001 |
| | `s sec secs second seconds segundo segundos` | s | 1 |
| | `min mins minute minutes minuto minutos` | min | 60 |
| | `h hr hrs hour hours hora horas` | hr | 3600 |
| Volume (base L) | `ml milliliter milliliters mililitro mililitros` | mL | 0.001 |
| | `l liter liters litre litres litro litros` | L | 1 |
| | `gal gallon gallons galao galoes` | gal (US) | 3.78541 |
| | `qt quart quarts` | qt (US) | 0.946353 |
| | `floz fluidounce fluidounces` | fl oz (US) | 0.0295735 |
| | `cup cups xicara xicaras` | cup | 0.24 |

  (Foundation's imperial factors are truncated to 6 significant digits; use these exact values to reproduce macOS output, or the exact legal values knowingly.)
- **Format**: the requested unit is always the displayed unit; medium (abbreviated) style, localized numbers; fraction digits by magnitude: 0 ⇒ 0, < 1 ⇒ 4, < 100 ⇒ 2, else 1. en_US examples: "62.14 mi", "68°F", "5,000 MB", "1,024 MiB", "120 min", "2.2 lb", "1,500 mL", "3 c" (cups), "1 fl oz". **Feet**: when converting *to* feet and the value ≥ 1 ⇒ whole feet + inches (inches rounded with the magnitude rule; 12 carries into feet; 0 inches omitted): `180 cm to ft` ⇒ "5 ft 10.87 in", `6 ft to ft` ⇒ "6 ft"; < 1 ft or negative stays decimal feet (`2 cm to ft` ⇒ "0.0656 ft").

### 6.8 Colors (`ColorValue`, `CommandBarColors`)

- **Accepted alone** (whole trimmed text, case-insensitive, ≤ 96 bytes): `#RGB`, `#RGBA`, `#RRGGBB`, `#RRGGBBAA`; `rgb(…)`/`rgba(…)` with 3–4 arguments separated by commas, spaces and/or `/` — channels 0–255 or 0–100 %, alpha 0–1 or 0–100 % (absent ⇒ 1); `hsl(…)`/`hsla(…)` — hue number with optional `deg` (wrapped mod 360, negatives allowed), saturation and lightness as percentages, optional alpha; SwiftUI `Color(red: r, green: g, blue: b[, opacity: a])` — labels exactly in that order, any spacing, values 0–1. Rejected: bare `RRGGBB`, `#12345`, wrong label order, out-of-range values, extra trailing comma, colors inside longer text.
- **HSL → RGB**: `h' = (h mod 360 + 360) mod 360 / 360`; `C = (1 − |2L − 1|)·S`; channel(n) = `L − C/2 · max(−1, min(k − 3, 9 − k, 1))` with `k = (n + 12h') mod 12`; R = channel(0), G = channel(8), B = channel(4).
- **RGB → HSL**: standard (max/min/delta; delta < 1e-6 ⇒ hue 0, saturation 0); hue in degrees.
- **Output** (components clamped 0–1): hex `#RRGGBB` upper-case (+`AA`); rgb `rgb(r, g, b)` / `rgba(r, g, b, a)`; hsl `hsl(h, s%, l%)` / `hsla(h, s%, l%, a)` (rounded integers); alpha text = `%g` of alpha rounded to 3 decimals (POSIX); SwiftUI `Color(red: 0.200, green: 0.400, blue: 0.600[, opacity: 0.500])` (3 decimals, POSIX).
- **Conversion query**: `<color> <conversion word> <target>` (last two whitespace tokens; ≥ 3 tokens), target ∈ `hex rgb rgba hsl hsla swift swiftui` (case-insensitive). Alpha is written when the target is an *a* form or the color's alpha < 1. Examples: `#a2b3b4 to rgb` ⇒ "rgb(162, 179, 180)"; `#A2B3B4 in HSL` ⇒ "hsl(183, 11%, 67%)"; `rgba(255, 0, 0, 0.5) to hex` ⇒ "#FF000080"; `#00000080 to rgb` ⇒ "rgba(0, 0, 0, 0.502)"; `#f80 nach rgb` ⇒ "rgb(255, 136, 0)"; `#336699 to swift` ⇒ "Color(red: 0.200, green: 0.400, blue: 0.600)". Every 8-bit alpha survives rgba/hsla/SwiftUI → hex round trips.
- **Color preview row**: a lone color value ⇒ answer row with a swatch, title = the typed value, Return copies it; placed per §3.8.6 step 9.
- Clipboard text entries that are a lone color show a swatch.

### 6.9 Date questions (`CommandBarDates`)

Input trimmed ≤ 80 chars, folded tokens (`normB`), ≥ 2 tokens. Tried in order:
1. **Relative date**: the first adjacent pair `<integer |n| ≤ 10000> <unit word>`; requires at least one *today*, *future* or *past* word (a sign alone is not a direction); backwards if a past word or a standalone `-` token is present. Answer = long date of `now ± n units`; detail = weekday name. (`in 3 weeks` ⇒ "August 18, 2026" / "Tuesday"; `3 days ago`, `ha 3 dias`, `today + 10 days`, `today - 10 days`; `3 days` alone ⇒ none.)
2. **Days until**: an *until* word followed by a date: parsed with the locale's short date format, else two numbers (day/month order from the locale's `yMd` pattern) with the year filled in (next occurrence: next year if already past). Answer = the absolute number of whole days between the start of today and the start of that day, as a full localized duration ("150 days", "1 day"); detail = long date of the target.
3. **Time elsewhere**: a *time* word plus a place: remaining tokens minus time words, future words and `a`, `at`, `de`, joined ⇒ looked up in the city→zone table (else its last word). Answer = short time in that zone; detail = "<City> · <full date there>".

Vocabulary (already folded):
- today: `today hoje heute hoy oggi aujourdhui aujourd'hui segodnya сегодня bugun 今日 오늘 今天`
- future: `in em daqui dentro nach tra fra dans через sonra 後 후 后 from`
- past: `ago atras ha hace vor fa назад once 前 전`
- until: `until till ate hasta bis fino jusqu до kadar 까지 까지는`
- time: `time hora horas hour uhrzeit uhr ora heure время saat 時刻 時間 시간 时间 现在`
- units — day: `day days dia dias tag tage giorno giorni jour jours den dnya dney дня дней день gun gunler 日 일 天`; week: `week weeks semana semanas woche wochen settimana settimane semaine semaines nedelya недели недель неделя hafta 週 주 周 星期`; month: `month months mes meses monat monate mese mesi mois месяц месяца месяцев ay 月 월 个月`; year: `year years ano anos jahr jahre anno anni an ans annee annees god года лет год yil 年 년`
- City table: every IANA identifier's last component, `_` → space, folded (first identifier in sorted order wins), plus aliases `londres→London, lisboa→Lisbon, roma→Rome, moscou/moscovo/москва→Moscow, nova york/nueva york/nova iorque→New_York, cidade do mexico/ciudad de mexico→Mexico_City, pequim/pequin→Beijing, toquio/tokio→Tokyo, genebra→Geneva, viena→Vienna, copenhague→Copenhagen, praga→Prague, atenas→Athens, varsovia→Warsaw, bruxelas→Brussels, zurique→Zurich, munique→Munich, colonia→Cologne, estocolmo→Stockholm, hamburgo→Hamburg` (an alias is added only if its target city exists).
- Must stay `none` for: `1password`, `2 monitors`, `3 tags`, `notes`, `day one`, `5 minutes`, `2026-07-28`, `the 3 body problem`, `time` alone.

### 6.10 Emoji data (`CommandBarEmoji`)

- **Source**: no shipped name list — names come from the Unicode character database (`Any-Name` transform: `\N{FACE WITH TEARS OF JOY}` → "face with tears of joy"; for multi-scalar sequences the names of all scalars except variation selectors and "ZERO WIDTH …" joined by spaces), lower-cased.
- **Order**: the *popular* list first (in this order), then the **long tail**: every scalar U+0000…U+1FAFF with `Emoji` property, excluding emoji modifiers (skin tones), regional indicators U+1F1E6–1F1FF, hair components U+1F9B0–1F9B3, and keycap bases `#`, `*`, `0–9`; text-default scalars get U+FE0F appended; sorted by name; duplicates of a popular emoji (ignoring U+FE0E/FE0F) skipped. Total > 1,000.
- **Popular list**: 😀 😃 😄 😁 😆 😅 🤣 😂 🙂 🙃 😉 😊 😍 🥰 😘 😗 😜 🤪 🤔 🤗 🤩 🥳 😎 🤓 😐 😑 😶 🙄 😏 😥 😮 😴 😌 😔 😪 🤤 😭 😢 😤 😠 😡 🤬 🤯 😳 🥺 😱 😨 😰 💀 ☠️ 🙏 👍 👎 👌 🤌 ✌️ 🤞 🤟 🤘 👏 🙌 👐 💪 🫶 👋 🤝 ✍️ 💅 👀 🧠 🫀 🦾 🦿 👣 ❤️ 🧡 💛 💚 💙 💜 🖤 🤍 💔 ❣️ 💕 💞 🔥 ✨ ⭐️ 🌟 💫 ⚡️ ☀️ 🌤 ☁️ 🌧 ⛈ ❄️ 🎉 🎊 🎁 🎂 🍰 🍕 🍔 🍟 🌮 🍣 🍎 🍌 ☕️ 🍺 🍻 🥂 🍷 🧉 🥤 🍫 🍪 🍩 🥐 🧀 🚀 ✈️ 🚗 🚕 🚲 🛴 🏠 🏢 🌍 🌎 🌏 🗺 💻 🖥 ⌨️ 🖱 📱 ⌚️ 🎧 📷 🔋 💾 🖨 📡 📁 📂 📄 📌 📎 🔖 🔍 🔒 🔓 🔑 🛠 ⚙️ ✅ ❌ ⚠️ ❓ ❗️ 💡 🔔 🔕 ♻️ 🆗 🆕 🔝 🐶 🐱 🐭 🐰 🦊 🐻 🐼 🐨 🦁 🐮 🐷 🐸 🌱 🌲 🌳 🌴 🌵 🌷 🌸 🌹 🌺 🌻 🍀 🍁 ⏰ ⏳ 📅 📈 📉 📊 💰 💳 🏆 🥇 🎯 🧩 (single text-default scalars get U+FE0F for display).
- **Aliases** (extra search words): 😂 "haha lol roflmao laugh laughing tears funny"; 🤣 "haha lol rofl roflmao laugh laughing funny"; 😊 "happy smile blush"; 🥰 "love affection hearts"; 😘 "kiss love"; 😎 "cool sunglasses"; 🤔 "think thinking hmm"; 🙄 "eyeroll whatever"; 😭 "cry crying sad sob"; 🥺 "please pleading puppy eyes"; 😡 "angry mad rage"; 🤬 "swear cursing angry"; 🤷 "idk shrug whatever"; 💀 "dead death dying skeleton halloween"; ☠️ "dead death danger poison pirate"; 🙏 "appreciate please thanks thank thx you pray prayer high five"; 👍 "yes good approve like okay"; 👎 "no bad disapprove dislike"; 👌 "okay perfect good"; 👏 "clap applause congrats congratulations"; 🙌 "hooray celebrate praise"; 🫶 "love heart hands"; 👀 "look looking eyes see"; ❤️ "love heart red"; 💔 "heartbreak broken heart sad"; 🔥 "fire hot lit trending"; ✨ "sparkle sparkles magic clean"; 🎉 "party celebrate celebration congrats congratulations"; ✅ "check done yes complete success"; ❌ "cross no wrong error fail"; ⚠️ "warning caution alert"; 💡 "idea lightbulb tip"; 🚀 "launch ship rocket fast". (🤷 is reached via the long tail.)
- **Row**: id `emoji.<character as listed>`; title `"<glyph with default tone>  <name>"`; match title = name; keywords = name + aliases + "Emoji".
- **Skin tones**: a tone applies only to a single-scalar base with `Emoji_Modifier_Base` that is not U+1F46A 👪; applying = base without variation selectors + the modifier scalar (U+1F3FB light … U+1F3FF dark).

### 6.11 URL cleaning (`URLCleaning`)

#### 6.11.1 Rule data — verbatim from `Sources/Vorssaint/Core/URLCleaning.swift`

```swift
private static let trackedParameters: Set<String> = [
    "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
    "utm_id", "utm_name", "utm_reader", "utm_viz_id", "utm_pubreferrer",
    "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "yclid",
    "mc_cid", "mc_eid", "igshid", "twclid", "ttclid", "li_fat_id",
    "mkt_tok", "_hsenc", "_hsmi", "__twitter_impression",
    "fb_action_ids", "fb_action_types", "fb_source", "mibextid",
]

/// Trackers that only one site uses. A global list cannot hold these: `si`
/// is a share token on YouTube but a real parameter elsewhere, and `t` is a
/// tracker on X while it is the playback position on YouTube. Matching on
/// the host is what keeps removing one from breaking the other.
///
/// Keys match the host itself or any subdomain of it.
private static let hostParameters: [String: Set<String>] = [
    "youtube.com": ["si", "pp", "feature", "kw"],
    "youtu.be": ["si", "pp", "feature", "kw"],
    "twitter.com": ["s", "t", "cn", "src", "refsrc", "ref_src", "ref_url"],
    "x.com": ["s", "t", "cn", "src", "refsrc", "ref_src", "ref_url"],
    "instagram.com": ["igsh"],
    "spotify.com": ["si"],
    // Reddit's share sheet routes through branch.io, whose deep link fields
    // start with a literal `$`. Links often carry them percent-encoded as
    // `%24…`, but only the decoded spelling is listed: `decodedName` decodes
    // the name half of each pair before the rules see it. ClearURLs lists
    // both because it matches the raw query with regex.
    "reddit.com": [
        "correlation_id", "ref_campaign", "ref_source", "rdt", "share_id",
        "_branch_match_id", "$deep_link", "$3p", "$original_url",
    ],
    "tiktok.com": [
        "u_code", "preview_pb", "_d", "_t", "_r", "timestamp", "user_id",
        "share_app_name", "share_iid",
    ],
    "bilibili.com": [
        "spm_id_from", "from_spmid", "from_source", "share_source", "share_from",
        "share_medium", "share_plat", "share_tag", "share_session_id", "msource",
        "refer_from", "seid", "unique_k", "vd_source", "plat_id", "buvid", "bbid",
        "up_id", "is_story_h5", "timestamp", "ts", "visit_id", "session_id",
        "broadcast_type", "is_room_feed",
    ],
    "xiaohongshu.com": [
        "xhsshare", "author_share", "xsec_source", "share_from_user_hidden",
        "shareredid", "share_id", "exsource", "app_version", "app_platform",
        "apptime", "appuid",
    ],
]

/// The key the global rules live under. Empty so a stored token reads as
/// `|ref` for a global name and `youtube.com|si` for a site one.
static let allSites = ""

/// The single row standing for every `utm_` name. The cleaner matches the
/// prefix, so listing `utm_source` and its siblings separately would show
/// rows that cannot be switched off on their own.
static let utmWildcard = "utm_*"

private static let plainPasteboardTypes: Set<String> = [
    "public.utf8-plain-text", "public.url", "public.url-name",
    "NSStringPboardType", "NSURLPboardType",
]

/// Marks from the nspasteboard.org convention: concealed is a password
/// manager handing over a secret, transient is content about to be put
/// back by whoever placed it.
private static let untouchablePasteboardTypes: Set<String> = [
    "org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType",
]

/// A file being copied or promised, under the names that are not types
/// the system knows and would otherwise pass as private notes.
private static let filePasteboardTypes: Set<String> = [
    "NSFilenamesPboardType",
    "com.apple.NSFilePromiseItemMetaData",
    "Apple files promise pasteboard type",
]
```

Same data, language-neutral (31 global names + `utm_` prefix; 10 hosts, 78 site names):

```json
{
  "allSitesKey": "",
  "utmPrefixWildcard": "utm_*",
  "globalTrackedParameters": ["utm_source","utm_medium","utm_campaign","utm_term","utm_content","utm_id","utm_name","utm_reader","utm_viz_id","utm_pubreferrer","fbclid","gclid","dclid","gbraid","wbraid","msclkid","yclid","mc_cid","mc_eid","igshid","twclid","ttclid","li_fat_id","mkt_tok","_hsenc","_hsmi","__twitter_impression","fb_action_ids","fb_action_types","fb_source","mibextid"],
  "hostParameters": {
    "youtube.com": ["si","pp","feature","kw"],
    "youtu.be": ["si","pp","feature","kw"],
    "twitter.com": ["s","t","cn","src","refsrc","ref_src","ref_url"],
    "x.com": ["s","t","cn","src","refsrc","ref_src","ref_url"],
    "instagram.com": ["igsh"],
    "spotify.com": ["si"],
    "reddit.com": ["correlation_id","ref_campaign","ref_source","rdt","share_id","_branch_match_id","$deep_link","$3p","$original_url"],
    "tiktok.com": ["u_code","preview_pb","_d","_t","_r","timestamp","user_id","share_app_name","share_iid"],
    "bilibili.com": ["spm_id_from","from_spmid","from_source","share_source","share_from","share_medium","share_plat","share_tag","share_session_id","msource","refer_from","seid","unique_k","vd_source","plat_id","buvid","bbid","up_id","is_story_h5","timestamp","ts","visit_id","session_id","broadcast_type","is_room_feed"],
    "xiaohongshu.com": ["xhsshare","author_share","xsec_source","share_from_user_hidden","shareredid","share_id","exsource","app_version","app_platform","apptime","appuid"]
  },
  "macOSRewriteSafety": {
    "plainPasteboardTypes": ["public.utf8-plain-text","public.url","public.url-name","NSStringPboardType","NSURLPboardType"],
    "untouchablePasteboardTypes": ["org.nspasteboard.ConcealedType","org.nspasteboard.TransientType"],
    "filePasteboardTypes": ["NSFilenamesPboardType","com.apple.NSFilePromiseItemMetaData","Apple files promise pasteboard type"]
  }
}
```

#### 6.11.2 User rules (stored as a *difference* from the built-ins)

`rules = {added: {site: Set<name>}, disabled: {site: Set<name>}}`, site `""` = all sites.
- `added[""]` = `customParameters(urlCleanerCustomParameters)`: split on `,` and newlines, trim, drop empty, lower-case.
- `added[site]` and `disabled[site]` from `host|name` tokens (`urlCleanerSiteParameters`, `urlCleanerDisabledParameters`): split on `,`/newlines; split each token at the first `|`; site and name lower-cased; empty name dropped. Written back sorted by site then name, comma-joined (`|ref,youtube.com|si`); the global custom list is written as sorted names joined by `", "`.
- Switching off `|utm_*` disables the whole `utm_` prefix.

#### 6.11.3 Cleaning algorithm (byte-exact deletion)

1. Trim whitespace/newlines. Reject (⇒ none) if any whitespace remains inside (a block of several links is never treated as one), or the text does not parse as a URL with scheme http/https **and** a host.
2. Work on the **original Unicode scalars** (never re-encode): fragment starts at the first `#`; query starts at the first `?` before the fragment. No query ⇒ result = trimmed text, nothing removed.
3. Matcher for this host: names = global built-ins without `utm_*` names, minus `disabled[""]`; ∪ `added[""]` minus `disabled[""]`; for every site key `s` (from built-ins, added and disabled keys, excluding `""`) where host == `s` or host ends with `"." + s` (sorted): ∪ (built-ins of `s` minus `disabled[s]`) ∪ (`added[s]` minus `disabled[s]`). The `utm_` **prefix** matches unless `utm_*` is disabled globally. Matching is case-insensitive on the name.
4. Split the raw query on `&` (keeping empty pairs). For each pair, name = the text before the first `=` (the whole pair if no `=`, so a bare `utm_medium` flag goes), percent-decoded if it contains `%` (if decoding fails, the raw text is matched — `utm_%ff` still hits the prefix). Matching pairs are dropped and their decoded names appended to `removed` once each, in link order.
5. Nothing removed ⇒ return the trimmed input unchanged. Else result = text before `?` + (`?` + surviving pairs joined by `&`, or nothing if none survive) + the fragment part unchanged.

Behavior pinned by tests (inputs → outputs):

| Input | Output | Removed |
|---|---|---|
| `https://example.com/path?utm_source=news&id=42&fbclid=abc` | `https://example.com/path?id=42` | utm_source, fbclid |
| ` https://example.com/?GCLID=one&utm_campaign=x#section ` | `https://example.com/#section` | GCLID, utm_campaign |
| `https://example.com/?id=42` | unchanged ("Nothing to clean.") | — |
| `https://www.youtube.com/watch?v=1&si=x&feature=share` (rule `youtube.com\|si` off) | `https://www.youtube.com/watch?v=1&si=x` | feature |
| `https://youtu.be/TImSMeurR84?si=Xq1&t=42` | `https://youtu.be/TImSMeurR84?t=42` | si |
| `https://x.com/user/status/1?s=20&t=abc` | `https://x.com/user/status/1` | s, t |
| `https://example.com/?si=keep&t=keep&s=keep` | unchanged (site rules never leak) | — |
| `https://open.spotify.com/track/abc?si=xyz` | `https://open.spotify.com/track/abc` (subdomains match) | si |
| `https://www.reddit.com/r/swift/comments/abc/?%24deep_link=true&%243p=x&share_id=y&sort=new` | `https://www.reddit.com/r/swift/comments/abc/?sort=new` | $deep_link, $3p, share_id |
| `https://www.bilibili.com/video/BV1xx411c7mD/?p=3&t=90&vd_source=abc` | `https://www.bilibili.com/video/BV1xx411c7mD/?p=3&t=90` | vd_source |
| `https://example.com/?flag&utm_medium&utm_source=news&id=1` | `https://example.com/?flag&id=1` | utm_medium, utm_source |
| `https://example.com/?%75tm_source=news&id=1` | `https://example.com/?id=1` | utm_source |
| `https://example.com/?redirect=https%3A%2F%2Fexample.org%2F%3Futm_source%3Dkeepme%26z%3D9&gclid=1` | same minus `&gclid=1` (wrapped target untouched) | gclid |
| `https://example.com/p?utm_source=x&` | `https://example.com/p?` | utm_source |
| `https://example.com/p#fragment?utm_source=x` | unchanged (that `?` is inside the fragment) | — |
| `https://a.example/x?utm_source=x\nhttps://b.example/y` | none (two links) | — |

Every link with nothing to remove (`https://example.com/?`, `?a=1&a=2`, `?q=a+b&r=%5B%5D&s=%20x`, user:pw@host:port, `[::1]:8443`, Unicode paths with combining marks) is returned **byte for byte**.

#### 6.11.4 "Can the clipboard be rewritten?" (`canRewritePasteboard`)

Non-empty type list and every type survives: plain types (`public.utf8-plain-text`, `public.url`, `public.url-name`, `NSStringPboardType`, `NSURLPboardType`) ⇒ yes; concealed/transient marks or legacy file types ⇒ no; a type unknown to the OS (an app's private note, e.g. a browser's source page) ⇒ yes; a declared type conforming to file URL ⇒ no; conforming to text, URL, RTFD or flat RTFD ⇒ yes; anything else (image, PDF, document, promise…) ⇒ no. Windows equivalent list: §7.2.

#### 6.11.5 Rules-editor data model

`ruleGroups(rules)`: group `""` first with built-ins `["utm_*"] + non-utm globals sorted`, then every site (built-in ∪ added keys ∪ disabled keys) sorted, built-ins sorted then user additions (excluding names that are built-ins) sorted; an entry is enabled unless listed in `disabled[site]`; groups with no entries are omitted. Enabled count = entries not disabled.

### 6.12 Snippet variables (`TextSnippetSupport.expand`)

#### 6.12.1 Algorithm

1. No `{{` in the replacement ⇒ return it unchanged.
2. `dateText` = date in the locale's **medium** date style; `timeText` = **short** time style (en_US: "Jul 8, 2025", "6:40 PM").
3. Literal replacements: `{{date}}` → `dateText`, `{{time}}` → `timeText`, `{{datetime}}` → `dateText + " " + timeText`.
4. Pattern tokens (only if `{{date`, `{{time` or `{{datetime` occurs): scan left to right; at each `{{` find the next `}}`; the chunk between must be one of
   - `{{<kind>-tz(<id>):<pattern>` — `<id>` must resolve as a time zone (else the tag stays literal) and `<pattern>` non-empty;
   - `{{<kind>:<pattern>` — `<pattern>` non-empty (it may contain `:`, e.g. `HH:mm`);
   with `<kind>` ∈ `date`, `time`, `datetime` (tried in that order). Match ⇒ replace the tag (through `}}`) with the date formatted by the ICU/TR35 pattern in that zone (or the device zone) and the user's locale (month/weekday names localized). No match (unknown tag, empty pattern, missing `}}`) ⇒ emit `{{` literally and continue scanning right after it, so a malformed tag never swallows a later valid one. A zone on one token never leaks into the next.
5. Finally `{{clipboard}}` → the clipboard's plain text (or empty). Last, so pasted text is never re-expanded.
- Examples (en_US, 2025‑07‑01 12:30:15 UTC): `{{date:yyyy-MM-dd}}` ⇒ "2025-07-01"; `{{datetime:yyyy}}` ⇒ "2025"; `{{date:MMMM}}` (pt_BR) ⇒ "julho"; `{{datetime-tz(Asia/Tokyo):yyyy-MM-dd HH:mm}}` ⇒ "2025-07-01 21:30"; `{{time-tz(Asia/Tokyo):HH:mm}}|{{time:HH:mm}}` ⇒ "21:30|<device-zone time>"; `{{date:}}`, `{{foo:yyyy}}`, `{{date:yyyy`, `{{unknown}}`, `{{datetime-tz(Not/ARealZone):…}}` stay literal; clipboard containing `{{date:yyyy}}` is inserted literally.

#### 6.12.2 Editor helpers

- `dateToken(text, caret)`: same traversal as step 4; returns the token whose span strictly contains the caret (start < caret < end) with its kind, pattern, zone and character offsets.
- Selection offsets are clamped to the current text (an edit may have shortened it); none ⇒ caret at the end.

#### 6.12.3 Time-zone abbreviations (Foundation's table, used by the builder search)

ADT, AST=America/Halifax; AKDT, AKST=America/Juneau; ART=America/Argentina/Buenos_Aires; BDT=Asia/Dhaka; BRST, BRT=America/Sao_Paulo; BST=Europe/London; CAT=Africa/Harare; CDT, CST=America/Chicago; CEST, CET=Europe/Paris; CLST, CLT=America/Santiago; COT=America/Bogota; EAT=Africa/Addis_Ababa; EDT, EST=America/New_York; EEST, EET=Europe/Athens; GMT=GMT; GST=Asia/Dubai; HKT=Asia/Hong_Kong; HST=Pacific/Honolulu; ICT=Asia/Bangkok; IRST=Asia/Tehran; IST=Asia/Kolkata; JST=Asia/Tokyo; KST=Asia/Seoul; MDT=America/Denver; MSD, MSK=Europe/Moscow; MST=America/Phoenix; NDT, NST=America/St_Johns; NZDT, NZST=Pacific/Auckland; PDT, PST=America/Los_Angeles; PET=America/Lima; PHT=Asia/Manila; PKT=Asia/Karachi; SGT=Asia/Singapore; TRT=Europe/Istanbul; UTC=UTC; WAT=Africa/Lagos; WEST, WET=Europe/Lisbon; WIT=Asia/Jakarta. Known identifiers: the IANA list (443 on the reference Mac).

### 6.13 "Looks sensitive" heuristic (`ClipboardHistorySensitiveText.looksSensitive`)

1. Lower-cased text contains any of `password`, `passwd`, `secret`, `token`, `apikey`, `api_key`, `authorization` ⇒ **sensitive** (any length — even a paragraph mentioning "password").
2. A single http/https URL with a host ⇒ not sensitive.
3. A GUID/UUID (hex groups 8-4-4-4-12, optionally wrapped in `{}`) ⇒ not sensitive.
4. 20–160 characters, no whitespace, contains a letter, a digit and a symbol (neither letter, digit nor whitespace) ⇒ **sensitive**.
5. Otherwise not sensitive.

### 6.14 JSON preview layout (`ClipboardJSONFormat.pretty`)

Only for text ≤ 256 KiB whose first non-whitespace character is `{` or `[` and that a standard JSON parser accepts (trailing commas tolerated if the parser does). Re-layout without re-encoding: walk the characters; inside strings copy verbatim (track `\` escapes); outside strings drop spaces/tabs/CR/LF; `{`/`[` followed (after blanks) by its closer ⇒ emit both on one line; otherwise emit and break the line with `depth+1` × 2 spaces; `}`/`]` ⇒ remove trailing spaces and one trailing newline, break at `depth−1`, emit; `,` ⇒ emit + break; `:` ⇒ `": "`. Abort (show the original text) if the output would exceed 1 MiB. Key order, number spelling (`1.50`, `1e3`) and string escapes are preserved; laying out twice changes nothing.

### 6.15 Small helpers

- **Typed URL** (`typedURL`): the system link detector must match the *whole* trimmed text and report an http/https URL (it rejects plain file names, numbers, e-mail addresses); then keep an explicit http(s) scheme or prefix `https://`; must have a non-empty host.
- **Link placeholders escaping**: for `link` destinations each value is percent-encoded leaving only ASCII letters/digits and `- . _ ~` (`café com leite` ⇒ `caf%C3%A9%20com%20leite`, `a+b` ⇒ `a%2Bb`); `place` destinations are not escaped.
- **Selection preview**: whitespace runs → one space; > 44 chars ⇒ first 44 (trimmed) + "…".
- **Word count**: runs of non-whitespace (CJK text without spaces counts as one word; the character count is shown beside it).
- **Menu shortcut glyphs** (from the menu item's command character and Carbon modifier mask): bit 2 ⌃, bit 1 ⌥, bit 0 ⇧, ⌘ unless bit 3 is set; written ⌃⌥⇧⌘ + upper-cased character.

---

## 7. macOS dependencies → Windows mapping

### 7.1 Focus model and synthetic input (the hardest part of this area)

- **macOS**: every panel here is a *non-activating key panel* — it receives keystrokes while the previous app stays active, so that app's caret and selection never move; synthetic ⌘V or typed characters land there without any focus change.
- **Windows has no equivalent.** A window only gets keyboard input when it is the foreground window. Recommended model for the clipboard quick panel, the Command Bar and the snippet library:
  1. *Before* showing: `prev = GetForegroundWindow()`; `GetGUIThreadInfo(threadOf(prev))` → `hwndFocus`/caret (for diagnostics and edit-control fallbacks); read the selected text now (Command Bar selection rows) — after activation it is gone.
  2. Show the panel and `SetForegroundWindow(panel)` (allowed right after our own hotkey; otherwise use the standard foreground-lock workarounds: `AllowSetForegroundWindow`, a synthesized Alt tap, or `AttachThreadInput`).
  3. On commit: hide the panel → `SetForegroundWindow(prev)` → poll until `GetForegroundWindow() == prev` (≤ 500 ms) → settle 30–60 ms → `SendInput`. Most apps restore their own focus, caret and selection on activation, so paste/typing/selection replacement work as on macOS.
  4. Cancel (Esc/click outside): hide and re-activate `prev` too (macOS never lost it).
- Optional for the **Quick panel** (arrows/digits/Return only): a `WS_EX_NOACTIVATE` window plus a temporary low-level keyboard hook can keep the front app truly active, at the cost of reimplementing key handling; not viable for panels with text fields (IME, dead keys, accessibility).
- **Synthetic paste**: wait until Ctrl/Alt/Shift/Win are physically up (`GetAsyncKeyState`, 15 ms × ≤ 100 — same as macOS), then `SendInput` Ctrl↓ V↓ V↑ Ctrl↑ with `dwExtraInfo = <marker>`. Never inject while Win is still held (releasing Win after injected input can open Start; AutoHotkey's "menu mask key" problem).
- **Typing text**: `SendInput` with `KEYEVENTF_UNICODE` (wVk = 0, wScan = UTF-16 unit), down+up per unit, ≤ 20 units per call, surrogate pairs in the same call; Backspace = `VK_BACK`. Some targets (games, some RDP/VM windows, some Electron builds) mishandle `VK_PACKET` — the multi-line paste path is the fallback.
- **Integrity levels (UIPI)**: input injected into a higher-integrity (elevated) window is silently dropped and low-level hooks do not see keystrokes typed there. Check the target's token integrity (`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `GetTokenInformation(TokenIntegrityLevel)`); if higher than ours, beep + HUD "Can't type into an app running as administrator" (new string) instead of silently failing.
- **"Secure input" / password fields**: no OS-wide flag. Use UI Automation `IsPassword` of the focused element (subscribe with `AddFocusChangedEventHandler` on a background COM thread and cache `focusIsPassword`), plus the fast path `ES_PASSWORD` on classic edit controls. Reset the snippet buffer on every focus change and while `focusIsPassword`; refuse library/Command Bar typing there.

### 7.2 Clipboard

| macOS | Windows |
|---|---|
| `NSPasteboard.general` | `OpenClipboard(hwnd)` / `GetClipboardData` / `EmptyClipboard` / `SetClipboardData` / `CloseClipboard` on the lane thread (owns a message-only window); `OleGetClipboard` for whole-`IDataObject` snapshots |
| `changeCount` | `GetClipboardSequenceNumber()` |
| 0.8 s polling | `AddClipboardFormatListener` → `WM_CLIPBOARDUPDATE` (debounce 50–100 ms); keep the start-up baseline |
| Serial lane + timeouts (lazy data can hang) | Same model. Delayed rendering: `GetClipboardData` sends `WM_RENDERFORMAT` to the owner and can block — never on the UI thread. `OpenClipboard` fails while another process holds it (`ERROR_ACCESS_DENIED`): retry ~10 × 15–30 ms |
| `public.utf8-plain-text` / `NSStringPboardType` | `CF_UNICODETEXT` (`CF_TEXT`, `CF_OEMTEXT`, `CF_LOCALE` are synthesized) |
| `public.url`, URL objects | `UniformResourceLocatorW` / `UniformResourceLocator`, Firefox `text/x-moz-url` (UTF-16 "url\ntitle") |
| `public.html` / `public.rtf` | `HTML Format` (CF_HTML header + UTF-8 fragment) / `Rich Text Format` |
| RTFD with image attachments (rich batch paste) | `HTML Format` with `<img src="data:image/png;base64,…">` **and** `Rich Text Format` with `\pict\pngblip`, plus `CF_UNICODETEXT` fallback (target support varies — verify Word, Outlook, OneNote, browsers) |
| `public.png` / `public.tiff` | registered `PNG` format (Chromium, Office, Snipping Tool) else `CF_DIBV5`/`CF_DIB` → PNG via WIC; when writing an image put `PNG` + `CF_DIB`(V5) |
| File URLs | `CF_HDROP` (`DROPFILES`, wide) + `Preferred DropEffect` = `DROPEFFECT_COPY`; read with `DragQueryFileW` |
| File promises | `FileGroupDescriptorW` + `FileContents` (virtual files, e.g. mail attachments), `Shell IDList Array` — never recorded as text, never rewritten |
| `org.nspasteboard.ConcealedType` | `ExcludeClipboardContentFromMonitorProcessing` (presence), `CanIncludeInClipboardHistory` = DWORD 0, `Clipboard Viewer Ignore` (KeePass-era convention) ⇒ do not read |
| `org.nspasteboard.TransientType` | same formats; **set** `ExcludeClipboardContentFromMonitorProcessing` + `CanIncludeInClipboardHistory = 0` + `CanUploadToCloudClipboard = 0` on our transient writes and restores so Win+V history and other managers ignore them |
| `org.nspasteboard.source` (writer id) | `GetClipboardOwner()` → `GetWindowThreadProcessId` → app identity (§5.4); our own writes add a private registered format `Vorssaint.Source` |
| `com.apple.is-remote-clipboard` | Windows cloud clipboard sync has no documented marker — treat as local (verify whether the owner is the clipboard user service) |
| Rewrite-safety type list (§6.11.4) | Allowed: `CF_UNICODETEXT`, `CF_TEXT`, `CF_OEMTEXT`, `CF_LOCALE`, `UniformResourceLocator(W)`, `text/x-moz-url`, `Rich Text Format`, `HTML Format` (with the HTML checks), known browser source-URL notes (e.g. Chromium's internal source URL format, Edge `msSourceUrl`). Refused: `CF_HDROP`, file descriptors/contents, `Shell IDList Array`, every image/metafile format, OLE/Office formats (`Embed Source`, `Object Descriptor`, `Link Source`, `Native`, `OwnerLink`, `Biff*`, `XML Spreadsheet`, `Csv`…), the exclusion formats. **Unknown registered formats ⇒ refuse** (stricter than macOS; Windows apps keep real content in private formats) |
| Auto clear | `OpenClipboard` + `EmptyClipboard`. It does not delete items already in Windows' own clipboard history (Win+V); optionally offer `Clipboard.DeleteItemFromHistory/ClearHistory` (WinRT, check desktop-app availability) |
| Transient snapshot (all items × types) | Enumerate formats (`EnumClipboardFormats`), copy HGLOBAL data; skip synthesized formats; GDI handles (`CF_BITMAP`, `CF_ENHMETAFILE`, `CF_PALETTE`) need explicit copies; any format that cannot be preserved ⇒ fail open. Large Office copies can be huge — consider a size cap (fail open above it) |

### 7.3 OS settings panes → `ms-settings:` (the 51 panes found on the reference Mac)

Keep the source id `macSettings` (persisted) and use row ids `macsettings.<uri>`; label the source "Windows Settings pages". Keywords: ship a localized keyword list per row (optionally enriched from `%windir%\ImmersiveControlPanel\Settings\AllSystemSettings_*.xml`, undocumented).

| macOS pane (bundle id) | Windows row | URI / command |
|---|---|---|
| About (`com.apple.SystemProfiler.AboutExtension`) | About | `ms-settings:about` |
| Accessibility (`com.apple.Accessibility-Settings.extension`) | Accessibility | `ms-settings:easeofaccess` |
| AirDrop & Handoff (`com.apple.AirDrop-Handoff-Settings.extension`) | Nearby sharing / Shared experiences | `ms-settings:crossdevice` |
| Appearance (`com.apple.Appearance-Settings.extension`) | Colors (light/dark mode) | `ms-settings:colors` |
| Apple Account (`com.apple.systempreferences.AppleIDSettings`) | Your info / Accounts | `ms-settings:yourinfo` |
| Background Security Improvements (`com.apple.SecurityImprovements-Settings.extension`) | Windows Security | `windowsdefender:` |
| Bluetooth (`com.apple.BluetoothSettings`) | Bluetooth & devices | `ms-settings:bluetooth` |
| CDs & DVDs (`com.apple.CD-DVD-Settings.extension`) | AutoPlay | `ms-settings:autoplay` |
| Class Progress (`com.apple.ClassKit-Settings.extension`) | — (drop) | — |
| Classroom (`com.apple.Classroom-Settings.extension`) | — (drop) | — |
| Control Center (`com.apple.ControlCenter-Settings.extension`) | Taskbar | `ms-settings:taskbar` |
| Coverage (`com.apple.Coverage-Settings.extension`) | — (drop; or About) | — |
| Date & Time (`com.apple.Date-Time-Settings.extension`) | Date & time | `ms-settings:dateandtime` |
| Desktop & Dock (`com.apple.Desktop-Settings.extension`) | Multitasking / Taskbar | `ms-settings:multitasking` |
| Device Management (`com.apple.Profiles-Settings.extension`) | Access work or school | `ms-settings:workplace` |
| Displays (`com.apple.Displays-Settings.extension`) | Display | `ms-settings:display` |
| Family (`com.apple.Family-Settings.extension`) | Family | `ms-settings:family-group` |
| Focus (`com.apple.Focus-Settings.extension`) | Focus / Do not disturb | `ms-settings:quiethours` |
| FollowUp (`com.apple.FollowUpSettings.FollowUpSettingsExtension`) | — (drop) | — |
| Game Center (`com.apple.Game-Center-Settings.extension`) | Gaming | `ms-settings:gaming-gamebar` |
| Game Controllers (`com.apple.Game-Controller-Settings.extension`) | Game controllers | `joy.cpl` |
| General (`com.apple.systempreferences.GeneralSettings`) | Settings home | `ms-settings:` |
| Headphones (`com.apple.HeadphoneSettings`) | Sound / Bluetooth devices | `ms-settings:sound` |
| Internet Accounts (`com.apple.Internet-Accounts-Settings.extension`) | Email & accounts | `ms-settings:emailandaccounts` |
| Keyboard (`com.apple.Keyboard-Settings.extension`) | Typing | `ms-settings:typing` |
| Language & Region (`com.apple.Localization-Settings.extension`) | Language & region | `ms-settings:regionlanguage` |
| Lock Screen (`com.apple.Lock-Screen-Settings.extension`) | Lock screen | `ms-settings:lockscreen` |
| Login Items (`com.apple.LoginItems-Settings.extension`) | Startup apps | `ms-settings:startupapps` |
| Mouse (`com.apple.Mouse-Settings.extension`) | Mouse | `ms-settings:mousetouchpad` |
| Network (`com.apple.Network-Settings.extension`) | Network & internet | `ms-settings:network-status` |
| Notifications (`com.apple.Notifications-Settings.extension`) | Notifications | `ms-settings:notifications` |
| Battery (`com.apple.Battery-Settings.extension`) | Power & battery | `ms-settings:powersleep` (battery saver: `ms-settings:batterysaver`) |
| Printers & Scanners (`com.apple.Print-Scan-Settings.extension`) | Printers & scanners | `ms-settings:printers` |
| Privacy & Security (`com.apple.settings.PrivacySecurity.extension`) | Privacy & security | `ms-settings:privacy` |
| Screen Time (`com.apple.Screen-Time-Settings.extension`) | Family (screen time) | `ms-settings:family-group` |
| Sharing (`com.apple.Sharing-Settings.extension`) | Remote Desktop / Nearby sharing | `ms-settings:remotedesktop` |
| Siri (`com.apple.Siri-Settings.extension`) | — (drop) | — |
| Software Update (`com.apple.Software-Update-Settings.extension`) | Windows Update | `ms-settings:windowsupdate` |
| Sound (`com.apple.Sound-Settings.extension`) | Sound | `ms-settings:sound` |
| Spotlight (`com.apple.Spotlight-Settings.extension`) | Searching Windows | `ms-settings:cortana-windowssearch` |
| Startup Disk (`com.apple.Startup-Disk-Settings.extension`) | — (drop) | — |
| Storage (`com.apple.settings.Storage`) | Storage | `ms-settings:storagesense` |
| Time Machine (`com.apple.Time-Machine-Settings.extension`) | Backup | `ms-settings:backup` |
| Touch ID & Password (`com.apple.Touch-ID-Settings.extension`) | Sign-in options | `ms-settings:signinoptions` |
| Trackpad (`com.apple.Trackpad-Settings.extension`) | Touchpad | `ms-settings:devices-touchpad` |
| Transfer or Reset (`com.apple.Transfer-Reset-Settings.extension`) | Recovery | `ms-settings:recovery` |
| Users & Groups (`com.apple.Users-Groups-Settings.extension`) | Other users | `ms-settings:otherusers` |
| VPN (`com.apple.NetworkExtensionSettingsUI.NESettingsUIExtension`) | VPN | `ms-settings:network-vpn` |
| Wallet & Apple Pay (`com.apple.WalletSettingsExtension`) | — (drop) | — |
| Wallpaper (`com.apple.Wallpaper-Settings.extension`) | Background | `ms-settings:personalization-background` |
| Wi-Fi (`com.apple.wifi-settings-extension`) | Wi-Fi | `ms-settings:network-wifi` |

Windows-only rows worth adding: Apps (`ms-settings:appsfeatures`), Default apps (`ms-settings:defaultapps`), Personalization (`ms-settings:personalization`), Themes (`ms-settings:themes`), Start (`ms-settings:personalization-start`), Clipboard (`ms-settings:clipboard`), Night light (`ms-settings:nightlight`), Airplane mode (`ms-settings:network-airplanemode`), Mobile hotspot (`ms-settings:network-mobilehotspot`), Ethernet (`ms-settings:network-ethernet`), Proxy (`ms-settings:network-proxy`), Camera privacy (`ms-settings:privacy-webcam`), Microphone privacy (`ms-settings:privacy-microphone`), Location (`ms-settings:privacy-location`), Optional features (`ms-settings:optionalfeatures`), For developers (`ms-settings:developers`), Activation (`ms-settings:activation`), Speech (`ms-settings:speech`), Fonts (`ms-settings:fonts`), Pen (`ms-settings:pen`), USB (`ms-settings:usb`), Graphics (`ms-settings:display-advancedgraphics`), Troubleshoot (`ms-settings:troubleshoot`), and classic applets: Sound devices (`mmsys.cpl`), Network connections (`ncpa.cpl`), Programs and Features (`appwiz.cpl`), Device Manager (`devmgmt.msc`), Power Options (`powercfg.cpl`). Validate every URI on Windows 10 22H2 and Windows 11 24H2 (several redirect differently).

### 7.4 Shortcuts

**Global defaults** (rule: Win-based combos for global hotkeys; avoid Ctrl+Alt+letter, which is AltGr on many European layouts and would steal typed characters such as `@`, `ł`, `€`):

| Feature | macOS default | Suggested Windows default | Conflicts to check |
|---|---|---|---|
| Command Bar | ⌥Space | Alt+Space | Shadows the window system menu (same choice as PowerToys Run); offer Win+Alt+Space |
| Clipboard history | ⌃⌥⌘V | Win+Alt+V | Win+V is the OS clipboard history (shell-reserved); Ctrl+Alt+V = Office Paste Special |
| Paste as plain text | ⇧⌥⌘V | Win+Alt+Shift+V | PowerToys: Win+Ctrl+Alt+V (plain), Win+Shift+V (Advanced Paste); Ctrl+Shift+V is per-app plain paste — never steal it |
| Quick panel | ⌃⌘V | Win+Alt+Q | Win+Ctrl+V = Windows 11 sound-output flyout |
| Snippet library | ⌃⌥⌘L | Win+Alt+S | Win+L (lock) and Win+Alt+K (call mic mute) nearby; verify |

Registration failure ⇒ "Windows rejected this shortcut. Choose another one." The macOS "take over a system shortcut" flow has no Windows equivalent (drop).

**In-panel keys**: ⌘ → Ctrl, ⌥ → Alt, ⌃N/⌃P stay Ctrl+N/Ctrl+P, therefore Command Bar **pin moves from ⌘P to Alt+P** (same as the clipboard panel's ⌥P). ⌘1…⌘9 → Ctrl+1…Ctrl+9 (holding Ctrl shows the badges); ⌘K → Ctrl+K; ⌘, → Ctrl+,; ⌘Return → Ctrl+Enter ("Show in File Explorer" in the bar; "toggle selection" in the clipboard panel); ⌥⌫ → Alt+Backspace / Alt+Delete; ⌘⌫ → Ctrl+Delete (with a selection); ⌘A/C/X/V → Ctrl+A/C/X/V; ⌘Q/W/M/H swallowing → Alt+F4 hides the panel instead of closing the app.

### 7.5 Quick toggles

| Toggle | Windows implementation | Current state | Notes |
|---|---|---|---|
| Dark mode | `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`: set **both** `AppsUseLightTheme` and `SystemUsesLightTheme` (DWORD 0 = dark, 1 = light), then `SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 100 ms)` | `AppsUseLightTheme` | Shell updates live; some Win32 apps only on restart |
| Keyboard light | No standard API (OEM WMI interfaces; Windows 11 Dynamic Lighting `LampArray` for RGB keyboards) | — | Hide the row when unsupported (as macOS does) |
| Mic mute | `IAudioEndpointVolume::SetMute` on the default capture endpoints (Mic Mute spec) | `GetMute` | |
| Empty Recycle Bin | Our own confirmation, then `SHEmptyRecycleBinW(NULL, NULL, SHERB_NOCONFIRMATION │ SHERB_NOPROGRESSUI │ SHERB_NOSOUND)`; `SHQueryRecycleBinW` for "already empty"/size | `SHQueryRecycleBin` | No permission state (drop *needs permission*); rename strings to "Recycle Bin" |
| Eject all disks | Enumerate volumes; qualify `DRIVE_REMOVABLE`, `DRIVE_CDROM`, or `DRIVE_FIXED` on bus USB/1394/SD/MMC (`IOCTL_STORAGE_QUERY_PROPERTY` → `BusType`); never the system drive, `DRIVE_REMOTE`, or the page-file volume; eject the parent device via `CM_Request_Device_EjectW` (veto ⇒ failed) or lock/dismount/`IOCTL_STORAGE_EJECT_MEDIA` | qualifying count | Exclusions match label, volume GUID path, drive root (`E:\`) or serial, case-insensitive |
| Hidden files | `HKCU\…\Explorer\Advanced` `Hidden` = 1 show / 2 hide (leave `ShowSuperHidden` alone), then refresh open Explorer windows (`SHChangeNotify(SHCNE_ASSOCCHANGED)` + refresh each `IShellWindows` view) | `Hidden` | No restart; caption → "Open File Explorer windows refresh to apply it." |
| Desktop icons | `HKCU\…\Explorer\Advanced` `HideIcons` = 1/0, apply live by sending `WM_COMMAND 0x7402` to the desktop `SHELLDLL_DefView` (under `Progman`/`WorkerW`; undocumented), fallback restart `explorer.exe` | `HideIcons` | |
| Lock | `LockWorkStation()` | — | |
| Display off | `PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2)` | — | Modern Standby devices may enter standby |
| Screen saver | `SendMessage(<own hidden window>, WM_SYSCOMMAND, SC_SCREENSAVE, 0)` | — | Needs a configured saver (`SPI_GETSCREENSAVEACTIVE`, `SCRNSAVE.EXE`); none ⇒ failed |

### 7.6 Command Bar providers and system services

| macOS API / mechanism | Windows replacement |
|---|---|
| App scan (`/Applications`, `~/Applications`, `/System/Applications`, Spotlight `mdfind` in home) + alternate names | Enumerate `shell:AppsFolder` (`FOLDERID_AppsFolder`) via `IShellItem`: Win32 shortcuts + packaged apps with AUMIDs; alternate names from shortcut names, package display names, `FileDescription`/`ProductName` |
| `openApplication` / activate | Packaged: `IApplicationActivationManager::ActivateApplication`; Win32: `ShellExecuteEx` on the shortcut / `shell:AppsFolder\<AUMID>`; running ⇒ bring its main window forward |
| Running apps (`NSRunningApplication`, regular policy) | `EnumWindows` (visible, unowned, not tool windows, not cloaked `DWMWA_CLOAKED`) grouped by process/AUMID (`SHGetPropertyStoreForWindow` → `PKEY_AppUserModel_ID`) |
| Quit (polite) / Force quit / Restart / Hide | `WM_CLOSE` to the app's top-level windows / `TerminateProcess` (after confirmation) / close → wait for process exit ≤ 60 s → relaunch / minimize its windows |
| Window list + activation (Accessibility, window server) | `EnumWindows` + `GetWindowTextW` (no permission needed); `ShowWindow(SW_RESTORE)` + `SetForegroundWindow`; current-desktop filter `IVirtualDesktopManager::IsWindowOnCurrentVirtualDesktop` |
| Menu bar walk + `AXPress` | Classic apps: `GetMenu(hwnd)` + `GetMenuItemInfoW` (string, id, submenu, state; accelerator text after `\t` = shortcut) and `PostMessage(WM_COMMAND, id)`; fallback UI Automation (MenuBar/MenuItem, Expand/Invoke) on a worker thread with timeouts. Ribbon/UWP/WinUI/Electron menus are mostly unreachable — coverage will be partial |
| Selected text (`AXSelectedText`) | UIA focused element → `TextPattern.GetSelection()` → `GetText(20000)`; classic edit fallback `EM_GETSEL` + `WM_GETTEXT`; never via synthetic Ctrl+C |
| Finder selection (uninstall row) | Selected items of the foreground Explorer window (`IShellWindows` → `IFolderView2`) |
| Reveal in Finder | `SHOpenFolderAndSelectItems` |
| Standard folders | Known folders: Downloads, Documents, Desktop, Videos, Pictures, Music, Profile; replace `/Applications` with "Apps" (`shell:AppsFolder`) |
| Spotlight `MDQuery` file search | Windows Search OLE DB (`Provider=Search.CollatorDSO`): `SELECT TOP 1000 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:<folder>' AND System.FileName LIKE '%<word>%' AND …` (+ diacritic-fold post-filter); non-indexed scopes ⇒ Everything SDK if installed, else a bounded cancellable `FindFirstFileExW` walk (≤ 1000 hits). "Package" exclusion has no Windows meaning (drop); hidden = `FILE_ATTRIBUTE_HIDDEN`/`SYSTEM` or dot-names |
| ExtensionKit pane scan + `x-apple.systempreferences:` | Static table §7.3 + `ShellExecute` of the URI |
| Battery / memory / storage | `GetSystemPowerStatus` / `GlobalMemoryStatusEx` / `GetDiskFreeSpaceExW(%SystemDrive%)` |
| Wi-Fi power (CoreWLAN) | `Windows.Devices.Radios` (`RequestAccessAsync`, radio kind WiFi, `SetStateAsync`) or WLAN API radio state |
| Sleep (`pmset sleepnow`) / Restart / Shut down / Log out (Apple Events) | `SetSuspendState(FALSE, FALSE, FALSE)` / `ExitWindowsEx(EWX_REBOOT │ EWX_POWEROFF │ EWX_LOGOFF)` with `SE_SHUTDOWN_NAME` — Windows shows **no** extra confirmation, so the in-bar confirmation is mandatory |
| Volume / mute / output device | `IAudioEndpointVolume` (`SetMasterVolumeLevelScalar`, `SetMute`); default-output switching only via undocumented `IPolicyConfig` (risk) |
| Brightness | WMI `WmiMonitorBrightnessMethods` (internal) / DDC-CI `SetMonitorBrightness` (Brightness spec) |
| Emoji names & properties (ICU via Foundation) | ICU in Windows (`icu.dll`, Windows 10 1903+: `u_charName`, `u_hasBinaryProperty(UCHAR_EMOJI…)`) or — preferred for determinism — a JSON table generated at build time from Unicode data |
| Pinyin keywords (`CFStringTransform`) | ICU transliterator `Han-Latin; Latin-ASCII` (`utrans_*`) or drop |
| `NSDataDetector` (typed URL) | Custom matcher: optional http(s) scheme, host labels with a public-suffix/IANA TLD, optional port/path/query; reject e-mail addresses and plain file names |
| `DateFormatter` styles, ICU date patterns, `MeasurementFormatter`, `DateComponentsFormatter`, `NumberFormatter` | ICU (`udat_*` styles and TR35 patterns, `unumf` with unit skeletons, number formatter) — required to keep snippet tokens and Command Bar output identical; .NET/Win32 formatters use different pattern languages |
| IANA time zones | ICU (`ucal_openTimeZones`) or .NET `TimeZoneInfo` with IANA ids |
| Locale separators | `GetLocaleInfoEx(LOCALE_SDECIMAL / LOCALE_STHOUSAND)` |
| Input source switch (TIS, ASCII layout) | `LoadKeyboardLayoutW("00000409")` + `ActivateKeyboardLayout` (or `WM_INPUTLANGCHANGEREQUEST` to our window); restore on close |
| Script runner (`Process`, merged output, 5 s, 64 KiB) | `CreateProcessW` with redirected pipes, `CREATE_NO_WINDOW`, a Job object (kill on timeout/close). Dispatch: `.exe/.com` direct; `.bat/.cmd` via `cmd.exe /d /s /c` (escape `^ & │ < > % !` in the argument); `.ps1` via `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`; others refused. Quote the argument per `CommandLineToArgvW` rules |

### 7.7 Other services

| macOS | Windows |
|---|---|
| `CGEventTap` keyboard/mouse (snippets) | `SetWindowsHookEx(WH_KEYBOARD_LL)` + `WH_MOUSE_LL` on a dedicated thread with a message loop. The OS silently removes a hook that exceeds `LowLevelHooksTimeout` — keep the callback O(1) and add a watchdog. Characters via `ToUnicodeEx(vk, scan, state, buf, 4, flags = 0x4, hkl)` (flag 0x4 = don't disturb dead-key state, Windows 10 1607+; `hkl` of the foreground thread); AltGr = Ctrl+Alt producing a character must count as typing, not a shortcut; IME input arrives as `VK_PROCESSKEY` (reset buffer) |
| Event marker (`eventSourceUserData`) | `dwExtraInfo` magic value (+ `LLKHF_INJECTED`) |
| Carbon `RegisterEventHotKey` | `RegisterHotKey(hwnd, id, MOD_… │ MOD_NOREPEAT, vk)` |
| App-activation notification | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` |
| Global mouse-down monitor (dismiss) | Deactivation of our activated panel (`WM_ACTIVATE`/`WA_INACTIVE`), or `WH_MOUSE_LL` for a non-activating variant |
| Accessibility Keyboard exception | On-Screen Keyboard (`osk.exe`) and touch keyboard windows: clicks there neither dismiss panels nor reset the snippet buffer |
| Floating `NSPanel` (all Spaces, over full screen) | `WS_EX_TOPMOST │ WS_EX_TOOLWINDOW` (no taskbar button), DWM rounded corners + Mica/Acrylic backdrop; works over borderless full-screen apps, not exclusive full screen |
| Pointer screen visible frame | `MonitorFromPoint(GetCursorPos())` + `GetMonitorInfo(rcWork)`; per-monitor DPI v2 |
| Sleep / display sleep / lock notifications | `WM_POWERBROADCAST PBT_APMSUSPEND` (or `RegisterSuspendResumeNotification`), `RegisterPowerSettingNotification(GUID_CONSOLE_DISPLAY_STATE)` value 0, `WTSRegisterSessionNotification` → `WTS_SESSION_LOCK` |
| Session active (fast user switching) | `WTS_CONSOLE_DISCONNECT/CONNECT`, `WTS_SESSION_LOCK/UNLOCK` |
| System alert sounds (`/System/Library/Sounds/*.aiff`, default Tink) | `PlaySoundW(..., SND_ASYNC │ SND_FILENAME)` with `%windir%\Media\*.wav` (picker), default "Windows Ding" |
| `NSSound.beep()` | `MessageBeep(MB_OK)` |
| HUD toast | Topmost, click-through (`WS_EX_LAYERED │ WS_EX_TRANSPARENT │ WS_EX_NOACTIVATE`) window |
| Status item text (latest copy) | Tray icons cannot show text: tray tooltip (`NIF_TIP`, 127 chars) and/or a small topmost "pill" window near the tray |
| `UserDefaults` / Application Support | settings JSON or `HKCU\Software\Vorssaint` / `%LOCALAPPDATA%\Vorssaint` |
| CryptoKit SHA-256, HMAC, random key | CNG (`BCryptHash`, HMAC flag, `BCryptGenRandom`) |
| ImageIO thumbnails, PNG/TIFF conversion | WIC (`IWICImagingFactory`, `IWICBitmapScaler`) |
| `NSWorkspace.icon(forFile:)` | `IShellItemImageFactory::GetImage` / `SHGetFileInfoW` |
| Vision OCR (history image "Copy text") | `Windows.Media.Ocr.OcrEngine` (Screen OCR spec) |
| Menu-item key-equivalent probe (Paste and Match Style) | No universal Windows convention (Ctrl+Shift+V means different things per app; in Word it pastes *formatting*). Drop; always use strip-and-restore |

---

## 8. Porting notes

### 8.1 Drop list (macOS-only, do not port)

- Dynamic Island ("notch") routing of the clipboard window, quick panel and Command Bar; the Command Bar "droplet"/"island" presentations and the mascot companion (`notchClipboard`, `notchClipboardWindow`, `notchCommandBar`, `notchCommandBarStyle`, `CommandBarDroplet*`).
- Accessibility / Automation permission prompts and the Finder-automation *needs permission* state of Empty Trash (Windows needs no grant; keep only the UIPI/elevation handling of §7.1).
- Pressing the front app's own "Paste and Match Style" (⌥⇧⌘V) menu item.
- Finder restart for hidden files / desktop icons (Explorer applies live).
- macOS "take over a system shortcut" offers (`SystemShortcutTakeover`).
- Spotlight-based app discovery, ExtensionKit pane scanning, `.searchTerms` keyword reading.
- Universal Clipboard "remote" marker, `org.nspasteboard.*` conventions (replaced by §7.2 formats).
- Screenshot-temp-file special case in capture (only if the Windows Screenshot feature copies files instead of bitmaps).
- "Show in Finder" wording → "Show in File Explorer"; "Trash" → "Recycle Bin"; "Mac" → "PC" in captions.

### 8.2 Deliberate Windows deviations (recommended)

1. Clipboard source attribution via `GetClipboardOwner()` first, foreground-history heuristic second.
2. Run URL cleaning *before* history capture in the same clipboard-update handler (avoid recording the tracked link too).
3. Treat unknown registered clipboard formats as content (refuse automatic URL rewriting), stricter than macOS.
4. Mark every transient write/restore with the exclusion formats so Windows' own clipboard history and other managers ignore it.
5. Snippet engine: password-field detection through UIA (no OS secure-input flag), integrity-level check before injecting.
6. Global hotkey defaults on the Win key, never Ctrl+Alt+letter (§7.4); Command Bar pin on Alt+P.
7. Optional DPAPI encryption of `ClipboardHistory.json` and the image store.
8. Command Bar "menu commands" limited to classic `HMENU` apps (+ best-effort UIA); hide the Menus chip when the front app exposes none.

### 8.3 Risks

**Top five**

1. **No non-activating key panels on Windows.** Every "act at the caret" flow (history paste, Command Bar typing/paste/case change/menu press, snippet library) relies on macOS panels that take keystrokes without stealing focus. Windows needs activate → restore-previous-foreground → inject, with foreground-lock rules, timing races and apps that do not restore caret/selection (§7.1). Selection must be read before the bar activates.
2. **Typed-trigger expansion through a low-level keyboard hook.** Character translation (dead keys, AltGr, IME, per-thread layouts), the OS silently removing slow hooks, UIPI blocking injection/observation for elevated windows, and *no* system "secure input" — password-field detection must be built with UI Automation or the engine leaks password keystrokes into its buffer.
3. **Clipboard fidelity and privacy conventions.** Delay-rendered/OLE/GDI formats make snapshot-and-restore (plain-text paste, multi-line snippets) lossy or blocking; secret detection relies on `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory` / `Clipboard Viewer Ignore`; interplay with Windows' own clipboard history and cloud sync; `OpenClipboard` contention; private formats make "is this rewrite lossless?" harder than on macOS.
4. **System-UI reach is much less uniform.** Menu-command search (AX menu bars) and selected-text reading (AXSelectedText) map to `HMENU` and UI Automation, which cover classic apps but not ribbons/UWP/WinUI/Electron; UIA calls can hang and need worker threads with timeouts. Quick toggles depend on undocumented Explorer messages (desktop icons), registry + broadcast (dark mode), and device ejection via Configuration Manager.
5. **ICU-dependent semantics.** Snippet tokens persist ICU/TR35 date patterns (with U+202F) and IANA zones; the calculator, unit, color and date answers, emoji names, pinyin and search folding all lean on Foundation/ICU behavior. Windows-native formatters use other pattern languages, so parity (and settings-backup interchange) requires ICU (`icu.dll` on Windows 10 1903+ or bundled) plus golden tests from §8.5.

**Others**

| Risk | Why | Mitigation |
|---|---|---|
| Foreground restoration races | `SetForegroundWindow` restrictions; target app slow to re-activate; paste lands in the wrong window | Poll for activation, cap 500 ms, abort with beep; never paste if the foreground is still us |
| Keyboard hook fidelity | dead keys, AltGr, IME, CapsLock, per-thread layouts; hook removal on timeout | `ToUnicodeEx` with flag 0x4, foreground thread HKL, watchdog, minimal work in the callback |
| Clipboard snapshot fidelity | OLE / delayed-render / GDI formats, huge Office payloads | Fail open; size cap; copy only HGLOBAL formats; skip synthesized formats |
| Rich batch paste with images | No RTFD; HTML data-URI and RTF `\pngblip` support varies by target | Write both + plain text; test Word, Outlook, OneNote, Teams, browsers |
| Hotkey conflicts | Shell-reserved Win combos, PowerToys, AltGr layouts, Office | Conservative defaults, clear failure text, recorder |
| ICU-dependent formatting/parsing | Snippet tokens store TR35 patterns (incl. U+202F) and IANA zones; units/dates/calculator rely on locale data | Use Windows `icu.dll` (1903+) or bundle ICU; golden tests vs. macOS outputs |
| Search folding parity | Swift's case/diacritic/width folding vs. NFKD-based folding | One shared fold function, tests from §6 |
| File search coverage | Windows Search indexes only indexed locations; diacritics not folded in `LIKE` | Fallbacks (Everything SDK / bounded walk), post-filter |
| Menu-command search | No uniform menu model on Windows | Scope to HMENU apps; time-boxed UIA |
| Default audio output switching | Only undocumented `IPolicyConfig` | Isolate behind an interface; feature-flag |
| Scripts | `.bat`/`cmd.exe` metacharacter injection from the typed argument; PowerShell execution policy | Strict quoting/escaping; run without a shell when possible; Job object timeouts |
| Settings backup interchange | macOS key codes and bundle ids inside shortcuts, ignored apps, pins, aliases | Translate on import (§5.3), drop unresolvable ids |
| Eject | Locked volumes, veto by open handles, BitLocker | Report veto as failure; never force-dismount |

### 8.4 Suggested implementation order

1. **Infrastructure**: settings store with §4 defaults; feature-hub availability flags; hotkey registry (+ suspend while recording); clipboard lane thread (listener, sequence number, retries, timeouts, admission); SendInput layer (modifier wait, paste, Unicode typing, marker); foreground save/restore; floating panel base (topmost tool window, placement helpers, dismiss rules); HUD; shared text-fold function; ICU wrapper (dates, numbers, units, time zones).
2. **Clipboard history core**: capture (text/image/files), dedup, ordering, pins, limits & budgets, JSON + image store, search; quick panel with keyboard map and paste-into-previous-app; tray tab.
3. **Auto clear** and **Paste as plain text** (transient paste with snapshot/restore).
4. **Clean URL**: engine with §6.11 golden tests → manual surfaces → rules editor → automatic watcher.
5. **Text snippets**: model/editor/storage → expansion engine (hook, buffer, matching, typing/paste paths, password guard) → variables → date builder → sound → **snippet library**.
6. **Command Bar core**: panel + modes + keyboard map; ranking (§6.3) with tests; catalog actions & generated toggles; settings pages; apps; windows; folders; answers; calculator, units, colors, dates; emoji; clipboard and snippet rows; home list, chips, compact mode; pins/aliases/hidden/sources; usage + session learning.
7. **Command Bar extended**: selection rows (UIA), files (Windows Search), saved links/places/searches/scripts, row shortcuts + App shortcuts center, OS settings table, power & Wi-Fi, kill/uninstall integrations, menu commands.
8. **Quick toggles**, then the **Quick panel** (tiles depend on other specs; ship with the tiles whose features exist).
9. Polish: preview sidebar (JSON, edit, OCR), rich batch paste, menu-bar/tray latest-copy chip, drag-to-move bar, ASCII layout, emoji skin tones.

### 8.5 Behavior to lock with golden tests (ported from the Swift test-suite)

- `Tests/CommandBarFeatureTests.swift`: calculator (all cases in §6.6, incl. locale separators, percent rules, virtual closers, reuse), color conversions and round trips, unit conversions (incl. feet+inches, separators per region), date phrases and the "must stay a search" list, link placeholders/trailing arguments, source ids, ranking contracts, panel clamping.
- `Tests/CommandBarEmojiTests.swift`: skin-tone rules (👪 excluded), one-off tones, learning isolation.
- `Tests/RepositoryFeatureTests.swift` (URL cleaning section): every input/output pair in §6.11.3, rule storage round trips, rule groups.
- `Tests/UtilitiesFeatureTests.swift`: snippet trigger sanitizing, buffer cap, matching (longest wins, modes don't cross), variable expansion incl. timezone tokens and literal fall-through, sound-name resolution.
- `Tests/ClipboardFeatureTests.swift`, `ClipboardHistoryWriteTests.swift`, `ClipboardHistoryAccessTests.swift`: search ranking, JSON layout, menu-bar text, auto-clear `decide` table, change-counter acceptance (server restart), capture admission/timeouts, write result semantics, reuse ordering, pin size refusal, clear-only-counted, source attribution cases (§3.2.9), preferred-text rule for separated links.
- `Tests/PastePlainTests.swift`: media types bypass conversion; ⌘V hotkey release/re-register; no overlapping forwards.
- `Tests/QuickLauncherActionTests.swift`, `Tests/QuickTogglesAlertTests.swift`: every tile's activation contract (dismiss-then-act vs. in-place vs. hosted), presentation reset, Trash confirmation semantics.
