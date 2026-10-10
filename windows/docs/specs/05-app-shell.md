# 05 — App shell and cross-cutting systems (Windows port spec)

Source of truth: `vorssaint-utils` at commit `b6d8d2db` (main, 2026-10-08), app version **3.4.1-beta.3**, build 98.
This spec is stack-neutral. You should be able to implement it on Windows without reading the Swift code.

**Conventions**

- **Units.** Sizes are macOS points, which equal Windows DIPs (effective pixels at 100 % scaling) unless a value says otherwise. Font sizes are in pt and are treated as DIP.
- **Glossary.** "Tray icon" means the macOS status item (menu bar icon). "Flyout" or "panel" means the popover that opens from it. "Settings" means the Settings window. "Hub" means the Features page in Settings.
- **Feature.** An `AppFeature`, the unit the hub installs and uninstalls. There are 77.
- **Enable key and availability key.** A feature's *enable key* is its own on/off preference. Its *availability key* is `featureAvailable.<id>`, the install state.
- **Settings keys.** Keys appear as the raw strings stored in macOS `UserDefaults`.
- **Quoted UI text.** English strings are quoted for orientation. The catalog uses typographic apostrophes and quotes (’ “ ”), which this spec sometimes writes as straight ones; copy UI text from `data/i18n/en-US.json`, not from this spec.
- **Screenshots.** Several screenshots in `docs/assets/readme` predate the code. **This spec describes the code, not the screenshots.**
  - `utilities-section.png` shows an old header (app icon, "Mac awake" chip) and a "List" footer button.
  - `features-hub.png` shows Uninstall buttons instead of switches, and an older sidebar grouping with 37 features.
  - `panel-utilities.png` and `panel-controls.png` show one card per row with captions always visible, and an old tile order. The current code groups rows in one card with hairline separators and hides captions behind tooltips. That change landed in commit 9e505896 (2026-09-28, #2339); the screenshots were last updated in 468c460c (2026-07-11, 3.1.12).
- **Companion data.** `05-app-shell-assets/` was generated from this revision with a verified prototype (see §5):
  - `tools/run.sh`, `tools/generate.py`, `tools/flatten.py`, `tools/Stubs.swift`, `tools/settings_schema_main.swift`
  - `data/strings.json`: all 74 catalogs × 15 languages
  - `data/i18n/<lang>.json` and `data/i18n/_meta.json`: flattened per-language files and per-key metadata
  - `data/settings_schema.json`: all registered defaults with their types and values, the availability defaults and the backup key sets

---

## 1. Overview

Vorssaint is a **single-process, menu-bar-only ("accessory") utility hub**. It has no Dock icon. It becomes a regular app with a Dock icon and a ⌘Tab entry only while its Settings window is open. Every capability is a *feature* behind an availability flag. An uninstalled feature never instantiates its service at launch and disappears from every surface: the flyout, Settings, the tray, shortcuts and the Command Bar. Its settings are kept so it comes back exactly as it was.

The shell owns these pieces:

| Area | macOS implementation | Windows target |
|---|---|---|
| Process lifecycle | `main.swift` CLI modes → `Defaults.register()` (migrations + registered defaults) → `NSApplication` + `AppDelegate` | single-instance process, tray-only by default |
| Tray presence | one `NSStatusItem`, plus optional per-metric items and an optional clipboard-preview item | notification-area icon(s) |
| Flyout panel | `NSPopover` hosting `MenuPanelView`: 332 pt wide, 12 tabbed sections, update banner, footer | borderless top-most flyout near the tray |
| Right-click menu | `NSMenu` built per click | tray context menu |
| Settings window | `NavigationSplitView`: sidebar (search, grouped tool rows), 35 pages, back/forward history | normal resizable window |
| Features hub | install/uninstall per feature, presets, "never turned on" batch, permissions portal | same |
| Onboarding and intros | 4-step first-run window; post-update intro chain; floating permission guide | same (simplified permissions) |
| Global shortcuts | Carbon hot keys + event taps; 29 roles + 41 window-layout actions | `RegisterHotKey` + low-level hooks |
| Localization | compile-time Swift catalogs; 15 languages; live switching | JSON resources (extracted, §5) |
| Settings store | `UserDefaults`: 825 keys, 739 registered defaults, 77 availability flags | JSON file in `%APPDATA%` |
| Backup | XML plist export/import with allow-list and sanitizers, then relaunch | JSON export/import |
| Self-update | GitHub Releases API → DMG → detached installer script (signature-verified) → relaunch | GitHub Releases → signed installer |
| Feedback | HTTPS POST to `screenshots.vorssaint.com/v1/feedback` | same contract (see risk §10) |
| Appearance | System/Light/Dark app override; optional Liquid Glass; tray follows the system | Light/Dark/System; Mica/Acrylic |
| Diagnostics | `--selftest`, `--sensors`, `--uninstall`; `os_log` category `menubar` | equivalents |
| Build and release | `build.sh` (direct `swiftc`), GitHub Actions: CI, signed and notarized release, immutable GitHub release | new Windows job(s) |

Core singletons, and what they map to on Windows:

- `L10n`: the current language plus the string catalog. Maps to a localization service.
- `FeatureRuntime`: availability changes and per-feature "bindings". Maps to a feature registry.
- `SettingsRouter`: Settings navigation and history. Maps to a navigation service.
- `UpdateService`: maps to an updater.
- `Permissions`: TCC permission state. On Windows this becomes mostly a capability/privacy probe.
- `AppAppearanceController`: maps to a theme service.
- `PanelInteractionState` and `MenuPanelFocus`: shared hints between the panel content and its host window. These map to the flyout controller.

---

## 2. Inventory (checklist)

**Process and lifecycle**
- [ ] Start as a tray-only app; no taskbar button until Settings opens.
- [ ] CLI modes: `--selftest`, `--sensors`, `--uninstall`. The two `--…Guard` cleanup sub-modes are feature-specific and out of scope here.
- [ ] Startup order (§3.1), including the "previous start crashed" guard (skip optional windows).
- [ ] Clean-install feature set: Essentials preset applied before any feature binding runs.
- [ ] Launch-at-login self-repair at startup.
- [ ] Ordered graceful shutdown that restores system state.
- [ ] Relaunch helper: waits for the PID to exit, then reopens.
- [ ] Re-launching the executable while it is running brings up the flyout (or Settings if the icon is not visible).

**Tray**
- [ ] Main icon with states: idle mark or user-chosen symbol; keep-awake active (style + tint); update available (blue); mic-muted badge.
- [ ] Tooltip text per state.
- [ ] Left click toggles the flyout. Right click opens the context menu, or toggles Keep Awake when that option is on.
- [ ] Optional extra items: one per pinned metric ("separate metrics"), and a clipboard-preview item.
- [ ] Text next to the icon (keep-awake countdown + metrics). This needs a Windows redesign (§10).
- [ ] Recovery: "Show menu bar icon" button, post-update visibility check, guidance alerts.

**Flyout panel**
- [ ] 332-wide panel, height capped to the work area, content-sized, scrolls inside.
- [ ] Layout: update banner, header (brand mark; Beta pill and feedback button on beta builds), icon tab bar, one section, footer (Settings, Quit).
- [ ] 12 sections, each gated by features and a visibility key; order and visibility editable from Settings.
- [ ] In-section edit mode (reorder, hide/show, reset) for Utilities, Controls, Toggles, System, Network, Disk, Power.
- [ ] Utilities: 18 tiles. Each either hosts a mini-tool in place or launches an overlay tool and closes the panel.
- [ ] Controls: 23 switch rows in 3 collapsible categories, with an `n/m` counter.
- [ ] Metric-detail mode, opened from a metric tray item.
- [ ] Dismissal policy (outside click, Esc, toggling icon) with dismissal-prevention rules, and activation hand-back to the previous app.
- [ ] Opening Settings from the panel keeps the panel open and places Settings beside it.

**Settings window**
- [ ] Minimum content size 772×528. Default height 838, capped to the screen. Size persisted.
- [ ] Sidebar: search field plus grouped, collapsible categories, with one row per tool.
- [ ] Search: grouped results, keyboard navigation, routing (uninstalled features go to the hub).
- [ ] Back/forward history: toolbar buttons, mouse side buttons, ⌘[ and ⌘].
- [ ] Deep links to a page plus a section anchor, with a landing highlight.
- [ ] Pages: General, Menu bar (panel layout editor + icon chooser), Keyboard shortcuts, Advanced (backup, reset permissions, uninstall), About (+ Updates), What's New, Support. Feature pages are covered by other specs.

**Features hub**
- [ ] Per-feature install switch. A hardware gate refuses unsupported installs and never revokes an existing one.
- [ ] Install all / Uninstall all, with a tally bar.
- [ ] 3 presets (bundles), with a confirmation dialog.
- [ ] "Never turned on" batch offer, with Undo and Keep.
- [ ] Dynamic Island card and extensions (macOS-only concept; see the island spec).
- [ ] Restart banner (unload features uninstalled this session).
- [ ] Permissions tab (portal).
- [ ] Reveal-and-highlight a feature row when routed from search or the Command Bar.

**Cross-cutting**
- [ ] Global shortcut registry, recorder, conflict detection, suspension while recording.
- [ ] Localization: 15 languages, live switching, a formatting locale that respects the system region and clock.
- [ ] Settings store, migrations, sanitizers, change notifications.
- [ ] Backup export/import with an allow-list, a type check, sanitizers and relaunch.
- [ ] Updater: channels (stable/beta), scheduling, preview window, download, verify, install, relaunch, failure reporting.
- [ ] Feedback window and service.
- [ ] Appearance: System/Light/Dark, glass toggles, high-contrast borders, reduce motion and transparency.
- [ ] Notifications (toasts).
- [ ] Onboarding (4 steps) and the post-update intro chain.
- [ ] Self-test and diagnostics; release pipeline.

---

## 3. Detailed behavior per subsystem

### 3.1 Process lifecycle

**Pre-UI (`main.swift`)**

1. The Super-key cleanup sub-mode (`--super-key-mapping-cleanup`) runs. When the process was started as that helper, it performs the cleanup and exits.
2. `Defaults.register()`:
   - First it runs pre-registration migrations, which must see only explicitly saved values.
   - Then it registers 739 defaults and 77 availability defaults.
   - Then it runs post-registration migrations and one-time markers (§7.2).
3. The mouse-acceleration cleanup sub-mode (`--mouse-acceleration-cleanup`) runs the same way.
4. Mouse-acceleration values that a crashed run left changed are restored.
5. CLI flags: `--selftest`, `--sensors` and `--uninstall` each run and exit (§3.14).
6. It creates the app object and the delegate, then enters the run loop.

**Guard helpers** (the pattern behind the two sub-modes; worth copying for any feature that changes system state)
- While the feature is active, the app starts a small `/bin/sh` child that blocks reading a pipe the app owns.
- When the pipe reaches end-of-file (normal exit, crash or SIGKILL), the child execs the app with the cleanup flag, which restores the system setting.
- Stopping the feature closes the pipe, waits up to 6 s, then terminates the child, then sends SIGKILL.
- **Windows:** use a watchdog child process that waits on the parent's process handle, or a Job Object, and restores the setting when the parent dies.

**`applicationDidFinishLaunching` (in order)**

1. Set the activation policy to *accessory* (no Dock icon).
2. Apply the appearance override before any window exists.
3. Start observing keyboard-layout changes. This refreshes the shortcut key-cap label cache.
4. Make the app the notification-center delegate, if it runs with a bundle id.
5. **Startup watch.** Read the `startupDidNotFinish` flag. If it is still set, the previous run died during startup, so skip the optional startup windows this time. Then set the flag again. It is cleared after **20 s** of healthy running or on a clean quit.
6. Bound the cross-process accessibility query timeout to 0.35 s (macOS-specific).
7. `BundleMigration` (rename of legacy bundles; macOS-only, not applicable).
8. `FeaturePreset.prepareFirstRunAvailability()`. On every launch while `hasOnboarded` is false and `onboardingStep == 0` (a clean install that has not chosen yet), it explicitly writes all 77 `featureAvailable.*` keys: true for the **Essentials** preset members, false for the rest.
9. `LaunchAtLogin.repairAtStartup()`, off the main thread (§3.12).
10. Restore any display a previous run left switched off (brightness feature).
11. Install the app main menu (§3.5.15). Install horizontal wheel support. Run the one-time reset of legacy collapsed panel sections.
12. Create the tray controller and wire its click handlers (§3.3).
13. Create the flyout and register it with the appearance controller. Bind keep-awake "session ended" notifications.
14. System-shortcut takeover crash recovery (§6.5). Register the Keep Awake hotkey.
15. Run crash recoveries for keep-awake, fan control and Spaces. When the keep-awake recovery finishes, it starts a session for the default duration if Keep Awake is installed and `keepAwakeAutoStart` is on.
16. `FeatureRuntime.syncAtLaunch()`. Each **available** feature's binding runs; unavailable ones never instantiate.
17. Start update checks (§8.1). Observe app activation: an update check if stale, the app-updates return path, and a launch-at-login refresh in Settings.
18. Permission observers re-sync input-dependent features when Accessibility or Screen Recording changes.
19. Language observer: rebuild the main menu titles.
20. Next run-loop turn, so the tray icon is visible first:
    - If not onboarded (and the previous start completed), show onboarding.
    - Otherwise:
      1. Read the previous `lastUpdateIntroVersion`.
      2. Queue the brightness prompt if that value marks an upgrade (§3.7.3).
      3. Write `featuresOnboardingVersion = 4` and `lastUpdateIntroVersion = <version>`.
      4. Unless the previous start crashed, run the post-update tray visibility check (driven by the previous version) and the **intro chain** (§3.7.3).

**Quit**

- `applicationShouldTerminate`: if the Command Bar borrowed the keyboard input source, the quit is delayed until it is restored.
- `applicationWillTerminate` stops or restores, in this order:
  - Command Bar input source; Dynamic Island.
  - End the startup watch. Restore displays and extra brightness.
  - Network monitor, URL cleaner, focus-follows-mouse, window maximizer, window layout.
  - Keyboard and mouse debounce, snippets, Super key (unmap), switcher (restore Dock hotkeys).
  - Mouse buttons, middle click, scroll inverter, smooth scroll, mouse acceleration (restore), mouse navigation.
  - Dock preview, sound output switcher, precise volume, per-app volume taps, fan control (restore), mic override, scratchpad flush.
  - **Restore every taken-over system shortcut.** Flush the clipboard history. Deactivate keep-awake.
- **Windows:** perform the same restores on `WM_QUERYENDSESSION` and `WM_ENDSESSION` (log off or shut down) and on console Ctrl events. Install crash-safe markers (as the macOS app does for shortcuts, fans and mouse acceleration) so the next launch can undo anything left changed.

**Relaunch** (used after a backup import, by the hub's "Restart now", after Full Disk Access, by the permission guide's "Relaunch to apply" button and by the Command Bar's "Restart Vorssaint" action)

- A detached shell helper loops `kill -0 <pid>` every 0.2 s, for at most 100 tries (20 s). Once the process is gone it runs `open <bundle>`. The app terminates only after the helper started successfully.
- **Windows:** spawn a detached helper process, or the app itself with `--relaunch-after <pid>`, that waits on the process handle and then starts the exe.

### 3.2 Single instance and "reopen"

- Opening the app while it runs (Finder, Spotlight, Launchpad, Terminal `open`) sends a reopen event. This is the recovery path when the tray icon is missing.
  - Reopens that Siri, Shortcuts or system daemons send (Apple bundle ids containing `siri`, `shortcut` or `workflow`, or non-app executables under `/System/`, `/usr/libexec/`, `/usr/sbin/`, `/Library/Apple/`) are ignored.
  - If no app window is visible, it rebuilds the status item only when it is genuinely missing and not hidden by the user's own option.
  - On the next turn it opens the panel if the icon is on screen; otherwise it opens Settings. **The user always gets back in.**
- **Windows:**
  - Use a named mutex or single-instance lock. A second launch forwards a "show" message over a named pipe or `WM_COPYDATA`.
  - The running instance shows the flyout. If the icon is hidden in overflow, or Explorer is not running, it shows Settings instead.
  - Optionally accept arguments such as `--settings`, `--settings=<page>` or `--panel=<section>` for deep links.

### 3.3 Tray icon (status item)

#### 3.3.1 Items

| Item | When present | Click | Notes |
|---|---|---|---|
| Main item | always, except when hidden by the user's options (below) | left: toggle panel | image + optional text |
| Metric items | `menuBarSeparateMetrics` = true, one per active metric group. CPU+temp, GPU+temp and Battery+temp combine when `menuBarCombineTemperatures` is on and the metric appearance is "values" ("bars" never combines) | left: panel in metric-detail mode, anchored under that item; same item again closes | text-only; removed after 5 consecutive empty renders; tooltip = metric title |
| Clipboard preview item | Clipboard History installed and enabled, `clipboardHistoryMenuBarPreview` on, history running | left: toggle the clipboard history window | shows the latest copied entry truncated to `clipboardHistoryMenuBarPreviewLength` (5–50, default 20); hidden while there is no entry |

- **Right click on any item** opens the context menu, or toggles Keep Awake when Keep Awake is installed and `keepAwakeRightClickToggle` is on.
- Clicks fire on **button release**. Only a right-button release counts as a right click, so Ctrl-click acts as a left click.
- Each item has a stable autosave identity: `VorssaintMenuBarItem[.<generation>]`, `VorssaintMetric.<id>` and `VorssaintClipboardPreview`. This lets macOS remember its position.

#### 3.3.2 Main icon image

Precedence is evaluated on every refresh, but the image is only re-rendered when the state key changes:

1. `updateAvailable` is true when the update state is `available`.
2. `micBadge` is true when the microphone is muted and `micMuteMenuBarIndicator` is on (default true).
   - `signal = updateAvailable || micBadge`.
   - `keepAwakeSignal = keepAwake active && (tint ≠ none || style ≠ vorssaint)`.
   - The badge and the keep-awake signal are **frozen while the panel is open**. A width change would move the panel's anchor.
3. **Glyph hidden** when either:
   - the island replaces the icon (`notchHidesMenuBarIcon`, the island is on and not hidden in fullscreen, and there is no signal), or
   - all of the following hold: `menuBarHideIconWithMetrics`, not separate metrics, metrics enabled, a non-empty title, and no signal or keep-awake signal.
4. **Whole main item hidden** when either:
   - the island hides it and the item has no title, or
   - all of the following hold: `menuBarHideIconWithMetrics`, separate metrics, at least one metric item actually rendering, an empty title, and neither a signal nor a keep-awake signal.
5. Image selection:
   - update → the idle icon (the custom symbol if one is set, else the mark) tinted **systemBlue**;
   - otherwise keep-awake active → the active style;
   - otherwise → the idle icon.
6. When `micBadge` is set, composite: base image + 2 pt gap + red `mic.slash.fill` (12 pt semibold). The composite is not a template.

**Idle mark**

- A template (monochrome, auto light/dark) image on a fixed 26×20 pt canvas (2× asset 52×40 px), generated from `Resources/Brand/logo.png` by `Tools/MakeIcon.swift`. The trimmed mark is drawn 12.5 pt tall (about 24.6×12.5 pt), centred, and dropped 1 pt below centre so it sits on the same visual floor as neighbouring icons.
- `menuBarIconSymbol` can replace it with any SF Symbol, chosen from a 40-item gallery or typed by name (§3.5.9). The symbol is rendered ink-centred in the same 26×20 canvas at 16 pt ink height.
- An unknown name falls back to the mark. A missing asset falls back to `circle.fill`, then to a drawn dot. The icon is never empty.

**Keep-awake active styles** (`keepAwakeActiveIcon`). Symbol styles are drawn as 15 pt semibold symbols scaled to a 16 pt ink height, at most 24 pt wide, in the same 26×20 canvas.

| Style | Image | Note |
|---|---|---|
| `vorssaint` (default) | the idle icon (custom symbol if set, else the mark) | |
| `coffee` | `cup.and.saucer.fill` | 1 pt drop |
| `eye` | `eye.fill` | |
| `moon` | `moon.fill` | |
| `light` | `lightbulb.fill` | 0.5 pt drop |

Tint (`keepAwakeIconTint`): `orange` (default, systemOrange), `green`, `blue`, `purple`, `pink`, or `none` (stays a template).

**Windows**

- Render the tray icon at runtime: 16×16 logical, with bitmaps at 16/20/24/32 px for 100/125/150/200 % scaling, and 48 for high DPI.
- Template images must follow the **taskbar** theme (`SystemUsesLightTheme`), not the app theme. Re-render on `WM_SETTINGCHANGE` ("ImmersiveColorSet") and on DPI change.
- The brand mark is ~1.97:1 wide; at 16 px it needs a dedicated square tray variant (§10).

#### 3.3.3 Text next to the icon (title)

The title is composed in this order:

1. **Countdown.** Shown when keep-awake is active and `showCountdownInMenuBar` (default false) is on. A timed session shows `H:MM` when at least an hour remains, otherwise `N min` (minimum 1). An indefinite session shows `∞`. Remaining time is max(0, end − now), truncated to whole minutes. These formats are hard-coded, not localized.
2. **Metrics** (only when they are *not* separate items). Two spaces separate the countdown from the metrics. Metric blocks are separated by a hair space (U+200A, "compact" spacing, the default) or one space ("standard" spacing).
3. One leading space precedes the whole title unless the glyph is hidden.

- Font: system monospaced 11.6 pt medium. The single-line title has no explicit line height.
- Unchanged title, tooltip and item length are not rewritten.
- Metric blocks are images. Typical block: a 6.6 pt label over a 12 pt semibold monospaced-digit value. There are also usage bars, two-line rate blocks and a battery block. See the Monitor spec.
- A 30 s timer runs only for a timed countdown.
- **Windows:** the notification area cannot show text. See §10 for options.

#### 3.3.4 Tooltip

| State | Tooltip |
|---|---|
| Idle | "Vorssaint: normal sleep" |
| Active with end time | "Vorssaint: awake until" + the end time in the short time style |
| Indefinite | "Vorssaint: awake indefinitely" |
| Automation session | the automation status text (Keep Awake spec), even when the session has an end date |

#### 3.3.5 Context menu (right click)

If the panel is open, it closes first and the menu opens after. The menu is built at click time:

1. "Enable keep awake" / "Disable keep awake" (if Keep Awake is installed).
2. "Activate for…" submenu, only when inactive: 15 minutes, 30 minutes, 1 hour, 2 hours, 4 hours, 8 hours, Indefinitely.
3. "Cleaning Mode" (if installed).
4. Separator, only if any of the above exist.
5. "Settings…" (⌘,).
6. "About Vorssaint": the standard About panel with credits = `aboutDescription`.
7. "Uninstall an app…" (if Uninstaller is installed): opens Settings → Uninstaller.
8. "Open shelf" (if Shelf is installed and enabled).
9. "Check for updates…": runs a manual check and opens Settings.
10. Separator.
11. "Quit Vorssaint" (⌘Q).

When the main item is hidden, the menu opens from the clicked item (metric or clipboard item).

#### 3.3.6 Refresh triggers

- Keep-awake active or end-date changes; update state changes; mute state changes; language changes; monitor snapshot ticks (only if any metric is pinned; default interval 2 s, allowed 1/2/5); island fullscreen visibility; clipboard latest entry.
- **Any settings change**, coalesced to one refresh on the next run-loop turn.
- A refresh is guarded against re-entry: requests made during a refresh produce one follow-up refresh on the next turn.

#### 3.3.7 Recovery (macOS-specific, with Windows equivalents)

**"Show menu bar icon" button** (Settings → Menu bar)

1. Clears both hide options.
2. Rebuilds the item while keeping its position.
3. Checks placement 6 times at 0.8 s intervals, plus up to 8 settling checks.
4. If it is still hidden and the system records Vorssaint as not allowed in the menu bar, shows an alert titled "The icon is still hidden" with "macOS is keeping Vorssaint out of the menu bar…", and **stops** without resetting.
5. Otherwise resets the position identity (`statusItemPlacementGeneration` + 1, clearing saved keys) and repeats the checks.
6. If it is still hidden, shows the same alert title with "The icon was rebuilt, but macOS did not give it a visible spot…", plus a hint naming a running menu bar organizer (Ice, Bartender, Hidden Bar or Dozer) if one is running.

Guards: repeated presses during a recovery only clear the options. The recovery is cancelled if a hide option is turned back on. The app activates before each alert.

**First launch of a newer version** (semver-newer, not a developer build):
- 12 checks at 0.8 s (about 9.6 s). If the icon is still off screen, recreate the item once (same identity) and check 12 more times. A 30 s deadline caps everything.
- It stops silently when any of these hold: the panel or a menu is open, or the item is invisible; a mouse button is down; the screens changed; `menuBarHideIconWithMetrics` is on; the menu bar is hidden, auto-hidden or in full screen; the session is not on the console, or is locked; a menu bar organizer is running.

**Windows equivalents**
- Re-add the icon on the `TaskbarCreated` registered message (Explorer restart).
- New icons land in the overflow ("hidden icons") area. On first run, explain how to pin the icon to the taskbar, with an illustration.
- The "Show tray icon" button re-adds the icon and opens Settings → Personalization → Taskbar → Other system tray icons (`ms-settings:taskbar`).
- Use a stable `guidItem` in `NOTIFYICONDATA` so the pinning survives updates. The GUID is bound to the exe path for unsigned apps, so keep the install path stable.

### 3.4 Flyout panel

#### 3.4.1 Window behavior

**Show and toggle**
- A left click toggles the panel.
- A click within **0.35 s** after a close is ignored. This prevents the click that dismissed the panel from reopening it.
- If the Dynamic Island hosts the panel and is accepting feedback, the click is routed to the island instead.

**Activation**
- On open, the app activates so panel controls receive keys. The previously frontmost app is remembered.
- While the panel is open, another app becoming active replaces the remembered app. Vorssaint becoming active keeps it. A desktop (Space) switch drops it.

**Close reasons** decide whether activation is handed back:

| Reason | Hands activation back? |
|---|---|
| `escape` | yes |
| `statusItem` | yes |
| `outsideClick` | no |
| `action` | no |

- The hand-back only happens if Vorssaint is still frontmost, none of its own windows is key, the remembered app is still running, and the panel was not shown again.
- It does not happen if activating the app would switch desktops.
- It runs one run-loop turn after the close animation.
- **Precedence.** Once `outsideClick` or `action` is set for a close, later requests cannot replace it; `escape` and `statusItem` can be replaced. A close that the system starts on its own has no reason, so it never hands activation back. The reason resets on every show.

**Dismissal monitors** (the panel does not auto-close on deactivation)
- A mouse down in another app closes the panel (reason `outsideClick`). Exceptions: a dismissal-prevention rule is active (§3.4.14), or the click is on one of the app's own tray items.
- A mouse down inside the app's Settings window closes the panel only if the Settings frame intersects the panel frame. Side by side, both stay open, so Settings edits preview live. Clicks in any other app window never dismiss it.
- Opening the Feedback, Onboarding, update highlights, showcase, support intro or update preview window closes the panel first.
- **Esc** while the panel is the key window closes it, unless an IME composition is active.
- **Space, Return or keypad Enter** (without ⌘, ⌃ or ⌥) are forwarded to the focused control while a view holds the panel open, unless a text field is editing.

**Placement**
- The arrow points at the clicked tray item.
- Normally the system centres the panel under the item, and the app adds no margin.
- When the item's frame cannot be trusted, the app pins the panel to the anchor captured at open, centred on it and clamped to the work area with an **8 pt** margin. The frame is untrusted when it has zero size, its middle is outside the top 48 pt band of a screen, or a click from the last 0.5 s landed more than half the button width + 24 pt away from it.
- When content height changes, the **top edge is held** and the panel grows downward.
- The panel joins all Spaces and stays above full-screen apps.
- The anchor screen is the screen of the click; failing that, the item's window; failing that, the menu-bar screen.

**Mode and anchor switching**
- Opening from the main icon always resets metric mode to the normal panel.
- Clicking another metric item while the panel is open refocuses it and re-anchors after 0.16 s. If the panel centre is more than 34 pt off, it closes and reopens 0.03 s later, without animation or re-activation.
- Metric clicks go to the island when the island hosts the panel and shows its System module.

**Height**
- `maxHeight = max(360, anchorScreen.workArea.height − 28)`.
- `panelHeight = clamp(contentHeight + chrome, 220, maxHeight)`.
- `chrome = 180`, plus `max(bannerHeight, 48) + 12` while the update banner shows.
- Content height is measured on every layout pass, including during expand/collapse animations, with a 0.5 pt hysteresis.
- Until a measurement arrives, these estimates are used:

| Section or metric | Estimate (pt) |
|---|---|
| keepAwake | 250 |
| brightness | 140 |
| mixer | 250 |
| system | 460 |
| network | 190 |
| disk | 360 |
| power | 170 |
| fanControl | 220 |
| utilities | 500 |
| controls | 360 |
| toggles | 420 |
| wallpaper | 480 |
| metric cpu, gpu, memory | 430 |
| metric network | 330 |
| metric disk | 360 |
| metric battery, power | 360 |
| metric fan, connected devices | 240 |

- Content scrolls with an **overlay scrollbar** that never reserves a gutter.

**Width:** fixed 332, with 308 for content.

**On show**
- Broadcast "panel will show". Sections recompute what the system monitor must sample: only the active section (System, Network, Disk or Power) or the selected metric.
- Check for updates if the last check is older than 15 min. The check is skipped when automatic checks are off, in developer builds, while checking, downloading or installing, and during a metric re-anchor.

**On close**
- Stop panel-driven sampling and clear the metric focus.
- Let per-app network monitoring wind down, and clear the process row and icon caches.
- Reset the keep-open and modal flags.

**Settings interaction:** opening Settings from the panel does **not** close it. Settings is positioned to avoid the panel (§3.5.1). Only when Settings cannot be placed beside, below or above the panel does the panel close.

**Windows notes**
- Use a borderless, top-most tool window (`WS_EX_TOOLWINDOW`) so it has no taskbar button and no Alt+Tab entry.
- Position it from `Shell_NotifyIconGetRect`, or the cursor position at click time. Place it above the taskbar, or below or beside it when the taskbar is on another edge (`SHAppBarMessage(ABM_GETTASKBARPOS)`).
- Dismiss on `WM_ACTIVATE(WA_INACTIVE)` unless a prevention rule is active.
- Windows 11 flyouts have **no arrow**. Optionally keep the arrow-less, rounded style.
- Handle the case where clicking the tray icon deactivates the flyout first. This is what the 0.35 s guard exists for.

#### 3.4.2 Layout

**Navigable mode** is the default. It is a vertical stack with spacing 12, padding 12 and width 332:

1. **Update banner.** Only while the update state is available, downloading or installing (§3.4.12).
2. **Header**, 28 tall plus 4 pt vertical padding:
   - The centred brand mark, 48 wide and 28 tall. Its tint is near-black `#080808` in light mode and white in dark mode.
   - On beta builds only: a "BETA" pill on the left (9 pt bold, orange text on 18 % orange) and a feedback icon button on the right (`bubble.left.and.text.bubble.right`, 11 pt, tooltip "Send feedback").
3. **Tab bar.**
   - One icon button per visible section, with spacing 2, inside a container with padding 4, card fill, radius 12 and a 0.7 border.
   - Each tab is a 13.5 pt semibold icon, 30 tall, with flexible width.
   - Active tab: accent foreground, accent fill at 13 % (light) or 20 % (dark), radius 8.
   - Inactive tab: secondary colour at 86 %.
   - Tooltip and accessibility label: the section title, plus a "selected" trait.
4. **Content.** A scroll view 308 wide that shows **exactly one section** (the active tab). The panel has always navigated by section tabs since 3.1.8: the legacy key `panelNavigationEnabled` is always true, and older collapsible sections are no longer used in the panel.
   - Moving keyboard focus onto a tab selects it, so the content follows focus.
   - A deep link `focus(section)` is ignored when that section is not visible; otherwise it selects the tab and moves keyboard focus to it. `focus(metric)` clears tab focus.
5. **Footer**, height 30, top padding 4, two equal buttons with spacing 8:
   - Each is at least 28 tall, radius 7, with card fill and a 0.8 border. Text is 11 pt medium, secondary colour, and scales down to 78 %.
   - **⚙ Settings** opens Settings on the page of the hosted tool, if a tool is hosted in Utilities, otherwise on General.
   - **⏻ Quit** quits.

**Metric mode** is the same stack, with the tab bar replaced by a header row:
- A back chevron button (24×24, radius 7, card fill, border) that returns to the metric's section.
- A label with the metric's icon and title (12.5 pt semibold).
- Content: the metric detail view (Monitor spec).

**Embedded mode** (inside the Dynamic Island):
- Navigation row (38 tall), content (height − 96) and footer.
- Forced dark scheme. Cards use the island surface (radius 18, padding 12). The tab bar background is black.

#### 3.4.3 Section catalog (default order = table order)

| id | Title (EN) | SF Symbol | Visibility key (default true) | Feature gate (any installed) | Extra rule | Editable | Content (owning spec) |
|---|---|---|---|---|---|---|---|
| `keepAwake` | Keep awake | `moon.zzz.fill` | `panelShowKeepAwake` | keepAwake | — | no | Keep Awake card §3.4.10 (Energy) |
| `brightness` | Displays | `display.2` | `panelShowBrightness` | brightness | only while `brightnessControlEnabled` | no | Displays (Energy/Display) |
| `mixer` | Volume mixer | `speaker.wave.2` | `monitorShowMixer` | mixer, audioPriority | full mixer if Mixer is installed, else the priority lists only | no | Sound |
| `system` | System | `cpu` | `monitorShowSystem` | monitorCPU, monitorGPU, monitorMemory, connectedDevices | — | yes | Monitor |
| `network` | Network | `network` | `monitorShowNetwork` | monitorNetwork | — | yes | Monitor |
| `disk` | Disks | `internaldrive` | `monitorShowDisk` | monitorDisk | — | yes | Monitor |
| `power` | Power | `bolt.fill` | `monitorShowPower` | monitorPower | — | yes | Monitor |
| `fanControl` | Fan Control | `fanblades.fill` | `panelShowFanControl` | fanControl | rendered only while the panel is visible | no | Monitor/Fan |
| `utilities` | Utilities | `wrench.and.screwdriver.fill` | `panelShowUtilities` | 18 tool features | — | yes | §3.4.7 |
| `controls` | Controls | `switch.2` | `panelShowControls` | 21 features | — | yes | §3.4.8 |
| `toggles` | Quick toggles | `togglepower` | `panelShowToggles` | quickToggles, micMute | holds the panel open while visible | yes | Tools |
| `wallpaper` | Wallpaper | `photo.on.rectangle` | `panelShowWallpaper` | wallpaper | — | no | Tools |

#### 3.4.4 Ordering and visibility rules

**Order.** Read the saved comma list `panelSectionOrder` and remove duplicates. Then add sections missing from the saved list at their canonical place:

| Missing section | Inserted |
|---|---|
| `disk` | after `network` |
| `controls` | after `utilities` |
| `brightness` | after `keepAwake` |
| any other | appended |

Unknown ids are ignored.

**Visible in panel** = a gating feature is installed, *and* the visibility key is true (an absent key means true), *and* for `brightness`, `brightnessControlEnabled` is true. The live panel and the Settings miniature share this one rule.

**Active tab.** The selected tab if it is visible, otherwise the first visible one. Selection is kept in memory for the process lifetime. A deep link `focus(section)` or `focus(metric)` sets it.

**Editing.** In Settings → Menu bar, the user cannot hide the last visible section (the switch is disabled). Fan Control has no switch there; its visibility is controlled on the Monitor page.

#### 3.4.5 Section header and in-section edit mode

- **Title.** Uppercase, 10 pt semibold, kerning 0.5, secondary colour.
- **Edit button** (editable sections only): a `slider.horizontal.3` icon (11 pt) in a 22×18 hit area (radius 6), secondary colour, tooltip "Edit". While editing it becomes an **"OK"** pill: checkmark plus "OK" (10.5 pt bold), 24 tall, white on the accent colour, radius 8, tooltip "Done!".
  - "OK" is a hard-coded, non-localized literal. Consider localizing it on Windows.
- **Reset button** (editing only): `arrow.counterclockwise` in 22×22, radius 7, 7 % primary fill. Its tooltip reuses the mixer's "Default" string. Reset restores the default order and makes every item visible.
- **Edit mode behavior**
  - Every installed item is listed, including hidden ones, and captions are shown.
  - Each row gets a drag handle (`line.3.horizontal`, 11 pt, 16 wide). Its tooltip reads "Drag to reorder the panel sections and use the eye to show or hide each one."
  - Hidden items are dimmed and carry a "Hidden" badge.
  - An eye button toggles each item (`eye.slash.fill` hides, `eye.fill` shows; 24×22, radius 7, 10 % fill).
  - Drag and drop reorders live: the list moves on drag-enter and persists to the section's order key.
  - Rows become inert:
    - Action rows lose their button, chevron, shortcut hint, permission button and accessory.
    - Toggle rows swap the switch for the Hidden badge and eye, and hide accessories. Their permission button can still show.
    - Controls sub-rows are hidden. Controls categories are forced open and their headers are disabled.
- The edit controls are hidden, and editing is forced off, while Utilities hosts a tool.

#### 3.4.6 Row primitives

- **Row group.** One card (no padding) holds a list of rows separated by hairlines. Separators are inset 41 pt on the left (66 pt with drag handles) and 10 pt on the right. Rows have 10 pt horizontal and 8 pt vertical insets. In the island they use 12 and 10.
- **Utility action row (tile)**
  - Layout, left to right:
    - Optional drag handle.
    - A 15 pt semibold icon in a 22 wide column (gap 9), accent colour. It turns **orange** when attention is needed and secondary when the row is hidden in edit mode.
    - The title, 12 pt semibold, up to 2 lines, with an optional beta pill.
    - A caption, 10 pt, shown only when attention is needed, when "caption stays visible" (live status such as recording time), or in edit mode. **Otherwise the caption is the tooltip.**
    - Trailing: a shortcut-hint pill and a `chevron.right` (9 pt, tertiary). The pill is 9 pt semibold rounded, tertiary colour, with 6 % primary fill, radius 5, padding 5×2.
  - **Click target.**
    - Without a permission button or accessory, the whole row is one button with a hover highlight (6 % primary, radius 7, inset 3).
    - With an accessory, the upper row is a button with no highlight.
    - While a permission button shows, the row itself is **not clickable**; only the permission button (and any accessory) acts. So Screenshot, Screen recording and Copy text from screen cannot be launched until Screen Recording is granted, Cleaning Mode needs Accessibility, and Camera preview stays inert while the camera is denied.
    - In edit mode the row is inert.
  - Rows that need a permission button or have an accessory show the row plus extra buttons below, indented 31:
    - The permission button: "Grant access", `hand.raised.fill`, bordered, mini size.
    - An accessory button, for example "Recent captures" with `clock.arrow.circlepath`.
- **Toggle row.** The same left part, with a trailing small switch.
  - The caption shows only when attention is needed or in edit mode; otherwise it is the tooltip.
  - Optional green "active" label: a checkmark plus text, 9.5 pt.
  - Permission button when attention is needed.
  - Optional accessory button (`arrow.up.forward.square`), for example "Open shelf (3)", shown only while the shelf is on and holds items.
  - The icon uses the accent colour when on, secondary when off, and orange when attention is needed.
- **Attention rule.** A switch that is on but missing a permission shows an orange caption "Permission required: Accessibility" (or the relevant permission) and a "Grant access" button. Switching such a feature on requests the permission at once.

#### 3.4.7 Utilities section (tiles)

Default order (from the enum, migrated so that Screenshot comes first and App updates is inserted): screenshot, quickLauncher, appUpdates, cleaner, homebrew, media, clipboard, windowLayout, uninstaller, cleanURL, cleaning, screenOCR, colorPicker, cameraPreview, scratchpad, commandBar, screenRecorder, portManager.

The persisted order key is `panelUtilityOrder`. Each tile has a visibility key `panelUtility<Name>` (default true). The exception is cleanURL, whose key is `panelUtilityURLCleaner`.

| Tile | Feature | Title (EN) | Icon | Action | Shortcut hint role |
|---|---|---|---|---|---|
| screenshot | screenshot | Screenshot | `camera.viewfinder` | close panel, start capture after 0.2 s. Needs Screen Recording. Accessory "Recent captures" hosts the recent-captures list | screenshot |
| quickLauncher | quickLauncher | Quick panel | `square.grid.2x2` | close, show the quick panel after 0.15 s | quickLauncher |
| appUpdates | appUpdates | App updates | `arrow.down.app` | **hosted** | — |
| cleaner | cleaner | Cleaner | `sparkle` | **hosted** | — |
| homebrew | homebrew | Homebrew | `shippingbox` | **hosted**; does *not* hold the panel open | — |
| media | mediaTools | Media | `photo.on.rectangle.angled` | **hosted** | — |
| clipboard | clipboardHistory | Clipboard | `doc.on.clipboard` | **hosted**. While history is off, the caption stays visible and reads "Enable history to start saving copied text." | clipboard |
| windowLayout | windowLayout | Window layout | `rectangle.3.group` | **hosted** | — |
| uninstaller | uninstaller | Uninstaller | `trash` | **hosted** | — |
| cleanURL | urlCleaner | Clean URL | `link` | **hosted** | — |
| cleaning | cleaningMode | Cleaning Mode | `keyboard` | close panel, then activate. Needs Accessibility: attention state plus Grant access | — |
| screenOCR | screenOCR | Copy text from screen | `text.viewfinder` | close, capture after 0.2 s. Needs Screen Recording | screenOCR |
| colorPicker | colorPicker | Color picker | `eyedropper` | close, pick after 0.15 s | colorPicker |
| cameraPreview | cameraPreview | Camera preview | `web.camera` | close, show after 0.15 s. Camera denied: attention plus "Open System Settings…" | cameraPreview |
| scratchpad | scratchpad | Scratchpad | `note.text` | close, show after 0.15 s | scratchpad |
| commandBar | commandBar | Command Bar | `command` | close, show after 0.15 s | commandBar |
| screenRecorder | screenRecorder | Screen recording; "Stop recording" while recording | `record.circle` / `stop.circle` | close, toggle after 0.2 s. Needs Screen Recording. While recording the caption shows elapsed time and the accessory is hidden | screenRecorder |
| portManager | portManager | Port Manager | `network` | **hosted** | — |

**Hosted tools**
- A hosted tool replaces the tile list in place, with its own back or close control.
- It marks the panel "keep open" (all except Homebrew), so clicks in other apps do not dismiss it.
- It also sets the Settings target page used by the footer: uninstaller → Uninstaller, cleaner → Cleaner, cleanURL → URL cleaner, homebrew → Homebrew, media → Media, clipboard → Clipboard, recent captures → Screenshot, windowLayout → Window layout, appUpdates → App updates, portManager → Port manager.
- **Lifetime.** Switching tabs or opening a metric removes the section, which clears both the keep-open flag and the Settings target. Closing the panel keeps the hosted tool, so reopening the panel shows it again; the tool re-arms keep-open when it reappears (the close handler resets the flag).

**Shortcut hint.** Shown only when the role is available *and* all of its `requiredEnableKeys` are true (§6). It shows the saved shortcut's display string.

#### 3.4.8 Controls section

- **Categories**, collapsed by default. The open state persists in `panelControlWindowsExpanded`, `panelControlInputExpanded` and `panelControlFilesExpanded`.
- **Category header.** A chevron, then the uppercase title (10 pt bold, tracking 0.5, secondary). On the right, an `enabled/total` counter (9.5 pt semibold rounded, monospaced digits): accent colour when greater than 0, otherwise secondary. The expand animation is ease-out 0.15 s.
- **Order.** The persisted order key is `panelControlOrder`. Per-item visibility keys are `panelControl<Name>`.

| Category | Items in default order (item → feature) |
|---|---|
| Windows | switcher → switcher; autoQuit → autoQuit; windowMaximize → windowMaximizer; dockPreview → dockPreview; dockClick / dockClickHide / dockClickCycle → dockClick; notch → notch; spacesOrder → spacesOrder |
| Mouse and keyboard | mouseScroll → scrollInverter; linearScroll; focusFollowsMouse; mouseAcceleration; mouseNavigation; keyDebounce → keyboardDebounce; middleClick; textSnippets; radialMenu; mouseButtonShortcuts; superKey; mouseClickDebounce |
| Files | cutPaste → finderCutPaste; shelf |

Global default enum order is: mouseScroll, linearScroll, focusFollowsMouse, mouseAcceleration, mouseNavigation, switcher, cutPaste, autoQuit, shelf, windowMaximize, dockPreview, keyDebounce, dockClick, dockClickHide, dockClickCycle, middleClick, textSnippets, radialMenu, mouseButtonShortcuts, superKey, mouseClickDebounce, notch, spacesOrder.

Rows are toggle rows. Toggling one calls the service's sync and requests Accessibility if needed. The behavior of each toggle belongs to the feature specs.

**Category rules**
- A category with no rows is omitted.
- The counter totals only the rows currently listed: visible rows normally, all installed rows in edit mode.
- The Mouse buttons row counts as on if either mouse-button shortcuts or the Spaces gesture is on.
- Reset also collapses all three categories.

**Sub-rows** (indented 41 leading, 10 trailing, 8 bottom; hidden in edit mode)
- Under App switcher: a mini switch "Show %@ with large icons", where `%@` is the switcher shortcut. It is disabled in simple mode, and its caption is the tooltip.
- Under Debounce: a stepper for the key window, shown as "N ms".

**Accessories and coupling**
- The Shelf accessory closes the panel and expands the docked shelf.
- The Text snippets, Dynamic Island ("Settings…"), Radial menu, Mouse buttons and Super key accessories open their Settings pages.
- "Click the Dock icon to minimize" and the Dock "hide" option switch each other off.

#### 3.4.9 Quick toggles section

One-click action rows, plus one switch row: keyboard backlight, listed only when its state can be read.
- **Editing.** Reorder, hide and reset via `panelToggleOrder` and the `panelToggle*` keys. Mic mute is the exception: it keeps the legacy key `panelUtilityMicMute`.
- **Keep open.** While the tab is shown it holds the panel open, because these actions activate other apps.
- **Row behavior**
  - A row is disabled while its action runs.
  - Lock screen, display off and screen saver close the panel and run after 0.15 s.
  - When Finder automation consent is declined, Empty Trash shows "Permission required: <Finder automation>" with "Open System Settings…".
  - The hidden-files and desktop-icons rows always show their Finder-restart caption.
  - The dark-mode row's title and icon flip with the current scheme.

The actions themselves belong to the Tools spec.

#### 3.4.10 Keep Awake card (structure only; behavior in the Energy spec)

- **Top row:** a status line and the main switch. With a timed session active, "+15m", "+30m" and "+1h" extend chips sit between the status line and the switch. The switch stays, and the status line hides when it does not fit. Chip labels are "+" plus an OS-abbreviated duration (hour and minute units).
- **Duration chips:** 15m, 30m, 1h, 2h, 4h, 8h, ∞, plus an "Until…" time chip. They fold into 2 rows of 4 when one row does not fit. Clicking the highlighted chip stops the session.
- **Hint line,** always present so the card height never jumps. It is either "Click a chip to start. Click it again to stop" (10 pt, tertiary) or an orange battery-protection note with a `battery.25percent` icon. This text comes from an inline per-language function; see §5.
- **"Options" disclosure:**
  - Active icon picker (style and tint).
  - Allow display sleep.
  - Start Keep Awake at launch.
  - An Automation sub-disclosure (external display / power / running apps / pause when locked).
  - Mouse jiggle, with an interval of 1/2/5/10/15 min shown as "N min" (a hard-coded unit).
- **Divider,** then the "Keep going with the lid closed" row (macOS clamshell; not applicable on Windows).

#### 3.4.11 Metric detail mode

Opened by clicking a separate metric tray item, or from System → Connected devices. The kinds are cpu, gpu, memory, network, disk, battery, power, fan and connectedDevices. Each maps to its panel section for "back". Clicking the same metric item again closes the panel. Clicking another re-anchors the panel under that item without animation.

#### 3.4.12 Update banner

**`available(version)`**
- One button-sized card: accent fill, or **orange** for prerelease versions, radius 10, padding 12×9.
- Contents: a white `arrow.down.circle.fill` (16), then "Update available" (12 pt semibold white) over "Version X" (10.5 pt, white at 85 %).
- Right side: a white capsule with "Update" (11 pt semibold, in the tint colour).
- Clicking closes the panel, then opens the **Update preview window** (§8.1).

**`downloading(progress)`**
- A row with 6 % primary fill and radius 10: "Downloading update…" (11.5 pt medium), then a progress bar and a percentage (10.5 pt, monospaced digits).
- While the size is unknown, a small spinner precedes the text, with no bar and no percentage.

**`installing`:** a spinner with "Installing and restarting…".

#### 3.4.13 Header and footer

See §3.4.2.

#### 3.4.14 Dismissal-prevention rules (`preventsPopoverDismissal`)

The panel ignores outside clicks while any of these hold:

- a view holds the panel open (`viewKeepsPopoverOpen`): any hosted Utilities tool except Homebrew, or the Quick toggles tab while it is shown;
- an alert or confirmation is presented from the panel;
- the AirPlay picker is open;
- a Homebrew operation is active;
- the Cleaner is scanning or cleaning;
- the Uninstaller is scanning or removing.

The tray icon click, Esc and app actions still close it.

#### 3.4.15 Keyboard

- Esc closes the panel, but only when the key event targets the panel window and no input method is composing. Esc in Settings, or in a popover opened from the panel, stays there.
- Tabs are focusable buttons; arrow keys follow standard focus behavior. Focusing a tab selects it.
- While a view holds the panel open, Space, Return and keypad Enter (without ⌘, ⌃ or ⌥) go to the focused control, unless a text field is editing. Otherwise keys follow normal handling (§3.4.1).

### 3.5 Settings window

#### 3.5.1 Window

- **Title:** "Vorssaint Settings". Style: titled, closable, minimizable, resizable. It is not restorable across launches and does not hide when the app deactivates.
- **Size**
  - Minimum content size is **772×528**.
  - Initial size is the saved `settingsWindowWidth`/`settingsWindowHeight` if both are valid (at least the minimum). Otherwise the content size is 772 × min(838, max(528, target work-area height − 40)).
  - Each positioning pass then limits the whole frame, title bar included, to the work area minus 40 pt in each dimension.
  - Page content never resizes the window; long pages scroll inside it.
  - The size is saved at the end of a live resize and on close.
- **Position**
  - The target screen is the panel's screen if the panel is open, otherwise the mouse screen.
  - The window is centred on first creation, or when it is on another display or off-screen. A centred window is clamped 20 pt inside the work area; otherwise it keeps its position and only the size limit applies.
  - Positioning runs on open and again one run-loop turn later.
  - **Avoiding an open panel.** The window moves to the side of the panel that faces the screen centre (panel in the right half → window on its left, and vice versa), with a 28 pt gap and a 20 pt screen margin. If that side does not fit, it goes below the panel, else above. If it still overlaps, it takes its preferred frame clamped to the work area and **the panel closes**.
  - When Settings is opened from the update highlights tour, the two windows are laid out side by side (16 pt gap, 20 pt inset), else stacked, else cascaded.
- **Dock and ⌘Tab presence.** While the window is open the app becomes a regular app (retain/release counting) and returns to accessory when it closes.
  - **Windows:** a normal top-level window gets a taskbar button automatically. The tray stays.
- **Visibility tracking.** Occlusion or minimization pauses page animations (`SettingsWindowVisibility`). The secure-input monitor is told whether the window is open.
- **Reopen.** Settings comes to the front and keeps its last page.
- **Mouse buttons 4 and 5** (back/forward side buttons) navigate history only while Settings is the key window, no sheet or modal is open, no mouse-button shortcut is being recorded, and neither the radial menu nor mouse-button shortcuts claim the button. The matching button-up and drag events are swallowed.
- **Hide (⌘H)** leaves the Settings window on screen (it is excluded from hiding).

#### 3.5.2 Router and history

**`SettingsRouter` state:**
- `page`;
- `destination` = page + optional `sectionAnchor`;
- `sidebarFeature`, the feature row that should look selected;
- `requestID`, fresh for every request;
- one-shot `pendingDestinationRequest` and `pendingFeatureTarget`;
- hints for Cleaner tool, island module and companion tab.

**`request(destination, targetFeature, sidebarFeature, replacingVisit)`**
- Requests for the island companion page redirect to the island page with the companion-tab hint.
- Each request publishes a new request id. The destination page consumes the request and scrolls to its anchor.
- History: a change of page appends an entry and truncates the forward stack. Section requests within the same page refine the current entry, except on General and Energy, where each anchor is its own tool, so a tool change appends.

**Back/forward.** Skip entries whose page is no longer visible (its features were uninstalled). Toolbar buttons are disabled when there is nowhere to go.

**Fallbacks**
- If the current page becomes invisible, go to Features.
- If an anchored General or Energy destination loses its tool, show the page overview or the first remaining tool.

**Pages (35) and their gates.** A page is visible if its gate list is empty or contains any installed feature.

| Page | Gate (any of) |
|---|---|
| general, features, shortcuts, advanced, about, releaseNotes, support | — (always visible) |
| energy | keepAwake, brightness, extraBrightness, bluetoothSleep |
| monitor | monitorCPU, monitorGPU, monitorMemory, monitorNetwork, monitorDisk, monitorPower, connectedDevices, fanControl |
| mouse | scrollInverter, scrollHorizontal, focusFollowsMouse, smoothScroll, linearScroll, mouseAcceleration, mouseNavigation, mouseButtonShortcuts, middleClick, mouseClickDebounce |
| switcher / dock / windowLayout | switcher / dockPreview, dockClick, spacesOrder / windowLayout, windowMaximizer |
| autoQuit / quitProtection | autoQuit / quitWindowProtection |
| clipboard / cutPaste / shelf / media | clipboardHistory, pastePlain, finderCutPaste / finderCutPaste, finderRename / shelf / mediaTools |
| quickTools | quickLauncher, quickToggles, micMute, cameraPreview, wallpaper, scratchpad, cleaningMode |
| screenshot | screenshot, screenRecorder, screenOCR, colorPicker |
| urlCleaner, cleaner, homebrew, appUpdates, uninstaller, killProcess, portManager, keyDebounce, superKey, textSnippets, radialMenu, commandBar | their own feature |
| notch | notch + 12 extensions (incl. companion) |
| notchMascot | notchMascot (redirects to a tab of notch) |

Every feature also has a **settings destination** (page + optional anchor). Destinations are used by hub row links, sidebar tool rows, Settings and Command Bar search, and links from other pages, the island controls and the highlights tour. (The "Open Features" button on a disabled row does not use the destination; it opens the hub and reveals the feature.) There are 40 stable anchors, for example `general#mixer`, `energy#keepAwake`, `mouse#scrollDirection`, `dock#dockClick`, `clipboard#clipboardHistory`, `quickTools#scratchpad`, `screenshot#screenRecorder`, `shortcuts#keyboardBrightnessShortcuts`, `monitor#fanControl` and `windowLayout#windowMaximizer`. **Anchors are persisted identifiers; never rename them.**

#### 3.5.3 Sidebar

- Column width min 198, ideal 210, max 240. The list uses the sidebar style.
- Each row shows an 18 wide icon (accent tint, or the primary colour when selected), 9 pt spacing and the title. The tooltip is the title.
- **Section headers are collapsible buttons** (chevron 9 pt semibold plus title). Collapse state lives in memory. An external route re-expands the target section and scrolls it to the centre.
- **Rows:** one row per page. A shared page also gets **one row per tool** (feature) whose destination has an anchor on that page. "Tool-only" pages (energy, mouse, switcher, dock, clipboard, cutPaste, quickTools, screenshot) show *only* the tool rows.
- General gets an extra **"Menu bar"** row (anchor `panelConfiguration`, icon `menubar.rectangle`) in second position.
- Shortcuts gets "Keyboard light" when the Displays feature is installed and the Mac's keyboard backlight is supported.
- A tool-only page that is still visible but has none of its tools installed shows its page row instead (for example, Clipboard kept visible only by Cut & paste).
- **Selection.** The sidebar highlights, in order of preference: the requested feature row if its destination matches; else the anchored tool row; else the page row; else the first row of that page. The router keeps `sidebarFeature` only when that feature's destination equals the request, which keeps two features that share one anchor (Invert mouse scrolling and Scroll sideways share `mouse#scrollDirection`) separately selectable.

**Final grouping and order** (empty sections are dropped). Rows appear only if visible.

1. **Essentials:** General, Menu bar, Features, [Dynamic Island], Monitor, [Fan Control].
2. **Utilities:** Command Bar, Quick panel, Quick toggles, Camera preview, Wallpaper, Scratchpad, Cleaning Mode, Screenshot, Screen recording, Copy text from screen, Color picker, Radial menu.
3. **Sound:** Music app blocker, Volume mixer, Output switcher, Audio device priority, Mute microphone.
4. **Energy and display:** Keep awake, Displays, Extra brightness, Bluetooth on sleep.
5. **Windows and Dock:** Window switcher, Dock Preview, Dock clicks, Keep Spaces in a fixed order, Window layout, Maximize windows, Quit on close.
6. **Mouse and keyboard:**
   - Mouse tools: Invert mouse scrolling, Scroll sideways while holding a key, Trackpad middle click, Focus follows mouse, Smooth scrolling, Linear scrolling, Disable mouse acceleration, Side buttons, Mouse button shortcuts, Extra click filter.
   - Then Quit & close protection, Debounce, Super key, Text snippets.
7. **Files:** Clipboard history, Paste as plain text, Cut & paste, Rename shortcut, Shelf, Media, Clean URL.
8. **App management:** App updates, Cleaner, Homebrew, Uninstaller, Kill Process, Port Manager.
9. **App:** Keyboard shortcuts, [Keyboard light], Advanced, About, What's New, Support.

Grouping algorithm, for reproduction. The sidebar is built from the page directory, then regrouped:
- Essentials keeps General, Menu bar and Features, then Dynamic Island (moved from Utilities), then Monitor and Fan Control.
- General's anchored sound rows form Sound; Energy's rows form Energy and display.
- Rows from the directory's "Window controls" section go to Windows and Dock if their feature group is windowsDock, otherwise to Mouse and keyboard (including the Quit & close protection page row).
- Rows from "Utilities" whose group is mouseKeyboard, clipboardFiles or sound move to Mouse and keyboard, Files or Sound. This applies to page rows too (Debounce, Super key, Text snippets, Clean URL); a page row's group comes from the feature whose destination is the bare page.
- Media stays in Files. App management and App are unchanged. The companion has no row.

On Windows the groups will be re-scoped to Windows features (§10).

#### 3.5.4 Search

**Field**
- A placeholder "Search settings" pinned at the top of the sidebar. Esc or the clear button empties it.
- While the query is non-blank, the sidebar list is replaced by results.

**Index**
- Every page has a title, an icon and **keywords**, each optionally owned by a feature. Keywords are mostly the localized labels of controls on the page, plus some fixed English tokens that match in every language ("debounce", "⌘Q", "PDF", "convert", "force quit", "PID", "notch", "Claude", "SF Symbols"). Some pages have none.
- Every feature contributes a row with its hub title and its destination.
- When a page and a feature are one-to-one, the page row and the feature row merge.
- There are extra rows for "Menu bar" (keywords: "Show menu bar icon", "Menu bar icon", "SF Symbols") and for keyboard-brightness shortcuts.

**Matching**
- Case-, diacritic- and width-insensitive containment, with **no locale**. The Turkish dotted I is deliberately not special-cased.
- Rank: exact title, then title contains the query, then keyword-only match.

**Grouping**
- Results are grouped under the page each item routes to. Every group draws its page row as a semibold, clickable header; that row is a keyboard-selectable result only when the page title itself matches.
- Each matching keyword becomes its own suggestion row, indented 18, with the page's icon. It routes to the keyword's owning feature destination, or to the page. On the Features page, keyword rows route to Features and reveal that feature's row.
- Keywords of uninstalled features are dropped. An uninstalled feature's own row routes to **Features**, revealing that row.
- Duplicate titles within a group are removed.

**Keyboard**
- Up and Down move a wrapping selection. Return or keypad Enter opens it.
- The selection resets to 0 when the query changes and follows the same item when results change.
- Scrolling animates in 0.2 s, or jumps when reduce motion is on.

**Routing**
- An uninstalled feature, an item whose destination is the Features page, or a page that is not visible goes to Features with that feature as the reveal target. A hidden page with no single feature goes to Features with no target.
- Otherwise it goes to the destination. The search clears on navigation.
- The Command Bar uses the same search index and routing (Command Bar spec).

#### 3.5.5 Section anchors and landing highlight

- A page marks anchored blocks. On a routed request it scrolls the anchor to the **centre**: animated 0.3 s ease-in-out, or instant with reduce motion. The first attempt runs one run-loop turn after the request and the second 0.08 s later; the 1.8 s highlight hold starts after the second attempt.
- **Highlight:**
  - accent fill at 10 %, a 2 pt accent stroke at 90 %, and an accent glow (radius 14, 50 %);
  - the corner radius matches the block (7 by default, 10 with 10 padding for grouped forms, 16 for cards);
  - it fades out after **1.8 s** over 0.4 s, or disappears instantly with reduce motion.
- General and Energy render only the selected tool, so they highlight without scrolling.

#### 3.5.6 Page anatomy

Seven pages use the card structure below: General (with its tool destinations and Menu bar), Features, Energy, Monitor, Mouse, Switcher and Dock.

- A scroll view containing a vertical stack, spacing 20, max width 760, centred, padding 22.
- Page title: `.title2` bold, which is 17 pt on macOS (see §4.3 for Windows sizes).
- **Settings card:** padding 16, radius 16, fill = the quaternary label colour at 0.35 (about 3.5 % black or white), optional `.headline` title (13 pt bold), inner spacing 13.
- **Settings row:**
  - A 26×26 icon tile: radius 7, accent at 12 %, a 12 pt semibold accent icon. When the icon is `nil`, the tile shows the tray glyph in white on 82 % black.
  - Title (body, 13 pt) with an optional badge (8 pt bold white on accent capsule) and a caption (`.caption`, 10 pt, secondary).
  - A trailing accessory with a 12 pt minimum spacer. The text column starts at 38 pt (`settingsRowTextInset`).
- **Choice row:** segmented control beside the title if it fits; otherwise under the title indented 38; otherwise a menu.
- **Menu row:** the pop-up menu sits beside the title if it fits; otherwise under the title, indented 38 if it fits there, else flush with the icon. It never switches control type.
- **Feature-dependent switch row:** if the feature is uninstalled, the whole row (icon, title and switch) is disabled, desaturated, at 45 % opacity and shown off. Below it: 'Enable “<feature>” in Features.' with a small "Open Features" button that opens the hub and reveals the feature. If something else rules the option out, the caption explains why.

Other pages:
- **Grouped form** (system grouped sections with header and footer): Clipboard, Cut & paste, Quick panel, Screen capture, Window layout, Quit on close, Quit & close protection, Debounce, Super key, Text snippets, Radial menu, Command Bar, Shelf, Clean URL, App updates, Keyboard shortcuts, Advanced and About.
- **Custom layouts:** Dynamic Island, Homebrew, Media, Cleaner, Port Manager, What's New and Support.

#### 3.5.7 General page

1. **Title** "General".
2. **Basics card**
   - **Launch at login** (`laptopcomputer` icon; caption "Opens by itself every time you turn on your Mac.").
     - The switch reflects the system registration, seeded from the stored wish.
     - A spinner shows while a change is pending.
     - In the needs-approval state the switch reads on, with a note and an "Open System Settings…" button.
     - Errors show in red.
     - The status is re-read when the page appears, each time Settings opens, and on every app activation while Settings is visible. Stale reads are dropped by request id.
   - **Divider,** then **Language:** a menu of 15 languages in their own names (§3.9). The change is applied live.
3. **Appearance card** (title "Appearance")
   - Three thumbnail buttons: System, Light, Dark. Each shows a 66-tall desktop picture (gradient plus a mini window), radius 9. The selected one has a 2 pt accent border, accent text and an 8 % accent background (radius 12).
   - Caption: "Applies to Vorssaint's own windows and panels, not to the whole Mac."
   - On macOS 26 and later: a "Liquid Glass" row with two toggles: "Other windows and panels" and "Dynamic Island".
4. **Global shortcut card** (only if Keep Awake is installed)
   - "Enable shortcut for 'Keep awake'" toggle (`hotkeyEnabled`), caption "Works in any app, no extra permissions."
   - A shortcut recorder row, indented, for the keep-awake role.
   - The note "macOS rejected this shortcut. Choose another one." when registration failed.
5. **Feedback card:** "Feedback", with the caption "Send a bug report or feature idea directly to the person who maintains Vorssaint." and a "Send feedback" button that opens the feedback window.

#### 3.5.8 General › tool destinations

General also hosts these tool destinations: Menu bar, Volume mixer, Output switcher, Audio device priority and Music blocker. Each shows only its own card. The Sound tools are covered by the Sound spec.

#### 3.5.9 Menu bar page (`general#panelConfiguration`)

The page is one card titled "Menu bar":

1. **Intro:** "Click Vorssaint's icon in the menu bar to open the panel. Its tabs appear in this order."
2. **Panel layout editor.** The miniature sits beside the rows when width allows, otherwise above them.
   - **Miniature** (232 wide), a faithful picture of the corner of the screen:
     - A 22-tall black (82 %) menu bar strip (radius 6) with a wifi glyph, a battery glyph (only on Macs with an internal battery) and the app glyph highlighted (white at 16 %, radius 4).
     - Below it, the panel bubble: 12 radius, with a 14×7 arrow pointing at the glyph 22 pt from the right edge. Its background is material plus base fill, with a border and shadow.
     - Inside: the brand mark (32), a tab strip (9 pt icons, 20 tall, radius 5) and the open tab (uppercase title plus three placeholder bars). It ends with footer pills (gear, power).
     - Clicking a tab selects it. Hovering a row lights its tab. Changes animate smoothly in 0.2 s.
   - **Caption** under the miniature: "Drag to reorder. Switch off anything you don't need."
   - **Rows**, one per installed section (Fan Control only if `panelShowFanControl`; Displays only if brightness is enabled):
     - A drag handle, then a 26×26 icon tile (accent when shown, secondary when hidden), the title (callout medium) and a description from `generalSettings.section*` (Wallpaper uses `wallpaper.panelDescription`). Descriptions wrap.
     - A small switch bound to the section's visibility key. The switch is disabled when this is the last visible section. Fan Control's row has no switch, because its visibility follows the Monitor page's toggle.
     - Hover fill 4 %; active (previewed) fill accent 10 %; radius 10.
     - Drag and drop reorders. The order persists on every drop-enter and on drop.
3. **Divider,** then the **Menu bar icon** chooser:
   - Title "Menu bar icon", caption "Choose the icon Vorssaint shows in the menu bar."
   - A wrapping grid of 34×30 cells, radius 7: the brand mark first (tooltip "Use the Vorssaint icon"), then 40 gallery symbols, then an **"Other symbol"** cell.
   - The "Other symbol" cell opens a 280 wide popover: a text field (placeholder "bolt.fill"), a clear button, the caption "Type the name of any SF Symbol. Leave it empty to use the Vorssaint icon." and the warning "This Mac has no symbol with that name." for unknown names.
   - A typed name is saved only while it resolves; otherwise the icon in use when the field opened is restored.
   - Selected cell: accent at 14 % fill, a 1.2 pt accent stroke at 75 %.
4. **Divider,** then **"Can't find the icon?"** (caption "A crowded menu bar can hide it, especially on Macs with a notch.") with a "Show menu bar icon" button (§3.3.7).

**Windows:** keep the section editor and miniature, drawn as taskbar plus flyout. Replace the SF Symbol gallery with Fluent icons and drop the free-text symbol name, or accept Fluent icon names.

#### 3.5.10 Keyboard shortcuts page

- **Form, top caption:** "Edit every global shortcut from the features installed on this Mac. Inactive shortcuts stay saved but do not run."
- **One section per feature group** that has at least one available role, in feature-group order with the group titles ("Dynamic Island" for the island group). Windows and Dock also appears whenever Window layout is installed.
- **Rows**
  - Each row is a label (feature or role icon, title, optional context label = feature name, and a status dot "Active"/"Inactive"), then a recorder field (108 wide), then **Reset**.
  - Features with more than one shortcut get a **disclosure header** with an active/inactive status and a count capsule. Children are indented 25.
  - Screen capture roles are combined into one group headed "Screen capture" (expanded by default), in this order: Screenshot, Screen recording, Copy text from screen, Color picker, Recent captures, Capture the whole screen, Edit latest screenshot, Edit clipboard image, Upload latest screenshot.
  - Displays gets its own disclosure under "Energy and display", with the display-brightness toggle and its two roles.
  - Window layout lists its 41 actions, each with a recorder, a Clear (x) button, Reset and "None" when cleared, followed by the pointer-to-next-display role.
  - Radial menu shows the shortcuts of its wheels (one per line, read-only) plus a "Manage the menu" button that opens the Radial menu page. Recording happens there.
  - Keyboard light: a disclosure headed "Keyboard light" in the Mouse and keyboard section. It carries the `keyboardBrightnessShortcuts` anchor and expands when routed to. Inside: the toggle "Use keyboard brightness shortcuts" and its two roles, which are disabled while the toggle is off (macOS hardware).
  - Each row shows "or <SuperKey + key>" when the Super key is running and the shortcut's modifiers equal the Super key modifiers.
- **Command Bar section** (only when the Command Bar is installed): an "App shortcuts" button that opens a sheet (Command Bar spec), with a caption.

The recording and conflict rules are in §6.

#### 3.5.11 Advanced page (grouped form)

**Backup** section
- Description: "Take your setup to another Mac: export every preference to a file and import it there. Your Scratchpad notes, clipboard history, Shelf items and system permissions never leave this Mac."
- Buttons: "Export settings…" (`square.and.arrow.up`) and "Import settings…" (`square.and.arrow.down`).
- Status: "Backup saved" in green, or "Could not save the backup." / "This file is not a valid Vorssaint backup." in orange.
- Import confirmation alert "Import these settings?" with Cancel or "Import and restart". The body is either "Your current settings are replaced by the file's and the app restarts. Nothing else on this Mac is touched." or, for backups without island keys, the island-specific variant. Details in §7.

**Permissions** section ("Clear all permissions", destructive, `lock.slash`)
- Confirmation alert, then: suspend input interceptors, restore sleep, unregister the fan-control helper and the login item, remove the closed-lid sudoers rule (admin prompt), then `tccutil reset All <bundle id>`.
- Result: "Permissions cleared." in green with a checkmark, or in orange "Some permissions or the closed-lid rule could not be removed. Try again and allow the password request if it appears."

**Uninstall** section ("Uninstall Vorssaint completely", destructive, `trash`)
- Confirmation, then everything above plus restoring the Spaces order, removing preferences and moving the app to the Trash, then quit.
- It stops if the input interceptors cannot be suspended. The login item is removed only after the permission reset succeeds.
- A failure shows an alert with the reason.

**Windows**
- Backup: same, as JSON.
- "Clear all permissions" becomes "Reset privacy and startup settings": remove the Run key or scheduled task and any app-created rules. It can mostly be dropped.
- Uninstall: launch the installer's uninstaller, or run the same steps (remove autostart, `%APPDATA%\Vorssaint`, `%LOCALAPPDATA%\Vorssaint`, then schedule self-deletion).

#### 3.5.12 About page (grouped form)

**About** section
- Brand badge (76): a squircle with radius 0.26×size on the space gradient, with the mark at 0.8×.
- "Vorssaint" (`.title2` bold), "Version X" plus a Beta capsule on beta builds. Developer builds also show the commit stamp in monospace.
- `aboutDescription` (12 pt, centred, secondary).
- Buttons: "Review introduction" (opens onboarding, closable), "Review highlights" (opens the update tour in review mode, then the support intro) and a "View on GitHub" link.
- "© 2026 Vorssaint" (caption2, tertiary).

**Updates** section
- "Check for updates automatically" (`autoCheckUpdates`, default true).
- "Receive beta updates" (`includeBetaUpdates`; on by default in beta builds) with the caption "Beta versions include features in development and may contain bugs or incomplete behavior."
- A status row with an icon:

| State | Icon | Text |
|---|---|---|
| idle | — | no row |
| checking | secondary `arrow.triangle.2.circlepath` | "Checking…" |
| up to date | green `checkmark.circle.fill` | "You're on the latest version." |
| available | accent `arrow.down.circle.fill` | "Update available: X" |
| downloading | secondary `arrow.down.circle` | "Downloading update…" plus " N%" when progress is known |
| installing | secondary `gearshape.2.fill` | "Installing and restarting…" |
| failed | orange `exclamationmark.triangle.fill` | "Couldn't check: <reason>" |

- "Check now" (disabled while busy) and "Download and install" (prominent, only when an update is available; opens the preview).
- "Last checked: <short date and time>".

#### 3.5.13 What's New page

- Title "What's new in this version". Version line "vX · <date>".
- The bundled `CHANGELOG.md` section for the current version, falling back to `Unreleased`.
- Parsed into sections. Section titles are uppercase 11 pt bold with tracking 1.2.
- Bullets carry an 18 wide accent icon chosen by section: Added → `plus.circle.fill`, Changed → `slider.horizontal.3`, Fixed → `checkmark.circle.fill`, other → `circle.fill`.
- Paragraphs are 12.8 pt. Images are rounded (radius 8), indented 27.
- Links are flattened to their label; `**` and backticks are stripped; "Website" and "Links" sections are skipped.
- Fallback text: "This update includes the latest fixes and improvements."

#### 3.5.14 Support page

- A heart in a 78 pt circle on the space gradient.
- Heading "Help Vorssaint keep growing", a message, and a prominent large "Support on Buy Me a Coffee" button (`cup.and.saucer.fill`; `https://buymeacoffee.com/vorssaint`).
- A star card (yellow star tile, message, "Star Vorssaint on GitHub" button, bordered, large; `https://github.com/vorssaint/vorssaint-utils`).
- A Discord card with a Discord-coloured tile `#5966F0` (RGB 0.35, 0.40, 0.94), title and message, plus "Join the Discord community" (prominent, large, Discord mark; `https://discord.gg/M6BwWH4BJp`) and "Follow @vorssaint on X" (bordered, X logo; `https://x.com/vorssaint`). The two buttons stack vertically when they do not fit side by side.
- A thanks line (caption, tertiary). The 🖤 in it becomes 🤍 in dark mode.
- Cards: radius 14, control-background fill, separator stroke at 45 %, max width 510.

#### 3.5.15 App main menu and accelerators

macOS shows a minimal main menu only while one of the app's windows is focused, so the standard shortcuts work in an accessory app:

- **App:** About (the standard macOS About panel with `aboutDescription` as 11 pt credits, not the About page); Settings… ⌘,; Hide ⌘H; Hide Others ⌥⌘H; Show All; Quit ⌘Q.
- **Edit:** Undo ⌘Z; Redo ⇧⌘Z; Cut ⌘X; Copy ⌘C; Paste ⌘V; Select All ⌘A.
- **Go:** Back ⌘[; Forward ⌘]. These are used by side-button drivers too. They are enabled only when navigation is allowed and a visible history target exists.
- **Window:** Minimize ⌘M; Zoom; Close ⌘W.

Titles are rebuilt on language change.

**Windows:** standard edit shortcuts work natively in text controls. Add Alt+Left / Alt+Right and XButton1 / XButton2 for history, Ctrl+F to focus search, Ctrl+W or Esc to close Settings (optional) and F1 for help (optional).

### 3.6 Features hub

#### 3.6.1 Availability model (`FeatureRuntime`)

**Availability**
- Availability is the layer **above** each feature's enable keys.
- Uninstalled: the service never instantiates on the next launch and is torn down now. The feature leaves every surface (panel sections and tiles, Settings pages and rows, search keywords, shortcuts, Command Bar rows, tray metrics).
- Uninstalling never deletes settings.

**Bindings**
- 70 of the 77 features have a closure that brings their services in line with the current availability and enable keys. Running it is idempotent.
- The on-demand tools quickToggles, cleaningMode, uninstaller, homebrew, killProcess, portManager and connectedDevices have no binding. For them, availability only changes what is visible.
- `syncAtLaunch` runs the bindings of available features only.
- Permission changes re-run bindings:
  - An Accessibility change re-runs these 26: scrollInverter, scrollHorizontal, focusFollowsMouse, smoothScroll, linearScroll, mouseNavigation, switcher, dockPreview, finderCutPaste, finderRename, autoQuit, dockClick, middleClick, windowMaximizer, keyboardDebounce, windowLayout, textSnippets, brightness, radialMenu, mouseButtonShortcuts, mouseClickDebounce, superKey, quitWindowProtection, mixer, musicBlock, notch.
  - A Screen Recording change re-runs dockPreview and screenRecorder.

**Install gate (`mayFlip`)**
- An install is refused when the hardware is unsupported. Currently only Fan Control is checked (no controllable fan; reason text `fanControl.noFans`). The row is greyed and a tooltip gives the reason.
- **Uninstalls are never refused, and an existing install is never revoked**, even if the hardware check would now say no.

**First-install enabling**
- A feature installed for the first time switches on its primary enable key, but only if none of its enable keys was ever saved on this machine.
- This uses the persistent domain, not the effective value. **The Windows store must distinguish "explicitly saved" from "default".**
- Exceptions:
  - Window layout uses `windowLayoutShortcutsEnabled` (its saved-check covers 5 keys).
  - Audio priority enables both output and input.
  - Live equalizer never auto-enables.
- Quirk: the saved-check does not skip `scrollInverterHorizontalEnabled`. A migration saves that key on every Mac, so Invert mouse scrolling never auto-enables.

**Bulk actions**
- **Install all** does not flip enable keys, to avoid switching on intrusive features. **Uninstall all** uninstalls everything.
- Presets and the first-run picker use `replaceAvailable(selected, enableKeys)`: the selected set becomes the installed set, and the given enable keys are turned on.
  1. It writes the given enable keys first, then snapshots the persistent domain.
  2. Every feature that newly joins gets first-install enabling. So the Windows preset also turns on `windowLayoutShortcutsEnabled` on a fresh machine, and features picked during onboarding get their primary key turned on.
  3. It re-runs the bindings of all selected installed features, and sets `notchInitialExtensionsInstalled` if the island ends up installed.
- `prepareFirstRunAvailability` writes explicit availability for all 77 features (Essentials true, the rest false). It runs on every launch while `hasOnboarded` is false and `onboardingStep` is 0, before any binding.

**Dynamic Island**
- The first-ever island install also installs its initial extensions (all except the companion) and sets `notchInitialExtensionsInstalled`.
- Uninstalling the island asks whether to take its installed extensions along, but only if at least one extension is installed.
  - Title "Uninstall Dynamic Island". Message "These extensions only work inside the Dynamic Island: %@. Uninstall them too? Nothing is deleted, and everything comes back with one click."
  - Buttons: "Uninstall extensions too" / "Keep extensions" / Cancel. The switch springs back on Cancel.

**Session tracking**
- `loadedThisSession` is the set loaded at launch or installed during this session. `needsRestartToUnload` is true when any of them is now uninstalled; this drives the restart card.
- `offerableThisSession` is the set installed at launch and not reinstalled since. Only these can be offered as "never turned on".

**After each change**
- Bump `revision` (all observers re-read).
- If the Command Bar is installed, rebuild its catalog, which drops rows of removed features.
- If the island is installed, re-sync it.

**Counts**
- `availableCount` is the number of installed features.
- `installableCount` is the number that are hardware-supported or already installed. "Install all" is disabled at installableCount, "Uninstall all" at 0.

#### 3.6.2 Hub page layout

The page is a scroll view containing a lazy vertical stack (spacing 20, max width 760, padding 22).

> **Implementation note:** the lazy stack must be the scroll view's direct content. Nested, it re-laid out the stack as each card appeared: scrolling stalled for up to a second, and the layout could loop until Settings froze (issue #2270). Use virtualization on Windows.

1. **Header.**
   - Title "Features" (`.title2` bold).
   - Intro (callout, secondary). On the Features tab: "Install only what you use. Whatever you uninstall disappears from the whole app and stops loading." On the Permissions tab: "What each permission does and which features use it. Other access is requested only when you use it."
2. **Segmented tabs:** Features / Permissions.
3. **Restart card,** on either tab, while `needsRestartToUnload`.
   - Contents: a 24 pt accent `arrow.clockwise.circle.fill`, the text "Features uninstalled in this session stay loaded until the app restarts. Restart to unload them from memory now." and a prominent "Restart now" button that relaunches.
   - Card style: accent fill at 12 %, radius 16, padding 16.

On the **Features** tab, the cards follow:

4. **Summary card.** "N of M features installed" (headline), "Install all" and "Uninstall all" buttons, and a linear accent progress bar.
5. **Never-turned-on card** (§3.6.5).
6. **Dynamic Island card.**
   - A group header, then the island row, then a "What appears" disclosure (open by default) listing 12 extension rows separated by dividers.
7. **Presets card.**
   - A "Start with a bundle" disclosure (open by default).
   - Inside: three preset cards in a row, then the caption "One click sets the app up for how you use your Mac. Everything else stays one click away."
8. **Group cards,** one per hub group except the island, in this order: Windows and Dock, Mouse and keyboard, Clipboard and files, Sound, Energy and display, Tools, System monitor.
   - Each is a disclosure, open by default.
   - **Header:** a 26 icon tile, the group title (headline), an "installed/total" caption (monospaced) and a 64 wide progress bar.
   - Rows are separated by dividers inset 8.
   - The monitor group adds "With everything off, the Monitor leaves the panel and the menu bar." when all of its features are off.
9. **Footer notes** (caption, secondary):
   - "Uninstalling deletes nothing: the feature just leaves the app and stops loading. Install it again anytime and everything returns as it was."
   - "What the feature keeps alive while it is on. Uninstalled features load nothing at all."

**Group icons:**

| Group | Icon |
|---|---|
| windowsDock | `macwindow.on.rectangle` |
| mouseKeyboard | `computermouse` |
| clipboardFiles | `doc.on.clipboard` |
| sound | `speaker.wave.2.fill` |
| energyDisplay | `bolt.fill` |
| tools | `wrench.and.screwdriver.fill` |
| dynamicIsland | `macbook` |
| monitor | `chart.line.uptrend.xyaxis` |

#### 3.6.3 Feature row

**Layout:** padding 7×8, radius 10. Hover fill (4 % primary) applies only if the row opens settings. A highlight (accent 10 %) appears when the row is revealed.

**Content, left to right:**
- The title, primary colour when installed, secondary when not.
  - The island row carries an **EXPERIMENTAL** capsule (9 pt bold orange on 14 % orange).
  - Beta features (Fan Control, Kill Process) carry a **BETA** capsule (8 pt bold white on accent).
- The description (caption; secondary, or 70 % secondary when uninstalled).
- A `chevron.forward` (10 pt, tertiary) when installed and the feature has its own settings destination. The whole row is then a link that opens Settings at that destination with the feature row selected.
- A **metadata button** (caption, secondary).
  - It reads "Permissions" if the feature uses any permission, otherwise the energy label.
  - The tooltip lists the permission names and the energy label.
  - Clicking it shows a popover: a grid of permission icon + name rows, plus an energy row (energy symbols + label).
- A small **install switch.** It moves only after the runtime flips. It is disabled, with a tooltip giving the reason, when the install is blocked.

Accessibility label: "<title>. <description>", with the badge text inserted after the title: "<title>. Experimental. <description>" for the island, and "<title>. <beta warning>. <description>" for beta features. A blocked switch is labelled "<title>. <reason>".

#### 3.6.4 Presets (bundles)

| Preset | Icon | Features installed | Enable keys turned on |
|---|---|---|---|
| Essentials | `star.fill` | mixer, keepAwake, monitorCPU, monitorGPU, monitorMemory, monitorNetwork, monitorDisk, monitorPower | — |
| Windows | `macwindow.on.rectangle` | switcher, windowLayout, dockPreview, dockClick, windowMaximizer | switcherEnabled, dockPreviewEnabled, dockClickMinimize, windowMaximizeEnabled |
| Battery and quiet | `battery.75percent` | monitorCPU, monitorMemory, monitorPower | — |

- **Preset card:** padding 12, minimum height 138, radius 12, fill at 5 % primary.
  - Contents: a 34×34 icon tile (radius 9, accent 12 %), the name (13 pt semibold), the description (caption) and an "Apply" button (small).
- **Apply** opens an alert titled with the preset name. The message is "Install the {name} bundle and uninstall the rest? Nothing is deleted, and everything comes back with one click.", with the buttons "Apply bundle" and Cancel.
- Applying replaces the installed set with the preset and turns its enable keys on, animated with ease-out 0.22 s.
- **Clean install:** availability is preset to **Essentials** before onboarding (§3.1).

#### 3.6.5 "Never turned on"

**Candidates** (`offeredWhenNeverSwitchedOn`): dockPreview, dockClick, windowMaximizer, autoQuit, scrollInverter, linearScroll, focusFollowsMouse, mouseAcceleration, mouseNavigation, mouseButtonShortcuts, middleClick, keyboardDebounce, mouseClickDebounce, superKey, finderCutPaste.

**A feature qualifies when** all of these hold:
- it is installed;
- it has at least one enable key;
- none of its enable keys is on;
- none of them was ever saved;
- it was offerable this session;
- it is not in `featureHubKeptFeatures`.

One key is ignored for the "saved" check, because a migration saves it on every machine: `scrollInverterHorizontalEnabled`.

**Card**
- Shown only when **at least 3** features qualify. It is computed when the page appears and after each availability change.
- Title "Never turned on". Message "These features are installed but have never been on: {list}. Uninstalling them makes the panel and Settings shorter. Nothing is deleted, and each one comes back with one click."
- Buttons: "Keep them" adds them to the kept list (comma-separated raw ids). "Uninstall these" re-checks the list at click time, then uninstalls.
- After uninstalling, the same card shows a 20 pt green check icon, the footer note (callout) and an **Undo** button. This confirmation lives only in view state. Undo reinstalls without enabling and marks the features as kept.

#### 3.6.6 Permissions portal

The portal is one card listing all 13 permissions, separated by dividers inset 42. Each row contains:

- A 30×30 tile in the status colour: green when granted, orange when missing, secondary when unknown.
- The name with a status chip (a 6 pt dot plus "Granted", "Not granted" or "The app can't check this one").
- An explainer line.
- A "Used by {features}" line, or "Nothing that is on uses this permission right now."
  - This comes from `activeFeatures(using:)`. A feature counts only if it is installed and engaged: any enable key on, or no enable keys at all.
  - It then applies dynamic rules: the simple-mode switcher needs no Screen Recording, monitor alerts need notifications only when enabled, and so on. The Notifications row also drops monitorPower on Macs without an internal battery.
- When granted and unused: a banner "You granted this permission, but nothing that is on needs it. If you like, revoke it in System Settings."
- Buttons:
  - "Request" appears only while the permission is not granted and a request flow exists. Accessibility, Screen Recording and Full Disk Access always have one. Notifications, camera and microphone have one only while undetermined; calendar while not determined or write-only; the others never.
  - The Full Disk Access "request" touches protected paths, then opens the settings pane after 0.9 s.
  - "Open System Settings" is always shown.

**How each status is derived**
- filesAndFolders: "unknown" unless Cleaner is installed and WhatsApp cleanup is on.
- automationPlayback and appManagement: always "unknown".
- audioCapture: "missing" only when the mixer is installed and its audio tap failed, otherwise "unknown".
- calendar: "granted" only with full access.
- Finder and Terminal automation: checked with the system's automation-permission query; a target app that is not running reads "unknown".

Permission list and icons:

| Permission | Icon |
|---|---|
| accessibility | `accessibility` |
| screenRecording | `rectangle.dashed.badge.record` |
| fullDiskAccess | `externaldrive.badge.person.crop` |
| filesAndFolders | `folder.badge.person.crop` |
| notifications | `bell.badge` |
| automationFinder, automationTerminal, automationPlayback | `gearshape.2` |
| audioCapture | `waveform` |
| microphone | `mic` |
| camera | `camera` |
| appManagement | `app.badge` |
| calendar | `calendar` |

While visible, the portal polls Accessibility and Screen Recording every 2.5 s. It checks automation status off the main thread.

**Windows**
- Most macOS TCC permissions do not exist for desktop apps.
- Keep the portal concept for real Windows gates:
  - Camera, Microphone, Location (Settings → Privacy & security; `ms-settings:privacy-webcam` and similar);
  - Notifications (`ms-settings:notifications`);
  - "Run as administrator" (needed to hook or automate elevated windows, because of UIPI);
  - Startup apps (`ms-settings:startupapps`);
  - Graphics capture border consent where applicable.
- The feature → permission mapping must be redone per Windows feature.

#### 3.6.7 Reveal target

A routed request with a target feature does the following:

1. Switch to the Features tab.
2. Expand the feature's group (or the island extensions).
3. Scroll the group to the top. Then, after 0.08 s, scroll the row to the centre with a 0.3 s animation, and retry once after another 0.08 s.
4. Highlight the row, and clear the highlight after 1.2 s (ease-out 0.25 s).

#### 3.6.8 Energy profiles (hub metadata)

| Profile | Label | Symbols |
|---|---|---|
| idle | "Nothing at rest" | `leaf` |
| mouse | "Listens to the mouse" | `computermouse` |
| pointer | "Listens to pointer input" | `hand.point.up.left` |
| keyboard | "Listens to the keyboard" | `keyboard` |
| inputs | "Listens to mouse and keyboard" | mouse + keyboard |
| periodic | "Checks on an interval" | `clock.arrow.circlepath` |

Some profiles depend on settings. For example, Window layout is `pointer` when gestures or edge snap are on, and `inputs` with a modifier trigger. The per-feature values are in Appendix A.

### 3.7 Onboarding, intros and permission guidance

#### 3.7.1 First run

**Window**
- 540×600 fixed, titled with a transparent title bar, full-size content, movable by background. It is centred and clamped to the work area.
- On first run there is **no close button**. When reopened from About, it is closable.
- The step index persists in `onboardingStep`. macOS may relaunch the app during a permission grant, and the flow resumes at the same step.
- The floating permission guide is suppressed while onboarding is open.

**Body:** scrollable content, a divider, then a navigation bar (padding 16):
- "Back", disabled on step 0.
- Page dots: capsules 7 tall. The active dot is 18 wide in the accent colour; the others are 7 wide at 15 % primary. They animate with a spring (response 0.3, damping 0.8).
- The primary button (large, prominent, default action): "Continue", or "Open Vorssaint" on the last step.

**Steps**

1. **Welcome**
   - A 220-tall hero on the space gradient: the brand mark (130), "Vorssaint" (26 bold white) and "A discreet menu bar utility that makes everyday macOS more practical." (12 pt, white at 85 %).
   - Below it (padding 24): a **Language** menu, then three feature rows. Each row has a 30×30 gradient tile (radius 8) with a 13 pt white icon, a title (13 semibold) and a body (11.5 pt):
     - `bolt.fill` "Energy under control"
     - `gauge.with.dots.needle.50percent` "A clear view of the system"
     - `rectangle.on.rectangle` "Mouse and windows, your way"
2. **Purpose** ("What brought you here?" / "Choose a ready setup or select exactly what you want to use.")
   - A step header: a 56×56 gradient tile (radius 14) with a 24 pt icon, the title (19 bold) and a subtitle (12 pt).
   - Three **bundle cards** (radius 10, padding 14×10). The selected card has accent fill at 12 % and a 45 % stroke.
   - A "FEATURES" label with "N of M features installed".
   - All features grouped by hub group, each as a selectable card (radius 10, padding 11×8):
     - a 32×32 icon tile, gradient when selected and 16 % secondary otherwise;
     - the hub title (12.5 pt semibold) and description (caption, max 2 lines);
     - a check circle.
   - Selecting the island adds its initial extensions; deselecting it removes all extensions.
   - Blocked (unsupported) features are greyed and disabled, with the reason as a tooltip.
   - Footer text: "You can add or remove features later in Settings."
   - The selection starts as the installed set once a selection was already applied (`hasOnboarded`, or `onboardingStep` ≥ 2). Otherwise it starts as Essentials, with the Essentials bundle card selected.
   - Clicking a bundle card replaces the whole selection. Toggling any feature card clears the selected bundle, so Continue then passes no enable keys.
   - **On Continue:** `replaceAvailable(selected, enable keys of the selected bundle, if any)`.
3. **Permissions**
   - A step header with `key.horizontal.fill`.
   - "Permissions for your choices": one row per needed permission. Only Accessibility and Screen Recording are broad grants explained here, based on the features' `onboardingPermissions`. Each row has a status, then (only while not granted) "Grant access" and "Open System Settings…" buttons, an explainer and "Used by …" with names sorted in locale order.
   - When none are needed: "You do not need to grant any permission to finish setup."
   - An "Other permissions" disclosure ("Optional. Grant these now or later, when a feature needs them.") that embeds the portal rows.
   - Note: "macOS may ask to reopen the app after granting."
4. **Done**
   - A 300-tall hero: the mark (150), "All set!" and "Vorssaint is already looking after your Mac."
   - Then a `menubar.arrow.up.rectangle` icon and the hint "Look for the black hole in the menu bar, at the top right of the screen."
   - **Windows:** "Look for Vorssaint in the system tray. If it's hidden, click ^ and drag it to the taskbar."

#### 3.7.2 Completion markers

Closing the window after completion sets:

- `hasOnboarded = true`;
- `featuresOnboardingVersion = 4` (the `OnboardingInfo.currentFeatureSet` value);
- `lastUpdateIntroVersion = <version>`;
- `brightnessUpdatePromptState = handled`;
- the support intro and showcase are marked seen if they belong to this version;
- the update highlights are marked seen, so a clean install never gets the update tour;
- on beta builds, once only: if the Command Bar and the island are both installed, the companion is installed and turned on, and `notchMascotBetaInstalled` is set. (`Defaults.register` runs the same rule on every launch.)

If the app is quitting (a system relaunch during a permission grant), the flow is **not** marked complete.

#### 3.7.3 Post-update intro chain (existing users)

The chain runs on **every** launch of an onboarded user, on the next run-loop turn, if the previous start finished. Each intro is shown only when its version matches and its seen marker is not yet written, so an intro closed while the app was quitting comes back on the next launch. Each step's completion triggers the next.

1. **Update highlights** ("New in this update").
   - Shown when the version matches the release series `3.4.0` (stable patches; beta 3.4.0-beta.N with N ≥ 1) and the seen marker differs. The markers are "3.4.0" or "3.4.0-beta.1".
   - A floating panel sized min(600, w − 32) × min(660, h − 32):
     - "Dynamic Island" title and "Vorssaint X" subtitle;
     - an animated GIF tour (bundled `Gifs/highlights-notch.gif`; static when reduce motion is on);
     - a caption;
     - "Set up", which installs the island if needed, opens Settings on it and arranges the tour beside Settings; and "Done".
   - In review mode (from About), closing it opens the support intro.
2. **Support intro** (not on betas; once per 3.4 series; marker "3.4.0-support").
   - A 560×400 floating panel with **no close button**. It can only be closed with "Done".
   - Contents: a `heart.fill` in a 74 pt gradient circle, "Help Vorssaint keep growing", a message, a "Support on Buy Me a Coffee" button (`cup.and.saucer.fill`; opens the Buy Me a Coffee page) and the thanks line "Thank you for being here." followed by a heart emoji (black, white in dark mode).
3. **Showcase intro** (version 3.1.4 only; obsolete).
   - 680×600: a title, a message and a verified video (SHA-256-checked download from the release asset), then "Not now" / "Continue", then the support step.
4. **Brightness prompt** (one-off, upgrade only).
   - **Queued** at launch as pending or handled, only if no prompt state is stored yet, the build is not a developer build, and the version went up compared with the previous `lastUpdateIntroVersion`.
   - **Shown** only if it is still needed: the island is installed and enabled, island brightness is on, Displays is installed, and display control is off.
   - Alert title "Show brightness in the Dynamic Island?". Message "The Dynamic Island shows brightness changes only while “Control displays” is on in Displays settings."
   - Buttons: "Open Settings" (opens the Displays page) and "Keep Off".

**Windows:** implement the chain as data-driven "intro" records (id, version predicate, seen marker, window). Drop the macOS-specific intros.

#### 3.7.4 Permission guidance

**`PermissionRow`** (Settings and onboarding)
- A status icon (green check or orange exclamation), the name, and "Granted" / "Not granted".
- When missing: "Grant access" (triggers the system prompt) and "Open System Settings…".
- While on screen, it registers a polling demand.

**Polling cadence**
- No polling unless a permission surface is visible or a running feature needs Accessibility or Screen Recording.
- A permission counts as "needed" only for features that monitor permission changes. Screen OCR, Cleaning Mode, Screenshot, Command Bar, Screen recording, Wallpaper, Watch and Companion never do. Window layout does only while shortcuts, gestures, or edge snap with at least one zone are on.
- 2.5 s while a surface is visible or a needed permission is missing; otherwise 60 s. Timer tolerance is 40 %.
- The interval is recomputed on every settings change, on activation and after each refresh.
- A full refresh runs on app activation.
- Full Disk Access is probed off the main thread on every full refresh (launch, activation, portal appearance, after some requests), never polled.

**Floating guide card**
- Shown after requesting Accessibility or Screen Recording when it is not yet granted.
- A non-activating, floating, movable card at the top right of the main screen's work area (20 pt margin), on all Spaces.
- Contents: "One step left", then three steps ("macOS opened System Settings on the right list." / "Turn Vorssaint on in that list." / "Come back. This card notices by itself."), then a "Waiting for the permission…" line.
- After **12 s** it adds the hint "Already on in that list? That entry belongs to an earlier copy of the app. Start over to replace it." with a "Start over" button (`tccutil reset`, then request again).
- The card registers its own polling demand and drops it on grant.
- When the grant is observed it shows "Permission granted!". The Accessibility card closes itself after 1.6 s. The Screen Recording card stays, with a "Relaunch to apply" button, until the user relaunches or closes it.

**Windows:** keep a generic "guide card" pattern for flows that send the user to Windows Settings (camera, microphone, notifications, startup) and for UAC or elevation guidance.

### 3.8 Global shortcuts (mechanics)

The full table and the rules are in §6. In summary:

- **Model.** `GlobalShortcut` = a virtual key code plus modifiers (control, option, shift, command). Fn is not part of the model.
- **Storage.** The string `"control+option+command:40"`: modifier tokens in the fixed order control, option, shift, command, then `:` and the decimal macOS virtual key code. Reading accepts the tokens in any order; an unknown token, a missing `:`, a key code outside 0…0xFFFF or an invalid combination makes the value unreadable, and the role falls back to its default.
- **Registration**
  - One Carbon `RegisterEventHotKey` registrar per feature, told apart by a four-character signature plus an id:

    | Signature / id | Owner |
    |---|---|
    | `VUTL` / 1 | Keep Awake |
    | `VUSH` / 2 | Shelf |
    | `VUCL` / 3 | Clipboard |
    | `VUSO` / 4 | Output switcher |
    | `VUQT` / 10–28, 57–61, 80 | quick tools, screen capture, brightness and other roles |
    | `VUQT` / 200+ | Command Bar per-app rows |
    | `VUQT` / 1700+ | radial menu wheels (one per wheel; the radial role key itself is never registered) |
    | `VUWL` / 30–70 | the 41 window-layout actions |
    | `VUWD` / 56 | the window directional trigger |

  - Ids are unique only per signature (for example `VUWD`/56 and `VUQT`/57–61 reuse numbers from the `VUWL` range). **Win32 `RegisterHotKey` ids have no signature, so the port needs one disjoint id map.**
  - Failure (the combination is taken) sets a `registrationFailed` state that the row reports.
  - The switcher (⌘Tab, ⌘\`) and Finder Rename use keyboard event taps instead of Carbon hotkeys, and need Accessibility. Finder Rename rewrites its key to Return only while Finder is frontmost.
- **Gating.** A role registers only if its feature is installed *and* all of its `requiredEnableKeys` are true. Some need more:
  - Window-layout actions and the directional trigger need Accessibility and an active session, and stand down while an app on the feature's "Ignore apps" list is frontmost (re-checked on every app activation).
  - Move pointer to next display also pauses for ignored apps, but needs no Accessibility.
  - Brightness shortcuts are not registered, and report failure, when the combination is an enabled macOS shortcut that was not taken over. Keyboard brightness also needs backlight hardware.
- **While recording** a new shortcut, every app hotkey is unregistered (`ShortcutCapture.begin`). A swallowing event tap routes keys to the field so the OS or other apps do not act on them. Everything is re-registered on every exit path.
- **Conflict checks** (which ones apply depends on the row; §6.2):
  - other app roles (active only, or all including inactive);
  - radial-menu wheel shortcuts;
  - window-layout actions and the directional trigger;
  - **macOS system shortcuts** (live WindowServer table, falling back to the prefs plist), with an optional **take-over offer** that disables the macOS shortcut while the app holds it and restores it on release, quit or crash.

### 3.9 Localization runtime

**Languages.** The 15 `AppLanguage` cases. The code is the rawValue, which is also the storage value in `appLanguage`.

| Code | Display name (own script) | Count agreement | Windows culture |
|---|---|---|---|
| `en-US` | English (US) | one/many | en-US |
| `pt-BR` | Português (Brasil) | one/many | pt-BR |
| `tr` | Türkçe | one/many | tr-TR |
| `ru` | Русский | by last digits (1 / 2–4 / other; 11–14 → other) | ru-RU |
| `es` | Español | one/many | es-ES |
| `sk` | Slovenčina | by whole number (1 / 2–4 / other) | sk-SK |
| `de` | Deutsch | one/many | de-DE |
| `fr` | Français | one/many | fr-FR |
| `it` | Italiano | one/many | it-IT |
| `ja` | 日本語 | one/many | ja-JP |
| `ko` | 한국어 | one/many | ko-KR |
| `uk` | Українська | by last digits | uk-UA |
| `zh-Hans` | 简体中文 | one/many | zh-CN (zh-Hans) |
| `zh-TW` | 繁體中文（台灣） | one/many | zh-TW (zh-Hant-TW) |
| `zh-HK` | 繁體中文（香港） | one/many | zh-HK (zh-Hant-HK) |

**Default language.** Used whenever no language is stored. It is never saved, so it is re-evaluated from the OS at every launch until the user picks a language. From the first preferred OS language (an empty list counts as "en"):
- lowercased, `zh-hk` or `zh-hant-hk` → zh-HK;
- lowercased, `zh-tw`, `zh-hant-tw` or `zh-hant` → zh-TW;
- otherwise a prefix match on the original string: pt, tr, ru, es, sk, de, fr, it, ja, ko, uk, zh (→ zh-Hans);
- else en-US.

**Windows:** read the first entry of `GetUserPreferredUILanguages` and apply the same mapping.

**Live switching**
- The language is observable; every view re-renders on change.
- The main menu and the tray tooltip and title are rebuilt.
- Windows already open re-read strings. Error captions are cleared on change.

**Date and time formatting.** Three behaviours coexist:
- **Formatting locale** (island views only: calendar, notifications, agents, watch, capsules): the **app language** combined with the **system region, hour cycle and first day of week**. For example, English with a 24-hour region shows "23:05", and a 12-hour system clock is kept in any language.
- **System locale:** the tray "awake until" time, the Until chip's time and its picker, and Settings timestamps (Last checked, app updates).
- **App language with its own clock** (`Locale(identifier: <code>)`): the Until summary and the keep-awake duration chips.
- **Windows:** build a culture from the app language, then override the time format, the first day of week and the region number formats from the user's locale settings. Decide whether to unify the three behaviours; the macOS mix is incidental.

**Key-cap labels.** Shortcut labels are layout-aware: the character the current layout produces for that key. Shortcuts containing ⌘ read the layout's "command" table.
- **Windows:** use `ToUnicodeEx` or `MapVirtualKeyEx` with the active HKL, and `GetKeyNameText` for named keys.

### 3.10 Appearance and theming

**Modes.** `appAppearance` = `system` | `light` | `dark`, default system. Unknown values fall back to system.
- It applies to **all app windows and the panel**.
- The tray icon always follows the system menu bar. **Windows:** it follows the taskbar theme.
- It is applied before any window is created, and live on change.

**Glass (macOS 26 and later)**
- `liquidGlassEnabled` covers panels and HUD surfaces. `notchLiquidGlassEnabled` covers the island. Both default false. On existing installs, `notchLiquidGlassEnabled` was seeded once from `liquidGlassEnabled`.
- The toggles appear only where Liquid Glass is available.
- Glass is ignored when the system "Reduce transparency" setting is on. The island's quick-access surface also drops glass under Increase contrast.
- The HUD opacity slider is disabled while glass is drawn.

**Accessibility**
- **Increase contrast** raises border opacities: panel borders go from 0.09 to 0.24 (light) and from 0.11 to 0.28 (dark); raised-control borders from 0.07 to 0.22 and from 0.16 to 0.32. Border changes take effect when the panel is rebuilt.
- **Reduce motion** disables scroll and highlight animations.
- **Reduce transparency** skips glass, so the system renders the materials opaque. The HUD material opacity is forced to 1 (overriding the user's opacity setting) and the HUD high-contrast plate is dropped.

**Windows**
- System, light and dark: read `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` for windows and `SystemUsesLightTheme` for the taskbar. Listen for `WM_SETTINGCHANGE`.
- Glass maps to **Mica** for Settings and **Acrylic** for the flyout and HUDs, via `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` on Windows 11 22H2 and later. Fall back to solid fills on Windows 10 or when "Transparency effects" is off (`EnableTransparency` = 0).
- High contrast: `SPI_GETHIGHCONTRAST`; use system colours.
- Reduce motion: `SPI_GETCLIENTAREAANIMATION`.
- Accent colour: `UISettings.GetColorValue(Accent)` or the DWM colorization colour.

### 3.11 Notifications

- They are posted only if authorized (authorized or provisional); otherwise they are logged and dropped.
- The shell itself never asks for notification permission. Only feature code does (monitor alerts, Watch, Cleaner, App updates, WhatsApp downloads, the hub's Request button). **On a fresh install, keep-awake, battery and update notices are dropped until one of those has asked.**
- The permission request is the system prompt for alert and sound. Posts have no sound and a random identifier. There is no foreground-presentation handler.
- **Shell notifications:**
  - keep-awake "Session ended" ("Time is up. The Mac will sleep normally again.");
  - battery protection;
  - "Vorssaint update" / "Update available: X" (automatic checks only, once per distinct version per app run);
  - an install failure after relaunch: "The update was downloaded but could not be applied…" plus the failure code.
- One actionable category exists: the WhatsApp organizer "Undo".
- **Windows:** toast notifications. An unpackaged app needs an AppUserModelID and a Start menu shortcut, or use packaged identity. Map actions to toast buttons with activation arguments.

### 3.12 Launch at login

- **Storage.** The stored wish is `launchAtLoginWanted`. The system registration is `SMAppService.mainApp`, with states enabled, needsApproval and off. All calls run on a serial background queue.
- **Set**
  - Enabling from an unstable location (read-only volume or translocation) fails with "The app is running from a place that cannot open at login. Drag Vorssaint to the Applications folder…".
  - The wish is stored, then register or unregister runs.
  - A needs-approval result (registered but blocked) keeps the wish true and shows the guidance note.
  - If the call threw an error and the real state differs from the wish, the stored wish is set to the real state and the error is shown. A mismatch without an error is not rolled back.
- **UI** (Settings → General)
  - The switch reads on for both enabled and needs-approval; turning it off unregisters.
  - Needs-approval shows the note plus a button that opens the system's Login Items settings.
  - While a change is pending, a spinner shows and the switch is disabled. Stale replies are discarded by request id.
- **Startup repair** (never disables anything):

| System state | Stored wish | Action |
|---|---|---|
| enabled | false | adopt it as wanted |
| needsApproval | any | nothing |
| off | true, from a stable location | register again |

- **Refresh** when Settings becomes visible or the app reactivates.
- **Windows**
  - Use `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value "Vorssaint" = `"<exe>" --autostart`.
  - Or a Task Scheduler logon task for elevated scenarios, or `StartupTask` for MSIX.
  - Detect "disabled by user" via `HKCU\...\Explorer\StartupApproved\Run`; this is the needs-approval analog. Point to `ms-settings:startupapps`.
  - Keep the same repair rules.

### 3.13 Feedback

**Window:** 600×650, titled with the title hidden, closable. Contents:

- **Header:** a tinted 💬 icon, "Send feedback" (`.title2` semibold) and a "Done" button.
- A segmented control **Bug / Feature idea**.
- "What would you like to share?" with a text editor (minimum height 145, radius 9, secondary background, separator stroke).
  - The placeholder depends on the kind: "Tell me what happened and what you expected." or "Describe the idea and how it would help."
  - A counter "N of 2000 characters" turns red when over the limit.
  - Input is hard-limited to 2,000 UTF-16 units, trimmed at a character boundary.
- **"Include technical details"** toggle (default **off**) with the caption "Adds only the technical details shown below. It does not include logs."
- **"What will be sent"** box (padding 16, radius 12, quaternary at 45 %):
  - "Your chosen category and the text above."
  - When diagnostics are on: "The technical details listed below." plus a monospaced grid: Vorssaint `<version> (<build>)`, macOS `<x.y.z>`, Mac `<hw.model>`, Language `<code>`, Beta ✓/○, Update channel.
  - A privacy note and a retention note (English text in `feedback.privacyNote` and `feedback.retentionNote`).
- **Footer:**
  - an error label (red);
  - a spinner with "Sending…";
  - "Send feedback" (prominent, large, ⌘Return). It is enabled when the trimmed text is at least 10 UTF-16 units, the total is at most 2,000, and nothing is sending.
- **After success:** a sent view with a green check, "Feedback sent", "Thank you. No contact information was sent, so you will not receive a direct reply." and "Done".

**Service contract**
- `POST https://screenshots.vorssaint.com/v1/feedback` with `Content-Type: application/json`, `Accept: application/json` and `Cache-Control: no-store`.
- The session is ephemeral: no cookies, no cache, a 20 s request timeout and a 30 s resource timeout.
- **Body**, at most 8 KiB:

```json
{
  "kind": "bug" | "feature",
  "message": "<trimmed, 10…2000 UTF-16 units>",
  "diagnostics": {
    "appVersion": "...",
    "appBuild": "...",
    "macOS": "x.y.z",
    "macModel": "...",
    "language": "en-US",
    "isBeta": false,
    "updateChannel": "stable" | "beta" | "beta-opt-in" | "developer"
  }
}
```

  `diagnostics` is omitted (never sent as null) unless the toggle is on. `macModel` is omitted when the model cannot be read.
- **Response**, at most 16 KiB:

| Status | Meaning | Shown message |
|---|---|---|
| 201 | success; body `{"id": "<32 chars [A-Za-z0-9_-]>"}` (validated) | — |
| 429 | rate limited | "Too many submissions from this network. Please try again later." |
| 5xx or network error | unavailable | "Could not connect. Check your internet connection and try again." |
| anything else | rejected | "Could not send the feedback right now." |

- A 201 with an invalid id, a response body over 16 KiB (checked before the status code) or a non-HTTP response all count as an invalid response and show the generic error. A message out of range, or a body over 8 KiB, is rejected before sending.
- The diagnostics grid shows the channel capitalized, with "-" turned into a space.
- Entry points: General → Feedback card; the Beta header icon in the panel (shown only on beta builds: a version containing -beta, -rc or -alpha, or a developer build with `simulateBetaUI`); Command Bar actions ("Report a bug", "Suggest a feature"). Opening the window closes the panel and resets the form.
- **Windows:** the schema has a `macOS`/`macModel` field. Coordinate with the server owner on an `os` field (§10).

### 3.14 Diagnostics, self-test, logging, CLI

**`--selftest`** prints `SELFTEST WARNING: …` lines, then `SELFTEST OK` (exit 0) or `SELFTEST FAILED: <list>` (exit 1). It checks:
- a power assertion can be created;
- memory reading bounds (memory used = 0 is only a warning), swap reading and uptime;
- SMC temperatures (warn only);
- the symbolic-hotkey table (warn);
- AirPlay routing (warn; macOS 27 and later only);
- network counters never decrease;
- disk counters and volumes (warn);
- power metrics (warn);
- a UserDefaults round trip;
- **every keep-awake active icon renders at 26×20 without clipping**;
- **all 40 gallery symbols fit the canvas**;
- an unknown symbol falls back to the mark;
- the bundled glyph is 26×20 at 1× (a missing `MenuBarIcon.png` is only a warning).

`Defaults.register()` runs before the self-test. CI runs it after every build.

**Other CLI modes**
- `--sensors` prints SMC temperature sensors and their classification.
- `--uninstall` detaches from the system before bundle removal: it unregisters the fan helper daemon, restores sleep and the Spaces setting only when a restore is flagged as pending, and removes the login item. It prints "UNINSTALL: …" lines. The exit code reflects only whether the fan helper daemon was unregistered; `Tools/uninstall.sh` relies on it.
- Crash-safe guard helpers: `--super-key-mapping-cleanup <source>` and `--mouse-acceleration-cleanup` (§3.1).
- Developer-only: `--notch-presentation-test`, `--preview-notch-tour` and `--glass`.

**Logging.** `os_log` with subsystem = the bundle id.
- Categories include `menubar` (status item placement and recovery stages, with frame, screens and organizer), `notifications` (authorization, drops, delivery errors), `keep-awake`, `display` and `spaces-order`. Without a bundle id, the subsystem falls back to "vorssaint".
- The debug command is `log show --last 1h --predicate 'category == "menubar"'`.

**Developer build**
- Bundle id `com.vorssaint.utils.dev`, name "Vorssaint (Developer)".
- It never updates. `simulateUpdate` and `simulateBetaUI` exercise the UI, and a commit stamp shows in About.

**Windows**
- Implement `--selftest` (settings round trip, tray icon assets at all DPIs, hotkey registration probe on a free combination, sensors) for CI.
- Log to ETW or EventSource, or to a rolling file in `%LOCALAPPDATA%\Vorssaint\logs`. Keep a "tray" category with the same stages.

### 3.15 Feedback into other areas (pointers)

These parts are only referenced here and are covered by other specs: keep-awake logic; the monitor and metric rendering; the Dynamic Island (including island-hosted panel routing); every section's content; the Command Bar search; the switcher's event tap; and the clipboard window.

---

## 4. Design system

### 4.1 Units, fonts, rendering

- Treat 1 pt as 1 DIP. Windows conventions use slightly larger text than macOS (WinUI body 14 vs macOS body 13; caption 12 vs 10). Either keep the macOS sizes for a faithful look, or use the suggested Windows sizes in §4.3 for native legibility. **Decide once and apply globally.**
- **Font mapping**

| macOS | Windows |
|---|---|
| SF Pro (system) | Segoe UI Variable (Windows 11), or Segoe UI (Windows 10) |
| SF Mono / `monospaced` | Cascadia Mono, falling back to Consolas |
| `monospacedDigit` | Segoe UI Variable with tabular numbers (`font-variant-numeric: tabular-nums`, or OpenType `tnum`) |
| `design: .rounded` | no Segoe equivalent; use the regular face |

### 4.2 Colour tokens

**Panel surface tokens** (`PanelSurface`). "Primary" means the label colour: black-ish in light mode, white in dark mode.

| Token | Light | Dark | Notes |
|---|---|---|---|
| `panel.material` | system "regular material" (vibrant blur) | same | Windows: Acrylic |
| `panel.base` (over material) | white @ 0.68 | black @ 0.42 | Glass variant: × 0.35 (light) / × 0.45 (dark) |
| `panel.card` | white @ 0.38 | white @ 0.075 | cards, tab bar, footer buttons |
| `panel.control` | black @ 0.055 | white @ 0.085 | placeholders, inert controls |
| `panel.border` | black @ 0.09 (high contrast 0.24) | white @ 0.11 (high contrast 0.28) | 0.7 pt; 0.8 on footer buttons and the inset surface |
| `panel.raised.fill` | white @ 0.88 | white @ 0.14 | controls sitting on glass; defined in the panel theme but used only by the radial menu |
| `panel.raised.border` | black @ 0.07 (high contrast 0.22) | white @ 0.16 (high contrast 0.32) | radial menu only |
| `panel.raised.shadow` | black @ 0.14 | black @ 0.38 | radial menu only |
| `panel.rim` | gradient white 0.95 → 0.12 (top → bottom) | white 0.30 → 0.04 | lit edge; radial menu only |
| `tab.active.fill` | accent @ 0.13 | accent @ 0.20 | |
| `row.hover` | primary @ 0.06 | primary @ 0.06 | island: white @ 0.08 |
| `chip.fill` / `chip.pressed` | primary @ 0.07 / 0.16 | same | selected: accent (pressed 0.8) |
| `badge.hidden.fill` | primary @ 0.08 | same | |
| `beta.pill` | orange text on orange @ 0.16, stroke orange @ 0.35 | same | header pill uses @ 0.18 |
| `shortcut.hint.fill` | primary @ 0.06 | same | text tertiary |
| `progress.row.fill` | primary @ 0.06 | same | update progress |

**Semantic metric colours** (`PanelMetricColor`). These are darkened in light mode for contrast; dark mode uses the system colours.

| Name | Light (sRGB) | Dark |
|---|---|---|
| green | #00702E | systemGreen |
| cyan | #006E8A | systemCyan |
| mint | #007066 | systemMint |
| yellow | #8F5C00 | systemYellow |
| red | #AD141A | systemRed |
| orange | #AD4D00 | systemOrange |
| pink | #AD0F57 | systemPink |

**System colours used.** These are reference values for macOS 14; on Windows use the closest Fluent equivalents or keep these hex values.

| Name | Light | Dark |
|---|---|---|
| systemBlue (default accent) | #007AFF | #0A84FF |
| systemOrange | #FF9500 | #FF9F0A |
| systemRed | #FF3B30 | #FF453A |
| systemGreen | #28CD41 | #32D74B |
| systemYellow | #FFCC00 | #FFD60A |
| systemPurple | #AF52DE | #BF5AF2 |
| systemPink | #FF2D55 | #FF375F |
| systemCyan | #32ADE6 | #64D2FF |
| systemMint | #00C7BE | #63E6E2 |

- Label colour: black @ 0.85 (light) / white @ 0.85 (dark).
- Secondary: @ 0.50 / 0.55. Tertiary: @ 0.26 / 0.25. Quaternary: @ 0.10.
- **Accent = the system accent.** On Windows use the user's accent, with its Light1–3 and Dark1–3 variants for light and dark themes.

**Brand and special colours**
- Space gradient (brand badge, onboarding heroes, step tiles): linear from top-leading to bottom-trailing, opaque #1A1A1A → #0A0A0A → #000000.
- Header mark tint: #080808 (light) / #FFFFFF (dark).
- Discord: #5966F0.
- Menu bar usage-bar defaults: normal #64D2FF, elevated #FFD60A, critical #FF453A, with thresholds at 70 % and 90 %. These are user-configurable (Monitor spec).
- Battery warning: red when low, orange when early.
- Memory pressure dot: green normal, yellow warning, red critical, secondary unknown.

**Settings window colours**
- Window background: system window background (Windows: Mica, or solid `#F3F3F3` light / `#202020` dark).
- Settings card: the quaternary label colour at 0.35, about black or white at 0.035.
- Preset card: primary @ 0.05.
- Search result selection: accent @ 0.18 fill with accent text, padding 4×6; keyword rows indented 18.
- Landing highlight: accent @ 0.10 fill, accent @ 0.9 2 pt stroke, accent @ 0.5 glow at radius 14.
- Hub row hover: primary @ 0.04. Highlight: accent @ 0.10.
- Restart card: accent @ 0.12.

**HUD backdrop** (floating panels shared by other features)
- Material "HUD window", behind-window blur.
- The optional high-contrast plate is black @ 0.55 (dark) or white @ 0.50 (light). Measured worst case: 4.8:1 for white text and 5.3:1 for black text.
- Glass variant border: white @ 0.12 (dark) / black @ 0.08 (light), 0.8 pt.
- A user-set opacity fades the HUD material. Reduce transparency forces full opacity and drops the plate. The glass variant ignores the opacity setting.

**Onboarding selection colours**
- Bundle card: selected accent @ 0.12 with an accent @ 0.45 stroke, otherwise primary @ 0.05. Selection animates ease-out 0.15 s.
- Feature card: selected accent @ 0.09, otherwise primary @ 0.035.

### 4.3 Typography scale (as used)

macOS SwiftUI text styles resolve to: largeTitle 26, title 22, **title2 17**, title3 15, **headline 13 bold**, **body 13**, **callout 12**, subheadline 11, footnote 10, **caption 10**, caption2 10.

| Usage | Size / weight | Suggested Windows (Segoe UI Variable) |
|---|---|---|
| Settings page title | 17 bold (`title2`) | 20 semibold ("Subtitle") |
| Card title / headline | 13 bold | 14 semibold ("Body strong") |
| Settings row title | 13 regular | 14 regular ("Body") |
| Captions / help | 10–11 regular, secondary | 12 regular ("Caption") |
| Panel section title | 10 semibold, UPPERCASE, kerning 0.5 | 11–12 semibold, uppercase, +0.5 tracking |
| Panel row title | 12 semibold | 13 semibold |
| Panel row caption | 10 regular | 11–12 |
| Panel tab icon | 13.5 semibold | 16 px icon |
| Footer buttons | 11 medium | 12 |
| Update banner title / subtitle / button | 12 semibold / 10.5 / 11 semibold | 13 / 12 / 12 |
| Badges (BETA, EXPERIMENTAL, Hidden, OK) | 8–10.5 bold; BETA and EXPERIMENTAL uppercase, Hidden in sentence case, OK literal | 10–11 semibold |
| Shortcut hint pill | 9 semibold rounded | 11 |
| Onboarding hero title / body | 26 bold / 12 (welcome), 12.5 (done) | 28 semibold / 14 |
| Onboarding step title | 19 bold | 20 semibold |
| Tray title (if emulated) | 11.6 monospaced medium | — |
| Release notes item / paragraph | 12.5 / 12.8 | 14 |

Text uses a single line where truncation is safe; otherwise it wraps (`fixedSize(vertical)`) and is never clipped.

### 4.4 Spacing and sizes

| Element | Value |
|---|---|
| Panel width / content width / padding / stack spacing | 332 / 308 / 12 / 12 |
| Panel min height / chrome / height cap margin | 220 / 180 (+ banner + 12) / work area − 28 (min 360) |
| Panel placement margin | 8 from work-area edges |
| Section header → content | 8 |
| Card padding (panel) | 10 (island 12) |
| Row insets (panel) | 10 horizontal / 8 vertical (island 12 / 10) |
| Row icon column / gap / separator inset | 22 / 9 / 41 (+25 with drag handle) |
| Tab bar | container padding 4, spacing 2, tab height 30 |
| Footer | height 30, top padding 4, buttons min height 28, spacing 8 |
| Settings page | max width 760, padding 22, spacing 20 |
| Settings card | padding 16, inner spacing 13 |
| Settings row | icon tile 26, spacing 12, text inset 38 |
| Settings sidebar | 198–240 wide (ideal 210), row icon 18, spacing 9 |
| Settings window minimum / default | 772×528 / 772×min(838, work-area height − 40) |
| Hub preset card | min height 138, padding 12, icon tile 34 |
| Onboarding window | 540×600; heroes 220 / 300 tall |
| Feedback window | 600×650 |
| Update preview | 640×600 |
| Support intro / showcase / highlights | 560×400 / 680×600 / ≤ 600×660 |
| Recorder field width | 108 in standard rows (140 in Command Bar app shortcuts, 130 in the radial menu, 86 in the screenshot tool order) |
| Menu bar icon gallery cell | 34×30 |
| Tray glyph canvas (macOS) | 26×20 pt |

### 4.5 Corner radii

| Element | Radius |
|---|---|
| Panel surface (inset variant before macOS 26) | 18; on macOS 26 the popover's own balloon clips a plain rectangle |
| Tab bar container / tab | 12 / 8 |
| Panel card / update banner / hub row / preset bundle card | 10 |
| Footer button / metric back button / row hover / reset button / settings icon tile / shortcut chip in Settings | 7 |
| Shortcut hint pill | 5 |
| Settings card / restart card | 16 |
| Preset card / appearance choice background / feedback "what is sent" box | 12 |
| Preset icon tile / appearance thumbnail / text editor | 9 |
| Onboarding feature tile / release-note images / permission tile | 8 |
| Step header tile | 14 |
| Brand badge | 0.26 × size |
| KeyCap | 6 |
| Search result highlight | 6 |
| Chips and badges | capsule |
| Island surfaces | 18 (cards), 14 (row highlight) |

Corners are "continuous" (squircle) on macOS. On Windows use standard rounded corners, and `DWMWCP_ROUND` for windows.

### 4.6 Materials

- **Panel:** blur-behind material plus a base tint (§4.2).
  - Before macOS 26, the panel is drawn as a rounded card (radius 18) inside the popover, with a border.
  - With glass on (macOS 26 and later), it uses a glass effect plus a reduced tint.
  - With Reduce transparency on, the glass branch is skipped and the standard material plus tint is used; the app paints no opaque colour of its own, but macOS renders that material opaque.
- **HUD panels:** HUD material plus an optional plate (§4.2).
- **Windows:** the flyout uses Acrylic (`DWMSBT_TRANSIENTWINDOW`) plus the `panel.base` tint. The fallback is a solid `#F9F9F9` (light) / `#2C2C2C` (dark) fill with the `panel.border`. Settings uses Mica (`DWMSBT_MAINWINDOW`).

### 4.7 Component specs

| Component | Spec |
|---|---|
| **Toggle (switch)** | Panel rows: small switch. Settings rows: regular switch, trailing. Features hub rows: small switch. Windows: `ToggleSwitch`, with no on/off text in compact rows |
| **Slider** | Native small slider, tinted accent (amber/orange when "boosting" above 100 % in the mixer). **Glass slider variant:** track capsule 5 tall (primary 11 % light / 16 % dark); fill capsule in the tint with a 3 pt tint glow; knob capsule 24×15 (glass, tint stroke 0.36 or 0.55 when boosting, top-white gradient highlight, soft shadows); drag anywhere; accessibility step 5 %; value animation ease-out 0.16 s. Without glass, the knob falls back to the control background colour plus the tint at 0.10 (0.16 when boosting) |
| **Segmented** | Native segmented (hub tabs, feedback kind, choice rows). It falls back to a menu when it does not fit. Windows: `SelectorBar` / `RadioButtons` / segmented `ToggleButton` group |
| **Tiles (utility rows)** | §3.4.6 |
| **Cards** | Panel: radius 10, `panel.card`, 0.7 `panel.border`, padding 10. Settings: radius 16, quaternary @ 0.35 fill (about 3.5 %), padding 16 |
| **Panel section controls** | Edit button 22×18, radius 6. "OK" state 10.5 bold white on accent, 24 tall, radius 8. Reset button 22×22, primary @ 0.07. Hide/show button 24×22, fill @ 0.10. Drag handle 16×22. Collapse chevron 9 pt semibold tertiary, rotates without animation. Metric back button 24×24 with an 11 pt chevron |
| **Chips** (keep-awake) | Capsule, min height 22, flexible width, 11 pt medium tabular digits, padding 8 horizontal |
| **Badges** | BETA: 8 pt bold uppercase, tracking 0.4, orange, 16 % orange capsule plus 0.5 stroke at 35 %. Hidden: label plus eye-slash, 9.5 bold, 8 % primary capsule, padding 7×3. Feature BETA in hub: 8 bold white on accent. EXPERIMENTAL: 9 bold orange on 14 % orange |
| **Disclosure header** | The whole row toggles. The trailing chevron rotates 90° (ease-in-out 0.18). Accessibility value "Expanded"/"Collapsed". Children are indented 25 |
| **Key caps** | 12 pt semibold rounded, minimum content 20×22 plus 5 horizontal padding on each side (no vertical padding), radius 6, 7 % fill, 14 % 1 pt stroke |
| **Recorder field** | Bezel button, 13 pt medium, truncating tail. It takes the width its row offers (see §4.4). Title = shortcut display string, "None", or "Press keys" while recording ("Record…" when empty in Command Bar app shortcuts) |
| **Search field (sidebar)** | macOS 26 only: capsule at 50 % quaternary fill, magnifier, plain text field, clear button; padding 5×7, margins 10 / 8 top / 4 bottom. Other macOS versions use the native sidebar search field |
| **Appearance thumbnails** | 66 tall. Desktop gradient (light #CCE0FF → #94B8F2, dark #333857 → #121221) with a mini window (traffic lights #FF5E57 / #FFBD2E / #29C740, a sidebar and placeholder bars; window greys 0.93/0.22, 0.90/0.16, 0.99/0.19 and 0.76/0.40 for light/dark). System = light with the dark one masked to the lower-right triangle. Selected: accent 2 pt stroke and accent @ 0.08 background; unselected: primary @ 0.12, 1 pt. Title 11 medium |

### 4.8 Motion

| Interaction | Animation |
|---|---|
| Flyout open/close | system popover animation; not animated when re-anchoring between metric items |
| Control category expand | ease-out 0.15 s |
| Disclosure rows | ease-in-out 0.18 s |
| Onboarding step change | ease-in-out 0.2 s; page dots spring (response 0.3, damping 0.8) |
| Hub install/uninstall / presets / never-used | ease-out 0.22 s |
| Scroll to anchor / reveal | ease-in-out 0.3 s |
| Landing highlight fade | after 1.8 s, ease-out 0.4 s |
| Hub row highlight clear | after 1.2 s, ease-out 0.25 s |
| Search selection scroll | ease-in-out 0.2 s |
| Panel layout miniature | smooth 0.2 s |
| Drag reorder in the panel editor | ease-in-out 0.12 s |
| Mixer glass slider | ease-out 0.16 s |

Reduce motion: scroll jumps and highlight changes become instant, and the highlights-tour GIF is static. The update-showcase media still plays.

### 4.9 Accessibility behaviors to keep

- Tabs, the hide/show button and the search clear button have accessibility labels. Selected tabs expose a selected trait. The header feedback button and the section edit and reset buttons have only tooltips, and the metric back button has neither. **On Windows, give every icon-only button a label.**
- Captions become tooltips plus accessibility help when hidden.
- Disabled controls that carry an explanation get the tooltip on a wrapper, because a disabled control does not show tooltips.
- Combined accessibility elements are used for hub rows and preset cards.
- A minimum 4.5:1 text contrast is targeted with the HUD plate.
- High-contrast borders apply.
- Localized list sorting uses locale-aware comparison.

### 4.10 Icon mapping (SF Symbols → Windows)

- **Columns.** "Segoe" means the Segoe Fluent Icons glyph name and codepoint; the same codepoint is in Segoe MDL2 Assets for Windows 10. "Fluent" means the base name in **Fluent UI System Icons** (MIT, e.g. `ic_fluent_<name>_20_regular`/`_filled`), which works for any stack.
- **Verification.** Every Segoe name and codepoint below was checked against Microsoft's Segoe Fluent Icons and Segoe MDL2 Assets pages, and every Fluent name against the font JSON in the official `microsoft/fluentui-system-icons` repository. All codepoints are also in Segoe MDL2 Assets except where a row says "Windows 11 only". "—" means there is no good glyph; use the Fluent icon or a custom SVG.
- **Caveats.**
  - Some Fluent icons exist only in a few sizes: `PanelBottom` and `WindowConsole` in 20 only; `ControlButton` and `Sleep` in 20 and 24.
  - In the Fluent icon *font*, a few size-20 glyphs sit above U+FFFF (Calendar, ArrowBidirectionalLeftRight, Hexagon, LayoutCellFour, LayoutColumnThree, CommentQuote, AnimalPawPrint). Addressing them by codepoint needs surrogate pairs; SVGs or the icon packages avoid this.
  - E713 is named "Settings" in Segoe Fluent Icons and "Setting" in Segoe MDL2 Assets.
- **Recommendation.** Standardize on Fluent UI System Icons, which have broader coverage, and use Segoe only where the native shell feel matters (tray menu, flyout footer).

**FeatureCatalog `symbolName` (all features)**

| SF Symbol | Used by | Segoe | Fluent |
|---|---|---|---|
| `rectangle.on.rectangle` | switcher (also settings, onboarding) | TaskView E7C4 | `WindowMultiple` |
| `dock.rectangle` | dockPreview | — | `PanelBottom` |
| `dock.arrow.down.rectangle` | dockClick | ChromeMinimize E921 | `ArrowMinimize` |
| `arrow.up.left.and.arrow.down.right` | windowMaximizer | FullScreen E740 | `ArrowMaximize` |
| `rectangle.3.group` | windowLayout | ViewAll E8A9 | `LayoutCellFour` |
| `xmark.rectangle` | autoQuit | ChromeClose E8BB | `DismissSquare` |
| `rectangle.split.3x1` | spacesOrder (→ virtual desktops) | TaskView E7C4 | `LayoutColumnThree` |
| `arrow.up.arrow.down` | scrollInverter | Sort E8CB | `ArrowSort` |
| `arrow.triangle.swap` | scrollHorizontal | Switch E8AB | `ArrowSwap` |
| `cursorarrow.and.square.on.square.dashed` | focusFollowsMouse | — | `CursorHover` |
| `cursorarrow.motionlines` | smoothScroll | Mouse E962 | `Cursor` |
| `arrow.up.and.down.text.horizontal` | linearScroll | — | `ArrowBidirectionalUpDown` |
| `cursorarrow.rays` | mouseAcceleration | — | `TopSpeed` |
| `arrow.left.arrow.right` | mouseNavigation | — | `ArrowBidirectionalLeftRight` |
| `button.programmable` | mouseButtonShortcuts | Mouse E962 | `ControlButton` |
| `hand.tap` | middleClick | TouchPointer E7C9 | `HandRight` / `TapSingle` |
| `cursorarrow.click` | mouseClickDebounce (catalog and panel row). Related: `cursorarrow.click.2` (middle-click panel row, keep-awake right-click row), `cursorarrow.click.badge.clock` (click filter row in Settings) | — | `CursorClick` |
| `keyboard` | keyboardDebounce (also metrics, tiles) | KeyboardClassic E765 | `Keyboard` |
| `text.append` | textSnippets | — | `TextExpand` |
| `capslock` / `command` / `option` / `control` / `shift` | superKey (by source key) | KeyboardClassic E765 | `Keyboard` / `KeyboardShift` |
| `shield.lefthalf.filled` | quitWindowProtection | Shield EA18 | `Shield` |
| `doc.on.clipboard` | clipboardHistory | Paste E77F | `ClipboardPaste` |
| `doc.plaintext` | pastePlain | Document E8A5 | `DocumentText` |
| `scissors` | finderCutPaste | Cut E8C6 | `Cut` |
| `pencil` | finderRename | Rename E8AC | `Rename` |
| `tray.full` | shelf | — | `Archive` |
| `link` | urlCleaner | Link E71B | `Link` |
| `externaldrive.badge.plus` | diskImageInstaller (macOS DMG concept) | — | `HardDrive` |
| `speaker.wave.2` (`.fill`) | mixer, Sound group | Volume E767 | `Speaker2` |
| `hifispeaker` | soundOutputSwitcher | Speakers E7F5 | `SpeakerBluetooth` / `Speaker2` |
| `list.number` | audioPriority | BulletedList E8FD | `TextNumberListLtr` |
| `mic.slash` | micMute | MicOff EC54 | `MicOff` |
| `music.note` | musicBlock | MusicNote EC4F | `MusicNote2` |
| `moon.zzz.fill` | keepAwake | QuietHours E708 | `WeatherMoon` / `Sleep` |
| `display.2` | brightness | TVMonitor E7F4 | `Desktop` |
| `sun.max.fill` | extraBrightness | Brightness E706 | `BrightnessHigh` |
| `wave.3.right.circle` | bluetoothSleep | Bluetooth E702 | `Bluetooth` |
| `wand.and.rays` | quickLauncher (Quick panel) | AllApps E71D | `Wand` |
| `togglepower` | quickToggles | — | `ToggleMultiple` |
| `eyedropper` | colorPicker | Eyedropper EF3C | `Eyedropper` |
| `text.viewfinder` | screenOCR | — | `ScanText` |
| `bubbles.and.sparkles` | cleaningMode | — | `Broom` |
| `photo.on.rectangle.angled` | mediaTools | Picture E8B9 | `ImageMultiple` |
| `sparkles` / `sparkle` | cleaner, notchAgents, What's New | — | `Sparkle` |
| `trash` | uninstaller | Delete E74D | `Delete` |
| `shippingbox` | homebrew (→ winget) | Package E7B8 | `Box` |
| `arrow.down.app` | appUpdates | Download E896 | `ArrowDownload` |
| `camera.viewfinder` | screenshot | Camera E722 | `Screenshot` |
| `record.circle` / `stop.circle` | screenRecorder | Record E7C8 / Stop E71A | `Record` / `RecordStop` |
| `web.camera` | cameraPreview | Webcam E8B8 | `Camera` |
| `photo.on.rectangle` | wallpaper | Personalize E771 | `ImageMultiple` |
| `circle.grid.cross` | radialMenu | — | `GridDots` (or custom) |
| `note.text` | scratchpad | QuickNote E70B | `Note` |
| `command` | commandBar (avoid ⌘ on Windows) | Search E721 | `Search` |
| `xmark.octagon` | killProcess | Cancel E711 | `ErrorCircle` |
| `network` | portManager, monitorNetwork, network section | Network E968 | `Globe` / `PlugDisconnected` |
| `macbook` | notch (Dynamic Island) | DeviceLaptopNoPic E7F8 | `Laptop` |
| `calendar` | notchCalendar | Calendar E787 | `Calendar` |
| `bell` | notchNotifications | Ringer EA8F | `Alert` |
| `hand.draw` | notchGestures | TouchPointer E7C9 | `HandDraw` |
| `timer` | notchTimer | Stopwatch E916 | `Timer` |
| `battery.25percent` | notchAccessories | Battery2 E852 | `Battery2` |
| `quote.bubble` | notchLyrics | Message E8BD | `CommentQuote` / `Chat` |
| `list.bullet` | notchQueue | BulletedList E8FD | `TextBulletListLtr` |
| `waveform` | notchLiveEqualizer, audio capture | Audio E8D6 | `Pulse` |
| `arrow.down.circle` | notchDownloads | Download E896 | `ArrowCircleDown` |
| `eye` | notchWatch | View E890 | `Eye` |
| `face.smiling` | notchMascot | Emoji2 E76E | `EmojiSmileSlight` |
| `cpu` | monitorCPU, system section | — | `DeveloperBoard` |
| `rectangle.connected.to.line.below` | monitorGPU | — | `DeveloperBoard` (no GPU glyph) |
| `memorychip` | monitorMemory | — | `Ram` |
| `internaldrive` (`.fill`) | monitorDisk, disk section | HardDrive EDA2 | `Storage` |
| `bolt.fill` | monitorPower, power section, energy page | LightningBolt E945 | `Flash` |
| `cable.connector` | connectedDevices | USB E88E | `UsbPlug` |
| `fanblades.fill` / `fanblades` | fanControl | — | — (custom fan SVG) |

**Shell and chrome symbols**

| SF Symbol | Used for | Segoe | Fluent |
|---|---|---|---|
| `gearshape` / `gearshape.fill` | Settings footer, General | Settings E713 | `Settings` |
| `gearshape.2` (`.fill`) | automation permission; installing | Processing E9F5 | `Settings` |
| `power` | Quit | PowerButton E7E8 | `Power` |
| `wrench.and.screwdriver(.fill)` | Utilities tab, Tools group, Advanced | Repair E90F | `WrenchScrewdriver` |
| `switch.2` | Controls tab | ToggleLeft F19E (Windows 11 only) | `ToggleLeft` |
| `chevron.left/right/down` | navigation, disclosure | ChevronLeft E76B / ChevronRight E76C / ChevronDown E70D | `ChevronLeft/Right/Down` |
| `chevron.backward/forward` | Settings history | Back E72B / Forward E72A | `ArrowLeft/Right` |
| `slider.horizontal.3` | section edit; "Changed" notes | Filter E71C | `Options` |
| `checkmark` | edit "OK" | CheckMark E73E | `Checkmark` |
| `checkmark.circle.fill` | granted, success, "Fixed" | CompletedSolid EC61 | `CheckmarkCircle` (filled) |
| `arrow.counterclockwise` | reset | Undo E7A7 | `ArrowCounterclockwise` |
| `arrow.clockwise.circle.fill` | restart card | Refresh E72C | `ArrowClockwise` |
| `arrow.triangle.2.circlepath` | checking | Sync E895 | `ArrowSync` |
| `line.3.horizontal` | drag handle | GlobalNavButton E700 | `ReOrderDotsVertical` |
| `eye.fill` / `eye.slash.fill` / `eye.slash` | show/hide item; Dock-click hide | View E890 / Hide ED1A (Windows 11 only; on Windows 10 use PasswordKeyHide E9A9) | `Eye` / `EyeOff` |
| `hand.raised.fill` / `hand.raised` | Grant access; feedback privacy | Permissions E8D7 | `HandRight` |
| `arrow.up.forward.square` | open accessory | OpenInNewWindow E8A7 | `Open` |
| `arrow.down.circle.fill` | update banner, available | Download E896 | `ArrowCircleDown` (filled) |
| `bubble.left.and.text.bubble.right(.fill)` | feedback | Feedback ED15 | `ChatMultiple` |
| `xmark.circle.fill` | clear field | Clear E894 | `DismissCircle` |
| `magnifyingglass` | search | Search E721 | `Search` |
| `exclamationmark.circle.fill` | missing permission | ErrorBadge EA39 | `ErrorCircle` |
| `exclamationmark.triangle.fill` | failed | Warning E7BA | `Warning` |
| `info.circle` | About, notes | Info E946 | `Info` |
| `heart.fill` | Support | HeartFill EB52 | `Heart` (filled) |
| `star.fill` | Essentials preset, GitHub star | FavoriteStarFill E735 | `Star` (filled) |
| `cup.and.saucer.fill` | coffee button; keep-awake style | — | `DrinkCoffee` |
| `lightbulb.fill` / `moon.fill` / `eye.fill` | keep-awake styles | — / QuietHours E708 / View E890 | `Lightbulb` / `WeatherMoon` / `Eye` |
| `mic.slash.fill` | tray mute badge | MicOff EC54 | `MicOff` (filled) |
| `macwindow.on.rectangle` | Windows preset, group | TaskView E7C4 | `WindowMultiple` |
| `computermouse` | Mouse group, page | Mouse E962 | `Cursor` |
| `battery.75percent` / `battery.100` | Battery preset; metrics | Battery7 E857 / Battery10 E83F | `Battery7` / `Battery10` |
| `chart.line.uptrend.xyaxis` | Monitor page, group | AreaChart E9D2 | `DataTrending` / `DataLine` |
| `square.grid.2x2` | Features page, quick panel tile | AllApps E71D | `Grid` |
| `menubar.rectangle` / `menubar.arrow.up.rectangle` | Menu bar settings; onboarding done | — | `PanelBottom` |
| `filemenu.and.selection` | Cut & paste page | Cut E8C6 | `DocumentEdit` |
| `clock` / `clock.arrow.circlepath` | battery time; recent captures, periodic | Recent E823 / History E81C | `Clock` / `History` |
| `powerplug.fill` | power metric | — | `PlugConnected` |
| `square.and.arrow.up` / `.down` | export / import | Export EDE1 / Import E8B5 | `ArrowUpload` / `ArrowDownload` |
| `lock.slash` | clear permissions | Unlock E785 | `LockOpen` |
| `accessibility` | permission | EaseOfAccess E776 | `Accessibility` |
| `rectangle.dashed.badge.record` | screen recording permission | Record E7C8 | `Record` |
| `externaldrive.badge.person.crop` / `folder.badge.person.crop` | Full Disk / Files permissions | Permissions E8D7 / Folder E8B7 | `ShieldLock` / `Folder` |
| `bell.badge` | notifications permission | Ringer EA8F | `AlertBadge` |
| `mic` / `camera` / `app.badge` | permissions | Microphone E720 / Camera E722 / AllApps E71D | `Mic` / `Camera` / `AppsAddIn` |
| `leaf` / `hand.point.up.left` | energy idle / pointer | LeafTwo F1E8 (Windows 11 only) / TouchPointer E7C9 | `LeafOne` / `HandLeft` |
| `key.horizontal.fill` | onboarding permissions | Permissions E8D7 | `Key` |
| `gauge.with.dots.needle.50percent` | onboarding | — | `Gauge` |
| `sparkles.rectangle.stack` | onboarding purpose | — | `Sparkle` |
| `text.alignleft` | feedback "what is sent" | AlignLeft E8E4 | `TextAlignLeft` |
| `plus.circle.fill` | "Added" notes | Add E710 | `AddCircle` |
| `circle` / `circle.fill` | unselected; fallback glyph | — | `Circle` |
| `play.circle` / `display` / `lock.fill` | keep-awake options | Play E768 / TVMonitor E7F4 / Lock E72E | `PlayCircle` / `Desktop` / `LockClosed` |

**Menu bar icon gallery** (Settings → Menu bar; user-selectable tray glyphs). Suggested Fluent equivalents:

- `bolt.fill` → `Flash`; `star.fill` → `Star`; `heart.fill` → `Heart`; `flame.fill` → `Fire`; `sparkles` → `Sparkle`; `leaf.fill` → `LeafTwo`
- `drop.fill` → `Drop`; `snowflake` → `WeatherSnowflake`; `sun.max.fill` → `WeatherSunny`; `moon.stars.fill` → `WeatherMoon`; `cloud.fill` → `Cloud`; `mountain.2.fill` → custom
- `circle.fill` → `Circle`; `square.fill` → `Square`; `triangle.fill` → `Triangle`; `diamond.fill` → `Diamond`; `hexagon.fill` → `Hexagon`; `seal.fill` → `Ribbon`
- `circle.lefthalf.filled` → `CircleHalfFill`; `circle.hexagongrid.fill` → custom; `infinity` → custom
- `command` → drop on Windows; `cpu.fill` → `DeveloperBoard`; `memorychip.fill` → `Ram`; `gauge.with.dots.needle.67percent` → `Gauge`; `fanblades.fill` → custom
- `gearshape.fill` → `Settings`; `terminal.fill` → `WindowConsole`; `waveform` → `Pulse`; `wand.and.stars` → `Wand`; `key.fill` → `Key`; `crown.fill` → `Crown`
- `gamecontroller.fill` → `XboxController`; `headphones` → `Headphones`; `music.note` → `MusicNote2`; `paperplane.fill` → `Send`; `pawprint.fill` → `AnimalPawPrint`
- `cat.fill` → `AnimalCat`; `hare.fill` → `AnimalRabbit`; `tortoise.fill` → `AnimalTurtle`

### 4.11 Brand assets (`Resources/Brand`)

| File | Content | Use |
|---|---|---|
| `logo.png` (1.3 MB, 1024×1024) | the mark master: a black-hole/Saturn-like planet with rings (about 1.97:1 once trimmed) | `MakeIcon.swift` trims the mark to its ink, then derives the menu-bar template glyph (`MenuBarIcon.png` on a fixed 26×20 pt canvas, `@2x` 52×40; mark 12.5 pt tall, centred, 1 pt low) and `BrandMark.png` (640 px wide; header, onboarding, About badge) |
| `AppIcon-Default.png` (1.2 MB, 1024×1024) | exported default rendition of the adaptive icon | iconset PNGs at 16–1024 px; the ICNS holds 32–1024 px (no 16 px entry) |
| `AppIcon.icon/` | Icon Composer project: `icon.json` plus `Assets/vorssaint-brandmark.svg` | compiled by `actool` into `Assets.car` when full Xcode is present |

Other assets:
- `Resources/Gifs`: `highlights-notch.gif` (tour) and `commandBar.gif`.
- `Resources/Images`: `menu-bar-temperature-metrics.png` (the one image the release notes use), `discord-symbol.svg` (white glyph), and seven legacy `highlights-*` images that no code references.
- `docs/assets/readme`: `logo.svg` and `logo-dark.svg`.

**Windows**
- Produce `.ico` files with 16/20/24/32/40/48/64/256 sizes.
- Tray icons: a square, simplified mark (the planet without the wide rings, or a cropped ring) as a monochrome glyph for light and dark taskbars, plus tinted variants.
- Store or MSIX logos: Square44, Square150, Wide310, StoreLogo.
- **Branding is trademark-restricted** (§10).

---

## 5. Localization extraction plan

### 5.1 How strings are stored (macOS)

1. **Main catalog: `struct Strings`** in `Core/Localization.swift`.
   - 1,057 `let <key>: String` fields, grouped by `// MARK:` comments, with no closures, functions or computed members.
   - Instances: `extension Strings { static let ptBR = Strings(...) }` and `static let enUS = Strings(...)` live in `Localization.swift`. The other 13 languages live in `Core/Localizations/Strings+<Language>.swift` (ChineseSimplified, ChineseTraditionalHK, ChineseTraditionalTW, French, German, Italian, Japanese, Korean, Russian, Slovak, Spanish, Turkish, Ukrainian) as memberwise initializer calls with labelled arguments.
   - Access: `Strings.localized(language)` (a switch), or `L10n.shared.s` for the current language.
2. **73 feature catalogs:** `struct XxxStrings { let …: String }`, one per feature or area (`FeatureHubStrings`, `GeneralSettingsStrings`, `ScreenshotFeatureStrings` and so on). Language instances are provided in one of three ways:
   - `static let enUS = Xxx(...)`, `static let ptBR = …` (55 types), selected by a switch inside `FeatureStrings.<name>(_ language:)`, or for three of them (MediaImageConverterStrings, WhatsAppOrganizerStrings, WhatsAppDownloadStrings) inside their own `localized(_:)`;
   - or built per language inside `static func localized(_ language:)` or `FeatureStrings.<name>(_:)` as `switch language { case .enUS: return Xxx(...) … }` (18 types: GraphScale, NotchActivity, NotchCalendar, NotchEditor, NotchFiles, NotchGesture, NotchLockScreen, NotchLowBattery, NotchMascot, NotchMusicExtras, NotchNotification, NotchTour, NotchWatch, PointerDisplay, RecorderExport, SettingsNavigation, ShelfPromiseDelivery, WindowDirectional);
   - or, for WhatsAppDownloadStrings, through a private builder plus a nested private `OperationalStrings` struct for de, fr, it, tr, ru, ja, ko and zh-*;
   - some languages **share an instance** (`case .zhTW, .zhHK: return .zhTW`; zh-TW and zh-HK also share the WhatsApp operational strings). No field falls back to English inside a translation.
3. **The `FeatureStrings` namespace** (`enum FeatureStrings` plus `extension FeatureStrings { static func … }` across `Core/*Strings.swift`) has 65 factories `FeatureStrings.<name>(AppLanguage)`. Eight more catalogs are reached via their own `X.localized(AppLanguage)`: MediaImageConverter, WindowDirectional, PointerDisplay, GraphScale, NotchLowBattery, WhatsAppOrganizer, ShelfPromiseDelivery, SettingsNavigation.
4. **Helper methods inside catalogs** only *compose existing fields*. They add no text, but **their logic must be ported**:
   - `enableFeature(_:) = String(format: enableFeatureFormat, title)`;
   - `NotchMascotStrings.moment(_:language:)`, which pulls titles from other catalogs;
   - `SuperKeyStrings.sourceLabel`, `KeepAwakeAutomationStrings.activeStatus(for:)`, `NotchAgentStrings.tokens(_:)` and about 45 others in 12 files.
5. **Runtime-composed catalog:** `ShelfTooltipStrings` is built at runtime from `Strings` fields plus the language's `CountAgreement`.
6. **Outside the catalogs** (these must be handled manually):
   - `AlertSoundStrings`: private `[String: String]` maps of macOS alert-sound display names for 10 languages; ja, ko and zh use English. **macOS-only; drop.**
   - `Resources/<lang>.lproj/InfoPlist.strings`: 14 files with 11 macOS permission-prompt texts each. **macOS-only.** Reuse the wording for Windows privacy text if needed.
   - **Inline per-language `switch` functions** (3 hold copy: 4 strings × 15 languages = 60 slots, 56 distinct values, because zh-TW and zh-HK share each one):
     - `MenuPanelView` battery-protection note: "Battery at \(percent)%…" (the only one that interpolates);
     - `MenuPanelView` chip hint: "Click a chip to start. Click it again to stop";
     - `KeepAwakeEndTimePicker` wheel labels "Hour (0–23)" and "Minute" (VoiceOver only).
     - A fourth switch, `CommandBarSystemSettingsSupport.resourceFolder(for:)`, holds no UI text: it maps languages to Apple `.lproj` folder names.
   - **Hard-coded literals** (not localized): "OK" (panel edit), "Esc" key caps, "PDF", "PNG", "JPEG", "HEIC", "MB/s", "Mbps", "\(n) min" (mouse jiggle interval), the tray countdown ("H:MM", "N min", "∞"), the "Vorssaint", "macOS" and "Mac" diagnostic labels, the brand, and English search keywords such as "force quit", "port" and "debounce" that are deliberately not translated.
   - **Runtime composition:**
     - `"\(permissionRequired): \(permissionName)"`;
     - `"\(versionPrefix) \(version)"`;
     - three space-joined prefix fields: `statusActiveUntil` + time, `keepAwakeEndsIn` + duration, `updateAvailablePrefix` + version. (31 `Strings` fields carry a `// +` comment, but the other 28 are printf formats whose comment only lists the arguments.)
     - `"+" + shortDuration`;
     - list joins with `", "`;
     - `.uppercased()` on section and category titles, without a locale;
     - `DateComponentsFormatter` abbreviated units ("15m", "1h"), localized by the OS;
     - `Date.FormatStyle` with the formatting locale.
   - **`CHANGELOG.md`** is English only and is shown in-app on the What's New page (and in the developer build's simulated update preview). The real update preview shows the GitHub release body, which is also English.
7. **Plurals.** There is no CLDR. Each grammatical form is its own field (for example `shelfTooltipImageSingular`, `…Few`, `…Plural`). Only the Shelf pile tooltip uses `AppLanguage.countAgreement` to select the form; every other singular/plural pair (cut feedback, URL-cleaner rule counts, connected devices) uses `count == 1` in every language. The rules:
   - `oneAndMany`: 1 vs other;
   - `byLastDigits` (ru, uk): last digit 1 → one, 2–4 → few, except 11–14 → many;
   - `byWholeNumber` (sk): 1 → one, 2–4 → few, else many.
8. **Format strings** use Apple printf syntax: `%@`, `%d`, `%.2f`, positional `%1$@`/`%2$d`/`%3$d`, and `%%` (no `%s` placeholder is used; the only "%s" are literal tokens in two screenshot captions). **181** keys contain placeholders: 163 named `…Format` and 18 not. `_meta.isFormat` flags 187, because it follows the macOS tests' rule (name contains "format" or English has arguments), which adds 6 plain labels such as `Strings.mediaFormat` "Format". **Not every `%` is a placeholder:** some captions document literal tokens (`%y %mo %#`) or contain "100 %". There is no reliable naming convention. Most format keys end in `Format`, but not all of them; for example `shelfTooltip*`, `weekNumber`, `sessionProgress`, `shortcutTakeOverOffer` and `switcherIconRowMode` are formats too.
9. **Validation that already exists** in the macOS test harness (`Tests/LocalizationTests.swift`, `LocalizationFeatureContractTests.swift`):
   - every catalog's field set must equal English;
   - no empty values;
   - no em dashes;
   - format argument positions and types must match English;
   - native quotation marks per language, checked **only on the main `Strings` catalog** (« » if and only if fr, ru or uk; „ if and only if de or sk);
   - the ellipsis character instead of three dots, checked **only on the 7 `Strings.homebrewOperation*` labels**.
   
   The schema, empty-value, em-dash and format checks cover `Strings`, the 65 factories and 7 standalone catalogs (`SettingsNavigationStrings` is not included). Feature factories are discovered by regex in `Tests/generate_sources.py`. Current data would fail global versions of the quote and ellipsis rules (for example the es `saveInSubfolder` uses «Converted», and two snippets captions contain "-tz(...)" in all 15 languages).

### 5.2 Extraction approach (implemented and verified)

**Principle.** Compile the real Swift catalogs with a tiny generated `main.swift` that walks every catalog instance for every `AppLanguage.allCases` using **`Mirror`**, and dumps JSON. This resolves shared instances, private builders and switch-built catalogs exactly as the app sees them, with no parsing ambiguity.

**Prototype** (in `05-app-shell-assets/tools/`; read-only against the repo; about 25 s end to end on an M-series Mac):

1. `generate.py <repo> <out>` does two things:
   - **Sources:** it reuses `build.sh`'s `TEST_SOURCES` array (the mostly headless set the unit-test binary compiles: 308 files, including 16 UI files), minus the `Tests/` and `build/` entries.
   - **Factories:** it applies the same regex as `Tests/generate_sources.py`, i.e. every `static func name(_ x: AppLanguage) ->` inside `extension/enum FeatureStrings {…}`, giving 65. It adds the 8 standalone `localized(_:)` catalogs and `Strings.localized`, then emits a `main.swift` with one `add("<catalog>") { Factory($0) }` line per catalog.
2. `Stubs.swift` provides 3 tiny stand-ins (`NotchPanel`, `NotchActivationButton`, `NotchNotice.minimumWing`). A few support files reference these app-only types; the test harness normally extracts them via `generate_sources.py`.
3. Compile with `swiftc -Onone -enable-batch-mode`, the target `arm64-apple-macosx14.0`, the same pinned SDK and compat flags as `build.sh`, `-I Sources/VMStatisticsCompat -I Sources/HIDEventSystem` and **an explicit `-output-file-map`**. Without the file map, the driver's temporary objects went missing at link time in this environment.
4. Run `locextract strings.json`. The output shape is `{ catalog: { lang: { key: value } } }`, and any non-`String` field is reported. There are currently none.
5. `flatten.py strings.json i18n/` writes:
   - `i18n/<lang>.json`, with dotted keys: `Strings.menuSettings`, `hub.pageTitle`, `screenshot.fileNamePatternCaption` (the `FeatureStrings.` prefix is dropped);
   - `i18n/_meta.json`, which records per key the catalog, the field, `isFormat`, the `args` signature (for example `["int","int"]`) and `sameAsEnglishIn`.
   
   Values are kept **verbatim** (printf). An optional `--icu` flag converts likely format strings to `{0}` placeholders. A small `LITERAL_PERCENT` allow-list excludes captions that contain a literal `%`.
6. `tools/run.sh <repo> <out>` runs everything, and also builds the **settings schema** dumper (§7).

**Verified results at this revision**

| Measure | Value |
|---|---|
| Catalogs | 74 (`Strings` + 65 factories + 8 standalone) |
| Keys per language | **3,253** (1,057 in `Strings` + 2,196 in feature catalogs) |
| Total values | 3,253 × 15 = **48,795** |
| Format strings | 181 keys with placeholders (187 flagged `isFormat`); **0 placeholder mismatches** across languages, after the literal-% allow-list |
| Values identical to English, per language | pt-BR 88, tr 44, ru 31, es 68, sk 63, de 114, fr 143, it 75, ja 35, ko 33, uk 35, zh-Hans 34, zh-TW 36, zh-HK 36 |
| Keys identical in all 14 translations | 20 |

Of the 835 language/key pairs identical to English, 816 are 12 characters or shorter: brand or technical terms (CPU, PID, USB, App Store, Liquid Glass) and real cognates (fr "minutes", sk "1 parameter"). None is an untranslated English sentence. They are listed per key in `_meta.json` for review.

**Largest catalogs**

| Catalog | Keys |
|---|---|
| Strings | 1,057 |
| screenshot | 196 |
| commandBar | 166 |
| recorder | 136 |
| hub | 117 |
| radialMenu | 102 |
| notchAgents | 93 |
| windowLayout | 90 |
| notch | 88 |

**Estimate of the total string count**
- About **48.8k** catalog values.
- Plus 56 inline per-language values (4 strings in 3 functions × 15 languages, with zh-TW and zh-HK shared).
- Plus about 15 hard-coded literals.
- Plus 154 macOS-only InfoPlist values (11 × 14).
- Plus the alert-sound names (macOS-only).

Many keys are macOS-specific (Finder, Dock, Spaces, notch, TCC). Expect **roughly 40–60 %** to survive into a Windows build once features are re-scoped, and new Windows-specific strings to be added.

### 5.3 Recommended Windows localization architecture

- **Keys.** Keep the dotted keys exactly. They are stable identifiers: the Swift property names, which are never renamed casually.
- **Format.** Use one JSON or RESX resource per language. For WinUI, generate `.resw` from the JSON in a build step. For Electron, Flutter or Avalonia, load the JSON directly.
- **Formatting.** Port a **printf-compatible formatter** (`%@`, `%d`, `%.Nf`, `%n$…` and `%%` are used today; supporting `%s`, `%i` and `%u` too costs nothing). Apply it **only** at call sites that used `String(format:)`. This avoids corrupting literal `%` text. Alternatively, run `--icu` and use `{0}` placeholders with .NET `string.Format` or ICU MessageFormat; review the `isFormat` list first.
- **Plurals.** Port the `CountAgreement` 3-form rule as is (simplest, matches the existing data). New strings can use ICU plural syntax.
- **Live switching.** Use an observable `Localizer` with a `CurrentLanguage` property. All bindings re-evaluate when it changes.
- **Fallback.** If a key is missing in a language, fall back to en-US, and log it in debug builds.
- **CI checks** (port the macOS harness rules):
  - same key set per language;
  - non-empty values;
  - placeholder signature equal to `_meta.args` for `isFormat` keys;
  - no em dashes;
  - per-language quote style (« » only in fr, ru and uk; „ only in de and sk), scoped as on macOS or after cleaning the known exceptions;
  - the ellipsis character (same caveat).
- **Re-extraction.** Run `tools/run.sh` against any newer macOS revision and diff the JSON to bring macOS copy changes across. Keep a mapping file that says which keys the Windows build uses.

### 5.4 Gotchas checklist

- [ ] Shared instances and private builders: the extractor already resolves them. Do not "fix" the values identical to English blindly; they are terms and cognates.
- [ ] Helper methods that compose fields (about 50) must be re-implemented. They are listed in `Core/*Strings.swift` as `func` and computed `var` members.
- [ ] The 3 inline `switch language` functions with copy, and the hard-coded literals, need new keys.
- [ ] The 3 space-joined prefix fields and the `permissionRequired` / `versionPrefix` compositions fix the word order. Consider converting them to format strings on Windows.
- [ ] Only the Shelf tooltip uses the 3-form plural rule; other counted strings use `count == 1` even in ru, uk and sk. Fix these when porting.
- [ ] `.uppercased()` is applied to section titles; use invariant or culture-aware uppercasing consistently. Turkish dotted I: search uses no locale, so mirror that.
- [ ] Locale-aware sorting for displayed lists (`localizedStandardCompare`).
- [ ] Duration abbreviations and times come from the OS formatter: use Windows APIs with the formatting locale.
- [ ] The Search index uses localized control labels as keywords. Rebuild it on language change.
- [ ] macOS terminology in strings ("menu bar", "Mac", "⌘", "System Settings", "Finder", "Dock") needs Windows-specific copy ("system tray", "PC", "Ctrl", "Windows Settings", "File Explorer", "taskbar"). **This is a translation pass, not a mechanical replace.**

---

## 6. Global shortcuts

### 6.1 Roles: default table

"mac default" is what ships. The "Required enable keys" column lists the keys that must **all** be true for the shortcut to register; the default value of each key is in parentheses. The Windows column is a **suggestion** that must be validated (§6.4). Storage: `<modifiers>:<macOS key code>`.

| Role | Feature | Title (EN) | Storage key | mac default | Stored value | Required enable keys (defaults) | Suggested Windows default |
|---|---|---|---|---|---|---|---|
| keepAwake | keepAwake | Keep awake | `keepAwakeShortcut` | ⌃⌥⌘K | `control+option+command:40` | `hotkeyEnabled` (true) | Ctrl+Alt+Win+K |
| shelf | shelf | Shelf | `shelfShortcut` | ⌃⌥⌘D | `…:2` | `shelfEnabled` (absent→false), `shelfShortcutEnabled` (true) | Ctrl+Alt+Win+D |
| switcher | switcher | Apps | `switcherShortcut` | ⌘Tab | `command:48` | `switcherEnabled` (true); event tap, needs Accessibility | Alt+Tab via hook (deliberately overrides the reserved system combination) |
| switcherWindow | switcher | Windows | `switcherWindowShortcut` | ⌘\` | `command:50` | `switcherEnabled` | Alt+\` (hook) |
| clipboard | clipboardHistory | Clipboard | `clipboardHistoryShortcut` | ⌃⌥⌘V | `…:9` | `clipboardHistoryEnabled` (false), `clipboardHistoryShortcutEnabled` (true) | Ctrl+Alt+Win+V (collides with PowerToys Advanced Paste when installed) |
| soundOutputSwitcher | soundOutputSwitcher | Output switcher | `soundOutputSwitcherShortcut` | ⌃⌥⌘S | `…:1` | `soundOutputSwitcherEnabled` (false) | Ctrl+Alt+Win+S |
| pastePlain | pastePlain | Paste as plain text | `pastePlainShortcut` | ⌥⇧⌘V | `option+shift+command:9` | `pastePlainEnabled` (false) | Ctrl+Shift+Alt+V |
| finderRename | finderRename | Rename shortcut | `finderRenameShortcut` | F2 (no modifier) | `:120` | `finderRenameEnabled` (false); event tap, needs Accessibility | n/a (Explorer has F2) |
| colorPicker | colorPicker | Color picker | `colorPickerShortcut` | ⌃⌥⌘C | `…:8` | `colorPickerShortcutEnabled` (false) | Ctrl+Alt+Win+C |
| screenOCR | screenOCR | Copy text from screen | `screenOCRShortcut` | ⌃⌥⌘T | `…:17` | `screenOCRShortcutEnabled` (false) | Ctrl+Alt+Win+T |
| micMute | micMute | Mute microphone | `micMuteShortcut` | ⌃⌥⌘M | `…:46` | `micMuteShortcutEnabled` (false) | Ctrl+Alt+Win+M |
| quickLauncher | quickLauncher | Quick panel | `quickLauncherShortcut` | ⌃⌘V | `control+command:9` | `quickLauncherShortcutEnabled` (**true**) | Ctrl+Alt+Win+Q (Win+Ctrl+V is taken by Windows) |
| screenshot | screenshot | Screenshot | `screenshotShortcut` | ⌃⌥⌘4 | `…:21` | `screenshotShortcutEnabled` (false) | Ctrl+Alt+Win+4 (optionally take over PrtScn) |
| screenshotFullScreen | screenshot | Capture the whole screen | `screenshotFullScreenShortcut` | ⌃⌥⌘3 | `…:20` | `screenshotFullScreenShortcutEnabled` (false) | Ctrl+Alt+Win+3 |
| screenshotLastCapture | screenshot | Edit latest screenshot | `screenshotLastCaptureShortcut` | ⌃⌥⌘E | `…:14` | `screenshotLastCaptureShortcutEnabled` (false) | Ctrl+Alt+Win+E |
| screenshotUpload | screenshot | Upload latest screenshot | `screenshotUploadShortcut` | ⌃⌥⌘U | `…:32` | `screenshotUploadShortcutEnabled` (false), `screenshotSharingEnabled` (true) | Ctrl+Alt+Win+U |
| recentCaptures | screenshot or screenRecorder | Recent captures | `recentCapturesShortcut` | ⌃⌥⌘H | `…:4` | `recentCapturesShortcutEnabled` (false) | Ctrl+Alt+Win+H |
| screenshotClipboard | screenshot | Edit clipboard image | `screenshotClipboardShortcut` | ⌃⌥⌘P | `…:35` | `screenshotClipboardShortcutEnabled` (false) | Ctrl+Alt+Win+P |
| cameraPreview | cameraPreview | Camera preview | `cameraPreviewShortcut` | ⌃⌥⌘W | `…:13` | `cameraPreviewShortcutEnabled` (false) | Ctrl+Alt+Win+W |
| radialMenu | radialMenu | Radial menu | `radialMenuShortcut` (seed only; never registered; each wheel registers its own) | ⌃⌥⌘Space | `…:49` | `radialMenuEnabled` (false) | Ctrl+Alt+Win+Space |
| scratchpad | scratchpad | Scratchpad | `scratchpadShortcut` | ⌃⌥⌘N | `…:45` | `scratchpadShortcutEnabled` (false) | Ctrl+Alt+Win+N |
| snippetLibrary | textSnippets | Quick snippet menu | `snippetLibraryShortcut` | ⌃⌥⌘L | `…:37` | `snippetLibraryEnabled` (false) | Ctrl+Alt+Win+I (Win+L locks the PC; verify) |
| commandBar | commandBar | Command Bar | `commandBarShortcut` | ⌥Space | `option:49` | `commandBarShortcutEnabled` (false) | Alt+Space via hook (deliberately overrides the reserved system window menu; PowerToys Run precedent) |
| screenRecorder | screenRecorder | Screen recording | `recorderShortcut` | ⌃⌥⌘5 | `…:23` | `recorderShortcutEnabled` (false) | Ctrl+Alt+Win+5 |
| displayBrightnessDecrease / Increase | brightness | Decrease / increase display brightness | `displayBrightness{Decrease,Increase}Shortcut` | ⇧⌘- / ⇧⌘= | `shift+command:27` / `:24` | `brightnessControlEnabled` (false), `displayBrightnessShortcutsEnabled` (false) | Ctrl+Alt+Win+- / = |
| keyboardBrightnessDecrease / Increase | brightness (group: mouseKeyboard) | Decrease / Increase keyboard brightness | `keyboardBrightness{…}Shortcut` | ⌥⌘- / ⌥⌘= | `option+command:27` / `:24` | `keyboardBrightnessShortcutsEnabled` (false) | n/a (no generic backlight API) |
| pointerNextDisplay | windowLayout | Move pointer to next display | `pointerDisplayShortcut` | ⌃⌥⌘Z | `…:6` | `pointerDisplayEnabled` (false) | Ctrl+Alt+Win+Z |

`…` stands for `control+option+command`. macOS key codes: K=40, D=2, Tab=48, \`=50, V=9, S=1, F2=120, C=8, T=17, M=46, 4=21, 3=20, E=14, U=32, H=4, P=35, W=13, Space=49, N=45, L=37, 5=23, −=27, ==24, Z=6, ←=123, →=124, ↓=125, ↑=126, Return=36, I=34, J=38, R=15, F=3, G=5.

**Window layout** (not roles; each action has its own key, gated by `windowLayoutShortcutsEnabled`, default false). On macOS the layout actions use ⌃⌥ while the roles use ⌃⌥⌘, so they never collide. On Windows, Ctrl+Alt+letter is unusable (on many European layouts Ctrl+Alt is AltGr and types characters such as €, @ and ł), and Ctrl+Alt+Win+letter is taken by the roles. The suggestion therefore puts layout actions on Ctrl+Alt plus non-character keys, and leaves the letter-based thirds unassigned.

| Action | mac default | Storage key | Suggested Windows |
|---|---|---|---|
| leftHalf / rightHalf / topHalf / bottomHalf | ⌃⌥← / → / ↑ / ↓ | `windowLayoutShortcut{Left,Right,Top,Bottom}` | Ctrl+Alt+Arrows (some older Intel graphics drivers use these to rotate the screen) |
| topLeft / topRight / bottomLeft / bottomRight | ⌃⌥U / I / J / K | `windowLayoutShortcut{TopLeft,…}` | Ctrl+Alt+Home / PgUp / End / PgDn (the keys' physical positions match the corners) |
| maximize / center / restore | ⌃⌥Return / C / R | `windowLayoutShortcut{Maximize,Center,Restore}` | Ctrl+Alt+Enter / Ctrl+Alt+Shift+Enter / Ctrl+Alt+Backspace |
| leftThird / centerThird / rightThird | ⌃⌥D / F / G | `windowLayoutShortcut{LeftThird,…}` | unassigned (user records them) |
| leftTwoThirds / rightTwoThirds | ⌃⌥E / T | `windowLayoutShortcut{LeftTwoThirds,RightTwoThirds}` | unassigned (user records them) |
| nextDisplay | ⌃⌥⌘→ | `windowLayoutShortcutNextDisplay` | Ctrl+Alt+Shift+Right (Win+Ctrl+Right already switches virtual desktops) |
| 24 more actions (sixths, quarters, top/middle/bottom thirds, top/bottom two-thirds, center half and two-thirds, margin maximize, full screen, previous display) | **unassigned** (`none`) | `windowLayoutShortcut*` | unassigned |
| directional trigger ("Shortcut + pointer layout") | ⌃⌥Space, or a held modifier chord (stored as `modifiers:control+option`, at least two of ⌃⌥⌘, Shift optional) | `windowDirectionalShortcut`, gated by `windowDirectionalEnabled` (false) | Ctrl+Alt+Space (verify AltGr+Space on target layouts). Press-and-hold: needs a hook or key-state polling, because `WM_HOTKEY` reports no release |

**Checks on the suggested Windows defaults**
- The suggestions above are free of internal collisions. An earlier draft that put layout letters on Ctrl+Alt+Win collided with seven roles (K, D, C, T, E, U, I). Keep it that way: defaults never pass through the recorder's conflict check (§6.2), so a duplicate default would simply fail to register at runtime (`ERROR_HOTKEY_ALREADY_REGISTERED`).
- Alt+Tab, Alt+\` and Alt+Space are on the reserved list (§6.4); the switcher and the Command Bar take them over on purpose with a hook.
- Verify every Ctrl+Alt+Win default on Windows 10 and 11. The shell may still match some Win+Ctrl+<key> shortcuts with Alt held (for example Win+Ctrl+D, which creates a desktop). Inside Remote Desktop sessions, Ctrl+Alt+End and Ctrl+Alt+Home are taken by the RDP client.

**Clearing.** Window-layout actions (stored as `none`), radial-menu wheels (stored as an empty string), Command Bar per-app shortcuts and the screenshot editor's tool keys can be cleared (Delete key or the x button). Roles and the directional trigger cannot; they always have a value, and Reset restores the default.

### 6.2 Recording flow

1. Click the recorder field. It takes keyboard focus. If that fails, recording does not start.
2. **Suspend all app hotkeys** (`ShortcutCapture.begin`): the switcher's capture flag, the keep-awake hotkey, the shelf, clipboard and output-switcher shortcuts, the window layout shortcuts and every quick-tool hotkey.
3. **Start a swallowing keyboard tap** (if Accessibility is granted). Keys go only to the field, so OS or other-app shortcuts do not fire while recording. Without it, the field's own key events are used.
   - The tap intercepts only key-down and key-up; modifier changes still reach the field (the "Nothing was captured" detection relies on this).
   - Auto-repeats are swallowed and not re-delivered.
   - If the key is still held when recording ends, the tap stays to swallow the release (1 s idle watchdog).
   - A held Super key is folded into its modifiers. A timed-out tap is re-enabled, and a session switch ends the capture.
4. The field title becomes "Press keys". The caption under the row is composed as `shortcutRecording + " " + "Escape cancels."`, plus `" Delete clears."` where clearing is supported. In English this renders "Press the new shortcut Escape cancels." because the first string has no final period (an upstream bug; add the period on Windows). An error message replaces the caption.
5. **Key handling**
   - **Esc** without ⌃, ⌥ or ⌘ cancels.
   - **Delete or Forward-delete** without ⌃, ⌥ or ⌘ clears, but only where clear is supported; otherwise it is ignored.
   - **Valid** = the key has a label *and* (⌃, ⌥ or ⌘ is held, *or* the key is F1–F20). So F1–F20 are valid with any modifiers, including Shift alone (⇧F5 saves), while a letter with Shift alone is not. Keys with a label are the named keys (Tab, Space, Return, Esc, arrows, ⌫, ⌦, Home, End, PgUp, PgDn, keypad Enter) plus any key the layout prints as one non-control character, with a US-ANSI fallback.
   - **Fn** is not stored. In global shortcut rows it is silently dropped (Fn+⌃N saves as ⌃N). Only fields that need no modifier (the screenshot editor's tool keys) and chord recording refuse Fn explicitly.
   - Invalid: a beep and "Use at least Control, Option or Command with a key." The field keeps listening.
   - If ⌃, ⌥ or ⌘ goes down and comes up with no key arriving, something upstream consumed the combination: "Nothing was captured. macOS or another app already uses that combination. Try another one." (Shift alone does not arm this.) The field keeps listening.
   - Optional chord-only recording, for the window directional trigger: record the largest set of modifiers held together. A key press disqualifies the chord.
   - A captured combination stops recording **before** the save checks run, so a refused combination needs a new click.
6. **Exit paths**, all idempotent: a capture, Esc, focus loss, window close, the view being removed, or the app losing focus. Each one ends the tap and re-syncs all hotkey features (`FeatureRuntime.sync(featuresToSilenceWhileRecording)`).
7. **Save pipeline** for role rows (`ShortcutPreferenceRow.save`):
   1. **App conflict:** another role already uses the combination. The central Shortcuts page and the display-brightness rows include inactive roles; other feature pages check active roles only. The radial menu counts its per-wheel shortcuts. Message: "This shortcut is already used by %@."
   2. **Additional conflicts** (window-layout actions while `windowLayoutShortcutsEnabled` is on, and the directional trigger while `windowDirectionalEnabled` is on). Only these rows run this check: the central page, Clipboard, Output switcher, Display brightness, Move pointer to next display and Finder Rename. Keep Awake, Shelf, Switcher, Paste as plain text, Command Bar, the snippet menu, the quick tools and the capture rows skip it.
   3. Only the two switcher rows cannot take over a system shortcut. They check the system table and refuse with "This shortcut is already used by macOS." on a collision, except for their own native ids.
   4. **System collision with take-over support:** an inline offer appears, and the field shows the pending combination meanwhile.
      - Text "macOS uses %@ for one of its own shortcuts." (`%@` = the combination's display string).
      - Buttons, right-aligned: "Don’t take over" and "Take over while Vorssaint runs". Caption: "macOS gets the key back whenever Vorssaint is not running or this feature is off."
      - Accepting saves and adds the row's storage key to `systemShortcutTakeOverKeys` (a sorted string array used as a set). Declining shows the conflict message. If the user already took over exactly this combination, it just saves.
   5. Write the storage value, then call `onChange` (re-sync the role's feature). Saving a combination macOS does not use removes the take-over mark.
8. **Reset** writes the default and clears the take-over mark. It is disabled when the value already equals the default, and it runs no conflict check.
9. **Registration failure** (taken by another app): feature pages show "macOS rejected this shortcut. Choose another one." in orange under the row. The central page shows it only under the keyboard-brightness toggle; Window layout shows one message for any failed action; Display brightness uses red; the output switcher uses a grey icon label.

**Other recorders**
- **Window-layout rows** use their own pipeline: role conflicts, then other layout actions plus the directional trigger, then the take-over decision. There is no "cannot take over" step. Clear writes `none` and drops the take-over mark; Reset writes the default, or `none` for unassigned actions.
- **Directional trigger:** saving a chord runs no conflict check. Saving a key trigger checks active roles and layout actions only, with no macOS check and no take-over offer. The recorder is enabled only when Accessibility is granted. The chord is watched by a listen-only tap that any key, click or scroll cancels.
- **Radial wheel recorders** check other wheels, active roles, layout actions and Command Bar app shortcuts, but never macOS shortcuts.

**Take-over mark lifecycle**
- A mark takes effect only while the feature's hotkey is registered: it is claimed on register and released on unregister.
- Only macOS ids that are enabled at that moment get disabled. Claims are re-resolved 3 s after wake.
- Keys the app already holds still count as macOS conflicts when another row records them.

### 6.3 Display

- Modifier glyphs come in the order ⌃⌥⇧⌘, then the key label.
- A single non-alphanumeric label gets a space separator, as in "⌘ \`".
- Named keys: Tab, Space, Return, Esc, ←→↑↓, ⌫, ⌦, ↖ (Home), ↘ (End), ⇞, ⇟, ⌤, F1–F20.
- Letters follow the active layout and are cached. The cache refreshes when the input source changes. An input method maps to its ASCII-capable layout.
- Combinations that contain ⌘ take the key cap from the layout's Command table; others use the plain table. They differ on layouts such as Russian, Greek and Dvorak-QWERTY⌘.
- A key with no label is shown as "Key <code>".
- **Windows:** show "Ctrl+Alt+Win+K" style labels. Map VK codes to layout characters with `ToUnicodeEx`, and use `GetKeyNameText` for named keys.

### 6.4 Windows implementation notes

**Registration**
- Use `RegisterHotKey(hwnd, id, MOD_CONTROL|MOD_ALT|MOD_SHIFT|MOD_WIN|MOD_NOREPEAT, vk)`.
- `ERROR_HOTKEY_ALREADY_REGISTERED` maps to the `registrationFailed` state.
- Unregister on suspend and on quit.

**Combinations that need hooks**
- System combinations such as Alt+Tab, Alt+\`, Win+arrows and PrtScn need a `WH_KEYBOARD_LL` hook to intercept and swallow them. Use this sparingly.
- The hook callback must return quickly, and it is skipped for elevated windows unless Vorssaint runs elevated (UIPI).

**Reserved and risky combinations**
- Ctrl+Alt+Del, Win+L, Win+(most letters and numbers), Win+Shift+S, Win+Ctrl+(D, F4, ←, →, V, C, O, Q, Enter), Win+Alt+(R, G, B, K, PrtScn), Alt+Tab/Esc/F4/Space, Ctrl+Esc, Ctrl+Shift+Esc, Win+. and Win+;.
- **Office key combinations:** Ctrl+Alt+Shift+Win(+letter).
- **AltGr = Ctrl+Alt** on European layouts.
- PowerToys defaults (Win+Shift+C/T/V, Win+Ctrl+Alt+V, Alt+Space).

**Pre-validation:** keep a static reserved list and pre-validate in the recorder: "Windows uses this combination."

**No equivalent of the macOS take-over** (disabling a system shortcut while holding it). Either drop the offer, or for a curated set (PrtScn, Alt+Space) offer "Override with Vorssaint", which uses the hook. Undo is automatic, because a hook just stops intercepting when removed.

**Storage format for Windows:** `"ctrl+alt+win:0x4B"` (modifier tokens + VK). Do not try to import macOS key codes from backups unless you add a translation table; dropping them is recommended.

### 6.5 System shortcut take-over bookkeeping (macOS)

- The app tracks the system shortcut ids it disabled in a write-ahead marker (`systemShortcutsSuppressed`). It re-enables them on release and on quit, and recovers at the next launch after a crash.
- The switcher does not use `systemShortcutTakeOverKeys`. It has its own toggle, `switcherTakeOverSystemShortcuts` (registered false; "Replace macOS ⌘Tab and ⌘\`"). Only while that toggle is on and its tap is running does it disable the native ids 1 (⌘Tab), 2 (⌘⇧Tab), 27 (⌘\`) and 220 (⌘⇧\`) through the shared marker. The recorder's macOS check exempts the switcher rows' own native ids.
- **Windows:** not needed if hooks are used.

---

## 7. Settings model and backup

### 7.1 Store

- **macOS.** `UserDefaults.standard` (domain = bundle id).
  - **825 keys** are declared in `DefaultsKey`.
  - **739 registered defaults** (`Defaults.registeredDefaults`): 392 bool, 203 string, 72 int, 41 double, 24 string arrays, 4 data blobs and 3 string→string maps.
  - **77 availability defaults** (`featureAvailable.<id>` = `installedByDefault`).
  - The remaining keys are intentionally **unregistered**: an absent key means the built-in behavior. Examples: `appLanguage`, `hasOnboarded`, panel orders, `shelfEnabled`, `finderCutPasteEnabled` and the six original menu-bar metric toggles (`menuBarCPU`, `menuBarGPU`, `menuBarMemory`, `menuBarNetwork`, `menuBarBattery`, `menuBarPower`). The newer metric toggles (temperatures, battery time, disk usage and activity, peripheral battery, connected devices, fan speed) are registered with default false.
- **Exact schema:** `05-app-shell-assets/data/settings_schema.json`, generated from the compiled code. It contains `registeredDefaults{key: {type, default}}`, `featureAvailabilityDefaults`, `backupExportKeys` (820), `backupUnregisteredPreferenceKeys`, `backupMachineStateKeys` (65) and `essentialPreset`.
- **Value conventions**
  - Enums are stored as raw strings and always read through **sanitizers**, so unknown values fall back. Examples: `sanitizedMonitorInterval` allows 1/2/5 and defaults to 2; durations allow 0/15/30/60/120/240/480.
  - Lists are comma-joined strings (orders, `featureHubKeptFeatures`) or string arrays.
  - Structured data is JSON in `Data` or `String` (radial menu profiles, snippets).
  - Shortcuts use the storage string from §6.
- **Semantics the Windows store must keep**
  1. **Explicitly-saved vs default.** First-install enabling and "never turned on" read the persistent domain. Store only explicitly written keys in the file, and keep defaults in code, so "absent" stays distinguishable.
  2. **Change notifications.** Many components observe *any* change; the tray coalesces them per run-loop turn. Provide a change event with the key, debounced for the tray.
  3. **Sanitize on read.** Treat the file as user-editable.

**Main shell keys**

| Key | Type / default | Meaning |
|---|---|---|
| `appLanguage` | string (unregistered → system language) | UI language |
| `appAppearance` | `system`/`light`/`dark` (system) | theme |
| `liquidGlassEnabled`, `notchLiquidGlassEnabled` | bool (false) | glass |
| `hasOnboarded`, `onboardingStep` | bool, int | first run |
| `featuresOnboardingVersion` | int (4 = current) | tour marker |
| `lastUpdateIntroVersion`, `supportUpdateIntroVersion`, `updateHighlightsSeenVersion`, `updateShowcaseIntroVersion`, `brightnessUpdatePromptState` | string | intro markers |
| `featureAvailable.<id>` (77) | bool (installedByDefault) | install state |
| `featureHubKeptFeatures` | comma list | never-used "Keep" |
| `launchAtLoginWanted` | bool | stored wish |
| `hotkeyEnabled`, `keepAwakeShortcut` | bool (true), shortcut | keep-awake hotkey |
| `autoCheckUpdates`, `includeBetaUpdates`, `updateLastInstallFailure` | bool (true), bool (false; auto-true on prerelease builds), string | updater |
| `settingsWindowWidth`, `settingsWindowHeight` | double (0 = unset) | window size |
| `startupDidNotFinish` | bool | crash guard |
| `statusItemPlacementGeneration` | int | tray identity reset |
| `panelSectionOrder`, `panelUtilityOrder`, `panelControlOrder`, `panelToggleOrder`, `panelSystemOrder`, `panelNetworkOrder`, `panelDiskOrder`, `panelPowerOrder` | comma lists (unregistered) | orders |
| `panelShow*`, `monitorShow*` | bool (true) | section visibility |
| `panelUtility*`, `panelControl*`, `panelToggle*` | bool | item visibility |
| `panelControl{Windows,Input,Files}Expanded` | bool (false) | categories |
| `menuBarIconSymbol` | string (`""` = brand mark) | tray glyph |
| `keepAwakeRightClickToggle`, `showCountdownInMenuBar`, `keepAwakeIconTint`, `keepAwakeActiveIcon` | false, false, `orange`, `vorssaint` | tray behavior |
| `menuBarHideIconWithMetrics`, `menuBarSeparateMetrics`, `menuBarCombineTemperatures`, `menuBarMetricOrder`, `menuBar<Metric>`, `monitorIntervalSeconds` (2) | | tray metrics (Monitor spec) |
| `micMuteMenuBarIndicator` | bool (true) | tray badge |
| `systemShortcutTakeOverKeys` | [String]: sorted shortcut storage keys, used as a set (exported) | §6.2 |
| `systemShortcutsSuppressed` | [Int]: macOS symbolic-hotkey ids currently disabled; crash-repair marker (never exported) | §6.5 |
| `simulateUpdate`, `simulateBetaUI` | bool | developer build only |

### 7.2 Registration and migrations (`Defaults.register()`, runs every launch)

1. **Pre-registration migrations.** These need to see whether a value was ever saved: the island default profile, glass on the island, fan-control visibility, scroll inverter axes, linear-scroll availability, WhatsApp downloads flag, battery temperature, switcher preview size and excluded apps.
2. **Register** the defaults and the availability defaults.
3. **Post-registration:**
   - activate the beta channel once per prerelease version (`betaChannelActivatedFor.<version>`);
   - install the companion for beta Command Bar users;
   - legacy menu-bar temperature metric;
   - legacy switcher window shortcut;
   - legacy keyboard debounce window;
   - utility order: Screenshot first, insert App updates;
   - screenshot editor flag;
   - unified, restored and orphaned capture shortcuts (each with a "migrated" marker);
   - silent headphone volume;
   - switcher windowless Finder;
   - recheck the brightness DDC cache once;
   - hide the scratchpad and keyboard-light island controls once.

Migrations are idempotent and guarded by markers or by the presence of a value. **Windows:** implement a versioned migration list that runs before anything reads settings; record `settingsSchemaVersion`.

### 7.3 Backup file format

- **macOS file:** XML property list, default name **"Vorssaint Settings.plist"**.

```
{
  "vorssaintBackupVersion": 1,              // int; also accepted as NSNumber or numeric string
  "vorssaintBackupAppVersion": "3.4.1-beta.3",
  "settings": { "<key>": <value>, ... }     // values as stored (bool/int/double/string/array/dict/data)
}
```

**Export**
- The keys are `registeredDefaults ∪ availabilityDefaults ∪ unregisteredPreferenceKeys − machineStateKeys`, which gives **820** keys.
- Values are read *through the registered defaults*, so the file is a complete snapshot, including untouched values.
- The scratchpad flushes its state first.
- **Sanitizers before writing:**
  - island display mode: unknown values become automatic;
  - screenshot watermark style and presets: drop `imagePath` and image-kind presets;
  - recorder editor presets: drop images;
  - media image profiles: made portable;
  - media watermark kind: logo becomes off; text+logo becomes text if there is text, else off;
  - mouse exception lists and window-layout ignored apps: drop executable-path entries (they carry the username and only exist on one machine).

**Included (unregistered keys that travel)**
- `autoQuitEnabled`, `shelfEnabled`, `finderCutPasteEnabled`;
- text snippets, radial menu items and profiles, Command Bar links and row shortcuts;
- `appLanguage`, app volumes and output devices, mixer universal output and hidden apps, preferred input device, sound output switcher devices;
- the six original menu-bar metric toggles;
- all panel orders and collapsed sections; the quick-launcher order; legacy island layout keys;
- `systemShortcutTakeOverKeys`;
- **experience flags** (`hasOnboarded`, `onboardingStep`, onboarding and intro markers, `featureHubKeptFeatures`, brightness prompt state, collapsed reset version), so a restored machine does not replay onboarding.

**Excluded (machine state; 65 keys)**
- displays switched off; Dock autohide restore; Spaces order restore and Dock restart pending; Bluetooth restore pending;
- mic mute live state and per-device saved volumes;
- cleaner, app-updates and WhatsApp run history; WhatsApp organizer records and paths; WhatsApp downloads exclusions (device:inode ids) and access confirmation;
- Command Bar usage, habits and file scopes; downloads-folder bookmark; wallpaper bookmarks; recorder and screenshot save folders; music-blocker replacement app path; watermark logo path;
- `simulateUpdate`; showcase markers; capture-shortcut migration markers;
- **Settings window size**;
- last loupe zoom; screenshot sharing developer endpoint; recorder system-audio verification;
- fan-control recovery, helper version and resume configuration;
- switcher and system-shortcut suppression markers;
- brightness DDC caches; island camera-fit measurements.

Not preference keys at all, and never in the file: clipboard entries, shelf items, scratchpad notes (files) and system permissions.

**Import**
1. Show the open panel (plist or xml). Use coordinated read with security-scoped access.
2. **Validate:** the format version must be 1 ≤ v ≤ 1, and `settings` must be a dictionary.
3. **Filter:** keep only allowed keys. Keys present in the registered defaults must match the default's type: Bool must be a real boolean; Int a non-boolean, non-float number; Double a finite number; then String, `[String]` and `[String:String]`. Everything else passes on the allow-list alone: registered Data defaults (`notchQuickAccessLayout`, `recorderEditorPresets`), the 77 `featureAvailable.*` keys (registered separately, so never type-checked) and the 44 unregistered keys, except `notchQuickAccessSide`, `notchQuickAccessSecond` and `notchQuickAccessThird`, which must be strings. Then apply the same sanitizers.
4. **Confirmation** alert (§3.5.11). Use the island variant when the file has none of the island keys (any key starting with `notch`, `panelControlNotch`, or island availability keys).
5. **Apply:**
   - capture the local-only parts: recorder preset images, watermark image style and presets, executable-path exceptions;
   - **clear every exported key** (except the island keys when the file has none), then write the file's values;
   - restore the local parts: matching recorder presets keep their images; the local watermark image path is kept; path exceptions are appended after the restored list, without duplicates;
   - the screenshot watermark style and presets are always rewritten, even when the file lacks them. Presets are capped at 12: the result is the last (12 − local image-preset count) restored text presets followed by the local image presets, and the style keeps the local image path and is sanitized;
   - before applying, the scratchpad drops its in-memory document so its flush on quit cannot overwrite the restore;
   - **relaunch the app** (§3.1), so every service starts clean.

**Windows backup**
- Use JSON with the same envelope, plus `"platform": "windows"` and `"settingsSchemaVersion"`.
- Use the default name "Vorssaint Settings.json" via the standard save dialog.
- Keep the same allow-list, type-check, sanitizer, clear-then-write and relaunch flow.
- Decide whether to accept macOS plist backups. If you do, import only cross-platform keys (language, appearance, panel layout, feature availability for shared features, snippets) and drop shortcuts.

---

## 8. Update, release and CI

### 8.1 In-app updater (macOS)

**Feed:** the GitHub REST API for `vorssaint/vorssaint-utils`.
- Stable channel: `GET https://api.github.com/repos/vorssaint/vorssaint-utils/releases/latest`.
- Beta channel (`includeBetaUpdates`): `GET …/releases?per_page=10`.
- Headers: `Accept: application/vnd.github+json` and `User-Agent: Vorssaint/<version>`. The local cache is ignored. No authentication is used, so GitHub's unauthenticated rate limit applies (60 requests per hour per IP, per GitHub's documentation). The code has no special 403/429 handling, and the HTTP status is never checked: a network error gives `failed(<error description>)`, and an empty or undecodable body gives `failed("-")`. Settings shows this as "Couldn’t check: …".
- Fields used: `tag_name`, `prerelease`, `draft`, `body`, `assets[].name`, `assets[].browser_download_url`, `assets[].size`.

**Selection**
1. Drop drafts and releases without an asset whose name ends in `.dmg`.
2. Drop betas unless the channel includes them. A release is a beta if `prerelease` is set or its semver has a prerelease part.
3. Keep releases newer than the current version (SemVer 2.0 comparison).
   - Prerelease identifiers compare per item: numeric before alphanumeric, alphanumeric by localized standard compare.
   - "v", "V" and whitespace are trimmed from both ends, and `+build` metadata is stripped.
   - A missing minor or patch counts as 0. The prerelease part is everything after the first "-", with empty identifiers dropped. When all shared prerelease identifiers are equal, the longer list wins.
   - An invalid current version (for example "dev") means any valid candidate is newer.
4. Pick the highest. The version string is the tag with `v`/`V` trimmed.
5. Release notes = the release body with the distribution footer removed. The footer is filtered by an exact whole-line match against the text `release.yml` appends, so **changing that footer text (for example to add Windows wording) breaks the filter in clients already shipped.**

**Schedule**
- 6 s after launch, if automatic checks are on.
- Then an **hourly** timer (5 min tolerance).
- Plus `checkIfStale` (more than 15 min since the last check) on app activation and when the panel opens.
- A manual check comes from the context menu ("Check for updates…" also opens Settings) or About → "Check now".
- Developer builds never check. They can simulate "9.9.9" or "9.9.9-beta.1" (the simulated preview shows the bundled CHANGELOG).
- Beta builds force `includeBetaUpdates = true` the first time (once per prerelease version).
- `check()` does nothing while checking, downloading or installing. `checkIfStale` runs only when automatic checks are on.
- **Build type.** The version falls back to "dev". A developer build is one whose bundle id ends in ".dev". A beta build is a version containing -beta, -rc or -alpha (case-insensitive), or `simulateBetaUI` on a developer build.

**Notification.** An automatic check that finds a new version posts "Vorssaint update" / "Update available: X", once per distinct version per app run (the memory of notified versions is not persisted).

**States:** idle → checking → upToDate | available(v) | failed(reason) → downloading(progress?) → installing.
- A failed download returns to `available`, so the user can retry. If writing or spawning the installer script fails, the state goes to `failed` and the DMG is deleted.
- The state drives the tray glyph (blue when available), the panel banner, Settings → About, and the Dynamic Island header's update button.

**Install flow**

1. The user clicks the banner or "Download and install". The **Update preview window** opens:
   - 640×600, titled "What's New", with a 22 pt bold header;
   - the scrollable notes (the GitHub release body fetched at check time, not the bundled CHANGELOG), rendered with the same parser as What's New (version header in the accent colour);
   - "Cancel" (Esc) and "Download and install" (default, prominent).
2. **Pre-flight:** if the app runs from a read-only volume or the translocation path, show the alert "Move Vorssaint to Applications" / "…cannot be updated…".
3. **Download** to a temporary file.
   - The ceiling is 200 × 1024 × 1024 bytes. The advertised size is the limit when 0 < size ≤ ceiling; otherwise the ceiling applies.
   - Progress is published in whole-percent steps.
   - The download is accepted only if the status is 200 and 0 < received ≤ ceiling; when an advertised size > 0 exists, received must **equal** it.
   - The file is moved to `$TMPDIR/Vorssaint-update.dmg`.
4. **Installer selection**
   - If the app's folder is writable and the last failure was not `fail-copy`/`fail-swap`, a **user installer** runs: a script file spawned detached, after which the app terminates in 0.4 s.
   - Otherwise an **admin installer** runs: the same script inline under an authorization prompt ("Vorssaint needs your password to install the update."). If the prompt is declined, the update returns to available.
5. **Installer script** (`$1` app path, `$2` dmg, `$3` pid, `$4` result marker path, `$5` uid, `$6` expected version from the trusted tag):
   1. Wait for the pid to exit.
   2. Verify the **DMG signature** (Apple-anchored, team OU `3D485NHW29`).
   3. Mount it (no browse) and find the `.app`.
   4. Copy it to a hidden staging name `.<Name>.update-new` and clear extended attributes.
   5. Check that `CFBundleShortVersionString` == the expected version.
   6. Gatekeeper assessment (skipped if Gatekeeper is disabled) plus `codesign --deep --strict` with the requirement `identifier "com.vorssaint.utils"` and team OU `3D485NHW29`.
   7. Swap: move the old app to `…update-old.<pid>` and the stage to its destination, then delete the backup. On failure, roll back. When the installer itself runs as root (the admin path) and `$5` is set, `chown -R` the new bundle to that uid. A renamed bundle replaces the old name.
   8. Detach, delete the DMG, finalize the marker and relaunch as the user.
   
   Every failure exit also finalizes the marker, deletes the DMG and the script, and relaunches the old app.
   
   **Write-ahead markers**, written to `<marker>.progress` before each step and promoted to `<marker>` only when the run finishes: `fail-dmg-verify`, `fail-tempdir`, `fail-mount`, `fail-no-app-in-dmg`, `fail-copy`, `fail-version`, `fail-verify`, `fail-swap`, `ok`. The marker lives in `~/Library/Application Support/<bundle id>/update-install-result`.
6. **Next launch:** delete any stale `.progress` file, then read and delete the marker.
   - Any marker not starting with "fail" counts as success and clears `updateLastInstallFailure`.
   - A `fail-*` code stores the code, notifies "The update was downloaded but could not be applied… (code)" and re-checks after 3 s (not on developer builds).

**Showcase media** (3.1.4 only, non-beta builds): download the release asset `vorssaint-3.1.4-showcase-1.mp4`, verify a hard-coded SHA-256 and cache it in `Caches/<bundle>/UpdateShowcase/<version>/`. The cache is deleted whenever the intro is not due (another version, or already seen) and whenever the intro closes.

### 8.2 Windows updater

- **Feed:** the same GitHub endpoints, if the Windows build is published in the same repository (subject to permission, §10). Select assets by name: `Vorssaint-<ver>-win-<arch>-setup.exe` or `.msix`, with arch x64 or arm64.
  - Adding Windows assets to the existing releases is compatible with macOS clients, which pick the first `.dmg`. **The release workflow's asset-count verification must include them, though.**
- **Selection, scheduling, channels and state machine:** reuse §8.1 exactly. The SemVer comparator is pure logic.
- **Verification, before execution:**
  - `WinVerifyTrust` (Authenticode) with the **publisher certificate pinned** (subject or thumbprint list);
  - the exact asset size;
  - optionally a `SHA256SUMS` asset. Today it is built but not uploaded, so add it to the published files.
- **Install**
  - **Per-user install** in `%LOCALAPPDATA%\Programs\Vorssaint`: no elevation. Download, verify, run the installer silently (`/S` or `/VERYSILENT`) with a `--relaunch` flag. The app exits so its files can be replaced. The installer relaunches it.
  - **Per-machine install:** UAC elevation via the installer manifest. This is the analog of the admin path.
  - **MSIX:** use an App Installer `.appinstaller` file with `UpdateSettings` (OnLaunch / AutomaticBackgroundTask), or the Store.
  - Off-the-shelf frameworks that implement delta updates and relaunch: Velopack, Squirrel, or WinSparkle (with an appcast).
- **Failure reporting:** keep the write-ahead marker idea. The installer writes `%LOCALAPPDATA%\Vorssaint\update-install-result` with the same codes. The app reads it on the next launch.
- **Pre-flight:** if the app runs from a non-writable location or a portable ZIP, offer "open the download page" instead.

### 8.3 Build (macOS)

- **`build.sh`** compiles with `swiftc` directly, not with Xcode. `Package.swift` exists only for tooling.
  - Target `arm64-apple-macosx14.0` (**Apple Silicon only**, macOS 14 or later).
  - It prefers the pinned `/Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk` with compat flags (`-Xfrontend -interface-compiler-version -Xfrontend 6.3.2`), unless `DEVELOPER_DIR` is set (as in the CI compatibility job); then it uses `xcrun --show-sdk-path`.
  - Incremental batch mode on all cores, with output file maps in `build/objects`.
  - Output layout: release builds wipe `build/` except `build/objects`; the executable is `build/<Executable>`. The bundle is assembled in a temp dir, then copied to `build/stage/<App>.app` (`build/stage.noindex` for dev builds). `make-dmg.sh`, `release.yml` and `ci.yml` depend on these paths.
- **Variants**
  - Release: `-O`, bundle `com.vorssaint.utils`, "Vorssaint".
  - `--dev`: `-Onone -D VORSSAINT_DEVELOPMENT`, `com.vorssaint.utils.dev`, "Vorssaint (Developer)", executable `VorssaintDeveloper`, with a commit stamp.
- **Other build products**
  - The privileged fan helper: a LaunchDaemon via SMAppService and XPC, which runs its own `--selftest` during the build.
  - The Now Playing adapter dylib, loaded through `/usr/bin/perl` + `now-playing.pl`.
- **Icons:** `swift Tools/MakeIcon.swift` produces the iconset, ICNS, `MenuBarIcon.png`/`@2x` and `BrandMark.png`. When full Xcode is present, `actool` compiles the adaptive `AppIcon.icon` into `Assets.car`.
- **Bundle contents:** `Info.plist` (from `Resources`), **`CHANGELOG.md`** (read at runtime for What's New), `*.lproj/InfoPlist.strings`, `Gifs/`, `Images/`, `agent-prices.json`, the fan helper and its LaunchDaemon plist, the adapter, and `PkgInfo`.
  - In the dev variant the plist is patched (id, name, executable) and stamped with `VorssaintBuildCommit`. `VorssaintFanControlHelperVersion` holds a hash of the helper and its plist.
- **Info.plist:**
  - `LSUIElement` = true (accessory), `LSMinimumSystemVersion` 14.0, category utilities;
  - `CFBundleLocalizations` lists the 15 languages, with `CFBundleAllowMixedLocalizations`;
  - usage descriptions for Apple Events, audio capture, Bluetooth, microphone, calendars, camera, and the Downloads/Desktop/Documents/network/removable volumes.
- **Entitlements** (hardened runtime, not sandboxed): apple-events, camera, audio-input, calendars.
- **Signing**, in order of preference:
  1. **Developer ID Application** with hardened runtime, entitlements and a timestamp (up to 3 attempts, sleeping 1 s then 2 s);
  2. the self-signed "Vorssaint Utils Signing" identity, auto-created by `Tools/setup-signing.sh` for `--dev` or `--install`, so privacy grants survive rebuilds;
  3. ad-hoc.
  
  Each build signs the helper, then the adapter, then the app, and verifies. `--install` copies the bundle to `/Applications` and re-signs it after the copy. On an official (non-dev) `--install`, the legacy `Vorss` / `VorssaintUtils` processes are stopped and the stray legacy bundles ("Vorss", "Vorssaint Utils") are removed.
- **`--test`** generates sources (`Tests/generate_sources.py`), compiles the test binary from the curated `TEST_SOURCES` list plus the tests, runs it, runs the shell tests, and cleans up test preference domains.

### 8.4 Release pipeline (GitHub Actions)

**`ci.yml`** runs on push to main and on PRs. Read-only permissions; in-progress PR runs are cancelled.
- Job `compatibility` (macos-15, Xcode 16.2, Swift 6.0.3, SDK 15.2): build, `--selftest`, unit tests.
- Job `build` (macos-26): build, `--selftest`, package DMG, unit tests.

**`release.yml`** runs on a pushed tag matching `v[0-9]*`. The actor and triggering actor must be `vorssaint`, and the repository must be the origin. The workflow sets `permissions: {}` at the top and uses a concurrency group `release-signing` that never cancels.

1. **preflight** (no secrets):
   - The tag must match `^v\d+\.\d+\.\d+(-(beta|rc|alpha)\.\d+(\.\d+)?)?$`.
   - The commit must be **verified** by GitHub and be identical to or an ancestor of `main`.
   - `Info.plist` `CFBundleShortVersionString` must equal the tag without its leading "v".
   - `CHANGELOG.md` must have a dated entry `## [X] - YYYY-MM-DD`.
   - Unit tests.
2. **release** (protected environment `release-signing`):
   1. Re-verify the tag and commit.
   2. Import the signing certificate (`SIGNING_CERT_P12` + `SIGNING_CERT_PASSWORD`) into a temporary keychain. `REQUIRE_SIGNING` / `REQUIRE_NOTARIZATION` make missing credentials fatal.
   3. `build.sh`.
   4. Verify the requirement (`identifier "com.vorssaint.utils"` + team OU `3D485NHW29`).
   5. `--selftest`.
   6. **Notarize** the app: zip it, `notarytool submit --wait` with the App Store Connect API key secrets (`NOTARY_API_KEY_P8` as base64, `NOTARY_KEY_ID`, `NOTARY_ISSUER_ID`), then `stapler staple`.
   7. `make-dmg.sh`: a styled DMG with a rendered background, the app and an Applications symlink. It is named `dist/Vorssaint-<version>.dmg`.
   8. Sign the DMG (Developer ID "Pedro Gomes (3D485NHW29)") and notarize it.
   9. Showcase media, for 3.1.4 only.
   10. Verify: `stapler validate`, mount, `spctl` reports "Notarized Developer ID", and the deep codesign requirement passes.
   11. Release notes: the CHANGELOG section plus the distribution footer ("Signed with an Apple Developer ID and notarized by Apple… Requires macOS 14 or later…"). An empty CHANGELOG section fails the job. `prerelease = true` if the tag has `-beta`, `-rc` or `-alpha`.
   12. Stage the DMG (+ mp4) and `release-notes.md` with `SHA256SUMS`, and upload them as the artifact `release-<tag>` (1 day retention).
3. **publish** (`contents: write`, no Apple secrets):
   1. Download the artifact and verify the checksums.
   2. Re-check that the tag has not moved.
   3. Create or reuse a **draft** release (title = tag, notes from the file; an existing draft must be bot-authored) and upload the assets.
   4. Verify the assets: exact count, each digest, and that the uploader is `github-actions[bot]`. Verify the metadata and body.
   5. Publish: stable releases are marked `--latest`; prereleases get `--prerelease --latest=false`.
   6. Wait until the release is **immutable**, then verify again.
   
   An existing published release must already be immutable and bot-authored, or the job refuses.

**`issue-fix-status.yml`** labels issues: merged PR → "fixed (pending release)"; next stable release → "please confirm close"; 14 days of silence → close. It also comments once on PRs that use closing keywords.

**External distribution:** Homebrew cask `vorssaint` (README). The repo documents only the install and uninstall commands and that betas do not replace the cask; how the cask is updated is not defined here.

### 8.5 Windows release proposal

- Add a `release-windows` job (`windows-latest`, and `windows-11-arm` for arm64) after `preflight`, in its own protected environment:
  1. build;
  2. `--selftest`;
  3. **Authenticode-sign** the exe, DLLs and installer. Use Azure Trusted Signing or an OV/EV certificate in an HSM via `signtool /fd sha256 /tr <RFC3161> /td sha256`;
  4. build the installer (Inno Setup, NSIS, WiX/MSI or MSIX);
  5. sign the installer;
  6. generate `SHA256SUMS`;
  7. upload the artifact.
- Extend **publish** so that it uploads both artifact sets to the same tag and the asset verification counts all files. Alternatively, publish Windows releases under a separate tag namespace (`win-v…`) and a separate API query.
- **winget** manifest PR (`wingetcreate update`) and/or a Scoop bucket as the Homebrew analog.
- SmartScreen reputation builds per certificate. EV or Trusted Signing helps.

---

## 9. macOS dependencies → Windows mapping

| macOS mechanism | Used for | Windows equivalent | Notes |
|---|---|---|---|
| `NSApplication` accessory policy (`LSUIElement`) | no Dock icon | no main window; tray icon only; `WS_EX_TOOLWINDOW` for the flyout | Settings appears as a normal taskbar window while open |
| `WindowActivationPolicy` retain/release (accessory ↔ regular) | Dock icon and ⌘Tab while Settings is open | automatic: a normal window has a taskbar button | — |
| `NSStatusItem` (+ autosave name, `isVisible`, variable length) | tray icon, metric items, clipboard preview | `Shell_NotifyIcon` (NIM_ADD/MODIFY/DELETE, `NOTIFYICON_VERSION_4`, `guidItem`) | icon only, no text; overflow by default; position not controllable; re-add on `TaskbarCreated` |
| Status item title text / attributed metric blocks | countdown and metrics next to the icon | render the value into the icon bitmap (2–3 glyphs), a separate always-on-top mini window near the tray, or the tooltip | deskbands are deprecated on Windows 11; see §10 |
| `NSMenu` on right click | context menu | `TrackPopupMenuEx` (after `SetForegroundWindow`), or a framework menu | submenu for "Activate for…" |
| `NSPopover` (application-defined behavior, arrow, safe area) | flyout panel | borderless top-most window with Acrylic, rounded corners, shadow; positioned from `Shell_NotifyIconGetRect` and taskbar edge | no arrow (Windows 11 style); handle multi-monitor and DPI (`WM_DPICHANGED`) |
| `NSEvent` global/local monitors (mouse down, key down) | outside-click dismissal, Esc | `WM_ACTIVATE` inactive; `WH_MOUSE_LL` if needed; key handling in the window | keep the 0.35 s reopen guard |
| `NSApp.activate`, return activation to the previous app | focus for panel controls; hand-back | `SetForegroundWindow` (focus-steal rules: allowed after a tray click); remember `GetForegroundWindow()` and restore it on Esc or icon close | foreground lock timeout restrictions apply |
| `canJoinAllSpaces`, `fullScreenAuxiliary` | panel on every Space and over full-screen apps | top-most window; virtual desktops: `IVirtualDesktopManager` (or pin to all desktops); above full screen: `HWND_TOPMOST` | exclusive full-screen apps may still cover it |
| `NSWindow` (Settings) + `NavigationSplitView` | Settings window | normal window with a navigation view (WinUI `NavigationView`, WPF/Avalonia split pane, …) | min size, saved size, back/forward |
| `NSMenu` main menu with key equivalents | standard shortcuts in an accessory app | native text-box shortcuts; window accelerators | Alt+Left/Right for history |
| Mouse side buttons (`otherMouseDown` 3/4) in Settings | back/forward | `WM_XBUTTONDOWN` XBUTTON1/2 | — |
| `UserDefaults` (+ registered defaults, persistent domain, `didChangeNotification`) | all settings | JSON in `%APPDATA%\Vorssaint\settings.json`, defaults in code, explicit-key tracking, change events, atomic write (temp file + `MoveFileEx`) | keep sanitizers |
| `@AppStorage` bindings | views bound to keys | an observable settings service with per-key change notifications | — |
| Carbon `RegisterEventHotKey` | global shortcuts | `RegisterHotKey` + `WM_HOTKEY` (`MOD_NOREPEAT`) | failure → "unavailable" message |
| `CGEventTap` (switcher, recording, input features) | intercepting system combos; swallowing keys while recording | `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks; Raw Input for read-only | UIPI: no hooks into elevated apps unless elevated |
| Symbolic hotkeys (SkyLight private API) + takeover | detect and disable macOS shortcuts | no API. Static reserved list + `RegisterHotKey` probe; optional hook-based override | — |
| TIS / UCKeyTranslate (layout key labels) | key caps | `ToUnicodeEx`, `MapVirtualKeyEx`, `GetKeyNameText`, `GetKeyboardLayout`; watch `WM_INPUTLANGCHANGE` | — |
| TCC (Accessibility, Screen Recording, Full Disk, Automation, Calendar, …) | permission gating and the portal | mostly not applicable. Camera, mic and location privacy toggles (`AppCapability` / `ms-settings:privacy-*`); notifications settings; **elevation** for controlling admin windows; Windows Graphics Capture consent | rebuild the portal around Windows realities |
| `PermissionGuideOverlay` (non-activating floating card) | guide through System Settings | top-most `WS_EX_NOACTIVATE` tool window | — |
| `SMAppService.mainApp` | launch at login | HKCU `…\Run` value or Task Scheduler logon task; `StartupTask` for MSIX; detect `StartupApproved` disabled | keep the repair rules |
| `UNUserNotificationCenter` | notifications | toast notifications (`ToastNotificationManagerCompat` or `Windows.UI.Notifications`) with an AUMID | Start menu shortcut needed when unpackaged |
| GitHub API + DMG + shell installer + `codesign`/`spctl`/`hdiutil` | self-update | GitHub API + signed installer/MSIX + `WinVerifyTrust` + helper/installer relaunch | Velopack, Squirrel or WinSparkle as alternatives |
| `NSSavePanel` / `NSOpenPanel` | backup export/import | `IFileSaveDialog` / `IFileOpenDialog` (or framework pickers) | — |
| `PropertyListSerialization` (XML plist) | backup format | JSON | optional plist reader for mac imports |
| `NSAppearance` aqua/darkAqua; `NSApp.appearance` | theme override | per-window theme (`DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` + framework theme) | tray follows the taskbar theme |
| `.regularMaterial`, `NSVisualEffectView` (`hudWindow`), `glassEffect` | translucency | Mica (`DWMSBT_MAINWINDOW`), Acrylic (`DWMSBT_TRANSIENTWINDOW`); Windows 10: solid fallback | respect "Transparency effects" |
| `NSWorkspace` accessibility flags (increase contrast, reduce motion, reduce transparency) | a11y adaptations | `SPI_GETHIGHCONTRAST`, `SPI_GETCLIENTAREAANIMATION`, `EnableTransparency` registry value | — |
| `NSColor.controlAccentColor` | accent | `UISettings.GetColorValue(UIColorType.Accent…)` | — |
| SF Symbols | icons | Fluent UI System Icons / Segoe Fluent Icons (§4.10) | — |
| `NSAlert` (modal) | confirmations | `TaskDialogIndirect` / framework `ContentDialog` | — |
| `os_log` (subsystem, categories) | diagnostics | ETW / EventSource, or rolling file logs | — |
| Apple Events reopen (`applicationShouldHandleReopen`) | recovery when the icon is missing | single-instance mutex + IPC "show" | — |
| `DetachedProcess.spawn("/bin/sh", …)` relaunch helper | relaunch after import or restart | `CreateProcess(DETACHED_PROCESS)` helper waiting on the process handle | — |
| `NSScreen` frames and work area (`visibleFrame`) | panel clamp, Settings placement | `MonitorFromPoint`, `GetMonitorInfo` (rcWork), per-monitor DPI | — |
| `IOPMAssertion` (self-test) | power assertion check | `SetThreadExecutionState` / `PowerCreateRequest` | Keep Awake spec |
| Bundle resources (CHANGELOG, GIFs, images) | release notes, tours | app folder resources or embedded resources | — |
| `CFBundleLocalizations`, `InfoPlist.strings` | OS-facing localized metadata | MSIX `Package.appxmanifest` resources (`resources.pri`), installer languages | — |
| `codesign`, Developer ID, notarization, stapling | trust | Authenticode, timestamping, SmartScreen reputation | — |
| `build.sh` (`swiftc`) | build | MSBuild/dotnet, CMake or Electron tooling | — |

---

## 10. Porting notes

### 10.1 Biggest risks

1. **The tray cannot do what the macOS menu bar does.**
   - The macOS shell shows **text and graphics next to the icon**: the keep-awake countdown, CPU/GPU/RAM blocks, network rates, battery. It can also show several separately clickable metric items whose positions the user arranges.
   - Windows notification-area icons are 16×16, **icon only**. New ones go into the overflow area. Order is user-controlled.
   - **Decide early** on one or more of these:
     - render one metric per tray icon (2–3 characters drawn into the bitmap, as many hardware monitors do);
     - a small top-most "taskbar companion" window docked next to the tray (fragile across Windows 11 taskbar changes);
     - drop inline text and rely on the flyout plus the tooltip.
   - Also plan **first-run guidance to pin the icon**.
2. **Global shortcut defaults do not translate 1:1.**
   - The macOS app uses a free ⌃⌥⌘ layer, ⌘Tab and ⌘\` for its switcher, and ⌥Space for the Command Bar. Windows has no equivalent free layer:
     - Win+letter combinations are mostly reserved;
     - Ctrl+Alt+letter collides with **AltGr** characters on European layouts;
     - Alt+Space is the system window menu;
     - Alt+Tab needs a low-level hook.
   - There is no API to disable an OS shortcut (the macOS "take-over" feature).
   - Stored shortcuts are macOS virtual key codes, so backups cannot carry shortcuts across, and Win32 hotkey ids need a new disjoint map (§3.8).
   - **Action:** finalize the Windows default table. The §6.1 suggestions are AltGr-safe and free of internal collisions, but every Ctrl+Alt+Win default still has to be tested against the Windows 10 and 11 shells.
3. **Feature scope and the hub catalog are macOS-shaped.**
   - About half of the 77 features are macOS concepts: Dock, Finder, Spaces, notch island, TCC, Homebrew, DMG installer, SMC fans, keyboard backlight, Music app blocker.
   - The hub, presets, installed-by-default list, Settings sidebar grouping, panel sections and Controls categories are all **derived from the feature list and its groups**.
   - **Action:** define a Windows feature catalog with ids, groups, gates, enable keys and permissions first. Then regenerate the presets, the default install set, the "never turned on" candidates, the sidebar and the search index from it. Keep the raw ids of shared features identical, for backup compatibility.
4. **Localization volume and semantics.**
   - The volume is 3,253 keys × 15 languages.
   - Values use Apple printf syntax, where not every `%` is a placeholder.
   - Plurals are done by hand with 3 forms and per-language rules.
   - A few strings are composed by concatenation (3 prefix fields plus 2 compositions), about 50 helper methods compose strings, and some strings are inline or hard-coded.
   - Much of the copy is macOS-specific: menu bar, ⌘, Finder, System Settings.
   - The extractor (§5.2) solves the *mechanical* part. Rewording macOS terms for Windows needs a real translation pass in all 14 languages.
5. **Ownership: trademark, update feed and services.**
   - `TRADEMARKS.md` reserves the Vorssaint name, icon, bundle identity, signing identity and **update feed** for the maintainer. Unofficial builds must use a different name, icon, identifier, signing identity and update feed.
   - The updater (GitHub releases of `vorssaint/vorssaint-utils`), the feedback endpoint (`screenshots.vorssaint.com`), the Buy Me a Coffee, Discord and X links, and release immutability are all maintainer-controlled.
   - **Confirm the official status and permissions before reusing any of these.** Otherwise, re-brand and stand up your own feed and endpoints.
   - The feedback API schema also hard-codes `macOS` and `macModel` fields.

### 10.2 Other notable risks

- **Flyout focus and activation.** The macOS panel activates the app, hands activation back on Esc or icon close, has precise outside-click rules and a 0.35 s anti-reopen guard, keeps Settings open beside it, and has hosted tools that keep it open. Reproduce this carefully with Windows foreground rules.
- **Settings store semantics.** "Explicitly saved vs default" drives first-install enabling and the never-used detection. A naive JSON store that writes all defaults breaks this.
- **Self-update trust.** Replacing a running exe requires a helper or installer. Pin the publisher certificate. Per-user versus per-machine installs, SmartScreen and portable mode all need decisions.
- **Theme.** The tray must follow the *taskbar* theme while windows follow the app theme. Mica and Acrylic need Windows 11 22H2 or later; plan fallbacks.
- **The brand mark is wide (1.97:1)** and illegible at 16 px. A square tray glyph must be designed.
- **Screenshots are outdated.** Use the code-derived specs here for visuals, not `docs/assets/readme`.

### 10.3 Suggested build order

1. Settings store (with explicit-key tracking, migrations and change events) and the localization service (load the `data/i18n` JSON, printf formatter, plural rule).
2. Feature registry: catalog, availability, bindings, gates, presets.
3. Tray icon (states, menu, recovery) and flyout host (positioning, dismissal, activation, height logic). The panel skeleton has the header, tab bar, one section and footer.
4. Settings window: router and history, sidebar generation from the catalog, search, anchors and highlight, cards and rows. Then the General, Menu bar, Features hub, Shortcuts, Advanced, About and What's New pages.
5. Global shortcut registry and recorder.
6. Onboarding and the intro chain; toasts; launch at login.
7. Backup and restore.
8. Updater, plus the CI/release job.
9. Self-test and diagnostics.

### 10.4 Things to keep exactly

- Raw ids: features, sections, anchors, tiles, control items, settings keys and shortcut storage keys.
- Order-merge rules: unknown ids are ignored, and new ids are inserted at their canonical position.
- "Never revoke an existing install". Never-disable-at-startup for launch at login.
- Write-ahead markers for anything that changes system state, with restore on the next launch.
- Every exit path from shortcut recording re-registers the hotkeys.
- The backup's allow-list, type check, sanitizers and clear-then-write-then-relaunch sequence.

---

## Appendix A — Feature catalog (77 features)

The last two columns need explanation:

- **"Installed by default"** is the `installedByDefault` value: the availability default used by **updating users who never chose**. A **clean install** starts from the Essentials preset instead (§3.6.4).
- **"Energy"** is the hub's energy profile; "dynamic" means it depends on settings.

The text is English from the hub catalogs, extracted from the compiled code.

| id | group | SF Symbol | English title (hub) | English description (hub) | installed by default | energy |
|---|---|---|---|---|---|---|
| `switcher` | windowsDock | `rectangle.on.rectangle` | Window switcher | Switch apps and windows with previews | yes | inputs |
| `dockPreview` | windowsDock | `dock.rectangle` | Dock Preview | Window previews when hovering the Dock | yes | mouse |
| `dockClick` | windowsDock | `dock.arrow.down.rectangle` | Dock clicks | Click a Dock icon to minimize or cycle windows | yes | mouse |
| `windowMaximizer` | windowsDock | `arrow.up.left.and.arrow.down.right` | Maximize windows | The green button maximizes instead of full screen | yes | mouse |
| `windowLayout` | windowsDock | `rectangle.3.group` | Window layout | Arrange windows with shortcuts or edge snapping, then adjust them by dragging | yes | dynamic |
| `autoQuit` | windowsDock | `xmark.rectangle` | Quit on close | Quit apps when their last window closes | yes | inputs |
| `spacesOrder` | windowsDock | `rectangle.split.3x1` | Keep Spaces in a fixed order | Stops macOS from rearranging Spaces by most recent use, so they stay in the order you set. Your previous setting returns when this is turned off. The Dock may restart once to apply the change. | no | idle |
| `scrollInverter` | mouseKeyboard | `arrow.up.arrow.down` | Invert mouse scrolling | Invert the mouse wheel direction | yes | mouse |
| `scrollHorizontal` | mouseKeyboard | `arrow.triangle.swap` | Scroll sideways while holding a key | Hold only the selected modifier to scroll the vertical mouse wheel horizontally. Other key combinations are unchanged. | no | mouse |
| `focusFollowsMouse` | mouseKeyboard | `cursorarrow.and.square.on.square.dashed` | Focus follows mouse | Focuses the window under the pointer. | no | mouse |
| `smoothScroll` | mouseKeyboard | `cursorarrow.motionlines` | Smooth scrolling | Smooth, animated mouse scrolling | yes | mouse |
| `linearScroll` | mouseKeyboard | `arrow.up.and.down.text.horizontal` | Linear scrolling | Every notch of the mouse wheel scrolls the same distance, no matter how fast it spins. The trackpad is not affected. | no | mouse |
| `mouseAcceleration` | mouseKeyboard | `cursorarrow.rays` | Disable mouse acceleration | Removes pointer acceleration for connected mice. Your previous setting returns when this is turned off or Vorssaint quits. | yes | idle |
| `mouseNavigation` | mouseKeyboard | `arrow.left.arrow.right` | Side buttons | Side mouse buttons go back and forward | yes | mouse |
| `mouseButtonShortcuts` | mouseKeyboard | `button.programmable` | Mouse button shortcuts | Extra buttons and side-wheel directions press a key combination you choose. | yes | mouse |
| `middleClick` | mouseKeyboard | `hand.tap` | Trackpad middle click | Three finger click acts as a middle click | yes | mouse |
| `mouseClickDebounce` | mouseKeyboard | `cursorarrow.click` | Extra click filter | Ignores rapid extra clicks from worn mouse buttons without slowing normal clicks. | yes | mouse |
| `keyboardDebounce` | mouseKeyboard | `keyboard` | Debounce | Ignore accidental double key presses | yes | keyboard |
| `textSnippets` | mouseKeyboard | `text.append` | Text snippets | Short triggers expand into full text | yes | inputs |
| `superKey` | mouseKeyboard | (by source: `capslock`/`command`/`option`/`control`/`shift`) | Super key | Turns one key into the modifier combination you choose. | yes | inputs |
| `quitWindowProtection` | mouseKeyboard | `shield.lefthalf.filled` | Quit & close protection | Protects ⌘Q and ⌘W from accidental presses | yes | keyboard |
| `clipboardHistory` | clipboardFiles | `doc.on.clipboard` | Clipboard | Keep a local history of what you copy | yes | periodic |
| `pastePlain` | clipboardFiles | `doc.plaintext` | Paste as plain text | Paste text without formatting | yes | idle |
| `finderCutPaste` | clipboardFiles | `scissors` | Cut & paste | Cut and paste files in Finder | yes | keyboard |
| `finderRename` | clipboardFiles | `pencil` | Rename shortcut | Rename the selected file or folder with a shortcut you choose. | yes | keyboard |
| `shelf` | clipboardFiles | `tray.full` | Shelf | Drop files on the menu bar to hold them | yes | mouse |
| `urlCleaner` | clipboardFiles | `link` | Clean URL | Copied links lose their tracking junk | yes | periodic |
| `diskImageInstaller` | clipboardFiles | `externaldrive.badge.plus` | Disk image installer | Install the single app inside a disk image and clean up the download | no | idle |
| `mixer` | sound | `speaker.wave.2` | Volume mixer | Per-app volume, pinning and custom order | yes | dynamic |
| `soundOutputSwitcher` | sound | `hifispeaker` | Output switcher | Cycle sound outputs with a shortcut | yes | idle |
| `audioPriority` | sound | `list.number` | Audio device priority | Automatically use your preferred audio devices | no | idle |
| `micMute` | sound | `mic.slash` | Mute microphone | Mute the microphone from anywhere | yes | idle |
| `musicBlock` | sound | `music.note` | Music app blocker | Block music launches from detected media keys | yes | keyboard |
| `keepAwake` | energyDisplay | `moon.zzz.fill` | Keep awake | Keep the Mac awake on demand | yes | idle |
| `brightness` | energyDisplay | `display.2` | Displays | Brightness and power controls for every display | yes | dynamic |
| `extraBrightness` | energyDisplay | `sun.max.fill` | Extra brightness | Extra brightness on XDR displays | yes | periodic |
| `bluetoothSleep` | energyDisplay | `wave.3.right.circle` | Bluetooth on sleep | Switches Bluetooth off while the Mac sleeps, so headphones in a bag stop connecting to it. | yes | idle |
| `quickLauncher` | tools | `wand.and.rays` | Quick panel | A floating panel with your favorite tools | yes | idle |
| `quickToggles` | tools | `togglepower` | Quick toggles | One-click actions like dark mode and Trash | yes | idle |
| `colorPicker` | tools | `eyedropper` | Color picker | Pick any color on screen | yes | idle |
| `screenOCR` | tools | `text.viewfinder` | Copy text from screen | Copy text or QR codes from anything on screen | yes | idle |
| `cleaningMode` | tools | `bubbles.and.sparkles` | Cleaning Mode | Lock keyboard and screen for cleaning | yes | idle |
| `mediaTools` | tools | `photo.on.rectangle.angled` | Media | Compress videos, images and GIFs | yes | idle |
| `cleaner` | tools | `sparkles` | Cleaner | Clear caches and junk files (+ " · " + WhatsApp downloads text when that is enabled) | yes | idle |
| `uninstaller` | tools | `trash` | Uninstaller | Remove apps and their leftovers | yes | idle |
| `homebrew` | tools | `shippingbox` | Homebrew | Keep Homebrew packages up to date | yes | idle |
| `appUpdates` | tools | `arrow.down.app` | App updates | Find and install updates for the apps you have | yes | dynamic |
| `screenshot` | tools | `camera.viewfinder` | Screenshot | Captures an area, window or screen and annotates it | yes | idle |
| `cameraPreview` | tools | `web.camera` | Camera preview | Opens a floating mirror with your camera | yes | idle |
| `radialMenu` | tools | `circle.grid.cross` | Radial menu | Opens a wheel of your favorite actions around the pointer | yes | dynamic |
| `scratchpad` | tools | `note.text` | Scratchpad | Floating pads for short-lived notes | yes | idle |
| `commandBar` | tools | `command` | Command Bar | One field that finds and runs everything the app does | yes | idle |
| `screenRecorder` | tools | `record.circle` | Screen recording | Records an area, window or screen and edits it afterwards | yes | idle |
| `wallpaper` | tools | `photo.on.rectangle` | Wallpaper | Pick a still wallpaper without opening System Settings | no | idle |
| `killProcess` (beta) | tools | `xmark.octagon` | Kill Process | Search running processes and force quit, restart, or kill process trees | no | idle |
| `portManager` | tools | `network` | Port Manager | View active listening ports and, with Kill Process installed, terminate the processes using them | no | idle |
| `notch` | dynamicIsland | `macbook` | Dynamic Island | Your music, controls and everyday tools, together at the top of your screen. Optional. Turn it off to keep using the separate panels. | yes | periodic |
| `notchCalendar` | dynamicIsland | `calendar` | Calendar | Browse the month and your upcoming appointments in the Dynamic Island. | yes | periodic |
| `notchNotifications` | dynamicIsland | `bell` | Notifications | New system notifications in the Dynamic Island. | yes | idle |
| `notchGestures` | dynamicIsland | `hand.draw` | Dynamic Island Gestures | Open and close the Dynamic Island with scrolling, and swipe to change tracks. | yes | idle |
| `notchTimer` | dynamicIsland | `timer` | Timer | Timers, a stopwatch and focused work sessions in the Dynamic Island. | yes | idle |
| `notchAccessories` | dynamicIsland | `battery.25percent` | Accessory alerts | Show connected accessories and warn once when their battery falls to 20%. | yes | periodic |
| `notchLyrics` | dynamicIsland | `quote.bubble` | Lyrics | Follow the lyrics of the current song in the Dynamic Island. | yes | periodic |
| `notchQueue` | dynamicIsland | `list.bullet` | Up next | See the actual upcoming songs shared by your player. | yes | idle |
| `notchLiveEqualizer` | dynamicIsland | `waveform` | Bars follow the music | Move the Dynamic Island bars with the sound your player makes. | yes | periodic |
| `notchDownloads` | dynamicIsland | `arrow.down.circle` | Downloads | See files arriving in a folder you choose, directly in the Dynamic Island. | yes | idle |
| `notchAgents` | dynamicIsland | `sparkles` | AI Agents | Follow plan limits, tokens, API value and the work in progress of Claude, Codex, OpenCode and GitHub Copilot in the Dynamic Island. | yes | periodic |
| `notchWatch` | dynamicIsland | `eye` | Watch | Turn any part of any window into a live activity in the Dynamic Island, with an alert when it changes, finishes or shows what you are waiting for. | yes | idle |
| `notchMascot` | dynamicIsland | `face.smiling` | Companion | A little friend in the Dynamic Island who says hello now and then and reacts to what happens on your Mac. | no | periodic |
| `monitorCPU` | monitor | `cpu` | CPU | Processor usage and temperature | yes | periodic |
| `monitorGPU` | monitor | `rectangle.connected.to.line.below` | GPU | Graphics usage and temperature | yes | periodic |
| `monitorMemory` | monitor | `memorychip` | Memory | Memory use and pressure | yes | periodic |
| `monitorNetwork` | monitor | `network` | Network | Network speed and usage | yes | periodic |
| `monitorDisk` | monitor | `internaldrive` | Disks | Disk space and activity | yes | periodic |
| `monitorPower` | monitor | `bolt.fill` | Power | Battery, power and charging | yes | periodic |
| `connectedDevices` | monitor | `cable.connector` | Connected Devices | Count connected external USB peripherals | yes | periodic |
| `fanControl` (beta) | monitor | `fanblades.fill` | Fan Control | Control fans manually or with temperature curves while seeing live and target RPM | no | idle |

**Hub permissions per feature.** These are the static permission sets behind the "Permissions" metadata button, listed in the order the popover and tooltip show them. 49 features show "Permissions"; the other 28 show their energy label. The portal's "Used by" line starts from these sets, then applies the dynamic rules in §3.6.6. Onboarding explains only Accessibility and Screen Recording.

| Permission set | Features |
|---|---|
| accessibility | scrollInverter, scrollHorizontal, focusFollowsMouse, smoothScroll, linearScroll, mouseNavigation, mouseButtonShortcuts, middleClick, keyboardDebounce, textSnippets, superKey, mouseClickDebounce, dockClick, windowMaximizer, windowLayout, autoQuit, quitWindowProtection, cleaningMode, pastePlain, radialMenu, commandBar, finderRename, keepAwake, brightness, musicBlock, notchNotifications (26) |
| accessibility, automationFinder | finderCutPaste |
| automationFinder | quickToggles |
| accessibility, screenRecording | switcher, dockPreview |
| screenRecording | screenOCR, screenshot |
| screenRecording, accessibility, audioCapture, microphone | screenRecorder |
| camera | cameraPreview |
| fullDiskAccess, filesAndFolders, notifications | cleaner |
| fullDiskAccess, automationFinder | uninstaller |
| automationTerminal, appManagement | homebrew |
| notifications, appManagement | appUpdates |
| appManagement | diskImageInstaller |
| audioCapture | notchLiveEqualizer |
| audioCapture, accessibility | mixer |
| filesAndFolders | notchDownloads |
| screenRecording, notifications | notchWatch |
| calendar | notchCalendar |
| accessibility, automationPlayback | notch |
| notifications | monitorCPU, monitorMemory, monitorDisk, monitorPower |
| none | mouseAcceleration, spacesOrder, clipboardHistory, shelf, urlCleaner, soundOutputSwitcher, audioPriority, micMute, extraBrightness, bluetoothSleep, quickLauncher, colorPicker, mediaTools, scratchpad, wallpaper, killProcess, portManager, monitorGPU, monitorNetwork, connectedDevices, fanControl, notchGestures, notchTimer, notchAccessories, notchLyrics, notchQueue, notchAgents, notchMascot (28) |

## Appendix B — Where things live in the macOS source

| Topic | Files |
|---|---|
| Lifecycle, windows, popover, tray recovery | `App/AppDelegate.swift`, `main.swift` |
| Feature availability and bindings | `App/FeatureRuntime.swift`, `Core/FeatureCatalog.swift`, `Core/FeaturePresets.swift` |
| Tray item, glyph, title | `App/StatusItemController.swift`, `App/MenuBarRenderer.swift`, `App/MenuBarSpacingSupport.swift`, `App/StatusItemAnchorSupport.swift`, `App/MenuBarAllowanceSupport.swift`, `App/ReopenRequestSupport.swift`, `App/AppAppearanceController.swift` |
| Panel | `UI/MenuPanel/MenuPanelView.swift`, `PanelLayout.swift`, `PanelInteractionState.swift`, `UI/Theme.swift`, `UI/SharedUI.swift` |
| Settings shell | `UI/Settings/SettingsView.swift`, `SettingsWindow.swift`, `SettingsCard.swift`, `SettingsDirectory.swift`, `SettingsSearchSupport.swift`, `SettingsSidebarSupport.swift`, `SettingsSectionFocus.swift`, `FeatureVisibilitySupport.swift` (pages, anchors, router) |
| Shell pages | `GeneralSettings.swift`, `GeneralToolSettings.swift` (Menu bar), `PanelLayoutEditor.swift`, `ShortcutsSettings.swift`, `AdvancedSettings.swift`, `FeatureHubSettings.swift`; About, What's New and Support are inside `SettingsView.swift` |
| Onboarding and intros | `UI/Onboarding/OnboardingView.swift`, `WhatsNewView.swift`, `UI/UpdateHighlightsView.swift`, `UI/PermissionGuideOverlay.swift` |
| Shortcuts | `Core/GlobalShortcut.swift`, `Core/SymbolicHotKeys.swift`, `UI/ShortcutRecorderButton.swift`, `Services/HotkeyManager.swift`, `Services/ShortcutCapture.swift`, `Services/ShortcutRecordingTap.swift`, `Services/SystemShortcutTakeover*.swift`, `Services/QuickTools/QuickToolHotkey.swift`, `Services/WindowLayout/WindowLayoutSupport.swift` |
| Localization | `Core/Localization.swift`, `Core/Localizations/Strings+*.swift`, `Core/FeatureStrings.swift`, `Core/*Strings.swift` |
| Settings and backup | `Core/Defaults.swift`, `Core/SettingsBackupSupport.swift`, `Services/SettingsBackup.swift` |
| Update | `Services/Update/UpdateService.swift`, `UpdateServiceSupport.swift`, `UpdateInstallerSupport.swift`, `UpdateShowcaseMedia.swift`, `Core/ReleaseNotes.swift` |
| Feedback | `Services/Feedback/FeedbackService.swift`, `UI/Feedback/FeedbackView.swift` |
| Permissions and login | `Core/Permissions.swift`, `Services/LaunchAtLogin*.swift`, `Services/Notifier.swift`, `Services/SelfUninstall.swift`, `Support/Uninstaller.swift` |
| Diagnostics | `Support/SelfTest.swift` |
| Build and release | `build.sh`, `Tools/*.sh`, `Tools/MakeIcon.swift`, `.github/workflows/{ci,release,issue-fix-status}.yml`, `Resources/Info.plist`, `Resources/Vorssaint.entitlements` |
