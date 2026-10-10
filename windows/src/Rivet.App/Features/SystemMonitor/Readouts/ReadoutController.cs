// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.SystemMonitor.Panel;
using Rivet.App.Modules;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Readouts;

/// <summary>
/// Turns snapshots into the Windows readout surfaces (spec §3.16.7): one
/// summary line in the tray icon's tooltip and the optional mini monitor
/// window. Both read the pinned "menu bar" tokens, so sampling happens only
/// while one of them is on and something is pinned.
/// </summary>
public sealed class ReadoutController : IDisposable
{
    public const string TrayIndicatorSource = "systemMonitor.readouts";

    private readonly IServiceProvider _services;
    private readonly SystemMonitorService _monitor;
    private readonly ISettingsStore _settings;
    private readonly FeatureRuntime _runtime;
    private readonly ITrayPresence? _tray;
    private readonly IFullScreenDetector? _fullScreen;
    private readonly List<IDisposable> _subscriptions = [];
    private IReadoutCountdownSource? _countdown;
    private MiniMonitorWindow? _window;
    private string? _lastTooltip;
    private bool _started;
    private bool _anyFamily;

    public ReadoutController(IServiceProvider services)
    {
        _services = services;
        _monitor = services.GetRequiredService<SystemMonitorService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _tray = services.GetService<ITrayPresence>();
        _fullScreen = services.GetService<IFullScreenDetector>();
    }

    /// <summary>The blocks shown now (diagnostics, tests).</summary>
    public IReadOnlyList<ReadoutBlock> CurrentBlocks { get; private set; } = [];

    /// <summary>Starts or stops with the monitor families (called from the feature controllers).</summary>
    public void Sync(bool anyFamilyAvailable)
    {
        _anyFamily = anyFamilyAvailable;
        if (anyFamilyAvailable && !_started)
        {
            _started = true;
            _countdown = _services.GetService<IReadoutCountdownSource>();
            if (_countdown is not null)
            {
                _countdown.Changed += OnCountdownChanged;
            }

            _monitor.SnapshotPublished += OnSnapshot;
            var keys = ReadoutTokens.DefaultOrder.Select(t => (SettingDefinition)ReadoutTokens.Setting(t))
                .Concat([MonitorSettings.ReadoutOrder, MonitorSettings.ReadoutAppearance, MonitorSettings.CombineTemperatures,
                    MonitorSettings.ReadoutSpacing, MonitorSettings.NetworkUploadFirst, MonitorSettings.MemoryStyle, MonitorSettings.DiskStyle,
                    MonitorSettings.BarNormalColor, MonitorSettings.BarElevatedColor, MonitorSettings.BarCriticalColor,
                    MonitorSettings.BarMediumThreshold, MonitorSettings.BarHighThreshold, MonitorSettings.TrayTooltipReadouts,
                    MonitorSettings.MiniMonitorEnabled, MonitorSettings.MiniMonitorClickThrough, MonitorSettings.MiniMonitorHideInFullScreen,
                    MonitorSettings.TemperatureUnit, MonitorSettings.NetworkSpeedUnit, MonitorSettings.MemoryMetric, MonitorSettings.ShowCountdown]);
            _subscriptions.Add(_settings.Observe(() => Dispatcher.UIThread.Post(Refresh), keys.ToArray()));
        }

        Dispatcher.UIThread.Post(Refresh);
    }

    private void OnSnapshot(object? sender, MonitorSnapshot e) => Post(Refresh);

    private void OnCountdownChanged(object? sender, EventArgs e) => Post(Refresh);

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>Recomputes the blocks and updates the tooltip and the mini monitor.</summary>
    public void Refresh()
    {
        var tokens = _anyFamily ? ReadoutComposer.EnabledTokens(_settings, _runtime, _monitor.HasBattery) : [];
        var blocks = tokens.Count == 0 ? [] : ReadoutComposer.Compose(tokens, _monitor.Latest, ReadoutStyle.From(_settings));
        CurrentBlocks = blocks;
        var countdown = _settings.Get(MonitorSettings.ShowCountdown) ? _countdown?.Countdown : null;

        // Tray tooltip summary line (re-sent only when the text changes).
        var tooltip = _settings.Get(MonitorSettings.TrayTooltipReadouts) && blocks.Count > 0 ? ReadoutComposer.TooltipLine(blocks) : null;
        if (tooltip != _lastTooltip)
        {
            _lastTooltip = tooltip;
            _tray?.SetIndicator(TrayIndicatorSource, tooltip is null ? null : new TrayIndicator { TooltipLine = tooltip });
        }

        // Mini monitor window.
        var wantWindow = _anyFamily && _settings.Get(MonitorSettings.MiniMonitorEnabled) && (tokens.Count > 0 || countdown is not null);
        if (wantWindow && _settings.Get(MonitorSettings.MiniMonitorHideInFullScreen) && SafeFullScreen())
        {
            wantWindow = false;
        }

        if (!wantWindow)
        {
            if (_window is not null)
            {
                _window.Close();
                _window = null;
            }

            return;
        }

        if (_window is null)
        {
            _window = new MiniMonitorWindow(_settings);
            _window.BlockClicked += (_, block) => OpenDetail(block.Detail);
            _window.OpenPanelRequested += (_, _) => _services.GetService<IAppShell>()?.ShowPanel(SystemMonitorModule.SystemSectionId);
            _window.OpenSettingsRequested += (_, _) => _services.GetService<IAppShell>()?.OpenSettings(SystemMonitorModule.SettingsPageId);
            _window.Update(blocks, countdown);
            _window.SetClickThrough(_settings.Get(MonitorSettings.MiniMonitorClickThrough));
            _window.ShowAtSavedPosition();
        }
        else
        {
            _window.Update(blocks, countdown);
            _window.SetClickThrough(_settings.Get(MonitorSettings.MiniMonitorClickThrough));
        }
    }

    private bool SafeFullScreen()
    {
        try
        {
            return _fullScreen?.IsFullScreenAppInFront() == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OpenDetail(MetricKind kind)
    {
        _services.GetService<MonitorNavigation>()?.Request(kind);
        _services.GetService<IAppShell>()?.ShowPanel(MonitorNavigation.SectionFor(kind));
    }

    public void Dispose()
    {
        _monitor.SnapshotPublished -= OnSnapshot;
        if (_countdown is not null)
        {
            _countdown.Changed -= OnCountdownChanged;
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _window?.Close();
        _window = null;
    }
}
