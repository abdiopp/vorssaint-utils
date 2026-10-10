# App updates (`appUpdates`)

Spec: `docs/specs/04-maintenance-and-sound.md` §3.3 (macOS) and §3.3.11 (Windows mapping), §6.4 (version
comparison).

Status: **implemented** on the winget CLI, compiled for `net10.0-windows10.0.22621.0`, exercised on
macOS through a pretend winget that prints real-shaped tables. Not yet run against a real winget.

## Files

| Path | What |
|---|---|
| `src/Rivet.Core/AppUpdates/` | `AppUpdatesService` (check lifecycle, sources, rules, selection, schedule, notifications, hand-offs), `AppUpdateModels` (rows, merge/reconcile, rules, settings), `VersionComparer` (ported from §6.4), `ElectronFeeds` (app-update.yml / latest.yml parsing and URL safety), `OnlineUpdateSource` (feed fetcher with strict limits) |
| `src/Rivet.Core/PackageManager/` | the winget layer shared with the Package manager (see `packageManager.md`) |
| `src/Rivet.App/Features/AppUpdates/` | `AppUpdatesModule` (panel tile, Settings page, actions, controller), `AppUpdatesView` |
| `tests/Rivet.Core.Tests/AppUpdates/` | version comparison vectors, rules, merge/selection, Electron feed parsing and URL safety |
| `tests/Rivet.App.Tests/AppUpdates/` | panel snapshots (unchecked, results, rules, updating, agreement, winget missing) and Settings page, light and dark; Store hand-off; last-source rule; icon matching |

## Implemented

* **Sources** (each switchable, at least one stays on):
  * *winget* (macOS "Homebrew apps", same setting key): `winget upgrade --include-unknown` parsed by
    column positions from the header (localized headers, display-width padding, "…" truncation, trailing
    summary lines, explicit-targeting/pinned sections); falls back without `--include-unknown` on old
    winget. Truncated ids are resolved through `winget export` JSON; rows that stay unresolved cannot be
    updated here (tooltip explains). Pinned and explicit-only packages are not offered, as winget does.
    This app's own package is never offered.
  * *Microsoft Store* (macOS "App Store"): `msstore` rows from the same winget pass; updating hands off to
    the Store (`ms-windows-store://pdp/?productid=<id>` for one, the Downloads and updates page for several).
  * *Other installed apps* (macOS "online catalog"): Electron apps whose
    `<InstallLocation>\resources\app-update.yml` names a public generic/GitHub feed; `latest.yml` gives the
    version. One request per feed, four in flight, 60 s for the pass; no cookies/credentials, https only,
    5 redirects, 2 MB cap. Apps winget already covers are skipped. "Open" starts the app so its own
    updater runs.
* **List**: per-row source badge, icon (borrowed from the installed-apps list), installed → latest
  version, select all/clear, "Update N" for the selection (confirmation that names the license
  acceptance), per-row Update/Store/Open, context menu with **Skip this version** and **Don't check this
  app**; the rules list can remove each rule.
* **Installing**: `winget upgrade --id <id> --exact --silent --accept-package-agreements
  --accept-source-agreements` one after another through the shared operation lane, live phase/percentage
  per package, cancel (ends the process tree), plain-language error mapping of winget HRESULTs, the list
  re-checks itself afterwards.
* **Agreements**: winget's source agreements are shown in the app and must be accepted by the person once
  ("Agree and continue") before any winget query passes `--accept-source-agreements`.
* **winget missing**: explained, with a button to the App Installer Store page.
* **Schedule**: Off / Daily / Weekly (`appUpdatesCheckFrequency`), first run 3 minutes after launch, an
  in-app timer; a toast for findings never announced before; surfaces re-check when older than 10 min.
* Panel tile, Settings page (App management), actions `appUpdates.open` / `appUpdates.check`.

## Not implemented / deviations

* **WinGet COM API** (`Microsoft.Management.Deployment`) is not used: it needs the packaged WinRT
  projection and activation through App Installer, which an unpackaged app cannot rely on. The CLI is used
  with a defensive parser and `winget export` JSON for id resolution (winget has no JSON output for
  `upgrade`). Revisit when the app is packaged.
* **Public catalog download** (macOS `cask.json`): dropped — winget's correlation with Add/Remove
  Programs covers that role (spec §3.3.11).
* **WinSparkle / Squirrel / Velopack** feeds are not discovered (their URLs are set at runtime).
* Store updates are not installed in-app (`AppInstallManager` needs privileges); they hand off to the Store.
* macOS "Monthly" is not offered for checks (the macOS app has Off/Daily/Weekly only).

## Risks

* winget's text output changes between versions and languages. The parser keys on the dashed separator
  line and header positions, not header words; tests include captured-style tables with localized headers,
  "…" truncation and wide characters. A future winget may still break it: failures show winget's message
  and nothing is updated.
* Machine-scope installers raise UAC prompts during `--silent` upgrades; a declined prompt is reported as
  a failed update.
* Electron feeds are third-party servers; requests are limited as described above.

## Windows manual test checklist

1. Make sure App Installer is current (`winget --version`). Install an old version of a small app with
   winget (e.g. `winget install 7zip.7zip --version 23.01`).
2. Open the panel › Utilities › App updates: the agreement card appears on first use; **Agree and
   continue**; the check lists 7-Zip with `23.01 → 24.xx` and the winget badge.
3. Update 7-Zip from its row: progress shows Downloading → Installing with percentages; a UAC prompt
   appears for the machine installer; afterwards the list re-checks and 7-Zip is gone.
4. **Skip this version** on another row: it disappears and the rule shows under Update rules; remove the
   rule and **Check now**: it returns.
5. Store row (any Store app with a pending update, if available): **Store** opens its Store page.
6. An Electron app with `resources\app-update.yml` (e.g. an older Obsidian or another electron-builder
   app installed per user): it shows with the Online badge; **Open** starts the app.
7. Switch off winget and Store sources: the last remaining switch is disabled.
8. Set the schedule to Daily, restart the app, wait 3 minutes: a toast reports updates once; it does not
   repeat for the same versions.
9. Rename `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe` access (or test on a PC without App
   Installer): the "winget is not installed" state with the Store button.
10. Change Windows display language to German and check again: rows still parse.
11. Light and dark themes, panel width.

## Requests for shared code

None. Depends on the Package manager's winget layer (`PackageManagerModule.AddWinget`) and on the
Uninstaller's `IInstalledAppsProvider` for install locations and icons; merge the modules together.
