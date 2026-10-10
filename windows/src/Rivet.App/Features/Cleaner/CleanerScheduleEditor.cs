// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Maintenance;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.Cleaner;

/// <summary>
/// The automatic cleanup card (spec §3.1.7): frequency, weekday or day of the
/// month, a time picker that follows the Windows clock style (12-hour with
/// AM/PM, or 24-hour), minutes in 5-minute steps plus the stored value, the
/// notification switch, and the next/last run lines.
/// </summary>
public sealed class CleanerScheduleEditor : UserControl
{
    private readonly ISettingsStore _settings;
    private readonly CleanerScheduler _scheduler;
    private readonly INotificationService _notifications;
    private readonly IShellService _shell;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _updating;

    public CleanerScheduleEditor(IServiceProvider services)
    {
        _settings = services.GetRequiredService<ISettingsStore>();
        _scheduler = services.GetRequiredService<CleanerScheduler>();
        _notifications = services.GetRequiredService<INotificationService>();
        _shell = services.GetRequiredService<IShellService>();
        Build();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _subscriptions.Add(_settings.Observe(() => Dispatcher.UIThread.Post(Build),
            CleanerSettings.ScheduleFrequency, CleanerSettings.ScheduleHour, CleanerSettings.ScheduleMinute, CleanerSettings.ScheduleWeekday,
            CleanerSettings.ScheduleMonthDay, CleanerSettings.ScheduleNotify, CleanerSettings.LastAutoRun));
        _scheduler.Changed += OnSchedulerChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _scheduler.Changed -= OnSchedulerChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>"Off", "Daily", "Weekly" or "Monthly" for the folded panel row.</summary>
    public static string Summary(ISettingsStore settings) =>
        CleanerScheduleSpec.ParseFrequency(settings.Get(CleanerSettings.ScheduleFrequency)) switch
        {
            ScheduleFrequency.Daily => L.Get("Strings.cleanerScheduleDaily"),
            ScheduleFrequency.Weekly => L.Get("Strings.cleanerScheduleWeekly"),
            ScheduleFrequency.Monthly => L.Get("win.cleaner.scheduleMonthly"),
            _ => L.Get("Strings.cleanerScheduleOff"),
        };

    private void OnSchedulerChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Build);

    private void Build()
    {
        if (_updating)
        {
            return;
        }

        var culture = Localizer.Current.Culture;
        var spec = _scheduler.Spec;
        var stack = VStack(8);

        var frequencies = new (string Key, string Label)[]
        {
            ("off", L.Get("Strings.cleanerScheduleOff")),
            ("daily", L.Get("Strings.cleanerScheduleDaily")),
            ("weekly", L.Get("Strings.cleanerScheduleWeekly")),
            ("monthly", L.Get("win.cleaner.scheduleMonthly")),
        };
        var frequency = Combo(frequencies.Select(f => f.Label).ToList(), Array.FindIndex(frequencies, f => f.Key == CleanerScheduleSpec.FrequencyKey(spec.Frequency)), index =>
            _settings.Set(CleanerSettings.ScheduleFrequency, frequencies[index].Key));
        stack.Children.Add(Columns("*,Auto", 8, Text(L.Get("Strings.cleanerScheduleTitle")), frequency));

        if (spec.Frequency != ScheduleFrequency.Off)
        {
            if (spec.Frequency == ScheduleFrequency.Weekly)
            {
                // Sunday-first, like the macOS picker (weekday 1 = Sunday).
                var days = Enumerable.Range(0, 7).Select(d => culture.DateTimeFormat.GetDayName((DayOfWeek)d)).ToList();
                stack.Children.Add(Columns("*,Auto", 8, Caption(L.Get("win.cleaner.scheduleDay")),
                    Combo(days, spec.Weekday - 1, index => _settings.Set(CleanerSettings.ScheduleWeekday, index + 1))));
            }
            else if (spec.Frequency == ScheduleFrequency.Monthly)
            {
                var monthDays = Enumerable.Range(1, 28).Select(d => L.Format("win.cleaner.scheduleDayFormat", d)).ToList();
                stack.Children.Add(Columns("*,Auto", 8, Caption(L.Get("win.cleaner.scheduleDay")),
                    Combo(monthDays, spec.MonthDay - 1, index => _settings.Set(CleanerSettings.ScheduleMonthDay, index + 1))));
            }

            stack.Children.Add(Columns("*,Auto", 8, Caption(L.Get("win.cleaner.scheduleTime")), TimePicker(spec, culture)));
            stack.Children.Add(NotifyRow());

            if (_scheduler.NextRunUtc() is { } next)
            {
                stack.Children.Add(Caption(L.Format("Strings.cleanerScheduleNextFormat", When(next))));
            }
        }

        if (LastRunLine() is { } last)
        {
            stack.Children.Add(Caption(last));
        }

        stack.Children.Add(Caption(L.Get("Strings.cleanerScheduleCaption")));
        Content = stack;
    }

    private Control TimePicker(CleanerScheduleSpec spec, CultureInfo culture)
    {
        var twelveHour = culture.DateTimeFormat.ShortTimePattern.Contains('h') && !culture.DateTimeFormat.ShortTimePattern.Contains('H');
        var minutes = Enumerable.Range(0, 12).Select(m => m * 5).ToList();
        if (!minutes.Contains(spec.Minute))
        {
            minutes.Add(spec.Minute);
            minutes.Sort();
        }

        var minuteCombo = Combo(minutes.Select(m => m.ToString("00", culture)).ToList(), minutes.IndexOf(spec.Minute), index =>
            _settings.Set(CleanerSettings.ScheduleMinute, minutes[index]));
        var separator = new TextBlock { Text = culture.DateTimeFormat.TimeSeparator, VerticalAlignment = VerticalAlignment.Center };
        if (!twelveHour)
        {
            var hourCombo = Combo(Enumerable.Range(0, 24).Select(h => h.ToString("00", culture)).ToList(), spec.Hour, index =>
                _settings.Set(CleanerSettings.ScheduleHour, index));
            return HStack(4, hourCombo, separator, minuteCombo);
        }

        var (hour12, pm) = CleanerSchedule.To12Hour(spec.Hour);
        var hour = Combo(Enumerable.Range(1, 12).Select(h => h.ToString(culture)).ToList(), hour12 - 1, index =>
            _settings.Set(CleanerSettings.ScheduleHour, CleanerSchedule.To24Hour(index + 1, CleanerSchedule.To12Hour(_settings.Get(CleanerSettings.ScheduleHour)).Pm)));
        var meridiem = Combo([culture.DateTimeFormat.AMDesignator, culture.DateTimeFormat.PMDesignator], pm ? 1 : 0, index =>
            _settings.Set(CleanerSettings.ScheduleHour, CleanerSchedule.To24Hour(CleanerSchedule.To12Hour(_settings.Get(CleanerSettings.ScheduleHour)).Hour12, index == 1)));
        return HStack(4, hour, separator, minuteCombo, meridiem);
    }

    private Control NotifyRow()
    {
        var notify = new CheckBox { Content = L.Get("Strings.cleanerScheduleNotifyToggle"), IsChecked = _settings.Get(CleanerSettings.ScheduleNotify) };
        notify.IsCheckedChanged += (_, _) =>
        {
            _updating = true;
            _settings.Set(CleanerSettings.ScheduleNotify, notify.IsChecked == true);
            _updating = false;
        };
        if (!_settings.Get(CleanerSettings.ScheduleNotify) || _notifications.IsEnabled)
        {
            return notify;
        }

        return VStack(4, notify, Colored(L.Get("win.cleaner.notificationsOff"), "WarningBrush"),
            LinkButton(L.Get("Strings.cleanerNotifOpenSettings"), () => _shell.OpenSystemSettings("ms-settings:notifications")));
    }

    private string? LastRunLine()
    {
        if (_scheduler.LastRunUtc is not { } last)
        {
            return null;
        }

        var freed = _settings.Get(CleanerSettings.LastAutoFreed);
        var failed = _settings.Get(CleanerSettings.LastAutoFailed);
        var line = freed > 0 ? L.Format("Strings.cleanerScheduleLastFormat", Size(freed)) : L.Format("Strings.cleanerScheduleRanFormat", When(last));
        return failed > 0 ? line + " " + L.Get("Strings.uninstallerSomeFailed") : line;
    }

    private ComboBox Combo(IReadOnlyList<string> items, int selected, Action<int> changed)
    {
        var combo = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1)), MinWidth = 64, VerticalAlignment = VerticalAlignment.Center };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                changed(combo.SelectedIndex);
            }
        };
        return combo;
    }
}
