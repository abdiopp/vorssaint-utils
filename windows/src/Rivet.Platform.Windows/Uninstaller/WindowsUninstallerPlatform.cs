// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Modules;
using WinPackageManager = global::Windows.Management.Deployment.PackageManager;

namespace Rivet.Platform.Windows.Uninstaller;

/// <summary>
/// Runs uninstallers and reads the registry for the leftover scan. The
/// uninstaller starts through the shell (so an uninstaller that needs
/// administrator rights raises its own UAC prompt) with its own windows
/// visible; the app then waits for it and for the copies it starts (Inno
/// Setup and NSIS uninstallers relaunch themselves from %TEMP% and exit).
/// </summary>
public sealed class WindowsUninstallerPlatform(IProcessPlatform processes) : IUninstallerPlatform
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(60);

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public async Task<UninstallRunResult> RunAsync(InstalledApp app, bool quiet, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (app.Kind == InstalledAppKind.Msix)
        {
            return await RemovePackageAsync(app, progress, cancellationToken).ConfigureAwait(false);
        }

        var invocation = ArpRules.Invocation(app, quiet, File.Exists, Environment.ExpandEnvironmentVariables);
        if (invocation is null)
        {
            return new UninstallRunResult(UninstallRunStatus.FailedToStart, Message: L.Get("Strings.uninstallerSelectionUnavailable"));
        }

        var program = invocation.IsMsi ? Path.Combine(Environment.SystemDirectory, "msiexec.exe") : invocation.FileName;
        var started = DateTime.UtcNow.ToFileTimeUtc();
        Process? process;
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = true, Arguments = invocation.Arguments };
            if (Path.GetDirectoryName(program) is { Length: > 0 } folder && Directory.Exists(folder))
            {
                info.WorkingDirectory = folder;
            }

            process = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The UAC prompt of the uninstaller was declined.
            return new UninstallRunResult(UninstallRunStatus.Cancelled);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn("uninstaller", $"Could not start the uninstaller of {app.DisplayName}.", ex);
            return new UninstallRunResult(UninstallRunStatus.FailedToStart, Message: L.Format("win.uninstaller.startFailedFormat", ex.Message));
        }

        try
        {
            int? exitCode = null;
            if (process is not null)
            {
                using (process)
                {
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(MaxWait, cancellationToken).ConfigureAwait(false);
                    exitCode = process.HasExited ? process.ExitCode : null;
                    await WaitForSpawnedAsync(process.Id, started, cancellationToken).ConfigureAwait(false);
                }
            }

            return new UninstallRunResult(UninstallRunStatus.Finished, exitCode);
        }
        catch (OperationCanceledException)
        {
            return new UninstallRunResult(UninstallRunStatus.Cancelled);
        }
        catch (TimeoutException)
        {
            return new UninstallRunResult(UninstallRunStatus.Finished);
        }
    }

    public bool IsStillInstalled(InstalledApp app)
    {
        if (app.Kind == InstalledAppKind.Msix)
        {
            try
            {
                return app.PackageFamilyName is { } family && new WinPackageManager().FindPackagesForUser(string.Empty, family).Any();
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        if (app.RegistryKeyName is not { } name || app.Scope is not { } scope)
        {
            return true;
        }

        var (hive, view) = scope switch
        {
            RegistryScope.Machine64 => (RegistryHive.LocalMachine, RegistryView.Registry64),
            RegistryScope.Machine32 => (RegistryHive.LocalMachine, RegistryView.Registry32),
            _ => (RegistryHive.CurrentUser, RegistryView.Default),
        };
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(UninstallPath + "\\" + name);
            return key is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Unknown counts as installed: leftovers are never offered for an app that may still be there.
            return true;
        }
    }

    public IReadOnlyList<string> SubKeyNames(RegistryKeyRef key)
    {
        try
        {
            using var opened = Open(key, writable: false);
            return opened?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    public bool KeyExists(RegistryKeyRef key)
    {
        try
        {
            using var opened = Open(key, writable: false);
            return opened is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    public RegKeySnapshot? Snapshot(RegistryKeyRef key)
    {
        try
        {
            using var opened = Open(key, writable: false);
            if (opened is null)
            {
                return null;
            }

            var budget = 20_000;
            return Capture(opened, key.FullPath, 0, ref budget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("uninstaller", $"Cannot read {key.DisplayPath} for its backup.", ex);
            return null;
        }
    }

    public bool DeleteKey(RegistryKeyRef key)
    {
        var slash = key.Path.LastIndexOf('\\');
        if (slash <= 0)
        {
            return false;
        }

        try
        {
            using var parent = Open(key with { Path = key.Path[..slash] }, writable: true);
            parent?.DeleteSubKeyTree(key.Path[(slash + 1)..], throwOnMissingSubKey: false);
            return !KeyExists(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Log.Warn("uninstaller", $"Could not delete {key.DisplayPath}.", ex);
            return false;
        }
    }

    public IReadOnlyList<ScheduledTaskInfo> ScheduledTasks()
    {
        var tasks = new List<ScheduledTaskInfo>();
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return tasks;
            }

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            Collect(service.GetFolder("\\"), tasks, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            Log.Debug("uninstaller", $"Scheduled tasks unavailable: {ex.Message}");
        }

        return tasks;
    }

    public IReadOnlyList<ServiceInfo> Services()
    {
        var services = new List<ServiceInfo>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (key is null)
            {
                return services;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var service = key.OpenSubKey(name);
                    // Win32 services only (own or shared process); drivers are never listed.
                    if (service?.GetValue("Type") is not int type || (type & 0x30) == 0 || service.GetValue("ImagePath") is not string image)
                    {
                        continue;
                    }

                    var display = service.GetValue("DisplayName") as string;
                    services.Add(new ServiceInfo(name, string.IsNullOrWhiteSpace(display) || display.StartsWith('@') ? name : display, Environment.ExpandEnvironmentVariables(image)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Debug("uninstaller", $"Services unavailable: {ex.Message}");
        }

        return services;
    }

    private static void Collect(dynamic folder, List<ScheduledTaskInfo> tasks, int depth)
    {
        if (depth > 6 || tasks.Count > 2000)
        {
            return;
        }

        string path = folder.Path;
        // Windows' own tasks live under \Microsoft; they never belong to an app being removed.
        if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var task in folder.GetTasks(1))
        {
            var commands = new List<string>();
            foreach (var action in task.Definition.Actions)
            {
                if ((int)action.Type == 0)
                {
                    string command = action.Path ?? string.Empty;
                    string arguments = action.Arguments ?? string.Empty;
                    commands.Add(arguments.Length > 0 ? $"\"{command}\" {arguments}" : $"\"{command}\"");
                }
            }

            if (commands.Count > 0)
            {
                tasks.Add(new ScheduledTaskInfo((string)task.Path, commands));
            }
        }

        foreach (var child in folder.GetFolders(0))
        {
            Collect(child, tasks, depth + 1);
        }
    }

    private static RegistryKey? Open(RegistryKeyRef key, bool writable)
    {
        var hive = key.IsMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var view = key.IsMachine ? (key.View32 ? RegistryView.Registry32 : RegistryView.Registry64) : RegistryView.Default;
        using var root = RegistryKey.OpenBaseKey(hive, view);
        return root.OpenSubKey(key.Path, writable);
    }

    private static RegKeySnapshot Capture(RegistryKey key, string path, int depth, ref int budget)
    {
        var values = new List<RegValue>();
        foreach (var name in key.GetValueNames())
        {
            if (budget-- <= 0)
            {
                break;
            }

            var kind = key.GetValueKind(name);
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values.Add(new RegValue(name, kind == RegistryValueKind.None ? 0 : (int)kind, data));
        }

        var subKeys = new List<RegKeySnapshot>();
        if (depth < 16)
        {
            foreach (var name in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(name);
                if (child is not null && budget > 0)
                {
                    subKeys.Add(Capture(child, path + "\\" + name, depth + 1, ref budget));
                }
            }
        }

        return new RegKeySnapshot(path, values, subKeys);
    }

    private static async Task<UninstallRunResult> RemovePackageAsync(InstalledApp app, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (app.PackageFullName is not { } fullName)
        {
            return new UninstallRunResult(UninstallRunStatus.FailedToStart);
        }

        try
        {
            var operation = new WinPackageManager().RemovePackageAsync(fullName);
            operation.Progress = (_, value) => progress?.Report(value.percentage / 100.0);
            var removal = operation.AsTask();
            // "I've finished" stops waiting; it never cancels the removal itself.
            var done = await Task.WhenAny(removal, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (done != removal)
            {
                return new UninstallRunResult(UninstallRunStatus.Cancelled);
            }

            var result = await removal.ConfigureAwait(false);
            return result.ExtendedErrorCode is null
                ? new UninstallRunResult(UninstallRunStatus.Finished, 0)
                : new UninstallRunResult(UninstallRunStatus.Failed, result.ExtendedErrorCode.HResult, result.ErrorText);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("uninstaller", $"Removing {fullName} failed.", ex);
            return new UninstallRunResult(UninstallRunStatus.Failed, ex.HResult, ex.Message);
        }
    }

    /// <summary>
    /// Waits until the processes the uninstaller started are gone: its
    /// children and helpers running from %TEMP% that appeared after it started.
    /// </summary>
    private async Task WaitForSpawnedAsync(int launchedPid, long startedFileTime, CancellationToken cancellationToken)
    {
        var temp = Path.GetTempPath();
        var deadline = DateTime.UtcNow + MaxWait;
        var quiet = 0;
        var children = new HashSet<int> { launchedPid };
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = false;
            foreach (var process in processes.Snapshot())
            {
                if (process.CreationTime < startedFileTime || !process.Identity.IsKnown)
                {
                    continue;
                }

                var name = process.ImageName;
                // Children cover msiexec clients started by an EXE wrapper; the Windows
                // Installer service process (started by Windows) is never waited for.
                var spawned = children.Contains(process.ParentPid)
                              || name.Equals("Au_.exe", StringComparison.OrdinalIgnoreCase)
                              || (name.StartsWith("_iu", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                              || (processes.ImagePath(process.Identity) is { } path && path.StartsWith(temp, StringComparison.OrdinalIgnoreCase));
                if (spawned)
                {
                    children.Add(process.Pid);
                    active = true;
                }
            }

            // Two quiet polls in a row end the wait.
            quiet = active ? 0 : quiet + 1;
            if (quiet >= 2)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class UninstallerRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IInstalledAppsProvider, WindowsInstalledApps>();
        services.AddSingleton<IUninstallerPlatform, WindowsUninstallerPlatform>();
    }
}
