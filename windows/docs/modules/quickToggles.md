# Quick toggles (quickToggles)

Spec: `docs/specs/06-clipboard-snippets-commandbar.md` §2.9, §3.10, §7.5; spec 05 §3.4.9.

## Where the code lives

| Layer | Files |
|---|---|
| Core (`Rivet.Core.Toggles`) | `src/Rivet.Core/QuickToggles/` — ids, order and visibility keys (`QuickToggleSettings`), the row state machine (`QuickTogglesService`), `IQuickTogglesPlatform` |
| Windows | `src/Rivet.Platform.Windows/QuickToggles/WindowsQuickTogglesPlatform.cs` |
| Fake | `src/Rivet.Platform.Fake/QuickToggles/FakeQuickTogglesPlatform.cs` (in-memory state) |
| App (`Rivet.App.Features.Toggles`) | `src/Rivet.App/Features/QuickToggles/` — `QuickTogglesModule`, `QuickToggleCatalog` (titles from the current state, captions, icons, run flow), `QuickTogglesView` (panel section, quick-panel hosted list, Settings editor), `QuickTogglesSettingsPage`, `ConfirmDialog` |
| Tests | `tests/Rivet.Core.Tests/QuickToggles/` (keys, order, state machine), `tests/Rivet.App.Tests/QuickToggles/` (section light/dark, edit mode, Settings page, catalog order/visibility, toggle flips the title) |

## Implemented

Panel section "Quick toggles" (`KeepsPanelOpen = true`, Order 90) with edit mode (move up/down, per-row visibility switch, Reset), the same list hosted in the quick panel (no editing), one action per row (`quickToggle.<id>`, for shortcuts, the radial menu and the Command Bar), and the Settings page (Tools) with the dark-mode button, the rows editor and "Excluded drives".

| Row | Windows implementation |
|---|---|
| Switch to dark / light mode | Writes `AppsUseLightTheme` and `SystemUsesLightTheme` (HKCU `…\Themes\Personalize`), then broadcasts `WM_SETTINGCHANGE` "ImmersiveColorSet" (with `SendMessageTimeout`, so hung apps do not block) |
| Mute / unmute microphone | Invokes action `micMute.toggle` of the microphone module; the row exists only while that action is registered |
| Empty the Recycle Bin | App's own confirmation dialog (Enter/Esc), then `SHEmptyRecycleBin` without Windows' dialog, progress or sound; the caption shows item count and size (`SHQueryRecycleBin`) or "already empty" |
| Eject all disks | Removable, optical and external (USB/1394/SD/MMC bus) volumes, never the system drive or excluded ones; `CM_Request_Device_Eject` on the device; a veto is a failure ("Could not eject") |
| Show / hide hidden files | Explorer `Advanced\Hidden` (1/2), then `SHChangeNotify` + "ShellState" broadcast + refresh of open Explorer views — no Explorer restart |
| Show / hide file extensions | Explorer `Advanced\HideFileExt`, refreshed the same way (Windows-only addition) |
| Hide / show desktop icons | Explorer `Advanced\HideIcons` plus the desktop view's toggle command (0x7402, undocumented) so it applies live |
| Lock the screen | Closes the surface, +0.15 s, `LockWorkStation` (screen saver as fallback) |
| Turn off the display | `WM_SYSCOMMAND SC_MONITORPOWER 2` posted to our own window |
| Start the screen saver | `WM_SYSCOMMAND SC_SCREENSAVE`; fails visibly when no screen saver is configured |
| Sleep | `SetSuspendState` (Windows-only addition; also a Command Bar power row) |

Row states: running (repeated clicks ignored), success clears at once (the new title is the feedback), failed shows "Could not complete." for 2.4 s.

## Not implemented / deviations

* **Keyboard light** is dropped: Windows has no general keyboard-backlight API (vendor tools only).
* **Needs permission** state (Finder automation) is dropped: Windows needs no grant.
* Explorer settings apply live instead of restarting Finder/Explorer.
* These toggles change system settings on purpose and are **not** undone when the app quits (they are the user's choice, like flipping the switch in Windows Settings).

## Risks

* The desktop-icons command id 0x7402 is undocumented; if a future Windows build ignores it, the registry value still applies at the next Explorer restart/sign-in.
* Some apps only follow the dark-mode change after restart (they read the theme once).
* Eject is vetoed by any open handle (antivirus scans, Explorer thumbnails); this is reported as a failure, never forced.
* `SetSuspendState` hibernates instead of sleeping when hibernation is configured as the sleep action.

## Manual test checklist (Windows)

1. Tray panel → Quick toggles: "Switch to dark mode" turns Windows and apps dark within a second; the row now says "Switch to light mode".
2. Show hidden files and file extensions with an Explorer window open: it refreshes without restarting; toggle back.
3. Hide desktop icons: icons disappear immediately; show them again.
4. Put a file in the Recycle Bin: caption shows "1 item (…)". Empty: confirmation appears, Esc cancels, Enter empties without Windows' own dialog.
5. Plug in a USB stick and open a file on it: Eject all disks fails with "Could not eject"; close the file, retry: the stick is ejected. Add it to Excluded drives: it is never ejected.
6. Lock the screen, Turn off the display, Start the screen saver, Sleep: each runs after the panel closes. With screen saver "(None)": the row fails visibly.
7. With the microphone module installed: Mute microphone row toggles the mic; without it the row is absent.
8. Edit mode: move rows, hide some, Reset restores order and visibility. The quick panel's "Quick toggles" tool shows the same rows without edit controls.
9. Command Bar: type `dark`, `recycle`, `lock`: the same actions; empty the Recycle Bin asks inline.

## Requests for shared code

None. (`micMute.toggle` is registered by the Sound module.)
