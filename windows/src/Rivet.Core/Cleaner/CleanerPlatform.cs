// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>The folders the Cleaner and the Uninstaller work in (Windows known folders).</summary>
public sealed record CleanerFolders
{
    /// <summary>%TEMP% of the user.</summary>
    public required string Temp { get; init; }

    public required string LocalAppData { get; init; }

    public required string RoamingAppData { get; init; }

    /// <summary>%USERPROFILE%\AppData\LocalLow.</summary>
    public required string LocalLow { get; init; }

    public required string UserProfile { get; init; }

    public required string ProgramData { get; init; }

    /// <summary>%WINDIR%.</summary>
    public required string Windows { get; init; }

    public required string ProgramFiles { get; init; }

    public string? ProgramFilesX86 { get; init; }

    /// <summary>The user's Start Menu\Programs.</summary>
    public required string StartMenuPrograms { get; init; }

    /// <summary>All users' Start Menu\Programs.</summary>
    public required string CommonStartMenuPrograms { get; init; }

    /// <summary>shell:startup.</summary>
    public required string Startup { get; init; }

    /// <summary>shell:common startup.</summary>
    public required string CommonStartup { get; init; }

    public required string Desktop { get; init; }

    public string? CommonDesktop { get; init; }

    public required string Documents { get; init; }

    public required string Downloads { get; init; }

    public required string Pictures { get; init; }

    public string? Music { get; init; }

    public string? Videos { get; init; }

    /// <summary>FOLDERID_Screenshots (Pictures\Screenshots).</summary>
    public required string Screenshots { get; init; }

    public string? OneDrive { get; init; }

    /// <summary>This app's own data folders: never scanned, never removable.</summary>
    public string? OwnLocal { get; init; }

    public string? OwnRoaming { get; init; }

    /// <summary>The folders under which leftovers may be claimed for an app (Program Files, %LOCALAPPDATA%\Programs).</summary>
    public IEnumerable<string> TrustedInstallRoots()
    {
        yield return ProgramFiles;
        if (ProgramFilesX86 is { } x86)
        {
            yield return x86;
        }

        yield return Path.Combine(LocalAppData, "Programs");
    }
}

public interface IKnownFolders
{
    CleanerFolders Folders { get; }
}

/// <summary>What kind of drive a path is on (recycling and "missing target" evidence depend on it).</summary>
public enum DriveKind
{
    Fixed,
    Removable,
    Network,
    Optical,

    /// <summary>No such drive (unplugged or unmapped): a target there is never proven missing.</summary>
    Unavailable,
    Unknown,
}

public sealed record RecycleBinInfo(long Items, long Bytes);

/// <summary>A startup command in the registry (Run/RunOnce).</summary>
public sealed record StartupEntry
{
    /// <summary>"HKEY_CURRENT_USER" or "HKEY_LOCAL_MACHINE".</summary>
    public required string Hive { get; init; }

    /// <summary>Key below the hive, e.g. Software\Microsoft\Windows\CurrentVersion\Run.</summary>
    public required string KeyPath { get; init; }

    public required string ValueName { get; init; }

    public required string Command { get; init; }

    /// <summary>The 32-bit view (WOW6432Node) of a machine key.</summary>
    public bool Is32BitView { get; init; }

    public bool IsMachine => Hive == "HKEY_LOCAL_MACHINE";

    public string DisplayPath => $@"{(IsMachine ? "HKLM" : "HKCU")}\{KeyPath}\{ValueName}";
}

/// <summary>Windows facilities the Cleaner needs beyond plain files.</summary>
public interface ICleanerPlatform
{
    bool IsElevated { get; }

    /// <summary>Size and item count of the Recycle Bin on every drive; null when Windows does not say.</summary>
    RecycleBinInfo? QueryRecycleBin();

    /// <summary>Empties every Recycle Bin without confirmation, progress UI or sound (permanent).</summary>
    bool EmptyRecycleBin();

    /// <summary>Lower-case image names of running processes ("msedge.exe").</summary>
    IReadOnlySet<string> RunningProcessNames();

    /// <summary>
    /// Full paths of running processes (lower case); used to skip caches of running
    /// IDEs. Empty when unknown.
    /// </summary>
    IReadOnlyList<string> RunningProcessPaths();

    /// <summary>
    /// Whether a package family is installed for the current user. Null when
    /// Windows cannot tell: the caller treats "unknown" as installed.
    /// </summary>
    bool? IsPackageFamilyInstalled(string familyName);

    /// <summary>Install folders named by Add/Remove Programs entries (all scopes).</summary>
    IReadOnlyList<string> InstalledProgramLocations();

    /// <summary>Run/RunOnce values of the user, plus the machine's when <paramref name="includeMachine"/>.</summary>
    IReadOnlyList<StartupEntry> StartupEntries(bool includeMachine);

    /// <summary>
    /// Saves the value to a .reg file in <paramref name="backupFolder"/>, then
    /// deletes it if it still holds the same command. False (and nothing
    /// deleted) when the backup could not be written.
    /// </summary>
    bool RemoveStartupEntry(StartupEntry entry, string backupFolder);

    DriveKind DriveKindOf(string path);
}
