// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Displays;
using Rivet.Core.Modules;
using Rivet.Core.Platform;
using Rivet.Platform.Windows.Interop;
using Rivet.Platform.Windows.SystemMonitor;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Rivet.Platform.Windows.Displays;

/// <summary>Monitor handles by GDI device name (<c>\\.\DISPLAY1</c>).</summary>
internal static unsafe class MonitorHandles
{
    public sealed record Monitor(HMONITOR Handle, string DeviceName, PixelRect Bounds, bool IsPrimary);

    public static List<Monitor> Enumerate()
    {
        var result = new List<Monitor>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                var r = info.monitorInfo.rcMonitor;
                result.Add(new Monitor(monitor, info.szDevice.ToString(), new PixelRect(r.left, r.top, r.right - r.left, r.bottom - r.top), (info.monitorInfo.dwFlags & 0x1) != 0));
            }

            return true;
        }, default);
        return result;
    }
}

/// <summary>
/// Active displays with their monitor names (QueryDisplayConfig) and the
/// events that rebuild the brightness routes: WM_DISPLAYCHANGE and resume
/// from sleep.
/// </summary>
public sealed class WindowsDisplayCatalog : IDisplayCatalog, IDisposable
{
    private const uint WmDisplayChange = 0x007E;
    private const uint WmPowerBroadcast = 0x0218;
    private const nuint PbtApmResumeAutomatic = 0x12;

    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;

    public WindowsDisplayCatalog(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
        _host.AddHandler(_handler);
    }

    public event EventHandler? ConfigurationChanged;

    public event EventHandler? Resumed;

    public IReadOnlyList<DisplayDevice> Enumerate()
    {
        var monitors = MonitorHandles.Enumerate();
        var targets = DisplayConfig.ActiveTargets() ?? [];
        var result = new List<DisplayDevice>();
        foreach (var monitor in monitors)
        {
            var paths = targets.Where(t => string.Equals(t.GdiDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
            var real = paths.Where(t => !t.IsVirtual).ToList();
            if (paths.Count > 0 && real.Count == 0)
            {
                continue; // Virtual displays (remote desktop helpers, software monitors) have no brightness.
            }

            // Mirrored outputs share one source: the source's row controls them (the panel's name wins).
            var target = real.FirstOrDefault(t => t.IsInternal) ?? real.FirstOrDefault();
            result.Add(new DisplayDevice
            {
                Id = monitor.DeviceName,
                Name = target?.FriendlyName ?? string.Empty,
                PathKey = target is { DevicePath.Length: > 0 } ? target.DevicePath : "gdi:" + monitor.DeviceName,
                Fingerprint = target is null ? monitor.DeviceName : $"{target.ManufacturerId:X4}:{target.ProductCodeId:X4}",
                IsInternal = target?.IsInternal ?? false,
                IsPrimary = monitor.IsPrimary,
                Bounds = monitor.Bounds,
            });
        }

        return result;
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == WmDisplayChange)
        {
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (message == WmPowerBroadcast && wParam == PbtApmResumeAutomatic)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }

    public void Dispose() => _host.RemoveHandler(_handler);
}

/// <summary>
/// DDC/CI luminance through the Monitor Configuration API (dxva2). The API
/// does the framing and checksums; each call takes 40–100 ms, so only the
/// brightness worker calls it. Handles are reopened at every rebuild because
/// monitor handles change with the display configuration.
/// </summary>
public sealed unsafe class WindowsDdcChannel : IDdcChannel, IDisposable
{
    private const byte Luminance = 0x10;
    private const int ReadAttempts = 2;
    private const int WriteAttempts = 5; // one try and up to four retries
    private const int RetryPauseMs = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, PHYSICAL_MONITOR[]> _monitors = new(StringComparer.OrdinalIgnoreCase);

    public void Open(IReadOnlyList<DisplayDevice> displays)
    {
        lock (_gate)
        {
            CloseLocked();
            var handles = MonitorHandles.Enumerate();
            foreach (var display in displays.Where(d => !d.IsInternal))
            {
                var monitor = handles.FirstOrDefault(h => string.Equals(h.DeviceName, display.Id, StringComparison.OrdinalIgnoreCase));
                if (monitor is null || !PInvoke.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor.Handle, out var count) || count == 0)
                {
                    continue;
                }

                var array = new PHYSICAL_MONITOR[count];
                fixed (PHYSICAL_MONITOR* first = array)
                {
                    if (!PInvoke.GetPhysicalMonitorsFromHMONITOR(monitor.Handle, count, first))
                    {
                        Log.Debug("brightness", $"No physical monitor for {display.Id} ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                        continue;
                    }
                }

                _monitors[display.Id] = array;
            }
        }
    }

    public bool HasChannel(DisplayDevice display)
    {
        lock (_gate)
        {
            return _monitors.ContainsKey(display.Id);
        }
    }

    public DdcReading? Read(DisplayDevice display)
    {
        lock (_gate)
        {
            if (!_monitors.TryGetValue(display.Id, out var array))
            {
                return null;
            }

            foreach (var monitor in array)
            {
                for (var attempt = 0; attempt < ReadAttempts; attempt++)
                {
                    MC_VCP_CODE_TYPE type;
                    uint current;
                    uint maximum;
                    if (PInvoke.GetVCPFeatureAndVCPFeatureReply(monitor.hPhysicalMonitor, Luminance, &type, &current, &maximum) != 0)
                    {
                        return new DdcReading((int)current, (int)maximum);
                    }

                    Thread.Sleep(RetryPauseMs);
                }
            }

            return null;
        }
    }

    public bool Write(DisplayDevice display, int value)
    {
        lock (_gate)
        {
            if (!_monitors.TryGetValue(display.Id, out var array))
            {
                return false;
            }

            // Mirrored monitors on one source all get the value.
            var any = false;
            foreach (var monitor in array)
            {
                for (var attempt = 0; attempt < WriteAttempts; attempt++)
                {
                    if (PInvoke.SetVCPFeature(monitor.hPhysicalMonitor, Luminance, (uint)Math.Max(0, value)) != 0)
                    {
                        any = true;
                        break;
                    }

                    Thread.Sleep(RetryPauseMs);
                }
            }

            return any;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            CloseLocked();
        }
    }

    private void CloseLocked()
    {
        foreach (var array in _monitors.Values)
        {
            fixed (PHYSICAL_MONITOR* first = array)
            {
                PInvoke.DestroyPhysicalMonitors((uint)array.Length, first);
            }
        }

        _monitors.Clear();
    }

    public void Dispose() => Close();
}

/// <summary>
/// Laptop panel brightness through WMI (<c>root\wmi</c>):
/// <c>WmiMonitorBrightness.CurrentBrightness</c> to read and
/// <c>WmiMonitorBrightnessMethods.WmiSetBrightness</c> to write (0–100; the
/// panel maps it to its nearest supported level). Desktops have no such
/// classes; the first "not supported" answer stops further queries.
/// </summary>
public sealed class WindowsSystemBrightness : ISystemBrightness, IDisposable
{
    private readonly WmiClient _wmi = new(@"ROOT\WMI");
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _methodPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _unsupported;

    public int? Read(DisplayDevice display)
    {
        if (!display.IsInternal || Unsupported)
        {
            return null;
        }

        try
        {
            var rows = _wmi.Query("SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE", "InstanceName", "CurrentBrightness");
            return Match(rows, display.PathKey) is { } row && row["CurrentBrightness"] is { } value ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex)
        {
            Fail(ex, "read");
            return null;
        }
    }

    public bool Write(DisplayDevice display, int percent)
    {
        if (!display.IsInternal || Unsupported)
        {
            return false;
        }

        try
        {
            string? path;
            lock (_gate)
            {
                _methodPaths.TryGetValue(display.PathKey, out path);
            }

            if (path is null)
            {
                var rows = _wmi.Query("SELECT __PATH, InstanceName FROM WmiMonitorBrightnessMethods WHERE Active = TRUE", "__PATH", "InstanceName");
                path = Match(rows, display.PathKey)?["__PATH"] as string;
                if (path is null)
                {
                    return false;
                }

                lock (_gate)
                {
                    _methodPaths[display.PathKey] = path;
                }
            }

            // CIM uint32 travels as VT_I4 and uint8 as VT_UI1.
            _wmi.Invoke(path, "WmiMonitorBrightnessMethods", "WmiSetBrightness",
                [("Timeout", (object)0), ("Brightness", (object)(byte)Math.Clamp(percent, 0, 100))]);
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _methodPaths.Remove(display.PathKey);
            }

            Fail(ex, "write");
            return false;
        }
    }

    private bool Unsupported
    {
        get
        {
            lock (_gate)
            {
                return _unsupported;
            }
        }
    }

    private void Fail(Exception ex, string what)
    {
        if (WmiClient.Classify(ex) == WmiFailure.NotSupported)
        {
            lock (_gate)
            {
                _unsupported = true;
            }

            Log.Info("brightness", "This PC has no WMI panel brightness; the built-in display uses dimming.");
            return;
        }

        Log.Warn("brightness", $"WMI panel brightness {what} failed.", ex);
    }

    /// <summary>
    /// Pairs a WMI instance (<c>DISPLAY\SDC4152\4&amp;2a3b&amp;0&amp;UID8388688_0</c>) with a
    /// display path (<c>\\?\DISPLAY#SDC4152#4&amp;2a3b&amp;0&amp;UID8388688#{guid}</c>);
    /// a single instance pairs with the panel.
    /// </summary>
    internal static Dictionary<string, object?>? Match(List<Dictionary<string, object?>> rows, string pathKey)
    {
        var key = InstanceKey(pathKey);
        if (key is not null)
        {
            foreach (var row in rows)
            {
                if (row.GetValueOrDefault("InstanceName") is string name && string.Equals(TrimInstanceSuffix(name), key, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }
        }

        return rows.Count == 1 ? rows[0] : null;
    }

    internal static string? InstanceKey(string devicePath)
    {
        var path = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var parts = path.Split('#');
        return parts.Length >= 3 ? string.Join('\\', parts[0], parts[1], parts[2]) : null;
    }

    internal static string TrimInstanceSuffix(string instanceName)
    {
        var underscore = instanceName.LastIndexOf('_');
        return underscore > 0 && instanceName[(underscore + 1)..].All(char.IsAsciiDigit) ? instanceName[..underscore] : instanceName;
    }

    public void Dispose() => _wmi.Dispose();
}

public sealed class DisplaysRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IDisplayCatalog, WindowsDisplayCatalog>();
        services.AddSingleton<ISystemBrightness, WindowsSystemBrightness>();
        services.AddSingleton<IDdcChannel, WindowsDdcChannel>();
    }
}
