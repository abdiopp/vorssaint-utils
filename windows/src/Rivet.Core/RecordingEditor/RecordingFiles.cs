// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;

namespace Rivet.Core.RecordingEditor;

/// <summary>
/// Staged writes with an atomic commit (spec 02 §3.35): every export writes to
/// a hidden sibling of the destination and replaces the destination only when
/// complete and not cancelled. A failed or cancelled export leaves an existing
/// file byte-for-byte untouched and nothing beside it.
/// </summary>
public sealed class StagedFile : IDisposable
{
    private bool _committed;

    public StagedFile(string destination)
    {
        Destination = Path.GetFullPath(destination);
        var folder = Path.GetDirectoryName(Destination) ?? throw new ArgumentException("Destination has no folder.", nameof(destination));
        Directory.CreateDirectory(folder);
        StagingPath = Path.Combine(folder, $".{AppIdentity.Id.ToLowerInvariant()}-partial-{Guid.NewGuid():D}{Path.GetExtension(Destination)}");
    }

    public string Destination { get; }

    public string StagingPath { get; }

    /// <summary>Marks the staging file hidden (Windows); harmless elsewhere.</summary>
    public void MarkHidden()
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(StagingPath))
            {
                File.SetAttributes(StagingPath, File.GetAttributes(StagingPath) | FileAttributes.Hidden);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Moves the finished file into place (replacing an existing one). Throws on failure.</summary>
    public void Commit(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(StagingPath) || new FileInfo(StagingPath).Length == 0)
        {
            throw new IOException("The export produced no data.");
        }

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(StagingPath, File.GetAttributes(StagingPath) & ~FileAttributes.Hidden);
        }

        // Same folder, so this is a rename: MoveFileEx(MOVEFILE_REPLACE_EXISTING) on Windows.
        File.Move(StagingPath, Destination, overwrite: true);
        _committed = true;
    }

    /// <summary>Removes the staging file unless it was committed.</summary>
    public void Dispose()
    {
        if (_committed)
        {
            return;
        }

        try
        {
            if (File.Exists(StagingPath))
            {
                File.Delete(StagingPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Folders the editor writes to (spec 02 §5.1).</summary>
public static class RecordingFolders
{
    /// <summary>Where Copy puts its files before handing them to the clipboard.</summary>
    public static string CopyCache(AppPaths paths) => AppPaths.EnsureDirectory(Path.Combine(paths.Cache, "Copied Recordings"));

    /// <summary>Thumbnails for recent-captures entries of saved recordings.</summary>
    public static string Thumbnails(AppPaths paths) => AppPaths.EnsureDirectory(Path.Combine(paths.Cache, "Recording Thumbnails"));

    /// <summary>Private copies of edit-preset images.</summary>
    public static string PresetImages(AppPaths paths) => paths.LocalFolder("RecorderPresetImages");

    /// <summary>Deletes files older than <paramref name="age"/> (the copy cache is purged before each copy).</summary>
    public static void Purge(string folder, TimeSpan age)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > age)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The save folder: the setting (tilde-expanded) when it exists and is a
    /// folder, else the Desktop (as on macOS), else the user's home.
    /// </summary>
    public static string SaveFolder(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = configured.StartsWith('~')
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), configured[1..].TrimStart('/', '\\'))
                : configured;
            if (Directory.Exists(expanded))
            {
                return expanded;
            }
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
        {
            return desktop;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Free space at a path in bytes (unknown → long.MaxValue).</summary>
    public static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Copies a picture into its own folder inside <paramref name="parent"/>
    /// (<c>&lt;parent&gt;/&lt;UUID&gt;/&lt;name&gt;</c>) after checking free space (size + 500 MB).
    /// Returns the copy's path; throws <see cref="IOException"/> on failure.
    /// </summary>
    public static string CopyIntoPrivateFolder(string source, string parent)
    {
        var info = new FileInfo(source);
        if (!info.Exists || info.Length <= 0 || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Not a regular file.");
        }

        if (FreeSpace(parent) < info.Length + 500_000_000)
        {
            throw new IOException("Not enough free space.");
        }

        var folder = Path.Combine(parent, Guid.NewGuid().ToString("D").ToUpperInvariant());
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, RecordingNames.Sanitize(Path.GetFileName(source)));
        try
        {
            File.Copy(source, destination, overwrite: false);
            return destination;
        }
        catch
        {
            TryDeleteFolder(folder);
            throw;
        }
    }

    public static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>True when <paramref name="path"/> is <c>&lt;store&gt;/&lt;folder&gt;/&lt;file&gt;</c> inside <paramref name="store"/>.</summary>
    public static bool IsInsideStore(string path, string store)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(store).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return false;
            }

            var relative = full[root.Length..].Split(Path.DirectorySeparatorChar);
            if (relative.Length != 2)
            {
                return false;
            }

            var folder = new DirectoryInfo(Path.Combine(root, relative[0]));
            return folder.Exists && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
