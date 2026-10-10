# Scratchpad (`scratchpad`)

A floating, tabbed, autosaved notes pad with Markdown helpers and a preview (spec 07 §3.3).

## Where the code is

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/Scratchpad/`: document model (`ScratchpadDocument`: ≤ 12 tabs, names, write-first operations), the notes store (`ScratchpadStore`: lazy load, atomic write + read-back, never overwrites unreadable notes, legacy migration, retention), the nine Markdown marks (`MarkdownMarks`), the pad's own shortcuts (`ScratchpadShortcuts`), settings, `IScratchpadPlatform` |
| App | `src/Rivet.App/Features/Scratchpad/`: `ScratchpadModule` (actions, shortcut role, panel tile, settings page), `ScratchpadService`, `ScratchpadWindow`, `MarkdownPreview` (Markdig → Avalonia text), `TextPromptDialog` |
| Windows | `src/Rivet.Platform.Windows/Scratchpad/WindowsScratchpadPlatform.cs` (on-screen / touch keyboard detection under the pointer) |
| Fake | `src/Rivet.Platform.Fake/Scratchpad/FakeScratchpadPlatform.cs` |
| Tests | `tests/Rivet.Core.Tests/Scratchpad/` (marks vectors, store, document), `tests/Rivet.App.Tests/Scratchpad/` (snapshots `scratchpad-*`, `settings-scratchpad-*`, light and dark) |

## Implemented

- Entry points: optional global shortcut role `scratchpad` (off by default, Ctrl+Alt+Win+N) with the smart toggle; action `scratchpad.open` for the panel tile, Command Bar and radial menu (0.15 s delay from launching surfaces); Settings "Open scratchpad".
- Window: 380×300 initial, 280×220 minimum, centred at 42 % from the top of the pointer's monitor, position remembered for the session; borderless and resizable from 6 DIP edges and 12 DIP corners; 34 DIP header with pin and close; "Keep above other windows" (always-on-top, default on); background opacity slider; text size 10–22.
- Tabs: up to 12, automatic names "Scratchpad N", rename (40 characters), close with confirmation when non-empty, the last tab cannot be closed; context menu and the "…" menu.
- Editor: plain text, undo/redo survives the preview (the editor is never recreated), find bar (Ctrl+F, F3 / Shift+F3) with replace and replace all, Alt+Up/Down move lines, Ctrl+T new tab, Ctrl+W close tab, matched on the produced character so AZERTY and Caps Lock work.
- The nine Markdown marks as toggles (bold, italic, strikethrough, heading 1–3, bullet, numbered, quote, code, link) with the spec's trimming and joining rules.
- Markdown preview: CommonMark + strikethrough (Markdig 1.4), headings, lists, quotes, code, links (http, https, mailto only) that open on click; a parse failure shows the raw text.
- Copy all, Clear (undoable), export as `.txt` or `.md` (`<tab> <yyyy-MM-dd>.txt`).
- Autosave 0.8 s after typing plus immediate saves for tab actions, marks, clear, export, hide and quit; atomic write (temp file, flush, replace) with read-back verification; a notes file that cannot be read is never overwritten (saving stays blocked, a banner and a HUD explain it). Notes live in `%APPDATA%\Rivet\Scratchpad.json` (roaming), never in settings backups.
- Auto-clear after a day/week/month without edits, checked when the pad opens.
- Outside-click dismissal through the shared mouse hook while unpinned, except clicks on the on-screen or touch keyboard; Esc closes.

## Not implemented

- Dynamic Island scratchpad page (the island is not ported).

## Deviations from macOS

- Shortcut default Ctrl+Alt+Win+N (app convention).
- The pad activates when shown (Windows needs the foreground for typing) and gives the foreground back on hide.
- "Keep above other windows" is a new option (macOS pads always float).
- Export offers Markdown (`.md`) as well as `.txt`.
- Preview links are limited to http, https and mailto (spec §8.3 suggestion).
- A final save runs unconditionally on quit (spec §8.3 quirk fixed).

## Risks

- The shortcut row is the shared `ShortcutRoleRow`.
- Typing into elevated apps is unaffected, but outside-click detection does not see clicks on elevated windows (UIPI), so the pad may stay open over them.

## Windows manual test checklist

1. Settings › Tools › Scratchpad: Open scratchpad. The pad appears centred on the pointer's monitor, slightly above the middle.
2. Type text; wait one second; quit the app from the tray; restart; open the pad: the text is back.
3. Add tabs up to 12; the "+" disables. Rename a tab; close a non-empty tab: a confirmation appears.
4. Select a word, Ctrl+B: `**word**`; again: removed. Try heading, bullet, numbered, quote, code, link.
5. Toggle the preview: headings, bold, strikethrough and a clickable link render; editing resumes with undo intact.
6. Ctrl+F, type a word, F3 / Shift+F3 cycle matches; replace all works.
7. Alt+Up/Down moves the current line.
8. Click outside the pad: it hides. Pin it: it stays. Open the Windows touch keyboard and type on it: the pad stays open.
9. Export: the save dialog proposes "<tab> <date>.txt"; `.md` is offered too.
10. Make `%APPDATA%\Rivet\Scratchpad.json` read-only garbage (e.g. write `{{`), open the pad: the banner says notes could not be loaded/saved and nothing overwrites the file.
11. Turn on the global shortcut; Ctrl+Alt+Win+N toggles the pad from any app.
12. Set "Clear on its own" to a day, change the file time to two days ago, open the pad: it is empty.

## Requests for shared code

1. ~~Shared `ShortcutRoleRow`~~: done at integration — the shared row resolves brushes without casting and subscribes only while attached; the local `RoleShortcutRow` copy was deleted and these pages use the shared row.
