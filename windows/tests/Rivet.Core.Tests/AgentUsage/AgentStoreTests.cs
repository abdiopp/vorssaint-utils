// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Agents;
using Xunit;
using static Rivet.Core.Tests.Agents.AgentFixtures;

namespace Rivet.Core.Tests.Agents;

public class AgentStoreTests
{
    private const string File = @"C:\logs\a.jsonl";

    private static UsageRecord Record(string key, long output, DateTimeOffset at, string model = "claude-sonnet-4-6", long input = 0) =>
        new() { Key = key, Provider = AgentProvider.Claude, Date = at, Model = model, Tokens = new TokenCounts(input, 0, 0, output) };

    [Fact]
    public void Repeated_keys_merge_by_maximum_and_are_repriced()
    {
        var store = new AgentUsageStore();
        store.AddRecord(Record("k", 10, T0, input: 500), "a");
        var (output, cost) = store.AddRecord(Record("k", 40, T0, input: 100), "b");
        var merged = store.Records["k"];
        Assert.Equal(new TokenCounts(500, 0, 0, 40), merged.Tokens);
        Assert.Equal(30, output);
        Assert.Equal(30 * 15 / 1e6, cost, 9);
        Assert.Equal(["a", "b"], merged.Sources.Order());
        Assert.Equal((500 * 3 + 40 * 15) / 1e6, merged.Cost!.Value, 9);
    }

    [Fact]
    public void A_completed_turn_notifies_with_its_duration_cost_and_output_only_after_the_initial_read()
    {
        var store = new AgentUsageStore();
        store.Apply(AgentProvider.Claude, File, File, [new TurnBeganEntry(T0, "app", "", "s")], T0);
        store.Apply(AgentProvider.Claude, File, File, [new UsageEntry(Record("r1", 1000, T0.AddSeconds(30)))], T0);
        store.Apply(AgentProvider.Claude, File, File, [new TurnEndedEntry(T0.AddSeconds(252), true, null)], T0.AddSeconds(252));
        Assert.Empty(store.DrainFinished());

        store.TransitionsEnabled = true;
        store.Apply(AgentProvider.Claude, File, File, [new TurnBeganEntry(T0.AddMinutes(10), "app", "", "s")], T0);
        store.Apply(AgentProvider.Claude, File, File, [new UsageEntry(Record("r2", 2000, T0.AddMinutes(11)))], T0);
        var now = T0.AddMinutes(14);
        store.Apply(AgentProvider.Claude, File, File, [new TurnEndedEntry(now, true, null)], now);
        var finished = Assert.Single(store.DrainFinished());
        Assert.Equal(TimeSpan.FromMinutes(4), finished.Duration);
        Assert.Equal(2000, finished.OutputTokens);
        Assert.Equal(2000 * 15 / 1e6, finished.Cost, 9);
        Assert.Equal("app", finished.Project);
    }

    [Fact]
    public void Late_or_uncompleted_ends_never_notify_and_usage_before_the_turn_is_not_counted()
    {
        var store = new AgentUsageStore { TransitionsEnabled = true };
        store.Apply(AgentProvider.Codex, File, File, [new TurnBeganEntry(T0, "", "", "")], T0);
        store.Apply(AgentProvider.Codex, File, File, [new UsageEntry(new UsageRecord { Key = "old", Provider = AgentProvider.Codex, Date = T0.AddSeconds(-5), Tokens = new TokenCounts(0, 0, 0, 99) })], T0);
        Assert.Equal(0, store.Turns[File].OutputTokens);
        store.Apply(AgentProvider.Codex, File, File, [new TurnEndedEntry(T0.AddMinutes(1), true, null)], T0.AddMinutes(1) + AgentUsageConstants.LateEnd + TimeSpan.FromSeconds(1));
        Assert.Empty(store.DrainFinished());
        store.Apply(AgentProvider.Codex, File, File, [new TurnBeganEntry(T0.AddMinutes(2), "", "", ""), new TurnEndedEntry(T0.AddMinutes(3), false, null)], T0.AddMinutes(3));
        Assert.Empty(store.DrainFinished());
    }

    [Fact]
    public void Idle_turns_wait_then_expire_but_a_waiting_turn_that_ends_still_notifies()
    {
        var store = new AgentUsageStore { TransitionsEnabled = true };
        store.Apply(AgentProvider.Claude, File, File, [new TurnBeganEntry(T0, "", "", "")], T0);
        store.Tick(T0.AddMinutes(9));
        Assert.True(store.Turns.ContainsKey(File));
        store.Tick(T0.AddMinutes(10));
        Assert.True(store.Waiting.ContainsKey(File));
        store.Apply(AgentProvider.Claude, File, File, [new TurnEndedEntry(T0.AddMinutes(30), true, null)], T0.AddMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(store.DrainFinished()).Duration);

        store.Apply(AgentProvider.Claude, File, File, [new TurnBeganEntry(T0, "", "", "")], T0);
        store.Tick(T0.AddMinutes(10));
        store.Tick(T0.AddMinutes(59));
        Assert.True(store.Waiting.ContainsKey(File));
        store.Tick(T0.AddMinutes(60));
        Assert.False(store.Waiting.ContainsKey(File));

        store.Apply(AgentProvider.Codex, @"C:\c.jsonl", @"C:\c.jsonl", [new TurnBeganEntry(T0, "", "", "")], T0);
        store.Tick(T0.AddMinutes(10));
        store.Tick(T0.AddHours(5));
        Assert.True(store.Waiting.ContainsKey(@"C:\c.jsonl"));
        store.Tick(T0.AddHours(6));
        Assert.Empty(store.Waiting);
    }

    [Fact]
    public void Activity_brings_a_waiting_turn_back_and_a_reread_begin_is_ignored()
    {
        var store = new AgentUsageStore();
        store.Apply(AgentProvider.Claude, File, File, [new TurnBeganEntry(T0, "app", "", "")], T0);
        store.Tick(T0.AddMinutes(10));
        store.Apply(AgentProvider.Claude, File, File, [new TurnActiveEntry(T0.AddMinutes(12))], T0.AddMinutes(12));
        Assert.True(store.Turns.ContainsKey(File));
        store.Apply(AgentProvider.Claude, File, File, [new TurnBeganEntry(T0.AddMilliseconds(500), "other", "", "")], T0.AddMinutes(12));
        Assert.Equal("app", store.Turns[File].Project);
        store.Apply(AgentProvider.Claude, File, File, [new TurnBeganEntry(T0.AddMinutes(13), "other", "", "")], T0.AddMinutes(13));
        Assert.Equal("other", store.Turns[File].Project);
    }

    [Fact]
    public void Settled_opencode_turns_end_silently_after_30_seconds_and_old_records_go()
    {
        var store = new AgentUsageStore { TransitionsEnabled = true };
        store.Apply(AgentProvider.OpenCode, "db", "db#s", [new TurnBeganEntry(T0, "", "", ""), new TurnSettledEntry(T0.AddSeconds(5))], T0);
        store.Tick(T0.AddSeconds(34));
        Assert.True(store.Turns.ContainsKey("db#s"));
        store.Tick(T0.AddSeconds(35));
        Assert.Empty(store.Turns);
        Assert.Empty(store.DrainFinished());

        store.AddRecord(Record("old", 1, T0.AddDays(-92)), "a");
        store.AddRecord(Record("new", 1, T0.AddDays(-1)), "a");
        store.Tick(T0);
        Assert.Equal(["new"], store.Records.Keys);
    }

    [Fact]
    public void Limits_keep_the_newest_reading_whatever_the_source()
    {
        var store = new AgentUsageStore();
        store.SetLimits(new ProviderLimits(AgentProvider.Codex, [new LimitWindow { Id = "codex.300", UsedPercent = 50 }], T0, LimitSource.Account));
        store.SetLimits(new ProviderLimits(AgentProvider.Codex, [new LimitWindow { Id = "codex.300", UsedPercent = 10 }], T0.AddMinutes(-1), LimitSource.Log));
        Assert.Equal(50, store.Limits[AgentProvider.Codex].Windows[0].UsedPercent);
    }

    [Fact]
    public void Copilot_activity_never_adds_to_the_live_turn()
    {
        var store = new AgentUsageStore();
        store.Apply(AgentProvider.Copilot, File, File, [new TurnBeganEntry(T0, "", "", ""), new UsageEntry(new UsageRecord { Key = "c", Provider = AgentProvider.Copilot, Date = T0, Tokens = new TokenCounts(0, 0, 0, 500), IsAggregate = true })], T0);
        Assert.Equal(0, store.Turns[File].OutputTokens);
    }
}

public class LimitMathTests
{
    [Fact]
    public void Renewed_windows_show_zero_and_pace_tracks_the_elapsed_share()
    {
        var window = new LimitWindow { Id = "w", Minutes = 300, UsedPercent = 60, ResetsAt = T0.AddHours(1) };
        Assert.Equal(0.8, window.Pace(T0)!.Value, 6);
        var after = window.Current(T0.AddHours(2));
        Assert.Equal(0, after.UsedPercent);
        Assert.Null(after.ResetsAt);
        Assert.Null(window.Pace(T0.AddHours(2)));
        var limits = new ProviderLimits(AgentProvider.Codex,
        [
            new LimitWindow { Id = "a", UsedPercent = 40, ResetsAt = T0.AddHours(1) },
            new LimitWindow { Id = "b", UsedPercent = 40, ResetsAt = T0.AddDays(2) },
        ], T0, LimitSource.Log);
        Assert.Equal("b", limits.Binding(T0)!.Id);
    }

    [Fact]
    public void Warnings_need_a_previous_reading_and_a_crossing_or_a_renewal()
    {
        var alerts = new LimitAlerts();
        ProviderLimits Reading(double used, DateTimeOffset reset, DateTimeOffset at) =>
            new(AgentProvider.Codex, [new LimitWindow { Id = "codex.300", UsedPercent = used, ResetsAt = reset }], at, LimitSource.Log);
        var reset = T0.AddHours(3);
        Assert.Empty(alerts.Check(Reading(90, reset, T0), 80, T0));
        Assert.Empty(alerts.Check(Reading(95, reset, T0.AddMinutes(1)), 80, T0.AddMinutes(1)));
        var fresh = new LimitAlerts();
        fresh.Check(Reading(50, reset, T0), 80, T0);
        Assert.IsType<LimitWarningAlert>(Assert.Single(fresh.Check(Reading(81, reset, T0.AddMinutes(5)), 80, T0.AddMinutes(5))));
        Assert.Empty(fresh.Check(Reading(85, reset, T0.AddMinutes(6)), 80, T0.AddMinutes(6)));

        // A banked reset: the renewal moved later and usage dropped below the threshold.
        var renewed = fresh.Check(Reading(5, reset.AddHours(5), T0.AddMinutes(7)), 80, T0.AddMinutes(7));
        Assert.IsType<LimitRenewedAlert>(Assert.Single(renewed));
        // Renewed above the threshold warns again.
        Assert.IsType<LimitWarningAlert>(Assert.Single(fresh.Check(Reading(90, reset.AddHours(10), T0.AddMinutes(8)), 80, T0.AddMinutes(8))));
        var limits = Reading(90, reset.AddHours(10), T0.AddMinutes(8));
        Assert.Empty(fresh.Tick([limits], T0.AddHours(12)));
        Assert.IsType<LimitRenewedAlert>(Assert.Single(fresh.Tick([limits], T0.AddHours(14))));
    }
}

public class ClaudeLimitsTests
{
    private static string History(params (DateTimeOffset At, string? Org, double? Fh, double? Sd)[] samples) =>
        "{\"version\":2,\"samples\":[" + string.Join(',', samples.Select(s =>
            $"{{\"t\":{s.At.ToUnixTimeMilliseconds()},\"org\":{(s.Org is null ? "null" : $"\"{s.Org}\"")},\"u\":{{{string.Join(',', new[] { s.Fh is { } fh ? $"\"fh\":{fh}" : null, s.Sd is { } sd ? $"\"sd\":{sd}" : null }.Where(x => x is not null))}}}}}")) + "]}";

    [Fact]
    public void Version_1_and_2_histories_parse_and_clamp()
    {
        var v1 = ClaudeLimits.ParseHistory(Encoding.UTF8.GetBytes($"{{\"version\":1,\"samples\":[{{\"t\":{T0.ToUnixTimeMilliseconds()},\"fh\":130,\"sd\":12}}]}}"))!;
        Assert.Equal(100, v1[0].Session);
        Assert.Equal(12, v1[0].Week);
        var v2 = ClaudeLimits.ParseHistory(Encoding.UTF8.GetBytes(History((T0, "org-1", 40, 10))))!;
        Assert.Equal("org-1", v2[0].Organization);
        Assert.Null(ClaudeLimits.ParseHistory("{\"nope\":1}"u8));
    }

    [Fact]
    public void The_session_renews_five_hours_after_the_run_began_dated_by_claude_code_s_first_request()
    {
        var samples = ClaudeLimits.ParseHistory(Encoding.UTF8.GetBytes(History(
            (T0, null, 70, 20),
            (T0.AddMinutes(30), null, 0, 20),
            (T0.AddMinutes(60), null, 5, 21),
            (T0.AddMinutes(90), null, 4.5, 22),
            (T0.AddMinutes(120), null, 30, 25))))!;
        Assert.Equal(T0.AddMinutes(60) + ClaudeLimits.SessionLength, ClaudeLimits.SessionRenewal(samples, null));
        var dated = ClaudeLimits.SessionRenewal(samples, (from, to) => from.AddMinutes(10));
        Assert.Equal(T0.AddMinutes(40) + ClaudeLimits.SessionLength, dated);

        var limits = ClaudeLimits.FromHistory(samples, null, T0.AddMinutes(125))!;
        Assert.Equal(LimitSource.ClaudeApp, limits.Source);
        var session = limits.Windows.Single(w => w.Kind == LimitWindowKind.Session);
        Assert.Equal(30, session.UsedPercent);
        Assert.Equal(T0.AddMinutes(360), session.ResetsAt);
    }

    [Fact]
    public void The_week_renews_on_the_utc_hour_after_its_last_drop()
    {
        var samples = ClaudeLimits.ParseHistory(Encoding.UTF8.GetBytes(History(
            (T0.AddDays(-2).AddMinutes(20), null, null, 80),
            (T0.AddDays(-2).AddMinutes(75), null, null, 3),
            (T0, null, null, 15))))!;
        var expected = T0.AddDays(-2).AddHours(1) + ClaudeLimits.WeekLength;
        Assert.Equal(expected, ClaudeLimits.WeeklyRenewal(samples, s => s.Week, T0));
    }

    [Fact]
    public void Old_future_or_foreign_readings_are_ignored()
    {
        var samples = ClaudeLimits.ParseHistory(Encoding.UTF8.GetBytes(History((T0, "org-2", 50, 10))))!;
        Assert.Null(ClaudeLimits.FromHistory(samples, "org-1", T0));
        Assert.Null(ClaudeLimits.FromHistory(samples, null, T0.AddDays(-1)));
        Assert.Null(ClaudeLimits.FromHistory(samples, null, T0.AddDays(7)));
        // The session window is dropped once the reading is 5 h old; a week without renewal after a day.
        var aged = ClaudeLimits.FromHistory(samples, null, T0.AddHours(6));
        Assert.True(aged is null || aged.Windows.All(w => w.Kind != LimitWindowKind.Session));
    }

    [Fact]
    public void The_estimate_chains_five_hour_blocks_from_the_first_request()
    {
        UsageRecord At(DateTimeOffset t, double cost) => new() { Key = t.ToString(), Provider = AgentProvider.Claude, Date = t, Cost = cost };
        var records = new[] { At(T0, 1), At(T0.AddHours(2), 2), At(T0.AddHours(5).AddMinutes(1), 4), At(T0.AddHours(6), 8) };
        var block = ClaudeLimits.Estimate(records, T0.AddHours(7))!;
        Assert.Equal(T0.AddHours(5).AddMinutes(1), block.Start);
        Assert.Equal(12, block.Cost);
        Assert.Null(ClaudeLimits.Estimate(records, T0.AddHours(11)));
    }
}

public class AgentArchiveTests
{
    private static AgentArchiveData Sample() => new()
    {
        Build = "1.0.0",
        Providers = ["claude", "codex"],
        Records =
        [
            ArchivedRecord.From(new UsageRecord { Key = "claude:m:r", Provider = AgentProvider.Claude, Date = T0, Model = "claude-opus-5-5", Tokens = new TokenCounts(1, 2, 3, 4, 1), Sources = { @"C:\a.jsonl" } }),
        ],
        Limits = [new ArchivedLimits("codex", [new LimitWindow { Id = "codex.300", Minutes = 300, UsedPercent = 12.5, ResetsAt = T0.AddHours(2) }], T0, LimitSource.Log)],
        CodexPlan = new AgentPlan("Plus", 20),
        Turns = [new ArchivedTurn(@"C:\a.jsonl", "claude", T0, T0, "m", "p", "s", 5, 0.5)],
        Cursors = [new LogCursor { Path = @"C:\a.jsonl", Provider = AgentProvider.Claude, Offset = 123, Identity = 7, Fingerprint = 99, ParserState = "{}" }],
    };

    [Fact]
    public void The_archive_round_trips()
    {
        var bytes = AgentUsageArchive.Encode(Sample());
        var decoded = AgentUsageArchive.Decode(bytes)!;
        Assert.Equal("1.0.0", decoded.Build);
        var record = decoded.Records.Single().ToRecord()!;
        Assert.Equal(new TokenCounts(1, 2, 3, 4, 1), record.Tokens);
        Assert.Equal([@"C:\a.jsonl"], record.Sources);
        Assert.Equal(12.5, decoded.Limits.Single().Windows.Single().UsedPercent);
        Assert.Equal(123, decoded.Cursors.Single().Offset);
        Assert.Equal(new AgentPlan("Plus", 20), decoded.CodexPlan);
    }

    [Fact]
    public void Every_single_bit_flip_is_rejected()
    {
        var bytes = AgentUsageArchive.Encode(Sample());
        for (var i = 0; i < bytes.Length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                bytes[i] ^= (byte)(1 << bit);
                Assert.Null(AgentUsageArchive.Decode(bytes));
                bytes[i] ^= (byte)(1 << bit);
            }
        }

        Assert.NotNull(AgentUsageArchive.Decode(bytes));
        Assert.Null(AgentUsageArchive.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Null(AgentUsageArchive.Decode([.. bytes, 0]));
    }
}
