// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Awake;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;

namespace Rivet.Platform.Fake.Awake;

/// <summary>Records power requests instead of making them.</summary>
public sealed class FakePowerRequests : IPowerRequests
{
    public bool System { get; private set; }

    public bool Display { get; private set; }

    public string? Reason { get; private set; }

    public void Apply(bool keepSystemAwake, bool keepDisplayOn, string reason)
    {
        System = keepSystemAwake;
        Display = keepDisplayOn;
        Reason = reason;
        Log.Info("keep-awake", $"[fake] power request system={keepSystemAwake} display={keepDisplayOn}");
    }

    public void Release()
    {
        System = Display = false;
        Log.Info("keep-awake", "[fake] power requests released");
    }
}

public sealed class FakeSessionLock : ISessionLockMonitor
{
    public bool IsLocked { get; private set; }

    public event EventHandler<bool>? LockChanged;

    public void Set(bool locked)
    {
        IsLocked = locked;
        LockChanged?.Invoke(this, locked);
    }
}

/// <summary>A laptop on battery at 78 % unless a test says otherwise.</summary>
public sealed class FakePowerSource : IPowerSource
{
    public bool HasBattery { get; set; } = true;

    public bool OnExternalPower { get; set; }

    public int? BatteryPercent { get; set; } = 78;

    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class FakeDisplayTopology : IDisplayTopology
{
    public bool External { get; set; }

    public event EventHandler? Changed;

    public bool? ExternalDisplayConnected() => External;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class FakeRunningApps : IRunningApps
{
    public HashSet<string> Running { get; } = new(StringComparer.Ordinal) { "explorer.exe", "code.exe", "chrome.exe" };

    public IReadOnlySet<string> RunningExecutables() => new HashSet<string>(Running);
}

public sealed class FakePointerJiggler : IPointerJiggler
{
    public int Nudges { get; private set; }

    public void Nudge()
    {
        Nudges++;
        Log.Info("keep-awake", "[fake] pointer nudged");
    }
}

/// <summary>A laptop lid set to "Sleep" on AC and battery.</summary>
public sealed class FakeLidController : ILidActionController
{
    public bool HasLid { get; set; } = true;

    public bool? IsLidClosed { get; set; } = false;

    public uint Ac { get; private set; } = 1;

    public uint Dc { get; private set; } = 1;

    public Guid Scheme { get; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");

    public event EventHandler? LidChanged;

    public LidRecovery? ReadCurrent() => new() { Scheme = Scheme, OriginalAc = Ac, OriginalDc = Dc };

    public bool Write(Guid scheme, uint ac, uint dc)
    {
        Ac = ac;
        Dc = dc;
        Log.Info("keep-awake", $"[fake] lid action AC={ac} DC={dc}");
        return true;
    }

    public bool RequestSleep()
    {
        Log.Info("keep-awake", "[fake] sleep requested");
        return true;
    }

    public void Raise() => LidChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class FakeKeepAwakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakePowerRequests>();
        services.AddSingleton<IPowerRequests>(sp => sp.GetRequiredService<FakePowerRequests>());
        services.AddSingleton<FakeSessionLock>();
        services.AddSingleton<ISessionLockMonitor>(sp => sp.GetRequiredService<FakeSessionLock>());
        services.AddSingleton<FakePowerSource>();
        services.AddSingleton<IPowerSource>(sp => sp.GetRequiredService<FakePowerSource>());
        services.AddSingleton<FakeDisplayTopology>();
        services.AddSingleton<IDisplayTopology>(sp => sp.GetRequiredService<FakeDisplayTopology>());
        services.AddSingleton<FakeRunningApps>();
        services.AddSingleton<IRunningApps>(sp => sp.GetRequiredService<FakeRunningApps>());
        services.AddSingleton<FakePointerJiggler>();
        services.AddSingleton<IPointerJiggler>(sp => sp.GetRequiredService<FakePointerJiggler>());
        services.AddSingleton<FakeLidController>();
        services.AddSingleton<ILidActionController>(sp => sp.GetRequiredService<FakeLidController>());
    }
}
