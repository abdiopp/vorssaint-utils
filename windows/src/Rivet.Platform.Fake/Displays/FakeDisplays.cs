// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Displays;
using Rivet.Core.Modules;
using Rivet.Core.Platform;

namespace Rivet.Platform.Fake.Displays;

/// <summary>A laptop with a readable external monitor and a TV that ignores brightness commands.</summary>
public sealed class FakeDisplayCatalog : IDisplayCatalog
{
    public const string Panel = @"\\.\DISPLAY1";
    public const string Monitor = @"\\.\DISPLAY2";
    public const string Tv = @"\\.\DISPLAY3";

    private readonly object _gate = new();

    public List<DisplayDevice> Devices { get; } =
    [
        new()
        {
            Id = Panel,
            Name = string.Empty,
            PathKey = @"\\?\DISPLAY#SDC4152#4&2a3b4c5d&0&UID8388688",
            Fingerprint = "4D10:4152",
            IsInternal = true,
            IsPrimary = true,
            Bounds = new PixelRect(0, 0, 1920, 1080),
        },
        new()
        {
            Id = Monitor,
            Name = "DELL U2720Q",
            PathKey = @"\\?\DISPLAY#DELA0EE#5&1d2b3c4&0&UID4353",
            Fingerprint = "10AC:A0EE",
            Bounds = new PixelRect(1920, 0, 2560, 1440),
        },
        new()
        {
            Id = Tv,
            Name = "LG TV SSCR2",
            PathKey = @"\\?\DISPLAY#GSM0001#5&1d2b3c4&0&UID4354",
            Fingerprint = "1E6D:0001",
            Bounds = new PixelRect(4480, 0, 1920, 1080),
        },
    ];

    public event EventHandler? ConfigurationChanged;

    public event EventHandler? Resumed;

    public IReadOnlyList<DisplayDevice> Enumerate()
    {
        lock (_gate)
        {
            return Devices.ToList();
        }
    }

    public void RaiseChanged() => ConfigurationChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}

/// <summary>The laptop panel's brightness, as WMI would report it.</summary>
public sealed class FakeSystemBrightness : ISystemBrightness
{
    public int Percent { get; set; } = 70;

    public int? Read(DisplayDevice display) => display.IsInternal ? Percent : null;

    public bool Write(DisplayDevice display, int percent)
    {
        if (!display.IsInternal)
        {
            return false;
        }

        Percent = Math.Clamp(percent, 0, 100);
        Log.Info("brightness", $"[fake] panel brightness {Percent}%");
        return true;
    }
}

/// <summary>DDC/CI: the monitor answers, the TV rejects writes.</summary>
public sealed class FakeDdcChannel : IDdcChannel
{
    private readonly object _gate = new();

    public Dictionary<string, int> Values { get; } = new(StringComparer.OrdinalIgnoreCase) { [FakeDisplayCatalog.Monitor] = 45 };

    public HashSet<string> Rejecting { get; } = new(StringComparer.OrdinalIgnoreCase) { FakeDisplayCatalog.Tv };

    public void Open(IReadOnlyList<DisplayDevice> displays)
    {
    }

    public bool HasChannel(DisplayDevice display) => !display.IsInternal;

    public DdcReading? Read(DisplayDevice display)
    {
        lock (_gate)
        {
            return Values.TryGetValue(display.Id, out var value) ? new DdcReading(value, 100) : null;
        }
    }

    public bool Write(DisplayDevice display, int value)
    {
        lock (_gate)
        {
            if (Rejecting.Contains(display.Id))
            {
                return false;
            }

            Values[display.Id] = value;
        }

        Log.Info("brightness", $"[fake] DDC luminance {value} on {display.Name}");
        return true;
    }

    public void Close()
    {
    }
}

public sealed class FakeDisplaysRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeDisplayCatalog>();
        services.AddSingleton<IDisplayCatalog>(sp => sp.GetRequiredService<FakeDisplayCatalog>());
        services.AddSingleton<FakeSystemBrightness>();
        services.AddSingleton<ISystemBrightness>(sp => sp.GetRequiredService<FakeSystemBrightness>());
        services.AddSingleton<FakeDdcChannel>();
        services.AddSingleton<IDdcChannel>(sp => sp.GetRequiredService<FakeDdcChannel>());
    }
}
