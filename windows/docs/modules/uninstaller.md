# Uninstaller (`uninstaller`)

Spec: `docs/specs/04-maintenance-and-sound.md` §3.2 (macOS behaviour) and §3.2.11 (Windows mapping).

Status: **implemented**, compiled for `net10.0-windows10.0.22621.0` and exercised on macOS with the
fake platform (sample apps, files and registry). Not yet run on a Windows PC.

## Files

| Path | What |
|---|---|
| `src/Rivet.Core/Uninstaller/` | `InstalledApp` + `IInstalledAppsProvider`, `ArpRules` (which ARP entries are offered, uninstall-command parsing incl. `MsiExec /I{GUID}` → `/X`, quoted/unquoted paths, quiet mode), `AppFingerprint` (identity: trusted install folder, name tokens with Windows role words, executables, publisher, package family, other apps' tokens for exclusivity), `LeftoverScanner`, `LeftoverModels` (`IUninstallerPlatform`, registry refs), `UninstallerService` (List → Selected → Uninstalling → StillInstalled/Scanning → Results → Removing → Done) |
| `src/Rivet.Platform.Windows/Uninstaller/` | `WindowsInstalledApps` (HKLM 64/32-bit + HKCU Uninstall keys via explicit registry views, MSIX via `PackageManager.FindPackagesForUser`, icons from `DisplayIcon` or the closest scaled package logo, sizes), `WindowsUninstallerPlatform` (runs the uninstaller through the shell, waits for it and the copies it spawns from `%TEMP%`, `RemovePackageAsync` for MSIX, registry read/snapshot/delete, scheduled tasks via `Schedule.Service` COM, services), registrar |
| `src/Rivet.Platform.Fake/Uninstaller/` | ten sample apps with drawn icons, a pretend uninstaller (leaves a plugin folder behind), a small registry, a task and a service |
| `src/Rivet.App/Features/Uninstaller/` | `UninstallerModule` (panel tile, Settings page, tray item "Uninstall an app…", Command Bar provider), `UninstallerView` |
| `tests/Rivet.Core.Tests/Uninstaller/` | ARP entry rules, uninstall-command parsing, token rules, end-to-end leftover scan and removal on a temp tree with fakes |
| `tests/Rivet.App.Tests/Uninstaller/` | panel snapshots (list, selected, uninstalling, review, done) and Settings page (list, review), light and dark; Command Bar scoring |

## Implemented

* **App list**: ARP entries from the three Uninstall keys (skips `SystemComponent`, updates/hotfixes,
  `ParentKeyName`, entries without a name or uninstall command, and this app), plus user MSIX packages
  (skips frameworks, resource packages, system-signed packages and packages without a display name; a non-removable package that slips through fails at removal with Windows' own error). Name, publisher,
  version, size, install date, icon; search; sort by name/size/date/publisher (persisted).
* **Before uninstalling**: details, running copies of the app (close politely or force, through the
  Kill Process service), "Uninstall silently" (QuietUninstallString or `msiexec /x … /qb`), the
  not-reversible warning, and for per-machine apps an explanation plus "Restart as administrator".
* **Uninstall**: MSIX → `PackageManager.RemovePackageAsync`; MSI → `msiexec /x {ProductCode}`; EXE →
  `UninstallString`/`QuietUninstallString`. Started through the shell so the uninstaller raises its own UAC
  prompt; the app waits for its process tree (Inno Setup/NSIS relaunch themselves from `%TEMP%`), with an
  "I've finished" button. Then it verifies the ARP entry/package is gone; if not, nothing is offered.
* **Leftover scan** (spec §3.2.11 roots/evidence): install-folder residue and package data (exact);
  `%APPDATA%`, `%LOCALAPPDATA%`, `LocalLow`, `%ProgramData%` vendor/product folders (related, only when no
  other installed app shares the name/vendor); `%TEMP%` exact names; crash dumps and WER folders by
  executable name (exact); Start-menu and desktop shortcuts pointing into the install folder (exact);
  `HKCU\Software\<Vendor>\<Product>` and `HKLM\SOFTWARE\(WOW6432Node\)…` (related), `App Paths` and Run
  values pointing into the install folder (exact); scheduled tasks and services pointing into it are
  **listed for review only** with links to Task Scheduler and Services.
* **Review and removal**: only exact evidence starts ticked; files/folders go to the Recycle Bin through
  the Cleaner's verified recycler (identity re-checked); registry keys are exported to a `.reg` file
  first and then deleted; HKLM keys need the app to run elevated (otherwise they stay and a note says so).
  Done screen shows freed size, failures and "Show backup".
* Panel tile, Settings page (App management), tray menu "Uninstall an app…", Command Bar
  ("Uninstall Application" and "Uninstall <app>…", opt-in), action `uninstaller.open`. The panel stays open
  while an uninstall runs.

## Not implemented / deviations

* **Package-manager handoff** (spec §3.2.7 / step 6 of §3.2.11: "Uninstall with winget"): not
  implemented. The vendor uninstaller always runs; winget packages can be removed from the Package manager.
* **Restart Manager** is not used to close the app; running copies are found by executable path inside
  the install folder and closed through Kill Process (WM_CLOSE, then terminate on request).
* **Authenticode signer** is not part of the identity (names, publisher, install folder and package family
  are); the exclusivity rule compensates.
* ProgIDs / `HKCU\Software\Classes\Applications\<exe>` are not scanned.
* Scheduled tasks and services are review-only (no export/delete) by design.
* The app itself cannot be "moved to the Recycle Bin" on Windows: the copy says the uninstaller step
  cannot be undone (spec §3.2.11).
* `Support/Uninstaller.swift` (self-detach, §3.2.12) belongs to the installer, not this module.

## Risks

* Uninstallers that never exit or hand off to a process outside `%TEMP%` keep the wait open: the person
  clicks "I've finished"; verification then decides.
* Program Files residue of per-machine apps needs a UAC prompt from the shell's recycle operation; if the
  person declines, those items fail (reported, not deleted).
* MSIX removal of a package installed for several users removes it only for the current user.
* Name heuristics can still offer a folder of a same-named app that is not registered anywhere; such
  items are always unticked and show their full path.

## Windows manual test checklist

1. Install 7-Zip (MSI from 7-zip.org) and Notepad++ (EXE, per machine), and a Store app (e.g. a small
   game). Open Settings › Uninstaller: all three are listed with icons, publisher, version, size.
2. Search "note", sort by size/date: list updates; sort is remembered after restart.
3. Select Notepad++ while it is running: "running" note; **Close** quits it (unsaved changes prompt
   appears in Notepad++); **Force quit** ends it.
4. Uninstall Notepad++: its uninstaller window appears with a UAC prompt; after it finishes the review
   lists `%APPDATA%\Notepad++` unticked and Start-menu entries ticked. Tick the AppData folder, **Move to
   Recycle Bin**: it is in the bin; the HKLM key note appears as a standard user.
5. Uninstall 7-Zip with "Uninstall silently" on: `msiexec /x` runs with a basic progress window only.
6. Cancel an uninstaller mid-way: the app reports the app is still installed and offers no leftovers.
7. Uninstall the Store app: package removed without a window; `%LOCALAPPDATA%\Packages\<PFN>` residue (if
   any) is ticked.
8. Restart the app as administrator, uninstall an app with HKLM keys: tick the HKLM key; after removal a
   `.reg` backup exists (Show backup) and double-clicking it restores the key.
9. Tray right-click › "Uninstall an app…" opens the page; Command Bar (after enabling it) finds
   "Uninstall Notepad++…".
10. Panel tile: the flow works in the 340 px panel and the panel stays open while the uninstaller runs.
11. Light and dark themes.

## Requests for shared code

None beyond the namespace note in `cleaner.md` (code lives in `Rivet.Core.Maintenance.Uninstaller`).
The Uninstaller reuses the Cleaner's platform services (`ICleanerFileSystem`, `IRecycler`,
`IKnownFolders`, `ICleanerPlatform`) and `CleanerModule.BackupFolder`; all are registered by registrars
regardless of which features are installed, so the modules must be merged together.
