// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.Shelf;

/// <summary>
/// Tile selection (spec 07 §3.1.12): a set of ids plus an anchor. Click
/// toggles one id and sets the anchor; Shift-click unions the visible range
/// between the anchor and the clicked tile (inclusive, either direction);
/// Ctrl+A unions all visible tiles; Esc clears both. Session-only.
/// </summary>
public sealed class ShelfSelection
{
    private readonly HashSet<Guid> _selected = [];

    public event EventHandler? Changed;

    public IReadOnlySet<Guid> Selected => _selected;

    public Guid? Anchor { get; private set; }

    public int Count => _selected.Count;

    public bool Contains(Guid id) => _selected.Contains(id);

    public void Click(Guid id)
    {
        if (!_selected.Remove(id))
        {
            _selected.Add(id);
        }

        Anchor = id;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ShiftClick(Guid id, IReadOnlyList<Guid> visibleOrder)
    {
        var to = IndexOf(visibleOrder, id);
        var from = Anchor is { } anchor ? IndexOf(visibleOrder, anchor) : -1;
        if (from < 0 || to < 0)
        {
            Click(id);
            return;
        }

        var (lo, hi) = from <= to ? (from, to) : (to, from);
        for (var i = lo; i <= hi; i++)
        {
            _selected.Add(visibleOrder[i]);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll(IEnumerable<Guid> visible)
    {
        _selected.UnionWith(visible);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_selected.Count == 0 && Anchor is null)
        {
            return;
        }

        _selected.Clear();
        Anchor = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deselect the given ids (collapsing a pile deselects its descendants).</summary>
    public void Deselect(IEnumerable<Guid> ids)
    {
        var changed = false;
        foreach (var id in ids)
        {
            changed |= _selected.Remove(id);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>After any removal: keep only surviving ids; drop a dead anchor.</summary>
    public void Prune(IReadOnlySet<Guid> surviving)
    {
        var before = _selected.Count;
        _selected.IntersectWith(surviving);
        var anchorDead = Anchor is { } anchor && !surviving.Contains(anchor);
        if (anchorDead)
        {
            Anchor = null;
        }

        if (before != _selected.Count || anchorDead)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static int IndexOf(IReadOnlyList<Guid> list, Guid id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}
