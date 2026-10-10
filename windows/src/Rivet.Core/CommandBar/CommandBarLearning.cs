// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rivet.Core.Clipboard;

namespace Rivet.Core.Launcher;

/// <summary>Run count and last use of one row.</summary>
public sealed record UsageRecord(int Count, long LastUsedUnix);

/// <summary>
/// Persisted run counts (<c>commandBarUsage</c>, spec 06 §6.4.1): never the
/// queries, only which rows ran. Count ≤ 999; more than 200 ids drops the
/// least recently used.
/// </summary>
public sealed class CommandBarUsage
{
    public const int MaxCount = 999;
    public const int MaxRows = 200;

    private readonly Dictionary<string, UsageRecord> _records = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, UsageRecord> Records => _records;

    public UsageRecord? Get(string rowId) => _records.GetValueOrDefault(rowId);

    public void Record(string rowId, DateTimeOffset now)
    {
        var count = _records.TryGetValue(rowId, out var existing) ? existing.Count : 0;
        _records[rowId] = new UsageRecord(Math.Min(count + 1, MaxCount), now.ToUnixTimeSeconds());
        while (_records.Count > MaxRows)
        {
            var oldest = _records.MinBy(kv => kv.Value.LastUsedUnix).Key;
            _records.Remove(oldest);
        }
    }

    public void Forget(string rowId) => _records.Remove(rowId);

    public void Clear() => _records.Clear();

    /// <summary>min(count, 40) × weight; weight 12 under 2 h, 8 under 48 h, 5 under 14 days, else 2 (max 480).</summary>
    public static int Boost(UsageRecord? use, DateTimeOffset now)
    {
        if (use is null)
        {
            return 0;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(use.LastUsedUnix);
        var weight = age < TimeSpan.FromHours(2) ? 12 : age < TimeSpan.FromHours(48) ? 8 : age < TimeSpan.FromDays(14) ? 5 : 2;
        return Math.Min(use.Count, 40) * weight;
    }

    /// <summary>Used rows, most used first (count, then recency).</summary>
    public IEnumerable<string> MostUsed() =>
        _records.OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.LastUsedUnix).Select(kv => kv.Key);

    public string Serialize()
    {
        var map = _records.ToDictionary(kv => kv.Key, kv => new Dictionary<string, long> { ["count"] = kv.Value.Count, ["lastUsed"] = kv.Value.LastUsedUnix });
        return JsonSerializer.Serialize(map);
    }

    public static CommandBarUsage Deserialize(string json)
    {
        var usage = new CommandBarUsage();
        if (string.IsNullOrWhiteSpace(json))
        {
            return usage;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return usage;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("count", out var count) && count.TryGetInt32(out var c)
                    && property.Value.TryGetProperty("lastUsed", out var last) && last.TryGetDouble(out var l))
                {
                    usage._records[property.Name] = new UsageRecord(Math.Clamp(c, 0, MaxCount), (long)l);
                }
            }

            while (usage._records.Count > MaxRows)
            {
                usage._records.Remove(usage._records.MinBy(kv => kv.Value.LastUsedUnix).Key);
            }
        }
        catch (JsonException)
        {
        }

        return usage;
    }
}

/// <summary>
/// Session-only query memory (spec 06 §6.4.2): which rows ran for which
/// typed prefixes. A tie-breaker only (0–3). Never written anywhere.
/// </summary>
public sealed class CommandBarQueryMemory
{
    private const int MaxPrefixLength = 12;
    private const int MaxRowsPerPrefix = 4;
    private const int MaxPrefixes = 60;

    private readonly Dictionary<string, Dictionary<string, (int Count, long Step)>> _prefixes = new(StringComparer.Ordinal);
    private long _step;

    public void Record(string query, string rowId)
    {
        var folded = TextFold.ForCommand(query.TrimStart(':'));
        if (folded.Length == 0)
        {
            return;
        }

        _step++;
        foreach (var prefix in Prefixes(folded))
        {
            if (!_prefixes.TryGetValue(prefix, out var rows))
            {
                _prefixes[prefix] = rows = new Dictionary<string, (int, long)>(StringComparer.Ordinal);
            }

            var count = rows.TryGetValue(rowId, out var existing) ? existing.Count : 0;
            rows[rowId] = (Math.Min(count + 1, 99), _step);
            while (rows.Count > MaxRowsPerPrefix)
            {
                rows.Remove(rows.OrderBy(kv => kv.Value.Count).ThenBy(kv => kv.Value.Step).First().Key);
            }
        }

        while (_prefixes.Count > MaxPrefixes)
        {
            _prefixes.Remove(_prefixes.MinBy(kv => kv.Value.Values.Max(v => v.Step)).Key);
        }
    }

    public int Boost(string foldedQuery, string rowId) =>
        _prefixes.TryGetValue(foldedQuery, out var rows) && rows.TryGetValue(rowId, out var use) ? Math.Min(use.Count, 3) : 0;

    public void Forget(string rowId)
    {
        foreach (var rows in _prefixes.Values)
        {
            rows.Remove(rowId);
        }
    }

    public void Clear() => _prefixes.Clear();

    private static IEnumerable<string> Prefixes(string folded)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var length = 1; length <= Math.Min(MaxPrefixLength, folded.Length); length++)
        {
            var prefix = folded[..length].Trim();
            if (prefix.Length > 0 && seen.Add(prefix))
            {
                yield return prefix;
            }
        }
    }
}

/// <summary>
/// Session-only query habits (spec 06 §6.4.3): rows chosen for typed
/// prefixes, keyed by HMAC digests with a random per-process key so even
/// memory dumps hold no plain queries. Lands in ranking priority.
/// </summary>
public sealed class CommandBarQueryHabits
{
    private const int MaxQueryLength = 24;
    private const int MaxChoices = 4;
    private const int MaxDigests = 320;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, Dictionary<string, UsageRecord>> _digests = new(StringComparer.Ordinal);

    /// <summary>(digest, specificity) for every prefix of the folded query cut to 24 characters.</summary>
    public IReadOnlyList<(string Digest, int Specificity)> Prepare(string foldedQuery)
    {
        var cut = foldedQuery.Length > MaxQueryLength ? foldedQuery[..MaxQueryLength] : foldedQuery;
        var result = new List<(string, int)>(cut.Length);
        using var hmac = new HMACSHA256(_key);
        for (var length = 1; length <= cut.Length; length++)
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(cut[..length]));
            result.Add((Convert.ToHexString(hash, 0, 12), length));
        }

        return result;
    }

    public void Record(string query, string rowId, DateTimeOffset now)
    {
        var folded = TextFold.ForCommand(query.TrimStart(':'));
        if (folded.Length == 0)
        {
            return;
        }

        foreach (var (digest, _) in Prepare(folded))
        {
            if (!_digests.TryGetValue(digest, out var choices))
            {
                _digests[digest] = choices = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
            }

            var count = choices.TryGetValue(rowId, out var existing) ? existing.Count : 0;
            choices[rowId] = new UsageRecord(Math.Min(count + 1, CommandBarUsage.MaxCount), now.ToUnixTimeSeconds());
            while (choices.Count > MaxChoices)
            {
                choices.Remove(choices.MinBy(kv => kv.Value.LastUsedUnix).Key);
            }
        }

        while (_digests.Count > MaxDigests)
        {
            _digests.Remove(_digests.MinBy(kv => kv.Value.Values.Max(v => v.LastUsedUnix)).Key);
        }
    }

    /// <summary>max over prefixes of min(usageBoost × 5 + specificity × 6, 720).</summary>
    public int Boost(IReadOnlyList<(string Digest, int Specificity)> prepared, string rowId, DateTimeOffset now)
    {
        var best = 0;
        foreach (var (digest, specificity) in prepared)
        {
            if (_digests.TryGetValue(digest, out var choices) && choices.TryGetValue(rowId, out var use))
            {
                best = Math.Max(best, Math.Min((CommandBarUsage.Boost(use, now) * 5) + (specificity * 6), 720));
            }
        }

        return best;
    }

    public void Forget(string rowId)
    {
        foreach (var choices in _digests.Values)
        {
            choices.Remove(rowId);
        }
    }

    public void Clear() => _digests.Clear();
}
