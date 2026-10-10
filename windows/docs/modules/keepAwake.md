# Keep awake (Windows)

Spec: `docs/specs/03-system-monitor.md` §3.18, §4.6, §6.12; `docs/specs/05-app-shell.md`
§3.4.10 (the Keep Awake card), §3.3 (tray tint, tooltip, menu, right-click toggle)
and §6.1 (shortcut). Module class: `Rivet.App.Features.Awake.KeepAwakeModule`.
Feature id: `keepAwake`.

Code: `src/Rivet.Core/KeepAwake` (namespace `Rivet.Core.Awake`: manager, settings,
formats, platform interfaces), `src/Rivet.Platform.Windows/KeepAwake`,
`src/Rivet.Platform.Fake/KeepAwake`, `src/Rivet.App/Features/KeepAwake`, tests in
`tests/Rivet.Core.Tests/KeepAwake` (35 behaviour tests) and
`tests/Rivet.App.Tests/KeepAwake` (snapshots, registrations, shortcut and tray).

## Status

| Area | State |
|---|---|
| Sessions: 15 m, 30 m, 1 h, 2 h, 4 h, 8 h, indefinitely, "Until…" a time, extend chips (+15 m, +30 m, +1 h), last pick | Done |
| Power requests (`PowerCreateRequest`/`PowerSetRequest`), "Allow the display to sleep" applied live | Done |
| Battery protection (limit 5–20 % or never, 30 s check, blocks starts and ends sessions) | Done |
| "Session ended" toast through `INotificationService` | Done |
| Automation: external display, on AC power, chosen apps running; Any/All; hand-over from a timed session; manual-stop suppression | Done |
| Pause while the PC is locked (WTS session notifications) | Done |
| Pointer jiggle (1 px and back, intervals 1–15 min) | Done |
| Start at launch (`keepAwakeAutoStart`) after crash recovery | Done |
| Keep going with the lid closed (power plan lid action, write-ahead marker, crash restore) | Done |
| Tray: tint, tooltip line, menu items, right-click toggle setting, countdown | Done (flat menu, see requests) |
| Action `keepAwake.toggle`, `keepAwake.start.<minutes>`; shortcut role `keepAwake` (Ctrl+Alt+Win+K, gated by `hotkeyEnabled`) | Done |
| Panel section `keepAwake` (Order 4) and Settings page `keepAwake` (Energy and display) | Done |

## Implemented

* **`KeepAwakeManager`** (UI thread, injected clock/timers so tests run on a
  manual clock) ports §3.18: presets in minutes (0 = indefinitely), "until"
  resolved to the next occurrence of the wall-clock time (DST-safe; a time
  already passed is ignored, never rolled to tomorrow for the last pick),
  extend = `max(end, now) + minutes`, the main switch / shortcut / tray start
  the last pick. When a timed session ends while automation would start one,
  it hands over silently (only in Any mode, and never past battery
  protection); otherwise the "session ended" toast is shown.
* **Power requests** (`WindowsPowerRequests`): one request object with the
  reason "<app>: Keep awake is on" (visible in `powercfg /requests`);
  `PowerRequestSystemRequired` always, `PowerRequestDisplayRequired` unless
  "Allow the display to sleep" is on. Released on stop, pause, uninstall and
  quit (and by Windows if the process dies, because the handle closes).
* **Battery protection**: on battery at or below the limit a start is refused
  (the card shows "Battery at N %. Plug in or lower the battery limit to
  start") and a running session ends; checked every 30 s and on every
  `WM_POWERBROADCAST` power status change.
* **Automation** (§3.18.4): external display = any active display path that is
  not the built-in panel and not virtual (`QueryDisplayConfig`, re-evaluated
  on screen changes with a 0.35 s debounce); power = `GetSystemPowerStatus`
  AC line (never matches on desktops without a battery, as the spec says);
  apps = executable names (`zoom.exe`) matched case-insensitively against one
  `NtQuerySystemInformation` process snapshot every 3 s, polled only while
  the Applications condition is on and has apps. Any/All rule, automatic
  sessions never end manual ones, a manual stop suppresses automation until
  its conditions clear.
* **Pause while locked**: `WTSRegisterSessionNotification` on the shared
  native window; locking releases the power requests and keeps the session's
  end time; unlocking resumes it, or ends it if it expired meanwhile.
* **Pointer jiggle** (`WindowsPointerJiggler`): `SendInput` absolute move of
  1 px (right, else left, else down, else up, staying 2 px inside the
  monitor), back after 80 ms if the pointer is still within 2 px. The input
  carries the app's injection signature so the shared hooks ignore it.
* **Closed-lid mode**: see "Design decisions".
* **Tray** (`KeepAwakePresenter`): `ITrayPresence` indicator `keepAwake` with
  the chosen tint while active (orange default, green, blue, purple, pink,
  none) and a tooltip line ("Awake until 14:30", "Awake indefinitely", the
  automation reason, or "Normal sleep"), plus the remaining time when
  "Show the remaining Keep Awake time" is on (30 s refresh, only while shown).
  It also supplies the countdown readout of the system monitor's mini monitor
  (`IReadoutCountdownSource`). Tray menu: "Enable/Disable Keep Awake"
  (Order −40) and, while idle, "Keep awake for 15 min … / indefinitely"
  (Orders −39…−33).
* **Panel card** (`KeepAwakeCard`): status line and switch, the preset chips
  (the running preset shows its countdown), "Until…" time picker, extend
  chips, battery note, "Options" (allow display sleep, start at launch,
  automation tiles with Any/All and the app list, pause while locked,
  pointer jiggle and interval, icon colour) and the lid row.
* **Settings page** (`KeepAwakeSettingsPage`): switch and default duration,
  automation, options (start at launch, right-click toggle, countdown,
  display sleep, jiggle, icon colour, battery limit), closed lid, shortcut.
* **App picker** (`AutomationEditor`): "Add an app…" opens a file picker for
  an `.exe`; the list stores its file name.

## Not implemented (and why)

| Item | Reason |
|---|---|
| Active icon *shape* (`keepAwakeActiveIcon`: coffee, eye, moon, light) | The shell's `ITrayPresence` offers a tint and a badge, not a replacement glyph. The key is kept for backups; only the tint applies. |
| "Activate for…" as a submenu | `TrayMenuItem` has no children; the presets are flat items shown only while idle. |
| Lid-closed display dimming (macOS) | Dropped by the spec (Mac-only). |

## Deviations from macOS

* "Mac" wording becomes "PC"; the app list holds executable names instead of
  bundle ids (bundle ids restored from a macOS backup simply never match).
* The power requests replace IOKit assertions; the display request is a
  separate request type on the same handle.
* Closed-lid mode changes the power plan instead of disabling sleep
  system-wide (macOS `pmset disablesleep`).

## Design decisions

* **Closed-lid mode** ("Keep going with the lid closed", `clamshellPreferred`):
  while a session runs, the active power scheme's
  `GUID_SYSTEM_BUTTON_SUBGROUP/GUID_LIDCLOSE_ACTION` becomes 0 ("Do nothing")
  for AC and battery (`PowerWriteACValueIndex`/`PowerWriteDCValueIndex`,
  then `PowerSetActiveScheme`). Before writing, the scheme GUID, both original
  values and the written value are saved in the write-ahead marker
  `vorssDisabledSleep` (machine state, never backed up). The end of the
  session, a pause, uninstall and quit restore the originals, but only for
  values still equal to what the app wrote (a change the user made in the
  meantime wins). If the app crashed, the next launch restores them before
  auto-start. Restoring with the lid already closed asks Windows to sleep
  (`SetSuspendState`, or turning the displays off on Modern Standby PCs;
  up to 10 tries 0.5 s apart). If Windows refuses the write (group policy),
  the preference is switched off and the card shows "Couldn’t turn on
  closed-lid mode. Try again." PCs without a lid
  (`GetPwrCapabilities.LidPresent`) show "This PC has no lid."
* Power requests are used rather than `SetThreadExecutionState`, because they
  are per handle (no dedicated thread), show a reason in `powercfg /requests`
  and are released by the kernel when the process exits.

## Risks

* Corporate group policy can lock the power plan; the lid write then fails
  and the preference turns off (tested with a fake; not seen on hardware).
* `SetSuspendState` is refused on Modern Standby PCs; the fallback (monitor
  off) relies on the lid being closed to enter standby.
* `SendInput` does not reach the secure desktop or elevated windows in front
  (UIPI); apps that watch for activity in those cases see nothing. Logged.
* `WTSQuerySessionInformation` lock state semantics changed in Windows 8;
  Windows 10/11 only is assumed (the project minimum is 10 2004).

## Windows manual test checklist

1. Install Keep Awake. Open the panel: the Keep awake tab sits before Displays and the monitor tabs.
2. Click "1h": the tray icon turns orange; the tooltip says "Awake until …";
   `powercfg /requests` (admin prompt) lists the app under SYSTEM and DISPLAY.
3. Turn on "Allow the display to sleep": DISPLAY disappears from
   `powercfg /requests` immediately; SYSTEM stays.
4. Click "+15m": the end time moves by 15 minutes. Click the running chip:
   the session stops, the tint goes away, the requests are gone.
5. Set the screen to turn off after 1 minute and start "15m": the screen stays
   on past a minute. Wait for the end (or use "Until…" one minute ahead): a
   "session ended" toast appears and the screen sleeps normally.
6. Press Ctrl+Alt+Win+K: toggles the session. Turn off "Enable shortcut":
   the key does nothing. Right-click tray option: with "Right-click the tray
   icon to toggle Keep Awake" on, right-click toggles instead of the menu.
7. Tray menu while idle shows "Keep awake for …" entries; while active only
   "Disable Keep Awake".
8. Laptop: set the battery limit to 20 %, unplug at a lower charge: the start
   is refused with the battery note; a running session ends within 30 s.
9. Automation: enable "Power", plug in: a session starts ("Active while
   connected to power" in the tooltip); unplug: it ends. Enable "External
   display", connect a monitor: starts; disconnect: ends. Add `notepad.exe`
   under Applications, start Notepad: starts within ~3 s; close it: ends.
   Stop an automatic session by hand: it does not restart until the condition
   clears once.
10. "Pause while the PC is locked": start 1 h, lock (Win+L): requests are
    released (check `powercfg /requests` from another session or the log);
    unlock: the session continues with the same end time.
11. "Move pointer slightly" at 1 min: watch the pointer nudge 1 px and return;
    Teams/Slack stay "Available".
12. Lid mode (laptop): enable "Keep going with the lid closed", start a
    session, close the lid on AC: the PC keeps running (music keeps playing).
    `powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION` shows 0 during the
    session and the original value after it ends.
13. Crash restore: during a lid-mode session, kill the app in Task Manager.
    The lid action stays 0. Start the app: it is restored at launch before
    auto-start runs.
14. "Keep Awake when Rivet opens": restart the app: a session with the default
    duration starts.
15. Quit the app during a session: requests are released and the lid action is
    restored.

## Requests for shared code

1. ~~**`ShortcutRoleRow` constructor crash**~~: done at integration — the shared row
   resolves brushes safely and both Settings pages use `new ShortcutRoleRow(role)`.
2. **Tray submenus**: a `Children` list on `TrayMenuItem` so "Activate for…"
   can be one submenu as on macOS.
3. **Tray glyph**: an optional replacement glyph on `TrayIndicator` (e.g. a
   Fluent symbol name) so `keepAwakeActiveIcon` can apply.
4. ~~**Panel order**~~: done at integration — Utilities is `Order = 60` and the
   mixer `Order = 7`, matching spec 05 §3.4.3.
5. ~~**Tray re-render**~~: done at integration — `TrayController` redraws the icon only
   when the taskbar theme, tint or badge changes.
