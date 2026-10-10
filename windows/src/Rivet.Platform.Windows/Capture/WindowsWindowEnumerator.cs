// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Capture;

/// <summary>
/// Top-level windows in z-order (EnumWindows is front to back) with their
/// visible frames (DWMWA_EXTENDED_FRAME_BOUNDS, no invisible resize border).
/// Skips invisible, minimized, cloaked (other virtual desktops, suspended
/// UWP), transparent, tool and shell windows. Windows of this app are kept
/// (the picker decides) and flagged protected when they carry capture exclusion.
/// </summary>
public sealed unsafe class WindowsWindowEnumerator : IWindowEnumerator
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    public IReadOnlyList<CaptureWindowInfo> EnumerateWindows()
    {
        var result = new List<CaptureWindowInfo>();
        var ownPid = PInvoke.GetCurrentProcessId();
        var classBuffer = new char[256];
        PInvoke.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (Describe(hwnd, ownPid, classBuffer) is { } info)
                {
                    result.Add(info);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException)
            {
                // A window that vanished mid-enumeration.
            }

            return true;
        }, default);
        return result;
    }

    internal static CaptureWindowInfo? Describe(HWND hwnd, uint ownPid, char[] classBuffer)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd) || IsCloaked(hwnd))
        {
            return null;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        var own = pid == ownPid;
        var exStyle = (WINDOW_EX_STYLE)(nint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (!own)
        {
            if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0)
            {
                return null;
            }

            var length = PInvoke.GetClassName(hwnd, classBuffer.AsSpan());
            if (length > 0 && ShellClasses.Contains(new string(classBuffer, 0, length)))
            {
                return null;
            }

            if ((exStyle & WINDOW_EX_STYLE.WS_EX_LAYERED) != 0 && IsInvisibleLayered(hwnd, exStyle))
            {
                return null;
            }
        }

        var bounds = FrameBounds(hwnd);
        if (bounds.IsEmpty)
        {
            return null;
        }

        var affinity = 0u;
        var isProtected = own && PInvoke.GetWindowDisplayAffinity(hwnd, out affinity) && affinity != 0;
        return new CaptureWindowInfo
        {
            Handle = (nint)hwnd.Value,
            Bounds = bounds,
            Title = Title(hwnd),
            ProcessId = (int)pid,
            Owner = (nint)PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).Value,
            IsOwnProcess = own,
            IsProtected = isProtected,
            Scale = MonitorScale(hwnd),
        };
    }

    internal static PixelRect FrameBounds(HWND hwnd)
    {
        RECT rect;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT)).Succeeded
            && rect.right > rect.left && rect.bottom > rect.top)
        {
            return new PixelRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }

        return PInvoke.GetWindowRect(hwnd, out rect) && rect.right > rect.left && rect.bottom > rect.top
            ? new PixelRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top)
            : default;
    }

    private static bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;
        return PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded && cloaked != 0;
    }

    private static bool IsInvisibleLayered(HWND hwnd, WINDOW_EX_STYLE exStyle)
    {
        byte alpha = 255;
        LAYERED_WINDOW_ATTRIBUTES_FLAGS flags = 0;
        if (!PInvoke.GetLayeredWindowAttributes(hwnd, null, &alpha, &flags))
        {
            // UpdateLayeredWindow windows report nothing; treat click-through ones as invisible.
            return (exStyle & WINDOW_EX_STYLE.WS_EX_TRANSPARENT) != 0;
        }

        return (flags & LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA) != 0 && alpha <= 2;
    }

    private static string Title(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[Math.Min(length + 1, 1024)];
        var copied = PInvoke.GetWindowText(hwnd, buffer.AsSpan());
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    internal static double MonitorScale(HWND hwnd)
    {
        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded && dpi > 0 ? dpi / 96.0 : 1.0;
    }
}

/// <summary>
/// Keeps this app's windows out of captured pixels for the duration of a
/// capture by setting WDA_EXCLUDEFROMCAPTURE on the ones that do not carry it
/// yet, and restores them on dispose. Must be created and disposed on the
/// thread that owns the windows (the UI thread).
/// </summary>
internal sealed unsafe class OwnWindowExclusion : IDisposable
{
    private readonly List<HWND> _changed;

    private OwnWindowExclusion(List<HWND> changed) => _changed = changed;

    /// <summary>True when affinities changed (give DWM a frame to apply them before capturing).</summary>
    public bool Changed => _changed.Count > 0;

    public static OwnWindowExclusion Apply(DisplayCaptureOptions options)
    {
        var targets = new HashSet<nint>(options.ExcludedWindows);
        if (options.HideOwnWindows)
        {
            var pid = PInvoke.GetCurrentProcessId();
            PInvoke.EnumWindows((hwnd, _) =>
            {
                uint owner;
                PInvoke.GetWindowThreadProcessId(hwnd, &owner);
                if (owner == pid && PInvoke.IsWindowVisible(hwnd))
                {
                    targets.Add((nint)hwnd.Value);
                }

                return true;
            }, default);
        }

        var changed = new List<HWND>();
        foreach (var handle in targets)
        {
            var hwnd = new HWND((void*)handle);
            if (!PInvoke.IsWindow(hwnd) || !PInvoke.GetWindowDisplayAffinity(hwnd, out var affinity) || affinity != 0)
            {
                continue;
            }

            if (PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
            {
                changed.Add(hwnd);
            }
        }

        return new OwnWindowExclusion(changed);
    }

    public void Dispose()
    {
        foreach (var hwnd in _changed)
        {
            if (PInvoke.IsWindow(hwnd))
            {
                PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_NONE);
            }
        }

        _changed.Clear();
    }
}
