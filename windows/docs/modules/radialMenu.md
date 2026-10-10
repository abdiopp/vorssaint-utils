# Radial menu (`radialMenu`)

Wheels of up to 12 favourite actions around the pointer (spec 07 §3.2). Off by default ("Use the radial menu").

## Where the code is

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/RadialMenu/`: model and ids (`RadialModel`), geometry and springs (`RadialGeometry`), the session state machine (`RadialSession`), profile codec with lenient decoding, cleanup, legacy migration and macOS shortcut translation (`RadialProfilesCodec`), presets, labels and symbols, link normalisation, the 41 window-layout actions, Now Playing sanitising, `IRadialPlatform`, `INowPlayingService` |
| Imaging | `src/Rivet.Imaging/RadialMenu/RadialWheelRenderer.cs` (disc, wedge, guides, chips, hub; wheel and canvas sizes) |
| App | `src/Rivet.App/Features/RadialMenu/`: `RadialMenuModule`, `RadialMenuService` (profiles, shortcut slots, trigger hook, sessions, running slices), `RadialPresenter` (labels, icons, tool resolution), `RadialWheelView` (Skia wheel + icon overlay + motion), `RadialWheelWindow` + `NowPlayingCard`, `RadialMenuSettingsPage`, `RadialCanvasEditor`, `RadialItemEditor` |
| Windows | `src/Rivet.Platform.Windows/RadialMenu/`: `WindowsRadialPlatform` (ShellExecute, activate running .exe, display names, layouts with DWM frame correction), `ShellImages` (IShellItemImageFactory), `WindowsNowPlaying` (GlobalSystemMediaTransportControlsSessionManager) |
| Fake | `src/Rivet.Platform.Fake/RadialMenu/FakeRadialPlatform.cs` (logs launches, a fake playing session) |
| Tests | `tests/Rivet.Core.Tests/RadialMenu/` (geometry, session, codec, presets, labels vectors), `tests/Rivet.Imaging.Tests/RadialMenu/`, `tests/Rivet.App.Tests/RadialMenu/` (sessions through the fake hooks, snapshots `radial-wheel-window-*`, `settings-radialMenu-*`, `radial-item-editor-*`, icon-name checks) |

## Implemented

- Profiles in `radialMenuProfiles` (JSON array; a base64/JSON string from a macOS backup also decodes), lenient per-profile/per-item decoding, cleanup on every read and write (unique ids, ≤ 12 items, one submenu level, valid payloads, exclusive shortcuts and mouse buttons), legacy single-wheel keys migrated once and persisted.
- Triggers: one global shortcut per wheel for the first six wheels, through the shared `ShortcutManager` (roles `radialMenu.wheel1…6`, mirrored into machine-state settings so the Keyboard shortcuts page shows and edits them; edits there write back into the wheel). Default first wheel Ctrl+Alt+Win+Space. Side mouse buttons Back/Forward through the shared mouse hook: both halves of a claimed click are always swallowed; the hook is installed only while some wheel claims a button (or the button test runs).
- Opening behaviours "Press or hold" / "Press to open" / "Hold to select", read once per session. Hold release: any required modifier up (checked through `IInputHooks.IsKeyDown`, also right after install and on every move as a fallback) or the same mouse button up. Same trigger closes, another wheel's trigger switches; a side-button down during a held keyboard session is ignored.
- Session: slices that cannot run are filtered (tools whose action is missing or uninstalled, the Shelf slice while the shelf is off, quick toggles without an action, empty submenus); nothing left → beep. Placement at the pointer or the work-area centre, kept 200 DIP inside. 40 DIP dead zone, 8 DIP arming, no outer limit; clicks by distance (> 150 close, < 40 back/close, else run); Esc, Enter, arrows (rotate; sticky phase except in hold mode), digits 1–9; one submenu level with the back hub; any click outside dismisses and still reaches its target.
- The wheel never activates: a non-activating, click-through, topmost 400×400 window; a session-scoped keyboard handler swallows only Esc, Enter, arrows and digits (and their key-ups); a mask key (VK 0xE8) is typed when a key is swallowed while Alt or Win is held, so releasing them does not open the menu bar or Start.
- Drawing with the shared Skia renderer (disc tint, rim, wedge gradient, guides, chips, hub), Fluent icons / shell icons / fetched favicons over it, hub face (name up to 3 lines, back chevron + submenu name, or the brand mark). Motion: fade/scale open, clockwise chip bloom within 0.17 s, spring wedge (shortest arc) and highlighted chip (×1.14), 0.13 s close; instant with "Animation effects" off. A token guards a stale fade from hiding a reclaimed window.
- Slice kinds: app (activate running copy or launch), file/folder, link (any scheme), keyboard shortcut (waits until no modifier is physically down: 15 ms polls, ≤ 100 tries, then 60 ms, down with modifiers, up 40 ms later via `SendInput`), media keys (`VK_MEDIA_PLAY_PAUSE/NEXT/PREV_TRACK`), Now Playing card (SMTC, 2 s bound; title, album/artist, artwork, "Open %@", click activates the player by AUMID), app actions (after 0.15 s, through `ActionRegistry` with source `RadialMenu`), quick toggles (after 0.15 s), window layout (after 0.15 s, on the window that was in front when the wheel opened), submenu.
- Cleaning Mode suspends the feature and closes an open wheel.
- Settings page: switch, opening behaviour, placement, "Try it" (works with the feature off or without a trigger), orange note when a shortcut registration failed; profiles (picker, add from the six presets, duplicate "<name> 2" without triggers, delete with confirmation, name saved on every keystroke, 12 colour swatches, shortcut recorder with conflict checks against other wheels and every app role, side-button picker, live button test, no-trigger note); actions (330 DIP visual canvas with hover names, click to edit, drag to swap by angle, context menu Edit / Edit actions / Remove, hub back/add; Reset to the preset with confirmation; Add action; list editor with move up/down, edit, remove; the 12-action limit caption); item editor sheet (kind, per-kind target with choosers, link validation and the website-icon fetch, shortcut recorder, pickers; name; curated icon grid; Remove / Cancel / Save enabled only for a valid target).
- Controls tab switch (Input category) bound to `radialMenuEnabled`.

## Not implemented

- Buttons 6–32: Windows does not deliver them to low-level hooks (vendor software or HID). Only Off/Back/Forward are offered; a stored button 5+ from a macOS backup shows a warning row.
- Four-finger trackpad tap (no public API; bind Windows' own touchpad gesture to the wheel shortcut instead). `trackpadTap` is kept in the data for backups.
- Wheels beyond the sixth have no global shortcut (mouse button or "Try it" only); the page says so.
- Super key interplay (the Super key is another module).
- Tools are any app action (the editor lists every registered action); the macOS fixed list of 15 tools is mapped by feature id (`screenshot`, `colorPicker`, `screenOCR`, `screenRecorder`, `micMute`, `scratchpad`, …) to that feature's `.open`/`.toggle`/`.start`/`.show`/`.capture`/`.pick`/`.run` action, else its first action.

## Deviations from macOS

- "Vorssaint tool" → "App action"; the macOS Accessibility-permission notes are replaced by a note that injected keys do not reach elevated apps (UIPI).
- A failed hotkey registration says the combination is taken (spec §8.3 quirk fixed).
- The favicon fetch downloads `/favicon.ico` once (8 s timeout, ≤ 1 MiB), scales it to 64×64 PNG and stores it in the item (`customIconData`, ≤ 64 KiB).
- The list editor moves rows with buttons instead of drag-and-drop rows.
- Without the macOS blur material the disc tint is more opaque (the renderer documents it).

## Risks

- Injected shortcuts and media keys do not reach elevated windows (UIPI); swallowed keys likewise only work for non-elevated foreground apps.
- Holding Ctrl+Alt+Win and releasing after a highlight: the mask key prevents the Start menu on most builds; verify on Windows 10 and 11.
- Ctrl+Alt+letter shortcuts collide with AltGr on European layouts; the default uses Win.
- Now Playing depends on the player publishing an SMTC session (browsers, Spotify and Media Player do; some apps do not).

## Windows manual test checklist

1. Settings › Tools › Radial menu: switch it on. The first wheel shows Ctrl+Alt+Win+Space.
2. Hold Ctrl+Alt+Win+Space, move up: Play/Pause highlights with the purple/accent wedge; release the keys: media playback toggles and the wheel fades.
3. Press and release the shortcut quickly: the wheel stays open. Press 3: the third slice runs. Open again and press Esc: it closes. Open, click outside: it closes and the click reaches the window below.
4. Arrow keys rotate the highlight; Enter runs it.
5. Add a submenu with two actions; open it from the wheel; the hub shows "‹ name"; Esc goes back.
6. Profiles: add "Window layout"; give it Ctrl+Alt+Win+L; with Notepad in front, open it and choose "Left half": Notepad snaps left without gaps.
7. Assign the Back side button to a wheel: Back no longer navigates in the browser; hold Back, point, release: the slice runs. The button test shows "This button reaches the app".
8. Add a link slice "example.com", Fetch Website Icon: the icon appears on the chip.
9. Add "Press a shortcut" Ctrl+Shift+Esc: running it opens Task Manager.
10. Media preset: with music playing in a browser, "Now Playing" shows the card; clicking it brings the browser forward.
11. Change the shortcut on the Keyboard shortcuts page: the wheel's recorder shows the new one.
12. Start Cleaning Mode: the wheel shortcut and side button do nothing; after unlocking they work again.
13. Turn "Animation effects" off in Windows Settings: the wheel appears without motion.

## Requests for shared code

1. ~~**Quick toggles action ids**~~: done at integration — the Quick toggles module registers `quickToggles.<id>` for every toggle (it used `quickToggle.`), pinned by `Quick_toggle_slices_resolve_to_the_quick_toggles_actions`.
2. **Tool actions**: feature modules should register an action named `<featureId>.open` (or `.toggle`/`.start`/`.capture`/`.pick`) for their primary command so macOS-backup tool slices (`screenshot`, `colorPicker`, `screenOCR`, `screenRecorder`, `micMute`, `clipboardHistory`, `quickLauncher`, `cleaner`, `uninstaller`, `appUpdates`, `keepAwake`) resolve.
3. ~~**Claimed mouse buttons**~~: done at integration — `RadialMenuService` implements the existing `Rivet.Core.Input.IMouseButtonClaims` (registered by this module), which mouse-button shortcuts consult before taking Back/Forward.
4. ~~Shared `ShortcutRoleRow` brush cast~~: done at integration (see shelf.md).
