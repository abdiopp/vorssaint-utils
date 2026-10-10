// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Modules;

namespace Rivet.Platform.Windows.Processes;

/// <summary>
/// "Restart as administrator": starts an elevated copy through UAC with
/// <c>--relaunch-after &lt;pid&gt;</c> (it waits for this copy to exit before
/// taking the single-instance lock), then quits. Offered only where a feature
/// truly needs it (system folders in the Cleaner, machine-wide registry
/// leftovers in the Uninstaller).
/// </summary>
public sealed class WindowsElevation : IElevationService
{
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public bool RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--relaunch-after " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The person declined the UAC prompt: keep running as is.
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn("elevation", "Could not start an elevated copy.", ex);
            return false;
        }

        AppLifetime.RequestShutdown();
        return true;
    }
}

/// <summary>Process services and the external command runner for Windows.</summary>
public sealed class ProcessesRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<IProcessPlatform, WindowsProcessPlatform>();
        services.AddSingleton<IPortTablePlatform, WindowsPortTable>();
        services.AddSingleton<IElevationService, WindowsElevation>();
    }
}
