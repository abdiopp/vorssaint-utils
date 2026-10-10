// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Clipboard;

public enum ClipboardEditResult
{
    Saved,
    Unchanged,
    NotStorable,
    TooLargeForPins,
    Missing,
}

/// <summary>
/// The ordered history (spec 06 §3.2.4–§3.2.6): pinned group first in manual
/// order, then recent entries newest first. UI-thread only. Every change
/// bumps <see cref="Version"/> and raises <see cref="Changed"/> once.
/// </summary>
public sealed class ClipboardHistory
{
    public const int MaxTextLength = 1_000_000;
    public const long TextBudgetBytes = 64L * 1024 * 1024;
    public const long FileBudgetBytes = 96L * 1024 * 1024;

    private List<ClipboardEntry> _entries = [];
    private int _limit = 50;

    public event EventHandler? Changed;

    public IReadOnlyList<ClipboardEntry> Entries => _entries;

    public int Version { get; private set; }

    /// <summary>The saved entry believed to be on the OS clipboard right now (spec 06 §3.2.12).</summary>
    public Guid? LatestCopyId { get; set; }

    public IEnumerable<ClipboardEntry> Pinned => _entries.Where(e => e.IsPinned);

    public IEnumerable<ClipboardEntry> Recent => _entries.Where(e => !e.IsPinned);

    public int RecentCount => _entries.Count(e => !e.IsPinned);

    /// <summary>Recent-entry cap; 0 is unlimited. Changing it trims immediately.</summary>
    public int Limit
    {
        get => _limit;
        set
        {
            if (_limit == value)
            {
                return;
            }

            _limit = value;
            if (Trim())
            {
                Commit();
            }
        }
    }

    public ClipboardEntry? Find(Guid id) => _entries.FirstOrDefault(e => e.Id == id);

    public int IndexOf(Guid id) => _entries.FindIndex(e => e.Id == id);

    /// <summary>Replaces everything (load, budget fit after a save) without trimming by count.</summary>
    public void Replace(IEnumerable<ClipboardEntry> entries)
    {
        _entries = Normalize(entries);
        Trim();
        Commit();
    }

    /// <summary>
    /// Records a capture (spec 06 §3.2.2 step 6). An entry with the same
    /// content keeps its id and pin, moves to the top of its group, gets the new
    /// time and the new source (unless <paramref name="keepOldSource"/>).
    /// </summary>
    public ClipboardEntry Record(ClipboardEntry candidate, bool keepOldSource = false)
    {
        var existingIndex = _entries.FindIndex(e => e.HasSameContent(candidate));
        ClipboardEntry recorded;
        if (existingIndex >= 0)
        {
            var existing = _entries[existingIndex];
            _entries.RemoveAt(existingIndex);
            recorded = existing with
            {
                CopiedAt = candidate.CopiedAt,
                SourceApp = keepOldSource ? existing.SourceApp : candidate.SourceApp,
            };
        }
        else
        {
            recorded = candidate;
        }

        if (recorded.IsPinned)
        {
            _entries.Insert(0, recorded);
        }
        else
        {
            _entries.Insert(FirstRecentIndex(), recorded);
        }

        LatestCopyId = recorded.Id;
        _entries = Normalize(_entries);
        Trim();
        Commit();
        return recorded;
    }

    /// <summary>Pins (to the top of the pinned group). Refused when the pinned entries would not fit the file budget.</summary>
    public bool Pin(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0 || _entries[index].IsPinned)
        {
            return index >= 0;
        }

        var pinned = _entries[index] with { PinnedAt = DateTimeOffset.UtcNow };
        if (PinnedSize(Pinned.Append(pinned)) > FileBudgetBytes)
        {
            return false;
        }

        _entries.RemoveAt(index);
        _entries.Insert(0, pinned);
        Commit();
        return true;
    }

    public void Unpin(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0 || !_entries[index].IsPinned)
        {
            return;
        }

        var entry = _entries[index] with { PinnedAt = null };
        _entries.RemoveAt(index);
        _entries.Insert(FirstRecentIndex(), entry);
        Trim();
        Commit();
    }

    public bool TogglePin(Guid id)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return false;
        }

        if (entry.IsPinned)
        {
            Unpin(id);
            return true;
        }

        return Pin(id);
    }

    /// <summary>
    /// Entries pasted or copied from the history: recent ones move to the top
    /// of the recent group (in the given order), pinned ones keep their place
    /// so Ctrl+1–9 positions stay stable; all get the current time.
    /// </summary>
    public void Reuse(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var moved = new List<ClipboardEntry>();
        foreach (var id in ids.Distinct())
        {
            var index = IndexOf(id);
            if (index < 0)
            {
                continue;
            }

            var entry = _entries[index] with { CopiedAt = now };
            if (entry.IsPinned)
            {
                _entries[index] = entry;
            }
            else
            {
                _entries.RemoveAt(index);
                moved.Add(entry);
            }
        }

        _entries.InsertRange(FirstRecentIndex(), moved);
        Commit();
    }

    /// <summary>Swaps with the neighbour in the same group; false at a group edge.</summary>
    public bool Move(Guid id, int delta)
    {
        var index = IndexOf(id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _entries.Count || _entries[index].IsPinned != _entries[target].IsPinned)
        {
            return false;
        }

        (_entries[index], _entries[target]) = (_entries[target], _entries[index]);
        Commit();
        return true;
    }

    public bool CanMove(Guid id, int delta)
    {
        var index = IndexOf(id);
        var target = index + delta;
        return index >= 0 && target >= 0 && target < _entries.Count && _entries[index].IsPinned == _entries[target].IsPinned;
    }

    public void Delete(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        if (_entries.RemoveAll(e => set.Contains(e.Id)) == 0)
        {
            return;
        }

        if (LatestCopyId is { } latest && set.Contains(latest))
        {
            LatestCopyId = null;
        }

        Commit();
    }

    /// <summary>The ids "Clear unpinned" counts when its button is pressed (search filter ignored).</summary>
    public IReadOnlyList<Guid> RecentIds() => Recent.Select(e => e.Id).ToList();

    /// <summary>Deletes exactly the counted ids that are still unpinned; anything copied meanwhile survives.</summary>
    public int ClearUnpinned(IReadOnlyCollection<Guid> counted)
    {
        var set = counted.ToHashSet();
        var removed = _entries.RemoveAll(e => !e.IsPinned && set.Contains(e.Id));
        if (removed > 0)
        {
            if (LatestCopyId is { } latest && set.Contains(latest))
            {
                LatestCopyId = null;
            }

            Commit();
        }

        return removed;
    }

    /// <summary>Whether a draft can be stored as text: at most 1,000,000 characters and not all whitespace.</summary>
    public static bool IsStorable(string text) => text.Length <= MaxTextLength && !string.IsNullOrWhiteSpace(text);

    /// <summary>Changes the stored text only (the OS clipboard is not touched).</summary>
    public ClipboardEditResult Edit(Guid id, string text)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return ClipboardEditResult.Missing;
        }

        var entry = _entries[index];
        if (entry.Kind != ClipboardEntryKind.Text || !IsStorable(text))
        {
            return ClipboardEditResult.NotStorable;
        }

        if (entry.Text == text)
        {
            return ClipboardEditResult.Unchanged;
        }

        var edited = entry with { Text = text };
        if (edited.IsPinned && PinnedSize(Pinned.Select(e => e.Id == id ? edited : e)) > FileBudgetBytes)
        {
            return ClipboardEditResult.TooLargeForPins;
        }

        _entries[index] = edited;
        if (LatestCopyId == id)
        {
            LatestCopyId = null;
        }

        Commit();
        return ClipboardEditResult.Saved;
    }

    /// <summary>
    /// Applies the recent count limit and the 64 MiB text budget (pinned
    /// first, then recent newest first; an entry that does not fit is skipped).
    /// Returns true when something was removed.
    /// </summary>
    public bool Trim()
    {
        var kept = new List<ClipboardEntry>(_entries.Count);
        long textBytes = 0;
        var recentKept = 0;
        foreach (var entry in _entries.Where(e => e.IsPinned).Concat(_entries.Where(e => !e.IsPinned)))
        {
            if (!entry.IsPinned && _limit > 0 && recentKept >= _limit)
            {
                continue;
            }

            var bytes = entry.TextBytes;
            if (textBytes + bytes > TextBudgetBytes)
            {
                continue;
            }

            textBytes += bytes;
            kept.Add(entry);
            if (!entry.IsPinned)
            {
                recentKept++;
            }
        }

        if (kept.Count == _entries.Count)
        {
            return false;
        }

        _entries = kept;
        if (LatestCopyId is { } latest && kept.All(e => e.Id != latest))
        {
            LatestCopyId = null;
        }

        return true;
    }

    private static long PinnedSize(IEnumerable<ClipboardEntry> pinned) =>
        pinned.Sum(ClipboardHistoryFile.EncodedSize) + 2;

    private int FirstRecentIndex()
    {
        var index = _entries.FindIndex(e => !e.IsPinned);
        return index < 0 ? _entries.Count : index;
    }

    private static List<ClipboardEntry> Normalize(IEnumerable<ClipboardEntry> entries)
    {
        var list = entries.ToList();
        return list.Where(e => e.IsPinned).Concat(list.Where(e => !e.IsPinned)).ToList();
    }

    private void Commit()
    {
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
