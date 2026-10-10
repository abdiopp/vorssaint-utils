// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>Scan categories. Raw values are persisted identities shared with macOS (append only).</summary>
public enum CleanerCategory
{
    Leftovers = 0,
    LoginItems = 1,
    Caches = 2,
    Logs = 3,
    Developer = 4,
    Trash = 5,
    DeviceBackups = 6,
    Screenshots = 7,
}

/// <summary>How results are grouped on screen: a "Safe cleanup" section, then "Optional, review first".</summary>
public enum CleanerGroup
{
    LoginItems,
    SafeCaches,
    Logs,
    Developer,
    Leftovers,
    OtherCaches,
    DeviceBackups,
    Screenshots,
    RecycleBin,
}

public enum CleanerItemKind
{
    File,
    Folder,

    /// <summary>The Recycle Bin as one row (emptying it is permanent).</summary>
    RecycleBin,

    /// <summary>A Run/RunOnce registry value (backed up to a .reg file, then deleted).</summary>
    StartupValue,
}

/// <summary>One removable item, recorded with its identity at scan time.</summary>
public sealed record CleanerItem
{
    /// <summary>Unique within a scan: the path, or a fixed id for special rows.</summary>
    public required string Id { get; init; }

    public required CleanerCategory Category { get; init; }

    public required CleanerItemKind Kind { get; init; }

    /// <summary>File system path (or the registry value's display path).</summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>Second line (browser and profile, owner, device name, creation date…).</summary>
    public string? Detail { get; init; }

    public long Size { get; init; }

    /// <summary>Strong evidence and cheap to rebuild: starts checked.</summary>
    public bool Recommended { get; init; }

    /// <summary>Removed permanently (only the Recycle Bin row).</summary>
    public bool Permanent { get; init; }

    public bool RequiresAdministrator { get; init; }

    public FileIdentity? Identity { get; init; }

    /// <summary>The category folder the item must still be a direct child of at removal time.</summary>
    public string? Root { get; init; }

    public StartupEntry? Startup { get; init; }

    /// <summary>Evidence re-checked at removal (a package family name, the per-user install folder).</summary>
    public string? Evidence { get; init; }

    public CleanerGroup Group => CleanerGroups.For(this);
}

/// <summary>Display metadata for each group (string keys and Fluent icon names).</summary>
public static class CleanerGroups
{
    public static readonly IReadOnlyList<CleanerGroup> Safe = [CleanerGroup.LoginItems, CleanerGroup.SafeCaches, CleanerGroup.Logs, CleanerGroup.Developer];

    public static readonly IReadOnlyList<CleanerGroup> Optional =
        [CleanerGroup.Leftovers, CleanerGroup.OtherCaches, CleanerGroup.DeviceBackups, CleanerGroup.Screenshots, CleanerGroup.RecycleBin];

    public static CleanerGroup For(CleanerItem item) => item.Category switch
    {
        CleanerCategory.LoginItems => CleanerGroup.LoginItems,
        CleanerCategory.Caches or CleanerCategory.Developer when !item.Recommended => CleanerGroup.OtherCaches,
        CleanerCategory.Caches => CleanerGroup.SafeCaches,
        CleanerCategory.Logs => CleanerGroup.Logs,
        CleanerCategory.Developer => CleanerGroup.Developer,
        CleanerCategory.Leftovers => CleanerGroup.Leftovers,
        CleanerCategory.DeviceBackups => CleanerGroup.DeviceBackups,
        CleanerCategory.Screenshots => CleanerGroup.Screenshots,
        _ => CleanerGroup.RecycleBin,
    };

    public static bool IsSafe(CleanerGroup group) => Safe.Contains(group);

    public static string TitleKey(CleanerGroup group) => group switch
    {
        CleanerGroup.LoginItems => "Strings.cleanerCatLoginItems",
        CleanerGroup.SafeCaches => "Strings.cleanerCatCaches",
        CleanerGroup.Logs => "Strings.cleanerCatLogs",
        CleanerGroup.Developer => "Strings.cleanerCatDeveloper",
        CleanerGroup.Leftovers => "Strings.cleanerCatLeftovers",
        CleanerGroup.OtherCaches => "Strings.cleanerCatOtherCaches",
        CleanerGroup.DeviceBackups => "Strings.cleanerCatDeviceBackups",
        CleanerGroup.Screenshots => "Strings.cleanerCatScreenshots",
        _ => "Strings.cleanerCatTrash",
    };

    public static string CaptionKey(CleanerGroup group) => group switch
    {
        CleanerGroup.LoginItems => "Strings.cleanerLoginItemsCaption",
        CleanerGroup.SafeCaches => "Strings.cleanerCachesCaption",
        CleanerGroup.Logs => "Strings.cleanerLogsCaption",
        CleanerGroup.Developer => "Strings.cleanerDeveloperCaption",
        CleanerGroup.Leftovers => "Strings.cleanerLeftoversCaption",
        CleanerGroup.OtherCaches => "Strings.cleanerOtherCachesCaption",
        CleanerGroup.DeviceBackups => "Strings.cleanerDeviceBackupsCaption",
        CleanerGroup.Screenshots => "Strings.cleanerScreenshotsCaptionFormat",
        _ => "Strings.cleanerTrashNote",
    };

    public static string Icon(CleanerGroup group) => group switch
    {
        CleanerGroup.LoginItems => "Power",
        CleanerGroup.SafeCaches => "Archive",
        CleanerGroup.Logs => "DocumentError",
        CleanerGroup.Developer => "WrenchScrewdriver",
        CleanerGroup.Leftovers => "PuzzlePiece",
        CleanerGroup.OtherCaches => "HardDrive",
        CleanerGroup.DeviceBackups => "Phone",
        CleanerGroup.Screenshots => "Camera",
        _ => "Delete",
    };
}

/// <summary>What a clean did.</summary>
public sealed record CleanResult
{
    /// <summary>Bytes confirmed gone (recycled, or emptied from the Recycle Bin).</summary>
    public long FreedBytes { get; init; }

    public int Removed { get; init; }

    public int Failed { get; init; }

    /// <summary>Items another program was using: left in place, not failures.</summary>
    public int InUse { get; init; }

    /// <summary>Items Windows removed without recycling despite the checks (reported, never hidden).</summary>
    public int DeletedPermanently { get; init; }

    public IReadOnlyList<string> FailedNames { get; init; } = [];

    /// <summary>Folder holding .reg backups of removed startup values, when any were removed.</summary>
    public string? BackupFolder { get; init; }
}
