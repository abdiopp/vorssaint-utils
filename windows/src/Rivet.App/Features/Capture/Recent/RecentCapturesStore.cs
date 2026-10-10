// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rivet.Core.App;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Util;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture.Recent;

/// <summary>One history entry as stored in <c>history.json</c> (spec 01 §5.2, plus size and duration).</summary>
internal sealed record RecentCaptureEntry
{
    public required string Id { get; init; }

    /// <summary>"screenshot", "recording" or "gif".</summary>
    public required string Kind { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Plain file name of the cached PNG (screenshots).</summary>
    public string? ScreenshotName { get; init; }

    /// <summary>Absolute path of a recording (never copied).</summary>
    public string? RecordingPath { get; init; }

    public string? ThumbnailName { get; init; }

    public double? Scale { get; init; }

    public int AnchorX { get; init; }

    public int AnchorY { get; init; }

    public int AnchorWidth { get; init; }

    public int AnchorHeight { get; init; }

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public double? DurationSeconds { get; init; }

    [JsonIgnore]
    public bool IsScreenshot => Kind == "screenshot";
}

/// <summary>
/// The shared history of screenshots and recordings (spec 01 §3.12):
/// newest first, at most 12 entries, screenshots within 256 MB, cached in
/// <c>%LOCALAPPDATA%\…\cache\RecentCaptures</c>. A missing or unreadable index
/// freezes the history (nothing is written or deleted) rather than wiping it.
/// </summary>
internal sealed partial class RecentCapturesStore : IRecentCaptures
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly object _gate = new();

    /// <summary>Serializes "write files, insert, save and clean" so cleanup never sees a half-added capture.</summary>
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly string _folder;
    private List<RecentCaptureEntry> _entries = [];

    /// <summary>Unreferenced cache files younger than this are left alone.</summary>
    internal static TimeSpan OrphanGrace { get; set; } = TimeSpan.FromMinutes(1);
    private bool _loaded;
    private bool _frozen;

    public RecentCapturesStore(AppPaths paths)
        : this(Path.Combine(paths.Cache, "RecentCaptures"))
    {
    }

    internal RecentCapturesStore(string folder)
    {
        _folder = folder;
    }

    public event EventHandler? Changed;

    public string Folder => _folder;

    /// <summary>True when the index could not be read: the history is shown but never modified.</summary>
    public bool IsFrozen
    {
        get
        {
            EnsureLoaded();
            return _frozen;
        }
    }

    public IReadOnlyList<CaptureRecord> Items
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
            {
                return _entries.Select(ToRecord).ToList();
            }
        }
    }

    internal IReadOnlyList<RecentCaptureEntry> Entries
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public string ThumbnailPath(RecentCaptureEntry entry) => entry.ThumbnailName is { } name && IsPlainName(name) ? Path.Combine(_folder, name) : string.Empty;

    public string? ScreenshotPath(RecentCaptureEntry entry) => entry.ScreenshotName is { } name && IsPlainName(name) ? Path.Combine(_folder, name) : null;

    /// <summary>Records a new screenshot: the PNG and thumbnail are written in the background. Returns its id.</summary>
    public string AddScreenshot(PixelBuffer image, PixelRect anchor)
    {
        var id = Guid.NewGuid().ToString("D");
        _ = Task.Run(async () =>
        {
            await _writes.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureLoaded();
                if (_frozen)
                {
                    return;
                }

                Directory.CreateDirectory(_folder);
                var name = $"{id}.png";
                var thumbnail = $"{id}-thumbnail.png";
                CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(image), Path.Combine(_folder, name));
                var thumb = CaptureImaging.WithScale(CaptureImaging.Thumbnail(image, RecentCapturesPolicy.ThumbnailMaxSide), 1);
                CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(thumb), Path.Combine(_folder, thumbnail));
                Insert(new RecentCaptureEntry
                {
                    Id = id,
                    Kind = "screenshot",
                    CreatedAt = DateTimeOffset.Now,
                    ScreenshotName = name,
                    ThumbnailName = thumbnail,
                    Scale = image.Scale,
                    AnchorX = anchor.X,
                    AnchorY = anchor.Y,
                    AnchorWidth = anchor.Width,
                    AnchorHeight = anchor.Height,
                    PixelWidth = image.Width,
                    PixelHeight = image.Height,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("capture", "Could not record the screenshot in recent captures.", ex);
            }
            finally
            {
                _writes.Release();
            }
        });
        return id;
    }

    /// <summary>
    /// Adds a record from another feature. Recordings keep their path (a re-save
    /// at the same path replaces the older entry); their thumbnail, when given,
    /// is copied into the cache. Screenshots given by path are copied in. A
    /// known id (an edited capture saved back) replaces its row.
    /// </summary>
    public void Add(CaptureRecord record)
    {
        _ = Task.Run(async () =>
        {
            await _writes.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureLoaded();
                if (_frozen)
                {
                    return;
                }

                Directory.CreateDirectory(_folder);
                var id = Guid.TryParse(record.Id, out _) ? record.Id : Guid.NewGuid().ToString("D");
                // Cached files are never overwritten: an id saved again gets new ones and the old ones go as orphans.
                var fileId = File.Exists(Path.Combine(_folder, $"{id}.png")) || File.Exists(Path.Combine(_folder, $"{id}-thumbnail.png"))
                    ? Guid.NewGuid().ToString("D")
                    : id;
                string? thumbnailName = null;
                if (record.ThumbnailPath is { } thumbnail && File.Exists(thumbnail) && CaptureImaging.DecodeFile(thumbnail) is { } thumbImage)
                {
                    thumbnailName = $"{fileId}-thumbnail.png";
                    var small = CaptureImaging.WithScale(CaptureImaging.Thumbnail(thumbImage, RecentCapturesPolicy.ThumbnailMaxSide), 1);
                    CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(small), Path.Combine(_folder, thumbnailName));
                }

                if (record.Kind == CaptureKind.Screenshot)
                {
                    if (CaptureImaging.DecodeFile(record.FilePath) is not { } image)
                    {
                        return;
                    }

                    var name = $"{fileId}.png";
                    CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(image), Path.Combine(_folder, name));
                    if (thumbnailName is null)
                    {
                        thumbnailName = $"{fileId}-thumbnail.png";
                        var small = CaptureImaging.WithScale(CaptureImaging.Thumbnail(image, RecentCapturesPolicy.ThumbnailMaxSide), 1);
                        CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(small), Path.Combine(_folder, thumbnailName));
                    }

                    Insert(new RecentCaptureEntry
                    {
                        Id = id, Kind = "screenshot", CreatedAt = record.CreatedAt, ScreenshotName = name, ThumbnailName = thumbnailName,
                        Scale = image.Scale, PixelWidth = image.Width, PixelHeight = image.Height,
                    });
                    return;
                }

                Insert(new RecentCaptureEntry
                {
                    Id = id,
                    Kind = record.Kind == CaptureKind.Gif ? "gif" : "recording",
                    CreatedAt = record.CreatedAt,
                    RecordingPath = record.FilePath,
                    ThumbnailName = thumbnailName,
                    PixelWidth = record.PixelWidth,
                    PixelHeight = record.PixelHeight,
                    DurationSeconds = record.Duration?.TotalSeconds,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("capture", "Could not add a recent capture.", ex);
            }
            finally
            {
                _writes.Release();
            }
        });
    }

    public void Remove(string id)
    {
        EnsureLoaded();
        bool removed;
        lock (_gate)
        {
            if (_frozen)
            {
                return;
            }

            removed = _entries.RemoveAll(e => e.Id == id) > 0;
        }

        if (removed)
        {
            RaiseChanged();
            _ = PersistAsync();
        }
    }

    /// <summary>Drops every entry; recording files themselves are never deleted.</summary>
    public void Clear()
    {
        EnsureLoaded();
        lock (_gate)
        {
            if (_frozen || _entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
        }

        RaiseChanged();
        _ = PersistAsync();
    }

    /// <summary>Saves the index off the UI thread, after any capture still being added.</summary>
    internal async Task PersistAsync()
    {
        await _writes.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(SaveAndClean).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Waits until pending writes are on disk (tests).</summary>
    internal async Task FlushAsync()
    {
        await _writes.WaitAsync().ConfigureAwait(false);
        _writes.Release();
    }

    /// <summary>Reloads from disk (a frozen history retries on the next load).</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _loaded = false;
        }

        EnsureLoaded();
        RaiseChanged();
    }

    private void Insert(RecentCaptureEntry entry)
    {
        lock (_gate)
        {
            if (_frozen)
            {
                return;
            }

            // The same id again (an edited capture saved back) or the same recording file replaces the old row.
            _entries.RemoveAll(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (entry.RecordingPath is { } path)
            {
                _entries.RemoveAll(e => e.RecordingPath is { } other && string.Equals(other, path, StringComparison.OrdinalIgnoreCase));
            }

            _entries.Insert(0, entry);
            _entries = RecentCapturesPolicy.Apply(_entries, e => e.IsScreenshot, SizeOf);
        }

        SaveAndClean();
        RaiseChanged();
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            _frozen = false;
            var index = Path.Combine(_folder, "history.json");
            if (!File.Exists(index))
            {
                // Images without an index: something went wrong, keep everything.
                _frozen = Directory.Exists(_folder) && Directory.EnumerateFiles(_folder, "*.png").Any(f => CacheName().IsMatch(Path.GetFileName(f)));
                _entries = [];
                return;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<List<RecentCaptureEntry>>(File.ReadAllBytes(index), Json);
                if (loaded is null)
                {
                    throw new JsonException("Empty index.");
                }

                var present = loaded
                    .Where(e => Guid.TryParse(e.Id, out _))
                    .Where(e => e.IsScreenshot
                        ? e.ScreenshotName is { } n && IsPlainName(n) && File.Exists(Path.Combine(_folder, n))
                        : e.RecordingPath is { } p && File.Exists(p))
                    .OrderByDescending(e => e.CreatedAt)
                    .ToList();
                _entries = RecentCapturesPolicy.Apply(present, e => e.IsScreenshot, SizeOf);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Warn("capture", "The recent-captures index is unreadable; the history is frozen until it can be read.", ex);
                _frozen = true;
                _entries = [];
            }
        }
    }

    private long? SizeOf(RecentCaptureEntry entry)
    {
        if (ScreenshotPath(entry) is not { } path)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the index, then deletes cache files the index no longer names (never through links).</summary>
    private void SaveAndClean()
    {
        List<RecentCaptureEntry> snapshot;
        lock (_gate)
        {
            if (_frozen)
            {
                return;
            }

            snapshot = _entries.ToList();
        }

        try
        {
            Directory.CreateDirectory(_folder);
            var temp = Path.Combine(_folder, $"history.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(snapshot, Json));
            File.Move(temp, Path.Combine(_folder, "history.json"), overwrite: true);

            var referenced = new HashSet<string>(snapshot.SelectMany(e => new[] { e.ScreenshotName, e.ThumbnailName }).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            var directory = new DirectoryInfo(_folder);
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            foreach (var file in directory.EnumerateFiles("*.png"))
            {
                // Files written in the last minute may belong to a capture that is being added.
                if (CacheName().IsMatch(file.Name) && !referenced.Contains(file.Name) && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && DateTime.UtcNow - file.LastWriteTimeUtc > OrphanGrace)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("capture", "Could not save the recent-captures index.", ex);
        }
    }

    private CaptureRecord ToRecord(RecentCaptureEntry entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind switch { "recording" => CaptureKind.Recording, "gif" => CaptureKind.Gif, _ => CaptureKind.Screenshot },
        FilePath = entry.IsScreenshot ? ScreenshotPath(entry) ?? string.Empty : entry.RecordingPath ?? string.Empty,
        ThumbnailPath = ThumbnailPath(entry) is { Length: > 0 } t ? t : null,
        CreatedAt = entry.CreatedAt,
        PixelWidth = entry.PixelWidth,
        PixelHeight = entry.PixelHeight,
        Duration = entry.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
    };

    private void RaiseChanged() => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));

    private static bool IsPlainName(string name) =>
        name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && name != "." && name != "..";

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(-thumbnail)?\\.png$")]
    private static partial Regex CacheName();
}
