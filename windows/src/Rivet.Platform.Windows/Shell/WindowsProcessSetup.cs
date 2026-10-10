// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Windows.Win32;

namespace Rivet.Platform.Windows.Shell;

/// <summary>Process-wide Windows setup that must happen before any window or toast exists.</summary>
public static class WindowsProcessSetup
{
    /// <summary>
    /// Ties the process to its AppUserModelID so toasts, taskbar grouping and the
    /// Start menu shortcut (created by the installer with the same id) agree.
    /// </summary>
    public static void SetAppUserModelId()
    {
        var result = PInvoke.SetCurrentProcessExplicitAppUserModelID(AppIdentity.AppUserModelId);
        if (result.Failed)
        {
            Log.Warn("startup", $"SetCurrentProcessExplicitAppUserModelID failed: 0x{result.Value:X8}");
        }
    }

    /// <summary>A GUI-subsystem exe has no console; attach to the parent's so <c>--selftest</c> output shows in CI.</summary>
    public static void AttachParentConsole()
    {
        const uint attachParentProcess = unchecked((uint)-1);
        PInvoke.AttachConsole(attachParentProcess);
    }
}
