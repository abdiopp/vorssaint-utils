# Package manager (`packageManager`)

Replaces the macOS Homebrew manager. Spec: `docs/specs/04-maintenance-and-sound.md` §3.4 (macOS) and
§3.4.7 (Windows mapping), §6.13.

Status: **implemented** on the winget CLI, compiled for `net10.0-windows10.0.22621.0`, exercised on
macOS through `FakeWingetRunner` (real-shaped tables, streamed progress). Not yet run against a real winget.

## Files

| Path | What |
|---|---|
| `src/Rivet.Core/PackageManager/` | `WingetClient` (detection, list/upgrade/search/show/source queries, operation requests, agreement gate, `winget export` id resolution), `WingetOutput` + `TextColumns` (table parser by header positions and display width, `show` parser, export JSON), `WingetProgress` (progress bars, byte counts, phases), `WingetErrors` (30 HRESULTs → plain text), `PackageOperationLane` (one operation at a time, log, cancel, auto-clear like macOS), `PackageManagerService` (installed/search/details/sources state with generation guards), `PackageArguments` (id/source/query validation), settings |
| `src/Rivet.Core/Processes/CommandRunner.cs` | `ICommandRunner` over `Process`: no shell, argument list, UTF-8, total and inactivity timeouts, cancellation that ends the process tree, output cap |
| `src/Rivet.Platform.Windows/PackageManager/` | `WindowsWingetLocator` (App Installer alias in `%LOCALAPPDATA%\Microsoft\WindowsApps`, then PATH), registrar |
| `src/Rivet.Platform.Fake/PackageManager/` | `FakeWingetRunner` (17 catalog packages, local ARP-only entries, streamed install/upgrade/uninstall output) |
| `src/Rivet.App/Features/PackageManager/` | `PackageManagerModule`, `PackageManagerView`, `OperationStatusView` (operation card, agreement card, winget-missing card; shared with App updates) |
| `tests/Rivet.Core.Tests/PackageManager/` | table parsing with captured-style output (localized headers, "…" truncation, CJK widths, progress spinners, summary lines, explicit-targeting sections), `show` parsing, progress parsing, error mapping, argument validation, the operation lane and the client against a fake runner |
| `tests/Rivet.App.Tests/PackageManager/` | panel snapshots (installed, details, running, done, search, agreement, missing) and Settings page, light and dark |

## Implemented

* **Detection**: `winget --version`; missing → explanation and a button to App Installer in the Store.
* **Agreements**: the msstore source terms and the region notice are shown; nothing that needs
  `--accept-source-agreements` runs until the person accepts in the app (stored as machine state).
  Package licenses are accepted per install/upgrade by the confirmation dialog that says so.
* **Installed** (`winget list`): name, id, version, available update, source; ARP-only entries marked
  "Local"; filter by source (All / winget / msstore); "Updates N" with **Update all** (confirmed).
* **Search** (`winget search --query`): results with installed state; source filter.
* **Details** (`winget show --id --exact`): description, version/latest, publisher, license, homepage,
  with **Install / Update / Uninstall** (each confirmed; uninstall is destructive-styled).
* **Operations**: `install/upgrade/uninstall --id <id> --exact --silent` (+ `--source` when known),
  `upgrade --all`, `source update` (confirmed). One at a time through the lane shared with App updates;
  live phase (preparing/downloading/installing/removing), percentage or byte counts, elapsed time, Cancel (ends the
  winget process tree), "Show details" log, plain-language errors with the raw code in the log; lists
  refresh afterwards. The panel stays open while an operation runs.
* **Sources** (Settings page): `winget source list` with **Update sources**.
* Panel tile, Settings page (App management), action `packageManager.open`.

## Not implemented / deviations

* **WinGet COM API** not used (unpackaged app; see `appUpdates.md`): CLI with a defensive parser.
* **Scoop / Chocolatey backends**: not implemented (optional in the spec).
* **Formulae vs casks** → replaced by a source filter; **dependency grouping** and **popularity
  analytics** dropped (winget has neither), as the spec suggests.
* **"Open Terminal" for interactive/sudo installs**: not offered. Installers elevate through UAC
  themselves; `--interactive` is not exposed.
* **Pins**: pinned packages are shown but winget refuses to upgrade them (mapped error "pinned"); there
  is no pin/unpin UI.
* Adding/removing sources is not offered (list and update only).

## Risks

* CLI output format and localization (see `appUpdates.md`). The parser uses the dashed separator and
  header column positions measured in display width; unknown layouts produce an error, never guesses.
* Long installs with no output for a while: the lane ends an operation after 15 minutes without any output;
  very slow installers could hit it (reported as failed, process tree ended).
* `--silent` installers that ignore the flag show their own UI; that is the installer's behaviour.

## Windows manual test checklist

1. Open the panel › Utilities › Package manager: on first use the agreement card; accept.
2. Installed tab lists apps winget recognizes, including ARP-only ones marked Local; the source filter
   narrows it; Updates count matches `winget upgrade`.
3. Search "notepad++": results; open details; **Install** → confirmation → progress with percentage →
   "Installed"; it appears under Installed.
4. **Uninstall** it from details: confirmation (destructive) → progress → gone from the list.
5. Start an install of a large package (e.g. `Microsoft.VisualStudioCode`) and **Cancel** during download:
   operation ends as cancelled, winget processes are gone (Task Manager).
6. **Update all** with at least two updates: one confirmation, then each package in turn.
7. Settings › Package manager › Sources: lists `msstore` and `winget`; **Update sources** runs.
8. Disconnect the network and search: a readable error, no crash.
9. Without App Installer (or with winget removed from PATH and the alias disabled under Settings › Apps ›
   Advanced app settings › App execution aliases): the missing card and Store button.
10. German display language: Installed and Search still parse.
11. Light and dark themes, panel width.

## Requests for shared code

None. `PackageManagerModule.AddWinget(services)` registers the winget services for App updates too;
`ICommandRunner` is registered by the Processes registrar (Windows) and by the fake registrar as
`FakeWingetRunner` (dev build: every external command goes to the pretend winget).
