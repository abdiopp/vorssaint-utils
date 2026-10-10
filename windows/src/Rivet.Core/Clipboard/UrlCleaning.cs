// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Rivet.Core.Clipboard;

/// <summary>The user's rule changes, stored as a difference from the built-ins (spec 06 §6.11.2).</summary>
public sealed record UrlCleanerRules
{
    public static UrlCleanerRules None { get; } = new();

    /// <summary>Names the user added, by site ("" = all sites).</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Added { get; init; } = new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>Names switched off, by site ("" = all sites; <c>utm_*</c> stands for the prefix).</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Disabled { get; init; } = new Dictionary<string, IReadOnlySet<string>>();

    public IReadOnlySet<string> AddedFor(string site) => Added.TryGetValue(site, out var set) ? set : EmptySet;

    public IReadOnlySet<string> DisabledFor(string site) => Disabled.TryGetValue(site, out var set) ? set : EmptySet;

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    /// <summary>Reads the three stored strings.</summary>
    public static UrlCleanerRules FromStorage(string customParameters, string siteParameters, string disabledParameters)
    {
        var added = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in UrlCleaning.SplitList(customParameters))
        {
            Add(added, string.Empty, name.ToLowerInvariant());
        }

        foreach (var (site, name) in UrlCleaning.ParseTokens(siteParameters))
        {
            Add(added, site, name);
        }

        var disabled = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (site, name) in UrlCleaning.ParseTokens(disabledParameters))
        {
            Add(disabled, site, name);
        }

        return new UrlCleanerRules
        {
            Added = added.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
            Disabled = disabled.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>The global custom list, written as sorted names joined by ", ".</summary>
    public string CustomParametersStorage() =>
        string.Join(", ", AddedFor(string.Empty).OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>Site additions as <c>site|name</c> tokens, sorted by site then name.</summary>
    public string SiteParametersStorage() =>
        UrlCleaning.JoinTokens(Added.Where(kv => kv.Key.Length > 0));

    public string DisabledParametersStorage() => UrlCleaning.JoinTokens(Disabled);

    private static void Add(Dictionary<string, HashSet<string>> map, string site, string name)
    {
        if (!map.TryGetValue(site, out var set))
        {
            map[site] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        set.Add(name);
    }
}

public sealed record UrlCleanResult(string Url, IReadOnlyList<string> Removed);

public enum UrlCleanOutcomeKind
{
    NotAUrl,
    Unchanged,
    Rewritten,
    Removed,
}

/// <summary>One message shared by every surface (spec 06 §3.5.1).</summary>
public sealed record UrlCleanOutcome(UrlCleanOutcomeKind Kind, string? Url, IReadOnlyList<string> Removed)
{
    public static UrlCleanOutcome From(string input, UrlCleanResult? result)
    {
        if (result is null)
        {
            return new UrlCleanOutcome(UrlCleanOutcomeKind.NotAUrl, null, []);
        }

        if (result.Removed.Count > 0)
        {
            return new UrlCleanOutcome(UrlCleanOutcomeKind.Removed, result.Url, result.Removed);
        }

        return result.Url == input.Trim()
            ? new UrlCleanOutcome(UrlCleanOutcomeKind.Unchanged, result.Url, [])
            : new UrlCleanOutcome(UrlCleanOutcomeKind.Rewritten, result.Url, []);
    }
}

/// <summary>
/// Removes tracking parameters from a link by deleting whole <c>name=value</c>
/// pairs from the original text, never re-encoding what survives (spec 06 §6.11).
/// </summary>
public static class UrlCleaning
{
    public const string AllSites = "";
    public const string UtmWildcard = "utm_*";
    private const string UtmPrefix = "utm_";

    /// <summary>31 global names (the utm_ ones are covered by the prefix rule).</summary>
    public static readonly IReadOnlyList<string> TrackedParameters =
    [
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "utm_id", "utm_name", "utm_reader", "utm_viz_id", "utm_pubreferrer",
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "yclid",
        "mc_cid", "mc_eid", "igshid", "twclid", "ttclid", "li_fat_id",
        "mkt_tok", "_hsenc", "_hsmi", "__twitter_impression",
        "fb_action_ids", "fb_action_types", "fb_source", "mibextid",
    ];

    /// <summary>
    /// Trackers only one site uses, keyed by host (matches the host or any
    /// subdomain). <c>si</c> is a share token on YouTube but a real parameter
    /// elsewhere, and <c>t</c> is a tracker on X but the playback position on
    /// YouTube, which is why these cannot be global. Reddit's branch.io fields
    /// start with a literal <c>$</c>; names are matched after percent-decoding.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> HostParameters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["youtube.com"] = ["si", "pp", "feature", "kw"],
        ["youtu.be"] = ["si", "pp", "feature", "kw"],
        ["twitter.com"] = ["s", "t", "cn", "src", "refsrc", "ref_src", "ref_url"],
        ["x.com"] = ["s", "t", "cn", "src", "refsrc", "ref_src", "ref_url"],
        ["instagram.com"] = ["igsh"],
        ["spotify.com"] = ["si"],
        ["reddit.com"] =
        [
            "correlation_id", "ref_campaign", "ref_source", "rdt", "share_id",
            "_branch_match_id", "$deep_link", "$3p", "$original_url",
        ],
        ["tiktok.com"] =
        [
            "u_code", "preview_pb", "_d", "_t", "_r", "timestamp", "user_id",
            "share_app_name", "share_iid",
        ],
        ["bilibili.com"] =
        [
            "spm_id_from", "from_spmid", "from_source", "share_source", "share_from",
            "share_medium", "share_plat", "share_tag", "share_session_id", "msource",
            "refer_from", "seid", "unique_k", "vd_source", "plat_id", "buvid", "bbid",
            "up_id", "is_story_h5", "timestamp", "ts", "visit_id", "session_id",
            "broadcast_type", "is_room_feed",
        ],
        ["xiaohongshu.com"] =
        [
            "xhsshare", "author_share", "xsec_source", "share_from_user_hidden",
            "shareredid", "share_id", "exsource", "app_version", "app_platform",
            "apptime", "appuid",
        ],
    };

    /// <summary>Cleans a single http(s) link; null when the text is not exactly one link.</summary>
    public static UrlCleanResult? Clean(string text, UrlCleanerRules? rules = null)
    {
        rules ??= UrlCleanerRules.None;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            return null;
        }

        var fragmentStart = trimmed.IndexOf('#');
        var queryEnd = fragmentStart >= 0 ? fragmentStart : trimmed.Length;
        var queryStart = trimmed.IndexOf('?', 0, queryEnd);
        if (queryStart < 0)
        {
            return new UrlCleanResult(trimmed, []);
        }

        var matcher = Matcher(uri.Host.ToLowerInvariant(), rules);
        var query = trimmed[(queryStart + 1)..queryEnd];
        var pairs = query.Split('&');
        var kept = new List<string>(pairs.Length);
        var removed = new List<string>();
        foreach (var pair in pairs)
        {
            var equals = pair.IndexOf('=');
            var rawName = equals >= 0 ? pair[..equals] : pair;
            var name = rawName.Contains('%') ? PercentDecode(rawName) ?? rawName : rawName;
            if (matcher(name))
            {
                if (!removed.Contains(name, StringComparer.Ordinal))
                {
                    removed.Add(name);
                }
            }
            else
            {
                kept.Add(pair);
            }
        }

        if (removed.Count == 0)
        {
            return new UrlCleanResult(trimmed, []);
        }

        var builder = new StringBuilder(trimmed.Length);
        builder.Append(trimmed, 0, queryStart);
        if (kept.Count > 0)
        {
            builder.Append('?').Append(string.Join('&', kept));
        }

        builder.Append(trimmed, queryEnd, trimmed.Length - queryEnd);
        return new UrlCleanResult(builder.ToString(), removed);
    }

    /// <summary>The name test for one host, built from the built-ins and the user's difference.</summary>
    public static Func<string, bool> Matcher(string host, UrlCleanerRules rules)
    {
        var globalDisabled = rules.DisabledFor(AllSites);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in TrackedParameters.Where(n => !n.StartsWith(UtmPrefix, StringComparison.Ordinal)))
        {
            if (!globalDisabled.Contains(name))
            {
                names.Add(name);
            }
        }

        foreach (var name in rules.AddedFor(AllSites))
        {
            if (!globalDisabled.Contains(name))
            {
                names.Add(name);
            }
        }

        foreach (var site in SiteKeys(rules).Where(s => s.Length > 0 && HostMatches(host, s)).OrderBy(s => s, StringComparer.Ordinal))
        {
            var disabled = rules.DisabledFor(site);
            var builtIn = HostParameters.TryGetValue(site, out var list) ? list : [];
            foreach (var name in builtIn.Concat(rules.AddedFor(site)))
            {
                if (!disabled.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        var utm = !globalDisabled.Contains(UtmWildcard);
        return name =>
        {
            var lower = name.ToLowerInvariant();
            return names.Contains(lower) || (utm && lower.StartsWith(UtmPrefix, StringComparison.Ordinal));
        };
    }

    public static bool HostMatches(string host, string site) =>
        host == site || host.EndsWith("." + site, StringComparison.Ordinal);

    /// <summary>Every site key known to the rules: built-in, added or switched off.</summary>
    public static IEnumerable<string> SiteKeys(UrlCleanerRules rules) =>
        HostParameters.Keys.Concat(rules.Added.Keys).Concat(rules.Disabled.Keys).Distinct(StringComparer.Ordinal);

    /// <summary>Strict percent-decoding (UTF-8); null when malformed, like Swift's removingPercentEncoding.</summary>
    public static string? PercentDecode(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '%')
            {
                if (i + 2 >= text.Length || !Uri.IsHexDigit(text[i + 1]) || !Uri.IsHexDigit(text[i + 2]))
                {
                    return null;
                }

                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // ── Storage helpers ────────────────────────────────────────────────

    internal static IEnumerable<string> SplitList(string text) =>
        text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary><c>site|name</c> tokens; site and name lower-cased; empty names dropped.</summary>
    internal static IEnumerable<(string Site, string Name)> ParseTokens(string text)
    {
        foreach (var token in SplitList(text))
        {
            var bar = token.IndexOf('|');
            var site = bar >= 0 ? token[..bar].Trim().ToLowerInvariant() : string.Empty;
            var name = (bar >= 0 ? token[(bar + 1)..] : token).Trim().ToLowerInvariant();
            if (name.Length > 0)
            {
                yield return (site, name);
            }
        }
    }

    internal static string JoinTokens(IEnumerable<KeyValuePair<string, IReadOnlySet<string>>> map) =>
        string.Join(',', map
            .SelectMany(kv => kv.Value.Select(name => (Site: kv.Key, Name: name)))
            .OrderBy(t => t.Site, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => $"{t.Site}|{t.Name}"));

    // ── Validation (rules editor) ──────────────────────────────────────

    /// <summary>A parameter name: trimmed, lower case, no whitespace, <c>| , &amp; =</c>; null when invalid.</summary>
    public static string? ValidParameterName(string text)
    {
        var name = text.Trim().ToLowerInvariant();
        return name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c is '|' or ',' or '&' or '=') ? null : name;
    }

    /// <summary>A site key: lower case, scheme and path removed, leading www. dropped, must contain a dot.</summary>
    public static string? ValidSiteKey(string text)
    {
        var site = text.Trim().ToLowerInvariant();
        var scheme = site.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            site = site[(scheme + 3)..];
        }

        var cut = site.IndexOfAny(['/', '?', '#']);
        if (cut >= 0)
        {
            site = site[..cut];
        }

        if (site.StartsWith("www.", StringComparison.Ordinal))
        {
            site = site[4..];
        }

        if (!site.Contains('.') || site.StartsWith('.') || site.EndsWith('.') || site.Any(c => char.IsWhiteSpace(c) || c is '|' or ','))
        {
            return null;
        }

        return site;
    }

    // ── Rules editor model (spec 06 §6.11.5) ───────────────────────────

    public sealed record RuleEntry(string Name, bool IsBuiltIn, bool IsEnabled);

    public sealed record RuleGroup(string Site, IReadOnlyList<RuleEntry> Entries)
    {
        public int EnabledCount => Entries.Count(e => e.IsEnabled);
    }

    public static IReadOnlyList<RuleGroup> RuleGroups(UrlCleanerRules rules)
    {
        var groups = new List<RuleGroup>();
        var globalBuiltIns = new List<string> { UtmWildcard };
        globalBuiltIns.AddRange(TrackedParameters.Where(n => !n.StartsWith(UtmPrefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal));
        groups.Add(Group(AllSites, globalBuiltIns, rules));
        foreach (var site in SiteKeys(rules).Where(s => s.Length > 0).OrderBy(s => s, StringComparer.Ordinal))
        {
            var builtIns = HostParameters.TryGetValue(site, out var list) ? list.OrderBy(n => n, StringComparer.Ordinal).ToList() : [];
            groups.Add(Group(site, builtIns, rules));
        }

        return groups.Where(g => g.Entries.Count > 0).ToList();
    }

    private static RuleGroup Group(string site, List<string> builtIns, UrlCleanerRules rules)
    {
        var disabled = rules.DisabledFor(site);
        var entries = builtIns.Select(n => new RuleEntry(n, true, !disabled.Contains(n))).ToList();
        entries.AddRange(rules.AddedFor(site)
            .Where(n => !builtIns.Contains(n, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new RuleEntry(n, false, !disabled.Contains(n))));
        return new RuleGroup(site, entries);
    }

    // ── Rule edits (return a new rule set) ─────────────────────────────

    public static UrlCleanerRules SetEnabled(UrlCleanerRules rules, string site, string name, bool enabled)
    {
        var disabled = Copy(rules.Disabled);
        Mutate(disabled, site, set =>
        {
            if (enabled)
            {
                set.Remove(name);
            }
            else
            {
                set.Add(name);
            }
        });
        return rules with { Disabled = Freeze(disabled) };
    }

    /// <summary>Adds a user name: clears a switch-off for it, then adds it (global → custom list).</summary>
    public static UrlCleanerRules AddName(UrlCleanerRules rules, string site, string name)
    {
        var disabled = Copy(rules.Disabled);
        Mutate(disabled, site, set => set.Remove(name));
        var added = Copy(rules.Added);
        var isBuiltIn = site.Length == 0
            ? name == UtmWildcard || TrackedParameters.Contains(name)
            : HostParameters.TryGetValue(site, out var list) && list.Contains(name);
        if (!isBuiltIn)
        {
            Mutate(added, site, set => set.Add(name));
        }

        return new UrlCleanerRules { Added = Freeze(added), Disabled = Freeze(disabled) };
    }

    /// <summary>Deletes a user name from both the additions and the switch-offs.</summary>
    public static UrlCleanerRules DeleteName(UrlCleanerRules rules, string site, string name)
    {
        var added = Copy(rules.Added);
        Mutate(added, site, set => set.Remove(name));
        var disabled = Copy(rules.Disabled);
        Mutate(disabled, site, set => set.Remove(name));
        return new UrlCleanerRules { Added = Freeze(added), Disabled = Freeze(disabled) };
    }

    /// <summary>The site switch: off = every listed name switched off; on = the site's switch-offs removed.</summary>
    public static UrlCleanerRules SetSiteEnabled(UrlCleanerRules rules, string site, bool enabled)
    {
        var disabled = Copy(rules.Disabled);
        if (enabled)
        {
            disabled.Remove(site);
        }
        else
        {
            var group = RuleGroups(rules).FirstOrDefault(g => g.Site == site);
            disabled[site] = new HashSet<string>(group?.Entries.Select(e => e.Name) ?? [], StringComparer.Ordinal);
        }

        return rules with { Disabled = Freeze(disabled) };
    }

    private static Dictionary<string, HashSet<string>> Copy(IReadOnlyDictionary<string, IReadOnlySet<string>> map) =>
        map.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static void Mutate(Dictionary<string, HashSet<string>> map, string site, Action<HashSet<string>> change)
    {
        if (!map.TryGetValue(site, out var set))
        {
            map[site] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        change(set);
        if (set.Count == 0)
        {
            map.Remove(site);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Freeze(Dictionary<string, HashSet<string>> map) =>
        map.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal);
}
