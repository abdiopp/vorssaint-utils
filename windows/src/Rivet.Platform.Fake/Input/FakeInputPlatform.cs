// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Input;
using Rivet.Core.Platform;

namespace Rivet.Platform.Fake.Input;

/// <summary>Identities set by hand. Null means "not identified yet".</summary>
public sealed class FakeAppIdentityResolver : IAppIdentityResolver
{
    private int _tracking;

    public string? PointerAppPath { get; set; } = @"C:\Program Files\Example\Example.exe";

    public string? ForegroundAppPath { get; set; } = @"C:\Program Files\Example\Example.exe";

    public nint ForegroundWindowHandle { get; set; } = 0x1000;

    public int TrackingCount => Volatile.Read(ref _tracking);

    public AppIdentityInfo? ForegroundApp => ForegroundAppPath is { } path ? new AppIdentityInfo(path) : null;

    public nint ForegroundWindow => ForegroundWindowHandle;

    public AppIdentityInfo? PointerApp(PixelPoint point) => PointerAppPath is { } path ? new AppIdentityInfo(path) : null;

    public (nint Window, AppIdentityInfo? App) ResolveForegroundNow() => (ForegroundWindowHandle, ForegroundApp);

    public IDisposable Track()
    {
        Interlocked.Increment(ref _tracking);
        return new Releaser(() => Interlocked.Decrement(ref _tracking));
    }
}

/// <summary>A hand-set list of running executables; tests call <see cref="NotifyChanged"/>.</summary>
public sealed class FakeRunningApps : IRunningAppsMonitor
{
    private readonly List<Action> _watchers = [];

    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int WatcherCount
    {
        get
        {
            lock (_watchers)
            {
                return _watchers.Count;
            }
        }
    }

    public bool IsAnyRunning(AppExclusionList apps)
    {
        lock (Running)
        {
            return Running.Any(apps.Matches);
        }
    }

    public IDisposable Watch(Action changed)
    {
        lock (_watchers)
        {
            _watchers.Add(changed);
        }

        return new Releaser(() =>
        {
            lock (_watchers)
            {
                _watchers.Remove(changed);
            }
        });
    }

    public void NotifyChanged()
    {
        Action[] watchers;
        lock (_watchers)
        {
            watchers = [.. _watchers];
        }

        foreach (var watcher in watchers)
        {
            watcher();
        }
    }
}

/// <summary>The notch heuristic, plus switches tests use to simulate a touchpad or a high-resolution wheel.</summary>
public sealed class FakeWheelClassifier : IWheelDeviceClassifier
{
    /// <summary>Every wheel event comes from the touchpad.</summary>
    public bool TouchpadActive { get; set; }

    /// <summary>Fractional deltas come from a high-resolution mouse (as Raw Input tracking would tell).</summary>
    public bool FractionsAreMouse { get; set; }

    public WheelSource Classify(int delta, long timestampNs)
    {
        if (TouchpadActive)
        {
            return WheelSource.Touchpad;
        }

        var baseline = NotchWheelClassifier.ClassifyDelta(delta);
        return baseline == WheelSource.Unknown && FractionsAreMouse ? WheelSource.MouseHighResolution : baseline;
    }

    public IDisposable Track() => NoopDisposable.Instance;
}

public sealed class FakeKeyboardInfo : IKeyboardInfo
{
    private readonly Dictionary<int, bool> _physical = [];

    public int RepeatDelayMs { get; set; } = 500;

    public int RepeatIntervalMs { get; set; } = 33;

    public bool IsCapsLockOn { get; set; }

    public int InputSourceSwitches { get; private set; }

    public int PhysicalTrackers { get; private set; }

    public void SetPhysical(int virtualKey, bool? down)
    {
        lock (_physical)
        {
            if (down is { } value)
            {
                _physical[virtualKey] = value;
            }
            else
            {
                _physical.Remove(virtualKey);
            }
        }
    }

    public bool? IsPhysicallyDown(int virtualKey)
    {
        lock (_physical)
        {
            return _physical.TryGetValue(virtualKey, out var down) ? down : null;
        }
    }

    public IDisposable TrackPhysicalKeys()
    {
        PhysicalTrackers++;
        return new Releaser(() => PhysicalTrackers--);
    }

    public void SelectNextInputSource() => InputSourceSwitches++;

    public int ScanToVirtualKey(int scanId) => DebounceKeyCatalog.UsVirtualKey(scanId) ?? 0;
}

/// <summary>A frame timer tests step by hand with <see cref="Tick"/>.</summary>
public sealed class FakeGlideFrameTimer : IGlideFrameTimer
{
    private Func<double, bool>? _onFrame;

    public bool IsRunning => _onFrame is not null;

    public int Starts { get; private set; }

    public void Start(Func<double, bool> onFrame)
    {
        if (_onFrame is null)
        {
            Starts++;
        }

        _onFrame = onFrame;
    }

    public void Stop() => _onFrame = null;

    /// <summary>Runs one frame; returns whether the glide continues.</summary>
    public bool Tick(double seconds = 1.0 / 60)
    {
        var callback = _onFrame;
        if (callback is null)
        {
            return false;
        }

        var more = callback(seconds);
        if (!more && ReferenceEquals(_onFrame, callback))
        {
            _onFrame = null;
        }

        return more;
    }

    /// <summary>Runs frames until the glide lands (bounded).</summary>
    public int RunToEnd(double seconds = 1.0 / 60, int maxFrames = 10_000)
    {
        var frames = 0;
        while (IsRunning && frames < maxFrames)
        {
            Tick(seconds);
            frames++;
        }

        return frames;
    }
}

public sealed class FakeAppCatalog : IAppCatalog
{
    public List<AppCatalogEntry> Apps { get; } =
    [
        new("Blender", @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"),
        new("Code", @"C:\Users\Developer\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
        new("Example", @"C:\Program Files\Example\Example.exe"),
        new("Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
    ];

    public IReadOnlyList<AppCatalogEntry> ListApps() => Apps;
}

internal sealed class Releaser(Action release) : IDisposable
{
    private Action? _release = release;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
