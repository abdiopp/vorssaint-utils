// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Clipboard;

/// <summary>
/// Clipboard history search (spec 06 §6.2): every query word must occur in
/// the folded searchable text; ranked by score, ties keep history order.
/// Folded text is cached per entry and the last result is memoized.
/// </summary>
public sealed class ClipboardSearch
{
    private readonly Dictionary<Guid, (ClipboardEntry Entry, string Folded, string[] Words)> _cache = [];
    private (string Query, int Version, IReadOnlyList<ClipboardEntry> Result)? _memo;

    public string ImageLabel { get; set; } = "Image";

    public IReadOnlyList<ClipboardEntry> Search(string query, IReadOnlyList<ClipboardEntry> entries, int version)
    {
        if (_memo is { } memo && memo.Version == version && memo.Query == query)
        {
            return memo.Result;
        }

        var result = Rank(query, entries);
        _memo = (query, version, result);
        return result;
    }

    public IReadOnlyList<ClipboardEntry> Rank(string query, IReadOnlyList<ClipboardEntry> entries)
    {
        var q = TextFold.ForClipboard(query);
        if (q.Length == 0)
        {
            return entries;
        }

        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(int Score, int Index, ClipboardEntry Entry)>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var (folded, words) = Folded(entry);
            if (!tokens.All(t => folded.Contains(t, StringComparison.Ordinal)))
            {
                continue;
            }

            scored.Add((Score(folded, words, q, tokens, entry.IsPinned), i, entry));
        }

        PruneCache(entries);
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Index).Select(s => s.Entry).ToList();
    }

    public static int Score(string folded, string[] words, string query, string[] tokens, bool pinned)
    {
        var score = pinned ? 30 : 0;
        if (folded == query)
        {
            score += 1200;
        }

        if (folded.StartsWith(query, StringComparison.Ordinal))
        {
            score += 900;
        }

        if (folded.Contains(query, StringComparison.Ordinal))
        {
            score += 700;
        }

        foreach (var token in tokens)
        {
            if (words.Contains(token, StringComparer.Ordinal))
            {
                score += 140;
            }
            else if (words.Any(w => w.StartsWith(token, StringComparison.Ordinal)))
            {
                score += 80;
            }
            else
            {
                score += 40;
            }
        }

        return score;
    }

    /// <summary>
    /// Ranges (start, length) in <paramref name="text"/> to highlight: every
    /// occurrence of every query word within the first 500 characters.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Highlights(string text, string query)
    {
        var tokens = TextFold.ForClipboard(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || text.Length == 0)
        {
            return [];
        }

        var head = text.Length > 500 ? text[..500] : text;
        var (folded, map) = TextFold.FoldWithMap(head);
        var marked = new bool[head.Length];
        foreach (var token in tokens)
        {
            var from = 0;
            while (from < folded.Length)
            {
                var index = folded.IndexOf(token, from, StringComparison.Ordinal);
                if (index < 0)
                {
                    break;
                }

                for (var k = index; k < index + token.Length && k < map.Length; k++)
                {
                    marked[map[k]] = true;
                }

                from = index + Math.Max(1, token.Length);
            }
        }

        var ranges = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= marked.Length; i++)
        {
            var on = i < marked.Length && marked[i];
            if (on && start < 0)
            {
                start = i;
            }
            else if (!on && start >= 0)
            {
                ranges.Add((start, i - start));
                start = -1;
            }
        }

        return ranges;
    }

    private (string Folded, string[] Words) Folded(ClipboardEntry entry)
    {
        if (_cache.TryGetValue(entry.Id, out var cached) && ReferenceEquals(cached.Entry, entry))
        {
            return (cached.Folded, cached.Words);
        }

        var folded = TextFold.ForClipboard(entry.SearchableText(ImageLabel));
        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _cache[entry.Id] = (entry, folded, words);
        return (folded, words);
    }

    private void PruneCache(IReadOnlyList<ClipboardEntry> entries)
    {
        if (_cache.Count <= entries.Count * 2 + 64)
        {
            return;
        }

        var live = entries.Select(e => e.Id).ToHashSet();
        foreach (var id in _cache.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _cache.Remove(id);
        }
    }
}
