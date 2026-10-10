// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Util;

namespace Rivet.Core.Modules.Shelf;

public enum ShelfAddResult
{
    Added,

    /// <summary>Nothing usable in the drop.</summary>
    Empty,

    /// <summary>The 200-leaf capacity would be exceeded; nothing was added.</summary>
    Full,

    /// <summary>The merge target no longer exists.</summary>
    TargetGone,
}

/// <summary>
/// The shelf's items and their persistence (spec 07 §5.1). Items are saved to
/// <c>shelf.json</c> in the local data folder (machine-specific paths, never in
/// settings backups); writes are coalesced to one per UI turn and flushed
/// synchronously on exit. Pasted images, GIFs and received files are owned
/// copies under <c>ShelfFiles</c>; only owned files are ever deleted (10
/// minutes after their item leaves), and a launch sweep removes orphans.
/// User files are referenced and never touched. Call on the UI thread.
/// </summary>
public sealed class ShelfStore : IDisposable
{
    public const string FileName = "shelf.json";

    private readonly string _folder;
    private readonly string _ownedRoot;
    private readonly ShelfRestoreContext _restore;
    private readonly Func<byte[], byte[]?> _toPng;
    private readonly Func<string, string?> _bookmark;
    private readonly List<(DateTime Due, string Path)> _retiring = [];
    private readonly DateTime _launchTime = DateTime.UtcNow;
    private IReadOnlyList<ShelfItem> _items = [];
    private bool _savePending;
    private bool _loaded;
    private bool _writable = true;
    private Timer? _retireTimer;

    public ShelfStore(string folder, string ownedRoot, ShelfRestoreContext? restore = null, Func<byte[], byte[]?>? toPng = null, Func<string, string?>? bookmark = null)
    {
        _folder = folder;
        _ownedRoot = ownedRoot;
        _restore = restore ?? new ShelfRestoreContext();
        _toPng = toPng ?? (bytes => bytes);
        _bookmark = bookmark ?? (_ => null);
    }

    /// <summary>Raised after the items changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ShelfItem> Items => _items;

    public int LeafCount => ShelfTree.LeafCount(_items);

    public bool IsEmpty => _items.Count == 0;

    public string FilePath => Path.Combine(_folder, FileName);

    public string OwnedRoot => _ownedRoot;

    public ShelfLoadOutcome? LastLoadOutcome { get; private set; }

    /// <summary>
    /// Restores the saved items. Reading and healing run off the UI thread;
    /// restored items go before anything added meanwhile. An unreadable or
    /// partially readable file is kept aside (never silently overwritten).
    /// </summary>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var path = FilePath;
        var (outcome, restored) = await Task.Run(() =>
        {
            byte[]? bytes = null;
            try
            {
                if (File.Exists(path))
                {
                    bytes = File.ReadAllBytes(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("shelf", "The saved shelf could not be read.", ex);
                return (ShelfLoadOutcome.Unreadable, (IReadOnlyList<ShelfItem>)[]);
            }

            var result = ShelfCodec.Decode(bytes);
            return (result.Outcome, ShelfCodec.Sanitize(result.Items, _restore));
        }).ConfigureAwait(true);

        LastLoadOutcome = outcome;
        if (outcome != ShelfLoadOutcome.Items)
        {
            PreserveDamagedFile(outcome);
        }

        if (restored.Count > 0)
        {
            _items = ShelfCodec.MergeRestored(restored, _items);
            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }

        if (outcome == ShelfLoadOutcome.Items)
        {
            _ = Task.Run(SweepOrphans);
        }
    }

    /// <summary>Adds a parsed drop (one item alone, several as one pile), writing owned payloads first.</summary>
    public ShelfAddResult Add(IReadOnlyList<ShelfDropPart> parts)
    {
        var (items, owned) = Materialize(parts);
        if (items.Count == 0)
        {
            return ShelfAddResult.Empty;
        }

        if (!ShelfTree.CanAdd(LeafCount, items.Count))
        {
            DeleteRejected(owned);
            return ShelfAddResult.Full;
        }

        SetItems(ShelfTree.Append(_items, items));
        return ShelfAddResult.Added;
    }

    /// <summary>An external drop onto a tile.</summary>
    public ShelfAddResult MergeInto(Guid targetId, IReadOnlyList<ShelfDropPart> parts)
    {
        if (ShelfTree.Find(_items, targetId) is null)
        {
            return ShelfAddResult.TargetGone;
        }

        var (items, owned) = Materialize(parts);
        if (items.Count == 0)
        {
            return ShelfAddResult.Empty;
        }

        var merged = ShelfTree.MergeExternal(_items, targetId, items);
        if (merged is null)
        {
            DeleteRejected(owned);
            return ShelfAddResult.Full;
        }

        SetItems(merged);
        return ShelfAddResult.Added;
    }

    /// <summary>A tile dragged onto another tile.</summary>
    public bool MoveInto(Guid targetId, IReadOnlyList<Guid> sources)
    {
        if (ShelfTree.MoveInto(_items, targetId, sources) is not { } moved)
        {
            return false;
        }

        SetItems(moved);
        return true;
    }

    /// <summary>Files by reference (several form one pile). Returns false when they don't fit.</summary>
    public ShelfAddResult AddFiles(IReadOnlyList<string> paths)
    {
        var parts = ShelfDropParser.Parse([new ShelfDropEntry { Files = paths }]);
        return Add(parts);
    }

    public ShelfAddResult AddText(string text)
    {
        var parts = ShelfDropParser.Parse([new ShelfDropEntry { Text = text }]);
        return Add(parts);
    }

    /// <summary>Tile ✕ and "Remove selected": removes even pinned items.</summary>
    public void Remove(IReadOnlySet<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var before = _items;
        SetItems(ShelfTree.Remove(_items, ids));
        RetireRemoved(before);
    }

    /// <summary>Clear all: everything that is not protected by a pin.</summary>
    public void ClearUnprotected()
    {
        var before = _items;
        SetItems(ShelfTree.RemoveUnprotected(_items));
        RetireRemoved(before);
    }

    /// <summary>After an accepted drag-out: removes the dragged items that are not protected.</summary>
    public void RemoveAfterDrag(IReadOnlyCollection<Guid> dragged)
    {
        var protectedIds = ShelfTree.Protected(_items);
        var removable = dragged.Where(id => !protectedIds.Contains(id)).ToHashSet();
        Remove(removable);
    }

    public void SetPinned(Guid id, bool pinned)
    {
        if (ShelfTree.Find(_items, id) is { } item && item.Pinned != pinned)
        {
            SetItems(ShelfTree.SetPinned(_items, id, pinned));
        }
    }

    /// <summary>Updates one item in place (healed path, bumped revision for a new thumbnail).</summary>
    public void Update(Guid id, Func<ShelfItem, ShelfItem> change)
    {
        if (ShelfTree.Find(_items, id) is null)
        {
            return;
        }

        SetItems(ShelfTree.ReplaceById(_items, id, change));
    }

    /// <summary>
    /// The living check before a drag-out or share: a missing file gets one
    /// chance to heal from its bookmark. Returns the living leaves; dead ones on
    /// an absent drive are kept on the shelf, other dead ones are reported in
    /// <paramref name="dead"/>.
    /// </summary>
    public IReadOnlyList<ShelfItem> Living(IReadOnlyList<ShelfItem> leaves, out IReadOnlyList<ShelfItem> dead)
    {
        var living = new List<ShelfItem>();
        var gone = new List<ShelfItem>();
        foreach (var leaf in leaves)
        {
            if (leaf.Kind != ShelfItemKind.File || leaf.Path is null || _restore.Exists(leaf.Path))
            {
                living.Add(leaf);
                continue;
            }

            if (_restore.Heal(leaf) is { } healed && _restore.Exists(healed))
            {
                var updated = leaf with { Path = healed, Title = Path.GetFileName(healed), Revision = leaf.Revision + 1 };
                Update(leaf.Id, _ => updated);
                living.Add(updated);
                continue;
            }

            if (_restore.RootAvailable(leaf.Path))
            {
                gone.Add(leaf);
            }
        }

        dead = gone;
        return living;
    }

    /// <summary>Files inside the store (or the temp fallback) belong to the shelf.</summary>
    public bool IsOwned(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(_ownedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Copies a file into the store as <c>&lt;UUID&gt;/&lt;original name&gt;</c> (screenshots, received files) and returns the new path.</summary>
    public string? CopyIntoStore(string source)
    {
        try
        {
            var name = Path.GetFileName(source);
            if (name is "" or "." or "..")
            {
                return null;
            }

            var folder = Path.Combine(_ownedRoot, Guid.NewGuid().ToString("D").ToUpperInvariant());
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, name);
            File.Copy(source, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("shelf", "Copying into the shelf store failed.", ex);
            return null;
        }
    }

    /// <summary>Writes pending changes now (app exit, feature off).</summary>
    public void Flush()
    {
        if (!_savePending)
        {
            return;
        }

        _savePending = false;
        Write();
    }

    public void Dispose()
    {
        Flush();
        _retireTimer?.Dispose();
        // Deletions that were still waiting are done now: the files are no longer referenced.
        foreach (var (_, path) in _retiring.ToList())
        {
            TryDeleteOwned(path);
        }

        _retiring.Clear();
    }

    private (List<ShelfItem> Items, List<string> Owned) Materialize(IReadOnlyList<ShelfDropPart> parts)
    {
        var items = new List<ShelfItem>();
        var owned = new List<string>();
        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case ShelfDropPartKind.Gif when part.Data is { } gif:
                    if (WriteOwned(gif, ".gif") is { } gifPath)
                    {
                        owned.Add(gifPath);
                        items.Add(ShelfItem.FileItem(gifPath, title: "GIF"));
                    }

                    break;
                case ShelfDropPartKind.Image when part.Data is { } image:
                    var png = _toPng(image);
                    if (png is not null && WriteOwned(png, ".png") is { } pngPath)
                    {
                        owned.Add(pngPath);
                        items.Add(ShelfItem.FileItem(pngPath, title: L("Strings.shelfItemImage")));
                    }

                    break;
                case ShelfDropPartKind.File when part.Item is { Path: { } path } file:
                    items.Add(file with { Bookmark = _bookmark(path) });
                    break;
                default:
                    if (part.Item is { } item)
                    {
                        items.Add(item);
                    }

                    break;
            }
        }

        return (items, owned);
    }

    private static string L(string key) => Localization.L.Get(key);

    private string? WriteOwned(byte[] data, string extension)
    {
        try
        {
            Directory.CreateDirectory(_ownedRoot);
            var path = Path.Combine(_ownedRoot, Guid.NewGuid().ToString("D").ToUpperInvariant() + extension);
            File.WriteAllBytes(path, data);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("shelf", "Could not store the dropped data.", ex);
            return null;
        }
    }

    private void DeleteRejected(IEnumerable<string> owned)
    {
        foreach (var path in owned)
        {
            if (!IsReferenced(path))
            {
                TryDeleteOwned(path);
            }
        }
    }

    private void SetItems(IReadOnlyList<ShelfItem> items)
    {
        _items = items;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_savePending)
        {
            return;
        }

        _savePending = true;
        UiThread.Post(() =>
        {
            if (_savePending)
            {
                _savePending = false;
                Write();
            }
        });
    }

    private void Write()
    {
        if (!_writable)
        {
            return;
        }

        var bytes = ShelfCodec.Encode(_items);
        var temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("shelf", "The shelf could not be saved.", ex);
        }
    }

    private void PreserveDamagedFile(ShelfLoadOutcome outcome)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var aside = $"{FilePath}.{(outcome == ShelfLoadOutcome.Partial ? "partial" : "unreadable")}-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(FilePath, aside, overwrite: true);
                Log.Warn("shelf", $"Saved shelf was {outcome}; kept a copy at {aside}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a safe copy, do not overwrite the original.
            _writable = false;
            Log.Error("shelf", "Could not keep a copy of the damaged shelf; saving is disabled for this session.", ex);
        }
    }

    private bool IsReferenced(string path) =>
        ShelfTree.Leaves(_items).Any(l => l.Path is { } p && string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    private void RetireRemoved(IReadOnlyList<ShelfItem> before)
    {
        var survivors = ShelfTree.Leaves(_items).Select(l => l.Path).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var due = DateTime.UtcNow + ShelfConstants.OwnedPayloadRetirement;
        foreach (var leaf in ShelfTree.Leaves(before))
        {
            if (leaf.Path is { } path && IsOwned(path) && !survivors.Contains(path))
            {
                _retiring.Add((due, path));
            }
        }

        if (_retiring.Count > 0)
        {
            _retireTimer ??= new Timer(_ => UiThread.Post(RetireDue), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    private void RetireDue()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _retiring.Where(r => r.Due <= now).ToList())
        {
            _retiring.Remove(entry);
            if (!IsReferenced(entry.Path))
            {
                TryDeleteOwned(entry.Path);
            }
        }
    }

    /// <summary>Deletes an owned file and, for <c>&lt;UUID&gt;/name</c> payloads, its folder directly inside the store.</summary>
    private void TryDeleteOwned(string path)
    {
        if (!IsOwned(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var parent = Path.GetDirectoryName(path);
            if (parent is not null && Guid.TryParse(Path.GetFileName(parent), out _)
                && string.Equals(Path.GetDirectoryName(parent)?.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_ownedRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("shelf", $"Could not delete {path}.", ex);
        }
    }

    /// <summary>Launch sweep: store entries no live item references and that were modified before launch.</summary>
    private void SweepOrphans()
    {
        try
        {
            if (!Directory.Exists(_ownedRoot))
            {
                return;
            }

            var referenced = ShelfTree.Leaves(_items).Select(l => l.Path).OfType<string>().Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Directory.EnumerateFileSystemEntries(_ownedRoot))
            {
                var full = Path.GetFullPath(entry);
                var isFolder = Directory.Exists(full);
                var modified = isFolder ? Directory.GetLastWriteTimeUtc(full) : File.GetLastWriteTimeUtc(full);
                if (modified >= _launchTime)
                {
                    continue;
                }

                var stillUsed = isFolder
                    ? referenced.Any(r => r.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    : referenced.Contains(full);
                if (stillUsed)
                {
                    continue;
                }

                if (isFolder)
                {
                    Directory.Delete(full, recursive: true);
                }
                else
                {
                    File.Delete(full);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("shelf", "The launch sweep of the shelf store failed.", ex);
        }
    }
}
