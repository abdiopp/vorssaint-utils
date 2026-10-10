# Input fixes (`input` module)

Spec: `docs/specs/07-shelf-media-input-agents-island.md` §3.7, §4.7, §6.7, §7.7, §8.
Features: `mouseClickDebounce`, `keyboardDebounce`, `mouseButtonShortcuts`,
`smoothScroll`, `scrollInverter`, `superKey`, `quitWindowProtection`.

## Where things live

| Path | Contents |
|---|---|
| `src/Rivet.Core/Input/` | Settings (raw macOS keys), pure state machines: `ClickDebounceFilter`, `KeyDebounceFilter` + `KeyDebounceOverrides` (+ macOS key code → scan code table), `SideWheelGate`, `DesktopDragTracker`, `SmoothScrollAxis/Engine/WheelUnitCarry`, `ScrollDirection`, `SuperKeyState` (+ sources, modifier sets, solo actions, input-source cycling), `QuitProtectionMachine` (+ slots, modes, scopes), `AppExclusionList`, `ChordStrokes` (exact-chord injection with mask key), platform interfaces (`IAppIdentityResolver`, `IRunningAppsMonitor`, `IWheelDeviceClassifier`, `IKeyboardInfo`, `IGlideFrameTimer`, `IAppCatalog`) and two optional cross-module contracts (`IMouseButtonClaims`, `IInputFixesControl`) |
| `src/Rivet.App/Features/Input/` | `InputModule`, one `IFeatureController` service per feature (`ClickFilterService`, `KeyDebounceService`, `MouseButtonShortcutsService`, `SmoothScrollService`, `ScrollInverterService`, `SuperKeyService`, `QuitProtectionService`), the quit-protection HUD, five Settings pages and the "Apps to leave alone" editor/app picker |
| `src/Rivet.Platform.Windows/Input/MouseButtonShortcuts/` | `WindowsAppIdentityResolver` (foreground + pointer window → process, WinEvent + worker thread), `WindowsAppCatalog` (running apps for the picker), `InputFixesRegistrar` |
| `src/Rivet.Platform.Windows/Input/SmoothScroll/` | `WindowsRawInput` (Raw Input side channel: physical key state, Precision Touchpad activity), `WindowsWheelClassifier`, `WindowsGlideFrameTimer` (high-resolution waitable timer at the pointer monitor's refresh rate) |
| `src/Rivet.Platform.Windows/Input/SuperKey/` | `WindowsKeyboardInfo` (repeat delay/rate, Caps state, next input source, scan → VK), `WindowsRunningApps` (Toolhelp snapshot poll) |
| `src/Rivet.Platform.Fake/Input/` | Fakes for every platform service (manual frame timer, settable identities, running apps, keyboard facts) |
| `tests/Rivet.Core.Tests/Input/`, `tests/Rivet.App.Tests/Input/` | State-machine tests with the spec's vectors, service tests driven through `FakeInputHooks`, snapshot tests |

## Implemented

All seven features subscribe to the shared `IInputHooks` **only while the
feature is installed and switched on** (and, for the click filter, key
debounce and button shortcuts, not suspended through `IInputFixesControl`).
Each hook session gets fresh state; configuration is an immutable snapshot
swapped atomically, so hook handlers take no settings lock and allocate
nothing per event (the few injections allocate one small stroke list).

Hook priorities (higher runs first) reproduce the spec's pipeline order:

| Priority | Feature |
|---|---|
| 3000 | key debounce (keyboard), extra click filter (mouse) |
| 2000 | Super key (before the shortcut recorder's 1000, so Super+key records as its full modifier set) |
| 1500 | mouse button shortcuts, side wheel, desktop drag |
| 1400 | smooth scrolling |
| 1300 | scroll inverter |
| 900 | quit/close protection (after the recorder) |

### Extra click filter
Exact port of the per-button state machine (left/right/middle; side buttons
never filtered): a down less than the window (5–100 ms, default 25, strict
`<`) after the last *accepted* up is swallowed with its paired up; no timers,
nothing delayed; moves never swallowed. QPC timestamps taken at hook entry.

### Key debounce
Rules 1 and 2 from the spec with strict comparisons, 5 s staleness, 0 =
accept everything, per-key overrides (0–500 ms; out of range → 5). Modifiers,
lock keys (Caps/Num/Scroll), `VK_PACKET` text and the mask key are never
filtered; key-ups always pass. Auto-repeat is inferred: a down-while-down
at least `max(150 ms, 0.8 × keyboard repeat delay)` after the accepted press
is a repeat (never filtered, never refreshes the press). Keys are identified
by scan code (+ extended bit). Overrides are stored as `"sc1E:100,scE04D:0"`;
macOS backups (`"37:100,40:0"`) are converted with the spec's key-code table
on read. UI: the 52-key picker shows layout-aware labels (`MapVirtualKeyEx`).

### Mouse button shortcuts, side wheel, desktop drag
- XBUTTON1 (`"3"`) / XBUTTON2 (`"4"`) and horizontal wheel left/right (`"-2"`/`"-1"`) → one key combination, fired once on press via `IInputHooks.SendKeys` (chord pressed exactly: held extra modifiers released behind the 0xE8 mask key, missing ones pressed around the key). Ups follow their down's decision.
- Side wheel: Windows `WM_MOUSEHWHEEL` positive = right; 250 ms burst gate (each direction once per burst); only mouse-classified events count (touchpad and unknown fractional deltas pass). The direction is read before inversion (the macOS quirk is fixed).
- Desktop drag (macOS "Spaces drag"): hold the bound button and drag ≥ 220 DIP (scaled by monitor DPI) left/right → `Ctrl+Win+Left/Right`, up ≥ 150 DIP → Task View (`Win+Tab`); 0.35 s repeat cooldown, one banked step, axis locks at the first firing. A short click is replayed as a tagged XDown+XUp at the release point. Moves are never swallowed.
- Capture flow ("Add a button or side wheel", "Choose a button"): extra-button presses and side-wheel directions are reported and consumed; the middle button is reported and passed; refusals per spec.
- Drain: when the feature stops while a swallowed press is still held, a tiny subscription swallows that press's up (10 s safety timeout) so apps never see a lone XBUTTON up (which would trigger Back/Forward).
- Per-app exclusions: pointer app **or** foreground app; an app not identified yet is left alone.

### Smooth scrolling
Spec engine verbatim (τ₀ = 0.160 − 0.120·R/100, coast stretch up to 3×,
finish threshold, minimum speed, 1/20 s frame clamp; first frame of 100 px =
18.4 px; 60 Hz ≡ 120 Hz). 1 px = 3 wheel units, so the default 40 px step is
exactly one 120-unit notch. Frames are sent with `SendWheel` from a dedicated
thread paced by a high-resolution waitable timer at the refresh rate of the
monitor under the pointer; truncation with carry, landing frame rounds to
nearest (a notch always totals exactly `step × 3` units). Only whole notches
from a mouse glide; Ctrl+wheel (zoom), touchpad, fractional/high-resolution
deltas and excluded apps pass raw. Shift is not swapped (frames go out with
Shift held; a Shift change restarts the glide). Notches it glides are
inverted here when the inverter applies, so the two never cancel.

### Scroll direction inverter
Swallows a mouse wheel event and re-sends the opposite delta. Vertical and
horizontal are independent; horizontal follows the vertical switch until it
is saved (macOS migration). Shift + vertical follows the **horizontal**
setting (Windows apps scroll that sideways). Ctrl+wheel is flipped too.
Touchpads are never touched (see "Mouse vs touchpad" below).

### Super key
Sources: Caps Lock, Right Win, Right Alt, Right Ctrl, Right Shift (raw macOS
values) plus the Menu key (`apps`, Windows-only). Default modifier set
**Ctrl+Alt+Shift** (`"control+option+shift"`); all four is allowed with an
Office-key warning, Ctrl+Alt shows an AltGr note; Shift alone is impossible.
Mechanism ("eager" injection): the source key is swallowed (repeats too) and
the missing modifiers are pressed (left-hand variants, tagged); they are
released when the source goes up. A lone press releases behind the mask key
(no Start menu, menu bar, Alt+Shift language switch or IME toggle) and then
runs the tap action: Escape / toggle Caps Lock / next input source (hold
≥ 500 ms → Caps Lock), per the solo-effect table. Physical ups of a
modifier the Super key holds are swallowed until the Super key goes up.
Starting on Caps Lock turns capitals off. Right Alt also swallows the AltGr
fake Left Ctrl (scan 0x21D). Pause while any listed app runs (2 s Toolhelp
poll). Modifiers are released on release, on stop/pause/dispose, and by a
watchdog (clamp(2 × repeat delay, 3–30 s)) that checks the physical key via
Raw Input.

### Quit and close protection
Two slots: "Quit" = Alt+F4 (+ Ctrl+Q, on by default, switchable) and
"Close" = Ctrl+W (+ Ctrl+F4, off by default). Modes hold (250–2000 ms,
default 800), double press (200–1500 ms, inclusive, ±100 ms expiry slack)
and extra modifier (Shift/Alt/Ctrl, never the chord's own base; 1.5 s hint).
Full state table from §6.7 incl. Esc cancel, base-modifier release cancel,
other-key cancel, repeat swallowing, swallow-until-release after confirming.
Confirmation re-sends the chord (exact, tagged) only if the foreground window
is still the one captured at press time; double press lets the confirming
press itself through. A swallowed Alt+F4 sends the mask key at once so the
later Alt release does not open the menu bar. Scope: all / selected only /
all except selected (foreground app resolved synchronously — kernel calls
only). HUD: near-black pill per §3.7.7 (300 DIP min width, 48/56 DIP, 13/10.5
text, progress inset 24/3/7), bottom centre of the pointer monitor's work area
18 DIP above the taskbar, non-activating, click-through, no fades.

### Apps to leave alone
One list per feature (`smoothScrollExceptions`, `scrollInverterExceptions`,
`mouseButtonExceptions`, `superKeyExceptions`, quit-protection scope lists).
Identity = executable path, case-insensitive, either separator, `\\?\`
tolerated, **version folders tolerated** (`app-1.0.9005` ≡ `app-1.0.9010`,
`Blender 4.1` ≡ `Blender 4.2`), bare `name.exe` entries match anywhere.
Packaged apps resolve through ApplicationFrameHost to their CoreWindow
process. Pointer lookups never run in the hook: the resolver keeps a
snapshot keyed by the root window under the cursor (0.5 s freshness, stale
answers still served for the same window) and refreshes on its own thread;
moves only post a refresh when the pointer leaves the cached window. Lists
are only consulted when non-empty.

### UI
Controls tab: 8 rows (quit protection has one row per slot because the
shared row binds a single setting). Settings (Mouse & keyboard): *Mouse &
touchpad* (click filter, smooth scrolling, scrolling direction), *Mouse
button shortcuts*, *Debounce*, *Super key* (keycap diagram), *Quit & close
protection*. Command Bar toggle actions `input.toggle*`.

## Mouse vs touchpad (risk #2)
WH_MOUSE_LL carries no device. Baseline: whole multiples of 120 are a notched
mouse wheel, everything else is "unknown" and left alone. On Windows,
`WindowsWheelClassifier` adds Raw Input tracking of Precision Touchpad
reports (HID usage page 0x0D / usage 0x05, `RIDEV_INPUTSINK`), registered only
while a wheel feature runs and only if a PTP is present:
- a touchpad report in the last 350 ms → the event is touchpad scrolling;
- otherwise fractional deltas count as a high-resolution mouse wheel (the inverter flips them; smooth scrolling still leaves them alone).
Raw Input cannot be matched to hook events one by one (the hook runs before
`WM_INPUT` is read), hence the activity window. Legacy (non-precision)
touchpad drivers look like a mouse and are treated as one.

## Not implemented / deviations
- Not ported (per README/spec drop list): side-button navigation, linear scrolling, "scroll sideways while holding a key", disable mouse acceleration (no feature ids in the catalog).
- Desktop drag "down" (macOS App Exposé) has no Windows equivalent and does nothing (the press still counts as a gesture, so no click is replayed).
- Click filter: a pending press older than 1 s is treated as lost (its up went to an elevated window) instead of swallowing the next press.
- Key debounce: no BounceKeys (`SPI_GETFILTERKEYS`) warning; no legacy 30/10 → 5 migration (only matters for old macOS backups).
- Quit protection always confirms by re-sending the keystroke; the `WM_SYSCOMMAND/SC_CLOSE` alternative and a per-app "keystroke vs message" list are not implemented. No App Switcher integration (no switcher on Windows).
- Super key: no "foreign mapping" warning (Scancode Map, PowerToys Keyboard Manager, AutoHotkey); no guard process (see requests); no `Scancode Map`/uiAccess mode. Input-source switching posts `WM_INPUTLANGCHANGEREQUEST` to the foreground window, which Windows applies asynchronously.
- Smooth scrolling has no acceleration curve (Windows wheels have none; constant distance per notch was chosen, spec decision 5). It does not stop on sleep (a glide lasts < 1 s).
- App picker lists running apps with visible windows (plus "Browse…" for any .exe); installed-app enumeration and app icons are not implemented.
- No hook watchdog/reinstall and no sleep/session handling of their own: the shared `WindowsInputHooks` owns the hooks (see requests).

## Risks
- **UIPI / elevated apps**: low-level hooks are not called for input going to windows of elevated apps and `SendInput` cannot reach them. All fixes are inactive while such a window is in front (stated in every page's footer). The Super key watchdog releases stranded modifiers once the source key is physically up (Raw Input); if the foreground stays elevated, the release itself may be blocked until focus returns.
- **Raw Input class ownership**: Windows allows one Raw Input target per device class per process. The Super key registers the keyboard class (while running) and the wheel features the touchpad class; another module registering the same class in-process would take it over (the watchdog then reports "unknown" and simply keeps waiting for the real key-up).
- **Raw-input consumers** (games, 3D apps) see the original wheel events *and* the injected frames/inversions: use the exception lists.
- **Hook removal**: if Windows drops the shared hooks after a timeout, features silently stop until restart (shared code).
- **Eager Super key injection** briefly shows apps a modifier press on every solo tap (masked, but an app watching raw modifier state may notice).
- Unverified on real hardware: refresh-rate pacing, PTP detection on unusual drivers, Menu-key source, AltGr layouts.

## Windows manual test checklist
Prepare: a notched mouse with side buttons and (ideally) a tilt wheel, a laptop with a Precision Touchpad, one app run as administrator (e.g. an elevated Notepad).

1. **Extra click filter**: Settings › Mouse & touchpad › switch on, window 25 ms. Double-click a file in Explorer (works). In a click-speed tester, a worn/bouncing button's duplicate clicks (<25 ms) disappear. Set 100 ms: fast double-clicks may be eaten (expected). Side buttons unaffected.
2. **Key debounce**: Settings › Debounce, 30 ms. Hold a key: it auto-repeats normally. Type fast "ere": nothing lost. Add a per-key override for A at 200 ms; tap A twice quickly → second press dropped. Shift/Ctrl/Caps are never filtered. Toggle "Filter active" appears only while on.
3. **Button shortcuts**: switch on, "Add a button or side wheel", press Back → row "Back side button", record `Alt+Left`; in Edge, Back now sends Alt+Left once per press (no repeat while held). Middle click during capture → refusal message. Tilt the wheel right with a mapping → fires once per tilt burst; touchpad two-finger horizontal scroll → never fires. Add Edge to "Apps to leave alone" → Back works natively there.
4. **Desktop drag**: create 2 virtual desktops; switch on the drag, choose Forward; hold Forward and drag 3 cm right → next desktop; up → Task View; short click → Forward still works (one click at release). "Desktops follow the drag" swaps left/right. Assigning a shortcut to the drag button disables the drag for it.
5. **Smooth scrolling**: switch on; scroll a long page in Edge with the mouse wheel → glides; one notch moves about one native notch at speed 40. Ctrl+wheel still zooms. Touchpad scrolling unchanged. Add Blender to exceptions → plain steps there. Change Response/Coast → applies on the next notch.
6. **Inverter**: switch on vertical → mouse wheel reversed, touchpad direction unchanged (also with smooth scrolling on: never double-inverted). Horizontal: tilt wheel reversed; Shift+wheel in Edge reversed only when horizontal is on.
7. **Super key**: switch on (Caps Lock, Ctrl+Alt+Shift). Hold Caps + T → app receives Ctrl+Alt+Shift+T (check a shortcut tester); Caps + click works as Ctrl+Alt+Shift+click. Tap Caps alone with "Press Escape" → Esc, no Start menu, no menu bar, no language switch. "Switch input source" with 2 layouts → tap cycles, hold ≥ 0.5 s toggles capitals. Caps Lock light never toggles from the source itself. Add notepad.exe to the list, open Notepad → status "Paused…", Caps works normally; close it → resumes. Switch off while holding Caps → no stuck modifiers (type letters afterwards). Right Alt on a German layout: no stray Ctrl.
8. **Quit & close protection**: protect Alt+F4 (hold). Alt+F4 tapped → HUD "Hold Alt+F4 to quit" with progress, window stays; hold 0.8 s → window closes; release early or press Esc → nothing; no menu bar flashes after releasing Alt. Ctrl+W double press in Edge: first press shows "Press Ctrl+W again to close", second closes the tab. Extra modifier: Ctrl+Shift+W closes the tab as Ctrl+W (note the Chromium warning). Scope "selected only" with Edge → other apps unprotected. In the elevated Notepad, Alt+F4 closes immediately (UIPI, documented).
9. **Controls tab**: each row toggles its feature; counts update.
10. **Elevated window in front**: with each feature on, focus the elevated app: input there is untouched; back in a normal window everything works; no stuck keys/buttons.
11. **Uninstall in the Features hub** while features are on: hooks stop, keys/buttons behave natively, no stuck modifiers.

## Requests for shared code
1. **Raw Input**: a shared Raw Input service in `Rivet.Platform.Windows` (one message-only window owning all registrations) so modules cannot steal each other's device classes. `WindowsRawInput` here can move there unchanged.
2. **Hook watchdog**: `WindowsInputHooks` should detect a hook removed after `LowLevelHooksTimeout` (e.g. Raw Input side channel or a periodic `GetLastInputInfo` comparison) and reinstall, notifying subscribers to reset state (an event on `IInputHooks`, e.g. `HooksReset`).
3. **Session/power events**: an `ISessionEvents` (lock/unlock, console connect/disconnect, suspend/resume) so the Super key can release modifiers on lock and features can reset on resume.
4. **Guard helper**: a `--release-modifiers <pid>` mode in `Program.cs` (wait on the parent process handle, then send key-ups for Ctrl/Alt/Shift/Win) so a crash while the Super key holds modifiers never leaves them down.
5. **Contracts**: move `IMouseButtonClaims` (radial menu → claimed buttons) and `IInputFixesControl` (Cleaning Mode → suspend the click filter, key debounce and button shortcuts) from `Rivet.Core.Input` to `Rivet.Core.Contracts`; the radial menu module should implement the first, Cleaning Mode should call the second. The radial menu's mouse hook must use a priority above 1500 so it wins over button shortcuts.
6. **Controls rows**: `PanelToggleDescriptor` support for (a) a live caption (`Func<string>`: "Caps Lock holds Ctrl+Alt+Shift.", "Global window: 5 ms"), (b) an inline sub-row control (the debounce window stepper), (c) a custom "is on" predicate (the Mouse buttons row is on when shortcuts *or* the desktop drag is on). Then quit protection could also use one row.
7. **`IInputHooks.SendKeys` return value**: report whether `SendInput` delivered every event (UIPI), so the Super key can retry a blocked release.
