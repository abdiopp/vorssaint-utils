// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Platform;

namespace Rivet.Core.Modules.CleaningMode;

/// <summary>Why the filter released the keyboard on its own.</summary>
public enum CleaningFailOpenReason
{
    /// <summary>The UI thread stopped answering while locked.</summary>
    UiNotResponding,

    /// <summary>An unlock was requested but the teardown never ran.</summary>
    TeardownStalled,
}

/// <summary>
/// The decision logic that runs inside the shared low-level hooks while the
/// keyboard is locked (spec 07 §3.4.4, §3.4.8):
/// <list type="bullet">
/// <item>every key event is swallowed, including Esc; key-downs feed the unlock counter;</item>
/// <item>wheel and horizontal-wheel events are swallowed;</item>
/// <item>left, right and middle buttons pass through and are tracked by the release gate;</item>
/// <item>side buttons (X1/X2) are swallowed on Windows, where they act like keyboard
/// commands (Back/Forward) and other features map them to key combinations;</item>
/// <item>pointer movement passes.</item>
/// </list>
/// Low-level hooks carry no repeat flag, so a key-down for a key that is
/// already held counts as auto-repeat. Two safety nets release the keyboard
/// when the app itself stops working: a UI heartbeat and a stalled-teardown
/// deadline. Handlers must stay tiny: Windows removes slow hooks.
/// </summary>
public sealed class CleaningInputFilter
{
    private static readonly long Frequency = Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly Func<TimeSpan> _clock;
    private readonly CleaningUnlockCounter _counter;
    private readonly HashSet<int> _heldKeys = [];
    private volatile bool _active;
    private volatile bool _failedOpen;
    private TimeSpan _lastHeartbeat;
    private TimeSpan? _unlockRequestedAt;
    private int _lastEventTick;

    public CleaningInputFilter(CleaningMouseReleaseGate gate, Func<TimeSpan>? clock = null, CleaningUnlockCounter? counter = null)
    {
        Gate = gate;
        _clock = clock ?? MonotonicNow;
        _counter = counter ?? new CleaningUnlockCounter();
    }

    /// <summary>Raised on the hook thread when the visible progress changes (0…threshold).</summary>
    public event Action<int>? ProgressChanged;

    /// <summary>Raised on the hook thread when Esc was pressed enough times.</summary>
    public event Action? UnlockRequested;

    /// <summary>Raised on the hook thread when a safety net released the keyboard.</summary>
    public event Action<CleaningFailOpenReason>? FailedOpen;

    /// <summary>Raised on the hook thread when a tracked button release completes a pending unlock.</summary>
    public event Action? ReleaseCompleted;

    public CleaningMouseReleaseGate Gate { get; }

    public bool IsActive => _active && !_failedOpen;

    public int Progress
    {
        get
        {
            lock (_gate)
            {
                return _counter.Progress;
            }
        }
    }

    public int Threshold => _counter.Threshold;

    /// <summary><see cref="Environment.TickCount"/> of the last input event the hooks delivered.</summary>
    public int LastEventTick => Volatile.Read(ref _lastEventTick);

    public static TimeSpan MonotonicNow() => TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Frequency);

    /// <summary>Starts filtering with a clean slate. Call before subscribing the handlers.</summary>
    public void Activate()
    {
        lock (_gate)
        {
            _counter.Reset();
            _heldKeys.Clear();
            _unlockRequestedAt = null;
            _lastHeartbeat = _clock();
            _failedOpen = false;
            Volatile.Write(ref _lastEventTick, Environment.TickCount);
            _active = true;
        }

        Gate.Reset();
    }

    /// <summary>Stops filtering (teardown). Safe to call repeatedly.</summary>
    public void Deactivate()
    {
        lock (_gate)
        {
            _active = false;
            _counter.Reset();
            _heldKeys.Clear();
            _unlockRequestedAt = null;
        }
    }

    /// <summary>The UI thread is alive (called about once a second while locked).</summary>
    public void Heartbeat()
    {
        lock (_gate)
        {
            _lastHeartbeat = _clock();
        }
    }

    /// <summary>Records that an unlock was requested (Esc ×5 or the Unlock button) for the stall safety net.</summary>
    public void NoteUnlockRequested()
    {
        lock (_gate)
        {
            _unlockRequestedAt ??= _clock();
        }
    }

    /// <summary>Keyboard hook handler. Returns true to swallow.</summary>
    public bool OnKey(int virtualKey, KeyAction action)
    {
        Volatile.Write(ref _lastEventTick, Environment.TickCount);
        if (!_active || _failedOpen)
        {
            return false;
        }

        int? progress = null;
        var unlock = false;
        CleaningFailOpenReason? failOpen = null;
        lock (_gate)
        {
            var now = _clock();
            if (now - _lastHeartbeat > CleaningModeConstants.UiHeartbeatLimit)
            {
                failOpen = CleaningFailOpenReason.UiNotResponding;
            }
            else if (_unlockRequestedAt is { } requested && now - requested > Gate.WaitLimit + CleaningModeConstants.PendingUnlockGrace)
            {
                failOpen = CleaningFailOpenReason.TeardownStalled;
            }

            if (failOpen is null)
            {
                var before = _counter.Progress;
                var isModifier = IsModifierOrLock(virtualKey);
                if (action == KeyAction.Down)
                {
                    var repeat = !_heldKeys.Add(virtualKey);
                    var result = isModifier
                        ? (repeat ? UnlockCounterResult.Ignored : _counter.ModifierChanged())
                        : _counter.KeyDown(virtualKey, repeat, now);
                    unlock = result == UnlockCounterResult.Unlock;
                }
                else
                {
                    _heldKeys.Remove(virtualKey);
                    if (isModifier)
                    {
                        _counter.ModifierChanged();
                    }
                }

                if (_counter.Progress != before)
                {
                    progress = _counter.Progress;
                }
            }
            else
            {
                _failedOpen = true;
            }
        }

        if (failOpen is { } reason)
        {
            FailedOpen?.Invoke(reason);
            return false;
        }

        if (progress is { } p)
        {
            ProgressChanged?.Invoke(p);
        }

        if (unlock)
        {
            NoteUnlockRequested();
            UnlockRequested?.Invoke();
        }

        // Esc presses are counted but still swallowed, so no app ever receives them.
        return true;
    }

    /// <summary>Mouse hook handler. Returns true to swallow.</summary>
    public bool OnMouse(MouseHookKind kind)
    {
        Volatile.Write(ref _lastEventTick, Environment.TickCount);
        if (!_active || _failedOpen)
        {
            return false;
        }

        switch (kind)
        {
            case MouseHookKind.Wheel:
            case MouseHookKind.HorizontalWheel:
            case MouseHookKind.XDown:
            case MouseHookKind.XUp:
                return true;
            case MouseHookKind.LeftDown:
                Gate.ButtonDown(CleaningMouseButton.Left);
                return false;
            case MouseHookKind.RightDown:
                Gate.ButtonDown(CleaningMouseButton.Right);
                return false;
            case MouseHookKind.MiddleDown:
                Gate.ButtonDown(CleaningMouseButton.Middle);
                return false;
            case MouseHookKind.LeftUp:
                Release(CleaningMouseButton.Left);
                return false;
            case MouseHookKind.RightUp:
                Release(CleaningMouseButton.Right);
                return false;
            case MouseHookKind.MiddleUp:
                Release(CleaningMouseButton.Middle);
                return false;
            default:
                return false;
        }
    }

    private void Release(CleaningMouseButton button)
    {
        if (Gate.ButtonUp(button) == ReleaseGateDecision.TearDown)
        {
            ReleaseCompleted?.Invoke();
        }
    }

    /// <summary>Shift, Ctrl, Alt, Win and the lock keys: both their press and release reset the counter.</summary>
    public static bool IsModifierOrLock(int vk) => vk is
        0x10 or 0x11 or 0x12 // Shift, Ctrl, Alt
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 // left/right variants
        or 0x5B or 0x5C // Win
        or 0x14 or 0x90 or 0x91; // Caps Lock, Num Lock, Scroll Lock
}
