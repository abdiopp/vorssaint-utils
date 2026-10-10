# Command Bar (commandBar)

Spec: `docs/specs/06-clipboard-snippets-commandbar.md` §2.7, §3.8, §5.6, §6.3–§6.10, §6.15, §7.3, §7.6; spec 05 §6.1 (shortcut table).

## Where the code lives

| Layer | Files |
|---|---|
| Core (`Rivet.Core.Launcher`) | `src/Rivet.Core/CommandBar/` — row model and source ids (`CommandRow`), ranking (`CommandBarSearch`: token scores, tiers, bonuses, caps, highlights), learning (`CommandBarLearning`: usage, query memory, query habits), engine (home list, categories, typed pipeline), session (modes, Esc ladder, selection, Tab completion, numeric arguments, confirmation, run plans), settings and preferences (pins, aliases, hidden, sources, row shortcuts, file scopes/ignores, position), calculator (`CommandBarMath`), units, colours, dates, emoji (generated table, Unicode 16 names), saved links/scripts (`CommandBarLinks`, `ScriptRunner`), Windows Settings table (`WindowsSettingsPages`), platform interface |
| Windows | `src/Rivet.Platform.Windows/CommandBar/` — `WindowsCommandBarPlatform` (apps, windows, file search, answers, power, known folders, selection read), `ShellInterop` (shortcut targets, AppUserModelIDs, shell icons) |
| Fake | `src/Rivet.Platform.Fake/CommandBar/FakeCommandBarPlatform.cs` (sample apps/windows, real bounded file walk) |
| App (`Rivet.App.Features.Launcher`) | `src/Rivet.App/Features/CommandBar/` — `CommandBarModule`, `CommandBarController` (open/close, background loads, running, personalization, row shortcuts), `CommandBarCatalog` (every row source), `CommandBarWindow`, `CommandBarProviders` (ISearchProvider adapters), `CommandBarSettingsPage`, `CommandBarLinkDialog`, `AppShortcutsDialog` |
| Tests | `tests/Rivet.Core.Tests/CommandBar/` (calculator grammar and functions, every unit category, colours, date math, emoji search, ranking, query memory, habits, links, scripts dispatch, Windows Settings table), `tests/Rivet.App.Tests/CommandBar/` (home, typed queries with several result kinds, actions and confirm modes, settings page, dialogs, providers; light and dark) |

## Implemented

* **Window and lifecycle:** opened by the shortcut role `commandBar` (Alt+Space, **off by default** as on macOS; registered with `HotkeyOptions.OverrideSystem` so it takes Alt+Space over from the window menu through the shared hook), by the Utilities tile, by "Open the bar now" and by the `commandBarWindow.toggle` action. Remembers the app in front and reads its selection (UI Automation TextPattern, 150 ms budget) *before* taking focus; suspends snippet expansion while open; Esc / click outside / running a row closes and gives focus back where appropriate. Drag the mark to move (offset saved as `dx,dy`), double-click to recenter.
* **Modes and keys:** search, argument (numeric range, "brightness 40"), confirm (red card; any other key cancels), actions (Ctrl+K), naming, shortcut capture. Keyboard map from §3.8.4 with Windows keys: Ctrl+1–9 run the Nth row, Ctrl+Enter = "Show in File Explorer", Ctrl+K actions, **Alt+P** pin (Ctrl+P is "previous row" per the map; §8.2 item 6), Ctrl+, opens Settings, Ctrl+N/Ctrl+P/↑↓ move, ←→ walk the chips when the field is empty, Tab completes. Holding Ctrl shows the Ctrl+N badges. IME composition passes through.
* **Home:** Selected, Pinned, Suggestions (most used + curated), every catalog row grouped by area (max 12 each), Paste from history; "Try" chips; category chips; compact mode with ↓ peek; empty state with "See suggestions".
* **Sources:** app actions (`ActionRegistry.Available`), generated "Turn on/off <feature>" rows, quick toggles, power (sleep, restart, shut down, log out — confirmed), Settings pages of this app (`SettingsPageRegistry`, opened with `IAppShell.OpenSettings`), Windows Settings pages (the §7.3 `ms-settings:` mapping plus Windows-only pages and classic applets), installed apps (Start-menu `.lnk` shortcuts of both Start menu folders + packaged apps from `Windows.Management.Deployment`, launched through `shell:AppsFolder`), open windows (`EnumWindows`, activate), quit rows ("quit Notepad"), snippets, clipboard history, emoji (`:` prefix, skin tones), standard folders, saved links/places/searches/scripts, selection actions (copy, search, Clean URL, UPPER/lower/Title Case, count, shelf when present), answers (battery, memory, storage, date, time), calculator, unit conversion, colours, date math, typed URLs, files. Other modules' `ISearchProvider`s are queried too (debounced, 2 s timeout each).
* **Each built-in source is also registered as an `ISearchProvider`** (`commandBar.actions`, `.settingsPages`, `.snippets`, `.links`, `.windowsSettings`, `.clipboard`, `.emoji`, `.calculator`, `.windows`, `.apps`, `.files`) so other surfaces can search them. The bar itself does not go through these adapters: it ranks all sources in one pool, which the spec's ranking needs (per-kind caps, tiers, cross-source learning).
* **Files:** Windows.Storage queries (they use the Windows Search index where the folder is indexed and enumerate otherwise; chosen over the OLE DB `Search.CollatorDSO` provider because it needs no extra package and also covers non-indexed folders), bounded walk as a last resort; every word must appear in the file name; hidden/system files, hidden folders and ignore patterns are never offered.
* **Scripts:** `.ps1` through `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File` (the argument is an argv entry, never evaluated), `.exe/.com` directly, `.bat/.cmd` through `cmd.exe` (arguments containing cmd metacharacters are refused, not escaped), no window, stdout+stderr merged, 64 KiB, 5 s timeout then the process tree is killed, output cached per opening and shown as an answer row.
* **Personalization:** pins (≤ 30), aliases (≤ 60 chars, word-clash refusal), hidden rows, sources on/off, row shortcuts (≤ 64, need a modifier, conflicts with app shortcuts refused, combinations Windows refuses listed in Settings), usage counts (persisted, machine state), query memory and habits (session only), "Forget what I use most".
* **Settings page** (Tools): open/recenter, explanation, privacy note, Try chips, compact mode, skin tone, global shortcut + recorder, App shortcuts centre (search, All/Pinned/With shortcuts, alias, recorder, pin), sources, file folders + ignores, saved shortcuts editor, row shortcuts, names, pins, hidden, forget.

## Not implemented / deviations

* **Menu commands of the front app** (Menus source) are not implemented: Windows has no uniform menu model (HMENU covers only classic apps; ribbons/WinUI/Electron would need UIA walks). The source is not offered in Settings.
* **Kill process, Uninstall, Wi-Fi on/off, sound output and keyboard-light rows** belong to other modules (killProcess, uninstaller, sound, brightness); they appear when those modules register actions or `ISearchProvider`s. The bar's own Kill/Uninstall categories are not built.
* **ASCII layout switch while open** is not implemented (Windows IMEs keep per-window state; switching layouts for the user is surprising on Windows).
* **File search with no folders falls back to recently opened files** (the brief asks for this) instead of "no file search at all" (macOS). Settings says so.
* An app row shortcut pressed while that app is already frontmost activates it again instead of hiding it (§3.8.9 step 4; Windows has no "hide app").
* OS settings panes come from a fixed table (§7.3) instead of a scan; keywords are English tokens that match in every language.
* "Restart the Mac?" etc. reworded for Windows; Return → Enter in hints.
* The quit row asks the app to close (`WM_CLOSE` to its windows); "Force quit" is not offered (Task Manager semantics belong to the killProcess module).

## Risks

* Alt+Space is taken over from every app's window menu while the shortcut is on (PowerToys Run precedent); users who rely on Alt+Space → window menu must pick another chord.
* Selection reading only works in apps exposing UIA TextPattern (most Win32 edit controls, browsers, Office; not all Electron apps).
* Packaged-app enumeration via `PackageManager.FindPackagesForUser` can take a second on machines with many packages; it runs off the UI thread and the list from the previous opening stays until it lands.
* Restoring focus after a row that types or pastes is subject to foreground-lock rules (see clipboard.md).

## Manual test checklist (Windows)

1. Settings → Command Bar: switch on "Global shortcut to open the bar". Press Alt+Space over Notepad: the bar opens (not the window menu); type immediately — no key lost.
2. Type `100 km to mi`, `2+2*3`, `20% of 480`, `#336699`, `in 3 weeks`, `:fire`: answer rows on top; Enter copies (HUD), Tab on a sum puts the reusable number in the field.
3. Type `note`: Notepad (app), open windows, files from Documents (after adding a folder), snippets/clipboard rows. Enter launches/activates. Ctrl+Enter on an app shows it in File Explorer.
4. Ctrl+K on an app row: Quit, Restart, Show in File Explorer, Pin, Name, Shortcut, Hide. Give Notepad the shortcut Ctrl+Alt+N, close the bar, press it: Notepad opens. Name it `np`, type `np`: Notepad first.
5. Type `restart`: the confirm card appears; type any letter: it cancels. Enter on "Sleep" puts the PC to sleep.
6. Type `display`, `wifi`, `bluetooth`: Windows Settings pages open the right `ms-settings:` page; `device manager` opens devmgmt.msc.
7. Select a word in Word, open the bar: SELECTED section with Copy, UPPERCASE, Count…; UPPERCASE replaces the selection in Word.
8. Add a script `~\Scripts\hello.ps1` (`param($n) "Hello $n"`) named `hi`; type `hi Sam`, pause: "Hello Sam" answer row; Enter copies it. A `.cmd` script with `a & calc` as argument must be refused (no calculator starts).
9. Add a link `gh` → `https://github.com/search?q={query}`; type `gh rivet`, Enter: the browser opens the search with `rivet` percent-encoded.
10. Turn compact mode on: the bar opens as a field only; ↓ shows suggestions. Drag the mark, reopen: same place; double-click: recentered.
11. Turn a source off in Settings (Emoji): `:fire` finds nothing. Hide a row, check "Never shown", restore it.
12. Light and dark mode: home, actions, confirm card, settings page and App shortcuts render correctly at 100 % and 150 % scaling.

## Requests for shared code

1. `IHotkeyService.Register` for row shortcuts is used directly by the controller (row shortcuts are user-defined, so they cannot be `ShortcutRole`s); please confirm this is acceptable or add a "dynamic role" API to `ShortcutManager`. Integration: accepted as is — these chords are user-defined per row and don't belong in the Keyboard shortcuts page.
2. Other modules that want rows in the bar (killProcess, uninstaller, sound output, Wi-Fi) should register an `ISearchProvider` (or actions with keywords); ids starting with `commandBar.` are reserved for this module's adapters and are skipped by the bar.
