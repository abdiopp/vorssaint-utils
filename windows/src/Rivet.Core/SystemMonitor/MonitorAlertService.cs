// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.Core.SystemMonitor;

public enum AlertKind
{
    Cpu,
    CpuTemperature,
    BatteryTemperature,
    Memory,
    Disk,
    Battery,
}

/// <summary>
/// The six monitor alerts (spec §3.15), evaluated on every published
/// snapshot. CPU usage and the temperatures must hold for 12 s of distinct
/// reads; every kind has the same repeat cooldown; last-sent times live in
/// memory only. Toasts go through <see cref="INotificationService"/>.
/// </summary>
public sealed class MonitorAlertService : IDisposable
{
    public const ulong DiskMinimumTotal = 10_000_000_000;

    private readonly SystemMonitorService? _monitor;
    private readonly ISettingsStore _settings;
    private readonly FeatureRuntime _runtime;
    private readonly INotificationService _notifications;
    private readonly Func<double> _clock;
    private readonly SustainedGate _cpuGate = new();
    private readonly SustainedGate _cpuTempGate = new();
    private readonly SustainedGate _batteryTempGate = new();
    private readonly Dictionary<AlertKind, double> _lastSent = [];
    private readonly IDisposable _subscription;
    private bool _active;

    public MonitorAlertService(SystemMonitorService monitor, ISettingsStore settings, FeatureRuntime runtime, INotificationService notifications)
        : this(settings, runtime, notifications, () => monitor.Sensors.Clock.Now)
    {
        _monitor = monitor;
        _monitor.SnapshotPublished += OnSnapshot;
    }

    /// <summary>A detached instance: the caller feeds snapshots to <see cref="Evaluate"/> (tests).</summary>
    public MonitorAlertService(ISettingsStore settings, FeatureRuntime runtime, INotificationService notifications, Func<double> clock)
    {
        _settings = settings;
        _runtime = runtime;
        _notifications = notifications;
        _clock = clock;
        _subscription = settings.Observe(MonitorSettings.AlertSwitches.Select(s => s.Key), ResetDisabledGates);
    }

    /// <summary>Alerts that were sent (newest last), for diagnostics and tests.</summary>
    public List<(AlertKind Kind, string Title, string Body)> Sent { get; } = [];

    /// <summary>Whether Windows lets the app show notifications at all.</summary>
    public bool NotificationsEnabled => _notifications.IsEnabled;

    private void OnSnapshot(object? sender, MonitorSnapshot snapshot) => Evaluate(snapshot);

    /// <summary>Evaluates every enabled rule against <paramref name="snapshot"/>.</summary>
    public void Evaluate(MonitorSnapshot snapshot)
    {
        var s = _settings;
        var unit = MetricFormat.ParseTemperatureUnit(s.Get(MonitorSettings.TemperatureUnit));
        var anyActive = false;

        if (s.Get(MonitorSettings.AlertCpu) && _runtime.IsAvailable(FeatureIds.MonitorCpu))
        {
            anyActive = true;
            var threshold = s.Get(MonitorSettings.AlertCpuThreshold);
            if (_cpuGate.ShouldAlert(snapshot.CpuUsage, threshold / 100.0, snapshot.CpuUsageReadAt))
            {
                Fire(AlertKind.Cpu, L.Get("monitorAlerts.cpuTitle"), L.Format("monitorAlerts.cpuBodyFormat", threshold));
            }
        }

        if (s.Get(MonitorSettings.AlertCpuTemperature) && _runtime.IsAvailable(FeatureIds.MonitorCpu))
        {
            anyActive = true;
            var threshold = s.Get(MonitorSettings.AlertCpuTemperatureThreshold);
            if (_cpuTempGate.ShouldAlert(snapshot.CpuTemperature, threshold, snapshot.CpuTemperatureReadAt) && snapshot.CpuTemperature is { } t)
            {
                Fire(AlertKind.CpuTemperature, L.Get("monitorAlerts.cpuTemperatureTitle"),
                    L.Format("monitorAlerts.cpuTemperatureBodyFormat", MetricFormat.Temperature(t, unit)));
            }
        }

        if (s.Get(MonitorSettings.AlertBatteryTemperature) && _runtime.IsAvailable(FeatureIds.MonitorPower) && snapshot.HasBattery)
        {
            anyActive = true;
            var threshold = s.Get(MonitorSettings.AlertBatteryTemperatureThreshold);
            if (_batteryTempGate.ShouldAlert(snapshot.BatteryTemperature, threshold, snapshot.BatteryTemperatureReadAt) && snapshot.BatteryTemperature is { } t)
            {
                Fire(AlertKind.BatteryTemperature, L.Get("monitorAlerts.batteryTemperatureTitle"),
                    L.Format("monitorAlerts.batteryTemperatureBodyFormat", MetricFormat.Temperature(t, unit)));
            }
        }

        if (s.Get(MonitorSettings.AlertMemory) && _runtime.IsAvailable(FeatureIds.MonitorMemory))
        {
            anyActive = true;
            if (snapshot.Memory?.Pressure == MemoryPressure.Critical)
            {
                Fire(AlertKind.Memory, L.Get("monitorAlerts.memoryTitle"), L.Get("monitorAlerts.memoryBody"));
            }
        }

        if (s.Get(MonitorSettings.AlertDisk) && _runtime.IsAvailable(FeatureIds.MonitorDisk))
        {
            anyActive = true;
            var threshold = s.Get(MonitorSettings.AlertDiskFreePercent);
            if (LowDisk(snapshot.Disks, threshold) is { } volume)
            {
                Fire(AlertKind.Disk, L.Get("monitorAlerts.diskTitle"), L.Format("monitorAlerts.diskBodyFormat", volume.Info.Name, threshold));
            }
        }

        if (s.Get(MonitorSettings.AlertBattery) && _runtime.IsAvailable(FeatureIds.MonitorPower))
        {
            anyActive = true;
            var threshold = s.Get(MonitorSettings.AlertBatteryPercent);
            // Not gated on external power: on AC but held at a charge limit can still fire.
            if (snapshot.Power is { HasBattery: true, IsCharging: false, ChargePercent: { } charge } && charge <= threshold)
            {
                Fire(AlertKind.Battery, L.Get("monitorAlerts.batteryTitle"), L.Format("monitorAlerts.batteryBodyFormat", charge));
            }
        }

        if (!anyActive && _active)
        {
            // Alerts stopped: gates restart from scratch next time.
            _cpuGate.Reset();
            _cpuTempGate.Reset();
            _batteryTempGate.Reset();
        }

        _active = anyActive;
    }

    /// <summary>The first volume (display order) of at least 10 GB whose free share is under the threshold.</summary>
    public static DiskVolume? LowDisk(IEnumerable<DiskVolume> disks, int thresholdPercent) =>
        disks.FirstOrDefault(d => d.Info.Total >= DiskMinimumTotal && (double)d.Info.Free / d.Info.Total * 100 < thresholdPercent);

    private void Fire(AlertKind kind, string title, string body)
    {
        var now = _clock();
        var cooldown = _settings.Get(MonitorSettings.AlertCooldownMinutes) * 60.0;
        if (_lastSent.TryGetValue(kind, out var last) && now - last < cooldown)
        {
            return;
        }

        _lastSent[kind] = now;
        Sent.Add((kind, title, body));
        _notifications.Show(new NotificationRequest
        {
            Title = title,
            Body = body,
            Tag = "monitor-alert-" + kind.ToString().ToLowerInvariant(),
        });
    }

    private void ResetDisabledGates()
    {
        if (!_settings.Get(MonitorSettings.AlertCpu))
        {
            _cpuGate.Reset();
        }

        if (!_settings.Get(MonitorSettings.AlertCpuTemperature))
        {
            _cpuTempGate.Reset();
        }

        if (!_settings.Get(MonitorSettings.AlertBatteryTemperature))
        {
            _batteryTempGate.Reset();
        }
    }

    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.SnapshotPublished -= OnSnapshot;
        }

        _subscription.Dispose();
    }
}
