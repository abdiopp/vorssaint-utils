// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;

namespace Rivet.Platform.Windows.Shell;

/// <summary>
/// Starts a new copy with <c>--relaunch-after &lt;pid&gt;</c> (it waits for this
/// process to exit before taking the single-instance lock), then shuts down.
/// </summary>
public sealed class WindowsRelauncher : IRelauncher
{
    public void RelaunchAndExit(IReadOnlyList<string>? args = null)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("--relaunch-after");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var arg in args ?? [])
            {
                info.ArgumentList.Add(arg);
            }

            Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("relaunch", "Could not start the new copy; not quitting.", ex);
            return;
        }

        AppLifetime.RequestShutdown();
    }
}
