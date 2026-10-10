// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Rivet.App.Controls;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules.Shelf;

namespace Rivet.App.Features.Shelf;

/// <summary>
/// Tile images (spec 07 §3.1.10): a 64 DIP shell thumbnail for images and
/// videos (and documents Windows can preview), decoded off the UI thread and
/// patched in under the same item id; the shell icon otherwise. Cached per
/// item revision so a healed path or new content redraws.
/// </summary>
public sealed class ShelfThumbnails(IShelfPlatform platform) : IDisposable
{
    private readonly Dictionary<(Guid, int), Entry> _cache = [];

    /// <summary>A thumbnail finished loading (UI thread).</summary>
    public event EventHandler<Guid>? Loaded;

    /// <summary>The cached image for a file item, or null (then a load starts and <see cref="Loaded"/> follows).</summary>
    public (Bitmap? Image, bool IsThumbnail) Get(ShelfItem item, double scale)
    {
        if (item.Kind != ShelfItemKind.File || item.Path is null)
        {
            return (null, false);
        }

        var key = (item.Id, item.Revision);
        if (_cache.TryGetValue(key, out var entry))
        {
            return (entry.Image, entry.IsThumbnail);
        }

        _cache[key] = new Entry(null, false);
        var path = item.Path;
        var thumbPx = (int)Math.Round(ShelfConstants.ThumbnailSize * Math.Max(1, scale));
        var iconPx = (int)Math.Round(32 * Math.Max(1, scale));
        _ = Task.Run(() =>
        {
            try
            {
                var thumbnail = platform.Thumbnail(path, thumbPx);
                var isThumb = thumbnail is not null;
                var buffer = thumbnail ?? platform.Icon(path, iconPx);
                if (buffer is null)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!_cache.ContainsKey(key))
                    {
                        return;
                    }

                    _cache[key] = new Entry(ImageInterop.ToBitmap(buffer), isThumb);
                    Loaded?.Invoke(this, key.Id);
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn("shelf", "A tile thumbnail could not be read.", ex);
            }
        });
        return (null, false);
    }

    /// <summary>Drops images of items that are gone.</summary>
    public void Prune(IReadOnlySet<Guid> living)
    {
        foreach (var key in _cache.Keys.Where(k => !living.Contains(k.Item1)).ToList())
        {
            _cache[key].Image?.Dispose();
            _cache.Remove(key);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _cache.Values)
        {
            entry.Image?.Dispose();
        }

        _cache.Clear();
    }

    private sealed record Entry(Bitmap? Image, bool IsThumbnail);
}
