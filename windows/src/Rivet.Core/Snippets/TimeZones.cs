// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Clipboard;

namespace Rivet.Core.Snippets;

/// <summary>
/// IANA time zones for snippet tokens (<c>{{date-tz(Asia/Tokyo):…}}</c>), the
/// date/time builder's search and the Command Bar's "time in …" answers
/// (spec 06 §3.6.4, §6.12.3). Raw UTC offsets are never accepted.
/// </summary>
public static class TimeZones
{
    /// <summary>Foundation's abbreviation table (used by the builder search).</summary>
    public static readonly IReadOnlyDictionary<string, string> Abbreviations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ADT"] = "America/Halifax", ["AST"] = "America/Halifax",
        ["AKDT"] = "America/Juneau", ["AKST"] = "America/Juneau",
        ["ART"] = "America/Argentina/Buenos_Aires",
        ["BDT"] = "Asia/Dhaka",
        ["BRST"] = "America/Sao_Paulo", ["BRT"] = "America/Sao_Paulo",
        ["BST"] = "Europe/London",
        ["CAT"] = "Africa/Harare",
        ["CDT"] = "America/Chicago", ["CST"] = "America/Chicago",
        ["CEST"] = "Europe/Paris", ["CET"] = "Europe/Paris",
        ["CLST"] = "America/Santiago", ["CLT"] = "America/Santiago",
        ["COT"] = "America/Bogota",
        ["EAT"] = "Africa/Addis_Ababa",
        ["EDT"] = "America/New_York", ["EST"] = "America/New_York",
        ["EEST"] = "Europe/Athens", ["EET"] = "Europe/Athens",
        ["GMT"] = "GMT",
        ["GST"] = "Asia/Dubai",
        ["HKT"] = "Asia/Hong_Kong",
        ["HST"] = "Pacific/Honolulu",
        ["ICT"] = "Asia/Bangkok",
        ["IRST"] = "Asia/Tehran",
        ["IST"] = "Asia/Kolkata",
        ["JST"] = "Asia/Tokyo",
        ["KST"] = "Asia/Seoul",
        ["MDT"] = "America/Denver",
        ["MSD"] = "Europe/Moscow", ["MSK"] = "Europe/Moscow",
        ["MST"] = "America/Phoenix",
        ["NDT"] = "America/St_Johns", ["NST"] = "America/St_Johns",
        ["NZDT"] = "Pacific/Auckland", ["NZST"] = "Pacific/Auckland",
        ["PDT"] = "America/Los_Angeles", ["PST"] = "America/Los_Angeles",
        ["PET"] = "America/Lima",
        ["PHT"] = "Asia/Manila",
        ["PKT"] = "Asia/Karachi",
        ["SGT"] = "Asia/Singapore",
        ["TRT"] = "Europe/Istanbul",
        ["UTC"] = "UTC",
        ["WAT"] = "Africa/Lagos",
        ["WEST"] = "Europe/Lisbon", ["WET"] = "Europe/Lisbon",
        ["WIT"] = "Asia/Jakarta",
    };

    private static readonly Lazy<IReadOnlyList<string>> KnownIds = new(() =>
        TimeZoneData.Identifiers.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static readonly Dictionary<string, TimeZoneInfo?> Cache = new(StringComparer.Ordinal);

    /// <summary>The known IANA identifiers, sorted.</summary>
    public static IReadOnlyList<string> Identifiers => KnownIds.Value;

    /// <summary>
    /// Resolves an IANA identifier (case-sensitive, as ICU does) to a zone the
    /// OS knows; null when unknown. Offsets like "+02:00" are never accepted.
    /// </summary>
    public static TimeZoneInfo? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith('+') || id.StartsWith('-') || char.IsDigit(id[0]))
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var cached))
            {
                return cached;
            }
        }

        TimeZoneInfo? zone = null;
        if (id is "UTC" or "GMT")
        {
            zone = TimeZoneInfo.Utc;
        }
        else if (Identifiers.Contains(id, StringComparer.Ordinal) || id.Contains('/'))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                zone = null;
            }
        }

        lock (Cache)
        {
            Cache[id] = zone;
        }

        return zone;
    }

    /// <summary>The city part of an identifier: "America/New_York" → "New York".</summary>
    public static string CityName(string id)
    {
        var slash = id.LastIndexOf('/');
        return (slash >= 0 ? id[(slash + 1)..] : id).Replace('_', ' ');
    }

    /// <summary>
    /// Builder search: identifiers containing the query (lower case, <c>_</c>
    /// as space) plus identifiers of matching abbreviations, ranked: exact id,
    /// city equals, city starts with, id starts with, other; then alphabetical.
    /// </summary>
    public static IReadOnlyList<string> Search(string query)
    {
        var q = Normalize(query);
        if (q.Length == 0)
        {
            return Identifiers;
        }

        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in Identifiers)
        {
            if (Normalize(id).Contains(q, StringComparison.Ordinal))
            {
                matches.Add(id);
            }
        }

        foreach (var (abbreviation, id) in Abbreviations)
        {
            if (abbreviation.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(id);
            }
        }

        return matches
            .OrderBy(id => Rank(id, q))
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The zone a query stands for: an exact identifier, an abbreviation, or the only match.</summary>
    public static string? Resolve(string query)
    {
        var trimmed = query.Trim();
        if (Identifiers.Contains(trimmed, StringComparer.Ordinal))
        {
            return trimmed;
        }

        if (Abbreviations.TryGetValue(trimmed, out var id))
        {
            return id;
        }

        var matches = Search(trimmed);
        return matches.Count == 1 ? matches[0] : null;
    }

    private static int Rank(string id, string q)
    {
        var normalizedId = Normalize(id);
        var city = Normalize(CityName(id));
        if (normalizedId == q)
        {
            return 0;
        }

        if (city == q)
        {
            return 1;
        }

        if (city.StartsWith(q, StringComparison.Ordinal))
        {
            return 2;
        }

        return normalizedId.StartsWith(q, StringComparison.Ordinal) ? 3 : 4;
    }

    private static string Normalize(string text) => text.Trim().ToLowerInvariant().Replace('_', ' ');

    /// <summary>
    /// City → identifier for "time in …": every identifier's city (folded,
    /// first in sorted order wins) plus localized aliases whose target exists.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Cities => CityTable.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> CityTable = new(() =>
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in Identifiers.Where(i => i.Contains('/')))
        {
            table.TryAdd(TextFold.ForCommand(CityName(id)), id);
        }

        (string Alias, string City)[] aliases =
        [
            ("londres", "London"), ("lisboa", "Lisbon"), ("roma", "Rome"), ("moscou", "Moscow"), ("moscovo", "Moscow"),
            ("москва", "Moscow"), ("nova york", "New_York"), ("nueva york", "New_York"), ("nova iorque", "New_York"),
            ("cidade do mexico", "Mexico_City"), ("ciudad de mexico", "Mexico_City"), ("pequim", "Beijing"), ("pequin", "Beijing"),
            ("toquio", "Tokyo"), ("tokio", "Tokyo"), ("genebra", "Geneva"), ("viena", "Vienna"), ("copenhague", "Copenhagen"),
            ("praga", "Prague"), ("atenas", "Athens"), ("varsovia", "Warsaw"), ("bruxelas", "Brussels"), ("zurique", "Zurich"),
            ("munique", "Munich"), ("colonia", "Cologne"), ("estocolmo", "Stockholm"), ("hamburgo", "Hamburg"),
        ];
        foreach (var (alias, city) in aliases)
        {
            if (table.TryGetValue(TextFold.ForCommand(city.Replace('_', ' ')), out var target))
            {
                table.TryAdd(TextFold.ForCommand(alias), target);
            }
        }

        return table;
    });
}
