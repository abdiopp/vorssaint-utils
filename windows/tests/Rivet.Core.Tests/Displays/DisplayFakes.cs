// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Displays;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.Core.Tests.Displays;

/// <summary>A serial worker under test control: work runs inline (or on <see cref="Drain"/>), time moves by hand.</summary>
internal sealed class ManualWorker : IBrightnessWorker
{
    private readonly Queue<Action> _queue = new();
    private readonly List<Scheduled> _timers = [];
    private bool _draining;

    public double Now { get; set; }

    /// <summary>When false, posted work waits for <see cref="Drain"/> (to observe coalescing).</summary>
    public bool AutoRun { get; set; } = true;

    public List<double> Pauses { get; } = [];

    public bool Disposed { get; private set; }

    public int Queued => _queue.Count;

    public void Post(Action work)
    {
        _queue.Enqueue(work);
        if (AutoRun)
        {
            Drain();
        }
    }

    public void Drain()
    {
        if (_draining)
        {
            return;
        }

        _draining = true;
        try
        {
            while (_queue.TryDequeue(out var work))
            {
                work();
            }
        }
        finally
        {
            _draining = false;
        }
    }

    public IDisposable Schedule(TimeSpan delay, Action work)
    {
        var timer = new Scheduled(Now + delay.TotalSeconds, work);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(double seconds)
    {
        Now += seconds;
        foreach (var timer in _timers.Where(t => !t.Cancelled && t.Due <= Now + 1e-9).OrderBy(t => t.Due).ToList())
        {
            _timers.Remove(timer);
            Post(timer.Work);
        }
    }

    public void Pause(TimeSpan duration)
    {
        Pauses.Add(duration.TotalSeconds);
        Now += duration.TotalSeconds;
    }

    public void Dispose() => Disposed = true;

    private sealed class Scheduled(double due, Action work) : IDisposable
    {
        public double Due { get; } = due;

        public Action Work { get; } = work;

        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }
}

internal sealed class FakeCatalog : IDisplayCatalog
{
    public List<DisplayDevice> Devices { get; } = [];

    public int Enumerations { get; private set; }

    public event EventHandler? ConfigurationChanged;

    public event EventHandler? Resumed;

    public IReadOnlyList<DisplayDevice> Enumerate()
    {
        Enumerations++;
        return Devices.ToList();
    }

    public void RaiseChanged() => ConfigurationChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeSystemBrightness : ISystemBrightness
{
    public Dictionary<string, int> Levels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Reads { get; private set; }

    public List<(string Id, int Percent)> Writes { get; } = [];

    public int? Read(DisplayDevice display)
    {
        Reads++;
        return Levels.TryGetValue(display.Id, out var level) ? level : null;
    }

    public bool Write(DisplayDevice display, int percent)
    {
        if (!Levels.ContainsKey(display.Id))
        {
            return false;
        }

        Levels[display.Id] = percent;
        Writes.Add((display.Id, percent));
        return true;
    }
}

internal enum DdcMode
{
    None,
    Live,
    WriteOnly,
    Dead,
}

internal sealed class FakeDdc(List<string> log) : IDdcChannel
{
    public Dictionary<string, DdcMode> Modes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Maximum { get; set; } = 100;

    public int Opens { get; private set; }

    public int Closes { get; private set; }

    public int Reads { get; private set; }

    public List<(string Id, int Value)> Writes { get; } = [];

    public void Open(IReadOnlyList<DisplayDevice> displays) => Opens++;

    public bool HasChannel(DisplayDevice display) => Modes.GetValueOrDefault(display.Id) != DdcMode.None;

    public DdcReading? Read(DisplayDevice display)
    {
        Reads++;
        return Modes.GetValueOrDefault(display.Id) == DdcMode.Live ? new DdcReading(Values.GetValueOrDefault(display.Id, 50), Maximum) : null;
    }

    public bool Write(DisplayDevice display, int value)
    {
        if (Modes.GetValueOrDefault(display.Id) is not (DdcMode.Live or DdcMode.WriteOnly))
        {
            log.Add($"ddc-reject {display.Id} {value}");
            return false;
        }

        Values[display.Id] = value;
        Writes.Add((display.Id, value));
        log.Add($"ddc {display.Id} {value}");
        return true;
    }

    public void Close() => Closes++;
}

internal sealed class RecordingDimmer(List<string> log) : ISoftwareDimmer
{
    public Dictionary<string, double> Active { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int RemoveAllCalls { get; private set; }

    public void Apply(DisplayDevice display, double factor)
    {
        Active[display.Id] = factor;
        log.Add($"dim {display.Id} {factor:0.###}");
    }

    public void Remove(string displayId)
    {
        if (Active.Remove(displayId))
        {
            log.Add($"undim {displayId}");
        }
    }

    public void RemoveAll()
    {
        RemoveAllCalls++;
        Active.Clear();
        log.Add("undim all");
    }
}

/// <summary>The service wired to fakes: a laptop panel, a readable monitor and whatever a test adds.</summary>
internal sealed class BrightnessRig
{
    public const string Panel = @"\\.\DISPLAY1";
    public const string Dell = @"\\.\DISPLAY2";
    public const string Tv = @"\\.\DISPLAY3";

    public BrightnessRig(bool start = true)
    {
        Ddc = new FakeDdc(Log);
        Dimmer = new RecordingDimmer(Log);
        Service = new BrightnessService(Settings, Catalog, System, Ddc, Dimmer, () => Pointer, () => Worker);
        Catalog.Devices.Add(Device(Panel, "Built-in", internalPanel: true, primary: true, x: 0));
        System.Levels[Panel] = 60;
        Catalog.Devices.Add(Device(Dell, "DELL U2720Q", x: 1920));
        Ddc.Modes[Dell] = DdcMode.Live;
        Ddc.Values[Dell] = 30;
        if (start)
        {
            Service.Sync(true);
        }
    }

    public List<string> Log { get; } = [];

    public SettingsStore Settings { get; } = SettingsStore.InMemory();

    public FakeCatalog Catalog { get; } = new();

    public FakeSystemBrightness System { get; } = new();

    public FakeDdc Ddc { get; }

    public RecordingDimmer Dimmer { get; }

    public ManualWorker Worker { get; } = new();

    public BrightnessService Service { get; }

    public string? Pointer { get; set; }

    public DisplayStatus Status(string id) => Service.Displays.Single(d => d.Id == id);

    public static DisplayDevice Device(string id, string name, bool internalPanel = false, bool primary = false, int x = 0) => new()
    {
        Id = id,
        Name = name,
        PathKey = $"path:{name}",
        Fingerprint = $"fp:{name}",
        IsInternal = internalPanel,
        IsPrimary = primary,
        Bounds = new PixelRect(x, 0, 1920, 1080),
    };

    /// <summary>Adds a monitor and rebuilds as a display change would.</summary>
    public void Connect(string id, string name, DdcMode mode, int x)
    {
        Catalog.Devices.Add(Device(id, name, x: x));
        Ddc.Modes[id] = mode;
        Catalog.RaiseChanged();
        Worker.Advance(BrightnessMath.SettleSeconds);
    }

    public void Disconnect(string id)
    {
        Catalog.Devices.RemoveAll(d => d.Id == id);
        Catalog.RaiseChanged();
        Worker.Advance(BrightnessMath.SettleSeconds);
    }
}
