# Cleaning Mode (`cleaningMode`)

Locks the keyboard so it can be wiped, with a black screen on every monitor or a small indicator (spec 07 §3.4).

## Where the code is

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/CleaningMode/`: `CleaningModeController` (state machine), `CleaningInputFilter` (the hook decisions and fail-open safety nets), `CleaningUnlockCounter` (Esc × 5), `CleaningMouseReleaseGate`, settings and constants, `ICleaningPlatform` |
| App | `src/Rivet.App/Features/CleaningMode/`: `CleaningModeModule` (action, panel tile, tray item, settings page), `CleaningModeService`, `CleaningOverlayWindows` (black overlay per monitor, corner indicator, progress dots) |
| Windows | `src/Rivet.Platform.Windows/CleaningMode/WindowsCleaningPlatform.cs` (WTS session notifications, EVENT_SYSTEM_FOREGROUND WinEvent hook, GetLastInputInfo probe, process paths) |
| Fake | `src/Rivet.Platform.Fake/CleaningMode/FakeCleaningPlatform.cs` |
| Tests | `tests/Rivet.Core.Tests/CleaningMode/` (unlock counter, release gate, filter, controller vectors), `tests/Rivet.App.Tests/CleaningMode/` (overlay, indicator light/dark, settings light/dark, lock/unlock through the fake hooks, session lock ends the lock) |

## Implemented

- Lock now from the tray menu, the panel tile, Settings, the Command Bar / Quick Launcher (0.1 s delay) and the radial menu (0.15 s). No hotkey and no timeout, as on macOS.
- Order of operations: the keyboard filter is installed (shared hooks, highest priority) **before** anything is shown, and removed **first** on every exit path (unlock, user switch or lock, uninstall, app shutdown through `Dispose`).
- While locked: every key event is swallowed, including Esc, media, volume, browser and launch keys; the wheel and horizontal wheel are swallowed; the side buttons are swallowed; pointer movement and left/right/middle clicks pass.
- Unlock: Esc five times, each within 6 s of the previous; any other key resets; auto-repeat (a key-down for a key already held) is ignored; or the Unlock button. Five progress dots.
- Mouse-release gate: teardown waits up to 5 s for buttons held during the lock to be released, never synthesising a release.
- Black overlay per monitor (opaque, topmost, absorbs clicks and touch, follows display changes, re-takes the foreground except from Task Manager) or, with "Keep screen visible" (read live), a corner card per monitor with the dots and Unlock.
- Fail-open safety nets: the filter releases the keyboard if the UI thread stops answering for 15 s, or if a requested unlock never tears down within 2 s; the session ends on a user switch or a lock (Win+L).
- The radial menu is suspended while locked (it closes any open wheel and ignores its triggers).

## What Windows never lets an app block (documented in the UI)

- **Ctrl+Alt+Del** (secure attention sequence) always works and is the guaranteed escape: from its screen choose Task Manager or Cancel; the overlay does not fight Task Manager.
- Win+L locks the PC (Cleaning Mode then ends).
- Firmware/OEM keys handled below Windows (brightness, Fn, power, some keyboard-backlight keys).
- Keys aimed at an elevated window (UIPI): the black overlay keeps the foreground to prevent this; the indicator mode cannot.

## Never stuck

- A crash or kill of the app removes its low-level hooks with the process, so the keyboard comes back immediately; nothing is written to the system.
- On a normal quit, `Dispose` removes the filter first.
- The UI heartbeat and the stalled-teardown deadline release the keyboard if the app hangs.

## Not implemented

- Touchpad gestures are not swallowed (no reliable API); the black overlay absorbs touch input.
- Pausing other input features (key debounce, click filter, middle click, mouse-button shortcuts, navigation) is the job of those modules; see the request below.

## Deviations from macOS

- Uninstalling while locked ends the lock (macOS leaves it unhandled).
- Side buttons are swallowed on Windows (they act like keys there).

## Risks

- A third-party low-level hook installed later than ours sees keys first; most keyboard utilities pass them on, but a remapper could still act on them.
- Firmware keys and Ctrl+Alt+Del are outside any app's reach (stated in the overlay).

## Windows manual test checklist

1. Tray menu › "Cleaning Mode": every monitor turns black with the instructions and five dots.
2. Type letters, Windows key, Alt+Tab, media and volume keys: nothing happens.
3. Move the mouse and click "Unlock": the lock ends.
4. Lock again; press Esc 4 times, then A: the dots reset. Press Esc 5 times quickly: unlocked.
5. Hold the left button during the lock, request unlock, keep holding: the lock ends when you release (or after 5 s).
6. Press Ctrl+Alt+Del: the security screen appears; choose Task Manager: it can be used over the overlay; end the app process: typing works at once.
7. Win+L during the lock: after signing in again the keyboard works and no overlay remains.
8. Turn on "Keep screen visible": a small card appears top-right on each monitor; apps stay clickable; keys are still blocked.
9. Plug or unplug a monitor while locked: overlays follow.
10. With an elevated Command Prompt in front, lock: the overlay takes the foreground; typing does not reach the prompt.

## Requests for shared code

1. **Cleaning Mode state for other modules**: a Core contract such as `ICleaningModeState { bool IsActive { get; } event EventHandler Changed; }` in `Rivet.Core.Contracts`, implemented by `CleaningModeService`, so the input-fix modules (key debounce, click filter, middle click, mouse-button shortcuts, navigation) can pause while the keyboard is locked (spec §3.4.7). The radial menu already subscribes to `CleaningModeService.StateChanged` directly.
