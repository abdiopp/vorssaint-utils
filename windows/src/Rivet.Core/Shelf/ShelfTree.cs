// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.Shelf;

/// <summary>A tile in display order: an item and its nesting depth (expanded piles show their children inline).</summary>
public readonly record struct ShelfRow(ShelfItem Item, int Depth, Guid? ParentId);

/// <summary>
/// Operations on the shelf's item tree (spec 07 §3.1.10, §3.1.12). The tree is
/// immutable: each operation returns a new list. Capacity is counted in leaves.
/// </summary>
public static class ShelfTree
{
    public static int LeafCount(ShelfItem item) => item.IsPile ? item.Children.Sum(LeafCount) : 1;

    public static int LeafCount(IEnumerable<ShelfItem> items) => items.Sum(LeafCount);

    /// <summary>existing ≥ 0, new &gt; 0, existing ≤ 200 − new. canAdd(199,1) true; (200,1) and (199,2) false.</summary>
    public static bool CanAdd(int existing, int added, int capacity = ShelfConstants.MaxLeaves) =>
        existing >= 0 && added > 0 && existing <= capacity - added;

    public static IEnumerable<ShelfItem> Leaves(ShelfItem item) => item.IsPile ? item.Children.SelectMany(Leaves) : [item];

    public static IEnumerable<ShelfItem> Leaves(IEnumerable<ShelfItem> items) => items.SelectMany(Leaves);

    public static IEnumerable<ShelfItem> AllItems(IEnumerable<ShelfItem> items) =>
        items.SelectMany(i => i.IsPile ? new[] { i }.Concat(AllItems(i.Children)) : [i]);

    public static ShelfItem? Find(IEnumerable<ShelfItem> items, Guid id)
    {
        foreach (var item in items)
        {
            if (item.Id == id)
            {
                return item;
            }

            if (item.IsPile && Find(item.Children, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>"‹first leaf title› +‹N−1›" for a pile of N leaves.</summary>
    public static string PileTitle(IReadOnlyList<ShelfItem> children)
    {
        var leaves = Leaves(children).ToList();
        if (leaves.Count == 0)
        {
            return string.Empty;
        }

        return leaves.Count == 1 ? leaves[0].Title : $"{leaves[0].Title} +{leaves.Count - 1}";
    }

    public static ShelfItem MakePile(IReadOnlyList<ShelfItem> children, bool pinned = false) => new()
    {
        Kind = ShelfItemKind.Batch,
        Children = children,
        Title = PileTitle(children),
        Pinned = pinned,
    };

    /// <summary>One new item is appended alone; more than one becomes a single pile.</summary>
    public static IReadOnlyList<ShelfItem> Append(IReadOnlyList<ShelfItem> items, IReadOnlyList<ShelfItem> added)
    {
        if (added.Count == 0)
        {
            return items;
        }

        var entry = added.Count == 1 ? added[0] : MakePile(added);
        return [.. items, entry];
    }

    /// <summary>
    /// Pinned ids plus every id inside a pinned pile. Protected items survive
    /// drag-out removal and Clear all, but not the tile ✕ or Remove selected.
    /// </summary>
    public static HashSet<Guid> Protected(IEnumerable<ShelfItem> items)
    {
        var result = new HashSet<Guid>();
        void Visit(ShelfItem item, bool insidePinned)
        {
            var pinned = insidePinned || item.Pinned;
            if (pinned)
            {
                result.Add(item.Id);
            }

            foreach (var child in item.Children)
            {
                Visit(child, pinned);
            }
        }

        foreach (var item in items)
        {
            Visit(item, false);
        }

        return result;
    }

    /// <summary>
    /// Removes items by id with the pile dissolve rules: an emptied pile
    /// disappears; a pile left with one child is replaced by that child, which
    /// inherits the pile's pin; otherwise the pile is rebuilt and retitled.
    /// </summary>
    public static IReadOnlyList<ShelfItem> Remove(IReadOnlyList<ShelfItem> items, IReadOnlySet<Guid> ids)
    {
        var result = new List<ShelfItem>();
        foreach (var item in items)
        {
            if (ids.Contains(item.Id))
            {
                continue;
            }

            if (!item.IsPile)
            {
                result.Add(item);
                continue;
            }

            var children = Remove(item.Children, ids);
            if (children.Count == item.Children.Count && children.SequenceEqual(item.Children))
            {
                result.Add(item);
            }
            else if (children.Count == 0)
            {
                continue;
            }
            else if (children.Count == 1)
            {
                var only = children[0];
                result.Add(item.Pinned && !only.Pinned ? only with { Pinned = true } : only);
            }
            else
            {
                result.Add(item with { Children = children, Title = PileTitle(children) });
            }
        }

        return result;
    }

    /// <summary>Removes every item that is not protected (Clear all).</summary>
    public static IReadOnlyList<ShelfItem> RemoveUnprotected(IReadOnlyList<ShelfItem> items)
    {
        var protectedIds = Protected(items);
        var toRemove = AllItems(items).Where(i => !protectedIds.Contains(i.Id) && !i.IsPile).Select(i => i.Id).ToHashSet();
        return Remove(items, toRemove);
    }

    /// <summary>
    /// An external drop onto a tile: new leaves are flattened and appended to a
    /// pile target (retitled), or a single target becomes a new pile
    /// [target + leaves] with a fresh id (the macOS quirk of reusing the
    /// target's id is not kept). Null when the target is gone or capacity is exceeded.
    /// </summary>
    public static IReadOnlyList<ShelfItem>? MergeExternal(IReadOnlyList<ShelfItem> items, Guid targetId, IReadOnlyList<ShelfItem> added)
    {
        var leaves = Leaves(added).ToList();
        if (leaves.Count == 0 || Find(items, targetId) is null || !CanAdd(LeafCount(items), leaves.Count))
        {
            return null;
        }

        return ReplaceById(items, targetId, target => target.IsPile
            ? target with { Children = [.. target.Children, .. leaves], Title = PileTitle([.. target.Children, .. leaves]), Revision = target.Revision + 1 }
            : MakePile([target, .. leaves]));
    }

    /// <summary>
    /// Tile onto tile (internal move): the target must not be dragged, nor any
    /// of its descendants. Sources leave the tree first (piles dissolve as
    /// needed), then join the target. Null when the move is not allowed.
    /// </summary>
    public static IReadOnlyList<ShelfItem>? MoveInto(IReadOnlyList<ShelfItem> items, Guid targetId, IReadOnlyList<Guid> sourceIds)
    {
        var target = Find(items, targetId);
        if (target is null || sourceIds.Count == 0 || sourceIds.Contains(targetId))
        {
            return null;
        }

        var targetDescendants = AllItems(target.Children).Select(i => i.Id).ToHashSet();
        if (sourceIds.Any(targetDescendants.Contains))
        {
            return null;
        }

        var sources = sourceIds.Select(id => Find(items, id)).OfType<ShelfItem>().ToList();
        // A source inside another source moves with its parent.
        var sourceSet = sources.Select(s => s.Id).ToHashSet();
        sources = sources.Where(s => !sources.Any(other => other.Id != s.Id && AllItems(other.Children).Any(d => d.Id == s.Id))).ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        var remaining = Remove(items, sourceSet);

        // The target itself may have been rebuilt (or replaced by its only child) by the removal.
        if (Find(remaining, targetId) is null)
        {
            return null;
        }

        var moved = sources.SelectMany(s => s.IsPile ? s.Children : [s]).ToList();
        return ReplaceById(remaining, targetId, t => t.IsPile
            ? t with { Children = [.. t.Children, .. moved], Title = PileTitle([.. t.Children, .. moved]), Revision = t.Revision + 1 }
            : MakePile([t, .. moved]));
    }

    public static IReadOnlyList<ShelfItem> ReplaceById(IReadOnlyList<ShelfItem> items, Guid id, Func<ShelfItem, ShelfItem> replace)
    {
        var result = new List<ShelfItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Id == id)
            {
                result.Add(replace(item));
            }
            else if (item.IsPile)
            {
                var children = ReplaceById(item.Children, id, replace);
                result.Add(ReferenceEquals(children, item.Children) || children.SequenceEqual(item.Children) ? item : item with { Children = children, Title = PileTitle(children) });
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>Sets the pin on one item.</summary>
    public static IReadOnlyList<ShelfItem> SetPinned(IReadOnlyList<ShelfItem> items, Guid id, bool pinned) =>
        ReplaceById(items, id, i => i with { Pinned = pinned });

    /// <summary>Tiles in display order: depth-first, an expanded pile's children right after it.</summary>
    public static IReadOnlyList<ShelfRow> VisibleRows(IReadOnlyList<ShelfItem> items, IReadOnlySet<Guid> expanded)
    {
        var rows = new List<ShelfRow>();
        void Visit(IReadOnlyList<ShelfItem> level, int depth, Guid? parent)
        {
            foreach (var item in level)
            {
                rows.Add(new ShelfRow(item, depth, parent));
                if (item.IsPile && expanded.Contains(item.Id))
                {
                    Visit(item.Children, depth + 1, item.Id);
                }
            }
        }

        Visit(items, 0, null);
        return rows;
    }

    /// <summary>The visible tile that represents <paramref name="id"/>: itself, or its outermost collapsed pile.</summary>
    public static Guid? VisibleAncestor(IReadOnlyList<ShelfItem> items, Guid id, IReadOnlySet<Guid> expanded)
    {
        foreach (var item in items)
        {
            if (item.Id == id)
            {
                return id;
            }

            if (item.IsPile && Find(item.Children, id) is not null)
            {
                return expanded.Contains(item.Id) ? VisibleAncestor(item.Children, id, expanded) : item.Id;
            }
        }

        return null;
    }

    /// <summary>The scope of a context menu or drag: the selection if the clicked tile is selected, else the tile; flattened to unique leaves.</summary>
    public static IReadOnlyList<ShelfItem> Scope(IReadOnlyList<ShelfItem> items, Guid clicked, IReadOnlySet<Guid> selection)
    {
        var ids = selection.Contains(clicked) ? selection : new HashSet<Guid> { clicked };
        var seen = new HashSet<Guid>();
        var result = new List<ShelfItem>();
        foreach (var id in ids)
        {
            if (Find(items, id) is not { } item)
            {
                continue;
            }

            foreach (var leaf in Leaves(item))
            {
                if (seen.Add(leaf.Id))
                {
                    result.Add(leaf);
                }
            }
        }

        // Keep shelf order.
        var order = Leaves(items).Select((l, i) => (l.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return result.OrderBy(l => order.GetValueOrDefault(l.Id, int.MaxValue)).ToList();
    }

    /// <summary>"N items: a images, b files, c notes, d links" counts for a pile tooltip.</summary>
    public static (int Images, int Files, int Notes, int Links) Breakdown(ShelfItem pile)
    {
        int images = 0, files = 0, notes = 0, links = 0;
        foreach (var leaf in Leaves(pile))
        {
            switch (leaf.Kind)
            {
                case ShelfItemKind.File when leaf.IsImage:
                    images++;
                    break;
                case ShelfItemKind.File:
                    files++;
                    break;
                case ShelfItemKind.Text:
                    notes++;
                    break;
                case ShelfItemKind.Link:
                    links++;
                    break;
            }
        }

        return (images, files, notes, links);
    }
}
