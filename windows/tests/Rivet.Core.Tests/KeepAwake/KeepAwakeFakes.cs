// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Awake;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.Core.Tests.Awake;

/// <summary>A manual clock: <see cref="Advance"/> fires due timers in order.</summary>
internal sealed class ManualTimers : IKeepAwakeTimers
{
    private readonly List<(DateTimeOffset Due, Action Callback, Token Token)> _pending = [];

    public DateTimeOffset Now { get; private set; } = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    public IDisposable Schedule(TimeSpan dueIn, Action callback)
    {
        var token = new Token();
        _pending.Add((Now + (dueIn < TimeSpan.Zero ? TimeSpan.Zero : dueIn), callback, token));
        return token;
    }

    public void Advance(TimeSpan by)
    {
        var target = Now + by;
        while (true)
        {
            var next = _pending.Where(p => !p.Token.Cancelled && p.Due <= target).OrderBy(p => p.Due).FirstOrDefault();
            if (next.Callback is null)
            {
                break;
            }

            _pending.Remove(next);
            Now = next.Due > Now ? next.Due : Now;
            next.Callback();
        }

        Now = target;
        _pending.RemoveAll(p => p.Token.Cancelled);
    }

    public sealed class Token : IDisposable
    {
        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }
}

internal sealed class FakeRequests : IPowerRequests
{
    public bool System { get; private set; }

    public bool Display { get; private set; }

    public int Releases { get; private set; }

    public void Apply(bool keepSystemAwake, bool keepDisplayOn, string reason)
    {
        System = keepSystemAwake;
        Display = keepDisplayOn;
    }

    public void Release()
    {
        System = Display = false;
        Releases++;
    }
}

internal sealed class FakeLock : ISessionLockMonitor
{
    public bool IsLocked { get; private set; }

    public event EventHandler<bool>? LockChanged;

    public void Set(bool locked)
    {
        IsLocked = locked;
        LockChanged?.Invoke(this, locked);
    }
}

internal sealed class FakePower : IPowerSource
{
    public bool HasBattery { get; set; } = true;

    public bool OnExternalPower { get; set; }

    public int? BatteryPercent { get; set; } = 80;

    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeDisplays : IDisplayTopology
{
    public bool External { get; set; }

    public event EventHandler? Changed;

    public bool? ExternalDisplayConnected() => External;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeApps : IRunningApps
{
    public HashSet<string> Running { get; } = new(StringComparer.Ordinal);

    public IReadOnlySet<string> RunningExecutables() => new HashSet<string>(Running);
}

internal sealed class FakeJiggler : IPointerJiggler
{
    public int Nudges { get; private set; }

    public void Nudge() => Nudges++;
}

internal sealed class FakeLid : ILidActionController
{
    public bool HasLid { get; set; } = true;

    public bool? IsLidClosed { get; set; } = false;

    public Guid Scheme { get; set; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");

    public uint Ac { get; set; } = 1;

    public uint Dc { get; set; } = 1;

    public bool FailWrites { get; set; }

    public bool SleepSucceeds { get; set; }

    public int SleepRequests { get; private set; }

    public List<string> Log { get; } = [];

    public event EventHandler? LidChanged;

    public LidRecovery? ReadCurrent() => new() { Scheme = Scheme, OriginalAc = Ac, OriginalDc = Dc };

    public bool Write(Guid scheme, uint ac, uint dc)
    {
        Log.Add($"write {ac}/{dc}");
        if (FailWrites)
        {
            return false;
        }

        Ac = ac;
        Dc = dc;
        return true;
    }

    public bool RequestSleep()
    {
        SleepRequests++;
        return SleepSucceeds;
    }

    public void Raise() => LidChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class RecordingToasts : INotificationService
{
    public List<NotificationRequest> Shown { get; } = [];

    public bool IsEnabled => true;

    public event EventHandler<string>? Activated
    {
        add { }
        remove { }
    }

    public void Show(NotificationRequest request) => Shown.Add(request);
}

/// <summary>A manager with every fake, ready to Sync(true).</summary>
internal sealed class KeepAwakeRig
{
    public SettingsStore Settings { get; } = SettingsStore.InMemory();

    public ManualTimers Timers { get; } = new();

    public FakeRequests Requests { get; } = new();

    public FakeLock Lock { get; } = new();

    public FakePower Power { get; } = new();

    public FakeDisplays Displays { get; } = new();

    public FakeApps Apps { get; } = new();

    public FakeJiggler Jiggler { get; } = new();

    public FakeLid Lid { get; } = new();

    public RecordingToasts Toasts { get; } = new();

    public KeepAwakeManager Create(bool sync = true)
    {
        var manager = new KeepAwakeManager(Settings, Timers, Requests, Power, Lock, Displays, Apps, Jiggler, Lid, Toasts);
        if (sync)
        {
            manager.Sync(true);
        }

        return manager;
    }
}
