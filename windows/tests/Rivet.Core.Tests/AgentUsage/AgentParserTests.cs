// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Rivet.Core.Agents;
using Xunit;
using static Rivet.Core.Tests.Agents.AgentFixtures;

namespace Rivet.Core.Tests.Agents;

public class ClaudeParserTests
{
    private const string Log = @"C:\Users\dev\.claude\projects\C--Users-dev-code-app\s-1.jsonl";

    [Fact]
    public void Assistant_usage_maps_every_counter_and_flag()
    {
        var entries = Parse(new ClaudeTranscriptParser(Log),
            ClaudeAssistant(T0, "msg_1", "req_1", "tool_use", input: 4, cacheWrite: 1000, cacheRead: 5000, output: 50, longWrite: 600,
                model: "claude-opus-5-5", speed: "fast", geo: "us", webSearches: 2));
        var record = entries.OfType<UsageEntry>().Single().Record;
        Assert.Equal("claude:msg_1:req_1", record.Key);
        Assert.Equal(new TokenCounts(4, 1000, 5000, 50), record.Tokens);
        Assert.Equal(600, record.LongCacheWrite);
        Assert.True(record.Fast);
        Assert.True(record.UsOnly);
        Assert.Equal(2, record.WebSearches);
        Assert.Equal("app", record.Project);
        Assert.Equal("s-1", record.Session);
        Assert.Equal(T0, record.Date);
    }

    [Fact]
    public void A_prompt_opens_a_turn_and_a_final_reply_completes_it()
    {
        var entries = Parse(new ClaudeTranscriptParser(Log),
            ClaudeUser(T0, "please fix the build"),
            ClaudeAssistant(T0.AddSeconds(5), "m1", "r1", "tool_use", bashToolId: "toolu_1"),
            ClaudeToolResult(T0.AddSeconds(30), "toolu_1"),
            ClaudeAssistant(T0.AddSeconds(90), "m2", "r2", "end_turn"));
        Assert.IsType<TurnBeganEntry>(entries[0]);
        Assert.Contains(entries, e => e is RunningCommandEntry { StartedId: "toolu_1" });
        Assert.Contains(entries, e => e is RunningCommandEntry { FinishedId: "toolu_1" });
        var ended = entries.OfType<TurnEndedEntry>().Single();
        Assert.True(ended.Completed);
        Assert.Equal(T0.AddSeconds(90), ended.At);
    }

    [Fact]
    public void Interruptions_end_the_turn_uncompleted_and_meta_or_sidechain_lines_never_open_one()
    {
        var parser = new ClaudeTranscriptParser(Log);
        var entries = Parse(parser,
            ClaudeUser(T0, "<command-name>/clear</command-name>", meta: true),
            ClaudeUser(T0, "a sidechain prompt", sidechain: true),
            ClaudeUser(T0.AddSeconds(1), "real prompt"),
            ClaudeUser(T0.AddSeconds(9), "[Request interrupted by user]"));
        Assert.Single(entries.OfType<TurnBeganEntry>());
        Assert.False(entries.OfType<TurnEndedEntry>().Single().Completed);

        var errored = Parse(new ClaudeTranscriptParser(Log),
            ClaudeUser(T0, "hi"),
            ClaudeAssistant(T0.AddSeconds(2), "m", "r", "end_turn", apiError: true));
        Assert.False(errored.OfType<TurnEndedEntry>().Single().Completed);
    }

    [Fact]
    public void Synthetic_models_are_not_usage_and_missing_ids_fall_back_to_session_time()
    {
        var entries = Parse(new ClaudeTranscriptParser(Log),
            ClaudeAssistant(T0, "m", "r", "end_turn", model: "<synthetic>"),
            ClaudeAssistant(T0, string.Empty, string.Empty, "tool_use"));
        var record = entries.OfType<UsageEntry>().Single().Record;
        Assert.Equal($"claude:s-1:{T0.ToUnixTimeSeconds()}", record.Key);
    }

    [Fact]
    public void Subagent_transcripts_count_usage_but_never_drive_turns()
    {
        var parser = new ClaudeTranscriptParser(@"C:\Users\dev\.claude\projects\p\s-1\subagents\agent-a.jsonl");
        var entries = Parse(parser, ClaudeUser(T0, "task"), ClaudeAssistant(T0, "m", "r", "end_turn"));
        Assert.Single(entries);
        Assert.IsType<UsageEntry>(entries[0]);
        Assert.Equal(@"C:\Users\dev\.claude\projects\p\s-1.jsonl", AgentUsagePaths.ClaudeParentLog(@"C:\Users\dev\.claude\projects\p\s-1\subagents\agent-a.jsonl"));
    }

    [Fact]
    public void Quoted_records_inside_text_are_not_read()
    {
        var quoted = JsonSerializer.Serialize(new { type = "progress", data = "{\"type\":\"assistant\",\"message\":{\"usage\":{}}}" });
        Assert.Empty(Parse(new ClaudeTranscriptParser(Log), quoted, "not json {\"type\":\"user\""));
    }

    [Theory]
    [InlineData(@"C:\Users\dev\code\app", "app")]
    [InlineData(@"C:\Users\dev\code\app\.claude\worktrees\feature-x", "app")]
    [InlineData("/home/dev/code/web/", "web")]
    [InlineData(@"C:\", "")]
    public void Project_names_split_on_both_separators(string cwd, string expected) =>
        Assert.Equal(expected, AgentLogParsers.ProjectName(cwd));

    [Fact]
    public void Parser_state_survives_the_archive()
    {
        var parser = new ClaudeTranscriptParser(Log);
        Parse(parser, ClaudeUser(T0, "prompt"));
        var restored = new ClaudeTranscriptParser(Log);
        restored.LoadState(parser.SaveState());
        var entries = Parse(restored, ClaudeAssistant(T0.AddMinutes(1), "m", "r", "end_turn"));
        Assert.True(entries.OfType<TurnEndedEntry>().Single().Completed);
    }
}

public class CodexParserTests
{
    private const string Log = @"C:\Users\dev\.codex\sessions\2026\10\09\rollout-2026-10-09T10-00-00-0199a.jsonl";

    [Fact]
    public void Only_known_record_types_pass_the_sniff()
    {
        Assert.Equal(("token_usage_record", null), CodexRolloutParser.Sniff(Encoding.UTF8.GetBytes(CodexUsage(T0, "r", 1, 0, 0, 1))));
        Assert.Equal(("event_msg", "task_started"), CodexRolloutParser.Sniff(Encoding.UTF8.GetBytes(CodexTaskStarted(T0))));
        Assert.Equal((null, null), CodexRolloutParser.Sniff("{\"timestamp\":\"x\",\"type\":\"response_item\",\"payload\":{\"type\":\"token_usage_record\"}}"u8));
        Assert.Equal((null, null), CodexRolloutParser.Sniff("{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"text\":\"{\\\"type\\\":\\\"token_count\\\"}\"}}"u8));
    }

    [Fact]
    public void Usage_records_split_cached_and_written_tokens_and_take_the_latest_context()
    {
        var entries = Parse(new CodexRolloutParser(Log),
            CodexMeta(T0),
            CodexUsage(T0.AddSeconds(1), "resp_0", 100, 0, 0, 1),
            CodexContext(T0.AddSeconds(2), "gpt-5.1-codex", tier: "Priority"),
            CodexUsage(T0.AddSeconds(3), "resp_1", 1000, 600, 100, 50, reasoning: 20));
        var records = entries.OfType<UsageEntry>().Select(e => e.Record).ToList();
        Assert.Equal(string.Empty, records[0].Model);
        var second = records[1];
        Assert.Equal("codex:resp_1", second.Key);
        Assert.Equal("gpt-5.1-codex", second.Model);
        Assert.Equal(new TokenCounts(300, 100, 600, 50, 20), second.Tokens);
        Assert.True(second.Fast);
        Assert.Equal("api", second.Project);
        Assert.Equal("c-1", second.Session);
    }

    [Fact]
    public void Legacy_totals_are_used_only_until_per_response_records_appear()
    {
        var parser = new CodexRolloutParser(Log);
        var entries = Parse(parser,
            CodexContext(T0),
            CodexTokenCount(T0.AddSeconds(1), info: new { total_token_usage = new { input_tokens = 100, cached_input_tokens = 0, output_tokens = 10, total_tokens = 110 }, last_token_usage = new { input_tokens = 100, cached_input_tokens = 0, output_tokens = 10, total_tokens = 110 } }),
            CodexTokenCount(T0.AddSeconds(2), info: new { total_token_usage = new { input_tokens = 150, cached_input_tokens = 40, output_tokens = 30, total_tokens = 180 } }),
            CodexTokenCount(T0.AddSeconds(3), info: new { total_token_usage = new { input_tokens = 150, cached_input_tokens = 40, output_tokens = 30, total_tokens = 180 } }));
        var legacy = entries.OfType<UsageEntry>().Select(e => e.Record).ToList();
        Assert.Equal(2, legacy.Count);
        Assert.Equal("codex::total:110", legacy[0].Key);
        Assert.Equal(new TokenCounts(10, 0, 40, 20), legacy[1].Tokens);

        var later = Parse(parser,
            CodexUsage(T0.AddSeconds(4), "resp_9", 10, 0, 0, 1),
            CodexTokenCount(T0.AddSeconds(5), info: new { total_token_usage = new { input_tokens = 999, output_tokens = 999, total_tokens = 1998 } }));
        Assert.Single(later.OfType<UsageEntry>());
    }

    [Fact]
    public void Rate_limits_give_windows_by_length_and_the_plan()
    {
        var entries = Parse(new CodexRolloutParser(Log), CodexTokenCount(T0, new
        {
            primary = new { used_percent = 42.5, window_minutes = 300, resets_in_seconds = 3600 },
            secondary = new { used_percent = 120.0, window_minutes = 10080, resets_at = T0.AddDays(3).ToUnixTimeSeconds() },
            plan_type = "plus",
        }));
        var limits = entries.OfType<LimitsEntry>().Single().Limits;
        Assert.Equal(LimitSource.Log, limits.Source);
        var session = limits.Windows.Single(w => w.Id == "codex.300");
        Assert.Equal(LimitWindowKind.Session, session.Kind);
        Assert.Equal(42.5, session.UsedPercent);
        Assert.Equal(T0.AddHours(1), session.ResetsAt);
        var week = limits.Windows.Single(w => w.Id == "codex.10080");
        Assert.Equal(LimitWindowKind.Weekly, week.Kind);
        Assert.Equal(100, week.UsedPercent);
        Assert.Equal("plus", entries.OfType<PlanEntry>().Single().PlanType);
        Assert.Equal(LimitWindowKind.Other, CodexRolloutParser.KindOf(1440));
    }

    [Fact]
    public void Other_buckets_are_ignored_and_business_accounts_use_the_individual_limit()
    {
        Assert.Empty(Parse(new CodexRolloutParser(Log), CodexTokenCount(T0, new { limit_id = "codex_spark", primary = new { used_percent = 9, window_minutes = 300 } })).OfType<LimitsEntry>());
        var individual = Parse(new CodexRolloutParser(Log), CodexTokenCount(T0, new { individual_limit = new { remaining_percent = 30, resets_at = T0.AddDays(1).ToUnixTimeMilliseconds() } }))
            .OfType<LimitsEntry>().Single().Limits.Windows.Single();
        Assert.Equal("codex.individual", individual.Id);
        Assert.Equal(70, individual.UsedPercent);
        Assert.Equal(T0.AddDays(1), individual.ResetsAt);
    }

    [Fact]
    public void Tasks_open_and_close_turns_with_codex_s_own_duration_except_in_side_threads()
    {
        var entries = Parse(new CodexRolloutParser(Log), CodexTaskStarted(T0), CodexTaskComplete(T0.AddMinutes(2), 125_000), CodexTaskStarted(T0.AddMinutes(3)), CodexAborted(T0.AddMinutes(4)));
        var ends = entries.OfType<TurnEndedEntry>().ToList();
        Assert.True(ends[0].Completed);
        Assert.Equal(TimeSpan.FromSeconds(125), ends[0].Duration);
        Assert.False(ends[1].Completed);
        Assert.Empty(Parse(new CodexRolloutParser(@"C:\x\rollout_side.jsonl"), CodexTaskStarted(T0)));
    }
}

public class CopilotParserTests
{
    private const string Log = @"C:\Users\dev\.copilot\session-state\sess-1\events.jsonl";

    [Theory]
    [InlineData("""{"type":"a","type":"b","data":{}}""", false)]
    [InlineData("""{"type":"a","data":{}} {"x":1}""", false)]
    [InlineData("""[1,2]""", false)]
    [InlineData("""{"type":"a","data":{"nested":{"deep":true}}}""", true)]
    public void The_envelope_must_be_one_object_with_unique_keys(string line, bool valid) =>
        Assert.Equal(valid, CopilotEventParser.IsValidEnvelope(Encoding.UTF8.GetBytes(line)));

    [Fact]
    public void Long_keys_are_rejected()
    {
        var key = new string('k', 257);
        Assert.False(CopilotEventParser.IsValidEnvelope(Encoding.UTF8.GetBytes($"{{\"{key}\":1}}")));
    }

    [Fact]
    public void A_turn_ends_on_the_matching_turn_end_after_a_final_response()
    {
        var entries = Parse(new CopilotEventParser(Log),
            Copilot(T0, "session.start", new { sessionId = "sess-1", selectedModel = "claude-sonnet-4.5", context = new { gitRoot = @"C:\Users\dev\code\cli" } }),
            Copilot(T0.AddSeconds(1), "user.message", new { content = "(prompt)" }),
            Copilot(T0.AddSeconds(2), "assistant.turn_start", new { turnId = "t1" }),
            Copilot(T0.AddSeconds(3), "assistant.message", new { messageId = "m1", apiCallId = "a1", toolRequests = new[] { new { name = "shell" } } }),
            Copilot(T0.AddSeconds(4), "assistant.turn_end", new { turnId = "t1" }),
            Copilot(T0.AddSeconds(5), "assistant.turn_start", new { turnId = "t2" }),
            Copilot(T0.AddSeconds(6), "assistant.message", new { messageId = "m2", phase = "final" }),
            Copilot(T0.AddSeconds(7), "assistant.turn_end", new { turnId = "t2" }));
        Assert.Single(entries.OfType<TurnBeganEntry>());
        var ended = entries.OfType<TurnEndedEntry>().Single();
        Assert.True(ended.Completed);
        Assert.Equal(T0.AddSeconds(7), ended.At);
        var activity = entries.OfType<UsageEntry>().Select(e => e.Record).ToList();
        Assert.Equal(["copilot:sess-1:root:api:a1", "copilot:sess-1:root:message:m2"], activity.Select(r => r.Key));
        Assert.All(activity, r => Assert.True(r.IsAggregate && r.Requests == 1 && r.Tokens.Total == 0));
        Assert.All(activity, r => Assert.Equal("cli", r.Project));
    }

    [Fact]
    public void Shutdown_metrics_count_only_growth_and_reconcile_requests()
    {
        var parser = new CopilotEventParser(Log);
        var entries = Parse(parser,
            Copilot(T0, "session.model_change", new { newModel = "gpt-5" }),
            Copilot(T0, "assistant.message", new { messageId = "m1" }),
            Copilot(T0, "assistant.message", new { messageId = "m2" }),
            Copilot(T0.AddMinutes(1), "session.shutdown", new
            {
                modelMetrics = new Dictionary<string, object>
                {
                    ["gpt-5"] = new { requests = new { count = 3 }, tokenDetails = new { input = new { tokenCount = 1000 }, cache_read = new { tokenCount = 400 }, cache_write = new { tokenCount = 0 }, output = new { tokenCount = 100 } } },
                },
            }),
            Copilot(T0.AddMinutes(2), "session.shutdown", new
            {
                modelMetrics = new Dictionary<string, object>
                {
                    ["gpt-5"] = new { requests = new { count = 4 }, usage = new { inputTokens = 1500, cacheReadTokens = 400, outputTokens = 150 } },
                },
            }));
        var shutdowns = entries.OfType<UsageEntry>().Select(e => e.Record).Where(r => r.Tokens.Total > 0).ToList();
        Assert.Equal(2, shutdowns.Count);
        Assert.Equal(new TokenCounts(1000, 0, 400, 100), shutdowns[0].Tokens);
        Assert.Equal(1, shutdowns[0].Requests);
        Assert.Equal(new TokenCounts(500, 0, 0, 50), shutdowns[1].Tokens);
        Assert.Equal(1, shutdowns[1].Requests);
        Assert.All(shutdowns, r => Assert.True(r.IsAggregate));
    }

    [Fact]
    public void Abort_and_shutdown_end_turns_uncompleted()
    {
        var entries = Parse(new CopilotEventParser(Log),
            Copilot(T0, "user.message", new { }),
            Copilot(T0.AddSeconds(1), "abort", new { reason = "user" }),
            Copilot(T0.AddSeconds(2), "user.message", new { }),
            Copilot(T0.AddSeconds(3), "session.shutdown", new { }));
        Assert.All(entries.OfType<TurnEndedEntry>(), e => Assert.False(e.Completed));
        Assert.Equal(2, entries.OfType<TurnEndedEntry>().Count());
    }
}

public class AgentJsonTests
{
    [Fact]
    public void Numbers_must_be_finite_and_positive_and_booleans_are_not_numbers()
    {
        using var doc = JsonDocument.Parse("""{"a":5,"b":-1,"c":true,"d":"7","e":2e13,"f":0}""");
        var root = doc.RootElement;
        Assert.Equal(5, root.Get("a").Count());
        Assert.Equal(0, root.Get("b").Count());
        Assert.Equal(0, root.Get("c").Count());
        Assert.Equal(0, root.Get("d").Count());
        Assert.Equal(1_000_000_000_000, root.Get("e").Count());
        Assert.Equal(0, root.Get("f").Count());
    }

    [Fact]
    public void Timestamps_need_a_zone_and_large_numbers_are_milliseconds()
    {
        Assert.Equal(T0, AgentJson.ParseTime("2026-10-09T10:00:00Z"));
        Assert.Equal(T0, AgentJson.ParseTime("2026-10-09T12:00:00.000+02:00"));
        Assert.Null(AgentJson.ParseTime("2026-10-09T10:00:00"));
        Assert.Equal(T0, AgentJson.FromUnix(T0.ToUnixTimeSeconds()));
        Assert.Equal(T0, AgentJson.FromUnix(T0.ToUnixTimeMilliseconds()));
    }
}
