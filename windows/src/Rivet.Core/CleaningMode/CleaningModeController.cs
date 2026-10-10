// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Util;

namespace Rivet.Core.Modules.CleaningMode;

public enum CleaningModeState
{
    Inactive,

    /// <summary>Keyboard and wheel blocked.</summary>
    Active,

    /// <summary>Unlock requested; waiting (at most 5 s) for mouse buttons held during the lock to come up.</summary>
    Pending,
}

public enum CleaningEndReason
{
    Unlocked,
    SessionChanged,
    FailedOpen,
    HooksLost,
    Shutdown,
}

/// <summary>
/// Cleaning Mode's state machine (spec 07 §6.4): Inactive → Active once the
/// filter is installed; an unlock request → Pending until the release gate
/// opens (or 5 s pass) → teardown on the next UI turn → Inactive. Forced
/// paths (user switch, filter lost, app quit) go straight to Inactive.
/// The filter is installed <em>before</em> anything is shown and removed
/// <em>first</em> on every exit path, so the keyboard is never locked without
/// its unlock path. Call the public members on the UI thread.
/// </summary>
public sealed class CleaningModeController : IDisposable
{
    /// <summary>Above every other subscriber (the shortcut recorder uses 1000), so the lock always sees keys first.</summary>
    public const int HookPriority = 1_000_000;

    private readonly IInputHooks _hooks;
    private readonly ICleaningPlatform? _platform;
    private readonly Func<TimeSpan> _clock;
    private IDisposable? _keyboard;
    private IDisposable? _mouse;
    private int _suspectedDeadChecks;
    private bool _teardownQueued;

    public CleaningModeController(IInputHooks hooks, ICleaningPlatform? platform = null, Func<TimeSpan>? clock = null)
    {
        _hooks = hooks;
        _platform = platform;
        _clock = clock ?? CleaningInputFilter.MonotonicNow;
        Gate = new CleaningMouseReleaseGate();
        Filter = new CleaningInputFilter(Gate, _clock);
        Filter.ProgressChanged += progress => UiThread.Post(() => ProgressChanged?.Invoke(this, progress));
        Filter.UnlockRequested += () => UiThread.Post(RequestUnlock);
        Filter.ReleaseCompleted += () => UiThread.Post(QueueTeardown);
        Filter.FailedOpen += reason => UiThread.Post(() =>
        {
            Log.Warn("cleaning", $"Released the keyboard on its own: {reason}.");
            End(CleaningEndReason.FailedOpen);
        });
    }

    public event EventHandler<CleaningModeState>? StateChanged;

    /// <summary>Unlock progress (0…5), on the UI thread.</summary>
    public event EventHandler<int>? ProgressChanged;

    public event EventHandler<CleaningEndReason>? Ended;

    public CleaningInputFilter Filter { get; }

    public CleaningMouseReleaseGate Gate { get; }

    public CleaningModeState State { get; private set; }

    public bool IsActive => State != CleaningModeState.Inactive;

    public int Progress => Filter.Progress;

    /// <summary>
    /// Installs the filter. Returns false (and changes nothing) when already
    /// active or when the hooks cannot be installed.
    /// </summary>
    public bool TryActivate()
    {
        if (IsActive)
        {
            return false;
        }

        Filter.Activate();
        try
        {
            _keyboard = _hooks.SubscribeKeyboard(OnKey, HookPriority);
            _mouse = _hooks.SubscribeMouse(OnMouse, HookPriority);
        }
        catch (Exception ex)
        {
            Log.Error("cleaning", "Could not install the input filter; not locking.", ex);
            RemoveFilter();
            return false;
        }

        _suspectedDeadChecks = 0;
        _teardownQueued = false;
        SetState(CleaningModeState.Active);
        return true;
    }

    /// <summary>Esc ×5 or the Unlock button.</summary>
    public void RequestUnlock()
    {
        if (!IsActive)
        {
            return;
        }

        Filter.NoteUnlockRequested();
        var decision = Gate.RequestUnlock(_clock());
        if (decision == ReleaseGateDecision.TearDown)
        {
            QueueTeardown();
        }
        else
        {
            SetState(CleaningModeState.Pending);
        }
    }

    /// <summary>
    /// Periodic work while locked (every ~250 ms on the UI thread): proves the
    /// UI is alive to the hook-side safety net, enforces the release deadline
    /// and notices when Windows removed the hooks.
    /// </summary>
    public void Tick()
    {
        if (!IsActive)
        {
            return;
        }

        Filter.Heartbeat();
        if (State == CleaningModeState.Pending && Gate.Poll(_clock()) == ReleaseGateDecision.TearDown)
        {
            QueueTeardown();
            return;
        }

        CheckHooksAlive();
    }

    /// <summary>Session switch, app quit, uninstall: end now, bypassing the release gate.</summary>
    public void ForceEnd(CleaningEndReason reason) => End(reason);

    public void Dispose()
    {
        if (IsActive)
        {
            End(CleaningEndReason.Shutdown);
        }
    }

    private bool OnKey(ref KeyboardHookEvent e) => Filter.OnKey(e.VirtualKey, e.Action);

    private bool OnMouse(ref MouseHookEvent e) => Filter.OnMouse(e.Kind);

    /// <summary>
    /// Windows silently drops a low-level hook that once answered too slowly.
    /// If the system saw input clearly newer than anything our hooks reported,
    /// twice in a row, the filter is gone: fail open and say so.
    /// </summary>
    private void CheckHooksAlive()
    {
        if (_platform?.LastInputTick() is not { } lastInput)
        {
            return;
        }

        var seen = Filter.LastEventTick;
        if (unchecked(lastInput - seen) > 3000)
        {
            if (++_suspectedDeadChecks >= 2)
            {
                Log.Warn("cleaning", "The input hooks stopped reporting events; ending Cleaning Mode.");
                End(CleaningEndReason.HooksLost);
            }
        }
        else
        {
            _suspectedDeadChecks = 0;
        }
    }

    private void QueueTeardown()
    {
        if (!IsActive || _teardownQueued)
        {
            return;
        }

        // Next UI turn: the Unlock button's click finishes before its window goes away.
        _teardownQueued = true;
        UiThread.Post(() => End(CleaningEndReason.Unlocked));
    }

    private void End(CleaningEndReason reason)
    {
        if (!IsActive)
        {
            return;
        }

        RemoveFilter();
        _teardownQueued = false;
        SetState(CleaningModeState.Inactive);
        Ended?.Invoke(this, reason);
    }

    private void RemoveFilter()
    {
        // The filter goes first on every path, before any window or feature is touched.
        Filter.Deactivate();
        _keyboard?.Dispose();
        _keyboard = null;
        _mouse?.Dispose();
        _mouse = null;
        Gate.Reset();
    }

    private void SetState(CleaningModeState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }
}
