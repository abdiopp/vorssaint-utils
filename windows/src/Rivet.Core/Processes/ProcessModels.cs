// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Maintenance.Processes;

/// <summary>
/// One process instance: its PID plus its creation time (FILETIME, 100 ns
/// ticks since 1601 UTC). Windows reuses PIDs, so every kill re-checks the
/// creation time right before acting (the macOS "start time" guard).
/// </summary>
public readonly record struct ProcessIdentity(int Pid, long CreationTime)
{
    /// <summary>Both parts are known; rows without a known identity are never killable.</summary>
    public bool IsKnown => Pid > 0 && CreationTime > 0;
}

/// <summary>A process as the platform snapshot reports it (no extra handles opened).</summary>
public sealed record RawProcess
{
    public required int Pid { get; init; }

    public int ParentPid { get; init; }

    /// <summary>Image file name, e.g. "chrome.exe" (or "System", "Registry").</summary>
    public required string ImageName { get; init; }

    /// <summary>Creation time as FILETIME (UTC); 0 when unknown (System, Idle).</summary>
    public long CreationTime { get; init; }

    /// <summary>Kernel plus user time in 100 ns units.</summary>
    public long CpuTime { get; init; }

    public long WorkingSetBytes { get; init; }

    public long PrivateBytes { get; init; }

    public int SessionId { get; init; }

    public ProcessIdentity Identity => new(Pid, CreationTime);
}

/// <summary>Version-resource facts about an executable.</summary>
public sealed record ExecutableDescription(string? FileDescription, string? CompanyName, string? ProductName);

/// <summary>What happened to one kill target.</summary>
public enum KillOutcome
{
    /// <summary>The signal was delivered (window close posted, or the process was terminated).</summary>
    Done,

    /// <summary>The process no longer exists (counts as removed).</summary>
    AlreadyGone,

    /// <summary>Windows refused (elevated or another user's process): needs administrator.</summary>
    AccessDenied,

    /// <summary>The PID now belongs to a different process: nothing was done.</summary>
    IdentityChanged,

    /// <summary>The process has no window to close.</summary>
    NoWindows,

    Failed,
}

public enum KillMode
{
    /// <summary>Close the app's windows (graceful); a process without windows is terminated.</summary>
    Kill,

    /// <summary>TerminateProcess.</summary>
    ForceKill,

    /// <summary>Kill every listed, unprotected process with the same display name.</summary>
    KillAll,

    /// <summary>Terminate the process and all its descendants, deepest first.</summary>
    KillTree,

    /// <summary>Close the app, wait for it to exit (10 s), start it again.</summary>
    Restart,
}

/// <summary>A row of the Kill Process list (a process, or a group of related processes).</summary>
public sealed record ProcessRow
{
    public required ProcessIdentity Identity { get; init; }

    public int ParentPid { get; init; }

    /// <summary>Display name: the version resource's description, else the image name.</summary>
    public required string Name { get; init; }

    public required string ImageName { get; init; }

    public string? Path { get; init; }

    public string? User { get; init; }

    /// <summary>Share of the whole machine (Task Manager style); null on the first sample.</summary>
    public double? CpuPercent { get; init; }

    public long MemoryBytes { get; init; }

    /// <summary>Processes folded into this row (1 for a plain process).</summary>
    public int MemberCount { get; init; } = 1;

    public IReadOnlyList<ProcessIdentity> Members { get; init; } = [];

    public bool IsProtected { get; init; }

    /// <summary>Has visible top-level windows: a regular app that can be closed politely and restarted.</summary>
    public bool HasWindows { get; init; }

    public int Pid => Identity.Pid;

    public bool CanRestart => HasWindows && !IsProtected && Identity.IsKnown && !string.IsNullOrEmpty(Path);
}

/// <summary>The result of one kill action.</summary>
public sealed record KillReport
{
    public required KillMode Mode { get; init; }

    public required string TargetName { get; init; }

    /// <summary>Targets that were removed or were already gone.</summary>
    public int Removed { get; init; }

    /// <summary>Targets Windows refused: an elevated retry may end them.</summary>
    public IReadOnlyList<ProcessIdentity> NeedsAdministrator { get; init; } = [];

    public int Failed { get; init; }

    /// <summary>Restart only: the app quit but could not be started again.</summary>
    public bool RelaunchFailed { get; init; }

    public bool Succeeded => Failed == 0 && NeedsAdministrator.Count == 0 && !RelaunchFailed;
}

/// <summary>Sort columns of the Kill Process list (persisted as strings).</summary>
public enum ProcessSortColumn
{
    Cpu,
    Memory,
    Name,
    Pid,
}
