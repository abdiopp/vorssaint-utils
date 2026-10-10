// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Capture;

/// <summary>Caps of the recent-captures history (spec 01 §3.12, §6.21).</summary>
public static class RecentCapturesPolicy
{
    public const int MaxEntries = 12;
    public const long MaxScreenshotBytes = 256L * 1024 * 1024;
    public const int ThumbnailMaxSide = 360;

    /// <summary>
    /// Walks entries newest first, keeping at most <see cref="MaxEntries"/>.
    /// Recordings are always kept; the first screenshot is always kept; a later
    /// screenshot is kept only while the screenshots' bytes stay within the
    /// budget (skipped otherwise, the walk continues). Unknown sizes are kept.
    /// </summary>
    public static List<T> Apply<T>(IEnumerable<T> newestFirst, Func<T, bool> isScreenshot, Func<T, long?> sizeOf)
    {
        var kept = new List<T>();
        long bytes = 0;
        var firstScreenshot = true;
        foreach (var entry in newestFirst)
        {
            if (kept.Count >= MaxEntries)
            {
                break;
            }

            if (!isScreenshot(entry))
            {
                kept.Add(entry);
                continue;
            }

            var size = sizeOf(entry);
            if (firstScreenshot)
            {
                firstScreenshot = false;
                bytes += Math.Max(0, size ?? 0);
                kept.Add(entry);
                continue;
            }

            if (size is null)
            {
                kept.Add(entry);
                continue;
            }

            if (bytes + size.Value <= MaxScreenshotBytes)
            {
                bytes += size.Value;
                kept.Add(entry);
            }
        }

        return kept;
    }
}

/// <summary>One file of the copied-screenshots cache.</summary>
public sealed record CachedFile(string Path, DateTime ModifiedUtc, long Size);

/// <summary>Pruning of the copied-screenshots cache (spec 01 §3.8.3, §6.21).</summary>
public static class CopiedFilesPolicy
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public const int MaxFiles = 100;
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Files to delete: everything but <paramref name="current"/> older than 24 h;
    /// of the rest (newest first, ties by path) keep the current file plus files
    /// while the count stays under 100 and the bytes within 256 MB (a negative
    /// size counts as over budget). The just-published file is never deleted.
    /// </summary>
    public static List<string> FilesToDelete(IEnumerable<CachedFile> files, string? current, DateTime nowUtc)
    {
        var delete = new List<string>();
        var fresh = new List<CachedFile>();
        foreach (var file in files)
        {
            if (IsCurrent(file.Path, current))
            {
                continue;
            }

            if (nowUtc - file.ModifiedUtc > MaxAge)
            {
                delete.Add(file.Path);
            }
            else
            {
                fresh.Add(file);
            }
        }

        var count = current is null ? 0 : 1;
        long bytes = 0;
        foreach (var file in fresh.OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            if (file.Size < 0 || count >= MaxFiles || bytes + file.Size > MaxBytes)
            {
                delete.Add(file.Path);
                continue;
            }

            count++;
            bytes += file.Size;
        }

        return delete;
    }

    private static bool IsCurrent(string path, string? current) =>
        current is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
}
