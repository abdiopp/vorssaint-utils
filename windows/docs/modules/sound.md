# Sound

Feature ids: `mixer`, `soundOutputSwitcher`, `audioPriority`, `micMute`.
Spec: `docs/specs/04-maintenance-and-sound.md` part B (§1.2, §3.9–§3.17, §4.2, §6.5–§6.11), and
`docs/specs/05-app-shell.md` §3.4.3 (panel section) and §6.1 (shortcut defaults).

## What is implemented

### Shared device layer (`AudioDeviceService`, `IAudioPlatform`)

- Active render and capture endpoints with friendly names, form factor
  (`PKEY_AudioEndpoint_FormFactor`), Bluetooth detection and priority tier from the
  device enumerator (`PKEY_Device_EnumeratorName`, falling back to the parent instance
  path in `PKEY_Device_ControllerDeviceId` / `PKEY_Device_InterfaceKey`, then the name).
- Default output and input (console role), endpoint volume and mute
  (`IAudioEndpointVolume`) with change notifications, device add/remove/state/default
  notifications (`IMMNotificationClient`). Bursts fold into one refresh: an isolated
  notification refreshes at once, notifications within 0.2 s of the last refresh fold
  into one trailing refresh; volume-only changes re-read just the levels.
- Runs while any of mixer, output switcher or audio priority is installed, and while
  mic mute is muted or still holds claims. Sessions (per-app) only while the mixer is
  installed. Every owner declares its demand; stopping releases every COM object.
- Output volume slider: one write in flight, only the newest value kept, shown at once;
  writes for a previous default are dropped; level 0 also mutes, any positive level
  unmutes.
- "All apps" switch: `IPolicyConfig::SetDefaultEndpoint` for console, multimedia and
  communications, confirmed by reading the default back; on success every per-app route
  is cleared (`ClearAllPersistedApplicationDefaultEndpoints`), the choice is remembered
  (`mixerUniversalOutputDevice`) and the new default is published immediately.
- Preferred microphone: the picker saves `preferredInputDevice`, makes it the default for
  every role, re-applies it whenever it is connected but not current, remembers the
  original input the first time and gives it back on uninstall/quit while the app still
  owns the override. Dormant while audio priority's microphone half owns the input.
- Headphone guard: lower the new default when headphones disconnect, give the level back
  when headphones return (only while the speaker still holds the applied level), and at
  quit. The pending give-back is persisted (machine state) so a crash does not lose it.

### Volume mixer (`MixerService`, panel tab "Volume mixer", Settings › Volume mixer)

- One row per app: sessions from every active output (`IAudioSessionManager2`,
  `IAudioSessionControl2`), grouped by package AUMID for packaged apps, else the
  lowercase executable path (Chromium/Electron helpers group with their app). Name from
  the package display name / version resource / session name / file name; icon from the
  package logo or the executable (`SHDefExtractIcon`), loaded in the background.
- Permanent "System sounds" row (Windows' analog of the macOS Finder row).
- Per-app slider 0–100 %, percent editor (type a value; Enter/Esc; invalid entries keep
  the field open), reset to 100 %, mute (Windows' per-app mute switch), green playing dot
  (`AudioSessionStateActive`). Writes carry a private event context; changes from
  Windows' own mixer or the app are followed and remembered. Mouse wheel over any slider
  steps 1 % (Windows steps 2 %).
- Saved volumes are re-applied whenever an app's session appears (no panel needed).
  A session that resets its own level within 2 s of appearing gets the saved level back.
- Per-app output through the undocumented `IAudioPolicyConfigFactory`: both interface ids
  are probed (Windows 11 changed it), the option is hidden when neither answers. The row
  shows the route (ours, or one chosen in Windows Settings), "Output unavailable" /
  "Using default until this device returns." when the device is away, and the hint that
  the app may need to restart its audio. New processes of a routed app are routed too.
- Pin to top, custom order (Ctrl+drag a row within its pin group, or Move up/down in the
  row menu), hide from the list, "Apps in the list" chooser, "Hide inactive apps".
  Arrangement semantics ported verbatim (slots of hidden/closed apps are kept).
- Options: hide inactive apps, headphone guard (+ level 10–100 % in steps of 5), finer
  volume steps (+ step size in Settings), and in the panel the output switcher controls
  and the audio priority lists. With only audio priority installed the tab shows the
  priority lists.

### Output switcher (`OutputSwitcherService`, Settings › Output switcher)

- Shortcut role `soundOutputSwitcher` (default Ctrl+Alt+Win+S, active only while
  `soundOutputSwitcherEnabled`), action `soundOutputSwitcher.next`, "Outputs in cycle"
  checkboxes (stored in visible order, disconnected selections kept), seeding with the
  current output when switched on with nothing selected, next-output rule of §6.9,
  switching through the "all apps" switch, HUD naming the new device (or the failure).

### Audio device priority (`AudioPriorityService`, Settings › Audio device priority)

- Output and microphone lists (≤ 64) with their switches (`audioPriorityOutputEnabled`,
  `audioPriorityInputEnabled`); new list = current, built-in, hardware, virtual; first
  non-empty snapshot is a baseline; only a change in the set of connected devices,
  switching a list on or editing it enforces (debounced 0.25 s); new devices are placed
  after 2 s (first when Windows made them current, else above the first virtual entry);
  unranked current device = hands off; last-known names kept and pruned. Reorder by
  dragging the handle, the row's context menu (Move up/down) or, in Settings, buttons.

### Mute microphone (`MicMuteService`, Settings › Mute microphone)

- Action `micMute.toggle` (quick toggles, radial menu, Command Bar), shortcut role
  `micMute` (default Ctrl+Alt+Win+M, active only while `micMuteShortcutEnabled`), tray
  badge + tooltip line through `ITrayPresence` while muted (`micMuteMenuBarIndicator`).
- Claim semantics of §3.13.3 ported verbatim (`MicMuteEngine`): every active capture
  endpoint, mute switch verified by reading back, level-0 fallback with saved levels and
  channel levels, partial results, unmute touches only claimed devices, absent claims
  carried forward and released on return, "missing ≠ empty" claim list.
- New microphones are muted on arrival while muted; the state survives quit and
  relaunch (re-asserted at launch); uninstalling the feature unmutes first.

### Finer volume steps (`PreciseVolumeService`)

- The shared low-level keyboard hook takes `VK_VOLUME_UP/DOWN` (and knobs that arrive as
  those keys), swallows them and steps the default output by 1 % or 0.5 % after reading
  the real level, with the §3.15 gate (30 ms spacing; a reversal within 300 ms needs three
  presses). Ctrl/Alt/Win + key goes to Windows unchanged. A HUD shows the level because
  Windows' own flyout no longer appears.

## Deviations from the macOS app

| macOS | Windows | Why |
|---|---|---|
| Per-app volume 0–200 % with a peak limiter (process taps) | 0–100 % (`ISimpleAudioVolume`), no limiter | Windows has no driverless per-app gain stage; noted on the Settings page |
| Mute = volume 0 with "last audible" | Windows' per-app mute switch (slider keeps its place); unmuting a row at 0 restores the last audible level | Matches Windows' own mixer, which shows the same switch |
| Finder row, `mixerShowFinder` | System sounds row, new key `mixerShowSystemSounds` | No Finder; the system sounds session is the analog |
| "System sounds" output picker | Not offered; the output picker sets every role including communications | Windows has no alerts device |
| Universal switch re-routes apps that keep an old device open | Not done | No tap; the only lever is the per-app route, which needs the app to reopen its stream |
| Headphone guard raises a speaker below the target level | Never raises (only lowers) | Deliberate, allowed by the spec; the feature exists to prevent a blast |
| Command-drag to reorder | Ctrl-drag, plus Move up/down in the row menu | Windows modifier |
| Precise roller posts macOS fine-step key events | Swallows the keys and writes the level itself, with its own HUD | No fine-step keys on Windows |
| Per-app volumes reset when taps go away | Per-app volume, mute and routes are Windows settings: they stay when the app quits or the mixer is uninstalled | Windows persists them itself; Windows' mixer shows and changes them |
| Display name / identity by bundle id | AUMID for packaged apps, else lowercase exe path | Spec §3.9.8 recommendation; changing it later needs a migration |

Windows-only settings: `mixerShowSystemSounds`, `mixerHeadphoneGuardRestore` (machine),
`preciseVolumeRollerStepPercent`, `preferredInputOriginalDevice` (machine). Shortcut
storage uses the Windows chord format; macOS values in a backup fall back to the default.

## Not implemented

- Boost above 100 % and the limiter (§6.5–6.6): no driverless path (spec S2).
- Music app blocker and AirPlay routing: dropped per spec (S13, S14).
- Peak-meter refinement of the playing dot (`IAudioMeterInformation`): the dot follows the
  session state, so apps that keep a silent stream open show as playing.
- Grouping WebView2 helpers (`msedgewebview2.exe`) under their host app: they appear as
  their own row (or under the host's package when Windows gives them its identity).
- Detecting exclusive-mode streams (which ignore session volume).
- A "Communications device" picker.

## Risks

1. **Undocumented interfaces.** `IPolicyConfig` (default device) and
   `IAudioPolicyConfigFactory` (per-app output) are called through fixed vtable slots
   (13; 25–27). Both are probed at start-up and the features fall back cleanly when
   creation fails, but a future Windows build that keeps the interface id while changing
   the layout would crash on the first call. Watch Windows Insider builds.
2. **Untested on Windows.** The Windows target compiles here (COM declarations,
   source-generated wrappers, P/Invoke signatures are type-checked), but nothing ran on a
   PC. The checklist below is the first real test.
3. **Event-context echo.** Endpoint writes carry the app's context through NAudio's
   `NotificationGuid`; session writes through the direct `ISimpleAudioVolume` calls. If a
   driver drops contexts, own writes would be "adopted" again (harmless: same value).
4. **Wired headphones on shared jacks.** Many onboard codecs switch the jack inside one
   "Speakers" endpoint; the guard cannot see that (noted on the Settings page).
5. **Volume keys that bypass the keyboard stream** (some HID consumer devices send
   `WM_APPCOMMAND` straight to the shell) are not seen by the hook and keep Windows' step.
6. **Apps that keep their device open** do not follow the default or a new per-app route
   until they reopen their stream (the row says so).

## Windows manual test checklist

Prepare: a PC with speakers, wired or Bluetooth headphones, a USB microphone or headset,
optionally VB-Audio Virtual Cable; Spotify (or any player) and a browser playing audio.

Mixer
1. Install "Volume mixer". Open the tray panel › Volume mixer: outputs and microphones
   are listed, the default is selected, levels match Windows' flyout.
2. Change the output in the picker: Windows Settings › Sound shows the new default (also
   for communications); playing apps move; per-app routes are cleared.
3. Drag the output slider and use the mouse wheel over it (1 % per notch); press the
   keyboard volume keys: the slider follows. Slide to 0: the output mutes; slide up: it
   unmutes.
4. Apps appear as rows with their name and icon (Chrome/Edge helper processes are one
   row); the green dot shows while playing; System sounds has its own row.
5. Move an app's slider: Windows' Volume mixer (Settings › System › Sound › Volume mixer)
   shows the same level. Change it there: the row follows. Restart the app: the saved
   level is applied without opening the panel.
6. Mute and unmute a row; set a row to 0 and click the speaker: the last level returns.
   Click the percentage, type `35`, Enter; type `abc`: the field stays open and outlined.
7. Per-app output (↗ button): route Spotify to the headphones. The row shows the device;
   Windows Settings › App volume and device preferences shows the same. Pause/play if
   the app does not move. Unplug the headphones: the row says it uses the default until
   the device returns; plug them back in: audio returns there. Choose "Default".
8. Pin a row, Ctrl+drag rows (only within the same group), Move up/down from the "…"
   menu, hide an app, bring it back from Options › Apps in the list, hide System sounds.
9. Options › Hide inactive apps hides idle rows at 100 % but keeps customized ones.
10. Open a dropdown and a row menu inside the panel: the panel stays open.

Headphone guard
11. Turn on "Lower volume when headphones disconnect" (25 %). Speakers at 80 %, headphones
    default, unplug them (Bluetooth: switch them off): the speakers come up at 25 %.
    Reconnect: the speakers are back at 80 %. Repeat, but change the speaker level by hand
    before reconnecting: it is left alone. Disconnect again and quit the app: 80 % returns.

Microphone
12. Pick a microphone in the panel: it becomes the default input (every role). Switch the
    input in Windows Settings: the app switches back. Choose "Default": the original input
    returns. Quit the app while a preferred microphone is set: the original returns.
13. Move the input level slider; it is disabled while "Mute microphone" is on.

Output switcher
14. Install "Output switcher", switch it on (the current output is pre-selected), tick two
    more outputs, press Ctrl+Alt+Win+S repeatedly: the default cycles through the ticked
    outputs and a HUD names each one. Untick everything connected: the HUD says to select
    one. Record another shortcut on the Settings page; Ctrl+Alt+Win+S stops working.

Audio priority
15. Install "Audio device priority": both lists start with the current device. Put the
    headphones first. Unplug them: the next device takes over; plug them in: they become
    the default after a moment. Switch the default by hand: it is kept until hardware
    changes. Plug a new USB device: after ~2 s it is listed above virtual devices and not
    selected. With the microphone list on, the mixer's microphone picker sets the system
    input directly.

Mute microphone
16. Enable the global shortcut, press Ctrl+Alt+Win+M: every microphone is muted (Windows
    Settings › Sound › Input shows them muted), a HUD appears, the tray icon gets a red
    badge and a tooltip line. Plug in a USB microphone: it is muted on arrival. Press
    again: all come back, except a microphone that was already muted before.
17. Mute, quit and relaunch: still muted, badge back. Uninstall "Mute microphone" in the
    Features hub: microphones unmute first.

Finer volume steps
18. Turn on "Use finer volume steps": each volume key press moves 1 % (0.5 % when chosen),
    a HUD shows the level and Windows' flyout does not appear; Ctrl+volume key steps 2 %
    with Windows' flyout. Turn it off: normal behaviour returns.

Shutdown
19. Quit from the tray: no audio change except the documented give-backs; Process Explorer
    shows no leftover audio threads (the app exits within ~3 s).

## Requests for shared code

1. **`src/Rivet.App/Controls/ShortcutRoleRow.cs` throws for every registered role** (this
   module is the first to register roles, so Settings › Keyboard shortcuts now shows the
   exception text, see `tests/artifacts/snapshots/settings-shortcuts.png`). `Refresh()` and
   `ShowMessage()` cast `this.FindResource(...)` to `IBrush`, but a control that is not
   attached yet (it is built in the page constructor) gets `AvaloniaProperty.UnsetValue`.
   Fix: use `as IBrush` instead of the casts, e.g.
   `ShortcutState.Active => this.FindResource("SuccessBrush") as IBrush ?? Brushes.Green`
   (same for `WarningBrush`, `TextTertiaryBrush`, and `_message.Foreground = (isError ?
   this.FindResource("WarningBrush") : this.FindResource("TextSecondaryBrush")) as IBrush;`),
   and call `Refresh()` again from `OnAttachedToVisualTree`, or bind the brushes with
   `GetResourceObservable(...).ToBinding()`. Until then this module's pages use a local
   copy (`Features/Sound/SoundShortcutRow.cs`), which can be deleted afterwards.
2. Optional: a short "Volume" OSD in the shell (`IHud` with a level bar) would read
   better than the text HUD the finer-steps feature shows.
3. Optional: a `--selftest` line on the Windows CI runner that creates
   `PolicyConfigClient` and activates `Windows.Media.Internal.AudioPolicyConfig` (both
   interface ids) and reports which answered, as an early warning for Windows changes
   (spec §8.3 risk 1). The app already logs the same probe at the first device start
   ("Audio: default switch …, per-app output …").

## Integration notes

- Files: `src/Rivet.Core/Sound/*` (models, settings, platform interface, pure rules,
  services), `src/Rivet.Platform.Windows/Sound/*` (NAudio devices + direct COM sessions,
  policy interfaces, audio thread, app identity, icons), `src/Rivet.Platform.Fake/Sound/*`
  (sample PC), `src/Rivet.App/Features/Sound/*` (module, panel tab, Settings pages),
  `src/Rivet.Core/Resources/i18n-win/sound.en-US.json`, tests under `tests/*/Sound/`.
- No shared file was edited. Flat Win32/WinRT calls use `[LibraryImport]` in
  `SoundNative.cs` (no `NativeMethods.txt`), COM uses `[GeneratedComInterface]` /
  `[GeneratedComClass]` with one `StrategyBasedComWrappers`, so nothing depends on the
  built-in COM marshaller and the code stays trimming/AOT-friendly like NAudio 3.
- Threading: all Core Audio objects live on one MTA thread (`AudioThread`, named
  "Audio"); COM callbacks only post work; services keep their state on the UI thread via
  `UiThread`. On quit, the DI container disposes the services (giving back the guard's
  level and the original input, ≤ 3 s) before the platform releases its COM objects.
- The panel tab id is `mixer` (Order 7, between Displays and System per spec 05 §3.4.3; gated by `mixer` or `audioPriority`). Settings
  page ids: `mixer`, `soundOutputSwitcher`, `audioPriority`, `micMute` (category Sound).
- Other modules can invoke `micMute.toggle`, `soundOutputSwitcher.next` and
  `sound.openMixer` by id.
- The UI snapshot tests write `sound-*.png`; they install audio priority temporarily and
  restore the shared host afterwards.
