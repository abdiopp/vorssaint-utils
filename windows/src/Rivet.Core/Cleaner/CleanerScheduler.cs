// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>Wake and clock-change notifications (schedules re-arm on them).</summary>
public interface ISystemClockEvents
{
    /// <summary>The PC resumed from sleep or hibernation.</summary>
    event EventHandler? Resumed;

    /// <summary>The system clock or time zone changed.</summary>
    event EventHandler? TimeChanged;
}

/// <summary>
/// Automatic cleanup: an in-app timer (the app must be running) that scans
/// unattended and cleans only what starts checked (the Safe groups), then
/// notifies — even when nothing was found, as proof of life. Nothing exists
/// while the feature or its schedule is off.
/// </summary>
public sealed class CleanerScheduler : IFeatureController, IDisposable
{
    private static readonly TimeSpan MaxTimerStep = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    private readonly CleanerService _cleaner;
    private readonly ISettingsStore _settings;
    private readonly INotificationService _notifications;
    private readonly ISystemClockEvents? _clock;
    private readonly IDisposable _subscription;
    private Timer? _timer;
    private DateTime? _retryAtUtc;
    private bool _available;
    private bool _running;

    public CleanerScheduler(CleanerService cleaner, ISettingsStore settings, INotificationService notifications, ISystemClockEvents? clock = null)
    {
        _cleaner = cleaner;
        _settings = settings;
        _notifications = notifications;
        _clock = clock;
        _subscription = settings.Observe(
            () => UiThread.Post(Arm),
            CleanerSettings.ScheduleFrequency, CleanerSettings.ScheduleHour, CleanerSettings.ScheduleMinute,
            CleanerSettings.ScheduleWeekday, CleanerSettings.ScheduleMonthDay);
        if (_clock is not null)
        {
            _clock.Resumed += OnClockEvent;
            _clock.TimeChanged += OnClockEvent;
        }
    }

    /// <summary>Raised on the UI thread after a run finished or the schedule moved.</summary>
    public event EventHandler? Changed;

    public CleanerScheduleSpec Spec => new(
        CleanerScheduleSpec.ParseFrequency(_settings.Get(CleanerSettings.ScheduleFrequency)),
        _settings.Get(CleanerSettings.ScheduleHour),
        _settings.Get(CleanerSettings.ScheduleMinute),
        _settings.Get(CleanerSettings.ScheduleWeekday),
        _settings.Get(CleanerSettings.ScheduleMonthDay));

    public DateTime? LastRunUtc
    {
        get
        {
            var seconds = _settings.Get(CleanerSettings.LastAutoRun);
            return seconds > 0 ? DateTime.UnixEpoch.AddSeconds(seconds) : null;
        }
    }

    /// <summary>When the next automatic cleanup will run, or null while the schedule is off.</summary>
    public DateTime? NextRunUtc()
    {
        if (!_available)
        {
            return null;
        }

        return _retryAtUtc ?? CleanerSchedule.DueUtc(Spec, LastRunUtc, DateTime.UtcNow, TimeZoneInfo.Local);
    }

    public void Sync(bool available)
    {
        _available = available;
        Arm();
    }

    /// <summary>The notification body for a finished automatic run (spec §3.1.7).</summary>
    public static string NotificationBody(long freed, int failed)
    {
        if (freed <= 0 && failed <= 0)
        {
            return L.Get("Strings.cleanerNothingFound");
        }

        var parts = new List<string>();
        if (freed > 0)
        {
            parts.Add(L.Format("Strings.cleanerAutoNotificationFormat", ByteSize.Format(freed)));
        }

        if (failed > 0)
        {
            parts.Add(L.Get("Strings.uninstallerSomeFailed"));
        }

        return string.Join(' ', parts);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _subscription.Dispose();
        if (_clock is not null)
        {
            _clock.Resumed -= OnClockEvent;
            _clock.TimeChanged -= OnClockEvent;
        }
    }

    private void OnClockEvent(object? sender, EventArgs e) => UiThread.Post(Arm);

    private void Arm()
    {
        _timer?.Dispose();
        _timer = null;
        if (!_available || _running || NextRunUtc() is not { } due)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var wait = due - DateTime.UtcNow;
        var step = wait <= TimeSpan.Zero ? TimeSpan.Zero : (wait > MaxTimerStep ? MaxTimerStep : wait);
        _timer = new Timer(_ => UiThread.Post(OnTimer), null, step, Timeout.InfiniteTimeSpan);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimer()
    {
        if (!_available || _running)
        {
            return;
        }

        if (NextRunUtc() is { } due && due - DateTime.UtcNow <= Tolerance)
        {
            _ = RunAsync();
        }
        else
        {
            Arm();
        }
    }

    private async Task RunAsync()
    {
        // A person is reviewing the Cleaner: try again in ten minutes.
        if (_cleaner.Phase != CleanerPhase.Idle)
        {
            _retryAtUtc = DateTime.UtcNow + CleanerSchedule.BusyRetry;
            Arm();
            return;
        }

        _retryAtUtc = null;
        _running = true;
        try
        {
            await _cleaner.ScanAsync(attended: false).ConfigureAwait(true);
            if (_cleaner.Phase != CleanerPhase.Results)
            {
                // Someone reset the Cleaner during the run: nothing is recorded.
                return;
            }

            CleanResult result = new();
            if (_cleaner.SelectedCount > 0)
            {
                result = await _cleaner.CleanSelectedAsync(escalate: false).ConfigureAwait(true);
            }

            Finish(result);
        }
        catch (Exception ex)
        {
            Log.Error("cleaner", "Automatic cleanup failed.", ex);
        }
        finally
        {
            _running = false;
            Arm();
        }
    }

    private void Finish(CleanResult result)
    {
        _cleaner.Reset();
        _settings.Set(CleanerSettings.LastAutoRun, (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);
        _settings.Set(CleanerSettings.LastAutoFreed, result.FreedBytes);
        _settings.Set(CleanerSettings.LastAutoFailed, result.Failed);
        if (_settings.Get(CleanerSettings.ScheduleNotify))
        {
            _notifications.Show(new NotificationRequest
            {
                Title = L.Get("Strings.cleanerScheduleTitle"),
                Body = NotificationBody(result.FreedBytes, result.Failed),
                ClickActionId = CleanerActions.Open,
                Tag = "cleaner.auto",
            });
        }
    }
}

/// <summary>Action ids of the Cleaner module (Core names them for notifications).</summary>
public static class CleanerActions
{
    public const string Open = FeatureIds.Cleaner + ".open";

    public const string Scan = FeatureIds.Cleaner + ".scan";
}
