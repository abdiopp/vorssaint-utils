// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Modules.Scratchpad;
using Rivet.Core.Platform;
using Rivet.Platform.Windows.CleaningMode;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Scratchpad;

/// <summary>Recognizes the on-screen keyboard (osk.exe) and the touch keyboard under the pointer.</summary>
public sealed unsafe class WindowsScratchpadPlatform : IScratchpadPlatform
{
    private static readonly string[] KeyboardClasses = ["OSKMainClass", "IPTip_Main_Window"];
    private static readonly string[] KeyboardProcesses = ["osk.exe", "TabTip.exe", "TextInputHost.exe"];

    public bool IsOnScreenKeyboardAt(PixelPoint point)
    {
        var hwnd = PInvoke.WindowFromPoint(new System.Drawing.Point(point.X, point.Y));
        if (hwnd.IsNull)
        {
            return false;
        }

        var root = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        var target = root.IsNull ? hwnd : root;
        var className = ClassName(target);
        if (KeyboardClasses.Any(c => string.Equals(c, className, StringComparison.Ordinal)))
        {
            return true;
        }

        var path = Win32Windows.ProcessPathOf((nint)target.Value);
        var exe = path is null ? null : Path.GetFileName(path);
        return exe is not null && KeyboardProcesses.Any(p => string.Equals(p, exe, StringComparison.OrdinalIgnoreCase));
    }

    internal static string ClassName(HWND hwnd)
    {
        var buffer = stackalloc char[256];
        var length = PInvoke.GetClassName(hwnd, new PWSTR(buffer), 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}

public sealed class ScratchpadWindowsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) => services.AddSingleton<IScratchpadPlatform, WindowsScratchpadPlatform>();
}
