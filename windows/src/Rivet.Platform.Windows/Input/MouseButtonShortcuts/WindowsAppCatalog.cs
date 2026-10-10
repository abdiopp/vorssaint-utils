// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Input;
using Rivet.Platform.Windows.Input.SuperKey;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Input.MouseButtonShortcuts;

/// <summary>
/// Apps for the "Add an app…" picker: the processes behind visible, uncloaked
/// top-level windows (packaged apps resolve to their CoreWindow process).
/// Anything else can be added with the picker's file chooser.
/// </summary>
public sealed unsafe class WindowsAppCatalog : IAppCatalog
{
    public IReadOnlyList<AppCatalogEntry> ListApps()
    {
        var windows = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, default);

        var own = (uint)Environment.ProcessId;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            if (!IsAppWindow(window))
            {
                continue;
            }

            uint pid;
            PInvoke.GetWindowThreadProcessId(window, &pid);
            if (pid == own || WindowsRunningApps.ProcessPath(pid) is not { } path)
            {
                continue;
            }

            if (path.EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var child = PInvoke.FindWindowEx(window, HWND.Null, "Windows.UI.Core.CoreWindow", null);
                uint childPid = 0;
                if (!child.IsNull)
                {
                    PInvoke.GetWindowThreadProcessId(child, &childPid);
                }

                if (childPid == 0 || WindowsRunningApps.ProcessPath(childPid) is not { } inner)
                {
                    continue;
                }

                path = inner;
            }

            paths.Add(path);
        }

        return paths.Select(p => new AppCatalogEntry(DisplayName(p), p)).ToList();
    }

    private static bool IsAppWindow(HWND window)
    {
        if (!PInvoke.IsWindowVisible(window) || PInvoke.GetWindowTextLength(window) == 0)
        {
            return false;
        }

        var exStyle = (WINDOW_EX_STYLE)(nint)PInvoke.GetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        int cloaked = 0;
        return PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int)).Failed || cloaked == 0;
    }

    private static string DisplayName(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }
        }
        catch (Exception)
        {
        }

        return Path.GetFileNameWithoutExtension(path);
    }
}
