# Cleaner (`cleaner`)

Spec: `docs/specs/04-maintenance-and-sound.md` §3.1 (Cleaner), §3.7 (WhatsApp downloads), §6.1–6.3, §6.15.

Status: **implemented**, compiled for `net10.0-windows10.0.22621.0` and exercised on macOS with the
fake platform. Not yet run on a Windows PC.

## Files

| Path | What |
|---|---|
| `src/Rivet.Core/Cleaner/` | `CleanerScanner` (categories), `CleanerService` (Idle → Scanning → Results → Cleaning → Done, removal guard `MayRemove`), `CleanerScheduler` (automatic cleanup), `CleanerRules` (pure rules: temp age, browser tables, shortcut/command parsing, screenshot names, iPhone backup plist), `SafeFiles` (`ICleanerFileSystem`, `IRecycler`, `SafePaths`, `CriticalPaths`), `RegistryBackup` (.reg writer), `ShellLink` (.lnk target reader), `WhatsAppDownloads`, settings and schedule math |
| `src/Rivet.Platform.Windows/Cleaner/` | `WindowsFileSystem` (FileIdBothDirectoryInfo enumeration, allocation sizes, file ids, reparse/cloud flags, in-use check), `WindowsRecycler` (verified `IFileOperation` recycling), `WindowsCleanerPlatform` (Recycle Bin query/empty, Run/RunOnce entries + .reg backup, MSIX family check, drive kinds), known folders, Mark-of-the-Web reader, power/clock events, registrar, `NativeMethods.txt` |
| `src/Rivet.Platform.Fake/Cleaner/` | in-memory Windows-style file system with a realistic sample profile ("Alex"), fake recycler/platform |
| `src/Rivet.App/Features/Cleaner/` | `CleanerModule`, `CleanerView` (panel + Settings), `CleanerScheduleEditor`, `WhatsAppDownloadsView`, `MaintenanceUi` (shared UI helpers for all five maintenance modules) |
| `tests/Rivet.Core.Tests/Cleaner/` | rules, paths, critical set, schedule math (DST gaps), shortcut parsing, .reg output, WhatsApp rules, end-to-end scan/clean on a temp tree with a recording recycler |
| `tests/Rivet.App.Tests/Cleaner/` | panel snapshots (idle, results, done) and the Settings page, light and dark |

Namespaces are `Rivet.Core.Maintenance.Cleaner` / `Rivet.App.Features.Cleaner` (see "Requests for shared code").

## Implemented

* **Categories** (spec §3.1.9 table), each only from fixed locations, never following reparse points,
  never offering cloud placeholders, never listing the critical set:
  * Leftovers from uninstalled apps: `%LOCALAPPDATA%\Packages\<PFN>` whose family is not installed
    (unknown = installed), orphaned `%LOCALAPPDATA%\Programs\<X>` folders (no ARP entry, no executable,
    untouched), Start-menu shortcuts whose target is gone from a fixed drive (common Start menu only when
    elevated). Never pre-checked.
  * Orphaned startup items: HKCU/HKLM `Run` and `RunOnce` (64- and 32-bit views) whose every referenced
    executable is missing from a fixed drive, plus broken shortcuts in the Startup folders. Values are
    exported to a `.reg` file before deletion. HKLM values only when elevated.
  * Caches (Safe): `%TEMP%` top-level entries untouched for 24 h, Chromium-family browser caches per
    profile (Edge, Chrome, Brave, Vivaldi), Firefox `cache2`,
    `INetCache` (minus exclusions), D3D/GPU shader caches, npm/pip/Yarn/NuGet HTTP caches.
    Browsers are skipped while they run and named in a "Not scanned while running" line.
  * Other caches (Optional): Explorer `thumbcache_*`/`iconcache_*` (locked ones skipped silently),
    browser Service Worker caches, offline media caches; `C:\Windows\Temp` (admin, unchecked).
  * Crash dumps and error reports: `%LOCALAPPDATA%\CrashDumps\*.dmp`, user WER archive/queue; when
    elevated also machine WER, `Minidump`, `MEMORY.DMP`, `CbsPersist_*` logs.
  * Developer caches: Visual Studio `ComponentModelCache`, JetBrains `caches` (skipped while the IDE
    runs), Gradle/NuGet packages as Optional.
  * Recycle Bin: `SHQueryRecycleBinW` / `SHEmptyRecycleBinW`, shown as a **permanent** row, emptied first.
  * iPhone backups (iTunes and Apple Devices locations, device name/date from `Info.plist`).
  * Forgotten screenshots: default Windows screenshot names whose name matches the creation time,
    older than the chosen age (attended scans only).
* **Removal**: files and folders go to the Recycle Bin through `WindowsRecycler`, which pre-checks each
  item (fixed drive, bin enabled for that drive, room left) and confirms through the progress sink that a
  recycled copy was created; anything that cannot be recycled is refused and counted as failed, never
  deleted. `MayRemove` re-checks identity (volume serial + 128-bit file id), the plain path chain, the
  category root, depth, the critical set and the leftover evidence right before each removal.
* **Elevation**: nothing runs elevated. Admin-only locations are skipped and a note offers
  "Restart as administrator". Program-Files residue picked in a manual clean may raise the shell's own UAC
  prompt (IFileOperation), never in the scheduled run.
* **Automatic cleanup**: Off / daily / weekly / monthly at a time (12/24 h aware), in-app timer
  re-armed on resume and clock changes; unattended scans clean only the Safe groups and post a toast
  (also when nothing was found). Last run stats are machine state.
* **WhatsApp downloads** (review only, off by default): files in Downloads whose Mark of the Web
  (`Zone.Identifier` `HostUrl`/`ReferrerUrl`) points at a WhatsApp host are "Confirmed"; files that merely
  carry WhatsApp's default names are "Name only" and never pre-selected. File-type and retention filters,
  per-file Keep (exclusions by file identity), recycle the ticked ones.
* Panel tile (Utilities "Cleaner", panel kept open while scanning/cleaning), Settings page
  (App management), actions `cleaner.open` / `cleaner.scan`.

## Not implemented / deviations

* **Windows Update / Delivery Optimization caches and Disk Cleanup handlers**: not cleaned by the app.
  These are permanent, need services stopped and admin rights; the Settings page has a card that opens
  Windows' own Storage › Temporary files (`ms-settings:storagesense`). Deliberate.
* **Scheduled tasks and services** are not part of "Orphaned startup items" (Run/RunOnce and the Startup
  folders only). Removing them safely needs an elevated helper and task/service export; left for later.
* **No elevated helper process**: the spec proposes a UAC helper per batch. The port instead offers
  "Restart as administrator" (the whole app relaunches elevated) because a second elevated entry point
  doubles the attack surface. Over-the-shoulder elevation therefore recycles into the admin account's bin
  (documented risk below).
* **"Restore" for removed registry values** is the `.reg` backup file ("Show backup" opens the folder;
  double-clicking the file restores it) rather than an in-app button.
* **WhatsApp**: no automatic cleanup and no organizer on Windows (§3.7.5–3.7.6): without a per-app
  quarantine record the evidence is too weak to delete unattended. Name-only matches need a manual tick.
* Screenshot "not opened" cannot be known on Windows (last-access is often disabled): age uses
  max(created, modified) and the caption says so.

## Risks

* `IFileOperation` behaviour on drives with unusual bin settings (per-volume `MaxCapacity` missing,
  `NukeOnDelete`) is reasoned from documentation; `FOF_WANTNUKEWARNING` is the last safety net. Verify on
  a PC (checklist below).
* Over-the-shoulder elevation (standard user + another admin's credentials) recycles into the other
  account's Recycle Bin.
* Browser cache folder names change between browser versions; unknown folders are simply not offered.
* Explorer thumbnail caches are nearly always locked; expect most to be skipped.

## Windows manual test checklist

1. Fresh profile, Features hub: Cleaner installed. Open the panel › Utilities › Cleaner. Click **Scan**:
   progress per category, panel stays open while you click elsewhere.
2. With Edge running, scan: Edge caches are absent and "Not scanned while running: Microsoft Edge" shows.
   Close Edge, scan again: Edge caches appear under Caches with profile names.
3. Create `%TEMP%\rivet-test\a.txt` (new) and an old folder (set its time back 2 days with PowerShell
   `(Get-Item …).LastWriteTime = (Get-Date).AddDays(-2)`): only the old one is listed.
4. Untick everything except one temp folder, **Clean**: it appears in the Recycle Bin and can be restored.
5. Put a file on a USB stick folder that is in a scanned location (or a network share mapped into
   `%TEMP%` via a junction): it must never be offered (junction) / refused, not deleted (no bin).
6. Set the C: Recycle Bin to "Don't move files to the Recycle Bin": cleaning must refuse every item and
   report failures; nothing is deleted.
7. Add a `HKCU\…\Run` value pointing at `C:\nope\missing.exe`: it shows under Orphaned startup items;
   clean it; a `.reg` file appears under the app's local `Backups\Cleaner\<timestamp>` folder (Show backup); double-click restores it.
8. Recycle Bin row: shows size/count, labelled permanent; cleaning empties it.
9. As a standard user: the admin note shows; **Restart as administrator** relaunches elevated (UAC);
   Windows Temp and machine WER now appear.
10. Automatic cleanup: set Daily at a time two minutes ahead; wait; a toast reports the result; Settings
    shows last run. Sleep the PC across the time; on resume it runs once.
11. Forgotten screenshots: Win+PrtScn a few times, set age to the shortest option after moving file dates
    back: they are listed unticked.
12. WhatsApp: turn it on, download an image from web.whatsapp.com in Edge and copy a file renamed
    `WhatsApp Image 2026-01-01 at 10.00.00.jpg` into Downloads: the first is Confirmed, the second
    Name only and unticked; Keep removes a file from the selection permanently.
13. Light and dark Windows themes: panel and Settings page readable.

## Requests for shared code

1. ~~**Namespaces**~~: done at integration — `Rivet.Core.Maintenance.<Area>` is kept and
   `MODULE_GUIDE.md`'s example now uses it and explains the clash.
2. `MaintenanceUi` (shared helpers for the five maintenance modules) lives in `Features/Cleaner/` under
   namespace `Rivet.App.Features.Maintenance`; if the integrator prefers, move it to a shared controls
   folder. Nothing else depends on it. (Kept where it is: only these five modules use it.)
