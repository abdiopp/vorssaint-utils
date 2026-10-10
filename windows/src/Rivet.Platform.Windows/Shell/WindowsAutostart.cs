// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;

namespace Rivet.Platform.Windows.Shell;

/// <summary>
/// Launch at sign-in through HKCU\...\Run. Windows' Startup apps page can
/// switch the entry off without removing it (StartupApproved\Run, odd first
/// byte); that state is reported as <see cref="AutostartState.DisabledByUser"/>.
/// </summary>
public sealed class WindowsAutostart : IAutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public AutostartState State
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(AppIdentity.Id) is not string)
            {
                return AutostartState.Disabled;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(AppIdentity.Id) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1)
            {
                return AutostartState.DisabledByUser;
            }

            return AutostartState.Enabled;
        }
    }

    public AutostartState SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
                run.SetValue(AppIdentity.Id, $"\"{exe}\" --autostart", RegistryValueKind.String);
            }
            else
            {
                run.DeleteValue(AppIdentity.Id, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            Log.Error("autostart", "Could not change the startup entry.", ex);
        }

        return State;
    }
}
