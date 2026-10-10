// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Recording.Engine;

/// <summary>
/// Deletes takes nothing owns any more (spec 02 §3.18): a take whose master
/// was last touched more than 24 h ago, or one with no master whose folder is
/// older than 1 h. Only <c>Take-&lt;UUID&gt;</c> folders are considered. A take
/// that is still in use (the editor has a file open, or edited it within the
/// last day) is skipped, so a sweep can never pull files out from under an
/// open editor. Run it off the UI thread.
/// </summary>
public static class TakeSweeper
{
    public static readonly TimeSpan FinishedLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan UnfinishedLifetime = TimeSpan.FromHours(1);

    /// <summary>Returns the number of takes deleted.</summary>
    public static int Sweep(string root, IReadOnlyCollection<string> owned, DateTime nowUtc)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var ownedSet = owned.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(root, TakeFolders.Prefix + "*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            if (!Guid.TryParse(name[TakeFolders.Prefix.Length..], out _) || ownedSet.Contains(Normalize(folder)))
            {
                continue;
            }

            try
            {
                if (IsExpired(folder, nowUtc) && !IsInUse(folder))
                {
                    Directory.Delete(folder, recursive: true);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug("recorder", $"Could not sweep {name}: {ex.Message}");
            }
        }

        if (deleted > 0)
        {
            Log.Info("recorder", $"Swept {deleted} abandoned take(s).");
        }

        return deleted;
    }

    public static bool IsExpired(string folder, DateTime nowUtc)
    {
        var master = Path.Combine(folder, RecordingSession.VideoFile);
        if (File.Exists(master))
        {
            var newest = File.GetLastWriteTimeUtc(master);
            foreach (var other in new[] { "edit.json", TakeManifest.FileName })
            {
                var path = Path.Combine(folder, other);
                if (File.Exists(path))
                {
                    var time = File.GetLastWriteTimeUtc(path);
                    if (time > newest)
                    {
                        newest = time;
                    }
                }
            }

            return nowUtc - newest > FinishedLifetime;
        }

        return nowUtc - Directory.GetCreationTimeUtc(folder) > UnfinishedLifetime;
    }

    /// <summary>An editor (or player) holding the master open means the take is alive.</summary>
    public static bool IsInUse(string folder)
    {
        var master = Path.Combine(folder, RecordingSession.VideoFile);
        if (!File.Exists(master))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(master, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>Free-space guards (spec 02 §3.2, §3.15).</summary>
public static class DiskSpace
{
    /// <summary>Refuse to start below this much free space at the take location.</summary>
    public const long MinimumToStart = 2_000_000_000;

    /// <summary>Stop (keeping the file) below this much free space while recording.</summary>
    public const long MinimumWhileRecording = 500_000_000;

    /// <summary>Available bytes on the volume holding <paramref name="path"/>, or null when unknown (network paths).</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
