// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Clipboard;

namespace Rivet.Core.Launcher;

/// <summary>What ranking needs to know about the user (pins, names, learning).</summary>
public sealed class RankingContext
{
    public static RankingContext Empty { get; } = new();

    public IReadOnlySet<string> Pins { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>stableKey → alias.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public CommandBarUsage? Usage { get; init; }

    public CommandBarQueryMemory? Memory { get; init; }

    public CommandBarQueryHabits? Habits { get; init; }

    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Inside a category and for ":" emoji searches only the habit boost applies.</summary>
    public bool HabitsOnly { get; init; }
}

/// <summary>A ranked row with its score parts.</summary>
public sealed record RankedRow(CommandRow Row, int Priority, int Tier, int Score, int Position);

/// <summary>
/// Command Bar ranking (spec 06 §6.3): per-token scores over folded title and
/// keywords, a whole-query bonus and tier, alias/habit priority, usage and
/// pin boosts, source bias, then feature ordering and per-kind caps.
/// </summary>
public static class CommandBarSearch
{
    public const int MaxRows = 12;

    /// <summary>Best score of one token over the words of the haystack; null rejects the row.</summary>
    public static int? TokenScore(string token, string[] words, string haystack)
    {
        var best = 0;
        foreach (var word in words)
        {
            if (word == token)
            {
                return 140;
            }

            if (word.StartsWith(token, StringComparison.Ordinal))
            {
                best = Math.Max(best, 80);
            }
        }

        if (best > 0)
        {
            return best;
        }

        if (haystack.Contains(token, StringComparison.Ordinal))
        {
            return 44;
        }

        var allDigits = token.All(char.IsAsciiDigit);
        if (token.Length >= 3 && !allDigits && words.Any(w => IsSubsequence(token, w)))
        {
            return 24;
        }

        if (token.Length >= 3 && !allDigits && words.Any(w => IsAdjacentTransposition(token, w)))
        {
            return 16;
        }

        if (token.Length >= 4 && !allDigits && words.Any(w => IsWithinOneEdit(token, w)))
        {
            return 16;
        }

        return null;
    }

    /// <summary>Every character of the token appears in the word, in order.</summary>
    public static bool IsSubsequence(string token, string word)
    {
        var j = 0;
        foreach (var c in word)
        {
            if (j < token.Length && token[j] == c)
            {
                j++;
            }
        }

        return j == token.Length;
    }

    /// <summary>Equal lengths and exactly one neighbouring pair swapped (brilho/birlho).</summary>
    public static bool IsAdjacentTransposition(string a, string b)
    {
        if (a.Length != b.Length || a == b)
        {
            return false;
        }

        var first = -1;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                first = i;
                break;
            }
        }

        if (first < 0 || first + 1 >= a.Length || a[first] != b[first + 1] || a[first + 1] != b[first])
        {
            return false;
        }

        return a.AsSpan(first + 2).SequenceEqual(b.AsSpan(first + 2));
    }

    /// <summary>One substitution, insertion, deletion or adjacent swap apart (identical strings qualify).</summary>
    public static bool IsWithinOneEdit(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        if (a.Length == b.Length)
        {
            var differences = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i] && ++differences > 1)
                {
                    return IsAdjacentTransposition(a, b);
                }
            }

            return true;
        }

        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        int s = 0, l = 0;
        var skipped = false;
        while (s < shorter.Length && l < longer.Length)
        {
            if (shorter[s] == longer[l])
            {
                s++;
                l++;
            }
            else if (!skipped)
            {
                skipped = true;
                l++;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whole-query bonus and tier from the folded title, keywords and query.</summary>
    public static (int Bonus, int Tier) Bonus(string title, string keywords, string query)
    {
        if (title == query)
        {
            return (1200, 5);
        }

        if (title.StartsWith(query, StringComparison.Ordinal))
        {
            return (900, 4);
        }

        if (title.Contains(query, StringComparison.Ordinal))
        {
            return (700, 3);
        }

        return keywords.Contains(query, StringComparison.Ordinal) ? (350, 2) : (0, 1);
    }

    /// <summary>Ranks <paramref name="pool"/> against <paramref name="query"/>; rows any token rejects are dropped.</summary>
    public static List<RankedRow> Rank(IReadOnlyList<CommandRow> pool, string query, RankingContext context)
    {
        var q = TextFold.ForCommand(query);
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        var habits = context.Habits?.Prepare(q) ?? [];
        var ranked = new List<RankedRow>(pool.Count);
        for (var position = 0; position < pool.Count; position++)
        {
            var row = pool[position];
            var titleText = row.NameForArgument is { } name && CommandBarLinks.TrailingArgument(query, name) is not null
                ? q
                : TextFold.ForCommand(row.MatchTitle ?? row.Title);
            var alias = context.HabitsOnly ? null : context.Aliases.GetValueOrDefault(row.StableKey);
            var keywordText = TextFold.ForCommand(row.Keywords);
            var foldedAlias = alias is null ? null : TextFold.ForCommand(alias);
            if (foldedAlias is not null)
            {
                keywordText = keywordText + " " + foldedAlias;
            }

            var haystack = titleText + " " + keywordText;
            var words = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var baseScore = 0;
            var rejected = false;
            foreach (var token in tokens)
            {
                if (TokenScore(token, words, haystack) is not { } score)
                {
                    rejected = true;
                    break;
                }

                baseScore += score;
            }

            if (rejected)
            {
                continue;
            }

            var (bonus, tier) = Bonus(titleText, keywordText, q);
            baseScore += bonus;
            var aliasHit = 0;
            if (foldedAlias is not null)
            {
                var aliasWords = foldedAlias.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                aliasHit = aliasWords.Any(w => w == q) ? 2400 : aliasWords.Any(w => w.StartsWith(q, StringComparison.Ordinal)) ? 1100 : 0;
            }

            var habit = row.CountsUsage && context.Habits is not null ? context.Habits.Boost(habits, row.Id, context.Now) : 0;
            int priority;
            int boost;
            if (context.HabitsOnly)
            {
                priority = habit;
                boost = 0;
            }
            else
            {
                priority = Math.Max(aliasHit, habit);
                boost = (row.CountsUsage ? CommandBarUsage.Boost(context.Usage?.Get(row.Id), context.Now) : 0)
                        + (row.IsLive ? 20 : 0)
                        + aliasHit
                        + (row.CountsUsage ? context.Memory?.Boost(q, row.Id) ?? 0 : 0)
                        + CommandSources.RankBias(row.Source)
                        + (context.Pins.Contains(row.StableKey) ? 60 : 0);
            }

            ranked.Add(new RankedRow(row, priority, tier, baseScore + boost, position));
        }

        ranked.Sort((a, b) =>
        {
            var c = b.Priority.CompareTo(a.Priority);
            if (c != 0) return c;
            c = b.Tier.CompareTo(a.Tier);
            if (c != 0) return c;
            c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Position.CompareTo(b.Position);
        });
        return FeatureOrdered(ranked);
    }

    /// <summary>
    /// Among rows with no priority, rows of the same feature keep the set of
    /// positions they occupy but are reordered by role: main command, presets,
    /// settings page. Nothing else moves.
    /// </summary>
    public static List<RankedRow> FeatureOrdered(List<RankedRow> ranked)
    {
        var groups = ranked
            .Select((r, index) => (r, index))
            .Where(x => x.r.Priority == 0 && x.r.Row.FeatureId is not null)
            .GroupBy(x => x.r.Row.FeatureId!)
            .Where(g => g.Count() > 1);
        var result = ranked.ToList();
        foreach (var group in groups)
        {
            var positions = group.Select(x => x.index).ToList();
            var reordered = group.Select(x => x.r).OrderBy(r => r.Row.Role).ToList(); // stable: equal roles keep ranked order
            for (var i = 0; i < positions.Count; i++)
            {
                result[positions[i]] = reordered[i];
            }
        }

        return result;
    }

    /// <summary>
    /// Final typed list: [answer] [typed URL] [script answer] then ranked rows,
    /// answer.* rows only when the first token (≥ 3 chars) starts the answer's
    /// title, per-kind caps, 12 rows; a lone colour preview goes first, or
    /// second when a row's title already contains the typed text. Duplicate ids are dropped.
    /// </summary>
    public static List<CommandRow> Assemble(CommandRow? answer, CommandRow? typedUrl, CommandRow? scriptAnswer, IEnumerable<CommandRow> ranked, string query, CommandRow? colorPreview = null)
    {
        var result = new List<CommandRow>(MaxRows);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(CommandRow? row)
        {
            if (row is not null && result.Count < MaxRows && seen.Add(row.Id))
            {
                result.Add(row);
            }
        }

        Add(answer);
        Add(typedUrl);
        Add(scriptAnswer);
        var firstToken = TextFold.ForCommand(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in ranked)
        {
            if (result.Count >= MaxRows)
            {
                break;
            }

            if (row.Id.StartsWith("answer.", StringComparison.Ordinal)
                && !(firstToken.Length >= 3 && TextFold.ForCommand(row.Title).StartsWith(firstToken, StringComparison.Ordinal)))
            {
                continue;
            }

            if (CommandSources.Cap(row.Id) is { } cap)
            {
                var prefix = row.Id[..(row.Id.IndexOf('.') + 1)];
                var count = counts.GetValueOrDefault(prefix);
                if (count >= cap)
                {
                    continue;
                }

                counts[prefix] = count + 1;
            }

            Add(row);
        }

        if (colorPreview is not null && seen.Add(colorPreview.Id))
        {
            var typed = query.Trim();
            var index = result.Any(r => r.Title.Contains(typed, StringComparison.OrdinalIgnoreCase)) ? Math.Min(1, result.Count) : 0;
            result.Insert(index, colorPreview);
            if (result.Count > MaxRows)
            {
                result.RemoveAt(result.Count - 1);
            }
        }

        return result;
    }

    /// <summary>"brightness 40" → ("brightness", 40); the last token is digits with an optional %.</summary>
    public static (string Text, int? Number) SplitTrailingNumber(string query)
    {
        var tokens = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2)
        {
            var last = tokens[^1].TrimEnd('%');
            if (last.Length is > 0 and <= 9 && last.All(char.IsAsciiDigit) && int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return (string.Join(' ', tokens[..^1]), number);
            }
        }

        return (query.Trim(), null);
    }

    /// <summary>Argument mode: trims, optional %, 1–4 digits only, clamped into the range.</summary>
    public static int? ArgumentValue(string text, NumericRange range)
    {
        var body = text.Trim().TrimEnd('%');
        if (body.Length is < 1 or > 4 || !body.All(char.IsAsciiDigit))
        {
            return null;
        }

        return Math.Clamp(int.Parse(body, CultureInfo.InvariantCulture), range.Min, range.Max);
    }

    /// <summary>
    /// Character ranges of <paramref name="title"/> to highlight: for each query
    /// token, the first occurrence at a word start, else the first anywhere.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Highlights(string title, string query)
    {
        var tokens = TextFold.ForCommand(query.TrimStart(':')).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        var (folded, map) = TextFold.FoldWithMap(title);
        var marked = new bool[title.Length];
        foreach (var token in tokens)
        {
            var index = -1;
            var from = 0;
            while (from <= folded.Length - token.Length)
            {
                var found = folded.IndexOf(token, from, StringComparison.Ordinal);
                if (found < 0)
                {
                    break;
                }

                if (found == 0 || folded[found - 1] == ' ')
                {
                    index = found;
                    break;
                }

                index = index < 0 ? found : index;
                from = found + 1;
            }

            if (index < 0)
            {
                continue;
            }

            for (var k = index; k < index + token.Length && k < map.Length; k++)
            {
                marked[map[k]] = true;
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
}
