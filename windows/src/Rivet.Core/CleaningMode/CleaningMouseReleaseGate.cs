// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.CleaningMode;

/// <summary>A mouse button as the release gate tracks it.</summary>
public enum CleaningMouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>What the gate says after an unlock request or a button release.</summary>
public enum ReleaseGateDecision
{
    /// <summary>No unlock is pending.</summary>
    Idle,

    /// <summary>Unlock pending; some button pressed during the lock is still held.</summary>
    Waiting,

    /// <summary>Tear down now (on the next UI turn).</summary>
    TearDown,
}

/// <summary>
/// Teardown waits until every mouse button pressed <em>during</em> the lock is
/// released again (at most 5 s), so an app never receives a lone button-up
/// after the lock ends. It never synthesizes a release and never reads the
/// global button state: it only learns from the events the filter saw
/// (spec 07 §3.4.5, test-asserted). Thread-safe: the hook thread feeds it,
/// the UI thread asks it.
/// </summary>
public sealed class CleaningMouseReleaseGate
{
    private readonly object _gate = new();
    private readonly HashSet<CleaningMouseButton> _held = [];
    private bool _pending;
    private TimeSpan _deadline;

    public CleaningMouseReleaseGate(TimeSpan? waitLimit = null)
    {
        WaitLimit = waitLimit ?? CleaningModeConstants.ReleaseWaitLimit;
    }

    public TimeSpan WaitLimit { get; }

    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    public int HeldCount
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>Forget everything (activation, and after the filter was re-armed).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _held.Clear();
            _pending = false;
        }
    }

    /// <summary>A button went down while locked (including during a pending unlock).</summary>
    public void ButtonDown(CleaningMouseButton button)
    {
        lock (_gate)
        {
            _held.Add(button);
        }
    }

    /// <summary>A button came up. Returns <see cref="ReleaseGateDecision.TearDown"/> when this release completes a pending unlock.</summary>
    public ReleaseGateDecision ButtonUp(CleaningMouseButton button)
    {
        lock (_gate)
        {
            // A release of a button that was already down before the lock is ignored.
            _held.Remove(button);
            if (!_pending)
            {
                return ReleaseGateDecision.Idle;
            }

            return _held.Count == 0 ? ReleaseGateDecision.TearDown : ReleaseGateDecision.Waiting;
        }
    }

    /// <summary>Esc ×5 or the Unlock button: tear down now, or wait for the held buttons (deadline = now + limit).</summary>
    public ReleaseGateDecision RequestUnlock(TimeSpan now)
    {
        lock (_gate)
        {
            if (!_pending)
            {
                _pending = true;
                _deadline = now + WaitLimit;
            }

            return _held.Count == 0 ? ReleaseGateDecision.TearDown : ReleaseGateDecision.Waiting;
        }
    }

    /// <summary>Periodic check while waiting: the deadline forces the teardown.</summary>
    public ReleaseGateDecision Poll(TimeSpan now)
    {
        lock (_gate)
        {
            if (!_pending)
            {
                return ReleaseGateDecision.Idle;
            }

            return _held.Count == 0 || now >= _deadline ? ReleaseGateDecision.TearDown : ReleaseGateDecision.Waiting;
        }
    }

    /// <summary>When the pending unlock must happen at the latest.</summary>
    public TimeSpan? Deadline
    {
        get
        {
            lock (_gate)
            {
                return _pending ? _deadline : null;
            }
        }
    }
}
