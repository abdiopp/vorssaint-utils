# Spec 04 — Maintenance tools and Sound (Windows port)

> Source of truth: `vorssaint-utils` at commit `b6d8d2db` (main). This document describes the shipped macOS behavior precisely enough to rebuild it without reading the Swift code, then proposes Windows mappings. It is stack-neutral: it names Windows APIs and system locations, never a UI framework.
>
> Files read for this area: `Services/Cleaner/*`, `Services/Uninstall/*`, `Support/Uninstaller.swift`, `Services/AppUpdates/*`, `Services/Homebrew/*`, `Services/PortManager/*`, `Services/KillProcess/*`, `Services/ManagedDownloads/*`, `Services/DiskImageInstaller/*`, `Services/Audio/*`, `Services/QuickTools/MicMute*`, `Services/InstalledApps.swift`, `Services/ResponsibleProcess.swift`, the matching `UI/*` views, `Core/Defaults.swift`, `Core/FeatureCatalog.swift`, the English string tables, `Core/SettingsBackupSupport.swift`, `App/FeatureRuntime.swift`, and the tests listed in §8.6.

---

## 0. Conventions and glossary

- **MUST / SHOULD / MAY** carry their usual meaning for the Windows build.
- "**macOS:**" marks shipped behavior; "**Windows:**" marks a proposed mapping. Where no marker is given, the rule is platform independent and should be ported as is.
- Preference keys are quoted exactly as stored (`cleanerScheduleFrequency`). Keeping the same names on Windows keeps settings backups comparable.
- Sizes are bytes. The UI uses the platform file-size formatter (macOS `ByteCountFormatter` `.file` style: decimal units, 1 KB = 1000 B). Windows SHOULD use `StrFormatByteSizeEx` or equivalent and keep the same rounding everywhere in a feature.
- "Main thread" = the UI thread. Every blocking system call in this area runs off it.

| macOS term | Meaning in this spec | Closest Windows concept |
|---|---|---|
| Bundle ID (`com.vendor.app`) | Reverse-DNS identity of an app, used as the persistence and ownership key | Win32: normalized main exe path, ARP key / MSI ProductCode; packaged: AppUserModelID / PackageFamilyName (PFN) |
| `.app` bundle | Self-contained app folder | Install folder (`InstallLocation`) or MSIX package |
| Trash / "move to Trash" | Reversible delete | Recycle Bin (`IFileOperation` with recycle flags) |
| `~/Library/...` | Per-user app data root | `%APPDATA%` (Roaming), `%LOCALAPPDATA%`, `%USERPROFILE%\AppData\LocalLow` |
| `/Library/...` | Machine-wide app data root | `%ProgramData%`, `HKLM\SOFTWARE` |
| LaunchAgents / LaunchDaemons / Login Items | Startup entries | `Run`/`RunOnce` registry values, Startup folders, Scheduled Tasks, Services |
| Homebrew formula / cask | CLI package / GUI app package | winget package (no split); Scoop main vs extras bucket |
| Menu bar panel ("menu panel") | Popover from the status icon that hosts utilities | Tray (notification area) flyout window |
| Full Disk Access (FDA) | Privacy grant needed to read some app data | Administrator elevation (UAC) for machine areas; no per-app grant |
| Finder | File manager, also the elevation path for protected trash moves | Explorer; elevated helper process |
| Spotlight | File metadata index | Windows Search index (optional) |
| CoreAudio HAL | Device/volume system | MMDevice API + WASAPI |
| Process tap + aggregate device | macOS mechanism used to intercept one app's audio | No equivalent; Windows exposes per-app volume natively (`ISimpleAudioVolume`) |

---

## 1. Overview

This area covers two families of features.

### 1.1 Maintenance tools

On-demand utilities. They open in place inside the tray panel's **Utilities** section (each one replaces the utility list until closed), several also open in the **Quick Launcher** floating panel (Cleaner, Uninstaller, Homebrew), each has a **Settings page**, and two integrate with the **Command Bar** (Uninstaller, Kill Process). Port Manager depends on Kill Process for its kill buttons.

They share one safety model, which the Windows build MUST keep:

1. **Review first.** Every removable item is listed with its full path and size before anything happens. Uncertain finds start unchecked.
2. **Reversible removal.** Files go to the Trash, never deleted in place. The only permanent action is the explicit "Empty Trash" row (labeled as permanent).
3. **Never guess against a living app.** A leftover needs strong evidence that its owner is gone. Plain names are never proof in the general Cleaner.
4. **Scoped roots.** Items come only from fixed, well-known locations. Symbolic links are never followed.
5. **Bound to identity.** Each item records its file identity (device + inode) at scan time; removal re-checks it, so a path swapped between review and removal is refused.
6. **Nothing runs at rest.** Scans run only when a person opens the tool or a schedule they armed fires. No timers or observers exist while a feature or its schedule is off.
7. **One operation per tool**, cancellable; late results from a cancelled run are discarded by a token.

### 1.2 Sound

A shared audio-device service (device list, default output, default "system sounds" output, default input, output volume/mute) feeds the **Volume mixer**, the **Output switcher**, and **Audio device priority**. Separate services handle **Mute all microphones** and the **Precise volume roller**. Principles:

- **Fail open.** An app is never left muted by a broken audio path. A dead path gets one replacement, then the app is left untouched.
- **Never block the UI thread on the audio system.** All device reads and writes run on a dedicated serial queue; notifications only schedule a refresh.
- **Coalesce notifications** (bursts fold into one refresh, 0.2 s).
- **Give back what the app changed**, but only while the system still holds the value the app set (input device on quit, speaker volume after headphones return).

### 1.3 Feature availability ("Features hub")

Each feature can be uninstalled from the Features hub (`featureAvailable.<id>`, Bool). Uninstalled = hidden everywhere and its service torn down.

| Feature id | Installed by default | Notes |
|---|---|---|
| `cleaner` | yes | Also hosts WhatsApp downloads (own sub-switch) |
| `uninstaller` | yes | |
| `homebrew` | yes | |
| `appUpdates` | yes | |
| `portManager` | no (opt-in) | Kill buttons need `killProcess` |
| `killProcess` | no (beta, "Experimental" badge) | |
| `diskImageInstaller` | no (opt-in) | |
| `mixer` | yes | Volume mixer, mic picker, headphone guard, precise roller |
| `soundOutputSwitcher` | yes | Its own enable switch is off by default |
| `audioPriority` | no (opt-in) | Both directions enabled on first install |
| `micMute` | yes | |
| `musicBlock` | yes | Its own enable switch is off by default |

---

## 2. Feature inventory

Verdicts: **Port** (same behavior), **Port\*** (same intent, changed mechanics), **Redesign** (intent kept, evidence or flow must change), **Drop**.

| # | Capability | Surfaces (macOS) | Windows verdict |
|---|---|---|---|
| M1 | Cleaner scan/review/clean (8 categories) | Panel utility, Quick Launcher, Settings › Cleaner | Redesign categories (§3.1.9) |
| M2 | Automatic cleanup schedule + notification | Cleaner idle card | Port |
| M3 | Forgotten screenshots | Cleaner category + age card | Redesign (weaker evidence) |
| M4 | WhatsApp downloads review + automatic cleanup | Settings › Cleaner › WhatsApp tab, panel card | Redesign or Drop (§3.7.7) |
| M5 | WhatsApp organizer (experimental) | same | Port only if M4 survives |
| M6 | Uninstaller (app + leftovers review) | Panel, Quick Launcher, Settings, Command Bar (opt-in) | Redesign (§3.2.11) |
| M7 | Homebrew-managed uninstall handoff | inside M6 | Port\* (winget/Scoop/Choco) |
| M8 | App updates (package manager, store, developer feeds, catalog) | Panel, Settings › App updates | Port\* (winget + Store + feeds) |
| M9 | Background update check + notification | Settings › App updates | Port |
| M10 | Homebrew manager (search/install/remove/upgrade/update) | Panel, Quick Launcher, Settings › Homebrew | Port\* as winget manager |
| M11 | Port manager | Panel, Settings › Port Manager | Port |
| M12 | Kill process (kill, force, all-by-name, tree, restart) | Settings › Kill Process, Command Bar | Port |
| M13 | Disk image installer | Background on volume mount | Drop |
| M14 | `--uninstall` self-detach CLI (`Support/Uninstaller.swift`) | `Tools/uninstall.sh` | Port\* as installer custom action (§3.2.12) |
| S1 | Per-app volume 0–100% + mute | Panel › Volume mixer, Settings, Dynamic Island | Port (native API) |
| S2 | Boost 100–200% with peak limiter | inside S1 | Drop for v1 (no driverless path) |
| S3 | Per-app output routing | per-row output menu | Port\* (undocumented API) |
| S4 | App list: discovery, playing dot, hide, hide-inactive, pin, reorder | Volume mixer | Port |
| S5 | System output picker ("all apps"), output volume slider | Volume mixer header | Port\* (undocumented default-device API) |
| S6 | System sounds (alerts) output picker | Volume mixer header | Redesign or Drop |
| S7 | Microphone picker (preferred input) + input level slider | Volume mixer header | Port |
| S8 | Lower volume when headphones disconnect | Mixer options | Port\* (detection caveat) |
| S9 | Precise volume roller (finer key steps) | Mixer options | Port\* (lower value) |
| S10 | Output switcher (shortcut cycles chosen outputs) | Mixer options, Settings | Port |
| S11 | Mute all microphones + shortcut + tray badge | Quick Tools, panel, island, launcher | Port |
| S12 | Audio device priority (output + input lists) | Mixer options, panel section, Settings | Port |
| S13 | Music app blocker | Settings › General › Media keys | Drop |
| S14 | AirPlay per-app route (macOS 27+) | per-row output menu | Drop |

---

## 3. Detailed behavior

### 3.1 Cleaner

#### 3.1.1 States and surfaces

State machine (one shared instance): `Idle → Scanning(category) → Results → Cleaning → Done(freed, failed) → (Scan again) → Idle`. Any state except Cleaning can be reset to Idle ("Cancel", close button). Closing the panel while Scanning cancels the scan; a running clean is left to finish.

**Idle screen** (top to bottom):
- Sparkles glyph (animated only while busy), title "Clean up your Mac", caption "Scans for leftovers from uninstalled apps, caches, logs and the Trash. You review everything first and removed items go to the Trash."
- Prominent **Scan** button.
- **Automatic cleanup** card (§3.1.7). In the panel it folds to one line: clock icon, "Automatic cleanup", summary "Off"/"Daily"/"Weekly", chevron. On the Settings page it is always open.
- **Forgotten screenshots** card: camera icon, label, age picker (`Off`, `After 7/14/30/60/90 days`, plus the stored value if it is not one of these). Settings page adds caption "The scan lists the screenshots you have not opened for this long, unchecked, with their total size."
- Settings page only, when WhatsApp downloads is off: opt-in card with toggle "WhatsApp downloads" and caption "Keeps WhatsApp files in Downloads under control".
- Panel only, when WhatsApp downloads is on: a folding card (title, summary "Off" or next run time), expanded shows last run line, next run line and a **Manage…** button that opens Settings › Cleaner on the WhatsApp tab.
- A Full Disk Access note when the grant is missing (Windows: replace with a "scan system locations as administrator" note, §3.1.9).

**Settings › Cleaner page**: when WhatsApp downloads is enabled, a segmented control at the top switches between "Cleaner" and "WhatsApp downloads" (§3.7). A panel surface can deep-link to a tab (one-shot hint).

**Scanning screen**: animated glyph, "Scanning…", a second line with the display-group title being scanned, **Cancel**.

**Results screen**:
- Header: glyph, "Cleaner", "<total size> found", close (×) = reset.
- No items: green seal, "Nothing to clean. Your Mac is tidy."
- Otherwise two sections, **"Safe cleanup"** then **"Optional, review first"**, each listing only non-empty display groups (§3.1.4). A group row has a checkbox (on when every item in the group is included; toggling sets all), icon, title, caption, group size; it expands to item rows: checkbox, file name (middle-truncated), parent path with the home folder shown as `~` (head-truncated), size; context menu **Reveal in Finder** (Windows: "Show in Explorer", `SHOpenFolderAndSelectItems`).
- Group notes inside the expansion: Leftovers shows an orange "Found by analysis and left unchecked. Check the path before ticking."; Orphaned startup items shows "The entry under Login Items disappears after restarting the Mac."
- Footer: "%d of %d selected", **Cancel**, prominent **Clean <selected size>** (disabled at 0 selected).
- In the panel the list does not own a scroll view; it scrolls with its host so the footer stays reachable.

**Cleaning screen**: "Cleaning…", no cancel.

**Done screen**: check icon, "Done!", "<freed> removed", "Items went to the Trash and can be recovered from there.", orange "Some items couldn’t be moved to the Trash." when `failed > 0`, **Scan again** (→ Idle).

#### 3.1.2 Scan pipeline

`scan(attended: Bool)` — `attended` is true for a scan a person started, false for the scheduled pass.

1. Ignored if already Scanning. Creates a new token and cancellation flag, clears items.
2. On a background thread: build the **installed-apps oracle** once (§3.1.5).
3. Run categories in this fixed order, checking cancellation before each and publishing the current category: `leftovers`, `loginItems`, `caches`, `logs`, `developer`, `trash`, `deviceBackups`, then `screenshots` **only when attended and the screenshot age setting > 0** (an unattended scan never reads the user's own folders).
4. Each category's items are sorted by size, largest first, and appended as soon as that category finishes.
5. Paths claimed by `leftovers` are excluded from `caches` and `logs` ("one path, one row, one decision").
6. Results are delivered only if the token still matches. Cancelling mid-scan stops at the next category boundary and delivers nothing.

Category raw values are persisted identities: `leftovers=0, loginItems=1, caches=2, logs=3, developer=4, trash=5, deviceBackups=6, screenshots=7` (append only).

#### 3.1.3 Categories (macOS rules)

| Category | Locations | Item = | Inclusion rules | Pre-checked | Detail line |
|---|---|---|---|---|---|
| Leftovers from uninstalled apps | Direct children of each of 27 roots, under both `~/Library` and `/Library`: `Application Support`, `Caches`, `Preferences`, `Preferences/ByHost`, `Saved Application State`, `HTTPStorages`, `WebKit`, `Logs`, `Containers` (reads container metadata), `Cookies`, `PreferencePanes`, `Internet Plug-Ins`, `Services`, `QuickLook`, `Spotlight`, `Input Methods`, `Screen Savers`, `ColorPickers`, `Widgets`, `Address Book Plug-Ins`, `Contextual Menu Items`, `Safari/Extensions`, `Automator`, `CoreImage`, `Dictionaries`, `Components`, `Audio/Plug-Ins` | entry | not dot-prefixed; not a symlink; not `*.localized`; owner ID derivable (below); owner not protected; owner has no living owner | **No** | owner ID |
| Orphaned startup items | `*.plist` in `~/Library/LaunchAgents`, `/Library/LaunchAgents`, `/Library/LaunchDaemons` | file | not a symlink; parses; `Label` not protected; at least one executable referenced (`Program`; `ProgramArguments[0]`; `BundleProgram` only if absolute); **every** referenced executable missing (a path under `/Volumes/` counts as present: the disk may be unplugged); `Label` has no living owner | **Yes** | label, else file name |
| Caches | Direct children of `~/Library/Caches` (user only) | entry | not dot-prefixed; not an excluded prefix (§6.1); not `*.localized`; not claimed by leftovers; size > 0 | per rule §6.1 | entry name |
| Logs | Direct children of `~/Library/Logs` except `DiagnosticReports`, plus `~/Library/Logs/DiagnosticReports` as one item | entry | not dot-prefixed; not excluded; not `*.localized`; not claimed; size > 0 | **Yes** | entry name |
| Developer junk | `~/Library/Developer/Xcode/DerivedData`, `…/Xcode/DocumentationCache`, `~/Library/Developer/CoreSimulator/Caches`, `…/Xcode/iOS DeviceSupport`, `…/Xcode/watchOS DeviceSupport`, `…/Xcode/tvOS DeviceSupport` | folder | exists; size > 0 | **Yes** | folder name |
| Trash | `~/.Trash` | one row | at least one visible (non-dot) entry; size = sum of visible entries; size > 0 (hidden bookkeeping like `.DS_Store` never makes an "empty" Trash look full) | **No** | — |
| iPhone backups | Directories in `~/Library/Application Support/MobileSync/Backup/` | folder | directory; not dot-prefixed; size > 0 (unreadable without FDA → category silently empty) | **No** | `"<Device Name>, <Last Backup Date (medium)>"` from the backup's `Info.plist`, else folder name |
| Forgotten screenshots | Top level of the screenshot folders (§3.1.8) | file | attended scans only; rules §3.1.8 | **No** | creation date (medium) |

**Owner ID from an entry name** (`bundleIDCandidate`):
1. Strip one trailing suffix, case-insensitive, from: `.plist .savedState .binarycookies .prefPane .qlgenerator .mdimporter .service .appex .plugin .webplugin .saver .colorPicker .wdgt .app .framework .component .vst .vst3 .clap .dpm .aaxplugin .dictionary .safariextz .mailbundle`.
2. Drop a trailing dot-component that is exactly a 36-character dashed UUID (ByHost preferences carry the Mac's UUID).
3. Drop a leading `group.` or `systemgroup.`.
4. If there are ≥ 3 dot-components and the first is a **team ID** (exactly 10 chars, all `A–Z0–9`, already uppercase), drop it.
5. Reject if a dashed UUID (8-4-4-4-12 hex) remains anywhere.
6. Accept only if it "looks like a bundle ID": ≥ 2 dot-separated non-empty parts, each `[A-Za-z0-9_-]+`, and every part contains at least one letter.

**Shared-container wrappers are never leftovers** in the general Cleaner: names starting `group.`, `systemgroup.`, or `<TEAMID>.` are skipped (they need a signed app's entitlements as evidence, which only the Uninstaller has). For `Containers`, the owner is read from `<folder>/.com.apple.containermanagerd.metadata.plist` key `MCMMetadataIdentifier` (itself rejected if wrapped), then reduced with the same rule.

**Protected IDs** (never junk owners): with `w = "." + id.lowercased() + "."`, protected if `w` contains `.com.apple.`, `.com.vorssaint.`, `.developer.apple.`, `.is.workflow.`; or `id == "com.apple"`; or id starts with `vorss.`; or starts with a shared-infrastructure prefix: `org.sparkle-project`, `com.plausiblelabs`, `com.crashlytics`, `com.segment`, `io.sentry`, `com.amplitude`, `com.rollbar`, `com.google.keystone`, `com.google.softwareupdate`, `org.cups`, `org.swift` (embedded updaters/crash reporters belong to whatever installed app carries them).

**Living owner** (any of):
- *Family match*: the installed set contains the candidate, or candidate starts with `installed + "."`, or installed starts with `candidate + "."` (dot boundary; `com.maker.App` keeps `com.maker.App.helper` and `com.maker` keeps `com.maker.App`; `com.maker.Apps` is not kept by `com.maker.App`).
- *Vendor namespace*: the first two components (`com.vendor`) equal those of any installed ID — a vendor's updater stays while any of its apps is installed — **except** when the second component is `github`, `gitlab`, `bitbucket`, `sourceforge` or `googlecode` (unrelated developers share those).
- *Launch Services*: the OS knows an app with that bundle ID anywhere on disk.

#### 3.1.4 Display groups (presentation)

| Section | Group | From category | Icon | Title | Caption |
|---|---|---|---|---|---|
| Safe | loginItems | loginItems | power | Orphaned startup items | Startup entries left by apps that no longer exist. |
| Safe | safeCaches | caches where pre-checked | archive box | Caches | Temporary files apps rebuild on their own. |
| Safe | logs | logs | doc | Logs | Old diagnostic logs. |
| Safe | developer | developer | hammer | Developer junk | Xcode build and simulator leftovers. |
| Optional | leftovers | leftovers | puzzle piece | Leftovers from uninstalled apps | Files left behind by apps you uninstalled. |
| Optional | otherCaches | caches not pre-checked | drive | Other caches | Safe to remove, nothing breaks. Apps may open slower once and downloaded content, like offline music, downloads again. |
| Optional | deviceBackups | deviceBackups | iPhone | iPhone backups | Old iPhone and iPad backups take a big slice of the storage macOS calls Other. Remove only the ones you no longer need; a new backup is made when you plug the device in again. |
| Optional | screenshots | screenshots | camera | Forgotten screenshots | Screenshots you have not opened in %d days that still have the name and folder macOS gave them. Ones you renamed or moved never show up here. |
| Optional | trash | trash | trash | Trash | Emptying the Trash is permanent. |

Each item's initial `include` = its `recommended` (pre-check) flag. Safe groups therefore start fully selected and Optional ones unchecked.

#### 3.1.5 Installed-apps oracle

Set of lowercase bundle IDs considered alive:
- Every `.app` found walking `/Applications`, `/System/Applications`, `~/Applications` up to **3 levels** (descending only into non-`.app` folders, skipping hidden entries and symlinks), plus each app's `Contents/Library/LoginItems/*.app`.
- Every running app's bundle ID and its bundle's ID.

Built once per scan, and once per clean **only if** a leftovers row is selected (it walks all app folders).

#### 3.1.6 Cleaning and safety guard

`cleanSelected(escalate: Bool)` — `escalate` has no default: a manual clean passes `true` (someone can answer an admin prompt), the scheduled pass passes `false`.

1. Collect included items. Phase → Cleaning.
2. **Trash rows first**: "empty trash" through Finder (permanent; requires Finder automation consent). Success → `freed += scanned size`; else `failed += 1`. It runs first so the clean never wipes the items it just made recoverable.
3. For each other item: if `mayRemove` fails → `failed += 1`. For login items: unload the user agent first (`launchctl bootout gui/<uid>/<Label>`, only for `~/Library/LaunchAgents`, label non-empty without `/` or `..`, errors ignored), then `mayRemove` again. Move to Trash; on error put it on a "stubborn" list.
4. Stubborn items: when `escalate`, re-check `mayRemove`, then ask Finder to delete them in **one batch** (Finder shows one administrator prompt and moves them to the Trash); an item that still exists afterwards counts as failed, others as freed. When not `escalate`, every stubborn item counts as failed (nobody can answer a prompt).
5. Phase → `Done(freed, failed)`, items cleared.

**`mayRemove(item)`** (last line of defense; all must hold):
- Standardized path is not in the critical set: `/`, `/Applications`, `/Library`, `/System`, `/Users`, `/usr`, `/bin`, `/sbin`, `/etc`, `/var`, `/private`, `/opt`, `~`, `~/Library`, `~/Documents`, `~/Desktop`, `~/Downloads`, `~/Pictures`, `~/Music`, `~/Movies`.
- Name does not end in `.localized`.
- Current file identity (device, inode via `lstat`) equals the scan-time identity.
- Not a symlink, and resolving symlinks yields the same path.
- Leftovers: still a direct child of a leftover root; detail is still bundle-ID shaped; not protected; still no living owner (fresh oracle).
- Screenshots: still a direct child of a screenshot folder and still carries the capture flag.
- Path has at least 4 components.

**Sizes**: allocated size (`totalFileAllocatedSize`, falling back to `fileAllocatedSize`) summed over a recursive walk. Symlinks contribute 0 and are never descended.

#### 3.1.7 Automatic cleanup schedule

Settings: frequency `off|daily|weekly`, hour 0–23, minute 0–59, weekday 1–7 (1 = Sunday), notify. Defaults: off, 09:00, Monday (2), notify on.

- **Next fire** = the next calendar match strictly after now (hour:minute, plus weekday when weekly), local wall clock, "next time" policy for DST gaps. Off never fires.
- **Missed run** (Mac was off/asleep): only if a last automatic run exists and `nextFire(after: lastRun) <= now` → fire at **now + 120 s** (keeps launch snappy). A schedule that never ran does not catch up (no surprise clean on first enable).
- Timer tolerance 5 s. Re-armed on: wake, time-zone change, system clock change, any schedule setting change, feature availability change. Nothing exists while off.
- **Run**: if the Cleaner is not Idle (a person is reviewing) → retry in **600 s**. Otherwise run an unattended scan; on Results, if anything is pre-checked → `cleanSelected(escalate: false)`, else finish with 0/0. On Done → finish. If the phase returns to Idle mid-run (a person reset it), stop observing and re-arm.
- **Finish**: reset the Cleaner to Idle; store `cleanerLastAutoRun` (epoch seconds), `cleanerLastAutoFreed`, `cleanerLastAutoFailed`; notify if enabled; re-arm.
- **Notification** (posted even when nothing was found, as proof of life): title "Automatic cleanup"; body = "<size> freed and sent to the Trash." if freed > 0, plus "Some items couldn’t be moved to the Trash." if failed > 0; if neither: "Nothing to clean. Your Mac is tidy."
- Only the **Safe** groups can ever be auto-cleaned (they are the only pre-checked ones). Screenshots are never scanned, leftovers/backups/Trash never selected.

Schedule card UI (when not off): weekday picker (weekly only; weekday names Sunday-first), time pickers following the system clock style (12-hour: hour 1–12 + AM/PM picker; 24-hour: 0–23), minute picker in 5-minute steps plus the stored off-grid value; "Notify when done" checkbox (turning it on requests notification permission; if denied shows "Vorssaint notifications are turned off in the system." and "Open Notification Settings…"); "Next cleanup <relative date + time>." ; last-run line: "The last automatic cleanup freed %@." when freed > 0, else "Last automatic cleanup %@." (date), plus " Some items couldn’t be moved to the Trash." when failed > 0; caption "Cleans only the safe part on its own at the chosen time and sends everything to the Trash." Permission status is re-read 1 s after appearing and whenever the app becomes active.

12-hour conversion: `hour24 = (clamp(h12,1,12) % 12) + (pm ? 12 : 0)`; back: `h12 = h % 12 == 0 ? 12 : h % 12`, `pm = h >= 12`.

#### 3.1.8 Forgotten screenshots (macOS rules)

- Age setting `cleanerScreenshotAgeDays`: 0 = off; offered 7/14/30/60/90; default 30; stored values clamp to ≤ 3650.
- Folders: (a) the system screenshot location (`com.apple.screencapture` → `location`: empty → `~/Desktop`; `~` → home; `~/x` → home/x; relative → `~/Desktop`; absolute → as is); (b) this app's own screenshot tool folder when that feature is installed (empty → `~/Desktop`). Both resolved (symlinks) and de-duplicated.
- A file qualifies when all hold:
  1. Top level of a folder, regular file, not hidden, not a symlink.
  2. Extended attribute `com.apple.metadata:kMDItemIsScreenCapture` is a property list `true` (or number 1). A file never proves it is a screenshot by its name alone.
  3. **Keeps its default name**: the name contains the creation date as `yyyy-MM-dd` (local time zone), and the base name (minus extension, minus a trailing ` (N)` collision suffix) ends with the time `H.MM.SS` (1–2 digit hour) followed by a tail that, after removing only space/NBSP/narrow-NBSP and lowercasing, is empty — or, if the hour is 1–12, equals an AM/PM symbol of some locale. Anything else ("… copy", "… ui", emoji, `!`) is a rename and disqualifies.
  4. **Forgotten**: `now − max(created, modified, lastUsed) ≥ days × 86 400`, where `lastUsed` comes from xattr `com.apple.lastuseddate#PS` (16 bytes: little-endian int64 seconds, int64 nanoseconds; seconds must be > 0).
  5. Size > 0.

#### 3.1.9 Windows mapping — Cleaner

General rules for Windows:
- **Recycle, verify, refuse**: use `IFileOperation::DeleteItems` with `FOFX_RECYCLEONDELETE | FOF_ALLOWUNDO | FOF_NOERRORUI | FOF_SILENT`, and an `IFileOperationProgressSink`; `PostDeleteItem` returns the recycled item (`psiNewlyCreated`), NULL means it was permanently deleted. Because Windows silently deletes items that cannot be recycled (network shares, removable drives without a Recycle Bin, items larger than the bin's quota), the Cleaner MUST pre-check: target volume has a Recycle Bin (fixed local drive), and item size < remaining bin capacity (`SHQueryRecycleBinW` + configured max). Otherwise refuse the item (count as failed) rather than deleting it.
- **Identity**: replace (device, inode) with (volume serial number, 128-bit file ID) from `GetFileInformationByHandleEx(FileIdInfo)`, opened with `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS`.
- **Links**: treat any `FILE_ATTRIBUTE_REPARSE_POINT` (symlinks, junctions, mount points) like a symlink: never descend, never remove through it. User profiles contain legacy junctions (`Application Data`, `Local Settings`, …) that loop.
- **Cloud placeholders** (OneDrive Files On-Demand, `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`/`RECALL_ON_OPEN`, reparse tag `IO_REPARSE_TAG_CLOUD*`): never open for reading (would download); size them by allocated size only; never offer them.
- **Size**: allocated size (`FILE_STANDARD_INFO.AllocationSize` or `GetCompressedFileSizeW`), walking with `FindFirstFileExW(FIND_FIRST_EX_LARGE_FETCH)`.
- **Elevation** replaces "Finder admin prompt": items under `%ProgramData%`, `C:\Windows`, `Program Files` or `HKLM` are recycled/removed by an elevated helper process (UAC via `ShellExecuteEx` verb `runas`), one prompt per batch. The helper MUST re-run the same safety guard. Note: the elevated helper of the same account recycles into that account's bin (same SID); "over-the-shoulder" elevation with another admin account recycles into the other account's bin — warn or refuse in that case.
- **Non-file items** (registry values, scheduled tasks, services) cannot go to the Recycle Bin. Export them before removal (`.reg` export / task XML / service config) into an app-owned folder, e.g. `%LOCALAPPDATA%\Vorssaint\Cleaner\Removed\<timestamp>\`, and offer "Restore" from the Done screen. Keep the copy "can be recovered" accurate.
- **Critical set** (never removable): drive roots, `%WINDIR%`, `%WINDIR%\System32`, `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramData%`, `%USERPROFILE%`, `%APPDATA%`, `%LOCALAPPDATA%`, known folders (Documents, Desktop, Downloads, Pictures, Music, Videos, OneDrive root), `%LOCALAPPDATA%\Packages` itself, `%ProgramData%\Package Cache` (needed for MSI repair/uninstall).

Proposed Windows categories (keep the same Safe/Optional split and pre-check philosophy: pre-check only strong evidence + cheap rebuild):

| macOS category | Windows category | Locations / mechanism | Rules | Pre-checked | Elevation |
|---|---|---|---|---|---|
| Leftovers | Leftovers from uninstalled apps | (1) `%LOCALAPPDATA%\Packages\<PackageFamilyName>` folders whose PFN is not installed for the user (`PackageManager.FindPackagesForUser("", pfn)` returns none) — exact evidence, the equivalent of container metadata; (2) `%LOCALAPPDATA%\Programs\<X>` per-user install folders with no ARP entry pointing at them and no executable left; (3) Start Menu `.lnk` files whose target is missing on a fixed drive | Plain vendor/product folder names in `%APPDATA%`/`%LOCALAPPDATA%`/`%ProgramData%` are **never** proof in the general scan (same principle as macOS); those belong to the Uninstaller where a selected app gives identity | No | (1)(2) no; common Start Menu: yes |
| Orphaned startup items | Orphaned startup items | `HKCU`/`HKLM` `\Software\Microsoft\Windows\CurrentVersion\Run` and `RunOnce` (+ `WOW6432Node`), Startup folders (`shell:startup`, `shell:common startup`), Scheduled Tasks (Task Scheduler 2.0 COM, exec actions), Services (`ImagePath`) | Resolve the command line to an executable (expand env vars, strip quotes/args, `rundll32`/`cmd` wrappers → their target); orphan only when **every** referenced executable is missing and none is on a removable/network/unmapped drive (≈ `/Volumes` rule); entry not Microsoft-signed/system (≈ protected) | Run/Startup: Yes; Tasks: Yes; Services: No | HKLM, common Startup, machine tasks, services: yes |
| Caches | Caches / Other caches | Safe: `%TEMP%` top-level entries not modified for 24 h; browser HTTP caches (`…\User Data\<Profile>\Cache`, `Code Cache`, `GPUCache` for Chromium browsers; `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2`); `%LOCALAPPDATA%\Microsoft\Windows\INetCache`; `%LOCALAPPDATA%\D3DSCache`; GPU shader caches (NVIDIA `DXCache`/`GLCache`, AMD `DxCache`); package caches (`%LOCALAPPDATA%\npm-cache`, `%LOCALAPPDATA%\pip\Cache`, `%LOCALAPPDATA%\Yarn\Cache`, `%LOCALAPPDATA%\NuGet\v3-cache`). Machine (elevated, unchecked): `%WINDIR%\Temp` top-level entries not modified for 24 h. Other (unchecked): thumbnail and icon caches (`%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db`, `iconcache_*.db`, locked by Explorer), browser `Service Worker\CacheStorage` (offline app data), offline media caches (Spotify `Storage`, etc.) | Skip in-use files (sharing violation = skip silently, not "failed"); never touch our own data; keep an exclusion list analogous to §6.1 (font cache, credential stores, licensing caches) | per row | none |
| Logs | Crash dumps and error reports | `%LOCALAPPDATA%\CrashDumps\*.dmp`; WER `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive`, `ReportQueue`; `%ProgramData%\Microsoft\Windows\WER\ReportArchive`, `ReportQueue`, `Temp`; `%WINDIR%\Minidump\*.dmp`, `%WINDIR%\MEMORY.DMP`; optional `%WINDIR%\Logs\CBS\*.log/.cab` | size > 0 | user: Yes; machine: Yes (but only cleaned when elevated) | machine items: yes |
| Developer junk | Developer caches | Visual Studio `%LOCALAPPDATA%\Microsoft\VisualStudio\<ver>\ComponentModelCache`; JetBrains `%LOCALAPPDATA%\JetBrains\<product>\caches`; Gradle `%USERPROFILE%\.gradle\caches` (Other); NuGet global packages `%USERPROFILE%\.nuget\packages` (Other) | exists, size > 0 | VS/JetBrains: Yes; others: No | none |
| Trash | Recycle Bin | `SHQueryRecycleBinW(NULL)` for size/count across drives; `SHEmptyRecycleBinW(NULL, NULL, SHERB_NOCONFIRMATION \| SHERB_NOPROGRESSUI \| SHERB_NOSOUND)` | count > 0 and size > 0; runs first; permanent | No | none |
| iPhone backups | iPhone backups | `%APPDATA%\Apple Computer\MobileSync\Backup\<UDID>` (iTunes desktop) and `%USERPROFILE%\Apple\MobileSync\Backup\<UDID>` (Apple Devices / Store iTunes); `Info.plist` (XML or binary plist) keys `Device Name`, `Last Backup Date` | directory, size > 0 | No | none |
| Forgotten screenshots | Forgotten screenshots | `FOLDERID_Screenshots` (`Pictures\Screenshots`), Snipping Tool auto-save folder, this app's screenshot folder | No capture flag exists: require default names (`Screenshot (N).png`, `Screenshot YYYY-MM-DD HHMMSS.png`) matching the file's creation time; "not opened" cannot be known (NTFS last-access is frequently disabled), so use `max(created, modified)` only and say so in the caption | No | none |
| — | (new, optional) Windows system cleanup | Windows Disk Cleanup handlers (`IEmptyVolumeCache` / `IEmptyVolumeCache2` registered under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches`): Windows Update Cleanup, Delivery Optimization Files, Temporary Windows installation files, Windows error reports, Thumbnails. Direct alternatives: Windows Update download cache `%WINDIR%\SoftwareDistribution\Download` (stop the `wuauserv` and `bits` services first, restart after); Delivery Optimization cache via the `Delete-DeliveryOptimizationCache -Force` cmdlet (do not delete a hard-coded folder: the cache location differs between Windows builds and can be moved by policy; get its size from the "Delivery Optimization Files" Disk Cleanup handler's `GetSpaceUsed`) | Handlers and these caches are purged **permanently** (not recyclable); show as Optional with an explicit "permanent" caption like the Trash | No | yes |

Schedule mapping: in-app timer like macOS (the app must be running); re-arm on `WM_POWERBROADCAST` (`PBT_APMRESUMEAUTOMATIC`), `WM_TIMECHANGE`, and setting changes. Notifications via Windows toast (unpackaged apps need an AppUserModelID registered on a Start Menu shortcut).

---

### 3.2 Uninstaller (app removal with leftovers review)

> `Support/Uninstaller.swift` is **not** this feature: it is the `Vorssaint --uninstall` command-line entry used by `Tools/uninstall.sh` to detach Vorssaint itself from the system before its own bundle is deleted (§3.2.12).

#### 3.2.1 Entry points

- Settings › Uninstaller page and the panel/Quick Launcher utility: a dashed drop zone ("Drag an app here" / "or choose one to scan"), **Choose app…** (opens an app picker: search field "Search apps", list of offered apps with icon, name, location/bundle ID; empty "No apps found"), note "Nothing is removed without your confirmation.", a Full Disk Access note when not granted. The Settings page also shows a toggle "Show in Command Bar" (`uninstallerCommandBarEnabled`, default **off**) with caption "Choose and uninstall apps in the Command Bar."
- Dropping a URL: the first `.app` among dropped URLs (else the first URL) is selected; a refused selection springs the drop back.
- Command Bar (when enabled): rows "Uninstall Application" (browse list of offered apps) and "Uninstall app selected in Finder"; both open the same review checklist inline. A Finder lookup that would need new automation consent is never made passively.
- The app picker lists only apps the selection rules accept (`offeredApplications()` = installed apps in `/Applications` and `~/Applications` (recursive, not system) filtered by `selection(for:)`).

#### 3.2.2 Selection acceptance (`selection(for:)`)

Refuse when: not a file URL; not a loadable bundle; a system app (resolved path starts with `/System/` or `/Library/Apple/`); bundle ID missing, not bundle-ID shaped, or protected (§3.1.3); the path is a symlink or not equal to its resolved form; the app is Vorssaint itself; `Contents/Info.plist` missing or reached through a symlink. On acceptance record: verified bundle ID, standardized URL, file identity of the bundle and of its `Info.plist` (both re-checked before results are shown and before removal; a mismatch resets the flow).

#### 3.2.3 Identity of the selected app

Built in the background (cancellable):

- **Owned bundle IDs**: the main verified ID plus IDs of embedded code — `*.appex`, `*.xpc`, and `*.app` inside `Contents/Library/LoginItems` (max 256 inspected; symlinks skipped). Resource bundles and other nested apps are excluded (their IDs may be shared).
- **Trusted install root**: only if the app lives under `/Applications/` or `~/Applications/` may it claim shared data. For an app elsewhere (e.g. a copy in Downloads) the owned set is **empty**, so no leftovers are offered — only the bundle itself.
- **Exclusive IDs**: drop any owned ID that another known application (installed apps incl. system, running apps, and every app Launch Services knows for that ID), at a path outside the selected bundle, also declares.
- **Team IDs**: from a *valid* code signature of the app and its owned embedded code (10-char uppercase alnum).
- **App group IDs**: `com.apple.security.application-groups` entitlements from valid signatures; kept only if no other known app's signature (validity not required for the others) declares the same group.
- **Name tokens**: for each of {localized display name, file name, `CFBundleName`, `CFBundleDisplayName`, `CFBundleExecutable`} (stripped of `.app`, skipping names with `$`, `/`, `..`): `normalized = lowercase letters+digits only`; add it if length ≥ 3 and not a role word; also add the name with a trailing version/channel word removed (regex `\s+(\d+(\.\d+)*|nightly|beta|alpha|dev|canary|preview|insider|stable|release|rc|lts|developer edition|technology preview)\s*$`, case-insensitive); add the last component of the primary bundle ID if length ≥ 4 and not a role word.
- **Role words** never used as tokens: `app mac macos osx helper agent daemon service desktop client launcher plugin extension web free pro lite plus ui xpc login updater installer renderer gpu worker crashpad broker utility alert network audio server shared core common framework bundle process handler tool runtime electron java python node mono wine`.

#### 3.2.4 Search folders

Each folder has flags: *depth* (extra child levels opened under a merely "related" match, to reach `Vendor/App`), *crash* (crash-report naming rules), *metadata* (read container metadata), *names* (display-name tokens allowed; otherwise "technical identity" = bundle/team/group IDs only), *signedGroup* (only exact signed group IDs).

| Scope | Folder (relative to `~/Library` and/or `/Library`) | Category | Flags |
|---|---|---|---|
| both | `Application Support` | Support | depth 2 |
| both | `Application Support/CrashReporter` | Logs | crash |
| both | `Caches` | Caches | depth 1 |
| both | `Preferences` | Preferences | |
| both | `Logs` | Logs | depth 1 |
| both | `Logs/DiagnosticReports` | Logs | crash |
| both | `LaunchAgents`, `PrivilegedHelperTools` | Other | technical only |
| both | `PreferencePanes`, `Internet Plug-Ins`, `Services`, `QuickLook`, `Spotlight`, `Input Methods`, `Screen Savers`, `ColorPickers`, `Frameworks`, `Automator`, `CoreImage`, `Dictionaries`, `Components` | Other | |
| both | `Audio/Plug-Ins` | Other | depth 1 |
| user | `Preferences/ByHost`, `SyncedPreferences` | Preferences | technical only |
| user | `Saved Application State`, `Autosave Information` | Saved state | |
| user | `HTTPStorages`, `WebKit`, `Cookies` | Caches | |
| user | `WebKit/com.apple.WebKit.WebContent`, `Caches/com.apple.nsurlsessiond/Downloads` | Caches | technical only |
| user | `Containers` | Containers | metadata, technical only |
| user | `Group Containers` | Containers | metadata, technical only, signed group only |
| user | `Application Scripts` | Containers | technical only |
| user | `Application Support/FileProvider` | Support | technical only |
| user | `Widgets`, `Address Book Plug-Ins`, `Contextual Menu Items`, `Safari/Extensions`, `Accessibility`, `Mail/Bundles`, `Workflows` | Other | |
| user | `Application Support/com.apple.sharedfilelist/com.apple.LSSharedFileList.ApplicationRecentDocuments` | Preferences | |
| system | `LaunchDaemons`, `Extensions`, `StartupItems` | Other | technical only |
| home | `~/.config`, `~/.local/share` | Support | technical only |
| home | `~/.cache` | Caches | technical only |
| per-user dirs | `DARWIN_USER_CACHE_DIR`, `DARWIN_USER_TEMP_DIR` | Caches | technical only |
| system | `/private/var/db/receipts` | Other | technical only |
| shared | `/Users/Shared/Library/Application Support` | Support | depth 2, names allowed |

Directory listings skip dot-entries and symlinks. For entries with a package extension (`app appex bundle framework plugin webplugin prefpane qlgenerator mdimporter service saver colorpicker wdgt component vst vst3 clap dpm aaxplugin dictionary action workflow mailbundle`) the bundle's own identifier is also read and matched (a plugin whose file name does not identify the app).

#### 3.2.5 Matching rules (`leftoverMatch(name)`)

Reject names starting with `.`, containing `..` or `/`. Let `stripped` = name minus one known suffix (`.plist .savedState .binarycookies .prefPane .qlgenerator .mdimporter .service .appex .plugin .webplugin .saver .colorPicker .wdgt .bundle .sfl .sfl2 .sfl3 .bom .action .workflow .app .framework .component .vst .vst3 .clap .dpm .aaxplugin .dictionary .safariextz .mailbundle`, case-insensitive). Comparisons below are case-insensitive unless stated.

**Exact** (starts checked):
1. `name` or `stripped` equals an exclusive signed **group ID** (case-sensitive).
2. `name` or `stripped` equals an owned **bundle ID**.
3. ByHost preference `<bundleID>.<36-char UUID>.plist`.
4. `<TEAMID>.<bundleID>` with TEAMID in the app's team IDs.
5. In crash folders: name starts with an owned bundle ID (case-sensitive prefix; checked after the related rules below have not matched).

**Related** (starts unchecked, shown with an orange "?" and the label "Optional, review first"):
1. Starts with `<bundleID>.`.
2. Normalized `stripped` or normalized `name` (length ≥ 3) equals a name token.
3. Crash folders: the part before the first `_` or `-`, normalized, equals a token.
4. Neither reverse-DNS shaped nor crash-dump shaped (`_YYYY-MM-DD` / `-YYYY-MM-DD`): normalized name **starts with** a token of length ≥ 5 (so `com.vendor.editor2` never follows from a token `editor`).
5. Equals a team ID, or starts with `<TEAMID>.`.

**Hit records with nesting**: in a folder with depth > 0, a *related* directory with children is opened; if a more specific nested hit exists, the nested hits replace the parent (`Application Support/Vendor/App` instead of the whole `Vendor`). Unmatched directories are also opened within the depth budget.

**Owner attribution** of a hit: an exact group ID wins (owner = group); otherwise the longest owned bundle ID that matches the name (or the bundle identifier read from the package) — else the shortest owned bundle ID. Hits with neither an owner nor a group are dropped.

**Container metadata** (folders flagged *metadata*): for directory entries not already matched, read `MCMMetadataIdentifier`; match it (and its `bundleIDCandidate` reduction); include = (match is exact).

**Spotlight pass** (supplements the walk): query `kMDItemFSName == "<bundleID>*"cd` for owned IDs (sorted, up to 16 clauses) OR `"<token>*"cd` for tokens of length ≥ 4 (up to 24 clauses total), scoped to `~/Library`, `/Library`, `/Users/Shared/Library`; max 200 results, accept ≤ 80. A result must be ≤ 2 components below its root, must not pass through `/Desktop/ /Documents/ /Downloads/ /Movies/ /Music/ /Pictures/ /Public/`, and in sensitive places uses the strict identity (`/Group Containers/` → group IDs only; `/Application Scripts/ /Containers/ /Preferences/ByHost/ /SyncedPreferences/ /LaunchAgents/ /LaunchDaemons/ /PrivilegedHelperTools/ /Extensions/ /StartupItems/` → technical identity). Spotlight hits always start unchecked. Category from path: contains `/Caches` → Caches; `/Preferences` → Preferences; `/Group Containers` or `/Containers` → Containers; `/Logs` → Logs; `/Saved Application State` → Saved state; `/Application Support` → Support; else Other.

**Dedupe**: process candidates by increasing path length; drop a related candidate if an exact candidate lies beneath it; same path → merge (include = OR; exact replaces related); drop anything nested under an already accepted path.

**Final safety filter**: the app bundle itself (not a symlink) plus only candidates under a scan root (`~/Library`, `/Library`, `~/.config`, `~/.cache`, `~/.local/share`, `/private/var/db/receipts`, `/Users/Shared/Library`, the two Darwin per-user dirs) whose every path component from the item up to the root exists and is not a symlink. Each surviving item gets its file identity and recursive allocated size (a symlink counts its own size here).

**Order**: by category rank `Application, Support, Caches, Preferences, Containers, Logs, Saved state, Other`, then size descending. The application row is always present and starts checked.

#### 3.2.6 Removal flow

1. Button "Move to Trash" (red, prominent; or "Uninstall" when a package manager owns the app, §3.2.7). Disabled with nothing selected, and while the package manager is busy when a package-managed app is selected.
2. Terminate (polite quit) every running app whose bundle is the target or nested inside it (embedded helpers do not always share the main ID).
3. After 0.3 s, in the background: re-validate the target's identity. Recompute ownership **at removal time**: exclusive bundle IDs must still be owned by the current bundle (unless the package manager already removed it) and still be exclusive; group IDs re-read from the current valid signature and re-checked for exclusivity. A non-app item keeps ownership only if its group is exclusive, or its owner bundle ID is exclusive **and** its evidence ID (the identifier that matched) is exclusive; otherwise it fails.
4. For each item: `removalIsStillSafe` (path is in the scanned allow-list, identity unchanged, app path not a symlink, other items still inside a scan root with no symlinked component) → move to Trash. If not safe but **confirmed absent** (`lstat` → `ENOENT`), count as freed; otherwise failed. Trash errors on an existing path → "stubborn" batch.
5. Stubborn batch → Finder delete (one administrator prompt); re-check absence; still present → failed.
6. **Freed bytes count only confirmed absences** (a bare "file does not exist" from a parent you can no longer read is not success; dangling symlinks still occupy an entry).
7. Done screen: icon is a check only if nothing failed, else a warning triangle; "Done!", "<freed> removed"; failure note "Some items couldn’t be moved to the Trash." with the first 4 failed names and "and %d more"; if any failed path is under `/Library/Containers/`, `/Library/Group Containers/` or `/Library/Application Scripts/` and FDA is missing, an FDA note "Sandboxed app data can only be moved with Full Disk Access. The administrator password does not stand in for it."; **Uninstall another**.
8. After a confirmed removal, a Command Bar shortcut stored for that app is released (only when no other copy of the same bundle ID remains, including copies Spotlight finds in the home folder).

While removing, the flow cannot be reset or re-targeted from any surface (panel, Settings, Command Bar share the one instance), and the checklist is frozen.

#### 3.2.7 Package-manager-managed apps (Homebrew handoff)

- When results arrive, ask Homebrew which installed cask's artifacts point at **exactly this app path** (`brew info --json=v2 --installed`, cached 60 s; one in-flight lookup per path). Exactly one match is required; same-named copies elsewhere never count.
- With the app row selected and a package found: the footer button reads "Uninstall", a status line says "%@ will also be removed from Homebrew.", and clicking asks for confirmation ("Uninstall with Homebrew?" / "Homebrew will uninstall %@. Configuration files may remain on the system.").
- The confirmation captures `(package, target path, selected item IDs)`; if any of these changed before confirming, nothing runs and a HUD says "This confirmation is no longer valid. Review the current items and confirm again."
- Runs `brew uninstall --cask <token>` through the shared Homebrew operation lane (§3.4), showing its live status inside the review. On success: deselect the app row; if the bundle is confirmed absent, credit its size; if it still exists, re-select it so the Trash pass removes it; if the lookup is unreadable, show it as failed. Then move the other selected items to the Trash. Failure keeps the review as it was.

#### 3.2.8 States

`Empty → Scanning → Results → Removing → Done(freed, failedItems)`. Scanning shows a spinner, "Scanning files…", the app's icon and name, **Cancel**. Removing shows "Moving to the Trash…". A new selection supersedes a running scan; a cancelled scan never delivers into a newer one (cancellation object per scan + target check).

#### 3.2.9 Edge cases

- Leftovers whose ownership cannot be re-proven at removal are reported as failed, never removed.
- An app that disappears during the scan → reset.
- Shared vendor data of suites (several apps from one vendor) is excluded by the exclusivity rule.

#### 3.2.10 UI details

Results header: app icon (44 px), name, bundle ID (or path), total found size, close (disabled during a package removal). List grouped by category with headers: Application, Support, Caches, Preferences, Containers, Logs, Saved state, Other. Row: checkbox, file icon, name, (orange "?" + "Optional, review first" for related), parent path (`~`), folder button "Reveal in Finder", size. Footer: "%d of %d selected", selected size, **Cancel**, primary action.

#### 3.2.11 Windows mapping — Uninstaller

This feature changes meaning on Windows: an app cannot be "moved to the Recycle Bin" safely; its vendor uninstaller MUST run (services, drivers, shell extensions, COM registrations, MSI database). That step is **not reversible**, which breaks the macOS promise "Nothing is deleted permanently" for the app itself; leftovers can still be recycled. Copy MUST say so.

**App list** (≈ `offeredApplications`):
- ARP entries from `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall`, `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall` (read with `KEY_WOW64_64KEY`/`32KEY` explicitly). Skip entries with `SystemComponent=1`, a `ParentKeyName` or `ReleaseType` of update/hotfix/security update, no `DisplayName`, or no `UninstallString`; skip Vorssaint itself.
- MSIX/AppX: `PackageManager.FindPackagesForUser("")`; skip `IsFramework`, `IsResourcePackage`, `SignatureKind == System`, non-removable packages, bundles' sub-packages.
- Show icon (`DisplayIcon` or package logo), `DisplayName`, `Publisher`, `DisplayVersion`, `InstallLocation`.

**Identity** (≈ §3.2.3): ARP key name; MSI ProductCode (GUID key names); `DisplayName`, `Publisher`; `InstallLocation` (if empty, derive from `DisplayIcon`/`UninstallString` executable directory, only when that directory is under Program Files or `%LOCALAPPDATA%\Programs`); main executables in InstallLocation (names + version-resource `ProductName`/`FileDescription`/`CompanyName`); Authenticode signer of the main exe (`WinVerifyTrust` + `CryptQueryObject` → leaf certificate subject/thumbprint; ≈ team ID); MSIX PFN and publisher ID. Name tokens and role words as in §3.2.3 (add Windows role words: `setup`, `uninstall`, `update`, `updater`, `x64`, `x86`, `win32`, `win64`).
- **Exclusivity** (≈ exclusive IDs): a vendor folder (e.g. `%APPDATA%\JetBrains`) is offered only at product granularity when another installed program has the same `Publisher`.
- **Trusted root** (≈ /Applications): leftovers are only claimed for apps whose install location is under `Program Files`, `Program Files (x86)`, `%LOCALAPPDATA%\Programs`, or that are MSIX packages.

**Leftover roots and evidence**:

| Root | Evidence class | Notes |
|---|---|---|
| `%LOCALAPPDATA%\Packages\<PFN>` | exact (PFN) | Usually removed by package removal; residue is exact |
| `InstallLocation` residue (Program Files, `%LOCALAPPDATA%\Programs`) | exact (path equality) | Admin for Program Files |
| `%APPDATA%`, `%LOCALAPPDATA%`, `AppData\LocalLow`, `%ProgramData%` (depth 2 for `Vendor\Product`) | related (name tokens; publisher folder) | Never pre-checked |
| `%LOCALAPPDATA%\CrashDumps` (`<exe>.<pid>.dmp`), WER `AppCrash_<exe>_*` folders | exact by exe name prefix (≈ crash rules) | |
| `%TEMP%` (top level) | technical only (exe/product exact names) | |
| Start Menu (user/common), Desktop `.lnk` | exact when target is inside InstallLocation | |
| Registry `HKCU\Software\<Publisher>\<Product>`, `HKLM\SOFTWARE\(WOW6432Node\)<Publisher>\<Product>` | related (names) | Export before delete |
| Registry `Run` values, `App Paths\<exe>`, ProgIDs/`Applications\<exe>` under `HKCU\Software\Classes`, scheduled tasks, services | exact when the command/ImagePath points inside InstallLocation or names a main exe | technical only; export before delete; services/HKLM need admin |

**Flow**:
1. Select app → scan identity → show "will run the uninstaller" + leftovers preview (pre-scan).
2. Close the app: Restart Manager (`RmStartSession`, `RmRegisterResources` with the main executables and InstallLocation files, `RmGetList`, `RmShutdown` with `RmForceShutdown` only after a second confirmation) — the analog of terminating nested apps.
3. Run the uninstaller: MSIX → `PackageManager.RemovePackageAsync(fullName)`; MSI → `msiexec /x {ProductCode}` (`/qb` basic UI); EXE → `UninstallString` (optionally `QuietUninstallString` when the person picks "silent"); per-machine installs run elevated. Wait for the process tree to exit (some uninstallers spawn a copy of themselves in `%TEMP%` and exit; wait on a job object or poll the ARP key).
4. Verify: the ARP entry/package is gone. If not, report failure and stop (leftovers are not offered while the app is still installed).
5. Re-scan leftovers (uninstallers remove some and create others), then review + recycle exactly like §3.2.6 with identity re-checks.
6. Package-manager handoff (≈ §3.2.7): when winget/Scoop/Chocolatey correlates exactly one package to this ARP entry/install path, offer "Uninstall with <manager>" with the same captured-confirmation rule.

#### 3.2.12 `Support/Uninstaller.swift` (self-detach, for completeness)

`Vorssaint --uninstall` (run from the installed bundle by `Tools/uninstall.sh`) unregisters the fan-control helper daemon, restores normal sleep if a closed-lid session left it disabled (only when a read-back proves it), restores the Spaces "rearrange" setting if pending, unregisters the login item, prints status lines, and exits non-zero only if the daemon is still registered. **Windows:** an installer custom action / uninstall hook that removes the app's Run key or scheduled task, any service it installed, toast AUMID registration, and restores any system setting it changed.

---

### 3.3 App updates

#### 3.3.1 Surfaces

- **Panel utility** "App updates" (row caption "See which apps have a newer version"): header with icon and title, a gear button that opens the Settings page, a close button; the list in compact form. On appear it calls `checkIfNeeded()` so the panel arrives with an answer.
- **Settings › App updates**: section with caption "Looks for newer versions of the apps on this Mac and helps you finish each update from its original source." and the full list; section **"Check in the background"**: segmented Off / Every day / Every week, "Next check <short date and time>", toggle "Tell me when an app has an update" (disabled while Off; turning it on, or choosing a frequency while it is on, requests notification permission); section **"Sources"**: "Include Homebrew apps", "Include apps from the App Store" (caption "Checks store versions using this Mac’s region. Apple installs these updates."), "Include other installed apps" (caption "Checks directly with app developers when supported, then uses a public catalog. The app’s own updater installs the update."). The last enabled source cannot be switched off. Any source change calls `sourceSelectionDidChange()`. Toggle "Show in panel" (`panelUtilityAppUpdates`). On appear: `checkIfNeeded()`.

#### 3.3.2 List UI

- Summary row: "Last checked <relative time>" (relative formatter, full units, "now" for a fresh check) or "Not checked yet"; **Check now** (spinner + "Checking" while running; disabled while checking or while a package operation runs).
- Empty state (only after a check in this process, and not while checking): "No updates found" (green check), "No updates found in this partial check" (info, when coverage is incomplete), or "No updates outside your rules" (info, when rules exist). Always the coverage note "Checks the original sources of installed apps and a public catalog. Updates install through their original source." If the package manager is missing: "Homebrew is not installed, so apps cannot be updated from here yet."
- With rows: selection bar "Select all" / "Clear" (only when selectable rows exist), the rows, and primary **"Update %d"** (selected count; disabled at 0 or while busy).
- Row: checkbox (package and store rows only; online rows show a spacer), app icon (from the bundle, generic if missing), name, source badge (**Homebrew** / **App Store** / **Online**), `"<installed> → <latest>"`, action button: Homebrew → "Update"; App Store → "App Store" (tooltip "Opens the App Store, where this update is installed"); Online → "Open" (tooltip "Opens the app so its own updater can finish"). Context menu when the row has a bundle ID: "Skip version %@", "Don’t check this app".
- Rules (disclosure "Update rules", shown when rules exist): hint "Skipping a version still allows newer releases. After removing an app exclusion, use Check now to refresh it."; each rule: app name, "Skipped version %@" or "Not checked until this rule is removed", **Remove rule** (disabled while busy). In the panel the rules list scrolls, height = min(rules, 3) × 54 pt.
- Incomplete-check warning (after a check, when the store source is on but incomplete, or the online source is on but incomplete): orange "Check incomplete", the unchecked app names joined by ", ", "The online check could not be completed. Other results are still shown.", and a link "Open the App Store" when the store part is incomplete.
- The shared package-operation status view (§3.4.5) while an upgrade runs; the last error (orange, up to 3 lines).
- Compact list height = `rows × 40 + (rows − 1) × 5` with `rows = clamp(count, 1, 4)` so it always ends on a whole row.

#### 3.3.3 Installed-app scan

- Folders `/Applications` and `~/Applications` walked recursively (not into packages), plus Spotlight results from the home folder (`mdfind -onlyin ~ "kMDItemContentType == 'com.apple.application-bundle'"`) that sit 1–3 components below home, not under `~/Library`, not inside a hidden folder. Never system apps (`/System/`, `/Library/Apple/`), never an `.app` nested in another `.app`. Deduplicated by resolved path.
- Per app (`Info.plist`): bundle ID (skip Vorssaint: its own ID or any ID starting `com.vorssaint`); version = `CFBundleShortVersionString` ?? `CFBundleVersion` ?? ""; build = `CFBundleVersion`; name = `CFBundleDisplayName` if non-empty, else the file name; **store app** = `Contents/_MASReceipt/receipt` exists; store ID = Spotlight `kMDItemAppStoreAdamID` (store apps only); publisher feed (non-store apps, only while the third source is on) per §3.3.6.
- Apps excluded by a rule (§3.3.8) are removed before any source is asked.

#### 3.3.4 Source 1 — package manager (Homebrew casks)

- Needs `brew` at `/opt/homebrew/bin/brew` or `/usr/local/bin/brew`; missing while this source is on → `packageManagerAvailable = false`.
- `brew info --json=v2 --installed` → **cask records**: token (validated), display name (first `name`), installed version, app file names (prefer `target` names, else `app` sources; basenames ending `.app`), absolute app paths (`/…`).
- **Covered paths** = app bundles that a record resolves to. Computed whenever source 1 or source 3 is on: those apps never reach the online catalog.
- `brew outdated --cask --greedy --json=v2` (includes casks that update themselves). A row survives only if: kind is cask; not pinned; token is not Vorssaint's own (`vorssaint`, `vorssaint@beta`, `vorssaint-beta`); current version comparable; the record resolves to **exactly one** installed bundle — by exact declared absolute path; else by file name, only directly in `/Applications` or `~/Applications`; a record without declared apps tries `"<DisplayName>.app"`; that bundle is not a store copy; its version is non-empty; `isNewer(current, bundleVersion)`. The bundle's own version is the truth (an app that updated itself is not offered again).
- Row: id `packageManager:<token>`, latest = `versionCore(current)`, token, bundle path, bundle ID.
- Commands: timeout 120 s, output cap 32 MB, Homebrew environment (§3.4.2). On failure `lastError` = last 3 meaningful output lines.
- If the installed-cask listing itself fails, source 3 cannot tell which apps the package manager covers: it is skipped for this check and its candidates are reported as unchecked (incomplete coverage).

#### 3.3.5 Source 2 — App Store

- Candidates: store apps with bundle ID and version, not covered by source 1, unique by bundle ID.
- Pass A (apps with a store ID): `GET https://uclient-api.itunes.apple.com/WebObjects/MZStorePlatform.woa/wa/lookup?id=<ids>&version=2&p=mdm-lockup&caller=MDM&platform=macappstore&cc=<region>` (batches of 20). Accept results whose `deviceFamilies` contains `mac`, with `bundleId`, non-empty `minimumOSVersion`, and offers whose `assets` contain flavor `macSoftware`; version = highest `offers[].version.display`; page = `url`.
- Pass B (apps pass A did not answer): `GET https://itunes.apple.com/lookup?bundleId=<ids>&entity=macSoftware&country=<region>` (batches of 20); accept only `kind == "mac-software"`; `version`, `minimumOsVersion`, `trackViewUrl`.
- Ephemeral session, 10 s request / 20 s resource timeout; a non-2xx response yields no entries.
- Row only if the OS version ≥ minimum OS and `isNewer(store, installed)`; id `appStore:<bundleID>`. `appStoreAvailable` = every candidate received an entry; candidates without one are named as unchecked.
- Action: one store row → its product page; several → `macappstore://showUpdatesPage`. Opening sets a hand-off flag (§3.3.9).

#### 3.3.6 Source 3 — other installed apps: developer feeds, then a public catalog

**Feed discovery** (non-store apps):
- `SUFeedURL` from `Info.plist` (Sparkle appcast), if it is a **public URL**: scheme https; no user, password or fragment; host contains a dot, contains a letter, no `:`, not ending `.local`/`.localhost`, every label `[A-Za-z0-9-]+`.
- Else `Contents/Resources/app-update.yml` (electron-builder, ≤ 64 KB) parsed as flat `key: value` lines only (JSON-style double-quoted and single-quoted values decoded; anchors, tags and flow values ignored; a duplicate key rejects the file). Require `channel` absent or `latest`, `private` ≠ `true`, no `token`, no `requestHeaders`. `provider: generic` with a public `url` without query → `<url>/latest-mac.yml`. `provider: github` (`host` absent or `github.com`; `owner` and `repo` non-empty, not `.`/`..`, characters `[A-Za-z0-9-_.]`) → `https://github.com/<owner>/<repo>/releases/latest/download/latest-mac.yml`.

**Feed loading**: one request per distinct feed URL (apps sharing a feed are answered together), 4 feeds in flight at a time, a **60 s** deadline for the whole pass (feeds not started by then are "unchecked"). Each request: ephemeral session, no cookies or credentials, no cache, 10 s / 20 s timeouts, at most 5 redirects each to a public https URL, body ≤ **2 MB** (checked on the declared length and while streaming). HTTP 404/410 = **absent** (may defer to the catalog); any other failure = **failed** (the app stays unchecked).

**Release eligibility**:
- Appcast: XML parsed without external or internal entities; depth ≤ 32; ≤ 4096 items; field text ≤ 64 KB. Item fields in the Sparkle namespace: `version`, `shortVersionString`, `minimumSystemVersion`, `maximumSystemVersion`, `minimumUpdateVersion`, `channel`, `hardwareRequirements`. An `enclosure` with a public `url`, no `deltaFrom`, and `os` absent or `macos` marks a download and may supply `version`/`shortVersionString`; a public `<link>` also counts as a download.
- Manifest (`latest-mac.yml`): flat scalars; needs `version` and `files` or `path`; `minimumSystemVersion` optional.
- Keep a release only if: it has a download; no channel; version and display version are both **stable** (start with a digit; only digits and dots); platform empty or `macos`; hardware empty or equal to the CPU (`arm64` / `x86_64`); `minimumSystemVersion ≤ system ≤ maximumSystemVersion` where "system" is the OS version for appcasts and the **Darwin kernel version** (`kern.osrelease`) for manifests (electron-builder writes Darwin versions); `installed ≥ minimumUpdateVersion`; `isNewer(version, installed)`. "Installed" = `CFBundleVersion` for appcasts when present (Sparkle compares builds), else the short version; an unstable installed version makes the app unchecked.
- Take the highest version. If its display version equals the installed short version, show builds on both sides: `"<short> (<installedBuild>)" → "<display> (<version>)"`.

**Catalog fallback**: `GET https://formulae.brew.sh/api/cask.json` (kept in memory **1 h**; a manual check forces a fresh download). Each entry keeps: token, version, app names, bundle IDs, OS constraints.
- App names: from `app` artifacts (artifact `target` when it ends `.app`; else the app targets; else the app sources; basenames), plus `uninstall.delete` literal paths directly inside `/Applications` or `~/Applications` that end `.app` and contain no `*?[]`.
- Bundle IDs: `uninstall.quit`.
- `depends_on.macos`: `>=` and `==` lists; any other operator makes the entry ineligible. Entries without app names and bundle IDs are dropped.
- Matching an installed app: entries that list its exact file name (and whose bundle-ID list is empty or contains the app's ID); if none matched by name, entries whose **single** declared bundle ID equals the app's. Exactly one match required; version comparable; OS compatible (all `>=` satisfied; if `==` present, equal or the OS version starts with `<v>.`); Vorssaint's tokens ignored.
- Coverage: a matched app with a comparable version counts as checked even when current.
- Precedence: an app whose feed answered (even "no update") never gets a catalog row. An absent feed clears its warning only when the catalog covers the app. A failed feed stays unchecked even if the catalog covers the app.

Rows from source 3: id `onlineCatalog:<app path>`, **not selectable**, action = open the app.

#### 3.3.7 Merge, selection, updating

- Merge (dedupe by id) and sort: package rows, then store, then online; alphabetical (case-insensitive) within each.
- Selection reconciliation after every change: rows that disappeared leave the selection; **new selectable rows arrive selected**.
- **Update selected**: package tokens of the selected rows in list order → one `brew upgrade --cask --greedy <tokens…>` through the shared operation lane (tokens validated, §3.4.4); store part: exactly one selected store row → its page, else the updates page. When that upgrade operation ends successfully, the list re-checks itself (the observer lives only for that operation).
- **Update one**: package → upgrade that token; store → its page (or the updates page); online → open the app.
- "Reveal" opens the bundle in Finder.

#### 3.3.8 Update rules

- One rule per bundle ID: `{bundleID, name, version?}`. With `version`: **skip exactly that release** (hides rows whose `versionCore(latest)` equals `versionCore(version)`, in every source; a newer release appears again). Without: **don't check this app** (removed from the scan before any lookup).
- Rule actions only from a listed row with a bundle ID, and only while not checking. A new skip replaces the app's previous rule. Removing a skip rule restores cached results without a scan. Removing an exclusion marks the session as "not checked" (no fabricated result, no automatic scan; the hint tells the person to press Check now).
- Stored as a JSON string in `appUpdatesRules`. Decoding drops: empty IDs or names, IDs with whitespace or `/`, uncomparable versions (empty, `latest`), duplicate IDs (first wins). A restore of settings while a scan runs discards that scan and runs once more.

#### 3.3.9 Checking lifecycle and schedule

- `check(automatic:)`: one scan at a time; a request during a scan is remembered (the automatic flag is kept). A source switch during a scan discards its answer and scans once more.
- `checkIfNeeded()` (every surface on appear): scans when a hand-off is pending, nothing was checked in this process, or the last check is **≥ 10 min** old.
- Hand-off: opening the store or an app sets a flag; when the app becomes active again it re-checks and brings back the Settings window that was showing (once). With no Dock icon this is the way back to the window.
- Finish: store `appUpdatesLastCheck` (epoch seconds) and `appUpdatesLastCount` (visible rows); availability flags; unchecked names. If the feature was uninstalled mid-scan, results are discarded.
- **Background schedule**: Off / daily (24 h) / weekly (7 days). Next = `lastCheck + interval`; if never checked or already due → **now + 180 s** (startup never waits on the package manager). Timer tolerance 60 s; re-armed on wake. Exists only while the feature is available and the frequency is not Off.
- **Notification** (automatic passes only, when enabled): only when some current finding was never announced before. Announced IDs persist across launches (`appUpdatesNotifiedIDs`) and are pruned to current findings, so an app that updates and later has another update is announced again. Title "App updates"; body "One app has a newer version." (total = 1) or "%@ apps have a newer version." (total count).

#### 3.3.10 Version comparison

- `versionCore(v)`: trim whitespace; drop a leading `v`/`V` only when a digit follows; cut at the first comma (Homebrew revisions: `3.5.262,260717d` → `3.5.262`).
- `isUncomparable(v)` ⇔ core is empty or equals `latest` (case-insensitive).
- `compare(a, b)`: split both on every character that is neither a letter nor a digit; walk parts pairwise (a missing part is ""):
  - both all-digit → numeric comparison of arbitrary length (strip leading zeros; longer is bigger; then lexical);
  - one side missing → equal if the other side is all zeros, else the missing side is older;
  - otherwise compare leading digit runs numerically (a part that starts with digits beats one that does not); if equal, a bare number beats the same number with a suffix (`5` > `5beta`); otherwise case-folded lexical comparison.
- `isNewer(candidate, installed) ⇔ compare(core(candidate), core(installed)) == descending`.
- Pinned by tests: `1.130.0 > 1.129.0`; `0.730.0.7300790 < 0.731.0`; `26.084.0504 < 26.119.0622.0003`; `1.2 == 1.2.0`; `1.2 < 1.2.1`; `00123 == 123`; `2024.1 < 2024.10`; `1.10 > 1.9a`; `3.5 > 3.5beta`; `1.9b > 1.9a`; `1.0Beta == 1.0beta`; `v2.0.11.1 == 2.0.11.1`; `v2.0.11.2 > 2.0.11.1`; `versionCore("version1") == "version1"`.

#### 3.3.11 Windows mapping — App updates

| macOS source | Windows source | Mechanism | Notes |
|---|---|---|---|
| Homebrew casks | **winget** | Preferred: WinGet COM API (`Microsoft.Management.Deployment.PackageManager`; composite catalog of installed + remote sources; `CatalogPackage.InstalledVersion`, `IsUpdateAvailable`, `DefaultInstallVersion`; `UpgradePackageAsync` with progress callbacks). Fallback: `winget upgrade --include-unknown` text (fragile: localized headers, truncated columns) or the `Microsoft.WinGet.Client` PowerShell module | winget correlates Add/Remove Programs entries with its catalog, so it also covers apps not installed through winget — it absorbs most of the "public catalog" role. Honor `winget pin` (≈ pinned casks). Exclude Vorssaint's own package |
| App Store | **Microsoft Store** | winget source `msstore`; or hand off to `ms-windows-store://downloadsandupdates` (≈ updates page) and `ms-windows-store://pdp/?productid=<id>` (≈ product page) | Querying other packages' pending Store updates from a desktop app (`AppInstallManager.SearchForAllUpdatesAsync`) may require privileges: verify before relying on it |
| Developer feeds | electron-updater; WinSparkle | Electron apps ship `<InstallLocation>\resources\app-update.yml` → `latest.yml` (same format, Windows file name); WinSparkle uses the Sparkle appcast format (filter `sparkle:os="windows"`) but its URL is normally set at runtime, so it is rarely discoverable; Squirrel/Velopack update URLs are not discoverable either | Installed version: ARP `DisplayVersion`, else the main executable's version resource |
| brew `cask.json` | winget catalog | via winget correlation | No separate catalog download |

Installing: `winget upgrade --id <id> --exact --silent`. Package and source agreements MUST be shown to the person and accepted by them (the app must not accept licenses on their behalf invisibly). Machine-scope installers raise their own UAC prompt. Store rows hand off to the Store; feed rows open the app.

---

### 3.4 Homebrew manager

#### 3.4.1 Surfaces

- **Settings › Homebrew**: header "Homebrew"; missing-brew state, or: toolbar (search field "Search packages" — Return submits; kind picker **Casks** (default) / **Formulae**; **Search** (disabled when empty or busy); refresh ↻; **Update Homebrew**), filter segmented "All N / Casks N / Formulae N" with the outdated summary (spinner "Updates" while loading; or orange "Updates N" + **Update all**), toggle **"Group dependencies"** (default on), the shell-setup banner when needed, and two panes: package list (232 pt wide: "Results" section, then "Installed", then "No longer needed") and detail pane (with the operation log at the bottom).
- **Panel utility** (also hosted by the Quick Launcher): header; missing state; mode picker **Search** / **Installed N**; shell-setup card; Search mode: native search field (auto-focused; Return submits), magnifier button, kind picker, hint "Space or Return closes the macOS panel. Use the search button."; Installed mode: filter All/Casks/Formulae with counts, **Check packages** (refresh), **Update Homebrew**, outdated summary ("Updates N" + **Update all**, or "Updates 0"); list (162 pt high) with count badge and spinner; trust card and error banner; detail card; operation card.
- Confirmation dialogs in the island are shown as standalone alerts (the island cannot host sheets).

#### 3.4.2 Detection and environment

- `brew` = first executable of `/opt/homebrew/bin/brew`, `/usr/local/bin/brew`.
- Every brew process gets the app's environment **plus** the login shell's exports of `http_proxy https_proxy ftp_proxy no_proxy all_proxy HTTPS_PROXY FTP_PROXY ALL_PROXY` and any `HOMEBREW_*` (an app started from Finder does not see shell-profile exports; brew keeps only these). Resolved once per launch, off the main thread: `<shell> -l -i -c 'printf %s __VORSSAINT_ENV_DUMP__; /usr/bin/env -0'`, then `-l` alone if no marker came back; each in a new session, 10 s timeout, 1 MB cap, with `VORSSAINT_RESOLVING_ENVIRONMENT=1` so profiles can skip slow work. Parse after the **last** marker: NUL-separated `NAME=value`, names `^[A-Za-z_][A-Za-z0-9_]*$`.
- **Shell setup check**: the profile for the user's shell (`zsh` → `~/.zprofile`; `bash` → `~/.bash_profile`; `fish` → `~/.config/fish/config.fish`; other → `~/.profile`) or any common profile (`.zprofile .zshrc .bash_profile .bashrc .profile`, fish config) contains `eval "$(<brew> shellenv)"` (fish: `eval (<brew> shellenv fish)`). If not: banner "Finish Terminal setup" / "Homebrew is installed, but Terminal may not find the brew command yet. Vorssaint can open Terminal with the setup command." + **Set up Terminal** (opens Terminal with a command that creates the profile if needed, appends the line only if missing, evaluates it, runs `brew --version`) → caption "Command opened in Terminal. Then come back here and click Refresh." + refresh.
- **Missing brew**: "Homebrew not found" / "Vorssaint can open Terminal with the official Homebrew installer. Terminal shows the steps and asks for your password if needed." + **Install Homebrew** (Terminal runs `/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"`) → "Installer opened in Terminal." / caption "When Terminal finishes, come back here and click Refresh." + **Refresh**.

#### 3.4.3 Data

- **Installed**: `brew info --json=v2 --installed` (30 s). Formulae: identifier = `full_name` if valid else `name`; display = `full_name`; installed versions joined by ", "; `versions.stable`; `desc`; `homepage`; `installed_on_request` (true if any installed record says so; nil when absent); requires = runtime dependencies' `full_name`. Casks: `token`; display = first `name`; `installed`; `version`; `desc`; `homepage`; requires = `depends_on.formula`. JSON may be preceded by warnings: fall back to extracting balanced `{…}` objects and use the first containing `formulae` or `casks`. Sorted: casks first, then display name.
- **Outdated**: `brew outdated --json=v2` → per `kind:name`: installed versions, current version, pinned. Applied to installed rows, search results and the selection; any failure → no updates shown.
- **Search**: `brew search --formula|--cask <query>`; output = one token per line (validated); output mentioning "No formulae or casks found" = empty; results reconciled with installed state (installed rows keep their data, others lose installed/update info).
- **Popularity**: `https://formulae.brew.sh/api/analytics/install-on-request/homebrew-core/30d.json` (formulae) or `…/cask-install/homebrew-cask/30d.json` (casks); ephemeral session 6 s / 8 s; cached **24 h** per kind; counts from grouped `formulae` objects (exact token record, else sum) or `items` lists; rank by count descending, then name. Search results sort by count descending (rows without data last), then name. Badge: compact count (`999`, `1.2K`, `12K`, `1.2M`, `12M`), tooltip "%@ installs in %@ days".
- **Details** (on select): `brew info --json=v2 --formula|--cask <name>` → description, homepage ("Open website"), version / latest, type, popularity. Status badge: "Installed" (green), "Update available" (orange), "Not installed", or the running action.
- Every read carries a generation number; stale answers are dropped. Untrusted-tap errors on reads show the trust card and retry the read after trusting.

#### 3.4.4 Actions

All actions are confirmed first:

| Action | Command (argv) | Confirmation |
|---|---|---|
| Install | `brew install [--cask] <name>` | "Install with Homebrew?" — "Homebrew will download and install %@. Dependencies may also be installed." |
| Uninstall (destructive style) | `brew uninstall [--cask] <name>` | "Uninstall with Homebrew?" — "Homebrew will uninstall %@. Configuration files may remain on the system." |
| Update one | `brew upgrade [--cask] <name>` | "Update with Homebrew?" — "Homebrew will download and apply the latest version of %@. Dependencies may also be updated." |
| Update all | `brew upgrade` | "Update all with Homebrew?" — "Homebrew will download and apply the latest versions for packages with updates available. Dependencies may also be updated." |
| Update Homebrew | `brew update` | "Update Homebrew?" — "Homebrew will fetch the latest information and then reload your packages." |
| Trust tap | `brew trust --tap <owner/repo>` | one-click card "Tap not trusted yet" — "Homebrew now asks for your confirmation before using third party taps. Trust %@ to continue." — **Trust and continue** |

Token validation for every name that reaches a command: non-empty, no leading `-`, no `..`, no `//`, and `^[A-Za-z0-9][A-Za-z0-9._+@/-]*$`. Arguments are an argv array (never a shell string). Entry points: row context menu (Package details, Update, Uninstall or Install), the row's trailing update arrow (and install arrow in panel search results), detail buttons.

#### 3.4.5 Operation lane

- **One operation at a time** for install, uninstall, upgrade, upgrade all, update — shared with App updates and the Uninstaller. `isBusy` = loading installed, searching, loading details, or an operation running; it disables actions.
- Output streams into a log; each chunk updates the status: **phase** (case-insensitive, ANSI stripped): `downloading/fetching/downloaded` → Downloading; `uninstalling/zap/purging` → Removing; `upgrading/upgraded` → Updating; for Update Homebrew `updating/updated/already up-to-date/already up to date` → Refreshing; `installing/pouring/moving app/linking` → Installing (Removing for uninstall, Updating for upgrades, Refreshing for update); `cleanup/cleaning/caveats/summary/installed!/uninstalled` → Finishing. **Progress** = the last `NN%` / `NN.N%` (0–1). **Activity** = last line that is not empty, not a `$ ` command echo, and not > 85% progress symbols (`#=-> .:%` and digits); leading `==>`/`->` trimmed.
- Timeouts: read commands 30 s; operations end only after **15 min without output** (never on total duration). Cancel = SIGTERM, then SIGKILL after 2 s.
- Outcomes:
  - success → refresh installed; install/upgrade re-select the package (details stay); uninstall clears the selection and scrolls the list to the top;
  - cancelled → "Operation cancelled.", refresh keeping the error;
  - **needs Terminal** when output contains `sudo:`, `a terminal is required`, `password is required`, `password:` or `administrator privileges` → status "Continue in Terminal." + "This operation needs Terminal to ask for the administrator password. Vorssaint does not capture passwords." + **Open Terminal** (runs the exact shell-quoted command) + **Copy**; refresh (part of an upgrade may have happened);
  - untrusted tap (output matches `from untrusted tap <owner/repo>`, name validated) → trust card; trusting retries the interrupted action;
  - other failure → error = last 3 meaningful lines; refresh anyway (brew reports failure when any package failed).
- Auto-clear of the status: success after **8 s**, cancelled after **6 s**; failed / needs-terminal clear only the log after **20 s** and keep the status.
- Status view: icon and color by action/result; title ("Installing %@", "Uninstalling %@", "Updating %@", "Updating packages", "Updating Homebrew", "%@ installed.", "%@ uninstalled.", "%@ updated.", "Packages updated.", "Homebrew updated.", "Could not finish %@.", "Operation cancelled.", "Continue in Terminal."; "packages" when no package); phase and "%@ elapsed" (`Ns`, `Nmin`, `Nh Mmin`); **Cancel** while running, **Clear log** after; determinate bar, or an indeterminate bar with "Homebrew has not reported a percentage yet."; activity line; "Show details" / "Hide details" technical log (monospaced, scrollable) with **Copy**.

#### 3.4.6 Dependency grouping (Settings page only)

Active when the toggle is on and `installed_on_request` data exists. Roots = installed packages not marked "installed as dependency". For each root, collect installed formulae reachable through `requires` (matching full names and short names). Visible rows = roots plus dependencies that no visible root reaches; each row folds its reachable dependencies under a chevron ("Dependencies: N"), and shows "Dependency updates: %d" in orange when any folded dependency has an update. Rows with an update (own or dependency) come first. **Orphans** (installed as dependency, needed by nothing) appear in a separate section "No longer needed" with the note "Installed as dependencies, but no installed package needs them any more." With grouping off: updates first, then the rest.

#### 3.4.7 Windows mapping — package manager

| Homebrew concept | winget | Scoop / Chocolatey (optional backends) |
|---|---|---|
| detection | ships with App Installer (Windows 10 1809+, 11); missing → open the App Installer Store page | Scoop: `irm get.scoop.sh \| iex` in a visible terminal; Chocolatey: official script, admin |
| installed list | COM installed catalog; `winget list` | `scoop list`; `choco list -r` |
| outdated | `IsUpdateAvailable`; `winget upgrade` | `scoop status`; `choco outdated -r` |
| search | COM `FindPackagesAsync` (Name/Id/Moniker contains, case-insensitive); `winget search` | `scoop search`; `choco search -r` |
| details | `CatalogPackageMetadata` (Description, Publisher, PackageUrl, License, ReleaseNotes); `winget show --id --exact` | `scoop info`; `choco info` |
| install / uninstall / upgrade | `InstallPackageAsync` / `UninstallPackageAsync` / `UpgradePackageAsync` (progress = download bytes + install percent); CLI `--id <id> --exact` | same verbs |
| upgrade all | `winget upgrade --all` | `scoop update *`; `choco upgrade all -y` |
| `brew update` | `winget source update` | `scoop update`; — |
| taps / trust | sources (`winget source add`; msstore agreement) | Scoop buckets |
| pinned | `winget pin` | `scoop hold`; `choco pin` |
| formulae vs casks | no split → replace the kind picker with a **Source** filter (winget / msstore) | Scoop main (CLI) vs extras (GUI) maps directly |
| dependencies / orphans | not tracked → drop grouping | tracked by Scoop/Chocolatey |
| popularity analytics | no public data → drop | — |
| sudo → Terminal | installers elevate via UAC; interactive installers via `--interactive`; "Open Terminal" → Windows Terminal (`wt.exe new-tab cmd /k <command>`) | |
| shell setup banner | not needed | Scoop manages PATH shims |

winget exits with HRESULT-style codes (e.g. `0x8A15…`): map the common ones (no applicable update, package not found, installer failed, blocked by policy, agreements not accepted) to plain text; keep the raw code in the technical log. Prefer the COM API: CLI output is localized and its tables truncate.

---

### 3.5 Port Manager

#### 3.5.1 Behavior (macOS)

- Listing: `lsof -nP +c0 -iTCP -sTCP:LISTEN -F pcnPT` with a 5 s timeout. Unprivileged, so only the current user's processes ("Your listening ports"). Field lines: `p<pid>` (resets port and address), `c<command>`, `P<protocol>`, `n<address:port>` (port = the last `:` component). One row per distinct `(protocol, port, address, pid)`; address lines without a numeric port are skipped (never paired with an earlier port). Sorted by port, then process name.
- A timeout is a failure: "Could not read listening ports. Try refreshing." The previous list stays (marked stale by the message). A clean exit with no listeners is a genuine empty list ("No listening ports found" / "Try refreshing or changing your search.").
- **Identity**: before running the listing, read the start time of every PID; a row is killable only if the start time read after the listing equals that snapshot. Rows of new or reused PIDs stay visible without kill actions until the next refresh.
- Row: network glyph; port (monospaced, bold); protocol badge ("TCP"); **All interfaces** badge (globe; the panel shows the globe only) when the host is `*`, `0.0.0.0`, `::` or `[::]` — tooltip "Listening on every network interface, so other devices on the network may be able to connect."; process name; `"PID <pid>  •  <address>"` (monospaced, middle-truncated).
- Actions (only when Kill Process is installed): **Kill** and **Force Kill** (bolt icon), disabled for unstable or protected rows. Confirmation title "Terminate %@?", message "This closes port %d by terminating PID %d.", buttons Cancel / "Kill" or "Force Kill" (destructive). Runs through Kill Process's identity-checked kill (§3.6.3), then refreshes.
- Context menu: **Copy port**, **Copy PID**, **Copy address:port**, and **Open in browser** for TCP rows with a valid port: `http://<host>:<port>` with `*` → `localhost`, `0.0.0.0` → `127.0.0.1`, `::`/`[::]` → `[::1]`, any other bound host as is.
- Filter "Filter by port, process, or PID": case-insensitive substring of `"<port> <process> <pid> <address>"`.
- Refresh on appear and with the refresh button (one at a time). No automatic refresh. Panel: caption "Your listening ports", count "%d open", list height ≤ 260 pt, gear → Settings page.

#### 3.5.2 Windows mapping

- `GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_LISTENER` for `AF_INET` and `AF_INET6` → local address, port (convert from network byte order), owning PID. No admin needed, and it covers **all users and services** (System PID 4, `svchost.exe`): either filter to the current user's processes (token owner via `OpenProcessToken` + `GetTokenInformation(TokenUser)`; failure → "other user") or list everything with a **User** column (requested) and rely on the protected-process rules for actions.
- UDP (`GetExtendedUdpTable`, `UDP_TABLE_OWNER_PID`) has no listen state; macOS shows TCP only. Optional "UDP" filter.
- Process names: `QueryFullProcessImageNameW` via `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`; fallback to the Toolhelp snapshot name.
- Identity: `GetProcessTimes` creation time, snapshot before and after the table read (same stability rule).
- All-interfaces hosts: `0.0.0.0` and `::`. Browser mapping unchanged.

---

### 3.6 Kill Process

#### 3.6.1 Surfaces

- **Settings › Kill Process** (beta; opt-in): form with "Group related processes" (default on; "Groups helper processes under the app responsible for them.") and "Show in Command Bar" (default on; "Adds running processes to the Command Bar, so you can find and kill them without opening Settings."); count "Processes: %d"; refresh button (tooltip "Refresh"); search field "Filter by name" (spinner while refreshing); sortable column headers **Process / CPU / Memory / PID** (clicking the active column flips direction; another column switches to its natural direction — name ascending, numbers descending; both persisted); list.
- Row: app icon (22 pt), name (+ "Processes: N" when a group holds more than one process), executable path (head-truncated), CPU `%.1f%%`, memory (bytes formatter), PID, **Kill**, and "…" menu: **Force Kill**, **Kill All “<name>”**, **Kill Process Tree**, **Restart** (only when restartable), **Copy PID**, **Copy Path**. Destructive actions are disabled for protected rows.
- Confirmations: "Kill %@?", "Force Kill %@?", "Kill all “%@” processes?", "Kill %@ and all its child processes?"; message = executable path (none for kill-all); confirm buttons "Kill", "Force Kill", "Kill", "Kill Process Tree".
- **Command Bar**: a "Kill Process" category lists processes (default action Kill; row actions Force Kill, Kill All, Kill Process Tree, Restart).
- Refresh: forced on appear, then every **3 s** while the page is visible and its window is focused (stops when it is not). The service keeps a 3 s freshness cache; unforced refreshes inside it skip work but still call their completion. Filter: name contains the text (case-insensitive) or the text equals the PID.

#### 3.6.2 Snapshot

- `ps -eo pid,ppid,pcpu,rss,comm` (5 s timeout). A failed or zero-row snapshot keeps the previous list.
- Per process: PID > 0; display name = running app's localized name, else kernel process name, else executable basename (another user's processes may refuse the name lookup), else `comm`'s basename; path = `comm`; CPU = `ps` %CPU (per core, can exceed 100); memory = RSS KiB × 1024; regular GUI app flag; app bundle (for restart).
- **Identity** = kernel start time in microseconds (`start_sec × 1 000 000 + start_usec`), read together with the row's PPID and exact executable path as cross-checks. Without identity the row is protected.
- **Grouping** (on): rows grouped by the OS "responsible process"; a group row takes the owner's PID, name and identity, sums CPU and memory, records the member count; a group whose owner is missing from the snapshot is dropped. Initial order: CPU descending.
- **Protected**: PID ≤ 1; own PID; names (case-insensitive) `kernel_task`, `launchd`, `windowserver`, `loginwindow`, `vorssaint`, `vorssaint (developer)`, `vorssaintdeveloper`; paths ending `/windowserver`, `/loginwindow`, `/launchd`.

#### 3.6.3 Kill paths

- Target = (PID, start time). Immediately before every signal, the current start time must equal the target's (PID-reuse guard); checked again just before signalling.
- **Direct**: a regular GUI app → polite quit (Kill) or force quit (Force Kill); any other process → SIGTERM (Kill) or SIGKILL (Force). `ESRCH` = already gone (counts as removed); `EPERM` = needs administrator.
- **Administrator escalation**: all `EPERM` targets of one action share one administrator prompt ("Vorssaint needs administrator access to end “%@”."). The privileged script compares each PID's start-time description (`ps -p <pid> -o lstart=`, normalized to `[A-Za-z0-9: ]+`) with the one captured beforehand, and only then runs `kill -15` or `kill -9`.
- **Kill all by name**: every listed, non-protected process with exactly that display name.
- **Kill tree**: verify the root's identity; descendants from one `ps -eo pid,ppid` table, breadth-first with a visited set (capped at 4096); protected descendants skipped; kill deepest first, root last.
- After any kill: remove the killed rows immediately, then force a refresh **0.5 s** later (and call the caller's completion, e.g. Port Manager's refresh).
- **Restart** (regular app with a bundle and identity, not protected): polite quit; wait for the real "app terminated" notification for that PID (give up after **10 s**); relaunch the bundle; refresh.

#### 3.6.4 Windows mapping

- Snapshot: `CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS)` (PID, PPID, exe name) or `NtQuerySystemInformation(SystemProcessInformation)` (also gives CPU times and working set in one call); full path `QueryFullProcessImageNameW`; CPU % = Δ(kernel + user time) / (Δwall × logical processors) between two samples (the first sample shows "—"; note macOS shows per-core %, Task Manager shows % of total); memory = working set (≈ RSS) or private bytes; display name = version-resource `FileDescription`, else exe name; packaged apps: package display name.
- Identity: creation time from `GetProcessTimes`. Windows keeps a child's PPID after the parent exits and reuses PIDs: a parent link is valid only when the parent's creation time precedes the child's.
- Kill (graceful): post `WM_CLOSE` to the process's visible top-level windows (≈ polite quit). Processes without windows have no graceful signal (console groups only via `GenerateConsoleCtrlEvent`) → offer Force, or document that Kill falls back to termination after a short wait. Force: `OpenProcess(PROCESS_TERMINATE)` + `TerminateProcess`.
- `ERROR_ACCESS_DENIED` (elevated or other users' processes) → one UAC-elevated helper per action that re-checks creation time and terminates.
- Protected: PID 0 (Idle), 4 (System), `Registry`, `smss.exe`, `csrss.exe`, `wininit.exe`, `winlogon.exe`, `services.exe`, `lsass.exe`, `dwm.exe`, `fontdrvhost.exe`, `Memory Compression`, own PID, **and every critical process** (`NtQueryInformationProcess(ProcessBreakOnTermination)` = true; killing one bug-checks Windows). `explorer.exe` may be killed/restarted.
- Grouping: no responsibility API. Heuristic: group each process under its highest validated ancestor with the same executable path (Chromium/Electron trees), optionally under the nearest ancestor that owns a visible window. Label the toggle's caption accordingly.
- Restart: capture image path, command line (`NtQueryInformationProcess(ProcessCommandLineInformation)`) and working directory before closing; wait on the process handle (≤ 10 s); relaunch non-elevated in the user's session.

---

### 3.7 WhatsApp downloads (part of the Cleaner module)

#### 3.7.1 Scope and opt-in

- Sub-feature of the Cleaner module, off by default (`whatsAppDownloadsEnabled`). Turned on from the Cleaner's opt-in card or the toggle at the top of its Settings tab ("WhatsApp downloads"; intro "Finds files that macOS confirms came from WhatsApp. File contents and chats are never read."). Turning it off resets the manager and stops the organizer.
- Watched folder = the user's **Downloads, top level only** ("Watched folder" + path + **Reveal in Finder**), plus files the organizer filed away (reviewable by hand, never trashed automatically). WhatsApp's own app container is never read.
- Access: listing Downloads may trigger the macOS Files & Folders prompt. A successful listing sets `whatsAppDownloadsAccessConfirmed = true` ("Downloads is accessible"); a failure sets it false ("Vorssaint cannot access Downloads. Allow it in Files & Folders." + "Open System Settings"). Background automation and the organizer never run before access was confirmed by a scan.

#### 3.7.2 Candidate rules

A file is a confirmed WhatsApp download when all hold:
- direct child of Downloads (or of an organizer destination folder, for organized files);
- regular file — not a directory, package, symlink, alias, or hidden;
- extension not in `download part partial crdownload tmp` (case-insensitive);
- quarantine properties exist and `LSQuarantineAgentName`, trimmed, equals `WhatsApp` case-insensitively — **the only accepted evidence**;
- `downloadedAt` = quarantine timestamp ?? date added to the folder ?? creation date; `modifiedAt` = content modification date ?? `downloadedAt`;
- fingerprint `"<device>:<inode>"` readable — the candidate's ID and its exclusion key.

**Category**: extension in `7z bz bz2 cab dmg gz iso rar tar tgz xz zip` → Archive; in `csv doc docm docx epub key md numbers odp ods odt pages pdf ppt pptm pptx rtf tex txt xls xlsm xlsx` → Document; else by content type: image → Image; movie or video → Video; audio → Audio; archive → Archive; PDF or text → Document; else Other. UI names: "Images", "Videos", "Audio and voice notes", "Documents", "Archives", "Other". Persisted category IDs: `image, video, audio, document, archive, other` (comma-joined).

#### 3.7.3 Retention rules

- Settings: file types (default `image,video,audio`; "All" checkbox + pairs), **Keep for** ∈ {1, 2, 7, 14, 30} days (default 7; any other value sanitizes to 7). Caption: "Recently edited files wait for the full period again."
- **Old enough** ⇔ `downloadedAt ≤ now − days` **and** `modifiedAt ≤ now − days` (calendar days).
- **Eligible for rules** ⇔ category enabled ∧ old enough.
- **Eligible for automatic cleanup** ⇔ eligible for rules ∧ not organized ∧ (`includeExisting` ∨ `downloadedAt ≥ automaticStartDate`).
- Excluded files ("Keep") are never selected and their checkbox is disabled; "Manage again" re-includes them per the rules.

#### 3.7.4 Manual review ("Clean now")

- Intro "Scan at any time. The initial selection follows your types and age limit; you can review every confirmed file." **Scan** (disabled while scanning or cleaning; spinner "Scanning…").
- Results sorted by download date (newest first), then name. Header "%1$d confirmed files · %2$@" + **Select by my rules**. List (≤ 320 pt): checkbox, category icon, name, `"<category> · <download date + time>"`, size, menu **Keep** / **Reveal in Finder** (or **Manage again** for excluded rows); same items in the context menu. Footer: "Files are moved to the Trash and remain recoverable until you empty it." + **"Move %1$d to Trash · %2$@"**.
- Initial selection = eligible for rules ∧ not excluded. Changing types or retention re-scans when results are showing.
- Cleaning re-validates each chosen file (still a candidate at the same location with the same fingerprint; an automatic pass also requires automatic eligibility), moves it to the Trash, records the last-cleanup stats (time, count, bytes, failed, automatic), and shows "%1$d files (%2$@) moved to the Trash. %3$d failed." (check or warning icon).
- Empty: "No confirmed WhatsApp files found in Downloads." Failure: "Downloads could not be scanned. Check Files & Folders in System Settings."

#### 3.7.5 Automatic cleanup ("Clean up automatically", off by default)

- Caption: "Checks once a day and sends matching files older than your limit to the Trash."
- Turning it on first scans. If eligible files already exist, an alert asks "What about existing files?" — "%d existing files already match your rules. Choose whether automation may manage them or only future downloads." — **Only future downloads** / **Include existing files** / Cancel. Either choice stores `whatsAppDownloadsIncludeExisting` and `whatsAppDownloadsAutomaticStartDate = now`. With nothing eligible it enables as "future only".
- Schedule: once a day at **09:00 local**. Missed check (a last run exists, now ≥ today 09:00, and the last run was before today 09:00) → run **120 s** later; never run → next 09:00. Tolerance 10 s. Re-armed on wake, time-zone change and clock change.
- Run: if the organizer is busy, the manager is scanning or cleaning, or the review page is visible with results → retry in **600 s**. Scan; if the review became visible in the meantime → retry in 600 s (automation never rewrites a person's checkboxes). Select automatic-eligible files; clean them, or record a zero pass. Store the last automatic run time; notify when enabled: "WhatsApp cleanup" — "%1$d files (%2$@) moved to the Trash. %3$d failed.". A scan failure records the pass without a notification.
- **Activity** section: "Last cleanup %@: %d files · %@ · %d failed" or "No cleanup has run yet."; "Next automatic check %@." when automatic.
- **Privacy**: "Only local file metadata is inspected. Vorssaint never reads chats or file contents." (the organizer variant when the organizer is on).

#### 3.7.6 Organizer (experimental, off by default)

- Section "Automatic organization" with an "EXPERIMENTAL" badge; description "Moves stable WhatsApp downloads to a dedicated folder and detects exact repeat downloads."; toggle "Organize automatically"; warning caption "WhatsApp may download a moved file again. Vorssaint cannot prevent the network download, but it can detect and discard an identical extra copy."
- **Destination**: default `Downloads/WhatsApp` ("Use Downloads/WhatsApp" resets); "Choose…" accepts any existing folder except Downloads itself ("Choose a folder other than Downloads itself."); reveal button.
- **Folder structure**: "No subfolders" (flat) / "By file type" (`Images`, `Videos`, `Audio`, `Documents`, `Archives`, `Other`) / "By year and month" (`YYYY/MM` from the download date).
- **Wait before moving**: 1, 5, 15, 60 minutes (default 5; others sanitize to 5). Stable ⇔ `now − max(downloadedAt, modifiedAt) ≥ delay`.
- **When the same file is downloaded again**: "Move the new copy to Trash" (default) / "Keep both copies" / "Replace the organized copy"; caption "Duplicates are confirmed with a private SHA-256 digest. The organized copy is rechecked before another copy is discarded."
- **File types** (collapsible; default all six).
- Triggers: a directory watch on Downloads (write, rename, delete, extend, attribute events) schedules a run **2 s** later (coalesced: an earlier pending run wins); a file not yet stable re-schedules the run at its eligibility time + 1 s; a run requested while one executes retries in 60 s; an automatic run while the review is open or the manager is busy retries in 300 s; failing to open the watch retries in 300 s. **"Organize eligible files now"** runs at once; **"Undo last organization"**.
- Per eligible file (enabled category, stable): re-check the fingerprint; SHA-256 (1 MiB chunks); look for a valid record with the same digest (its destination must exist and still hash the same, otherwise the record is dropped); re-check the source (same fingerprint, same size, mtime within 1 ms); then
  - duplicate + "trash new" → move the source to the Trash (undo = move it back);
  - duplicate + "replace" → stage the existing copy as `.vorssaint-replaced-<uuid>.partial` beside it, move the new file into its place (verified), trash the staged copy; on error restore the original;
  - otherwise (also "keep both") → create the target folder; unique name (`stem 2.ext`, `stem 3.ext`, …); same volume → rename (no re-hash); different volume → copy to `.vorssaint-<uuid>.partial`, verify SHA-256, rename into place, trash the source (an unknown volume identity always takes the verified copy path).
- **Records**: `{digest, destinationPath, originalName, size, organizedAt}`; records whose destination vanished are pruned; the newest **5000** are kept. Organized files are reviewable but never auto-trashed.
- **Undo**: each run that did something stores a transaction `{id, actions (in reverse order), recordsBefore, recordsAfter, createdAt}`; at most **20** kept; valid **7 days**. Undo replays the actions (a move goes back only if the restore path is free; a "trash" action trashes the current file) and is allowed only while the affected records are unchanged since the run. Success restores the records and drops the transaction; then the review re-scans.
- Status lines: "Organizing WhatsApp files…", "%1$d moved · %2$d duplicates · %3$d failed", "Last organization %@: %d moved · %d duplicates · %d failed", "No organization has run yet.". Notification "WhatsApp organization" — "%1$d files organized. %2$d duplicate downloads handled. %3$d failed." — with an **Undo** action when the run is undoable (posted only when something was moved or deduplicated).
- Privacy: "To identify exact duplicates, file bytes are read locally only while calculating a cryptographic digest. Contents and chats are never stored or uploaded."

#### 3.7.7 Windows mapping

- **Provenance is the blocker.** macOS trusts only the OS-written quarantine agent name. Windows' Mark of the Web (`Zone.Identifier` alternate data stream with `ZoneId`, `ReferrerUrl`, `HostUrl`) is written by browsers and by apps that use `IAttachmentExecute`; whether WhatsApp Desktop (packaged, `5319275A.WhatsAppDesktop_cv1g1gvanyjgm`) writes it, and with which URLs, must be verified on real machines. If it does: accept only files whose `HostUrl`/`ReferrerUrl` host belongs to WhatsApp (domains to be determined). If it does not: the feature MUST NOT trash files based on name patterns (`WhatsApp Image …`), which are forgeable and change on rename — drop it, or ship review-only with per-file confirmation and no automation.
- Folder: `SHGetKnownFolderPath(FOLDERID_Downloads)`. Watch: `ReadDirectoryChangesW` (non-recursive). Fingerprint: volume serial + file ID. Hash: CNG `BCryptHash` (SHA-256). Same-volume check: volume serial of source and destination folder.
- Undo of trashed files is weaker: `IFileOperation` does not report where an item landed in the Recycle Bin, and restoring requires enumerating the Recycle Bin shell folder (match original path + deletion time, invoke `undelete`). Prefer moving organizer-trashed files into an app-owned holding folder for the 7-day undo window and recycling them when the window closes.

---

### 3.8 Disk image installer (brief — recommended Drop)

**macOS**: when a volume mounts, if `hdiutil info -plist` maps the mount point to exactly one `.dmg` image and the volume's top level holds exactly one valid `.app` (directory, not a symlink, executable inside the bundle) that is not already in `/Applications` (nor `~/Applications` when that destination is chosen), a non-modal prompt "Install this app?" — "%@ will be copied to %@ and the disk image ejected." — offers **Install** / Cancel with checkboxes "Move the download to Trash" (default on), "Show the installed app in Finder" (default off), "Install in the Applications folder inside your home folder" (default off); choices persist. Install: `ditto --rsrc --extattr --acl --noqtn` into a staging folder (no quarantine, so the app is not path-randomized) → bundle check → `codesign --verify --deep --strict` and `spctl -a -t exec` (skipped when Gatekeeper is disabled) → collision re-check → move into Applications → eject → trash the image if its file identity is unchanged → result alert ("App installed" / "%@ is ready in %@. The disk image was ejected and its download moved to Trash." and variants when the mount or download had to be kept; "Could not install" / "Nothing was changed. You can still drag the app to Applications." / "This Mac could not verify the app, so nothing was installed." / "%@ is already in Applications."). A small progress panel "Installing %@…" shows meanwhile; prompts queue one at a time.

**Windows**: no equivalent distribution pattern (apps ship as `.exe`, `.msi`, `.msix`; app ISOs are rare). Drop. A "ZIP with one portable app → copy to `%LOCALAPPDATA%\Programs` + Start Menu shortcut" helper would be a new feature, not a port.

---

### 3.9 Volume mixer (per-app volume)

#### 3.9.1 Availability, permission, lifecycle

- macOS 14.4 or later (process taps); older systems show "Available on macOS 14.4 and later" in place of rows.
- Taps need the "Screen & System Audio Recording" permission. When a tap cannot be created and no engine is already running for that row, the rows are replaced by "To adjust per-app volume, allow “Screen & System Audio Recording” in System Settings. Audio is never recorded." + **Open System Settings…**. The hint clears when a tap succeeds or no adjustment needs a tap any more.
- The device-observation layer (device list, default output, default system output, output volume and mute) runs while **any** of Volume mixer, Audio priority or Output switcher is installed; per-app process discovery runs only while Volume mixer is installed (priority-only operation never scans processes).
- On quit, every tap is torn down synchronously (apps return to untouched output) and a speaker volume lowered by the headphone guard is restored if it still holds the app's value.

Panel layout ("Volume mixer" section, collapsible): **audio devices block** (Output picker + output volume; System sounds picker; Microphone picker + input level; switch error), divider, **app rows** (or the unsupported/permission/empty message "Apps that use audio show up here"), divider, **Options** disclosure (§3.9.7). Settings shows the same controls in a "Volume mixer" card (Output switcher and Audio priority have their own cards there). The Dynamic Island has its own mixer page with the same rows laid out sideways (covered by the island spec).

#### 3.9.2 App discovery and rows

- Source: the audio system's process objects — every process holding an audio connection, playing or not, so apps are adjustable before they play.
- For each object: its PID (skip Vorssaint's own); the **owning regular app** = the OS "responsible process" if it is a GUI app; otherwise walk the BSD parent chain up to **6** levels to the nearest GUI app (browsers detach audio helpers from the responsibility chain); processes whose chain ends at launchd (daemons, login items) are not listed.
- Objects are grouped per owner PID into one row: `isPlaying` = any object reports running output (green dot on the icon); bundle hint = the owning app's bundle ID, else — only when the object belongs to the owner itself — the bundle ID the audio object reports.
- **Row identity**: `rowID` = bundle ID if present, else `process:<ownerPid>`; `persistenceID` = bundle ID, else the display name (never the `pid N` fallback), else none (the row is listed and adjustable, but nothing is stored). Two same-named bare processes are separate rows sharing one saved volume.
- **Bypassed apps** — listed with the caption "This app manages its own audio.", no slider, no output menu, volume fixed at 100%, never tapped: Zoom (`us.zoom.xos`, any `us.zoom.*`; names `zoom`, `zoom.us`, `zoom workplace`) and pro-audio hosts with bundle prefixes `com.apple.logic`, `com.apple.garageband`, `com.apple.mainstage`, `com.ableton.`, `com.avid.`, `com.cockos.reaper`, `com.steinberg.`, `com.presonus.`, `com.bitwig.`, `com.image-line.`, `com.motu.`.
- **Finder row**: while `mixerShowFinder` (default true) and Finder has no row of its own, a persistent Finder row (no audio objects yet) is added so Quick Look audio can be adjusted before it plays.
- **Hidden apps** produce no row and are never tapped (an app taken off the list always plays untouched). Hidden = persistence ID in `mixerHiddenApps` (map id → display name), or Finder while `mixerShowFinder` is false. A row without a persistence ID cannot be hidden.
- Rows sorted by display name (case-insensitive), ties by ID; rows with the same row ID are merged (objects united, playing OR, bypassed OR).
- **Visible list** = the user's arrangement (§3.9.6) applied, then the "Hide inactive apps" filter (default off): a row stays when it is playing, or its volume is not 100%, or it has a per-app output (a custom setting is never hidden).
- **Refresh triggers**: device list, default output, default system output, process object list, and on each process object running-output, running and output-devices changes; wake (also resets render watchdogs and resubscribes volume listeners). Coalescing: an isolated notification refreshes immediately; notifications within **0.2 s** of the last refresh fold into one trailing refresh. Only one HAL read at a time; a request during a read is replayed once afterwards; a read whose generation went stale (the app changed the output itself, or stopped) publishes nothing. Published properties change only when the value differs.

#### 3.9.3 Per-app volume

- Range **0.0 – 2.0** (0–200%); non-finite values become 1.0; clamped.
- **Unity** ⇔ `|v − 1| < 0.005` (what the UI shows as 100%). Storing unity removes the entry.
- Row controls: app icon (32 pt) with the playing dot; name; pin glyph when pinned; "…" menu (pin/unpin); output menu (112 pt wide); slider 0–2 (accent tint, switching to amber when the displayed percentage is above 100); percentage label (bolt icon + amber when boosting); reset button ↺ (tooltip "Reset to 100%", hidden and disabled at exactly 100%); mute button (speaker icon; red crossed speaker at ≤ 0.001). Accessibility actions Move Up / Move Down.
- **Percent editor**: clicking the label turns it into a focused, fully selected text field followed by a "%" glyph. Accepts a number with an optional trailing `%` and the locale's decimal separator; clamps to 0–200 (0–100 for the output and microphone sliders); an invalid entry beeps and keeps editing; Return commits; Escape cancels (only when the key event belongs to the mixer's own window, and not while an input method is composing); losing focus commits.
- **Mute toggle**: volume > 0.001 → remember it as "last audible", set 0; else restore the last audible value (default 1.0).
- **Persistence**: `appVolumes[persistenceID] = volume`; rows without a persistence ID use a session-only map. Saved volumes apply as soon as the matching app produces sound, with no panel open.

#### 3.9.4 Engine, boost and limiter (macOS)

- An engine exists for a row only when it has audio objects, a target output, and (volume ≠ unity, **or** an explicit per-app output, **or** a universal-route repair, §3.10.2). A second gate refuses to tap a row unless a saved volume ≠ unity, a saved route, or a universal request matching the current default exists ("listing an app never taps it").
- Engine = a **muted**, private, stereo-mixdown process tap of the app's audio objects + a private aggregate device named "Vorssaint Mixer" (main sub-device = the target output, the tap attached with drift compensation and auto-start) + an IO callback that:
  1. finds the tap among the input buffers: the last buffer whose channel count equals the tap's (an output device that also records presents its microphone first); a lone buffer is used only when the device records nothing; no tap → silence the output and return;
  2. renders the tap into the device's output layout scaled by `gain × compensation` (§6.6), silencing every frame it could not fill (never replaying stale buffer memory);
  3. runs the **lookahead limiter** on every live engine, attenuated ones included, so crossing into boost never inserts a block of silence (§6.5);
  4. counts a render cycle only when frames reached the device.
- Gain is read lock-free each cycle; `gain` clamps to [0, 2].
- Builds run off the main thread; a per-row **build token** discards a build that lands after the mixer moved on (otherwise two taps would play the app twice); a slider drag never queues more than one build per row.
- **Replace before teardown**: the new engine is built and running before the old one stops (an engine that disappears even briefly hands the app back to the speakers at full volume). Exception: an engine rendering to a device that vanished is stopped at once (it can only mute the app).
- **Churn window**: a row whose audio objects disappear keeps its tap for **0.2 s** (apps recreate their audio unit between clips).
- **Render watchdog**: while the app plays, an engine whose cycle counter has not moved for **1.5 s** is "wedged" (seen after sleep): it is dropped immediately (which unmutes the app) and rebuilt once; if the identical configuration wedges again, the row stays untapped (fail open) until an explicit change.
- **Teardown** runs on a bounded queue (**4** concurrent) because destroying a tap on a wedged audio path can block.
- **Level compensation** (macOS tap bug: the stereo mixdown divides by the number of channel pairs of the output the app plays to): factor = `min(max(fewestStreamChannels / 2, 1), 4)` over the app's current outputs (unreadable = stereo), ramped up over 30 ms and down over 5 ms (§6.7). macOS-only.
- None of this machinery is needed on Windows (§3.9.8) except the gain semantics and, if boost is attempted, §6.5–6.6.

#### 3.9.5 Per-app output routing

- Row output menu: **Default**; every output device (`"<name> (current)"` for the system default); an **"Output unavailable"** entry when the saved device is not connected and not otherwise listed; (macOS 27) a divider and "Choose AirPlay speaker…".
- Choosing a device stores `appOutputDevices[persistenceID] = <device UID>` (session map without a persistence ID); **Default** removes it.
- A saved device that is not connected → audio follows the default output; the preference is kept; the row says "Using default until this device returns." It switches back automatically when the device returns.
- An explicit route is enforced even at 100% and even when it equals the current default (some apps keep an old device open).
- The new route's engine is built before the old one stops.

#### 3.9.6 Arrangement: pin and reorder

- Stored in `mixerAppArrangement` as JSON `{"order": [persistenceIDs], "pinned": [persistenceIDs]}`. Invalid JSON → empty arrangement; duplicates and empty IDs dropped on load. Apps that are not running keep their slots. Refreshing audio never rewrites it.
- **Ordering**: pinned rows first; inside each group by index in `order`; IDs not in `order` after known ones, keeping their alphabetical order.
- **Pin/Unpin** ("Pin to Top" / "Unpin" in the row context menu and "…" menu): appends to / removes from `pinned`.
- **Reorder**: hold **Command** and drag a row onto another row of the **same pin group**; the pointer's half decides above/below (left/right in the island); a 2 pt accent line marks the insertion; the dragged row dims; leaving without a drop saves nothing; drops from outside the app or with a mismatched payload are refused. Accessibility Move Up/Down swap with the neighbor in the same group.
- **Move semantics**: take the visible IDs of the moved row's group, remove the row, insert it before/after the target, then write the new sequence back **only into the slots those visible IDs occupy** in `order ∪ visible`; hidden, closed and other-group apps keep their remembered positions. Dragging never pins or unpins.
- Row context menu also offers **Hide from the list**.

#### 3.9.7 Options disclosure

"Options" (collapsed by default):
1. "Hide inactive apps" switch (`mixerHideInactiveApps`).
2. "Lower volume when headphones disconnect" (§3.12).
3. "Use finer volume steps" (§3.15).
4. Output switcher controls (§3.11) — panel only, when installed.
5. Audio priority disclosure (§3.14) — panel only, when installed.
6. **"Apps in the list"** chooser: summary "All" or "Hidden: N"; expands to one checkbox per known app (hidden apps unchecked + running apps checked, alphabetical; a just-hidden app appears once); rows without a persistence ID are disabled. Unchecking hides, checking shows again; the Finder entry maps to `mixerShowFinder`.

#### 3.9.8 Windows mapping — mixer

- **Discovery**: for each active render endpoint (`IMMDeviceEnumerator::EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)`), `IAudioSessionManager2::GetSessionEnumerator` → `IAudioSessionControl2`: `GetProcessId`, `IsSystemSoundsSession`, `GetDisplayName`/`GetIconPath`, `GetSessionIdentifier` (includes the executable path or package identity), `GetState` (`AudioSessionStateActive` = playing; `IAudioMeterInformation::GetPeakValue` can refine the dot). Subscribe with `RegisterSessionNotification` (`OnSessionCreated`) and per-session `IAudioSessionEvents` (`OnStateChanged`, `OnSessionDisconnected`, `OnSimpleVolumeChanged`, `OnDisplayNameChanged`). Endpoint changes via `IMMNotificationClient`.
- **Grouping**: by normalized executable path (Chromium/Electron audio helpers run the same executable) or by package family for packaged apps — simpler than the macOS parent walk.
- **Identity**: `persistenceID` = PFN / AUMID for packaged apps; else the lowercase full executable path (or file name when the path is unreadable). Display name = version-resource `FileDescription`, else the session display name, else the exe name. Expect migration needs if the key scheme changes later; pick one now.
- **Volume**: `ISimpleAudioVolume::SetMasterVolume(0…1, ctx)` / `SetMute` on **every** session of the app on **every** endpoint (an app may have sessions on several devices). Pass a private event-context GUID to recognize and ignore our own change callbacks; external changes (Windows' own mixer) update the slider. Windows also remembers per-app volumes itself; the app's map stays the source of truth and is re-applied in `OnSessionCreated`.
- **System sounds**: show the System Sounds session as a permanent row (the analog of the Finder row).
- **Bypass list**: not needed (ASIO hosts never appear as sessions). Exclusive-mode streams ignore session volume; show such rows disabled if detectable.
- **Boost above 100%**: `ISimpleAudioVolume` is capped at 1.0; there is no driverless per-app gain stage. Recommended v1: slider maximum 100% and no limiter. Possible later paths: (a) per-process loopback capture (`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, Windows 10 2004+) re-rendered with gain + §6.5 limiter — viable only if the original stream can be silenced without silencing the capture, which is unverified and probably impossible because session volume/mute is applied before loopback; (b) a signed virtual audio driver (large effort); (c) an APO (per device, not per app).
- **Per-app output**: undocumented `IAudioPolicyConfigFactory` (activation class `Windows.Media.Internal.AudioPolicyConfig`; the interface GUID differs between Windows 10 builds and 21H2+/Windows 11 — as used by EarTrumpet): `SetPersistedDefaultAudioEndpoint(pid, eRender, role, deviceId)` for `eConsole` and `eMultimedia`, `GetPersistedDefaultAudioEndpoint`, `ClearAllPersistedApplicationDefaultEndpoints`. The device ID must be wrapped as `\\?\SWD#MMDEVAPI#<endpointId>#{e6327cad-dcec-4949-ae8a-991e976a79d2}`. Windows persists the choice per app; many apps only move after reopening their stream (pause/play or restart) — show "The app may need to restart its audio to move." Version-gate and feature-flag it.

---

### 3.10 System output, system sounds output, output volume

#### 3.10.1 Header pickers

1. **Output** (speaker icon; tooltip "Choose system output"): outputs that can be the default (`"<name> (current)"`), "Output unavailable" when the current default is not in the list, disabled with "No outputs found" when empty. Choosing → universal switch (§3.10.2). Below, when the default device has a settable volume: icon (crossed when muted or at 0), slider 0–100%, editable percent.
2. **System sounds** (bell icon; tooltip "Choose where alerts and sound effects play"): outputs that can be the default *system* output; sets the default system-output device; current one marked "(current)".
3. **Microphone** (§3.13).
4. Switch errors: "Could not switch: %@" (OS status code).

Output device list: devices with output streams, alive, not hidden, not the app's own private devices (`Vorssaint Mixer`, `Vorssaint AirPlay`, `Vorssaint Island Levels`, `Vorssaint Recorder`); fields UID, name, transport type (→ priority tier), headphone heuristic (§6.8, using name, UID and the output data-source name), can-be-default and can-be-default-system flags; sorted default first, then name, then UID.

#### 3.10.2 Universal switch ("all apps")

1. Validate: device connected and able to be default, else error "Output unavailable".
2. Set the system default output.
3. On success: clear all saved and session per-app routes (volumes kept); remember the choice in `mixerUniversalOutputDevice`; publish the new default immediately (a second shortcut press cycles from it without waiting for the audio system); invalidate in-flight builds; refresh.
4. With the next snapshot: any app still playing to a device outside the new default (e.g. a Wine game that keeps its old device open) gets a **universal route** engine at unity forcing it onto the new default; an app that really followed returns to passthrough. Silence or unreadable device lists never start or discard such a route. A later hardware or priority change never routes apps to an old universal choice (it applies only while it equals the current default). Bypassed apps and apps with an explicit route are excluded.
5. On failure: error "Could not switch: OSStatus <n>", every earlier route kept.

Audio priority uses a separate, lighter path: it sets only the default output, off the main thread, without clearing per-app routes.

#### 3.10.3 Output volume and keys

- Slider and keys write through a coalescing queue: one write in flight, only the newest pending level kept; the UI updates immediately; writes intended for a previous output are dropped when the default changes (completions still fire).
- Setting volume 0 also mutes (when the device has a mute control); any positive volume unmutes (asking for sound means asking for sound).
- Volume and mute keys handled by the app (island routing) first read the device's real level (after sleep the published value can be stale), then step by **1/16**, or **1/64** with the fine modifiers; two quick mute presses cancel out; keys queued for an output that is no longer the default settle without replaying (mute keys go back to the system).
- Volume is read/written via the virtual main volume, then the volume scalar; mute on the main element, then channels 1 and 2.

#### 3.10.4 Windows mapping

- Default output: undocumented `IPolicyConfig::SetDefaultEndpoint(endpointId, role)` (COM class `PolicyConfigClient`) for `eConsole` and `eMultimedia`, and optionally `eCommunications` (a setting "Also use for calls").
- Universal switch clears per-app routes with `ClearAllPersistedApplicationDefaultEndpoints`. The "repair" step for apps that ignore the default has no tap equivalent; the only lever is a per-app persisted endpoint, and the app may need to restart its stream.
- Output volume: `IAudioEndpointVolume` on the default render endpoint (`GetMasterVolumeLevelScalar`/`SetMasterVolumeLevelScalar`, `GetMute`/`SetMute`, `RegisterControlChangeNotify` → `IAudioEndpointVolumeCallback`).
- System sounds output: Windows has no separate alerts device. Windows' "App volume and device preferences" can route the System Sounds session; reproduce through the per-app persisted endpoint API if it accepts that session (verify), otherwise **drop** the picker or replace it with a "Communications device" picker (`eCommunications`).
- Device list: active render endpoints; names from `PKEY_Device_FriendlyName`; endpoint ID strings as UIDs (stable for a given device and port).

---

### 3.11 Output switcher

- Controls (Settings card "Output switcher"; also in mixer Options in the panel): toggle **"Switch outputs with shortcut"** (default off; caption "Choose outputs and use the shortcut to move to the next available one."), shortcut recorder (default **Control–Option–Command–S**), and **"Outputs in cycle"** with a checkbox per output that can be default.
- Turning it on with an empty selection seeds the selection with the current output. Stored order = the checked devices in visible order, followed by checked devices that are not connected (kept).
- **Shortcut press**: candidates = selected UIDs that are connected (sanitized, de-duplicated, in order). No candidates → failure message "Select at least one available output.". Current output not among candidates → first candidate. Exactly one candidate and it is current → success, nothing changes. Otherwise the candidate after the current one, wrapping. Switching uses the universal switch (§3.10.2), so it also clears per-app routes. The AirPlay entry is never a candidate.
- Registration failure: "macOS rejected this shortcut. Choose another one." The global key is released while a shortcut field is recording, and re-registered after.
- **Windows**: `RegisterHotKey` with `MOD_NOREPEAT`; suggested default Ctrl+Alt+Shift+S (Control–Option–Command has no 1:1 mapping and many Win-key combinations are reserved). macOS stores shortcuts as `"control+option+command:<macOS virtual key code>"` (S = 1, M = 46); translate to Windows virtual keys, never reuse the numbers.

---

### 3.12 Lower volume when headphones disconnect

- Option (default off): "Lower volume when headphones disconnect" — "Adjusts output when wired or Bluetooth headphones disconnect." When on, a stepper "Volume after disconnect" 10–100% in steps of 5 (default **25%**; stored values below 10 migrate to 25 — it prevents a blast, it never silences).
- Rule, evaluated in every device snapshot: the previous default output was headphones; it is no longer present as headphones; the new default exists and is not headphones; and the app has not already lowered this device since → set the new default's volume to the configured level. If the device's previous level was higher, remember `{device, previous level, applied level}`. Note the shipped behavior: a speaker that was already **below** the configured level is raised to it (only a higher previous level is remembered for restore); a port may choose to skip the write in that case, but should do so deliberately.
- **Restore**: when headphones become the default again, give the speaker back its previous level only if it still equals the level the app applied (`|current − applied| < 0.005`); otherwise forget it (a manual change wins). Also on quit. If the speaker is absent, keep the record for its return.
- Headphone heuristic: §6.8.
- **Windows**: drive it from `IMMNotificationClient` (`OnDefaultDeviceChanged(eRender, eConsole)`, `OnDeviceStateChanged`, `OnDeviceRemoved`). Headphones = `PKEY_AudioEndpoint_FormFactor` ∈ {Headphones (3), Headset (5)} or a Bluetooth enumerator (`BTHENUM`, `BTHLEDevice`), falling back to the name heuristic. Caveat: on many onboard codecs the 3.5 mm jack and the speakers are one "Speakers" endpoint switched internally by jack detection — no endpoint change is visible, so the guard cannot trigger for wired headphones there.

---

### 3.13 Microphone tools

#### 3.13.1 Preferred microphone

- Picker: **Default** (no preference), every input (`"<name> (current)"`), and **"Microphone unavailable"** when the saved preference is not connected (caption "Using default until this microphone returns."). Other captions: "No microphones found", "Could not switch: %@".
- Choosing a device saves `preferredInputDevice` and makes it the **system default input**. The system's original input is remembered the first time the app overrides it. Whenever the preferred device is connected and not current (reconnect, OS switched away), it is applied again.
- On stop or quit: restore the original input only if the app still owns the override (current input equals the device the app applied, the original differs and is connected).
- While Audio priority's microphone half is on, priority owns the input: the picker shows and sets the **current** system input, the dormant preference is not applied, and quitting keeps the priority pick. Turning priority off lets the saved preference take over again.
- The preferred microphone applies only while Volume mixer is installed.
- Input list: devices with input streams, alive, not hidden, able to be default input, not the app's own private devices; default first, then name, then UID.

#### 3.13.2 Input level

- Slider 0–100% with editable percent, shown when the effective input has a settable volume: the main element (virtual main volume, else volume scalar), else the **mean** of the writable per-channel volumes across all input streams. Writes prefer a main control (keeps channel balance); with none, every writable channel is written. Disabled while "Mute microphone" is active.
- Writes are serialized with the mute service (an older gain write can never reopen a muted microphone), cancelled by device or preference changes, verified by reading back (drivers may accept and ignore a write), and coalesced (read once 30 ms after a burst of notifications; an older read never overwrites a newer drag).
- This is input **gain**, not a live level meter.

#### 3.13.3 Mute all microphones

- Entry points: Quick Tools (button "Mute microphone" / "Unmute microphone", status, caption "Cuts the Mac’s microphone with a click or shortcut, across every app."), the tray panel's quick tools, the Quick Launcher, the Dynamic Island, and a global shortcut ("Global shortcut" toggle, default off; default **Control–Option–Command–M**; failure text when registration is refused).
- **Mute** applies to **every** input device (an app can record from a non-default headset):
  - already silent (mute switch on, or volume ≤ 0.01) → left alone; still claimed only if the app had claimed it before;
  - else set the device's mute switch; it counts only if it reads back as set;
  - else save the current level (only if > 0.01) and per-channel levels of channels 1 and 2, set the level to 0, and claim the device only if it now reads ≤ 0.01; otherwise drop the saved levels and — if the device is not an aggregate and some app is recording from it — mark the result **partial**.
- **Unmute** touches only devices the app claimed (`micMuteMutedDevices`). A **missing** claim list (never tracked: restored settings, old versions) restores every present device; an **empty** list means there is nothing the app may open. For each target: clear the mute switch if it is set; else if its level is ≤ 0.01, restore the saved level (per device; else the legacy single value; else **0.75**), restoring each channel's own saved level; a device that cannot be read right now keeps its claim. Claims for devices that are away are carried forward and released when they return.
- State survives relaunch (`micMuteActive`). While muted, or while claims are outstanding, listeners on the device list and the default input re-assert the mute (a headset connecting mid-call is muted on arrival) and catch returning devices. A sweep that reached nothing is dropped. Uninstalling the feature unmutes first; the app's self-uninstall unmutes synchronously.
- Feedback: HUD "Microphone muted" / "Mute off"; partial: "Some microphones could not be muted" / "Some microphones are still muted" (always the floating HUD, retracting any island notice); otherwise, with the Dynamic Island enabled, the island shows the change instead of the HUD.
- Status-icon badge "Show in the menu bar while muted" (default **on**; caption "A red crossed-out mic appears beside the app’s icon in the menu bar while this feature mutes it.").
- All device I/O runs on one serial queue; each sweep reads and writes the claim record on that queue, so two quick sweeps never start from the same record.

#### 3.13.4 Windows mapping

- Inputs: `EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE)`. Default input via `IPolicyConfig::SetDefaultEndpoint` for `eConsole`, `eMultimedia` and usually `eCommunications` (call apps follow the communications default).
- Level: capture-side `IAudioEndpointVolume` (`SetMasterVolumeLevelScalar`; per channel `SetChannelVolumeLevelScalar`).
- Mute: `IAudioEndpointVolume::SetMute(TRUE)` on every active capture endpoint, verified with `GetMute`; fallback to level 0 with saved levels; same claim semantics keyed by endpoint ID; re-assert on `OnDeviceAdded`, `OnDeviceStateChanged`, `OnDefaultDeviceChanged(eCapture)`.
- Optional addition: a live input meter via `IAudioMeterInformation::GetPeakValue`.
- Shortcut: `RegisterHotKey`. Avoid Windows 11's own `Win+Alt+K` call-mute shortcut as a default.
- Badge: overlay a red crossed microphone on the tray icon while muted.

---

### 3.14 Audio device priority

- Opt-in feature; on first install both halves are on (`audioPriorityOutputEnabled`, `audioPriorityInputEnabled`). Surfaces: mixer Options disclosure "Audio priority" (panel), a Settings card, or a panel section "Audio priority" (lists expanded) when Volume mixer is not installed. Caption: "Devices are selected in priority order. When a higher-priority device connects, it becomes active. When the active one disconnects, the next available takes over."
- Two independent ordered lists of device UIDs, ≤ **64** entries each, each with a switch ("Automatically switch to the highest-priority output" / "…microphone"). Row: rank number, name (last-known name when disconnected), accent "Current" badge and highlighted border for the active device, orange "Unavailable" for disconnected entries, drag handle (drag to reorder; accessibility Move Up/Down swap with the neighbor). Empty list text: "No outputs found" / "No microphones found".
- **New list** (empty): current device first, then built-in, then other hardware, then virtual/aggregate, keeping discovery order within a tier. Tier from transport type: built-in → builtIn; virtual, aggregate, auto-aggregate → virtual; anything else → hardware.
- **Newly seen device**: waits **2 s** for the OS to settle, then: if it is the current device → inserted first (the OS or the person just picked it; never taken back); otherwise inserted just above the first virtual entry, or last if it is virtual itself. While an unranked device is current, enforcement does nothing.
- **Enforcement**, debounced **0.25 s**: the first non-empty device snapshot is a baseline (launch is not "every device connecting"); afterwards only a change in the **set** of available UIDs (connect or disconnect), switching a list on, or editing a list triggers enforcement. A change of the default device alone never counts (manual choices stay until hardware changes). Target = first connected UID in list order; switch only if it differs from the current device. Output priority sets only the normal default (off the main thread, never clearing per-app routes); input priority sets the system input as a persistent choice (survives quit).
- Disconnected entries keep their positions; the last-known names map (`audioPriorityDeviceNames`) is pruned to UIDs still in a list.
- **Windows**: same policy over `IMMNotificationClient` and `IPolicyConfig`. Tiers: built-in = endpoints whose device enumerator is an onboard audio bus (`HDAUDIO`, `INTELAUDIO`, ACP, SST — read `PKEY_Device_EnumeratorName`) or an internal jack; virtual = root-enumerated/software devices (`ROOT\…`, known virtual-cable drivers); else hardware (USB, `BTHENUM`, `BTHLEDevice`). Heuristic; manual reordering corrects it.

---

### 3.15 Precise volume roller ("Use finer volume steps")

- Option (default off; caption "Turns volume wheels and keys into smaller system volume steps."). Needs Accessibility: enabling requests it; while missing, an "Open System Settings…" link shows; if the event tap cannot be created: "Could not listen for volume keys.". Runs only while enabled, permitted, and the user session is active (also kept alive when the Dynamic Island routes volume keys).
- Mechanism: a session-level event tap for system-defined media-key events (subtype 8). For volume up/down **key-downs** (key codes 0 and 1): if Option, Command or Control was held when the key went down, the press (and its repeats) is left to the system; otherwise the event is swallowed and replaced by a posted macOS "fine" key press (Option+Shift modifiers), which steps **1/64** instead of 1/16. Posted events carry a marker so the tap ignores them.
- Gate against coarse wheel bursts: accept a press only if more than **30 ms** passed since the last accepted one; a direction reversal within **300 ms** of the last accepted press is accepted only on the **3rd** consecutive press in the new direction (more than 2 confirmations).
- **Windows**: a `WH_KEYBOARD_LL` hook on `VK_VOLUME_UP` / `VK_VOLUME_DOWN` (consumer-control volume knobs arrive as these keys); swallow and apply a custom step via `IAudioEndpointVolume` (e.g. 1%, configurable), with the same gate. Windows' native step is already 2%, so the value is lower. Swallowing the key also suppresses the native volume flyout (no public API shows it) — the app must draw its own indicator.

---

### 3.16 Music app blocker (brief — Drop)

macOS (opt-in switch, needs Accessibility): a media-key event tap records the last key-down of Play/Pause (16), Next (17), Previous (18), Fast-forward (19) or Rewind (20). A launch of `com.apple.Music` or `com.apple.iTunes` (will-launch, or did-launch as a backstop) within **2 s** of such a key, with no newer click or key press, is terminated; optionally a chosen replacement app ("Open instead") is opened, and for Play/Pause asked to play (up to 3 attempts after it finishes launching within 15 s, plus 1 s settle). Windows media keys do not launch a music app by default. **Drop.**

### 3.17 AirPlay per-app route (brief — Drop)

macOS 27 only, via the private routing stack: an "AirPlay" entry in each app's output menu streams that app's tapped audio (ring buffer → sample-buffer renderer bound to a shared output context) to the speaker picked in the system route picker ("Choose AirPlay speaker…"); while no speaker is picked, routed apps play on the default output like unplugged headphones; a stall/start watchdog (10 s / 60 s) reports failures. Windows has no AirPlay. **Drop.**

---

## 4. Settings table

"Backup": **yes** = included in the settings export; **machine** = per-machine state, never exported. Keys marked *(unregistered)* have no registered default: absence means the built-in behavior. Availability keys have the form `featureAvailable.<id>`.

Sanitizing rules shared by many keys: device UIDs and app IDs are trimmed, non-empty, ≤ 512 characters, and contain no control or newline characters; invalid entries are dropped on read.

### 4.1 Maintenance tools

| Key | Type | Default | Range / values | Meaning | Backup |
|---|---|---|---|---|---|
| `featureAvailable.cleaner` | Bool | true | | Cleaner module installed (incl. WhatsApp downloads) | yes |
| `panelUtilityCleaner` | Bool | true | | Cleaner row visible in panel Utilities | yes |
| `cleanerScheduleFrequency` | String | `off` | `off` \| `daily` \| `weekly` (unknown → off) | Automatic cleanup frequency | yes |
| `cleanerScheduleHour` | Int | 9 | 0–23 (clamped) | Automatic cleanup hour | yes |
| `cleanerScheduleMinute` | Int | 0 | 0–59 (UI offers 5-minute steps + stored value) | Automatic cleanup minute | yes |
| `cleanerScheduleWeekday` | Int | 2 (Monday) | 1–7, 1 = Sunday | Weekday for weekly runs | yes |
| `cleanerScheduleNotify` | Bool | true | | Notify after automatic runs | yes |
| `cleanerScreenshotAgeDays` | Int | 30 | 0 = off; offered 7, 14, 30, 60, 90; ≤ 3650 | Forgotten-screenshot age | yes |
| `cleanerLastAutoRun` | Double | 0 | epoch seconds | Last automatic run | machine |
| `cleanerLastAutoFreed` | Int | 0 | bytes | Bytes freed by last automatic run | machine |
| `cleanerLastAutoFailed` | Int | 0 | count | Items left in place by last automatic run | machine |
| `whatsAppDownloadsEnabled` | Bool | false | | WhatsApp downloads sub-feature on | yes |
| `whatsAppDownloadsAutomaticEnabled` | Bool | false | | Daily automatic cleanup on | yes |
| `whatsAppDownloadsCategories` | String | `image,video,audio` | comma list of `image video audio document archive other`; key absent → default; empty → none | Types managed | yes |
| `whatsAppDownloadsRetentionDays` | Int | 7 | 1, 2, 7, 14, 30 (else 7) | Keep-for period | yes |
| `whatsAppDownloadsNotify` | Bool | true | | Notify after automatic cleanup/organization | yes |
| `whatsAppDownloadsIncludeExisting` | Bool | false | | Automation may manage files that predate enabling | yes |
| `whatsAppDownloadsAutomaticStartDate` | Double | 0 | epoch seconds | Automation start (future-only cut-off) | machine |
| `whatsAppDownloadsLastAutoRun` | Double | 0 | epoch seconds | Last automatic pass | machine |
| `whatsAppDownloadsLastCleanup` / `…Count` / `…Bytes` / `…Failed` / `…Automatic` | Double / Int / Int / Int / Bool | 0 / 0 / 0 / 0 / false | | Last cleanup stats | machine |
| `whatsAppDownloadsExclusions` | [String] | [] | `"<device>:<inode>"` | Files the person chose to keep | machine |
| `whatsAppDownloadsAccessConfirmed` | Bool | false | | A scan of Downloads has succeeded | machine |
| `whatsAppOrganizerEnabled` | Bool | false | | Experimental organizer on | yes |
| `whatsAppOrganizerDestinationPath` | String | `""` | `""` = `Downloads/WhatsApp`; never Downloads itself | Destination folder | machine |
| `whatsAppOrganizerDelayMinutes` | Int | 5 | 1, 5, 15, 60 (else 5) | Stability delay | yes |
| `whatsAppOrganizerCategories` | String | all six | as above | Types organized | yes |
| `whatsAppOrganizerLayout` | String | `flat` | `flat` \| `category` \| `month` | Folder structure | yes |
| `whatsAppOrganizerDuplicateAction` | String | `trashNew` | `trashNew` \| `keepBoth` \| `replaceExisting` | Duplicate policy | yes |
| `whatsAppOrganizerRecords` | Data | empty | JSON, ≤ 5000 records | Organized files and digests | machine |
| `whatsAppOrganizerUndoTransaction` | Data | empty | JSON array, ≤ 20, 7-day validity | Undo journal | machine |
| `whatsAppOrganizerLastRun` / `…LastMoved` / `…LastDuplicates` / `…LastFailed` | Double / Int / Int / Int | 0 | | Last organization stats | machine |
| `featureAvailable.uninstaller` | Bool | true | | Uninstaller installed | yes |
| `panelUtilityUninstaller` | Bool | true | | Uninstaller row in panel | yes |
| `uninstallerCommandBarEnabled` | Bool | false | | Uninstall rows in the Command Bar | yes |
| `featureAvailable.appUpdates` | Bool | true | | App updates installed | yes |
| `panelUtilityAppUpdates` | Bool | true | | App updates row in panel | yes |
| `appUpdatesCheckFrequency` | String | `off` | `off` \| `daily` \| `weekly` | Background check | yes |
| `appUpdatesIncludeHomebrewApps` | Bool | true | at least one source stays on | Package-manager source | yes |
| `appUpdatesIncludeAppStore` | Bool | true | | Store source | yes |
| `appUpdatesIncludeOnlineCatalog` | Bool | true | | Feeds + public catalog source | yes |
| `appUpdatesNotify` | Bool | true | effective only with a schedule | Notify on new findings | yes |
| `appUpdatesRules` | String | `[]` | JSON `[{bundleID, name, version?}]` | Skip/exclude rules | yes |
| `appUpdatesLastCheck` | Double | 0 | epoch seconds | Last finished check | machine |
| `appUpdatesLastCount` | Int | 0 | | Visible findings of last check | machine |
| `appUpdatesNotifiedIDs` | [String] | [] | row IDs | Findings already announced | machine |
| `featureAvailable.homebrew` | Bool | true | | Homebrew manager installed | yes |
| `panelUtilityHomebrew` | Bool | true | | Homebrew row in panel | yes |
| `homebrewGroupDependencies` | Bool | true | | Fold dependencies under their parents (Settings page) | yes |
| `featureAvailable.portManager` | Bool | false | | Port Manager installed | yes |
| `panelUtilityPortManager` | Bool | true | | Port Manager row in panel | yes |
| `featureAvailable.killProcess` | Bool | false | beta | Kill Process installed | yes |
| `killProcessCommandBarEnabled` | Bool | true | | Process rows in the Command Bar | yes |
| `killProcessGroupRelated` | Bool | true | | Group helpers under their app | yes |
| `killProcessSortBy` | String | `cpu` | `cpu` \| `memory` \| `name` \| `pid` | Sort column | yes |
| `killProcessSortAscending` | Bool | false | | Sort direction | yes |
| `featureAvailable.diskImageInstaller` | Bool | false | | Disk image installer installed | yes |
| `diskImageInstallerTrashesDownload` | Bool | true | | Trash the `.dmg` after install | yes |
| `diskImageInstallerRevealsApp` | Bool | false | | Reveal installed app | yes |
| `diskImageInstallerUseUserApplications` | Bool | false | | Install into `~/Applications` | yes |

### 4.2 Sound

| Key | Type | Default | Range / values | Meaning | Backup |
|---|---|---|---|---|---|
| `featureAvailable.mixer` | Bool | true | | Volume mixer installed | yes |
| `appVolumes` *(unregistered)* | [String: Double] | absent (all 100%) | 0.0–2.0; unity entries removed | Saved per-app volume by persistence ID | yes |
| `appOutputDevices` *(unregistered)* | [String: String] | absent | persistence ID → device UID | Saved per-app output | yes |
| `mixerUniversalOutputDevice` *(unregistered)* | String | absent | device UID | Last successful manual "all apps" output | yes |
| `mixerShowFinder` | Bool | true | | Persistent Finder row | yes |
| `mixerAppArrangement` | String | `""` | JSON `{order:[id], pinned:[id]}` | Pins and custom order | yes |
| `mixerHideInactiveApps` | Bool | false | | Hide idle, uncustomized rows | yes |
| `mixerHiddenApps` *(unregistered)* | [String: String] | absent | persistence ID → display name (never Finder) | Apps taken off the list | yes |
| `mixerLowerVolumeOnHeadphonesDisconnect` | Bool | false | | Headphone guard | yes |
| `mixerHeadphonesDisconnectVolumePercent` | Int | 25 | 10–100 (UI step 5); < 10 migrates to 25 | Level after disconnect | yes |
| `preciseVolumeRollerEnabled` | Bool | false | needs Accessibility | Finer volume key steps | yes |
| `featureAvailable.soundOutputSwitcher` | Bool | true | | Output switcher installed | yes |
| `soundOutputSwitcherEnabled` | Bool | false | | Shortcut active | yes |
| `soundOutputSwitcherShortcut` | String | `control+option+command:1` | `<modifiers>:<macOS key code>` | Cycle shortcut (⌃⌥⌘S) | yes |
| `soundOutputSwitcherDeviceUIDs` *(unregistered)* | [String] | absent | ordered, unique UIDs | Outputs in the cycle | yes |
| `featureAvailable.audioPriority` | Bool | false | | Audio priority installed | yes |
| `audioPriorityOutputEnabled` | Bool | true | | Enforce output list | yes |
| `audioPriorityInputEnabled` | Bool | true | | Enforce microphone list | yes |
| `audioPriorityOutputUIDs` | [String] | [] | ≤ 64 unique UIDs | Output priority order | yes |
| `audioPriorityInputUIDs` | [String] | [] | ≤ 64 unique UIDs | Microphone priority order | yes |
| `audioPriorityDeviceNames` | [String: String] | {} | UID → last-known name | Names for disconnected entries | yes |
| `preferredInputDevice` *(unregistered)* | String | absent | device UID | Preferred microphone | yes |
| `featureAvailable.micMute` | Bool | true | | Mute microphone installed | yes |
| `micMuteShortcutEnabled` | Bool | false | | Global mute shortcut on | yes |
| `micMuteShortcut` | String | `control+option+command:46` | | Mute shortcut (⌃⌥⌘M) | yes |
| `micMuteMenuBarIndicator` | Bool | true | | Status-icon badge while muted | yes |
| `micMuteActive` | Bool | false | | Mute is on (survives relaunch) | machine |
| `micMuteSavedVolume` | Double | 0.75 | | Legacy single saved level | machine |
| `micMuteSavedVolumes` | [String: Double] | absent | UID → level | Levels to restore | machine |
| `micMuteSavedChannelVolumes` | [String: [String: Double]] | absent | UID → {channel: level} | Channel levels to restore | machine |
| `micMuteMutedDevices` | [String] | absent | absent ≠ empty (§3.13.3) | Devices this app silenced | machine |
| `featureAvailable.musicBlock` | Bool | true | | Music blocker installed | yes |
| `musicBlockEnabled` | Bool | false | | Blocker on | yes |
| `musicBlockReplacementPath` | String | `""` | app path | App opened instead | yes |
| `musicBlockPlayReplacement` | Bool | true | | Play after Play/Pause opens it | yes |

---

## 5. Data and files

### 5.1 Storage

- macOS keeps everything in the app's preferences domain (`com.vorssaint.utils`). Types used: Bool, Int, Double, String, `[String]`, `[String: String]`, `[String: Double]`, nested dictionaries, and `Data` blobs holding JSON.
- **Windows**: any per-user store works (a JSON file under `%APPDATA%\Vorssaint\` keeps backups simple; or `HKCU\Software\Vorssaint`). Preserve key names, value types and the "absent vs empty" distinctions (`micMuteMutedDevices`, `whatsAppDownloadsCategories`, unregistered maps).

### 5.2 JSON documents

| Key | Shape |
|---|---|
| `appUpdatesRules` | `[{"bundleID": "com.x.y", "name": "Display Name", "version": "1.2.3"}]` — `version` omitted/null = exclude app |
| `mixerAppArrangement` | `{"order": ["id", …], "pinned": ["id", …]}` |
| `whatsAppOrganizerRecords` | `[{"digest": "<sha256 hex>", "destinationPath": "/…", "originalName": "…", "size": 123, "organizedAt": <date>}]` |
| `whatsAppOrganizerUndoTransaction` | `[{"id": "<UUID>", "actions": [{"kind": "move"\|"trash", "currentPath": "/…", "restorePath": "/…"\|null}], "recordsBefore": [Record], "recordsAfter": [Record], "createdAt": <date>}]` |

Dates in the organizer JSON use the Swift default encoding: a number of **seconds since 2001-01-01T00:00:00Z** (not the Unix epoch). A Windows reader of migrated data must convert (`unix = value + 978307200`). All other timestamps in §4 are Unix epoch seconds.

### 5.3 Files and system objects touched

| Feature | Creates / changes |
|---|---|
| Cleaner | Moves selected items to the Trash; empties the Trash; unloads user launch agents (`launchctl bootout`); admin batch via Finder |
| Uninstaller | Moves the app and selected leftovers to the Trash; quits the app; may run `brew uninstall --cask` |
| App updates / Homebrew | Runs `brew` (install/uninstall/upgrade/update/trust); opens Terminal with commands; opens the App Store or apps |
| Kill Process / Port Manager | Signals processes; admin prompt via `osascript … with administrator privileges` |
| WhatsApp downloads | Moves files to the Trash; organizer creates destination subfolders, temporary `.vorssaint-<uuid>.partial` and `.vorssaint-replaced-<uuid>.partial` files next to the destination (removed on failure) |
| Disk image installer | Staging folder (item-replacement directory), copies the app into Applications, ejects the image, trashes the `.dmg` |
| Mixer | Private (process-local) HAL objects: process taps and aggregate devices named `Vorssaint Mixer` / `Vorssaint AirPlay`; changes default output/system output devices; changes output volume/mute |
| Mic tools | Changes default input, input gain, input mute switches |
| Priority / switcher | Changes default output/input |

### 5.4 Network endpoints

| Endpoint | Feature | Notes |
|---|---|---|
| `https://itunes.apple.com/lookup?bundleId=…&entity=macSoftware&country=…` | App updates (store) | batches of 20 |
| `https://uclient-api.itunes.apple.com/WebObjects/MZStorePlatform.woa/wa/lookup?id=…&version=2&p=mdm-lockup&caller=MDM&platform=macappstore&cc=…` | App updates (store, Mac-specific metadata) | batches of 20 |
| `https://formulae.brew.sh/api/cask.json` | App updates (catalog) | 1 h cache |
| Publisher feeds (`SUFeedURL`, `…/latest-mac.yml`, GitHub `releases/latest/download/latest-mac.yml`) | App updates (feeds) | public https only, ≤ 2 MB, ≤ 5 redirects |
| `https://formulae.brew.sh/api/analytics/install-on-request/homebrew-core/30d.json`, `…/cask-install/homebrew-cask/30d.json` | Homebrew popularity | 24 h cache |
| `https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh` | Homebrew installer (run in Terminal by the person) | |

No endpoint receives personal data beyond bundle IDs / store IDs needed for lookups and the region code.

### 5.5 Backups and resets

Exported: every registered key except the machine-state keys marked in §4, plus the unregistered maps listed (`appVolumes`, `appOutputDevices`, `mixerUniversalOutputDevice`, `mixerHiddenApps`, `preferredInputDevice`, `soundOutputSwitcherDeviceUIDs`). Restoring `appUpdatesRules` while a scan runs re-runs the scan. A restored `micMuteActive` is never imported (levels and device IDs belong to one machine).

---

## 6. Algorithms and constants

### 6.1 Cleaner cache classification

```
excluded(name)  = name ends ".localized" (ci) OR name has ci-prefix in HIDDEN
HIDDEN = com.vorssaint, CloudKit, com.apple.bird, com.apple.coreaudio, com.apple.audio.,
         coreaudiod, com.apple.systempreferences, com.apple.controlcenter, com.apple.finder,
         com.apple.dock, com.apple.FontRegistry, com.apple.ATS, com.apple.akd, com.apple.AuthKit,
         com.paceap., com.native-instruments, com.fabfilter, com.spotify.client
SENSITIVE = ms-playwright                      (listed, never pre-checked)
SAFE_PLAIN = homebrew, pip, node-gyp, yarn, npm, google, electron, cypress, typescript, puppeteer
precheck(name) = !excluded && !sensitive &&
                 (looksLikeBundleID(name) ? !lower.hasPrefix("com.apple.") : SAFE_PLAIN ∋ lower)
```
The same `excluded` rule filters Logs. Apple caches are listed but never pre-checked (the system rebuilds them eagerly).

### 6.2 Name shapes

- `looksLikeBundleID`: ≥ 2 dot-separated parts, none empty, characters `[A-Za-z0-9_-]`.
- Team ID: exactly 10 characters, `[A-Z0-9]`, input already uppercase.
- UUID present: any substring `8-4-4-4-12` hex groups with dashes. Trailing ByHost UUID: last dot-component of exactly 36 characters containing a UUID.
- Uninstaller token: `normalizedToken(s)` = Unicode letters and digits of `s` (minus a trailing `.app`, trimmed), lowercased.

### 6.3 Schedules

| Schedule | Next fire | Missed / due | Busy retry | Tolerance |
|---|---|---|---|---|
| Cleaner | next calendar match after now (hour, minute[, weekday]) | last run exists and next-after-last-run ≤ now → now + 120 s | +600 s | 5 s |
| App updates | lastCheck + 24 h / 7 d | never checked or due → now + 180 s | merged into running scan | 60 s |
| App updates on surface open | — | rescan if hand-off pending, not checked this session, or ≥ 600 s old | | |
| WhatsApp cleanup | next 09:00 local | last run exists, now ≥ today 09:00, last run < today 09:00 → now + 120 s | +600 s | 10 s |
| WhatsApp organizer | watch event + 2 s | not stable → eligibility time + 1 s | running → +60 s; review/manager busy → +300 s; watch failed → +300 s | min(10, max(1, delay/10)) s |

All re-arm on wake, time-zone change and clock change (Cleaner, WhatsApp) or wake (App updates).

### 6.4 Version comparison

See §3.3.10 (normative). Pseudocode of one part:
```
cmpPart(a, b):
  if digits(a) and digits(b): return numCmp(a, b)          # strip leading zeros, length, lexical
  if a == "": return (digits(b) and allZeros(b)) ? same : older
  if b == "": return (digits(a) and allZeros(a)) ? same : newer
  if a == b: return same
  na, nb = leadingDigits(a), leadingDigits(b)
  if empty(na) != empty(nb): return empty(na) ? older : newer
  if na: r = numCmp(na, nb); if r != same: return r
         if bare(a) != bare(b): return bare(a) ? newer : older   # "5" > "5beta"
  return caseFold(a) <=> caseFold(b)
```

### 6.5 Boost limiter (only needed if boost is implemented)

Constants: ceiling `C = 0.944` (≈ −0.5 dBFS); release `160 ms`; release coefficient `r(fs) = exp(−1000 / (fs × 160))` with `fs = 48000` when the rate is non-finite or < 8000 (at 48 kHz `r ≈ 0.99987`); lookahead `L = 256` frames (5.3 ms at 48 kHz). All channels of a frame (across all buffers) share one gain. The coefficient is recomputed when the device's nominal rate changes (headsets renegotiate during calls) and handed to the audio thread atomically.

Zero-lookahead limiter (fallback):
```
for each frame: p = max |x_ch|
  env = p > env ? p : p + (env − p) × r
  if env > C: x_ch *= C / env
```

Lookahead limiter (normal path; allocate the delay line before audio starts):
```
for each input frame:
  p = max |x_ch| over all channels
  req = p > C ? C / p : 1
  if req < target:
      target = req
      step = (target − gain) / L
      attackStep = attacking ? min(attackStep, step) : step     # keep the faster ramp
      attackFrames = L
  if req < 1: hold = L
  if attackFrames > 0: gain += attackStep; attackFrames −= 1; if gain <= target: gain = target; attackFrames = 0
  elif req < 1: (keep gain)
  elif hold > 0: hold −= 1
  else: gain = 1 + (gain − 1) × r; target = gain
  out_ch = delayed_ch(L frames ago, 0 while filling) × gain; write x_ch into the delay line
```
A change in channel count resets the state; more channels than preallocated → fall back to the zero-lookahead limiter. Samples already inside the ceiling pass through bit-identical (after the fixed delay). The limiter runs on every live engine, attenuated ones included, so moving into boost never inserts silence.

### 6.6 Rendering a stereo source into any output layout

```
frames = min(sourceFrames, every writable output buffer's frames)
if one output buffer with the same channel count: out = src × gain                (plain copy)
elif output has 1 channel and source > 1: out = gain/srcCh × Σ src_ch              (fold to mono)
else for each output channel i (counted across buffers):
       src = i < srcCh ? i : (srcCh == 1 and i == 1 ? 0 : none)
       out_i = src != none ? src × gain : 0
silence every frame the source did not fill (never leave stale memory); count a cycle only if frames > 0
```

### 6.7 Tap level compensation (macOS only — do not port)

`factor = min(max(minStreamChannels / 2, 1), 4)` over the outputs the app currently plays to (unreadable output = 2 channels; stream = the one containing the preferred stereo pair). Provisional value on a move = 1; kept for a silent app only while the default output's devices are a subset of its last outputs. Per-cycle ramp: `limit = 4 ^ min(dt / dur, 1)`, `dur = 0.005 s` when decreasing and `0.03 s` when increasing; `next = clamp(target, applied / limit, applied × limit)`. Re-read at most once per second unless announced; first read awaited ≤ 50 ms.

### 6.8 Headphone heuristic

`haystack = (name + " " + uid + " " + dataSourceName)` → case- and diacritic-folded, lowercased, every run of non-`[a-z0-9]` replaced by a space. Headphones if it contains any of: `headphone`, `headphones`, `headset`, `earphone`, `earphones`, `earbud`, `earbuds`, `airpod`, `airpods`, `earpod`, `earpods`, `galaxy buds`, `pixel buds`, `beats`, `bose qc`, `sony wh`, `sony wf`, `jabra`, `soundcore`. Built-in speakers and Bluetooth speakers (without those words) are not headphones. Windows: prefer `PKEY_AudioEndpoint_FormFactor`, keep this as fallback.

### 6.9 Output switcher

```
candidates = unique(sanitized(selected)) ∩ connectedDefaultCapable   (keep selection order)
if candidates empty: fail → "Select at least one available output."
if current ∉ candidates: target = candidates[0]
elif candidates.count == 1: success, no change
else target = candidates[(index(current) + 1) % count]
```

### 6.10 Priority lists

```
initial(available, current) = [current if available] + stableSortByTier(available − current)
    tier: builtIn(0) < hardware(1) < virtual(2)
place(newUID, list, isCurrent):
    if isCurrent: return [newUID] + list
    if tier(newUID) != virtual and list has a virtual entry: insert before the first virtual
    else append
enforce: target = first(list ∩ available); switch iff target != current
trigger: set(available) changed vs settled set (first snapshot = baseline) OR list enabled OR list edited
debounce 0.25 s; placement delay 2 s; lists ≤ 64
```

### 6.11 Timing constants (sound)

| Constant | Value |
|---|---|
| Per-app volume range / unity tolerance / mute threshold | 0–2.0 / 0.005 / 0.001 |
| HAL notification coalescing (mixer and input manager) | 0.2 s |
| Output-control and input-volume read debounce | 0.03 s |
| Engine churn window (audio objects briefly gone) | 0.2 s |
| Render stall window (wedged engine) | 1.5 s; one rebuild per configuration |
| Engine teardown concurrency | 4 |
| Wake: reconcile now + again after | 2 s |
| Owning-app parent walk depth | 6 |
| Headphone guard level | 10–100%, default 25% |
| Volume key steps | 1/16 normal, 1/64 fine |
| Precise roller gate | > 30 ms spacing; reversal window 300 ms; reversal accepted on 3rd press |
| Priority debounce / new-device placement / max list | 0.25 s / 2 s / 64 |
| Mic mute silence threshold / fallback restore level | 0.01 / 0.75 |

### 6.12 Kill Process constants

`ps` timeout 5 s; refresh every 3 s while visible and focused; freshness cache 3 s; post-kill refresh 0.5 s; tree walk cap 4096; restart wait 10 s; start-time identity in microseconds.

### 6.13 Homebrew parsing

- Percent: `([0-9]{1,3}(?:\.[0-9]+)?)%` → last match / 100, clamped 0–1.
- ANSI: `\x1B\[[0-9;?]*[ -/]*[@-~]` removed.
- Untrusted tap: `from untrusted tap ([A-Za-z0-9._-]+/[A-Za-z0-9._-]+)` (trailing `.` trimmed; token-validated).
- Needs Terminal: output (lowercased) contains `sudo:`, `a terminal is required`, `password is required`, `password:`, or `administrator privileges`.
- Shell quoting for "Open Terminal"/"Copy": a value matching `^[A-Za-z0-9._+@%/=:,-]+$` is left bare, else single-quoted with `'` → `'\''`.
- Compact counts: ≥ 1 000 000 → `N.NM` (< 10M) or `NM`; ≥ 1 000 → `N.NK` (< 10K) or `NK`; else the number.
- Elapsed: `<60 s` → `Ns`; `<60 min` → `Nmin`; else `Nh Mmin`.

### 6.14 Volume text entry

`trim → drop trailing "%" → replace locale decimal separator with "." → parse Double (finite) → clamp to [0, max%] → /100`; failure = beep and keep editing.

### 6.15 WhatsApp rules

```
oldEnough      = downloadedAt <= now − days  AND  modifiedAt <= now − days        (calendar days)
rules          = category ∈ enabled AND oldEnough
automatic      = rules AND !organized AND (includeExisting OR downloadedAt >= automaticStartDate)
stable (org.)  = now − max(downloadedAt, modifiedAt) >= delayMinutes × 60
undo valid     = 0 <= now − createdAt < 7 × 86400
```

### 6.16 Sizes

Allocated size (`totalFileAllocatedSize` → `fileAllocatedSize`), recursive, symlinks not followed (Cleaner counts a symlink as 0, Uninstaller as its own size). Display with the platform file-size formatter.

---

## 7. macOS dependencies → Windows mapping

| macOS dependency | Used by | Windows equivalent | Gap / risk |
|---|---|---|---|
| `FileManager.trashItem` | Cleaner, Uninstaller, WhatsApp | `IFileOperation::DeleteItems` with `FOFX_RECYCLEONDELETE \| FOF_ALLOWUNDO`, progress sink (`PostDeleteItem.psiNewlyCreated` = recycled item) | Silent permanent delete for non-recyclable volumes or oversized items → pre-check and refuse |
| Finder "empty trash" (AppleScript) | Cleaner | `SHQueryRecycleBinW`, `SHEmptyRecycleBinW` | none |
| Finder delete with admin prompt | Cleaner, Uninstaller | Elevated helper process (UAC `runas`) doing the same guarded recycle | Other-admin elevation recycles into another account's bin |
| `lstat` device+inode identity | all removals | `GetFileInformationByHandleEx(FileIdInfo)` (volume serial + 128-bit file ID) | none |
| Symlink checks | all scans | `FILE_ATTRIBUTE_REPARSE_POINT` (symlinks, junctions, mount points); cloud placeholders | Profile junction loops; OneDrive hydration on read |
| Allocated size resource keys | all sizes | `FILE_STANDARD_INFO.AllocationSize` / `GetCompressedFileSizeW` | none |
| Extended attributes (`kMDItemIsScreenCapture`, `lastuseddate#PS`) | Screenshots | none | Weaker evidence; last-access often disabled |
| Quarantine `LSQuarantineAgentName` | WhatsApp | `Zone.Identifier` ADS (`HostUrl`, `ReferrerUrl`) | May be absent / not per-app → feature at risk |
| Bundle/Info.plist, bundle IDs | Cleaner, Uninstaller, App updates, Mixer | ARP registry values, exe version resources, MSIX manifest (PFN/AUMID) | No universal app identity for Win32 apps |
| Launch Services "app with bundle ID exists" | Cleaner living-owner check | ARP + MSIX + App Paths lookups | weaker |
| Code signing team ID / app groups | Uninstaller | Authenticode signer (`WinVerifyTrust`, `CryptQueryObject`); MSIX publisher ID | No app-group concept |
| Container metadata plist | Cleaner, Uninstaller | `%LOCALAPPDATA%\Packages\<PFN>` | Only for packaged apps (exact) |
| Spotlight (`MDQuery`, `mdfind`) | Uninstaller, App updates | Windows Search (`SystemIndex` via OLE DB/`ISearchQueryHelper`) or plain enumeration | Optional |
| `launchctl bootout` | Cleaner | none for Run keys; Task Scheduler COM; SCM for services | Registry/task removal not recyclable → export backups |
| `confstr` per-user cache/temp dirs | Uninstaller | `GetTempPath2W`, `%LOCALAPPDATA%` | none |
| Homebrew CLI | Homebrew manager, App updates, Uninstaller handoff | winget COM API / CLI; Scoop; Chocolatey | CLI text parsing fragile; no formula/cask split; no dependency graph |
| AppleScript → Terminal `do script` | Homebrew | Windows Terminal `wt.exe new-tab cmd /k …` | none |
| iTunes/App Store lookup, `macappstore://` | App updates | winget `msstore`; `ms-windows-store://` URIs | Querying other apps' Store updates may need privileges |
| Sparkle appcast / electron-builder `app-update.yml` | App updates | electron `resources\app-update.yml` → `latest.yml`; WinSparkle appcast | WinSparkle/Squirrel URLs usually not discoverable |
| `lsof` | Port Manager | `GetExtendedTcpTable` / `GetExtendedUdpTable` | Lists all users (behavior change) |
| `ps`, `proc_pidinfo`, `proc_pidpath` | Kill Process, Port Manager | Toolhelp32 / `NtQuerySystemInformation`, `GetProcessTimes`, `GetProcessMemoryInfo`, `QueryFullProcessImageNameW` | CPU needs two samples |
| `kill()`, `NSRunningApplication.terminate/forceTerminate` | Kill Process | `WM_CLOSE` to top-level windows; `TerminateProcess` | No graceful signal for windowless processes |
| `osascript … with administrator privileges` | Kill Process | UAC-elevated helper | none |
| Responsibility API (`responsibility_get_pid_responsible_for_pid`) | Kill grouping, Mixer | none → parent/exe heuristics | Grouping is approximate |
| Workspace "app terminated" notification | Restart | `WaitForSingleObject` on process handle | none |
| kqueue directory watch | Organizer | `ReadDirectoryChangesW` | none |
| CryptoKit SHA-256 | Organizer | CNG `BCryptHash` | none |
| `hdiutil`, `ditto`, `codesign`, `spctl` | Disk image installer | — | Dropped |
| CoreAudio HAL devices + listeners | all Sound | MMDevice: `IMMDeviceEnumerator`, `IMMNotificationClient`, property store keys | none |
| Set default output / input (`kAudioHardwarePropertyDefault…Device`) | Mixer, switcher, priority, mic | `IPolicyConfig::SetDefaultEndpoint` (undocumented) per role | Undocumented COM interface |
| Default *system* (alerts) output | Mixer header | none (System Sounds session routing via undocumented per-app API, or `eCommunications`) | Redesign/drop |
| Device volume/mute properties | Mixer, headphone guard, keys, mic | `IAudioEndpointVolume` (+ callback) | none |
| Process objects, IsRunningOutput | Mixer discovery | `IAudioSessionManager2`, `IAudioSessionControl2`, `GetState`, `IAudioMeterInformation` | none (simpler) |
| Process tap + aggregate + IO proc (gain/route) | Mixer | `ISimpleAudioVolume` (attenuation only) | **No boost**; routing via undocumented `IAudioPolicyConfigFactory` |
| Carbon `RegisterEventHotKey` | Switcher, Mic mute | `RegisterHotKey` | Different modifier set; reserved Win combos |
| CGEventTap (media keys) | Precise roller, Music blocker | `WH_KEYBOARD_LL` | Native volume flyout suppressed when swallowing keys |
| Accessibility trust (`AXIsProcessTrusted`) | Precise roller, Music blocker | not needed | none |
| TCC grants (Full Disk Access, Files & Folders, Audio capture, Automation, App Management) | many | none; UAC for machine areas; Controlled Folder Access may block writes to protected folders (Pictures/Desktop) | Screenshot cleaning may hit Controlled Folder Access |
| `UNUserNotificationCenter` (+ actions) | Cleaner, App updates, WhatsApp | Windows toast notifications (AUMID; COM activator for the Undo action) | Unpackaged apps need registration |
| Wake / clock / time-zone notifications | schedules | `WM_POWERBROADCAST`, `WM_TIMECHANGE` | none |
| `NSAlert`, `NSOpenPanel`, Reveal in Finder | many | message dialogs, `IFileOpenDialog` (folders), `SHOpenFolderAndSelectItems` | none |

---

## 8. Porting notes

### 8.1 Drop list

- Disk image installer (M13), Music app blocker (S13), AirPlay per-app route (S14).
- Mixer internals that only exist because of macOS taps: tap level compensation, render watchdog/recovery, churn window, build tokens for taps, Zoom/DAW bypass list, "Screen & System Audio Recording" permission hint.
- Boost above 100% and the limiter for v1 (keep §6.5–6.6 for a later prototype).
- Homebrew-only concepts: tap trust card, shell-setup banner, Install-Homebrew-in-Terminal flow (replace with App Installer/Scoop guidance), formula/cask split (→ source filter), dependency grouping and orphans, popularity analytics, Homebrew environment resolution.
- macOS-only Cleaner content: Xcode developer junk paths, launch daemons, `~/Library` roots (replaced by §3.1.9), Full Disk Access notes (→ "scan system locations as administrator").
- Finder row (→ System Sounds row); "System sounds" output picker (→ drop or Communications device).

### 8.2 Behavior changes to communicate in the UI

- Uninstalling an app runs its vendor uninstaller and is **not reversible**; only leftovers go to the Recycle Bin.
- Per-app output changes may need the app to restart its audio.
- Volume slider tops out at 100% (unless a boost path ships later).
- Port Manager may list other users' and system listeners (or filter them out explicitly).
- CPU % semantics differ (Windows: share of total CPU, two-sample average).
- Forgotten screenshots cannot know whether a screenshot was opened.
- Some items (registry values, tasks, services) are restored from the app's own backup, not from the Recycle Bin.

### 8.3 Biggest risks (ranked)

1. **Per-app output routing and default-device switching use undocumented COM interfaces** (`IAudioPolicyConfigFactory`, `IPolicyConfig`). GUIDs/vtables changed between Windows 10 builds and Windows 11; apps often need to reopen streams. Mitigate: version-gated adapters, runtime capability probes, feature flags, clear UI hint, telemetry-free self-test on startup.
2. **No driverless per-app boost.** The macOS tap + aggregate + limiter design cannot be reproduced with WASAPI session volume (max 1.0). Process-loopback re-rendering is unproven (muting the source likely mutes the loopback). Mitigate: cap at 100% in v1; prototype separately.
3. **Uninstaller semantics and evidence change.** Vendor uninstallers (interactive, UAC, sometimes reboot-requiring, irreversible) replace "move to Trash"; leftover matching loses reverse-DNS bundle IDs, signed app groups and container metadata — only MSIX package folders are exact. Name-token matching on vendor/product folders and registry keys is riskier. Mitigate: strict "exact = path/PFN/ProductCode evidence" vs "related = unchecked", trusted install roots, publisher-sharing exclusion, re-scan after the uninstaller, backups for registry/tasks.
4. **"Recycle, never delete" is harder to guarantee.** Silent permanent deletion for non-recyclable volumes/oversized items, registry/task/service items not recyclable, elevation needed for machine caches (and Disk Cleanup handlers purge permanently), OneDrive placeholders that hydrate when read, junction loops in profiles. Mitigate: pre-checks and refusal, app-owned backup folder with restore, reparse-point guard, elevated helper re-running the same guard.
5. **Provenance-based features lose their OS evidence.** WhatsApp cleanup relies on the quarantine agent name (Windows `Zone.Identifier` may be absent and is not per-app); forgotten screenshots rely on a capture flag and last-opened date (neither exists; NTFS last access often disabled). Mitigate: verify MOTW from WhatsApp Desktop before committing; otherwise drop automation or the feature; keep screenshots review-only with honest captions.

Also notable: winget CLI output is localized and truncated (use the COM API); Store update discovery for other apps may need privileges; publisher-feed discovery works mainly for electron-updater apps; headphone-disconnect detection fails on shared 3.5 mm jacks; process grouping is heuristic; swallowing volume keys hides the native flyout.

### 8.4 Implementation order

1. **Shared Windows services** (each with unit tests):
   - *SafeFileOps*: identity capture/compare, reparse-point and cloud-file guards, critical-path set, recyclability pre-check, guarded recycle with verification, elevated helper protocol (batch, re-validate, report per item), backup/restore for registry values, tasks, services.
   - *AudioDevices*: endpoint enumeration, properties (name, form factor, enumerator), `IMMNotificationClient` with 0.2 s coalescing, default get/set (`IPolicyConfig` adapter), endpoint volume/mute with a single-writer queue.
   - *AudioSessions*: session discovery per endpoint, grouping, per-app volume/mute, event contexts.
   - *ProcessSnapshot*: snapshot, creation-time identity, CPU sampling, protected/critical detection, kill (graceful/force), elevated kill helper.
   - *PackageManager* (winget COM first; CLI fallback) with a single operation lane, progress, cancellation, timeouts (reads 30 s, operations 15 min of silence).
   - *Scheduler* (wall-clock timers + resume/time-change re-arm) and *Toasts*.
2. **Sound**: mixer rows (discovery, volume, mute, persistence, hide, hide-inactive, pin/reorder, percent editor) → Output picker + output volume → Microphone picker + input level → Mute all microphones (+ shortcut, tray badge) → Output switcher → Audio priority → Headphone guard → Precise roller → per-app output routing (flagged).
3. **Kill Process**, then **Port Manager** (reuses the kill path).
4. **App updates** on winget (+ Store hand-off, electron feeds), then the **winget manager** UI.
5. **Cleaner**: safe categories first (temp, browser/app caches, crash dumps & WER, Recycle Bin), then schedule + toasts, then orphaned startup items (with backups), then MSIX/per-user leftovers, iPhone backups, screenshots, optional Disk Cleanup handlers.
6. **Uninstaller**: app list (ARP + MSIX) → run uninstaller → verify → leftover scan/review → package-manager handoff.
7. **WhatsApp downloads**: only after the provenance prototype (§3.7.7).

### 8.5 Platform-independent logic to port verbatim

Version comparison (§3.3.10), update rules (§3.3.8), selection reconciliation and merge order (§3.3.7), schedule math (§6.3, incl. 12-hour conversion), Cleaner pre-check/protected/owner rules where they still apply, arrangement semantics (§3.9.6), hide/visibility rules (§3.9.2), volume text parsing (§6.14), output-switcher next (§6.9), priority list init/placement/enforcement triggers (§6.10), mic-mute claim semantics (§3.13.3), WhatsApp retention/stability/undo rules (§6.15), Homebrew-style progress/error parsing adapted to winget, token validation, port row parsing rules (dedupe, wildcard detection, browser URL mapping), kill-tree walk.

### 8.6 Tests worth porting (from `Tests/`)

`CleanerEligibilityTests`, `CleanerScanFlowTests`, `CleanerLastRunContract`, `CleanerLayoutTests`, `UninstallerFlowTests`, `AppUpdateRulesTests`, `AppUpdatesTests` (feed/catalog coverage), the App-updates block of `UpdateFeatureTests` (version comparison and package rows), `AppManagementFeatureTests` (cleaner/uninstaller/schedule/WhatsApp rules), `UtilitiesFeatureTests` (port parser, Homebrew commands), `PortManagerRefreshTests`, `ProcessForceQuitTests`, `ProcessNameTests`, `MixerFeatureTests` (sanitizers, arrangement, visibility, limiter, rendering), `MixerInputVolumeTests` (gain + mute interplay), `MixerOutputAdjustmentTests` (write coalescing, keys), `MixerUniversalRoutingTests`, `MixerPercentKeyTests`, `MixerNativeDragTests`, `AudioPriorityTests`, `SoundOutputSwitchTests`. Tests about taps, aggregates, level compensation and AirPlay (`MixerLevelCompensationTests`, `AirPlayStreamTests`) do not apply.

### 8.7 Open questions for the Windows team

- Does WhatsApp Desktop write Mark-of-the-Web, and with which URLs?
- Which per-app identity key (exe path vs exe name vs AUMID) will the mixer store? (Changing later needs a migration.)
- Is the System Sounds session routable with `IAudioPolicyConfigFactory`?
- Can a desktop app enumerate other packages' pending Store updates?
- Will the product ship an elevated helper (service vs on-demand UAC process)?
- Ship Scoop/Chocolatey backends, or winget only?

---

## Appendix A — Additional English strings

Feature hub descriptions: Cleaner "Clear caches and junk files"; Uninstaller "Remove apps and their leftovers"; Homebrew "Keep Homebrew packages up to date" (enable caption "Search, install and remove formulae and casks."); App updates "Find and install updates for the apps you have"; Port Manager "View active listening ports and, with Kill Process installed, terminate the processes using them"; Kill Process "Search running processes and force quit, restart, or kill process trees" (page subtitle "Browse & Kill"); WhatsApp downloads "Keeps WhatsApp files in Downloads under control"; Disk image installer "Install the single app inside a disk image and clean up the download"; Volume mixer "Per-app volume, pinning and custom order"; Output switcher "Cycle sound outputs with a shortcut"; Audio device priority "Automatically use your preferred audio devices"; Mute microphone "Mute the microphone from anywhere"; Music app blocker "Block music launches from detected media keys".

Permission explanations (macOS hub): Full Disk Access "Lets the cleaner and the uninstaller find leftover files everywhere."; Files & Folders "Lets WhatsApp downloads cleanup and the experimental organizer inspect your Downloads folder."; App audio "Lets the mixer adjust each app’s volume, the Dynamic Island bars follow the music and screen recordings include the Mac’s sound."; Finder automation "Lets the app ask Finder to move files for you."; Terminal automation "Lets Homebrew commands open in Terminal."; App management "Lets updates replace or remove apps installed by the package manager."; Notifications "Lets the app notify you about alerts you turned on."

Panel row captions: Cleaner "App leftovers, caches and logs"; App updates "See which apps have a newer version"; Uninstaller menu item "Uninstall an app…".

Uninstaller onboarding steps: "Drag an app onto Settings, or pick one from the list." / "Review the files found and how much space they take." / "Move what you want to the Trash. Nothing is deleted permanently." (Windows must reword the last step for apps.) Full Disk Access note: "Grant Full Disk Access for a more thorough scan." + "Turn Vorssaint on in the list. If it isn’t there, click + and pick Vorssaint from Applications. Access only applies after you reopen the app." + "Grant access…" / "Relaunch now".

Kill Process failure (reserved): "Couldn’t Kill Process" / "The process may have already exited or require additional privileges."

Mixer misc: "Apps that use audio show up here"; "Output unavailable"; "Default"; "current"; "No outputs found"; "Choose output"; "Choose microphone"; "No microphones found"; "Hold Command and drag to reorder" (row tooltip); island-only: "Pin to Front", "Move Left", "Move Right".
