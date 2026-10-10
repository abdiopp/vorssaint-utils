// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Rivet.Core.Agents;
using Xunit;
using static Rivet.Core.Tests.Agents.AgentFixtures;

namespace Rivet.Core.Tests.Agents;

public class LogCursorReaderTests
{
    [Fact]
    public void Appended_lines_are_read_once_and_a_partial_line_waits_for_its_newline()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("s.jsonl");
        Write(path, ClaudeAssistant(T0, "m1", "r1", "tool_use"));
        File.AppendAllText(path, ClaudeAssistant(T0, "m2", "r2", "tool_use"));
        var cursor = new LogCursor { Path = path, Provider = AgentProvider.Claude };
        var platform = new PortableAgentUsagePlatform();
        var entries = new List<AgentEntry>();
        Assert.Equal(LogReadOutcome.Appended, LogCursorReader.Read(cursor, platform.FileIdentity, entries));
        Assert.Single(entries.OfType<UsageEntry>());
        entries.Clear();
        File.AppendAllText(path, "\n");
        LogCursorReader.Read(cursor, platform.FileIdentity, entries);
        Assert.Equal("claude:m2:r2", entries.OfType<UsageEntry>().Single().Record.Key);
        entries.Clear();
        Assert.Equal(LogReadOutcome.Unchanged, LogCursorReader.Read(cursor, platform.FileIdentity, entries));
    }

    [Fact]
    public void A_truncated_or_rewritten_file_is_read_again_from_the_start()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("s.jsonl");
        Write(path, ClaudeAssistant(T0, "m1", "r1", "tool_use"), ClaudeAssistant(T0, "m2", "r2", "tool_use"));
        var cursor = new LogCursor { Path = path, Provider = AgentProvider.Claude };
        var platform = new PortableAgentUsagePlatform();
        LogCursorReader.Read(cursor, platform.FileIdentity, []);
        Write(path, ClaudeAssistant(T0, "m9", "r9", "tool_use"));
        var entries = new List<AgentEntry>();
        Assert.Equal(LogReadOutcome.Restarted, LogCursorReader.Read(cursor, platform.FileIdentity, entries));
        Assert.Equal("claude:m9:r9", entries.OfType<UsageEntry>().Single().Record.Key);

        // Same size, different bytes before the offset: the fingerprint catches the rewrite.
        var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("m9", "m8") + ClaudeAssistant(T0, "m10", "r10", "tool_use") + "\n");
        entries.Clear();
        Assert.Equal(LogReadOutcome.Restarted, LogCursorReader.Read(cursor, platform.FileIdentity, entries));
        Assert.Equal(["claude:m8:r9", "claude:m10:r10"], entries.OfType<UsageEntry>().Select(e => e.Record.Key));
    }

    [Fact]
    public void Lines_over_the_limit_are_skipped()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("big.jsonl");
        using (var stream = File.Create(path))
        {
            var huge = new byte[LogCursorReader.MaxLineBytes + 10];
            Array.Fill(huge, (byte)'x');
            stream.Write(huge);
            stream.Write("\n"u8);
            stream.Write(Encoding.UTF8.GetBytes(ClaudeAssistant(T0, "m1", "r1", "tool_use") + "\n"));
        }

        var cursor = new LogCursor { Path = path, Provider = AgentProvider.Claude };
        var entries = new List<AgentEntry>();
        LogCursorReader.Read(cursor, new PortableAgentUsagePlatform().FileIdentity, entries);
        Assert.Single(entries.OfType<UsageEntry>());
        Assert.False(cursor.Discarding);
    }
}

public class AgentEngineTests
{
    private static (AgentUsageEngine Engine, TestAgentPlatform Platform, AgentUsagePaths Paths, Func<DateTimeOffset> Clock) Create(TempFolder temp, DateTimeOffset now, string? archive = null, params AgentProvider[] providers)
    {
        var platform = new TestAgentPlatform(temp.Path);
        var paths = new AgentUsagePaths(platform);
        var set = (providers.Length == 0 ? AgentProviders.All : providers).ToHashSet();
        var clock = now;
        var engine = new AgentUsageEngine(platform, paths, () => new AgentUsageOptions { Providers = set, Build = "test" }, archive, clock: () => clock, zone: TimeZoneInfo.Utc);
        return (engine, platform, paths, () => clock);
    }

    private static string ClaudeLog(TempFolder temp, string session = "s-1") =>
        temp.Combine("home", ".claude", "projects", "C--Users-dev-code-app", session + ".jsonl");

    [Fact]
    public void Discovery_finds_each_agent_in_its_windows_layout()
    {
        using var temp = new TempFolder();
        var (_, platform, paths, _) = Create(temp, T0);
        Write(ClaudeLog(temp), ClaudeUser(T0, "hi"));
        Write(temp.Combine("home", ".claude", "projects", "C--Users-dev-code-app", "s-1", "subagents", "a.jsonl"), ClaudeUser(T0, "sub"));
        Write(temp.Combine("home", ".codex", "sessions", "2026", "10", "09", "rollout-a.jsonl"), CodexMeta(T0));
        Write(temp.Combine("home", ".codex", "archived_sessions", "rollout-b.jsonl"), CodexMeta(T0));
        Write(temp.Combine("home", ".copilot", "session-state", "sess-1", "events.jsonl"), Copilot(T0, "user.message", new { }));
        Write(temp.Combine("home", ".copilot", "session-state", "sess-1", "repo", "nested", "events.jsonl"), Copilot(T0, "user.message", new { }));
        Write(temp.Combine("home", ".copilot", "session-state", ".hidden", "events.jsonl"), Copilot(T0, "user.message", new { }));
        var old = temp.Combine("home", ".codex", "sessions", "old.jsonl");
        Write(old, CodexMeta(T0));
        File.SetLastWriteTimeUtc(old, T0.UtcDateTime.AddDays(-100));

        var files = paths.Discover(AgentProviders.All.ToHashSet(), T0.AddMinutes(1));
        Assert.Equal(5, files.Count);
        Assert.EndsWith("a.jsonl", files[^1].Path);
        Assert.Equal(2, files.Count(f => f.Provider == AgentProvider.Codex));
        Assert.Single(files, f => f.Provider == AgentProvider.Copilot);

        platform.Environment["CODEX_HOME"] = temp.Combine("elsewhere");
        Assert.Equal(temp.Combine("elsewhere", "sessions"), paths.CodexRoots[0]);
        var overridden = new AgentUsagePaths(platform, p => p == AgentProvider.Claude ? temp.Combine("claude-data") : null);
        Assert.Equal([temp.Combine("claude-data", "projects")], overridden.ClaudeProjectRoots);
    }

    [Fact]
    public void The_initial_read_prices_usage_and_never_replays_finished_tasks()
    {
        using var temp = new TempFolder();
        var log = ClaudeLog(temp);
        Write(log,
            ClaudeUser(T0, "first"),
            ClaudeAssistant(T0.AddMinutes(1), "m1", "r1", "tool_use", input: 1000, output: 200, model: "claude-sonnet-4-6"),
            ClaudeAssistant(T0.AddMinutes(2), "m2", "r2", "end_turn", input: 1000, output: 300, model: "claude-sonnet-4-6"));
        var (engine, _, _, _) = Create(temp, T0.AddMinutes(3));
        var finished = new List<AgentFinished>();
        engine.TaskFinished += (_, f) => finished.Add(f);
        engine.Initialize();
        Assert.Empty(finished);
        var snapshot = engine.BuildSnapshot();
        Assert.True(snapshot.Loaded);
        var today = snapshot.Today!;
        Assert.Equal(2, today.Totals.Requests);
        Assert.Equal((2000 * 3 + 500 * 15) / 1e6, today.Totals.Cost, 9);
        Assert.Equal("Sonnet 4.6", today.Models.Single().Name);
        Assert.Equal("app", today.Projects.Single().Name);
        Assert.True(snapshot.Providers.Single(p => p.Provider == AgentProvider.Claude).Seen);
        Assert.False(snapshot.Providers.Single(p => p.Provider == AgentProvider.Codex).Seen);
    }

    [Fact]
    public void A_live_turn_appears_while_working_and_its_end_notifies()
    {
        using var temp = new TempFolder();
        var log = ClaudeLog(temp);
        Write(log, ClaudeUser(T0, "build it"));
        var platform = new TestAgentPlatform(temp.Path);
        var paths = new AgentUsagePaths(platform);
        var now = T0.AddSeconds(5);
        var engine = new AgentUsageEngine(platform, paths, () => new AgentUsageOptions { Providers = AgentProviders.All.ToHashSet(), Build = "test" }, null, clock: () => now, zone: TimeZoneInfo.Utc);
        var finished = new List<AgentFinished>();
        engine.TaskFinished += (_, f) => finished.Add(f);
        engine.Initialize();
        var live = engine.BuildSnapshot().Live.Single();
        Assert.Equal("app", live.Project);

        Append(log,
            ClaudeAssistant(T0.AddMinutes(1), "m1", "r1", "tool_use", output: 400, model: "claude-opus-5-5"),
            ClaudeAssistant(T0.AddMinutes(4).AddSeconds(12), "m2", "r2", "end_turn", output: 600, model: "claude-opus-5-5"));
        now = T0.AddMinutes(4).AddSeconds(15);
        engine.Poll();
        var task = Assert.Single(finished);
        Assert.Equal(TimeSpan.FromSeconds(252), task.Duration);
        Assert.Equal(1000, task.OutputTokens);
        Assert.Equal(AgentProvider.Claude, task.Provider);
        Assert.Empty(engine.BuildSnapshot().Live);
    }

    [Fact]
    public void Subagent_usage_counts_toward_the_parent_turn()
    {
        using var temp = new TempFolder();
        var log = ClaudeLog(temp);
        Write(log, ClaudeUser(T0, "go"));
        Write(temp.Combine("home", ".claude", "projects", "C--Users-dev-code-app", "s-1", "subagents", "agent-1.jsonl"),
            ClaudeAssistant(T0.AddSeconds(10), "sub1", "subr1", "end_turn", output: 777));
        var (engine, _, _, _) = Create(temp, T0.AddSeconds(20));
        engine.Initialize();
        Assert.Equal(777, engine.BuildSnapshot().Live.Single().OutputTokens);
    }

    [Fact]
    public void Codex_limits_plan_and_warnings_flow_through()
    {
        using var temp = new TempFolder();
        var log = temp.Combine("home", ".codex", "sessions", "2026", "10", "09", "rollout-a.jsonl");
        Write(log, CodexMeta(T0), CodexContext(T0), CodexTokenCount(T0, new { primary = new { used_percent = 50, window_minutes = 300, resets_in_seconds = 7200 }, plan_type = "plus" }));
        var platform = new TestAgentPlatform(temp.Path);
        var now = T0.AddMinutes(1);
        var engine = new AgentUsageEngine(platform, new AgentUsagePaths(platform), () => new AgentUsageOptions { Providers = AgentProviders.All.ToHashSet(), Build = "test", LimitThreshold = 80 }, null, clock: () => now, zone: TimeZoneInfo.Utc);
        var alerts = new List<AgentAlert>();
        engine.Alerted += (_, a) => alerts.Add(a);
        engine.Initialize();
        var codex = engine.BuildSnapshot().Providers.Single(p => p.Provider == AgentProvider.Codex);
        Assert.Equal(new AgentPlan("Plus", 20), codex.Plan);
        Assert.Equal(50, codex.Windows.Single().UsedPercent);
        Assert.Empty(alerts);

        Append(log, CodexTokenCount(T0.AddMinutes(2), new { primary = new { used_percent = 85, window_minutes = 300, resets_in_seconds = 7080 } }));
        now = T0.AddMinutes(2);
        engine.Poll();
        Assert.IsType<LimitWarningAlert>(Assert.Single(alerts));
    }

    [Fact]
    public void Dead_claude_processes_and_lost_networks_end_turns_silently()
    {
        using var temp = new TempFolder();
        var log = ClaudeLog(temp, "sess-a");
        Write(log, ClaudeUser(T0, "work"));
        var sessions = temp.Combine("home", ".claude", "sessions");
        Write(Path.Combine(sessions, "4242.json"), """{"pid":4242,"sessionId":"sess-a","pidDomain":"win32"}""");
        Write(Path.Combine(sessions, "1.json"), """{"pid":1,"sessionId":"other","pidDomain":"darwin"}""");
        var platform = new TestAgentPlatform(temp.Path);
        platform.Processes[4242] = AgentProcessState.Alive;
        var now = T0.AddSeconds(10);
        var engine = new AgentUsageEngine(platform, new AgentUsagePaths(platform), () => new AgentUsageOptions { Providers = AgentProviders.All.ToHashSet(), Build = "test" }, null, clock: () => now, zone: TimeZoneInfo.Utc);
        var finished = new List<AgentFinished>();
        engine.TaskFinished += (_, f) => finished.Add(f);
        engine.Initialize();
        Assert.Single(engine.BuildSnapshot().Live);
        platform.Processes[4242] = AgentProcessState.Dead;
        engine.Poll();
        Assert.Empty(engine.BuildSnapshot().Live);
        Assert.Empty(finished);

        // Offline: 20 s of awake time without a network ends Claude turns.
        File.Delete(Path.Combine(sessions, "4242.json"));
        Directory.Delete(sessions, true);
        Append(log, ClaudeUser(T0.AddMinutes(1), "again"));
        now = T0.AddMinutes(1);
        engine.Poll();
        Assert.Single(engine.BuildSnapshot().Live);
        platform.Network = false;
        engine.Poll();
        platform.Awake += TimeSpan.FromSeconds(19);
        engine.Poll();
        Assert.Single(engine.BuildSnapshot().Live);
        platform.Awake += TimeSpan.FromSeconds(1);
        engine.Poll();
        Assert.Empty(engine.BuildSnapshot().Live);
        Assert.Empty(finished);
    }

    [Fact]
    public void Resuming_from_the_archive_equals_reading_everything_from_scratch()
    {
        using var temp = new TempFolder();
        var claude = ClaudeLog(temp);
        var codex = temp.Combine("home", ".codex", "sessions", "2026", "10", "09", "rollout-a.jsonl");
        var gone = ClaudeLog(temp, "s-gone");
        var copilot = temp.Combine("home", ".copilot", "session-state", "sess-1", "events.jsonl");
        Write(claude, ClaudeUser(T0, "a"), ClaudeAssistant(T0.AddMinutes(1), "m1", "r1", "tool_use", output: 10));
        Write(codex, CodexMeta(T0), CodexContext(T0), CodexUsage(T0.AddMinutes(1), "x1", 100, 20, 0, 5));
        Write(gone, ClaudeAssistant(T0, "shared", "req", "end_turn", output: 50));
        Write(copilot, Copilot(T0, "session.start", new { sessionId = "sess-1", selectedModel = "gpt-5" }), Copilot(T0, "assistant.message", new { messageId = "c1" }));
        var archive = temp.Combine("local", "agent-usage.bin");
        var (first, _, _, _) = Create(temp, T0.AddMinutes(2), archive);
        first.Initialize();
        first.SaveArchive();

        // Between runs: logs grow, one log disappears, another is rewritten.
        Append(claude, ClaudeAssistant(T0.AddMinutes(3), "m2", "r2", "end_turn", output: 20), ClaudeAssistant(T0.AddMinutes(3), "shared", "req", "end_turn", output: 30));
        Append(codex, CodexUsage(T0.AddMinutes(3), "x2", 300, 100, 50, 7));
        File.Delete(gone);
        Write(copilot, Copilot(T0, "session.start", new { sessionId = "sess-1", selectedModel = "gpt-5" }), Copilot(T0, "assistant.message", new { messageId = "c9" }));

        var (resumed, _, _, _) = Create(temp, T0.AddMinutes(4), archive);
        resumed.Initialize();
        var (fresh, _, _, _) = Create(temp, T0.AddMinutes(4));
        fresh.Initialize();
        string Describe(AgentUsageEngine engine) => string.Join('\n', engine.Store.Records.Values.OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Key}|{r.Tokens}|{r.Cost}|{r.Model}|{r.Requests}")));
        Assert.Equal(Describe(fresh), Describe(resumed));
        Assert.Contains("claude:shared:req", Describe(resumed));
        Assert.Equal(30, resumed.Store.Records["claude:shared:req"].Tokens.Output);
    }

    [Fact]
    public void A_changed_provider_set_or_build_ignores_the_archive()
    {
        using var temp = new TempFolder();
        Write(ClaudeLog(temp), ClaudeAssistant(T0, "m1", "r1", "end_turn", output: 10));
        var archive = temp.Combine("local", "agent-usage.bin");
        var (first, _, _, _) = Create(temp, T0.AddMinutes(1), archive);
        first.Initialize();
        first.SaveArchive();
        var data = AgentUsageArchive.Load(archive)!;
        Assert.Equal("test", data.Build);
        Assert.Equal(["claude", "codex", "copilot", "opencode"], data.Providers);
        Assert.Single(data.Cursors);

        var (other, _, _, _) = Create(temp, T0.AddMinutes(1), archive, AgentProvider.Claude);
        other.Initialize();
        Assert.Single(other.Store.Records);
        first.Stop(keepArchive: false);
        Assert.False(File.Exists(archive));
    }
}

public class OpenCodeReaderTests
{
    private static void Seed(string path, DateTimeOffset t)
    {
        using var db = SqliteDatabase.OpenReadWrite(path);
        db.Execute("CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT, parent_id TEXT)");
        db.Execute("CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT)");
        db.Execute("CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT, session_id TEXT, data TEXT)");
        db.Execute(@"INSERT INTO session VALUES ('ses_1', 'C:\Users\dev\code\site', NULL), ('ses_sub', 'C:\Users\dev\code\site', 'ses_1')");
        Insert(db, "msg_u1", "ses_1", t, """{"role":"user","time":{"created":0}}""");
        Insert(db, "msg_a1", "ses_1", t.AddSeconds(5), """{"role":"assistant","parentID":"msg_u1","modelID":"claude-sonnet-4-6","tokens":{"input":100,"output":40,"reasoning":10,"cache":{"read":500,"write":20}},"cost":0.01,"finish":"tool-calls","time":{"created":0,"completed":1},"path":{"cwd":"C:\\Users\\dev\\code\\site"}}""");
        Insert(db, "msg_s1", "ses_sub", t.AddSeconds(6), """{"role":"assistant","parentID":"x","modelID":"local-model","tokens":{"input":10,"output":5,"reasoning":0,"cache":{"read":0,"write":0}},"cost":0.002,"time":{"created":0,"completed":1}}""");
    }

    private static void Insert(SqliteDatabase db, string id, string session, DateTimeOffset t, string data)
    {
        using var statement = db.Prepare("INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?3, ?4)");
        statement.Bind(1, id).Bind(2, session).Bind(3, t.ToUnixTimeMilliseconds()).Bind(4, data);
        statement.Step();
    }

    [Fact]
    public void Rows_become_usage_and_turns_incrementally()
    {
        Assert.SkipUnless(SqliteNative.IsAvailable, "SQLite is not available on this machine.");
        using var temp = new TempFolder();
        var path = temp.Combine("opencode.db");
        Seed(path, T0);
        var reader = new OpenCodeReader(path);
        var platform = new PortableAgentUsagePlatform();
        var entries = new List<AgentEntry>();
        Assert.True(reader.Read(entries, platform.FileIdentity, T0.AddMinutes(1)));
        var records = entries.OfType<UsageEntry>().Select(e => e.Record).ToList();
        Assert.Equal(2, records.Count);
        var main = records.Single(r => r.Key == "opencode:ses_1:msg_a1");
        Assert.Equal(new TokenCounts(100, 20, 500, 50, 10), main.Tokens);
        Assert.Equal("site", main.Project);
        Assert.Equal(0.01, main.ReportedCost);
        var sub = records.Single(r => r.Key == "opencode:ses_sub:msg_s1");
        Assert.Equal("ses_1", sub.Session);
        Assert.Contains(entries, e => e is TurnBeganEntry && e.TurnKey == reader.TurnKey("ses_1"));
        Assert.Contains(entries, e => e is TurnSettledEntry);

        // The final reply ends the turn; only new rows are read.
        using (var db = SqliteDatabase.OpenReadWrite(path))
        {
            Insert(db, "msg_a2", "ses_1", T0.AddSeconds(30), """{"role":"assistant","parentID":"msg_u1","modelID":"claude-sonnet-4-6","tokens":{"input":5,"output":5,"reasoning":0,"cache":{"read":0,"write":0}},"finish":"stop","time":{"created":0,"completed":1}}""");
        }

        entries.Clear();
        reader.Read(entries, platform.FileIdentity, T0.AddMinutes(2));
        Assert.Single(entries.OfType<UsageEntry>());
        Assert.True(entries.OfType<TurnEndedEntry>().Single().Completed);

        // An unknown model keeps OpenCode's own cost.
        var store = new AgentUsageStore();
        store.AddRecord(sub.Clone(), path);
        Assert.Equal(0.002, store.Records[sub.Key].Cost);
    }

    [Fact]
    public void A_revert_that_removes_every_known_row_starts_over()
    {
        Assert.SkipUnless(SqliteNative.IsAvailable, "SQLite is not available on this machine.");
        using var temp = new TempFolder();
        var path = temp.Combine("opencode.db");
        Seed(path, T0);
        var reader = new OpenCodeReader(path);
        var platform = new PortableAgentUsagePlatform();
        reader.Read([], platform.FileIdentity, T0.AddMinutes(1));
        using (var db = SqliteDatabase.OpenReadWrite(path))
        {
            db.Execute("DELETE FROM message");
            Insert(db, "msg_new", "ses_1", T0.AddMinutes(2), """{"role":"user"}""");
        }

        var entries = new List<AgentEntry>();
        reader.Read(entries, platform.FileIdentity, T0.AddMinutes(3));
        Assert.IsType<ResetEntry>(entries[0]);
        Assert.Contains(entries, e => e is TurnBeganEntry);
    }
}
