// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.SystemMonitor;

/// <summary>
/// The demand-driven sampler (spec §3.1). Consumers lease what they show
/// (a panel section, a detail view); pinned readouts and enabled alerts are
/// read from settings. A low-priority worker thread exists only while the
/// plan needs anything; it reads the due families on each tick and
/// publishes immutable snapshots on the UI thread. Zero cost at rest.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    /// <summary>Opening a surface skips GPU for the immediate refresh and reads it this much later.</summary>
    public static readonly TimeSpan GpuSuppression = TimeSpan.FromSeconds(0.9);

    private readonly MonitorSensors _sensors;
    private readonly ISettingsStore _settings;
    private readonly FeatureRuntime _runtime;
    private readonly MonitorEngine _engine;
    private readonly SamplingCadence _cadence = new();
    private readonly object _gate = new();
    private readonly object _engineGate = new();
    private readonly Dictionary<MonitorSurface, int> _surfaces = [];
    private readonly Dictionary<MetricKind, int> _details = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly IDisposable _settingsSubscription;
    private int _fullSurfaceDepth;
    private Thread? _thread;
    private bool _planDirty = true;
    private bool _refreshRequested;
    private double _suppressGpuUntil = double.NegativeInfinity;
    private double _deferredGpuAt = double.NaN;
    private double _nextTickAt;
    private SamplingPlan _plan = SamplingPlan.None;
    private bool _foreground;
    private int _interval = 2;
    private bool _publishedForegroundState;
    private SamplingPlan _publishedPlan = SamplingPlan.None;
    private MonitorSnapshot? _pending;
    private bool _disposed;

    public SystemMonitorService(MonitorSensors sensors, ISettingsStore settings, FeatureRuntime runtime)
    {
        _sensors = sensors;
        _settings = settings;
        _runtime = runtime;
        _engine = new MonitorEngine(sensors);
        _settingsSubscription = settings.Observe(MonitorSettings.PlanKeys.Select(k => k.Key), Invalidate);
        runtime.Changed += (_, _) => Invalidate();
        sensors.Power.PowerStatusChanged += OnPowerStatusChanged;
    }

    /// <summary>The latest snapshot (read and replaced on the UI thread).</summary>
    public MonitorSnapshot Latest { get; private set; } = MonitorSnapshot.Empty;

    /// <summary>Raised on the UI thread whenever a snapshot is published.</summary>
    public event EventHandler<MonitorSnapshot>? SnapshotPublished;

    /// <summary>The plan in effect (for diagnostics and tests).</summary>
    public SamplingPlan CurrentPlan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
    }

    /// <summary>The sampler thread is alive (false at rest).</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _thread is not null;
            }
        }
    }

    internal MonitorEngine Engine => _engine;

    /// <summary>Per-process GPU usage from the latest GPU read.</summary>
    public GpuSample? LatestGpuSample => _engine.LatestGpuSample;

    public bool HasBattery => _engine.HasBattery;

    public MonitorSensors Sensors => _sensors;

    /// <summary>A panel section is on screen. Dispose when it leaves the screen or the panel closes.</summary>
    public IDisposable AcquireSurface(MonitorSurface surface) =>
        Lease(() => Add(_surfaces, surface), () => Remove(_surfaces, surface));

    /// <summary>A metric detail view is on screen.</summary>
    public IDisposable AcquireDetail(MetricKind kind) =>
        Lease(() => Add(_details, kind), () => Remove(_details, kind));

    /// <summary>A surface that shows every section at once (a depth counter, not a flag).</summary>
    public IDisposable AcquireFullSurface() =>
        Lease(() => _fullSurfaceDepth++, () => _fullSurfaceDepth = Math.Max(0, _fullSurfaceDepth - 1));

    /// <summary>A transient UI animation: re-serve the previous GPU value for a moment (minimum 0.1 s).</summary>
    public void SuppressGpu(TimeSpan duration)
    {
        var seconds = Math.Max(0.1, duration.TotalSeconds);
        lock (_gate)
        {
            _suppressGpuUntil = Math.Max(_suppressGpuUntil, _sensors.Clock.Now + seconds);
        }
    }

    /// <summary>Asks for an immediate refresh of everything the plan needs.</summary>
    public void RequestRefresh()
    {
        lock (_gate)
        {
            _refreshRequested = true;
        }

        _wake.Set();
    }

    /// <summary>
    /// Re-evaluates the plan and refreshes synchronously on the calling thread
    /// (tests, previews). The snapshot is published as usual.
    /// </summary>
    public MonitorSnapshot RefreshNow()
    {
        RecomputePlan(out var plan, out var foreground, out var interval);
        var due = SamplingCadence.NeededKinds(plan);
        MonitorSnapshot snapshot;
        lock (_engineGate)
        {
            snapshot = _engine.Refresh(new RefreshRequest { Plan = plan, Due = due, Foreground = foreground, IntervalSeconds = interval }).Snapshot;
        }

        Publish(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Refreshes synchronously with an explicit plan, without leasing anything
    /// (so no worker thread starts). For tests and previews that fill histories.
    /// </summary>
    public MonitorSnapshot RefreshWith(SamplingPlan plan, bool foreground = true)
    {
        MonitorSnapshot snapshot;
        lock (_engineGate)
        {
            snapshot = _engine.Refresh(new RefreshRequest { Plan = plan, Due = SamplingCadence.NeededKinds(plan), Foreground = foreground, IntervalSeconds = _settings.Get(MonitorSettings.IntervalSeconds) }).Snapshot;
        }

        Publish(snapshot);
        return snapshot;
    }

    /// <summary>Plan inputs changed (settings, hub, leases): re-plan and refresh at once.</summary>
    public void Invalidate()
    {
        bool start;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _planDirty = true;
            _refreshRequested = true;
            start = _thread is null && SamplingPlanner.Compute(GatherInputs()).Any;
            if (start)
            {
                _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "SystemMonitor", Priority = ThreadPriority.BelowNormal };
            }
        }

        if (start)
        {
            _thread!.Start();
        }
        else
        {
            _wake.Set();
        }
    }

    private IDisposable Lease(Action acquire, Action release)
    {
        lock (_gate)
        {
            acquire();
            if (HasForegroundLocked())
            {
                // Opening animations raise GPU load: skip GPU now and read it shortly after.
                var now = _sensors.Clock.Now;
                _suppressGpuUntil = Math.Max(_suppressGpuUntil, now + GpuSuppression.TotalSeconds);
                _deferredGpuAt = now + GpuSuppression.TotalSeconds;
            }
        }

        Invalidate();
        return new LeaseToken(() =>
        {
            lock (_gate)
            {
                release();
            }

            Invalidate();
        });
    }

    private static void Add<T>(Dictionary<T, int> counts, T key)
        where T : notnull => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static void Remove<T>(Dictionary<T, int> counts, T key)
        where T : notnull
    {
        var count = counts.GetValueOrDefault(key) - 1;
        if (count <= 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = count;
        }
    }

    private bool HasForegroundLocked() => _surfaces.Count > 0 || _details.Count > 0 || _fullSurfaceDepth > 0;

    private SamplingInputs GatherInputs()
    {
        var visible = MonitorSurface.None;
        foreach (var surface in _surfaces.Keys)
        {
            visible |= surface;
        }

        var s = _settings;
        bool Has(string id) => _runtime.IsAvailable(id);
        return new SamplingInputs
        {
            Visible = visible,
            FullSurface = _fullSurfaceDepth > 0,
            Details = _details.Keys.ToHashSet(),
            ReadoutsActive = s.Get(MonitorSettings.TrayTooltipReadouts) || s.Get(MonitorSettings.MiniMonitorEnabled),
            HasBattery = _engine.HasBattery,
            SysCpu = s.Get(MonitorSettings.SysCpu),
            SysCpuCores = s.Get(MonitorSettings.SysCpuCores),
            SysMemory = s.Get(MonitorSettings.SysMemory),
            SysGpu = s.Get(MonitorSettings.SysGpu),
            SysTemps = s.Get(MonitorSettings.SysTemps),
            SysConnectedDevices = s.Get(MonitorSettings.SysConnectedDevices),
            SysBattery = s.Get(MonitorSettings.SysBattery),
            PwrTemperature = s.Get(MonitorSettings.PwrTemperature),
            ReadoutCpu = s.Get(MonitorSettings.ReadoutCpu),
            ReadoutCpuTemperature = s.Get(MonitorSettings.ReadoutCpuTemperature),
            ReadoutGpu = s.Get(MonitorSettings.ReadoutGpu),
            ReadoutGpuTemperature = s.Get(MonitorSettings.ReadoutGpuTemperature),
            ReadoutMemory = s.Get(MonitorSettings.ReadoutMemory),
            ReadoutNetwork = s.Get(MonitorSettings.ReadoutNetwork),
            ReadoutDiskUsage = s.Get(MonitorSettings.ReadoutDiskUsage),
            ReadoutDiskActivity = s.Get(MonitorSettings.ReadoutDiskActivity),
            ReadoutBattery = s.Get(MonitorSettings.ReadoutBattery),
            ReadoutBatteryTime = s.Get(MonitorSettings.ReadoutBatteryTime),
            ReadoutBatteryTemperature = s.Get(MonitorSettings.ReadoutBatteryTemperature),
            ReadoutPeripheralBattery = s.Get(MonitorSettings.ReadoutPeripheralBattery),
            ReadoutConnectedDevices = s.Get(MonitorSettings.ReadoutConnectedDevices),
            ReadoutPower = s.Get(MonitorSettings.ReadoutPower),
            AlertCpu = s.Get(MonitorSettings.AlertCpu),
            AlertCpuTemperature = s.Get(MonitorSettings.AlertCpuTemperature),
            AlertMemory = s.Get(MonitorSettings.AlertMemory),
            AlertDisk = s.Get(MonitorSettings.AlertDisk),
            AlertBattery = s.Get(MonitorSettings.AlertBattery),
            AlertBatteryTemperature = s.Get(MonitorSettings.AlertBatteryTemperature),
            HasCpuFamily = Has(FeatureIds.MonitorCpu),
            HasGpuFamily = Has(FeatureIds.MonitorGpu),
            HasMemoryFamily = Has(FeatureIds.MonitorMemory),
            HasNetworkFamily = Has(FeatureIds.MonitorNetwork),
            HasDiskFamily = Has(FeatureIds.MonitorDisk),
            HasPowerFamily = Has(FeatureIds.MonitorPower),
            HasConnectedDevicesFamily = Has(FeatureIds.ConnectedDevices),
        };
    }

    /// <summary>Recomputes the plan from current inputs.</summary>
    private void RecomputePlan(out SamplingPlan plan, out bool foreground, out int interval)
    {
        lock (_gate)
        {
            RecomputePlanLocked();
            plan = _plan;
            foreground = _foreground;
            interval = _interval;
        }
    }

    private void RecomputePlanLocked()
    {
        var inputs = GatherInputs();
        _plan = SamplingPlanner.Compute(inputs);
        _foreground = inputs.Foreground;
        _interval = _settings.Get(MonitorSettings.IntervalSeconds);
        _planDirty = false;
        _cadence.Configure(_plan, _interval, _foreground);
    }

    private void WorkerLoop()
    {
        Log.Info("monitor", "Sampler started.");
        try
        {
            while (true)
            {
                SamplingPlan plan;
                bool foreground;
                int interval;
                bool skipGpu;
                IReadOnlyCollection<SampleKind>? due = null;
                double wait = 0;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        _thread = null;
                        return;
                    }

                    // Re-plan and decide to exit under one lock, so a lease taken
                    // meanwhile either is seen here or starts a new thread.
                    if (_planDirty)
                    {
                        RecomputePlanLocked();
                    }

                    plan = _plan;
                    foreground = _foreground;
                    interval = _interval;
                    if (!plan.Any)
                    {
                        _thread = null;
                        Log.Info("monitor", "Sampler stopped (no consumer).");
                        return;
                    }

                    var now = _sensors.Clock.Now;
                    var deferredGpuDue = !double.IsNaN(_deferredGpuAt) && now >= _deferredGpuAt;
                    skipGpu = now < _suppressGpuUntil && !deferredGpuDue;
                    if (_refreshRequested)
                    {
                        // Immediate refresh of everything needed (plan change, power change, explicit request).
                        _refreshRequested = false;
                        due = SamplingCadence.NeededKinds(plan);
                        _nextTickAt = now + _cadence.PeriodSeconds(interval);
                    }
                    else if (now >= _nextTickAt)
                    {
                        due = _cadence.Due(plan, interval, foreground);
                        if (deferredGpuDue && plan.GpuUsage && !due.Contains(SampleKind.GpuUsage))
                        {
                            due = [.. due, SampleKind.GpuUsage];
                        }

                        if (deferredGpuDue)
                        {
                            _deferredGpuAt = double.NaN;
                        }

                        _cadence.Advance();
                        _nextTickAt = Math.Max(_nextTickAt + _cadence.PeriodSeconds(interval), now + 0.05);
                    }
                    else if (deferredGpuDue)
                    {
                        // The deferred GPU read after a surface opened (spec §3.1.4).
                        _deferredGpuAt = double.NaN;
                        due = plan.GpuUsage ? [SampleKind.GpuUsage] : [];
                    }
                    else
                    {
                        wait = _nextTickAt - now;
                        if (!double.IsNaN(_deferredGpuAt))
                        {
                            wait = Math.Min(wait, _deferredGpuAt - now);
                        }
                    }
                }

                if (due is null)
                {
                    _wake.WaitOne(TimeSpan.FromSeconds(Math.Clamp(wait, 0.01, 60)));
                    continue;
                }

                RunRefresh(plan, due, foreground, interval, skipGpu);
            }
        }
        catch (Exception ex)
        {
            Log.Error("monitor", "Sampler crashed; it restarts on the next change.", ex);
            lock (_gate)
            {
                _thread = null;
            }
        }
    }

    private void RunRefresh(SamplingPlan plan, IReadOnlyCollection<SampleKind> due, bool foreground, int interval, bool skipGpu)
    {
        MonitorSnapshot snapshot;
        bool anyRead;
        lock (_engineGate)
        {
            (snapshot, anyRead) = _engine.Refresh(new RefreshRequest
            {
                Plan = plan,
                Due = due,
                Foreground = foreground,
                SkipGpu = skipGpu,
                IntervalSeconds = interval,
            });
        }

        // Pure carry-over ticks do not republish (spec §3.1.4).
        var stateChanged = plan != _publishedPlan || foreground != _publishedForegroundState;
        if (anyRead || stateChanged)
        {
            _publishedPlan = plan;
            _publishedForegroundState = foreground;
            Publish(snapshot);
        }
    }

    private void Publish(MonitorSnapshot snapshot)
    {
        bool post;
        lock (_gate)
        {
            post = _pending is null;
            _pending = snapshot;
        }

        if (post)
        {
            UiThread.Post(Deliver);
        }
    }

    private void Deliver()
    {
        MonitorSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _pending;
            _pending = null;
        }

        if (snapshot is null)
        {
            return;
        }

        Latest = snapshot;
        try
        {
            SnapshotPublished?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("monitor", "A snapshot subscriber failed.", ex);
        }
    }

    private void OnPowerStatusChanged(object? sender, EventArgs e)
    {
        bool wanted;
        lock (_gate)
        {
            wanted = (_plan.Power || _plan.PowerDraw) && _engine.HasBattery;
        }

        if (wanted)
        {
            RequestRefresh();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
        }

        _settingsSubscription.Dispose();
        _sensors.Power.PowerStatusChanged -= OnPowerStatusChanged;
        _wake.Set();
        thread?.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }

    private sealed class LeaseToken(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
