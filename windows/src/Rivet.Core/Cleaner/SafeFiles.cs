// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>
/// A file's identity: volume serial number plus its 128-bit file id
/// (Windows <c>FILE_ID_INFO</c>), read without following reparse points.
/// Recorded at scan time and compared before removal, so a path swapped
/// between review and removal is refused.
/// </summary>
public readonly record struct FileIdentity(ulong Volume, ulong IdHigh, ulong IdLow);

[Flags]
public enum EntryFlags
{
    None = 0,
    Directory = 1,
    Hidden = 2,
    System = 4,

    /// <summary>Symbolic link, junction, mount point, app execution alias: never followed, never removed through.</summary>
    ReparsePoint = 8,

    /// <summary>OneDrive Files On-Demand and similar: reading would download it, so it is never offered.</summary>
    CloudPlaceholder = 16,

    ReadOnly = 32,
}

/// <summary>A directory entry as seen without following links.</summary>
public sealed record FsEntry(string Path, string Name, EntryFlags Flags, long Length, DateTime LastWriteUtc, DateTime CreationUtc)
{
    public bool IsDirectory => Flags.HasFlag(EntryFlags.Directory);

    public bool IsReparsePoint => Flags.HasFlag(EntryFlags.ReparsePoint);

    public bool IsHidden => Flags.HasFlag(EntryFlags.Hidden);

    public bool IsCloudPlaceholder => Flags.HasFlag(EntryFlags.CloudPlaceholder);

    /// <summary>Newest of the creation and last write times (copies keep their write time).</summary>
    public DateTime TouchedUtc => LastWriteUtc > CreationUtc ? LastWriteUtc : CreationUtc;
}

/// <summary>Result of a recursive size walk.</summary>
public readonly record struct TreeMeasure(long Bytes, long Files, DateTime NewestWriteUtc, bool ContainsReparsePoints);

/// <summary>
/// The file system as the Cleaner and the Uninstaller see it. Implementations
/// never follow reparse points, never read cloud placeholders and never throw
/// for unreadable or missing paths.
/// </summary>
public interface ICleanerFileSystem
{
    /// <summary>Direct children of a folder; empty when it is missing or unreadable.</summary>
    IReadOnlyList<FsEntry> List(string folder);

    /// <summary>The entry itself (lstat semantics), or null when it does not exist.</summary>
    FsEntry? Stat(string path);

    FileIdentity? Identity(string path);

    /// <summary>
    /// Allocated size of a file or of a folder tree, with the newest write
    /// time inside. Reparse points count as nothing and are never descended;
    /// cloud placeholders count only what is stored locally.
    /// </summary>
    TreeMeasure Measure(string path, CancellationToken cancellationToken);

    /// <summary>Another process holds the file open without sharing deletion (Explorer's thumbnail cache, a running log).</summary>
    bool IsInUse(string path);

    /// <summary>Reads a small text file (≤ <paramref name="maxBytes"/>); null when missing, too large or unreadable.</summary>
    string? ReadText(string path, int maxBytes);

    /// <summary>Reads a small binary file; null when missing, too large or unreadable.</summary>
    byte[]? ReadBytes(string path, int maxBytes);
}

public enum RecycleStatus
{
    /// <summary>The item is in the Recycle Bin and can be restored from there.</summary>
    Recycled,

    /// <summary>Windows removed it without recycling (should not happen after the pre-checks; reported honestly).</summary>
    DeletedPermanently,

    /// <summary>Another program uses it: left in place, not counted as a failure.</summary>
    InUse,

    /// <summary>It cannot go to the Recycle Bin (removable or network drive, bin off, too large): left in place.</summary>
    Refused,

    /// <summary>The item no longer exists (counts as removed only when absence is confirmed).</summary>
    Missing,

    Failed,
}

public sealed record RecycleOutcome(string Path, RecycleStatus Status, string? Detail = null);

/// <summary>
/// Moves items to the Recycle Bin, verifying each one: Windows silently
/// deletes items that cannot be recycled, so the implementation first checks
/// the drive has a Recycle Bin with room for the item and refuses otherwise,
/// then confirms through the shell that a recycled copy was created.
/// </summary>
public interface IRecycler
{
    /// <param name="paths">Absolute paths, already checked by the caller's guard.</param>
    /// <param name="allowElevationPrompt">
    /// A person is present and the items need administrator rights (Program
    /// Files residue): the shell may show its own UAC prompt for the batch.
    /// </param>
    IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths, bool allowElevationPrompt);
}

/// <summary>
/// Path helpers shared by the Cleaner and the Uninstaller guards. They treat
/// '\' and '/' alike and normalize Windows-style paths themselves when not
/// running on Windows, so the rules are unit tested on any OS.
/// </summary>
public static class SafePaths
{
    public static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>"C:\…", "C:/…" or "\\server\share…".</summary>
    public static bool IsWindowsStyle(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]))
        || (path.Length >= 3 && path[0] == '\\' && path[1] == '\\');

    /// <summary>A fully qualified path (never relative to a current folder or drive).</summary>
    public static bool IsAbsolute(string path) =>
        IsWindowsStyle(path) || (!OperatingSystem.IsWindows() && path.StartsWith('/')) || (OperatingSystem.IsWindows() && System.IO.Path.IsPathFullyQualified(path));

    /// <summary>Normalized absolute path without a trailing separator (drive roots keep theirs).</summary>
    public static string Normalize(string path)
    {
        if (IsWindowsStyle(path) && !OperatingSystem.IsWindows())
        {
            return NormalizeWindowsStyle(path);
        }

        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    public static bool Equal(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> lies inside <paramref name="folder"/> (not equal to it).</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return p.Length > f.Length
               && p.StartsWith(f, StringComparison.OrdinalIgnoreCase)
               && (IsSeparator(p[f.Length]) || IsSeparator(f[^1]));
    }

    /// <summary>The parent folder, or null for a root.</summary>
    public static string? Parent(string path)
    {
        var full = Normalize(path);
        var index = full.LastIndexOfAny(['\\', '/']);
        if (index < 0)
        {
            return null;
        }

        var parent = full[..index];
        var root = Root(full);
        if (parent.Length < root.Length)
        {
            return full.Length > root.Length ? root : null;
        }

        return parent.Length == 0 ? null : parent;
    }

    /// <summary>
    /// Appends a relative path written with backslashes ("Google\Chrome\User Data")
    /// segment by segment, so rule tables read like Windows paths on every OS.
    /// </summary>
    public static string Join(string root, string relative) =>
        System.IO.Path.Combine([root, .. relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>The last component.</summary>
    public static string FileName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    public static bool IsDirectChild(string path, string folder)
    {
        var parent = Parent(path);
        return parent is not null && Equal(parent, folder);
    }

    /// <summary>Number of path components, the root counting as one ("C:\Users\a" → 3).</summary>
    public static int ComponentCount(string path)
    {
        var full = Normalize(path);
        var root = Root(full);
        var rest = full[root.Length..];
        return 1 + rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>A drive root or UNC share root.</summary>
    public static bool IsRoot(string path)
    {
        var full = Normalize(path);
        return string.Equals(Root(full).TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string Root(string full)
    {
        if (IsWindowsStyle(full))
        {
            if (full[1] == ':')
            {
                return full[..3];
            }

            // \\server\share\
            var share = full.IndexOfAny(['\\', '/'], 2);
            share = share < 0 ? -1 : full.IndexOfAny(['\\', '/'], share + 1);
            return share < 0 ? full : full[..(share + 1)];
        }

        return System.IO.Path.GetPathRoot(full) ?? string.Empty;
    }

    private static string NormalizeWindowsStyle(string path)
    {
        var text = path.Replace('/', '\\');
        var unc = text.StartsWith(@"\\", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (var part in text.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > (unc ? 2 : 1))
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            if (part != "..")
            {
                parts.Add(part);
            }
        }

        if (unc)
        {
            return @"\\" + string.Join('\\', parts);
        }

        return parts.Count == 1 ? parts[0] + "\\" : string.Join('\\', parts);
    }

    /// <summary>
    /// Every component from <paramref name="path"/> up to (not including)
    /// <paramref name="root"/> exists and is not a reparse point.
    /// </summary>
    public static bool ChainIsPlain(ICleanerFileSystem fs, string path, string root)
    {
        var current = Normalize(path);
        var stop = Normalize(root);
        var guard = 0;
        while (!string.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
        {
            if (guard++ > 64 || !IsInside(current, stop))
            {
                return false;
            }

            var entry = fs.Stat(current);
            if (entry is null || entry.IsReparsePoint)
            {
                return false;
            }

            current = Parent(current) ?? stop;
        }

        return true;
    }

    /// <summary>Shows the profile folder as "~" like the macOS list (only for display).</summary>
    public static string Display(string path, string? profile)
    {
        if (profile is { Length: > 0 } && (Equal(path, profile) || IsInside(path, profile)))
        {
            return "~" + Normalize(path)[Normalize(profile).Length..];
        }

        return path;
    }
}

/// <summary>
/// Folders that are never removable whatever a scan says (spec §3.1.9
/// critical set): drive roots, Windows, Program Files, ProgramData, the
/// profile and AppData roots, known folders, the Packages root and the MSI
/// package cache.
/// </summary>
public sealed class CriticalPaths
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public CriticalPaths(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _paths.Add(SafePaths.Normalize(path));
            }
        }
    }

    public static CriticalPaths For(CleanerFolders folders) => new(
    [
        folders.Windows, Combine(folders.Windows, "System32"), Combine(folders.Windows, "SysWOW64"), Combine(folders.Windows, "Temp"),
        folders.ProgramFiles, folders.ProgramFilesX86, folders.ProgramData, Combine(folders.ProgramData, "Package Cache"),
        Combine(folders.ProgramData, "Microsoft"), Combine(folders.ProgramData, "Microsoft", "Windows"),
        folders.UserProfile, folders.RoamingAppData, folders.LocalAppData, folders.LocalLow, Combine(folders.UserProfile, "AppData"),
        Combine(folders.LocalAppData, "Packages"), Combine(folders.LocalAppData, "Programs"), Combine(folders.LocalAppData, "Microsoft"),
        Combine(folders.LocalAppData, "Microsoft", "Windows"), Combine(folders.RoamingAppData, "Microsoft"),
        Combine(folders.RoamingAppData, "Microsoft", "Windows"), Combine(folders.RoamingAppData, "Microsoft", "Windows", "Start Menu"),
        folders.Temp, folders.Desktop, folders.Documents, folders.Downloads, folders.Pictures, folders.Music, folders.Videos,
        folders.OneDrive, folders.StartMenuPrograms, folders.CommonStartMenuPrograms, folders.Startup, folders.CommonStartup,
        folders.Screenshots, folders.OwnLocal, folders.OwnRoaming,
    ]);

    public bool Contains(string path)
    {
        var normalized = SafePaths.Normalize(path);
        if (_paths.Contains(normalized))
        {
            return true;
        }

        // Every drive root and every UNC share root is critical.
        return SafePaths.IsRoot(normalized);
    }

    private static string? Combine(string? first, params string[] rest) =>
        string.IsNullOrEmpty(first) ? null : System.IO.Path.Combine([first, .. rest]);
}

/// <summary>
/// <see cref="ICleanerFileSystem"/> over System.IO. Used on platforms without
/// the native implementation (tests on temp folders); sizes are logical
/// lengths there, since allocation sizes need Windows APIs.
/// </summary>
public sealed class ManagedFileSystem : ICleanerFileSystem
{
    private const int CloudAttributes = 0x00400000 | 0x00040000; // RECALL_ON_DATA_ACCESS | RECALL_ON_OPEN

    public IReadOnlyList<FsEntry> List(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            if (!directory.Exists)
            {
                return [];
            }

            return directory.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 })
                .Select(ToEntry)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    public FsEntry? Stat(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null)
            {
                return null;
            }

            return ToEntry(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public FileIdentity? Identity(string path)
    {
        var entry = Stat(path);
        if (entry is null)
        {
            return null;
        }

        // Portable stand-in: creation time and length identify a test file well enough.
        var hash = (ulong)StringComparer.OrdinalIgnoreCase.GetHashCode(SafePaths.Normalize(path));
        return new FileIdentity(hash, (ulong)entry.CreationUtc.Ticks, entry.IsDirectory ? 0UL : (ulong)entry.Length);
    }

    public TreeMeasure Measure(string path, CancellationToken cancellationToken)
    {
        var root = Stat(path);
        if (root is null || root.IsReparsePoint)
        {
            return new TreeMeasure(0, 0, root?.LastWriteUtc ?? DateTime.MinValue, root?.IsReparsePoint ?? false);
        }

        if (!root.IsDirectory)
        {
            return new TreeMeasure(root.IsCloudPlaceholder ? 0 : root.Length, 1, root.LastWriteUtc, false);
        }

        long bytes = 0;
        long files = 0;
        var newest = root.LastWriteUtc;
        var links = false;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in List(stack.Pop()))
            {
                if (entry.LastWriteUtc > newest)
                {
                    newest = entry.LastWriteUtc;
                }

                if (entry.IsReparsePoint)
                {
                    links = true;
                    continue;
                }

                if (entry.IsDirectory)
                {
                    stack.Push(entry.Path);
                }
                else
                {
                    files++;
                    bytes += entry.IsCloudPlaceholder ? 0 : entry.Length;
                }
            }
        }

        return new TreeMeasure(bytes, files, newest, links);
    }

    public bool IsInUse(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException)
        {
            return File.Exists(path);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string? ReadText(string path, int maxBytes) =>
        ReadBytes(path, maxBytes) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public byte[]? ReadBytes(string path, int maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes || info.LinkTarget is not null)
            {
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static FsEntry ToEntry(FileSystemInfo info)
    {
        var attributes = info.Attributes;
        var flags = EntryFlags.None;
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            flags |= EntryFlags.Directory;
        }

        if (attributes.HasFlag(FileAttributes.Hidden) || info.Name.StartsWith('.'))
        {
            flags |= EntryFlags.Hidden;
        }

        if (attributes.HasFlag(FileAttributes.System))
        {
            flags |= EntryFlags.System;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            flags |= EntryFlags.ReparsePoint;
        }

        if (((int)attributes & CloudAttributes) != 0 || attributes.HasFlag(FileAttributes.Offline))
        {
            flags |= EntryFlags.CloudPlaceholder;
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            flags |= EntryFlags.ReadOnly;
        }

        var length = info is FileInfo file && !flags.HasFlag(EntryFlags.Directory) ? SafeLength(file) : 0;
        return new FsEntry(info.FullName, info.Name, flags, length, SafeTime(() => info.LastWriteTimeUtc), SafeTime(() => info.CreationTimeUtc));
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTime SafeTime(Func<DateTime> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }
}
