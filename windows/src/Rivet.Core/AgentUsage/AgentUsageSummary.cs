// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Agents;

public enum AgentPeriod
{
    Today,
    Week,
    Month,
}

/// <summary>Totals over a set of records.</summary>
public sealed record UsageTotals(long Tokens, double Cost, double Savings, long Requests, int Unpriced, long CacheRead, long Prompt)
{
    public static UsageTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public bool AllPriced => Unpriced == 0;

    /// <summary>Share of the prompt read from the cache.</summary>
    public double CacheRate => Prompt > 0 ? (double)CacheRead / Prompt : 0;

    public UsageTotals Add(UsageRecord record) => new(
        Tokens + record.Tokens.Total,
        Cost + (record.Cost ?? 0),
        Savings + record.Savings,
        Requests + record.Requests,
        Unpriced + (record.Cost is null && (record.Tokens.Total > 0 || !record.IsAggregate) ? 1 : 0),
        CacheRead + record.Tokens.CacheRead,
        Prompt + record.Tokens.Prompt);
}

public sealed record NamedTotals(string Key, string Name, AgentProvider? Provider, UsageTotals Totals);

/// <summary>Totals for Today, 7 days or 30 days (calendar days in the local time zone).</summary>
public sealed record PeriodSummary(
    AgentPeriod Period,
    UsageTotals Totals,
    IReadOnlyDictionary<AgentProvider, UsageTotals> ByProvider,
    IReadOnlyList<NamedTotals> Models,
    IReadOnlyList<NamedTotals> Projects);

/// <summary>One bar of the trend chart (an hour of today, or a day).</summary>
public sealed record UsageBucket(DateTime Start, IReadOnlyDictionary<AgentProvider, UsageTotals> ByProvider)
{
    public UsageTotals Total => ByProvider.Values.Aggregate(UsageTotals.Empty, (a, b) => a with
    {
        Tokens = a.Tokens + b.Tokens, Cost = a.Cost + b.Cost, Savings = a.Savings + b.Savings, Requests = a.Requests + b.Requests,
        Unpriced = a.Unpriced + b.Unpriced, CacheRead = a.CacheRead + b.CacheRead, Prompt = a.Prompt + b.Prompt,
    });
}

/// <summary>The 13-week activity heatmap figures.</summary>
public sealed record ActivityStats(UsageTotals Total, int ActiveDays, DateTime? BusiestDay, int Streak, IReadOnlyList<double> Levels);

/// <summary>
/// Builds the summaries from the records (spec 07 §3.8.10). Recomputed from
/// scratch on every publish, so it always equals a full recompute.
/// </summary>
public static class AgentUsageSummary
{
    public const int DayBuckets = 91;

    public static DateTime LocalDay(DateTimeOffset time, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(time, zone).Date;

    public static PeriodSummary Period(IReadOnlyCollection<UsageRecord> records, AgentPeriod period, DateTimeOffset now, TimeZoneInfo zone, ISet<AgentProvider>? providers = null)
    {
        var today = LocalDay(now, zone);
        var first = period switch
        {
            AgentPeriod.Week => today.AddDays(-6),
            AgentPeriod.Month => today.AddDays(-29),
            _ => today,
        };
        var totals = UsageTotals.Empty;
        var byProvider = new Dictionary<AgentProvider, UsageTotals>();
        var models = new Dictionary<string, (string Name, AgentProvider Provider, UsageTotals Totals)>(StringComparer.Ordinal);
        var projects = new Dictionary<string, UsageTotals>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (providers is not null && !providers.Contains(record.Provider))
            {
                continue;
            }

            var day = LocalDay(record.Date, zone);
            if (day < first || day > today || record.Date > now)
            {
                continue;
            }

            totals = totals.Add(record);
            byProvider[record.Provider] = byProvider.GetValueOrDefault(record.Provider, UsageTotals.Empty).Add(record);
            var name = AgentFormat.ModelDisplayName(record.Model);
            var modelKey = $"{record.Provider.Id()}:{name}";
            var existing = models.TryGetValue(modelKey, out var found) ? found.Totals : UsageTotals.Empty;
            models[modelKey] = (name, record.Provider, existing.Add(record));
            if (record.Project.Length > 0)
            {
                projects[record.Project] = projects.GetValueOrDefault(record.Project, UsageTotals.Empty).Add(record);
            }
        }

        // Sorted by cost when everything in the period is priced, else by tokens.
        var byCost = totals.AllPriced;
        return new PeriodSummary(
            period,
            totals,
            byProvider,
            models.Select(kv => new NamedTotals(kv.Key, kv.Value.Name, kv.Value.Provider, kv.Value.Totals)).OrderByDescending(m => byCost ? m.Totals.Cost : m.Totals.Tokens).ToList(),
            projects.Select(kv => new NamedTotals(kv.Key, kv.Key, null, kv.Value)).OrderByDescending(p => byCost ? p.Totals.Cost : p.Totals.Tokens).ToList());
    }

    /// <summary>24 hourly buckets from local midnight.</summary>
    public static IReadOnlyList<UsageBucket> Hours(IReadOnlyCollection<UsageRecord> records, DateTimeOffset now, TimeZoneInfo zone, ISet<AgentProvider>? providers = null)
    {
        var today = LocalDay(now, zone);
        var buckets = Enumerable.Range(0, 24).Select(_ => new Dictionary<AgentProvider, UsageTotals>()).ToArray();
        foreach (var record in records)
        {
            if ((providers is not null && !providers.Contains(record.Provider)) || record.Date > now)
            {
                continue;
            }

            var local = TimeZoneInfo.ConvertTime(record.Date, zone);
            if (local.Date != today)
            {
                continue;
            }

            var bucket = buckets[local.Hour];
            bucket[record.Provider] = bucket.GetValueOrDefault(record.Provider, UsageTotals.Empty).Add(record);
        }

        return buckets.Select((b, i) => new UsageBucket(today.AddHours(i), b)).ToList();
    }

    /// <summary>Day buckets ending today (91 for the heatmap; the trend uses the last 7 or 30).</summary>
    public static IReadOnlyList<UsageBucket> Days(IReadOnlyCollection<UsageRecord> records, DateTimeOffset now, TimeZoneInfo zone, int count = DayBuckets, ISet<AgentProvider>? providers = null)
    {
        var today = LocalDay(now, zone);
        var first = today.AddDays(-(count - 1));
        var buckets = Enumerable.Range(0, count).Select(_ => new Dictionary<AgentProvider, UsageTotals>()).ToArray();
        foreach (var record in records)
        {
            if ((providers is not null && !providers.Contains(record.Provider)) || record.Date > now)
            {
                continue;
            }

            var day = LocalDay(record.Date, zone);
            if (day < first || day > today)
            {
                continue;
            }

            var bucket = buckets[(day - first).Days];
            bucket[record.Provider] = bucket.GetValueOrDefault(record.Provider, UsageTotals.Empty).Add(record);
        }

        return buckets.Select((b, i) => new UsageBucket(first.AddDays(i), b)).ToList();
    }

    /// <summary>
    /// Heatmap figures for the 91 days: total, active days, busiest day, the
    /// current streak, and a level per day by quartiles of active days
    /// (0.28 / 0.45 / 0.65 / 0.9; empty days 0.07).
    /// </summary>
    public static ActivityStats Activity(IReadOnlyList<UsageBucket> days)
    {
        var allPriced = days.All(d => d.Total.AllPriced);
        double Weight(UsageBucket d) => allPriced ? d.Total.Cost : d.Total.Tokens;
        var active = days.Where(d => d.Total.Requests > 0 || d.Total.Tokens > 0).ToList();
        var weights = active.Select(Weight).OrderBy(w => w).ToList();
        double Quartile(double q) => weights.Count == 0 ? 0 : weights[(int)Math.Clamp(Math.Ceiling(q * weights.Count) - 1, 0, weights.Count - 1)];
        var q1 = Quartile(0.25);
        var q2 = Quartile(0.5);
        var q3 = Quartile(0.75);
        var levels = days.Select(d =>
        {
            if (d.Total.Requests == 0 && d.Total.Tokens == 0)
            {
                return 0.07;
            }

            var w = Weight(d);
            return w <= q1 ? 0.28 : w <= q2 ? 0.45 : w <= q3 ? 0.65 : 0.9;
        }).ToList();

        var streak = 0;
        for (var i = days.Count - 1; i >= 0 && (days[i].Total.Requests > 0 || days[i].Total.Tokens > 0); i--)
        {
            streak++;
        }

        var total = days.Aggregate(UsageTotals.Empty, (acc, d) => acc with
        {
            Tokens = acc.Tokens + d.Total.Tokens, Cost = acc.Cost + d.Total.Cost, Savings = acc.Savings + d.Total.Savings,
            Requests = acc.Requests + d.Total.Requests, Unpriced = acc.Unpriced + d.Total.Unpriced,
            CacheRead = acc.CacheRead + d.Total.CacheRead, Prompt = acc.Prompt + d.Total.Prompt,
        });
        var busiest = active.OrderByDescending(Weight).FirstOrDefault()?.Start;
        return new ActivityStats(total, active.Count, busiest, streak, levels);
    }

    /// <summary>The locale's first weekday, for heatmap columns.</summary>
    public static DayOfWeek FirstDayOfWeek(CultureInfo? culture = null) => (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.FirstDayOfWeek;
}

/// <summary>How fresh the Claude app's limit reading is (settings status).</summary>
public enum ClaudeLimitsStatus
{
    /// <summary>No reading and no Claude app: the 5-hour session is estimated.</summary>
    None,
    Fresh,
    Stale,
}

/// <summary>What the UI shows for one agent.</summary>
public sealed record AgentProviderStatus
{
    public required AgentProvider Provider { get; init; }

    public bool Enabled { get; init; }

    /// <summary>Its log location exists.</summary>
    public bool Found { get; init; }

    /// <summary>It has records, limits or a live turn.</summary>
    public bool Seen { get; init; }

    public AgentPlan? Plan { get; init; }

    /// <summary>Current windows (renewals in the past show 0 %), session first.</summary>
    public IReadOnlyList<LimitWindow> Windows { get; init; } = [];

    public LimitSource? LimitsSource { get; init; }

    public DateTimeOffset? LimitsObservedAt { get; init; }

    public DateTimeOffset? LastActivity { get; init; }

    public bool Working { get; init; }

    /// <summary>Claude without an app reading: the estimated 5-hour block.</summary>
    public ClaudeSessionEstimate? Estimate { get; init; }

    /// <summary>Where the logs are read from (for Settings).</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];
}

/// <summary>A task in progress.</summary>
public sealed record LiveTurn(AgentProvider Provider, string Project, string Model, DateTimeOffset Started, long OutputTokens, double Cost);

/// <summary>Everything the AI agents views show, published by the engine.</summary>
public sealed record AgentUsageSnapshot
{
    public static AgentUsageSnapshot Loading { get; } = new() { Loaded = false };

    public bool Loaded { get; init; }

    public DateTimeOffset At { get; init; }

    public IReadOnlyList<AgentProviderStatus> Providers { get; init; } = [];

    public IReadOnlyList<LiveTurn> Live { get; init; } = [];

    public PeriodSummary? Today { get; init; }

    public PeriodSummary? Week { get; init; }

    public PeriodSummary? Month { get; init; }

    public IReadOnlyList<UsageBucket> Hours { get; init; } = [];

    public IReadOnlyList<UsageBucket> Days { get; init; } = [];

    public ActivityStats? Activity { get; init; }

    public DateOnly? PricesUpdated { get; init; }

    public ClaudeLimitsStatus ClaudeLimits { get; init; }

    public bool ClaudeAppInstalled { get; init; }

    public bool AnySeen => Providers.Any(p => p.Enabled && p.Seen);

    public PeriodSummary? For(AgentPeriod period) => period switch
    {
        AgentPeriod.Week => Week,
        AgentPeriod.Month => Month,
        _ => Today,
    };
}
