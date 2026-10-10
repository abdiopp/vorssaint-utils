# Shelf (`shelf`)

A temporary holding area for dragged files, images, links and text (spec 07 §3.1).
Off by default ("Temporary area for dragging files").

## Where the code is

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/Shelf/`: item model and file types (`ShelfItem`), tree operations, piles, pins, capacity (`ShelfTree`), selection (`ShelfSelection`), drop parsing (`ShelfDropParser`), store and persistence (`ShelfStore`, `ShelfCodec`), gestures (`ShakeDetector`, `DragGestureTracker`, `ShelfExclusions`), geometry (summon, dock frame, trigger/retreat frames, edge match, peek), drag-out policy and tooltips (`ShelfRules`), settings and constants, `IShelfPlatform` |
| App | `src/Rivet.App/Features/Shelf/`: `ShelfModule` (actions, shortcut role, Controls toggle, tray item, settings page), `ShelfService` (also `IShelfIntake` and the feature controller), `ShelfDragMonitor`, `ShelfCardView`, `ShelfTileView`, `ShelfWindows` (card window, pill/badge window), `ShelfDropReader`, `ShelfThumbnails` |
| Windows | `src/Rivet.Platform.Windows/Shelf/WindowsShelfPlatform.cs` (+ `RadialMenu/ShellImages.cs` for thumbnails and icons) |
| Fake | `src/Rivet.Platform.Fake/Shelf/FakeShelfPlatform.cs` |
| Tests | `tests/Rivet.Core.Tests/Shelf/`, `tests/Rivet.App.Tests/Shelf/` (snapshots `shelf-card-*`, `shelf-pill-*`, `shelf-badge-*`, `settings-shelf-*`, light and dark) |

## Implemented

- Feature gate: `featureAvailable.shelf` + `shelfEnabled`; every entry point re-checks both. Turning the shelf off never deletes items; it hides both surfaces, stops the drag monitor, invalidates pending Explorer-selection reads and flushes the store.
- Entry points: global shortcut role `shelf` (default Ctrl+Alt+Win+D, needs `shelfShortcutEnabled`) with the "Add the File Explorer selection" variant (ticketed: stale reads are discarded, nothing new → toggle, new files → one pile + summon, full → beep + toggle); actions `shelf.toggle` and `shelf.open` (Command Bar / radial "Shelf"; opens Settings when the shelf is off); tray menu "Open shelf (N)"; Settings "Open now"; Controls tab switch (Files category) bound to `shelfEnabled`.
- Global drag detection through `IInputHooks` only (no own hooks): primary button (swap-aware) press + movement past `SM_CXDRAG/SM_CYDRAG`; window moves/resizes cancelled through the move/size WinEvents; presses on the app's own windows and internal tile drags never count; a 0.15 s watchdog ends a gesture whose button-up was swallowed and advances dwell timers while the pointer rests. The hook handler only copies and posts the event.
- Shake gesture (≥ 3 reversals, > 220 DIP travel within 0.5 s, 6 DIP direction threshold, 1 s cooldown, DIP-corrected per monitor) summons the card at the pointer, speculatively: if the drag ends without a drop and the shelf is empty, the card hides at once.
- Docked shelf: pill near the tray (bottom/top/side taskbar aware) or badge at the top centre; appears during a content drag, stays with a count while items exist; 150 ms dwell inside the trigger frame expands it to the card; leaving the card padded 32 DIP collapses it; drag end waits 0.15 s; a drop shows a green check for 0.9 s; explicit expand/collapse with the forced-open flag; a hovering OLE drag on the pill expands it immediately.
- Edge peek: outer left/right edges only (seams between monitors never count, side taskbars excluded), 150 ms dwell, one third of the card on screen, retreat beyond 330 DIP, retract 0.15 s after a release without a drop, a landing drop slides the card flush to the usable edge.
- Classic card: 304 DIP, header (glyph, "Shelf" / "%d selected", leaf-count badge, keep-open pin, close), 188 DIP scrolling tile grid (3 columns of 78×88 tiles), dashed empty state "Drag items here", footer (hint, Share, Clear all / Remove selected). Movable by the header, the empty state and blank grid space. Shown without activation; the first click makes it key (Esc clears the selection, Ctrl+A selects all). Refits with the top-left corner fixed. Auto-hide after 5 s + 0.22 s fade only while nothing holds it (pin, items, pointer inside, drop hover, interaction, peek). "Clear when closed" on the close button only.
- Drops: files and folders by reference (with an NTFS file-id bookmark), GIF/bitmap data stored as owned PNG/GIF, browser links (`UniformResourceLocatorW`, or a single http(s) URL as text), plain text up to 200,000 characters. One item alone, several as one pile; 200-leaf capacity (nothing added, owned payloads deleted, when it would overflow). Drop on a tile merges (external) or stacks (internal tile drag, offered as Move); non-tile targets refuse internal drags.
- Tiles: icon well with real shell thumbnails (decoded off the UI thread and patched in by item revision) or the shell icon / a Fluent glyph; pile back plates, count badge and expand chevron; selection check; pin badge that turns into ✕ on hover; click / Shift-click / double-click on piles; tooltips after 1 s with the spec texts; context menu Pin/Unpin, Open, Open With, Edit (screenshot editor, one image, when that feature is installed), Share, Show in File Explorer.
- Drag-out of a tile or the selection with Avalonia DnD (files as storage items, text and links as text), Copy + Move only when "Remove items after dropping" is on and nothing dragged is pinned, otherwise Copy; living check with one heal attempt from the bookmark; dead files on a present drive are dropped with the HUD "The file no longer exists", files on an absent drive are kept. After an accepted drop the unpinned dragged items leave and the surface closes when "Close after dropping" is on and it is not pinned. Explorer's optimized move (reported as "none") is treated as accepted when the source file vanished.
- Share through the Windows share sheet (`IDataTransferManagerInterop::ShowShareUIForWindow` + `DataPackage.SetStorageItems`); beep when sharing is unavailable. Reveal selects all files per folder (`SHOpenFolderAndSelectItems`).
- Persistence: `%LOCALAPPDATA%\Rivet\Shelf\shelf.json` (machine-specific paths, never in settings backups), owned payloads in `ShelfFiles\<UUID>\`, deleted 10 minutes after their item leaves; a launch sweep removes orphans; unreadable files are kept aside, never overwritten; synchronous flush on exit.
- `IShelfIntake` for other modules (screenshot "Add to Shelf", media outputs).
- "Automatic exceptions": .exe picker, full paths or bare names, honoured by shake, dock and edge peek.

## Not implemented

- **Virtual files** (`CFSTR_FILEDESCRIPTORW` + `CFSTR_FILECONTENTS`: Outlook attachments, some browser image drags). Avalonia's drop API does not expose indexed `FileContents`; such drops are refused. Needs a native `IDropTarget` in Platform.Windows (shared drop plumbing) — see requests.
- **Thin OLE "sensor" windows** for content confirmation (spec §3.1.5 step 3). Windows has no global drag clipboard; instead the dock and edge peek wait for the shell drag image (`SysDragImage`, shown by Explorer, browsers and most OLE sources) as the content hint, and the shake opens speculatively. Drags from sources without a drag image (some old Win32 apps) show no pill until the pointer reaches it.
- Command Bar "Keep it on the shelf" (needs a shared "selected text" service).
- "Open With" lists only the Windows chooser for the first file (no per-app submenu from `SHAssocEnumHandlers`).
- Stacked drag image with a count badge (`IDragSourceHelper`): Avalonia shows its default drag cursor.
- The Controls-tab accessory "Open shelf (N)" (the panel toggle descriptor has no accessory slot).
- Dynamic Island Files page.

## Deviations from macOS

- Default shortcut Ctrl+Alt+Win+D (app-wide convention from spec 05 §6.1; Win+Ctrl+D alone creates a virtual desktop and is untouched).
- "Below the menu bar icon" → "Near the tray icon"; the pill sits 4 DIP from the taskbar. Without a tray-icon rectangle from the shell (see requests) it anchors to the work-area corner (work.right − w − 12).
- A merge onto a single tile mints a new pile id (the spec's quirk §8.3.1 is not copied).
- The pill expands as soon as an OLE drag hovers it (in addition to the 150 ms pointer dwell).
- Tooltips use the standard Avalonia tooltip with a 1 s delay.

## Risks

- Speculative semantics: the pill and peek may appear for a drag that carries nothing droppable when the source still shows a drag image (rare); they hide again on release.
- The shelf windows are not registered drop targets for elevated apps (UIPI): drags from an elevated Explorer cannot land.
- Explorer "move" detection relies on the source file disappearing; a destination that copies then deletes later may be reported as not accepted.

## Windows manual test checklist

1. Settings › Clipboard and files › Shelf: switch on "Temporary area for dragging files". The Controls tab shows the Shelf switch on.
2. Press Ctrl+Alt+Win+D: an empty card appears 16 DIP below the pointer; it fades out after about 5 s. Press again while visible: it hides.
3. Drag a file from File Explorer and shake the mouse left-right: the card appears at the pointer. Drop the file: one tile with a real thumbnail (images, videos) or the file icon.
4. Drag three files at once onto the card: one pile "<first> +2" with a count badge. Double-click it: children appear inline. Drag a single tile onto another tile: they stack.
5. Drag text from a browser, and a link from the address bar: a note tile and a link tile titled with the host.
6. Drag a tile into an Explorer folder on the same drive: the file moves and the tile leaves the shelf. Pin a tile, drag it out: it is copied and stays.
7. Rename/move a shelved file in Explorer, then drag the tile out: it still works (healed from the file id). Delete the file, drag the tile: "The file no longer exists" and the tile goes.
8. Share button: the Windows share sheet opens with the files (Nearby Share, Mail…).
9. Right-click a tile: Pin, Open, Open With, Show in File Explorer select the files.
10. With "Keep dragged files near the tray icon" on: start dragging any file; a pill appears near the tray. Hover it: the card opens; drop: a green check, then the pill with a count. Switch the position to top centre: a badge.
11. Turn on "Open near a screen edge": drag a file to the far left edge and pause: a third of the card peeks in; pull back: it retreats; drop on it: it slides fully in.
12. Add paint.exe to Automatic exceptions; drag inside Paint and shake: nothing opens. The shortcut still works.
13. Turn on "Add the File Explorer selection": select files in Explorer, press the shortcut: they are added and the card opens. Press again with the same selection: it toggles.
14. Restart the app: items and pins are back. Turn the shelf off and on: items are kept.
15. Move a window by its title bar fast left and right: the shelf must not open.

## Requests for shared code

1. **Tray icon rectangle**: expose `PixelRect? IconRect` (`Shell_NotifyIconGetRect`) on `ITrayPresence` (or `IAppShell`) so the docked pill centres on the icon; `ShelfService.TrayIconRect` already consumes it (returns null today).
2. **Controls-toggle accessory**: an optional `Func<IServiceProvider, Control?> Accessory` on `PanelToggleDescriptor` for the "Open shelf (N)" button.
3. **Virtual-file drops**: a shared native `IDropTarget` helper in Platform.Windows that can read `CFSTR_FILEDESCRIPTORW`/`CFSTR_FILECONTENTS` (with `IDataObjectAsyncCapability`) into a temp folder; the shelf would then use the spec §3.1.11 flow.
4. **Selected text** for the Command Bar "Keep it on the shelf" (the Command Bar owns the selection capture); it can call `IShelfIntake.AddText`.
5. ~~Shared `ShortcutRoleRow`~~: done at integration — the shared row resolves brushes without casting and subscribes only while attached; the local `RoleShortcutRow` copy was deleted and these pages use the shared row.
