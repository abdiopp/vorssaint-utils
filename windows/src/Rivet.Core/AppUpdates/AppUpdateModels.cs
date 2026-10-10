// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Core.Settings;

namespace Rivet.Core.Maintenance.AppUpdates;

/// <summary>Where an update comes from (and how it installs).</summary>
public enum AppUpdateKind
{
    /// <summary>winget package: installs here, silently, with progress.</summary>
    PackageManager,

    /// <summary>Microsoft Store app: the Store installs it.</summary>
    Store,

    /// <summary>Found through the developer's own feed: the app's updater installs it.</summary>
    Online,
}

/// <summary>A row of the App updates list.</summary>
public sealed record AppUpdateRow
{
    /// <summary>Stable row id: <c>packageManager:&lt;id&gt;</c>, <c>appStore:&lt;id&gt;</c> or <c>onlineCatalog:&lt;key&gt;</c>.</summary>
    public required string Id { get; init; }

    public required AppUpdateKind Kind { get; init; }

    public required string Name { get; init; }

    public required string InstalledVersion { get; init; }

    public required string LatestVersion { get; init; }

    /// <summary>Identity used by update rules: the winget/Store package id, or the app's registry key.</summary>
    public required string RuleKey { get; init; }

    /// <summary>winget or Store package id.</summary>
    public string? PackageId { get; init; }

    /// <summary>winget source name ("winget", "msstore").</summary>
    public string? Source { get; init; }

    /// <summary>Online rows: the program to open so its own updater can finish.</summary>
    public string? AppPath { get; init; }

    /// <summary>winget shortened the id and it could not be completed: the row cannot be updated from here.</summary>
    public bool IdUnresolved { get; init; }

    /// <summary>Package and Store rows can be ticked; online rows open the app instead.</summary>
    public bool IsSelectable => Kind != AppUpdateKind.Online && !IdUnresolved;

    public static string PackageRowId(string id) => "packageManager:" + id;

    public static string StoreRowId(string id) => "appStore:" + id;

    public static string OnlineRowId(string key) => "onlineCatalog:" + key;
}

/// <summary>
/// One rule per app (<c>appUpdatesRules</c>, JSON shape kept from macOS:
/// <c>{bundleID, name, version?}</c>; on Windows <c>bundleID</c> holds the
/// winget/Store package id or the app's key). With a version: skip exactly
/// that release. Without: don't check this app.
/// </summary>
public sealed record UpdateRule
{
    [JsonPropertyName("bundleID")]
    public required string Key { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    [JsonIgnore]
    public bool IsExclusion => Version is null;
}

/// <summary>Decoding, encoding and applying update rules.</summary>
public static class UpdateRules
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>
    /// Drops empty keys or names, keys with whitespace or '/', uncomparable
    /// versions (empty, "latest") and duplicate keys (the first wins).
    /// </summary>
    public static IReadOnlyList<UpdateRule> Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<RawRule>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<RawRule>>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<UpdateRule>();
        foreach (var rule in raw ?? [])
        {
            var key = rule.BundleID?.Trim();
            var name = rule.Name?.Trim();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name) || key.Any(char.IsWhiteSpace) || key.Contains('/') || key.Length > 512)
            {
                continue;
            }

            if (rule.Version is not null && VersionComparer.IsUncomparable(rule.Version))
            {
                continue;
            }

            if (!seen.Add(key))
            {
                continue;
            }

            rules.Add(new UpdateRule { Key = key, Name = name, Version = rule.Version?.Trim() });
        }

        return rules;
    }

    public static string Encode(IEnumerable<UpdateRule> rules) => JsonSerializer.Serialize(rules.ToList(), Options);

    /// <summary>A new skip replaces the app's previous rule.</summary>
    public static IReadOnlyList<UpdateRule> WithRule(IReadOnlyList<UpdateRule> rules, UpdateRule rule) =>
        rules.Where(r => r.Key != rule.Key).Append(rule).ToList();

    public static IReadOnlyList<UpdateRule> Without(IReadOnlyList<UpdateRule> rules, string key) =>
        rules.Where(r => r.Key != key).ToList();

    /// <summary>Excluded keys are removed before any source is asked.</summary>
    public static bool IsExcluded(IReadOnlyList<UpdateRule> rules, string key) =>
        rules.Any(r => r.IsExclusion && r.Key == key);

    /// <summary>A skip hides only the release whose version core equals the skipped one; newer releases show again.</summary>
    public static bool IsSkipped(IReadOnlyList<UpdateRule> rules, AppUpdateRow row) =>
        rules.Any(r => r.Version is { } version && r.Key == row.RuleKey && VersionComparer.Compare(VersionComparer.Core(row.LatestVersion), VersionComparer.Core(version)) == 0);

    public static IReadOnlyList<AppUpdateRow> Apply(IReadOnlyList<UpdateRule> rules, IEnumerable<AppUpdateRow> rows) =>
        rows.Where(r => !IsExcluded(rules, r.RuleKey) && !IsSkipped(rules, r)).ToList();

    private sealed class RawRule
    {
        [JsonPropertyName("bundleID")]
        public string? BundleID { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}

/// <summary>Merging and selection rules of the list (spec §3.3.7).</summary>
public static class AppUpdateList
{
    /// <summary>Dedupe by id; package rows, then Store, then online; alphabetical within each.</summary>
    public static IReadOnlyList<AppUpdateRow> Merge(IEnumerable<AppUpdateRow> rows) =>
        rows.GroupBy(r => r.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Rows that disappeared leave the selection; new selectable rows arrive selected.</summary>
    public static HashSet<string> Reconcile(IReadOnlySet<string> selection, IReadOnlyList<AppUpdateRow> previous, IReadOnlyList<AppUpdateRow> current)
    {
        var known = previous.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in current.Where(r => r.IsSelectable))
        {
            if (selection.Contains(row.Id) || !known.Contains(row.Id))
            {
                result.Add(row.Id);
            }
        }

        return result;
    }

    /// <summary>Compact list height: rows × 40 + gaps × 5 with 1–4 rows, so it ends on a whole row.</summary>
    public static double CompactHeight(int count)
    {
        var rows = Math.Clamp(count, 1, 4);
        return (rows * 40) + ((rows - 1) * 5);
    }
}

/// <summary>App updates preferences (keys match the macOS app; the sources are reinterpreted for Windows).</summary>
public static class AppUpdatesSettings
{
    public static readonly Setting<string> CheckFrequency =
        new("appUpdatesCheckFrequency", "off", Sanitize.OneOfStrings("off", "off", "daily", "weekly"));

    /// <summary>macOS "Include Homebrew apps": on Windows, apps winget can update.</summary>
    public static readonly Setting<bool> IncludePackageManager = new("appUpdatesIncludeHomebrewApps", true);

    /// <summary>macOS "Include apps from the App Store": on Windows, Microsoft Store apps.</summary>
    public static readonly Setting<bool> IncludeStore = new("appUpdatesIncludeAppStore", true);

    /// <summary>Other installed apps through their developers' update feeds.</summary>
    public static readonly Setting<bool> IncludeOnline = new("appUpdatesIncludeOnlineCatalog", true);

    public static readonly Setting<bool> Notify = new("appUpdatesNotify", true);

    public static readonly Setting<string> Rules = new("appUpdatesRules", "[]");

    public static readonly Setting<double> LastCheck = new("appUpdatesLastCheck", 0d, machineState: true);

    public static readonly Setting<int> LastCount = new("appUpdatesLastCount", 0, machineState: true);

    public static readonly Setting<List<string>> NotifiedIds = new("appUpdatesNotifiedIDs", [], machineState: true);
}
