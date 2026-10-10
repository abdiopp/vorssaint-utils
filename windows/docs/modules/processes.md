# Processes: Kill Process (`killProcess`) and Port Manager (`portManager`)

Spec: `docs/specs/04-maintenance-and-sound.md` §3.6 (Kill Process), §3.5 (Port Manager), §6.12.

Status: **implemented**, compiled for `net10.0-windows10.0.22621.0`, exercised on macOS with a fake
process table and port table. Not yet run on a Windows PC. Both features are opt-in in the Features hub
(as on macOS).

## Files

| Path | What |
|---|---|
| `src/Rivet.Core/Processes/` | `ProcessModels`, `ProcessRules` (protection, PID-reuse-safe tree walk, same-executable grouping, CPU sampler, sort/filter), `ProcessService` (snapshots off the UI thread with a 3 s freshness cache, identity-checked kill/force/all/tree/restart, row removal and refresh 0.5 s later), `PortTable` (binary `MIB_TCP(6)ROW_OWNER_PID`/UDP table parser, port rules, browser URL mapping), `PortService`, `IProcessPlatform`/`IPortTablePlatform`/`IElevationService`, `CommandRunner` (shared external-command runner), settings |
| `src/Rivet.Platform.Windows/Processes/` | `WindowsProcessPlatform` (`NtQuerySystemInformation` snapshot; paths via `QueryFullProcessImageNameW`; users from the token; critical flag via `ProcessBreakOnTermination`; command line via `ProcessCommandLineInformation`; WM_CLOSE to visible top-level windows; `TerminateProcess` after re-checking creation time on the same handle; relaunch), `WindowsPortTable` (`GetExtendedTcpTable` listeners and `GetExtendedUdpTable`, IPv4 + IPv6), `WindowsElevation` ("Restart as administrator"), registrar, `NativeMethods.txt` |
| `src/Rivet.Platform.Fake/Processes/` | ~30 realistic processes (Chrome/VS Code trees, services, critical ones), a port table, fake elevation |
| `src/Rivet.App/Features/Processes/` | `ProcessesModule.cs` (`KillProcessModule`, `PortManagerModule`, both Settings pages, Command Bar provider), `KillProcessView`, `PortManagerView` |
| `tests/Rivet.Core.Tests/Processes/` | protection rules, tree walk with PID reuse, grouping, CPU math, sorting/filter, TCP/UDP v4/v6 table parsing (byte order, scope ids), port rules and browser URLs |
| `tests/Rivet.App.Tests/Processes/` | Kill Process panel + Settings page, Port Manager panel + Settings page (UDP on), light and dark; force kill through the service; Command Bar scoring |

## Implemented

### Kill Process
* List: name (`FileDescription`, else exe name), path, CPU % (share of the whole PC between two
  samples, like Task Manager; first sample shows "—"), memory (working set), PID, member count for groups.
  Filter by name or exact PID; sortable Process/CPU/Memory/PID headers (persisted, natural directions);
  refresh on appear and every 3 s while the window is active; refresh button.
* Grouping ("Group related processes", default on): each process goes under its highest validated
  ancestor running the same executable (Chromium/Electron trees). Parent links are trusted only when the
  parent was created before the child (Windows keeps stale PPIDs and reuses PIDs).
* Actions with confirmation: **Kill** (WM_CLOSE to the process's windows; a process without windows is
  terminated, and the confirmation says so), **Force Kill** (`TerminateProcess`), **Kill All "<name>"**,
  **Kill Process Tree** (deepest first, protected descendants skipped), **Restart** (windowed apps: capture
  path and arguments, close, wait ≤ 10 s, relaunch non-elevated from the executable's folder), **Copy PID**, **Copy Path**.
  Every action re-checks the creation time before acting (PID-reuse guard).
* Protection: PID 0 and 4, `Registry`, `Memory Compression`, `smss`, `csrss`, `wininit`, `winlogon`,
  `services`, `lsass`, `LsaIso`, `dwm`, `fontdrvhost`, this app, every process flagged critical, and any
  process whose identity cannot be read. Protected rows show a tooltip and disabled actions.
  `explorer.exe` can be killed and restarted.
* Access denied (elevated or other users' processes): the app explains and offers **Restart as
  administrator**; nothing is elevated otherwise.
* Command Bar category "Kill Process" (setting "Show in Command Bar", default on): processes by name
  or PID; activating asks before killing.
* Panel tile (compact list) and Settings page with both toggles and the admin note.

### Port Manager
* Listening TCP ports (IPv4 and IPv6) of **every user and service**, with a User column on the Settings
  page; optional "Show UDP sockets" (all open UDP sockets, since UDP has no listen state).
* Row: port (mono, bold), protocol badge, "All interfaces" badge for `0.0.0.0` / `::` with the spec's
  tooltip, process name, `PID • address • user`. Filter by port, process, PID or address.
* Identity: process creation times are read before and after the table read; only stable rows are
  killable. **Kill** / **Force Kill** (only while Kill Process is installed) go through Kill Process's
  identity-checked kill, then the table refreshes.
* Context menu: Copy port, Copy PID, Copy address:port, Open in browser (`0.0.0.0` → `127.0.0.1`,
  `::` → `[::1]`).
* Refresh on appear and with the button only (no timer). Panel tile and Settings page.

## Not implemented / deviations

* **No per-action elevated helper** (spec: one UAC helper that re-checks creation time and terminates):
  replaced by "Restart as administrator" for the whole app, to avoid a second elevated entry point.
* Console processes get no Ctrl+C/Ctrl+Break (`GenerateConsoleCtrlEvent` only reaches the caller's own
  console group); Kill terminates windowless processes, and the confirmation says so.
* Packaged apps show their executable's `FileDescription`, not the package display name.
* Restart starts the program in its executable's folder, not the original working folder (reading another
  process's working folder needs its PEB).
* macOS lists only the current user's ports; Windows lists all, with the User column (the spec's
  requested option).

## Risks

* `NtQuerySystemInformation` and `ProcessCommandLineInformation` are undocumented-but-stable NT APIs; a
  failure keeps the previous list and logs a warning.
* WM_CLOSE to an app that shows "Save changes?" leaves it running; the row stays until the next refresh.
* Restart relaunches non-elevated in the current session; an app that was elevated comes back without
  elevation (it cannot be reached when elevated anyway).

## Windows manual test checklist

1. Features hub: install Kill Process and Port Manager. Settings › Kill Process: the list fills, CPU
   values appear after ~3 s, sorting by each column works and is remembered.
2. Open Chrome with several tabs: one "Google Chrome" row with "Processes: N"; switch grouping off: one
   row per process.
3. Open Notepad with unsaved text, **Kill**: Notepad asks to save (polite close). **Force Kill**: gone at
   once.
4. Start `cmd /c ping -t localhost` in a console: **Kill Process Tree** on `cmd.exe` ends ping too.
5. **Restart** File Explorer (`explorer.exe`): taskbar comes back.
6. `lsass.exe`, `csrss.exe`, `System`: actions disabled with the protection tooltip.
7. Run Notepad as administrator, Kill it as a standard user: the access-denied message with **Restart
   as administrator**; after restarting elevated, Kill works.
8. Command Bar: type "notep" → Notepad under Kill Process; Enter asks before killing.
9. Port Manager: run `python -m http.server 8000` (or any dev server): port 8000 listed with python and the
   All interfaces globe; **Open in browser** opens `http://127.0.0.1:8000`; **Kill** stops it and the row
   disappears after refresh.
10. Turn on "Show UDP sockets": UDP rows (e.g. 5353, 1900) appear.
11. Uninstall Kill Process in the Features hub: Port Manager shows no kill buttons.
12. Light and dark themes, panel width for both tiles.

## Requests for shared code

None. Kill Process's `ProcessService` is also used by the Uninstaller (closing running copies) and by
Port Manager; it is registered with `TryAddSingleton` by each of those modules.
