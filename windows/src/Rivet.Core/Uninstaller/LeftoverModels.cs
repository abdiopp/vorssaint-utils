// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.Cleaner;

namespace Rivet.Core.Maintenance.Uninstaller;

/// <summary>Leftover groups, in display order.</summary>
public enum LeftoverCategory
{
    /// <summary>What is left of the install folder.</summary>
    Application,

    /// <summary>The MSIX package's data folder.</summary>
    Package,

    Support,
    Caches,
    Logs,
    Registry,
    Shortcuts,
    Startup,

    /// <summary>Scheduled tasks and services: listed for review, never removed by the app.</summary>
    Review,
}

public enum LeftoverKind
{
    File,
    Folder,
    RegistryKey,
    StartupValue,
    ScheduledTask,
    Service,
}

/// <summary>A registry key, as hive + path (+ the 32-bit view of HKLM\SOFTWARE).</summary>
public sealed record RegistryKeyRef(string Hive, string Path, bool View32 = false)
{
    public const string CurrentUser = "HKEY_CURRENT_USER";
    public const string LocalMachine = "HKEY_LOCAL_MACHINE";

    public bool IsMachine => Hive == LocalMachine;

    /// <summary>Path as regedit shows it, with WOW6432Node for the 32-bit view.</summary>
    public string FullPath
    {
        get
        {
            var path = View32 && IsMachine && Path.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
                ? @"SOFTWARE\WOW6432Node\" + Path[9..]
                : Path;
            return Hive + "\\" + path;
        }
    }

    public string DisplayPath => (IsMachine ? "HKLM" : "HKCU") + FullPath[Hive.Length..];

    public RegistryKeyRef Child(string name) => this with { Path = Path + "\\" + name };
}

public sealed record ScheduledTaskInfo(string Path, IReadOnlyList<string> Commands);

public sealed record ServiceInfo(string Name, string DisplayName, string ImagePath);

/// <summary>One thing an app left behind, recorded with its identity for the removal-time guard.</summary>
public sealed record LeftoverItem
{
    public required string Id { get; init; }

    public required LeftoverKind Kind { get; init; }

    public required LeftoverCategory Category { get; init; }

    /// <summary>File path, or the registry/task/service path shown in the list.</summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    public string? Detail { get; init; }

    public long Size { get; init; }

    /// <summary>Exact evidence (path, package family, executable name): starts checked. Related finds start unchecked.</summary>
    public bool Exact { get; init; }

    public FileIdentity? Identity { get; init; }

    /// <summary>The scan root the item must still lie under at removal time.</summary>
    public string? Root { get; init; }

    public bool RequiresAdministrator { get; init; }

    public RegistryKeyRef? Registry { get; init; }

    public StartupEntry? Startup { get; init; }

    /// <summary>The token, executable or rule that matched (shown on hover, logged).</summary>
    public string? Evidence { get; init; }

    public bool IsReviewOnly => Kind is LeftoverKind.ScheduledTask or LeftoverKind.Service;
}

public enum UninstallRunStatus
{
    /// <summary>The uninstaller ran and exited (whether it removed the app is checked afterwards).</summary>
    Finished,

    FailedToStart,

    /// <summary>The person stopped waiting.</summary>
    Cancelled,

    /// <summary>Windows refused to remove the package.</summary>
    Failed,
}

public sealed record UninstallRunResult(UninstallRunStatus Status, int? ExitCode = null, string? Message = null);

/// <summary>Windows facilities of the Uninstaller.</summary>
public interface IUninstallerPlatform
{
    bool IsElevated { get; }

    /// <summary>
    /// Runs the app's own uninstaller (or removes the MSIX package) and waits
    /// until it and the copies it spawns have finished. Progress (0…1) is
    /// reported for package removals only.
    /// </summary>
    Task<UninstallRunResult> RunAsync(InstalledApp app, bool quiet, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>The Add/Remove Programs entry or the package is still there.</summary>
    bool IsStillInstalled(InstalledApp app);

    IReadOnlyList<string> SubKeyNames(RegistryKeyRef key);

    bool KeyExists(RegistryKeyRef key);

    /// <summary>The key and everything below it, for the .reg backup; null when it cannot be read.</summary>
    RegKeySnapshot? Snapshot(RegistryKeyRef key);

    bool DeleteKey(RegistryKeyRef key);

    IReadOnlyList<ScheduledTaskInfo> ScheduledTasks();

    IReadOnlyList<ServiceInfo> Services();
}
