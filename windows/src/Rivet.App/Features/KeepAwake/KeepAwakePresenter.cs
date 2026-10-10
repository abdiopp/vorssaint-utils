// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Rivet.App.Modules;
using Rivet.Core.Awake;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.Awake;

/// <summary>
/// Shows the session in the tray (spec 05 §3.3.2/§3.3.4): a tint while
/// active, and a tooltip line ("Awake until 14:30", the automation reason or
/// "Normal sleep"). Also feeds the countdown readout of the mini monitor.
/// </summary>
public sealed class KeepAwakePresenter : IReadoutCountdownSource, IDisposable
{
    public const string TraySource = "keepAwake";

    private readonly KeepAwakeManager _manager;
    private readonly ISettingsStore _settings;
    private readonly ITrayPresence? _tray;
    private readonly IDisposable _subscription;
    private DispatcherTimer? _timer;
    private bool _available;

    public KeepAwakePresenter(KeepAwakeManager manager, ISettingsStore settings, ITrayPresence? tray)
    {
        _manager = manager;
        _settings = settings;
        _tray = tray;
        _manager.StateChanged += (_, _) => Refresh();
        _subscription = settings.Observe(() => Dispatcher.UIThread.Post(Refresh), KeepAwakeSettings.IconTint, KeepAwakeSettings.ShowCountdown);
        Localizer.Current.LanguageChanged += (_, _) => Refresh();
    }

    public event EventHandler? Changed;

    public string? Countdown =>
        _available && _manager.IsActive && _settings.Get(KeepAwakeSettings.ShowCountdown) ? KeepAwakeFormat.Compact(_manager.Remaining) : null;

    public void Sync(bool available)
    {
        _available = available;
        Refresh();
    }

    /// <summary>Tray tint (0xAARRGGBB) of the chosen colour; null for "No color".</summary>
    public static uint? Tint(string name) => name switch
    {
        "green" => 0xFF34C759,
        "blue" => 0xFF0A84FF,
        "purple" => 0xFFBF5AF2,
        "pink" => 0xFFFF375F,
        "none" => null,
        _ => 0xFFFF9500,
    };

    /// <summary>The tooltip line for a state.</summary>
    public static string TooltipLine(KeepAwakeManager manager, bool showCountdown)
    {
        var state = manager.State;
        if (!state.IsActive)
        {
            return L.Get("win.keepAwake.trayIdle");
        }

        string line;
        if (state.Trigger == KeepAwakeTrigger.Automation)
        {
            line = AutomationStatus(state.ActiveConditions);
        }
        else if (state.EndTime is { } end)
        {
            line = L.Format("win.keepAwake.trayUntilFormat", KeepAwakeFormat.Time(end));
        }
        else
        {
            line = L.Get("win.keepAwake.trayIndefinitely");
        }

        return showCountdown && state.EndTime is not null ? $"{line} · {KeepAwakeFormat.Compact(manager.Remaining)}" : line;
    }

    /// <summary>One condition → its "Active while…" text; several → the generic reason.</summary>
    public static string AutomationStatus(AutomationCondition conditions) => conditions switch
    {
        AutomationCondition.ExternalDisplay => L.Get("keepAwakeAutomation.externalDisplayActive"),
        AutomationCondition.Power => L.Get("keepAwakeAutomation.powerActive"),
        AutomationCondition.Apps => L.Get("keepAwakeAutomation.runningAppsActive"),
        _ => L.Get("keepAwakeAutomation.automationActive"),
    };

    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        if (!_available)
        {
            _tray?.SetIndicator(TraySource, null);
            StopTimer();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var state = _manager.State;
        var showCountdown = _settings.Get(KeepAwakeSettings.ShowCountdown);
        _tray?.SetIndicator(TraySource, new TrayIndicator
        {
            Tint = state.IsActive ? Tint(_settings.Get(KeepAwakeSettings.IconTint)) : null,
            TooltipLine = TooltipLine(_manager, showCountdown),
            Priority = 10,
        });

        // A 30 s timer runs only while a timed countdown is shown.
        if (state is { IsActive: true, EndTime: not null } && showCountdown)
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _timer.Tick += (_, _) => Refresh();
                _timer.Start();
            }
        }
        else
        {
            StopTimer();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    public void Dispose()
    {
        StopTimer();
        _subscription.Dispose();
    }
}
