// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Rivet.Platform.Windows.Shell;

/// <summary>Monitor layout in physical pixels (the process is per-monitor DPI aware v2).</summary>
public sealed unsafe class WindowsScreens : IScreenService, IDisposable
{
    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;
    private IReadOnlyList<ScreenInfo>? _screens;

    public WindowsScreens(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
        _host.AddHandler(_handler);
    }

    public event EventHandler? ScreensChanged;

    public IReadOnlyList<ScreenInfo> Screens => _screens ??= Enumerate();

    public ScreenInfo Primary => Screens.FirstOrDefault(s => s.IsPrimary) ?? Screens[0];

    public PixelRect VirtualScreen => Screens.Aggregate(default(PixelRect), (acc, s) => acc.Union(s.Bounds));

    public PixelPoint CursorPosition =>
        PInvoke.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    public ScreenInfo ScreenFromPoint(PixelPoint point) =>
        Screens.FirstOrDefault(s => s.Bounds.Contains(point))
        ?? Screens.OrderBy(s => Distance(s.Bounds, point)).First();

    private static long Distance(PixelRect r, PixelPoint p)
    {
        var dx = Math.Max(Math.Max(r.X - p.X, 0), p.X - r.Right);
        var dy = Math.Max(Math.Max(r.Y - p.Y, 0), p.Y - r.Bottom);
        return ((long)dx * dx) + ((long)dy * dy);
    }

    private static IReadOnlyList<ScreenInfo> Enumerate()
    {
        var result = new List<ScreenInfo>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                return true;
            }

            var scale = 1.0;
            if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Succeeded && dpiX > 0)
            {
                scale = dpiX / 96.0;
            }

            var device = info.szDevice.ToString();
            var m = info.monitorInfo;
            result.Add(new ScreenInfo
            {
                Id = device,
                FriendlyName = FriendlyName(device),
                Bounds = new PixelRect(m.rcMonitor.left, m.rcMonitor.top, m.rcMonitor.right - m.rcMonitor.left, m.rcMonitor.bottom - m.rcMonitor.top),
                WorkArea = new PixelRect(m.rcWork.left, m.rcWork.top, m.rcWork.right - m.rcWork.left, m.rcWork.bottom - m.rcWork.top),
                Scale = scale,
                IsPrimary = (m.dwFlags & 0x1) != 0, // MONITORINFOF_PRIMARY
            });
            return true;
        }, default);

        if (result.Count == 0)
        {
            result.Add(new ScreenInfo
            {
                Id = "primary",
                FriendlyName = "Display",
                Bounds = new PixelRect(0, 0, 1920, 1080),
                WorkArea = new PixelRect(0, 0, 1920, 1040),
                Scale = 1,
                IsPrimary = true,
            });
        }

        return result;
    }

    private static string FriendlyName(string device)
    {
        var display = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        return PInvoke.EnumDisplayDevices(device, 0, ref display, 0) ? display.DeviceString.ToString() : device;
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == PInvoke.WM_DISPLAYCHANGE || message == PInvoke.WM_DPICHANGED || message == PInvoke.WM_SETTINGCHANGE)
        {
            _screens = null;
            ScreensChanged?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }

    public void Dispose() => _host.RemoveHandler(_handler);
}
