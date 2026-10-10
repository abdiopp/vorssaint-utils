// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Rivet.Core.Agents;

/// <summary>One saved reading of the Claude desktop app's plan usage.</summary>
public sealed record ClaudeUsageSample(DateTimeOffset At, string? Organization, double? Session, double? Week, double? WeekOpus, double? WeekSonnet);

/// <summary>The 5-hour session estimated from Claude Code's own activity.</summary>
public sealed record ClaudeSessionEstimate(DateTimeOffset Start, DateTimeOffset End, double Cost, bool PartlyUnpriced);

/// <summary>
/// Claude plan limits (spec 07 §3.8.7): read from the history file the Claude
/// desktop app keeps (<c>plan-usage-history.json</c>, ≤ 4 MiB), or estimated
/// from Claude Code activity when the app is absent.
/// </summary>
public static class ClaudeLimits
{
    public const int MaxHistoryBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan SessionLength = TimeSpan.FromHours(5);
    public static readonly TimeSpan WeekLength = TimeSpan.FromDays(7);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);

    /// <summary>Parses version 1 (window keys beside "t") and version 2 ("u" object) histories.</summary>
    public static List<ClaudeUsageSample>? ParseHistory(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxHistoryBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            if (root.Get("samples") is not { ValueKind: JsonValueKind.Array } samples)
            {
                return null;
            }

            var result = new List<ClaudeUsageSample>();
            foreach (var sample in samples.EnumerateArray())
            {
                if (sample.Get("t").Time() is not { } at)
                {
                    continue;
                }

                var u = sample.Get("u") is { ValueKind: JsonValueKind.Object } usage ? usage : sample;
                result.Add(new ClaudeUsageSample(at, sample.Get("org").String(),
                    Percent(u.Get("fh")), Percent(u.Get("sd")), Percent(u.Get("so")), Percent(u.Get("sn"))));
            }

            result.Sort((a, b) => a.At.CompareTo(b.At));
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The windows of the latest usable reading. <paramref name="firstRequestBetween"/>
    /// returns Claude Code's own first request in a time range (used to date the session start).
    /// </summary>
    public static ProviderLimits? FromHistory(IReadOnlyList<ClaudeUsageSample> history, string? organization, DateTimeOffset now,
        Func<DateTimeOffset, DateTimeOffset, DateTimeOffset?>? firstRequestBetween = null)
    {
        var samples = history.Where(s => organization is null || s.Organization is null || s.Organization == organization).ToList();
        if (samples.Count == 0)
        {
            return null;
        }

        var latest = samples[^1];
        if (latest.At - now > FutureTolerance || now - latest.At >= MaxAge)
        {
            return null;
        }

        var windows = new List<LimitWindow>();
        if (latest.Session is { } session && now - latest.At < SessionLength)
        {
            var reset = SessionRenewal(samples, firstRequestBetween);
            Add(windows, new LimitWindow { Id = "claude.session", Kind = LimitWindowKind.Session, Minutes = 300, UsedPercent = session, ResetsAt = reset }, latest.At, now);
        }

        var weekReset = WeeklyRenewal(samples, s => s.Week, latest.At);
        if (latest.Week is { } week)
        {
            Add(windows, new LimitWindow { Id = "claude.week", Kind = LimitWindowKind.Weekly, Minutes = 10080, UsedPercent = week, ResetsAt = weekReset }, latest.At, now);
        }

        if (latest.WeekOpus is { } opus)
        {
            Add(windows, new LimitWindow { Id = "claude.week.opus", Kind = LimitWindowKind.Weekly, Minutes = 10080, Scope = "Opus", UsedPercent = opus, ResetsAt = WeeklyRenewal(samples, s => s.WeekOpus, latest.At) ?? weekReset }, latest.At, now);
        }

        if (latest.WeekSonnet is { } sonnet)
        {
            Add(windows, new LimitWindow { Id = "claude.week.sonnet", Kind = LimitWindowKind.Weekly, Minutes = 10080, Scope = "Sonnet", UsedPercent = sonnet, ResetsAt = WeeklyRenewal(samples, s => s.WeekSonnet, latest.At) ?? weekReset }, latest.At, now);
        }

        return windows.Count == 0 ? null : new ProviderLimits(AgentProvider.Claude, windows, latest.At, LimitSource.ClaudeApp);
    }

    /// <summary>
    /// Session renewal: the run of non-zero session readings ending with the
    /// latest (values may rise, or fall by at most one point; gaps under 5 h)
    /// starts the block, preferably at Claude Code's own first request between
    /// the reading before the run and the run's first reading. Renewal = start + 5 h.
    /// </summary>
    public static DateTimeOffset? SessionRenewal(IReadOnlyList<ClaudeUsageSample> samples, Func<DateTimeOffset, DateTimeOffset, DateTimeOffset?>? firstRequestBetween)
    {
        var last = samples.Count - 1;
        if (last < 0 || samples[last].Session is not > 0)
        {
            return null;
        }

        var first = last;
        while (first > 0)
        {
            var earlier = samples[first - 1];
            var later = samples[first];
            if (earlier.Session is not > 0 || later.At - earlier.At >= SessionLength || later.Session < earlier.Session - 1)
            {
                break;
            }

            first--;
        }

        var start = samples[first].At;
        if (first > 0 && firstRequestBetween?.Invoke(samples[first - 1].At, samples[first].At) is { } request)
        {
            start = request;
        }

        return start + SessionLength;
    }

    /// <summary>
    /// Weekly renewal: the most recent drop of more than one point; the next
    /// UTC hour after the earlier sample (capped at the later one), plus whole
    /// weeks until it is after the reading.
    /// </summary>
    public static DateTimeOffset? WeeklyRenewal(IReadOnlyList<ClaudeUsageSample> samples, Func<ClaudeUsageSample, double?> value, DateTimeOffset readingAt)
    {
        for (var i = samples.Count - 1; i > 0; i--)
        {
            if (value(samples[i - 1]) is not { } before || value(samples[i]) is not { } after || after >= before - 1)
            {
                continue;
            }

            var earlier = samples[i - 1].At.ToUniversalTime();
            var hour = new DateTimeOffset(earlier.Year, earlier.Month, earlier.Day, earlier.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
            if (hour > samples[i].At)
            {
                hour = samples[i].At;
            }

            while (hour <= readingAt)
            {
                hour += WeekLength;
            }

            return hour;
        }

        return null;
    }

    /// <summary>
    /// The 5-hour blocks of the last 24 h: each starts at the first request at
    /// or after the previous block's end; the current block ends after now.
    /// </summary>
    public static ClaudeSessionEstimate? Estimate(IEnumerable<UsageRecord> claudeRecords, DateTimeOffset now)
    {
        var recent = claudeRecords.Where(r => r.Provider == AgentProvider.Claude && r.Date > now - TimeSpan.FromHours(24) && r.Date <= now)
            .OrderBy(r => r.Date).ToList();
        ClaudeSessionEstimate? block = null;
        DateTimeOffset? end = null;
        var cost = 0.0;
        var unpriced = false;
        foreach (var record in recent)
        {
            if (end is null || record.Date >= end)
            {
                end = record.Date + SessionLength;
                cost = 0;
                unpriced = false;
                block = new ClaudeSessionEstimate(record.Date, end.Value, 0, false);
            }

            cost += record.Cost ?? 0;
            unpriced |= record.Cost is null;
            block = block! with { Cost = cost, PartlyUnpriced = unpriced };
        }

        return block is not null && block.End > now ? block : null;
    }

    private static void Add(List<LimitWindow> windows, LimitWindow window, DateTimeOffset readingAt, DateTimeOffset now)
    {
        // Omitted once its renewal passed, or with no known renewal when the reading is a day old.
        if (window.ResetsAt is { } reset ? reset <= now : now - readingAt >= TimeSpan.FromDays(1))
        {
            return;
        }

        windows.Add(window);
    }

    private static double? Percent(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out var v) && double.IsFinite(v) ? Math.Clamp(v, 0, 100) : null;
}
