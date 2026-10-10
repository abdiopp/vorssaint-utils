// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Platform;
using Rivet.Platform.Windows.CleaningMode;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.RadialMenu;

/// <summary>
/// Radial slices on Windows: ShellExecute for files, links and apps (activating
/// a running copy of an .exe first), shell display names and icons, and
/// window placement for the 41 layout actions with DWM frame correction so
/// windows line up without their invisible resize borders.
/// </summary>
public sealed unsafe class WindowsRadialPlatform : IRadialPlatform
{
    private readonly Dictionary<nint, RECT> _previousBounds = [];

    public bool LaunchOrActivateApp(string path)
    {
        var expanded = ExpandPath(path);
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return Start("explorer.exe", path);
        }

        if (!File.Exists(expanded) && !Directory.Exists(expanded))
        {
            return false;
        }

        if (expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && FindAppWindow(expanded) is { } hwnd)
        {
            if (PInvoke.IsIconic(hwnd))
            {
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
            }

            if (PInvoke.SetForegroundWindow(hwnd))
            {
                return true;
            }
        }

        return Start(expanded, null);
    }

    public bool OpenPath(string path)
    {
        var expanded = ExpandPath(path);
        if (!File.Exists(expanded) && !Directory.Exists(expanded))
        {
            return false;
        }

        return Start(expanded, null);
    }

    public void OpenUrl(string url) => Start(url, null);

    public string DisplayName(string path)
    {
        var expanded = ExpandPath(path);
        try
        {
            var info = new SHFILEINFOW();
            if (PInvoke.SHGetFileInfo(expanded, 0, ref info, SHGFI_FLAGS.SHGFI_DISPLAYNAME) != 0)
            {
                var name = info.szDisplayName.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var description = FileVersionInfo.GetVersionInfo(expanded).FileDescription;
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            return description.Trim();
                        }
                    }

                    return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
        }

        var trimmed = expanded.TrimEnd('\\', '/');
        var fallback = Path.GetFileName(trimmed);
        return fallback.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || fallback.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? fallback[..^4] : fallback;
    }

    public PixelBuffer? Icon(string path, int sizePx) => ShellImages.Icon(ExpandPath(path), sizePx);

    public nint ForegroundWindow() => (nint)PInvoke.GetForegroundWindow().Value;

    public void Activate(nint hwnd)
    {
        if (hwnd != 0 && PInvoke.IsWindow(new HWND((void*)hwnd)))
        {
            PInvoke.SetForegroundWindow(new HWND((void*)hwnd));
        }
    }

    public bool ApplyWindowLayout(nint handle, string layoutId)
    {
        var hwnd = new HWND((void*)handle);
        if (handle == 0 || !PInvoke.IsWindow(hwnd) || !PInvoke.IsWindowVisible(hwnd) || hwnd == PInvoke.GetShellWindow())
        {
            return false;
        }

        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, &info))
        {
            return false;
        }

        var work = ToRect(info.rcWork);
        var frame = VisibleFrame(hwnd);
        var screens = new List<PixelRect>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (m, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (PInvoke.GetMonitorInfo(m, &mi))
            {
                screens.Add(ToRect(mi.rcWork));
            }

            return true;
        }, 0);

        var margin = (int)Math.Round(16 * (PInvoke.GetDpiForWindow(hwnd) / 96.0));
        var result = WindowLayoutActions.Compute(layoutId, frame, work, screens, margin);
        if (result is not { } layout)
        {
            return false;
        }

        switch (layout.Apply)
        {
            case WindowLayoutApply.Maximize:
                Remember(hwnd);
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MAXIMIZE);
                return true;
            case WindowLayoutApply.Restore:
                if (PInvoke.IsZoomed(hwnd))
                {
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
                    return true;
                }

                if (_previousBounds.Remove(handle, out var previous))
                {
                    PInvoke.SetWindowPos(hwnd, HWND.Null, previous.left, previous.top, previous.right - previous.left, previous.bottom - previous.top,
                        SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                    return true;
                }

                return false;
            default:
                Remember(hwnd);
                if (PInvoke.IsZoomed(hwnd) || PInvoke.IsIconic(hwnd))
                {
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
                }

                // SetWindowPos works on the outer rectangle, which includes invisible borders on Windows 10/11.
                PInvoke.GetWindowRect(hwnd, out var outer);
                var visible = VisibleFrame(hwnd);
                var left = layout.Bounds.X - (visible.X - outer.left);
                var top = layout.Bounds.Y - (visible.Y - outer.top);
                var width = layout.Bounds.Width + ((outer.right - outer.left) - visible.Width);
                var height = layout.Bounds.Height + ((outer.bottom - outer.top) - visible.Height);
                return PInvoke.SetWindowPos(hwnd, HWND.Null, left, top, width, height, SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
    }

    public string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = home + expanded[1..];
        }

        return expanded.Replace('/', '\\');
    }

    public void Beep() => PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_OK);

    private void Remember(HWND hwnd)
    {
        if (!PInvoke.IsZoomed(hwnd) && PInvoke.GetWindowRect(hwnd, out var rect))
        {
            _previousBounds[(nint)hwnd.Value] = rect;
        }
    }

    private static PixelRect VisibleFrame(HWND hwnd)
    {
        RECT rect;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT)).Succeeded)
        {
            return ToRect(rect);
        }

        PInvoke.GetWindowRect(hwnd, out rect);
        return ToRect(rect);
    }

    private static PixelRect ToRect(RECT r) => new(r.left, r.top, r.right - r.left, r.bottom - r.top);

    /// <summary>A visible, unowned top-level window of a process running <paramref name="exePath"/>.</summary>
    internal static HWND? FindAppWindow(string exePath)
    {
        HWND? found = null;
        var target = Path.GetFullPath(exePath);
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hwnd) || !PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull)
            {
                return true;
            }

            var path = Win32Windows.ProcessPathOf((nint)hwnd.Value);
            if (path is not null && string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    /// <summary>A visible, unowned top-level window whose process file name is <paramref name="exeName"/>.</summary>
    internal static HWND? FindWindowByExeName(string exeName)
    {
        HWND? found = null;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hwnd) || !PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull)
            {
                return true;
            }

            var path = Win32Windows.ProcessPathOf((nint)hwnd.Value);
            if (path is not null && string.Equals(Path.GetFileName(path), exeName, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    internal static bool Start(string target, string? arguments)
    {
        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (arguments is not null)
            {
                info.Arguments = arguments;
            }

            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn("radial", $"Could not open '{target}'.", ex);
            return false;
        }
    }
}

public sealed class RadialMenuWindowsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IRadialPlatform, WindowsRadialPlatform>();
        services.AddSingleton<INowPlayingService, WindowsNowPlaying>();
    }
}
