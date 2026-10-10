// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.Core.Awake;

public enum KeepAwakeTrigger
{
    Manual,
    Automation,
}

public enum KeepAwakeEndReason
{
    Manual,
    Timer,
    Battery,
    Quit,
}

[Flags]
public enum AutomationCondition
{
    None = 0,
    ExternalDisplay = 1,
    Power = 2,
    Apps = 4,
}

public enum LidModeStatus
{
    /// <summary>Not preferred (or this PC has no lid).</summary>
    Off,

    /// <summary>Preferred; applied whenever a session runs.</summary>
    Ready,

    /// <summary>Applied now: closing the lid does nothing.</summary>
    Active,

    /// <summary>Windows refused the change (policy or permissions).</summary>
    Failed,
}

/// <summary>An immutable view of the session.</summary>
public sealed record KeepAwakeState
{
    public bool IsActive { get; init; }

    /// <summary>Null while active means indefinitely.</summary>
    public DateTimeOffset? EndTime { get; init; }

    public KeepAwakeTrigger Trigger { get; init; }

    /// <summary>The preset that started the session (null for "until" and automation).</summary>
    public int? SessionMinutes { get; init; }

    public AutomationCondition ActiveConditions { get; init; }

    /// <summary>Paused while the PC is locked ("Pause while locked").</summary>
    public bool IsPaused { get; init; }

    /// <summary>Battery protection refused the last start: the charge it saw.</summary>
    public int? BatteryBlockedPercent { get; init; }

    public LidModeStatus LidMode { get; init; }

    public static KeepAwakeState Idle { get; } = new();
}

/// <summary>
/// Keep Awake sessions (spec §3.18): presets, "until", indefinite, extend,
/// last pick, battery protection, pointer jiggle, pause while locked,
/// automation (external display / power / apps, Any or All), the timer
/// hand-over to automation and the "keep going with the lid closed" mode.
/// Runs on the UI thread; platform events and timers arrive there.
/// </summary>
public sealed class KeepAwakeManager : IDisposable
{
    public static readonly TimeSpan BatteryCheckInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AppsPollInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DisplayDebounce = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan PowerAndAppsDebounce = TimeSpan.FromMilliseconds(100);
    public const int SleepRetries = 10;
    public static readonly TimeSpan SleepRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsStore _settings;
    private readonly IKeepAwakeTimers _timers;
    private readonly IPowerRequests _requests;
    private readonly IPowerSource _power;
    private readonly ISessionLockMonitor _lock;
    private readonly IDisplayTopology _displays;
    private readonly IRunningApps _apps;
    private readonly IPointerJiggler _jiggler;
    private readonly ILidActionController _lid;
    private readonly INotificationService? _notifications;
    private readonly List<IDisposable> _subscriptions = [];
    private KeepAwakeState _state = KeepAwakeState.Idle;
    private IDisposable? _endTimer;
    private IDisposable? _batteryTimer;
    private IDisposable? _jiggleTimer;
    private IDisposable? _appsTimer;
    private IDisposable? _evaluateTimer;
    private bool _available;
    private bool _recovered;
    private bool _quitting;
    private bool _suppressed;
    private bool _holdsApplied;
    private bool? _lastExternalDisplay;
    private IReadOnlySet<string> _running = new HashSet<string>();
    private bool _lidFailed;

    public KeepAwakeManager(
        ISettingsStore settings,
        IKeepAwakeTimers timers,
        IPowerRequests requests,
        IPowerSource power,
        ISessionLockMonitor sessionLock,
        IDisplayTopology displays,
        IRunningApps apps,
        IPointerJiggler jiggler,
        ILidActionController lid,
        INotificationService? notifications)
    {
        _settings = settings;
        _timers = timers;
        _requests = requests;
        _power = power;
        _lock = sessionLock;
        _displays = displays;
        _apps = apps;
        _jiggler = jiggler;
        _lid = lid;
        _notifications = notifications;
    }

    public KeepAwakeState State => _state;

    public bool IsActive => _state.IsActive;

    /// <summary>Raised on the UI thread after every state change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The time left in a timed session (null while idle or indefinite).</summary>
    public TimeSpan? Remaining => _state is { IsActive: true, EndTime: { } end } ? end - _timers.Now : null;

    /// <summary>Automation conditions the user switched on (Applications only with apps in the list).</summary>
    public AutomationCondition EnabledConditions
    {
        get
        {
            var enabled = AutomationCondition.None;
            if (_settings.Get(KeepAwakeSettings.AutomationExternalDisplay)) enabled |= AutomationCondition.ExternalDisplay;
            if (_settings.Get(KeepAwakeSettings.AutomationPower)) enabled |= AutomationCondition.Power;
            if (_settings.Get(KeepAwakeSettings.AutomationApps) && _settings.Get(KeepAwakeSettings.AutomationAppList).Count > 0) enabled |= AutomationCondition.Apps;
            return enabled;
        }
    }

    /// <summary>Feature controller: starts monitoring when installed, ends everything when uninstalled.</summary>
    public void Sync(bool available)
    {
        if (available == _available)
        {
            return;
        }

        _available = available;
        if (available)
        {
            Subscribe();
            if (!_recovered)
            {
                RecoverAtLaunch();
            }

            UpdateAutomationMonitoring();
            if (!_state.IsActive)
            {
                SetLidStatus(LidStatusAtRest());
            }

            EvaluateAutomation();
        }
        else
        {
            if (_state.IsActive)
            {
                EndSession(KeepAwakeEndReason.Manual);
            }

            Unsubscribe();
            _appsTimer?.Dispose();
            _appsTimer = null;
        }
    }

    /// <summary>
    /// Launch order (spec §3.18.1/§3.18.5): restore a lid action a crash left
    /// behind, then auto-start, then let automation decide.
    /// </summary>
    private void RecoverAtLaunch()
    {
        _recovered = true;
        if (_settings.Get(KeepAwakeSettings.LidRecoveryMarker) is not null)
        {
            Log.Info("keep-awake", "Restoring the lid action left by an earlier session.");
            RestoreLid(synchronousSleep: false);
        }

        if (_settings.Get(KeepAwakeSettings.AutoStart))
        {
            Activate(_settings.Get(KeepAwakeSettings.DefaultDurationMinutes));
        }
    }

    /// <summary>Starts a preset (0 = indefinitely) and records it as the last pick.</summary>
    public void Activate(int minutes)
    {
        minutes = KeepAwakeSettings.SanitizeMinutes(minutes);
        _settings.Set(KeepAwakeSettings.DefaultDurationMinutes, minutes);
        _settings.Set(KeepAwakeSettings.SwitchUsesUntil, false);
        StartSession(KeepAwakeTrigger.Manual, minutes == 0 ? null : _timers.Now.AddMinutes(minutes), minutes);
    }

    /// <summary>Runs until a time (ignored when it already passed) and records it as the last pick.</summary>
    public void ActivateUntil(DateTimeOffset until)
    {
        if (until <= _timers.Now)
        {
            return;
        }

        _settings.Set(KeepAwakeSettings.SwitchUsesUntil, true);
        _settings.Set(KeepAwakeSettings.UntilTime, KeepAwakeSettings.ToReferenceSeconds(until));
        StartSession(KeepAwakeTrigger.Manual, until, null);
    }

    /// <summary>The main switch, shortcut and menus: the last "until" if still ahead, else the saved duration.</summary>
    public void StartLastPick()
    {
        if (_settings.Get(KeepAwakeSettings.SwitchUsesUntil))
        {
            var saved = KeepAwakeSettings.FromReferenceSeconds(_settings.Get(KeepAwakeSettings.UntilTime));
            if (_settings.Get(KeepAwakeSettings.UntilTime) > 0 && saved > _timers.Now)
            {
                ActivateUntil(saved);
                return;
            }
        }

        Activate(_settings.Get(KeepAwakeSettings.DefaultDurationMinutes));
    }

    /// <summary>Stops a running session (suppressing automation while its conditions hold) or starts the last pick.</summary>
    public void Toggle()
    {
        if (_state.IsActive)
        {
            Stop();
        }
        else
        {
            StartLastPick();
        }
    }

    /// <summary>
    /// Ends the session by hand. When it was automatic, or automation would
    /// start one right now, automation is suppressed until its conditions clear.
    /// </summary>
    public void Stop()
    {
        if (!_state.IsActive)
        {
            return;
        }

        if (_state.Trigger == KeepAwakeTrigger.Automation || AutomationSatisfied())
        {
            _suppressed = true;
        }

        EndSession(KeepAwakeEndReason.Manual);
    }

    /// <summary>Adds time to a timed session: end = max(end, now) + minutes.</summary>
    public void Extend(int minutes)
    {
        if (_state is not { IsActive: true, EndTime: { } end })
        {
            return;
        }

        var now = _timers.Now;
        Update(_state with { EndTime = (end > now ? end : now).AddMinutes(minutes) });
        ScheduleEnd();
    }

    /// <summary>App quit: release everything and restore the lid action now (sleeping if the lid is closed).</summary>
    public void Shutdown()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        if (_state.IsActive)
        {
            EndSession(KeepAwakeEndReason.Quit);
        }
        else
        {
            ReleaseHolds(synchronousSleep: true);
        }

        Unsubscribe();
    }

    /// <summary>The next occurrence of a wall-clock time: today if still ahead, else tomorrow (DST-safe).</summary>
    public static DateTimeOffset ResolveUntil(TimeOnly time, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        for (var dayOffset = 0; dayOffset <= 2; dayOffset++)
        {
            var date = local.Date.AddDays(dayOffset);
            var wall = date + time.ToTimeSpan();
            if (zone.IsInvalidTime(wall))
            {
                // Skipped by a DST jump: use the first valid minute after it.
                wall = wall.AddHours(1);
            }

            var candidate = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (candidate > now)
            {
                return candidate;
            }
        }

        return now.AddDays(1);
    }

    private void StartSession(KeepAwakeTrigger trigger, DateTimeOffset? end, int? minutes, AutomationCondition conditions = AutomationCondition.None)
    {
        if (!_available || _quitting)
        {
            return;
        }

        if (!BatteryAllows(out var percent))
        {
            Update(_state with { BatteryBlockedPercent = percent });
            return;
        }

        var paused = _lock.IsLocked && _settings.Get(KeepAwakeSettings.PauseWhenLocked);
        Update(new KeepAwakeState
        {
            IsActive = true,
            EndTime = end,
            Trigger = trigger,
            SessionMinutes = minutes,
            ActiveConditions = conditions,
            IsPaused = paused,
        });
        ScheduleEnd();
        ApplyHolds();
        Log.Info("keep-awake", $"Session started ({trigger}, {(end is null ? "indefinitely" : "until " + end.Value.ToString("O"))}{(paused ? ", paused while locked" : string.Empty)}).");
    }

    private void EndSession(KeepAwakeEndReason reason)
    {
        if (!_state.IsActive)
        {
            return;
        }

        _endTimer?.Dispose();
        _endTimer = null;
        Update(KeepAwakeState.Idle with { LidMode = _state.LidMode });
        ReleaseHolds(synchronousSleep: reason == KeepAwakeEndReason.Quit);
        Log.Info("keep-awake", $"Session ended ({reason}).");
        switch (reason)
        {
            case KeepAwakeEndReason.Timer:
                Notify(L.Get("Strings.notifySessionEndedTitle"), L.Get("Strings.notifySessionEndedBody"));
                break;
            case KeepAwakeEndReason.Battery:
                Notify(L.Get("Strings.notifyBatteryTitle"), L.Get("Strings.notifyBatteryBody"));
                break;
        }

        if (reason != KeepAwakeEndReason.Quit)
        {
            EvaluateAutomationSoon(PowerAndAppsDebounce);
        }
    }

    private void Notify(string title, string body) =>
        _notifications?.Show(new NotificationRequest { Title = title, Body = body, Tag = "keep-awake" });

    private void ScheduleEnd()
    {
        _endTimer?.Dispose();
        _endTimer = null;
        if (_state is { IsActive: true, EndTime: { } end })
        {
            var due = end - _timers.Now;
            _endTimer = _timers.Schedule(due > TimeSpan.Zero ? due : TimeSpan.Zero, OnEndTimer);
        }
    }

    private void OnEndTimer()
    {
        if (_state is not { IsActive: true, EndTime: { } end })
        {
            return;
        }

        if (_timers.Now < end)
        {
            ScheduleEnd();
            return;
        }

        if (_state.IsPaused)
        {
            // Handled when the PC is unlocked.
            return;
        }

        HandleTimerEnd();
    }

    /// <summary>A manual session that ends while automation would start one hands over silently.</summary>
    private void HandleTimerEnd()
    {
        if (_state.Trigger == KeepAwakeTrigger.Manual && AutomationWouldStart())
        {
            Log.Info("keep-awake", "Timer ended; automation takes over.");
            Update(_state with { EndTime = null, Trigger = KeepAwakeTrigger.Automation, SessionMinutes = null, ActiveConditions = MatchingConditions() & EnabledConditions });
            _endTimer?.Dispose();
            _endTimer = null;
            return;
        }

        EndSession(KeepAwakeEndReason.Timer);
    }

    private bool BatteryAllows(out int? percent)
    {
        percent = _power.BatteryPercent;
        var limit = _settings.Get(KeepAwakeSettings.BatteryLimitPercent);
        return limit == 0 || !_power.HasBattery || _power.OnExternalPower || percent is not { } p || p > limit;
    }

    private bool BatteryAllows() => BatteryAllows(out _);

    // ── System state ────────────────────────────────────────────────────

    private void ApplyHolds()
    {
        if (!_state.IsActive || _state.IsPaused || _quitting)
        {
            ReleaseHolds(synchronousSleep: false);
            return;
        }

        var reason = L.Format("win.keepAwake.powerRequestReason", AppIdentity.DisplayName);
        _requests.Apply(true, !_settings.Get(KeepAwakeSettings.AllowDisplaySleep), reason);
        _holdsApplied = true;
        ApplyLidMode();
        ScheduleBatteryCheck();
        ScheduleJiggle();
    }

    private void ReleaseHolds(bool synchronousSleep)
    {
        _batteryTimer?.Dispose();
        _batteryTimer = null;
        _jiggleTimer?.Dispose();
        _jiggleTimer = null;
        if (_holdsApplied)
        {
            _requests.Release();
            _holdsApplied = false;
        }

        RestoreLid(synchronousSleep);
    }

    private void ScheduleBatteryCheck()
    {
        _batteryTimer?.Dispose();
        _batteryTimer = _timers.Schedule(BatteryCheckInterval, () =>
        {
            _batteryTimer = null;
            if (!_state.IsActive || _state.IsPaused)
            {
                return;
            }

            if (!BatteryAllows(out var percent))
            {
                EndSession(KeepAwakeEndReason.Battery);
                Update(_state with { BatteryBlockedPercent = percent });
                return;
            }

            ScheduleBatteryCheck();
        });
    }

    private void ScheduleJiggle()
    {
        _jiggleTimer?.Dispose();
        _jiggleTimer = null;
        if (!_settings.Get(KeepAwakeSettings.MouseJiggle) || !_state.IsActive || _state.IsPaused)
        {
            return;
        }

        _jiggleTimer = _timers.Schedule(TimeSpan.FromMinutes(_settings.Get(KeepAwakeSettings.MouseJiggleIntervalMinutes)), () =>
        {
            _jiggleTimer = null;
            if (_state is { IsActive: true, IsPaused: false })
            {
                try
                {
                    _jiggler.Nudge();
                }
                catch (Exception ex)
                {
                    Log.Warn("keep-awake", "Pointer jiggle failed.", ex);
                }

                ScheduleJiggle();
            }
        });
    }

    // ── Closed-lid mode ─────────────────────────────────────────────────

    private void ApplyLidMode()
    {
        if (!_settings.Get(KeepAwakeSettings.LidModePreferred) || !_lid.HasLid)
        {
            RestoreLid(synchronousSleep: false);
            return;
        }

        if (_settings.Get(KeepAwakeSettings.LidRecoveryMarker) is not null)
        {
            SetLidStatus(LidModeStatus.Active);
            return;
        }

        var current = _lid.ReadCurrent();
        if (current is null)
        {
            FailLidMode();
            return;
        }

        if (current is { OriginalAc: 0, OriginalDc: 0 })
        {
            // The user already chose "Do nothing": nothing to change or restore.
            SetLidStatus(LidModeStatus.Active);
            return;
        }

        // Journal before acting, so a crash is undone at the next launch.
        _settings.Set(KeepAwakeSettings.LidRecoveryMarker, current with { Written = 0 });
        _settings.Flush();
        if (!_lid.Write(current.Scheme, 0, 0))
        {
            _settings.Reset(KeepAwakeSettings.LidRecoveryMarker.Key);
            _settings.Flush();
            FailLidMode();
            return;
        }

        Log.Info("keep-awake", "Lid action set to \"Do nothing\" for the session.");
        SetLidStatus(LidModeStatus.Active);
    }

    private void FailLidMode()
    {
        Log.Warn("keep-awake", "Could not change the lid action; closed-lid mode switched off.");
        _lidFailed = true;
        _settings.Set(KeepAwakeSettings.LidModePreferred, false);
        SetLidStatus(LidModeStatus.Failed);
    }

    /// <summary>Restores the journaled lid action (only values still equal to what the app wrote).</summary>
    private void RestoreLid(bool synchronousSleep)
    {
        var marker = _settings.Get(KeepAwakeSettings.LidRecoveryMarker);
        if (marker is null)
        {
            SetLidStatus(LidStatusAtRest());
            return;
        }

        var current = _lid.ReadCurrent();
        var sameScheme = current is null || current.Scheme == marker.Scheme;
        var ac = !sameScheme || current is null || current.OriginalAc == marker.Written ? marker.OriginalAc : current.OriginalAc;
        var dc = !sameScheme || current is null || current.OriginalDc == marker.Written ? marker.OriginalDc : current.OriginalDc;
        if (!_lid.Write(marker.Scheme, ac, dc))
        {
            Log.Warn("keep-awake", "Restoring the lid action failed; it is retried at the next launch.");
            return;
        }

        _settings.Reset(KeepAwakeSettings.LidRecoveryMarker.Key);
        _settings.Flush();
        Log.Info("keep-awake", "Lid action restored.");
        SetLidStatus(LidStatusAtRest());

        // A closed lid would otherwise keep the PC awake in a bag until the battery dies.
        var restoredAction = _power.OnExternalPower || !_power.HasBattery ? ac : dc;
        if (_lid.IsLidClosed == true && restoredAction != 0)
        {
            SleepWithRetries(synchronousSleep);
        }
    }

    private void SleepWithRetries(bool synchronous)
    {
        if (synchronous)
        {
            for (var i = 0; i < SleepRetries; i++)
            {
                if (_lid.RequestSleep())
                {
                    return;
                }

                Thread.Sleep(SleepRetryDelay);
            }

            return;
        }

        var attempt = 0;
        void Try()
        {
            if (_state.IsActive || _lid.IsLidClosed != true || _lid.RequestSleep() || ++attempt >= SleepRetries)
            {
                return;
            }

            _timers.Schedule(SleepRetryDelay, Try);
        }

        Try();
    }

    private LidModeStatus LidStatusAtRest() =>
        _lidFailed && !_settings.Get(KeepAwakeSettings.LidModePreferred) ? LidModeStatus.Failed
        : _settings.Get(KeepAwakeSettings.LidModePreferred) && _lid.HasLid ? LidModeStatus.Ready
        : LidModeStatus.Off;

    private void SetLidStatus(LidModeStatus status)
    {
        if (_state.LidMode != status)
        {
            Update(_state with { LidMode = status });
        }
    }

    // ── Pause while locked ──────────────────────────────────────────────

    private void OnLockChanged(object? sender, bool locked)
    {
        if (!_settings.Get(KeepAwakeSettings.PauseWhenLocked))
        {
            if (!locked)
            {
                EvaluateAutomationSoon(PowerAndAppsDebounce);
            }

            return;
        }

        if (locked)
        {
            if (_state is { IsActive: true, IsPaused: false })
            {
                Update(_state with { IsPaused = true });
                ReleaseHolds(synchronousSleep: false);
            }

            return;
        }

        if (_state is { IsActive: true, IsPaused: true })
        {
            Update(_state with { IsPaused = false });
            if (_state.EndTime is { } end && _timers.Now >= end)
            {
                HandleTimerEnd();
            }
            else if (_state.Trigger == KeepAwakeTrigger.Automation && !AutomationSatisfied())
            {
                EndSession(KeepAwakeEndReason.Manual);
            }
            else
            {
                ApplyHolds();
            }
        }

        EvaluateAutomationSoon(PowerAndAppsDebounce);
    }

    // ── Automation ──────────────────────────────────────────────────────

    /// <summary>Conditions that hold right now (not filtered by what is enabled).</summary>
    public AutomationCondition MatchingConditions()
    {
        var matching = AutomationCondition.None;
        var external = SafeExternalDisplay();
        if (external == true)
        {
            matching |= AutomationCondition.ExternalDisplay;
        }

        if (_power.HasBattery && _power.OnExternalPower)
        {
            matching |= AutomationCondition.Power;
        }

        var list = _settings.Get(KeepAwakeSettings.AutomationAppList);
        if (list.Any(app => _running.Contains(app.ToLowerInvariant())))
        {
            matching |= AutomationCondition.Apps;
        }

        return matching;
    }

    /// <summary>Any (default): one enabled condition matches; All: every enabled condition matches.</summary>
    public static bool IsSatisfied(AutomationCondition enabled, AutomationCondition matching, bool requireAll) =>
        enabled != AutomationCondition.None
        && (requireAll ? (matching & enabled) == enabled : (matching & enabled) != AutomationCondition.None);

    private bool AutomationSatisfied() =>
        IsSatisfied(EnabledConditions, MatchingConditions(), _settings.Get(KeepAwakeSettings.AutomationRequireAll));

    private bool AutomationWouldStart() => AutomationSatisfied() && !_suppressed && BatteryAllows();

    private bool? SafeExternalDisplay()
    {
        if ((EnabledConditions & AutomationCondition.ExternalDisplay) == 0)
        {
            return _lastExternalDisplay;
        }

        try
        {
            var value = _displays.ExternalDisplayConnected();
            if (value is not null)
            {
                _lastExternalDisplay = value;
            }

            return _lastExternalDisplay;
        }
        catch (Exception ex)
        {
            Log.Warn("keep-awake", "Could not check external displays.", ex);
            return _lastExternalDisplay;
        }
    }

    /// <summary>Starts or ends automatic sessions as conditions change (spec §3.18.4).</summary>
    public void EvaluateAutomation()
    {
        if (!_available || !_recovered || _quitting)
        {
            return;
        }

        var enabled = EnabledConditions;
        var matching = MatchingConditions() & enabled;
        var satisfied = IsSatisfied(enabled, matching, _settings.Get(KeepAwakeSettings.AutomationRequireAll));
        if (!satisfied)
        {
            // Conditions cleared: a manual stop no longer holds automation back.
            _suppressed = false;
        }

        if (_state.IsActive)
        {
            if (_state.Trigger != KeepAwakeTrigger.Automation)
            {
                return;
            }

            if (!satisfied)
            {
                EndSession(KeepAwakeEndReason.Manual);
            }
            else if (_state.ActiveConditions != matching)
            {
                Update(_state with { ActiveConditions = matching });
            }

            return;
        }

        var lockedAndPausing = _lock.IsLocked && _settings.Get(KeepAwakeSettings.PauseWhenLocked);
        if (satisfied && !_suppressed && !lockedAndPausing)
        {
            StartSession(KeepAwakeTrigger.Automation, null, null, matching);
        }
    }

    private void EvaluateAutomationSoon(TimeSpan delay)
    {
        _evaluateTimer?.Dispose();
        _evaluateTimer = _timers.Schedule(delay, () =>
        {
            _evaluateTimer = null;
            EvaluateAutomation();
        });
    }

    /// <summary>Monitoring resources exist only for enabled conditions (the apps poll).</summary>
    private void UpdateAutomationMonitoring()
    {
        var wantApps = _available && _settings.Get(KeepAwakeSettings.AutomationApps) && _settings.Get(KeepAwakeSettings.AutomationAppList).Count > 0;
        if (wantApps && _appsTimer is null)
        {
            PollApps();
        }
        else if (!wantApps)
        {
            _appsTimer?.Dispose();
            _appsTimer = null;
            _running = new HashSet<string>();
        }
    }

    private void PollApps()
    {
        try
        {
            var running = _apps.RunningExecutables();
            var changed = !running.SetEquals(_running);
            _running = running;
            if (changed)
            {
                EvaluateAutomationSoon(PowerAndAppsDebounce);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("keep-awake", "Could not list running apps.", ex);
        }

        _appsTimer = _timers.Schedule(AppsPollInterval, PollApps);
    }

    // ── Wiring ──────────────────────────────────────────────────────────

    private void Subscribe()
    {
        _lock.LockChanged += OnLockChanged;
        _power.Changed += OnPowerChanged;
        _displays.Changed += OnDisplaysChanged;
        _lid.LidChanged += OnLidChanged;
        _subscriptions.Add(_settings.Observe(OnAutomationSettingsChanged, [.. KeepAwakeSettings.AutomationKeys]));
        _subscriptions.Add(_settings.Observe(() => UiPost(ApplyHoldsIfActive), KeepAwakeSettings.AllowDisplaySleep, KeepAwakeSettings.MouseJiggle,
            KeepAwakeSettings.MouseJiggleIntervalMinutes, KeepAwakeSettings.LidModePreferred));
        _subscriptions.Add(_settings.Observe(() => UiPost(OnPauseSettingChanged), KeepAwakeSettings.PauseWhenLocked));
    }

    private void Unsubscribe()
    {
        _lock.LockChanged -= OnLockChanged;
        _power.Changed -= OnPowerChanged;
        _displays.Changed -= OnDisplaysChanged;
        _lid.LidChanged -= OnLidChanged;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private static void UiPost(Action action) => Util.UiThread.Run(action);

    private void OnAutomationSettingsChanged() => UiPost(() =>
    {
        // A deliberate change to automation clears the suppression.
        _suppressed = false;
        UpdateAutomationMonitoring();
        EvaluateAutomationSoon(PowerAndAppsDebounce);
    });

    private void ApplyHoldsIfActive()
    {
        if (!_settings.Get(KeepAwakeSettings.LidModePreferred))
        {
            _lidFailed = _lidFailed && _state.LidMode == LidModeStatus.Failed;
        }
        else
        {
            _lidFailed = false;
        }

        if (_state is { IsActive: true, IsPaused: false })
        {
            ApplyHolds();
        }
        else
        {
            SetLidStatus(LidStatusAtRest());
        }
    }

    private void OnPauseSettingChanged()
    {
        if (_settings.Get(KeepAwakeSettings.PauseWhenLocked) && _lock.IsLocked && _state is { IsActive: true, IsPaused: false })
        {
            Update(_state with { IsPaused = true });
            ReleaseHolds(synchronousSleep: false);
        }
        else if (!_settings.Get(KeepAwakeSettings.PauseWhenLocked) && _state is { IsActive: true, IsPaused: true })
        {
            Update(_state with { IsPaused = false });
            ApplyHolds();
        }
    }

    private void OnPowerChanged(object? sender, EventArgs e)
    {
        if (_state is { IsActive: true, IsPaused: false } && !BatteryAllows(out var percent))
        {
            EndSession(KeepAwakeEndReason.Battery);
            Update(_state with { BatteryBlockedPercent = percent });
        }
        else if (_state.BatteryBlockedPercent is not null && BatteryAllows())
        {
            Update(_state with { BatteryBlockedPercent = null });
        }

        EvaluateAutomationSoon(PowerAndAppsDebounce);
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => EvaluateAutomationSoon(DisplayDebounce);

    private void OnLidChanged(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void Update(KeepAwakeState state)
    {
        if (state == _state)
        {
            return;
        }

        _state = state;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("keep-awake", "A state subscriber failed.", ex);
        }
    }

    public void Dispose()
    {
        Shutdown();
        _endTimer?.Dispose();
        _appsTimer?.Dispose();
        _evaluateTimer?.Dispose();
    }
}
