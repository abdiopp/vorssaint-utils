# Quick panel (quickLauncher)

Spec: `docs/specs/06-clipboard-snippets-commandbar.md` §2.8, §3.9, §4 (`quickLauncher*` keys); spec 05 §3.4.7 (Utilities tile) and §6.1 (shortcut table).

## Where the code lives

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/QuickPanel/QuickPanelSettings.cs` — keys (`quickLauncherShortcutEnabled` = true, `quickLauncherShortcut`, `quickLauncherItemOrder`, `quickLauncherHiddenItems`), default order, order sanitizing, hidden list format, clamped grid movement |
| App | `src/Rivet.App/Features/QuickPanel/` — `QuickPanelModule` (+ `QuickPanelHost`), `QuickPanelCatalog` (resolves tools from the registries), `QuickPanelWindow`, `QuickPanelSettingsPage` |
| Tests | `tests/Rivet.Core.Tests/QuickPanel/` (order, hidden, movement vectors, default shortcut), `tests/Rivet.App.Tests/QuickPanel/` (grid light/dark, edit mode with options card and Add back, hosted tool + Esc ladder, keyboard, persistence, Settings page light/dark) |

No platform code: the panel only uses the shared floating-panel base (`Features/Clipboard/FloatingPanel.cs`) and the registries.

## Implemented

* **Opening:** shortcut role `quickLauncher` (Ctrl+Alt+Win+Q, on by default; Win+Ctrl+V is taken by Windows), the Utilities tile "Quick panel" (tray panel closes, the quick panel shows 0.15 s later), the Command Bar row and "Open quick panel" in Settings. Each opening refreshes availability, leaves edit mode and selects the first tile.
* **Tools:** the macOS tile list (keepAwake, cleaner, toggles, micMute, screenOCR, colorPicker, clipboard, cleaning, homebrew → packageManager, media, urlCleaner, uninstaller, screenshot, screenRecorder, cameraPreview, scratchpad) resolved against what modules registered: a Utilities tile of the same feature (hosted view or overlay action), else a Controls switch (flipped in place, live dot while on), else a `<feature>.toggle` action. Quick toggles and Clean URL are hosted by this module; Clipboard opens the clipboard history window after 0.1 s. Utilities tiles of other features (Port Manager, App updates, Command Bar…) join after the known ones, so new modules appear without a shared list. A tool exists only while its feature is installed.
* **Layout:** 420 wide, corner 22, header with ✕ (‹ while a tool is hosted), centred mark and the tune button (✓ while editing); 3-column grid, 46×46 plates, number badges 1–9, live dot, two-line titles; footer with the shortcut and Esc; empty state.
* **Keyboard:** Esc closes the hosted tool › options card › edit mode › the panel; Enter activates; arrows move one cell, clamped, no wrap (↑/↓ by 3); 1–9 activate directly; keys are ignored while editing, hosting or with Ctrl/Alt/Win held. Hover selects.
* **Hosted tools** replace the grid (scrollable, 470 max height); while hosting or editing, clicks in other apps do not close the panel.
* **Edit mode:** red ⊖ hides a tool, "Add back" lists hidden tools as + chips, drag a tile onto another to reorder (persisted), the Clipboard tile opens an inline options card ("Save clipboard history" + "Limit").
* **Settings page** (Tools): open now, shortcut switch + recorder, the tools list with show/hide switches, move up/down and Reset.

## Not implemented / deviations

* **Window layout** tile: window snapping is not ported to Windows (README), so there is no tool.
* **Inline option cards of other modules' tools** (Keep awake duration and lid, Mic mute tray icon, Color picker format) are not built here, because those settings belong to other modules; in edit mode a gear on those tiles opens their Settings page instead. Only the Clipboard card (this area's settings) is inline.
* Live state: switch-backed tools, and action-backed tools whose action reports `AppAction.IsOn` (keep awake, mic mute). Screen recording shows no dot yet. Tile titles are static (e.g. "Mute microphone" does not become "Unmute microphone").
* No fade-in animation; the panel appears at once.

## Risks

* Tool resolution depends on other modules' registrations (feature ids, Utilities tile `FeatureId`, `<feature>.toggle` actions). If a module registers its tile under another feature id, its tool appears among the extras instead of in the macOS position.
* Drag-to-reorder uses pointer moves over tiles; on touch screens use the Settings page arrows.

## Manual test checklist (Windows)

1. Press Ctrl+Alt+Win+Q: the panel appears in the upper centre of the monitor under the pointer with the first tile selected; Enter runs it.
2. Arrows: ← on the first column and → on the last column stay put; ↓ moves by 3; digits 1–9 run tiles directly.
3. Run Screenshot / Color picker: the panel hides first, then the tool starts (no panel in the capture).
4. Open "Quick toggles": the list replaces the grid; Esc returns to the grid; Esc again closes. While hosted, clicking another app does not close the panel.
5. Keep awake (switch-backed): clicking flips it, the panel stays, the green dot follows the state.
6. Tune button: hide two tools, drag one tile onto another, open the Clipboard options card and change the limit. Close and reopen: order and hidden tools persist; "Add back" restores a tool.
7. Hide all tools: the empty-state text appears.
8. Settings → Quick panel: the list mirrors the panel; Reset restores the default order with everything visible. Change the shortcut and verify it.
9. Uninstall a feature in the Features hub (e.g. Clean URL): its tool disappears; reinstall: it comes back in its position.
10. Light and dark mode at 100 % and 150 % scaling.

## Requests for shared code

1. ~~Live state for action tiles~~: done at integration as `AppAction.IsOn` (on the action rather than the tile, since these tools resolve to `<feature>.toggle` actions); `keepAwake.toggle` and `micMute.toggle` set it, and `QuickPanelCatalog.IsLive` reads it.
2. ~~`keepAwake.toggle` / `micMute.toggle`~~: both are registered by their modules.
