# Clipboard (clipboardHistory, pastePlain, urlCleaner)

Spec: `docs/specs/06-clipboard-snippets-commandbar.md` §2.1–§2.4, §3.1–§3.5, §5.1–§5.2, §6.1–§6.2, §6.11, §6.13–§6.14, §7.1–§7.2.

## Where the code lives

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/Clipboard/` — lane (`ClipboardLane`), watcher and acceptance rule (`ClipboardWatcher`), entry model, history list (dedup, pins, limits, budgets), JSON + image store (`ClipboardHistoryFile`), search ranking, sensitive-text heuristic and JSON preview (`ClipboardRules`), rich text (RTF/HTML), text folding, history service, auto clear, transient paste, paste as plain text, URL cleaning engine (verbatim rule data) and automatic cleaner, synthetic input and "act at the caret" helpers (`CaretActions`, `TextInserter`) shared with Snippets and the Command Bar |
| Windows | `src/Rivet.Platform.Windows/Clipboard/` — `WindowsClipboardPlatform` (own lane thread + message-only window, `AddClipboardFormatListener`, all reads/writes), `WindowsForegroundService` (foreground tracking, restore, UIPI/integrity check, password-field detection), `WindowsSessionEvents` (sleep, display off, lock), `ProcessIdentity` |
| Fake | `src/Rivet.Platform.Fake/Clipboard/FakeClipboardPlatform.cs` |
| App | `src/Rivet.App/Features/Clipboard/` — `ClipboardModule`, `ClipboardHistoryWindow`, `ClipboardPanelView`, `UrlCleanerView`, settings pages, `FloatingPanel` (base of every floating panel of this area), `ClipboardUi` |
| Tests | `tests/Rivet.Core.Tests/Clipboard/` (history, dedup, retention, budgets, persistence, URL cleaning vectors), `tests/Rivet.App.Tests/Clipboard/` (snapshots) |

## Implemented

* **Capture** on `WM_CLIPBOARDUPDATE` (dedicated STA thread with a message-only window; `GetClipboardSequenceNumber` as the change counter). Text (with the spec's text-preference rule), RTF and HTML kept as secondary formats, images (PNG preferred, then DIB/DIBV5; stored as files), files (`CF_HDROP`), links and colours as derived kinds. Dedup, move-to-top, pins, limit choices (20…10,000, unlimited) and the byte/image budgets from §3.2.6.
* **Storage:** `%LOCALAPPDATA%\Rivet\ClipboardHistory.json` (Swift-compatible date encoding, atomic writes, size cap) and an image folder next to it. Clearing unpinned items or switching history off deletes the image files too.
* **Privacy:** formats `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore` and `CanIncludeInClipboardHistory = 0` mark a copy as concealed; it is never read. Ignored apps by executable path (owner via `GetClipboardOwner`, foreground history as fallback). The "looks sensitive" heuristic (§6.13) skips likely secrets (on by default). Contents are never logged. Our own transient writes carry `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0` and `CanUploadToCloudClipboard = 0`, so Win+V history and other managers ignore them.
* **History window** (shortcut role `clipboard`, Ctrl+Alt+Win+V): strip of cards with previews, search, preview sidebar with editing, batch selection, number keys 1–9 to paste, Enter pastes into the previous app, Ctrl+C copies only, pin/delete, Esc ladder.
* **Tray panel tile** "Clipboard" (hosted view with the latest entries) and the "Clean URL" hosted tool.
* **Auto clear:** after a delay, on sleep (`PBT_APMSUSPEND`), display off (`GUID_CONSOLE_DISPLAY_STATE`) and lock (`WTSRegisterSessionNotification`). Clears the system clipboard only; saved history is kept.
* **Paste as plain text** (shortcut role `pastePlain`, Ctrl+Alt+Shift+V): snapshot every format, write text only, Ctrl+V, restore the original 0.5 s later unless the user copied something else. Files/images are forwarded as a normal paste.
* **Paste flows:** hide the panel → restore the previous foreground window (`IFocusHandoff`/foreground service, waits ≤ 500 ms) → wait for modifiers to be released → `SendInput` Ctrl+V. Refused with a beep/HUD when the target runs elevated (UIPI) or when the foreground is still us.
* **Clean URL:** the full rule set (verbatim from §6.11.1) with the byte-exact deletion algorithm; user rules stored as a difference from the built-ins; manual cleaner (panel, Settings, quick panel), automatic cleaning of copied links (runs before history capture in the same update, refuses rewrites when unknown formats are present), rules editor page.
* **Settings pages:** Clipboard (category Clipboard and files) and Clean URL.

## Not implemented / deviations

* Tray "latest copy" preview item (macOS menu-bar only) — skipped as the brief says.
* Optional DPAPI encryption of the history file (§8.2 item 7) is not done; the file relies on the per-user ACL of `%LOCALAPPDATA%`.
* Delay-rendered/OLE formats: snapshot-and-restore copies only HGLOBAL formats and fails open (nothing changed) above 64 MiB or when a format cannot be read, so Paste as plain text may not restore exotic Office formats byte-for-byte.
* "Paste and Match Style" menu probing is not ported (no Windows equivalent); strip-and-restore is always used.
* Source attribution uses the owner window's process; apps that copy through a helper process are attributed to that helper.

## Risks

* Foreground restoration is subject to Windows' foreground-lock rules; the paste is aborted rather than sent to the wrong window, so a rare "nothing happened" is possible.
* Apps holding the clipboard open make `OpenClipboard` fail; the lane retries with short back-off and then gives up silently for that copy.
* UIPI: pasting into elevated apps is impossible from a standard-user process (we refuse instead of failing silently).

## Manual test checklist (Windows)

1. Settings → Clipboard: switch history on. Copy text in Notepad, an image in Paint, two files in File Explorer, a URL in Edge and `#336699`. Press Ctrl+Alt+Win+V: five cards with the right kinds and source names.
2. Copy the same text twice: one card, moved to the top. Pin a card, clear unpinned: the pinned one stays and image files of removed cards disappear from `%LOCALAPPDATA%\Rivet\ClipboardImages`.
3. In the history window press 2: the second entry is pasted into Notepad (focus returns there). Ctrl+C on a card copies without pasting.
4. Copy a password from KeePass/1Password/Bitwarden (they set the exclusion formats): no card appears. Win+V history must not show our transient writes after a plain-text paste.
5. Add Notepad to "Apps to skip", copy in Notepad: nothing recorded.
6. Auto clear: delay 10 s → clipboard empties, history stays. Lock (Win+L) with "on lock" on → clipboard empty after unlocking. Sleep likewise.
7. Copy rich text from Word, press Ctrl+Alt+Shift+V in WordPad: unformatted text arrives; 1 s later Ctrl+V pastes the formatted original again.
8. Run Notepad as administrator and try to paste from history: refused with a message, nothing typed elsewhere.
9. Copy `https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=abc&feature=share` with automatic Clean URL on: the clipboard holds the cleaned link and only that one appears in history. Edit a rule in Settings → Clean URL and verify it applies.
10. Tray panel → Utilities → Clipboard and Clean URL hosted views work in light and dark mode.

## Requests for shared code

1. ~~`ShortcutRoleRow` constructor crash~~: done at integration — the shared row is fixed and the Settings pages of this area use it; `RoleShortcutRow` was deleted.
2. No SQLite was needed (JSON + image files); `Microsoft.Data.Sqlite` stays unreferenced.
