// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Core.Agents;

namespace Rivet.Core.Tests.Agents;

/// <summary>
/// Synthetic log lines in each agent's format. They carry made-up ids,
/// models and counters only, never real transcripts.
/// </summary>
internal static class AgentFixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    // ── Claude Code ─────────────────────────────────────────────────────
    public static string ClaudeUser(DateTimeOffset t, string text, string session = "s-1", string cwd = @"C:\Users\dev\code\app", bool meta = false, bool sidechain = false) => Json(new Dictionary<string, object?>
    {
        ["parentUuid"] = null,
        ["isSidechain"] = sidechain,
        ["userType"] = "external",
        ["cwd"] = cwd,
        ["sessionId"] = session,
        ["type"] = "user",
        ["isMeta"] = meta ? true : null,
        ["message"] = new { role = "user", content = text },
        ["uuid"] = Guid.NewGuid().ToString(),
        ["timestamp"] = Iso(t),
    });

    public static string ClaudeToolResult(DateTimeOffset t, string toolUseId, string session = "s-1") => Json(new Dictionary<string, object?>
    {
        ["isSidechain"] = false,
        ["cwd"] = @"C:\Users\dev\code\app",
        ["sessionId"] = session,
        ["type"] = "user",
        ["message"] = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content = "(output)" } } },
        ["timestamp"] = Iso(t),
    });

    public static string ClaudeAssistant(DateTimeOffset t, string messageId, string requestId, string stop, long input = 10, long cacheWrite = 0, long cacheRead = 0, long output = 5,
        string model = "claude-sonnet-4-6", string session = "s-1", string cwd = @"C:\Users\dev\code\app", long longWrite = 0, string? speed = null, string? geo = null,
        long webSearches = 0, bool sidechain = false, bool apiError = false, string? bashToolId = null)
    {
        var usage = new JsonObject
        {
            ["input_tokens"] = input,
            ["cache_creation_input_tokens"] = cacheWrite,
            ["cache_read_input_tokens"] = cacheRead,
            ["output_tokens"] = output,
            ["cache_creation"] = new JsonObject { ["ephemeral_5m_input_tokens"] = cacheWrite - longWrite, ["ephemeral_1h_input_tokens"] = longWrite },
            ["server_tool_use"] = new JsonObject { ["web_search_requests"] = webSearches },
        };
        if (speed is not null) usage["speed"] = speed;
        if (geo is not null) usage["inference_geo"] = geo;
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "(reply)" } };
        if (bashToolId is not null)
        {
            content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = bashToolId, ["name"] = "Bash", ["input"] = new JsonObject() });
        }

        var root = new JsonObject
        {
            ["parentUuid"] = null,
            ["isSidechain"] = sidechain,
            ["cwd"] = cwd,
            ["sessionId"] = session,
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["id"] = messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = content,
                ["stop_reason"] = stop,
                ["usage"] = usage,
            },
            ["requestId"] = requestId,
            ["timestamp"] = Iso(t),
        };
        if (apiError) root["isApiErrorMessage"] = true;
        return root.ToJsonString();
    }

    // ── Codex ───────────────────────────────────────────────────────────
    public static string CodexMeta(DateTimeOffset t, string session = "c-1", string cwd = @"C:\Users\dev\code\api") =>
        Json(new { timestamp = Iso(t), type = "session_meta", payload = new { id = session, cwd, originator = "codex_cli_rs" } });

    public static string CodexContext(DateTimeOffset t, string model = "gpt-5-codex", string? tier = null, string cwd = @"C:\Users\dev\code\api") =>
        Json(new { timestamp = Iso(t), type = "turn_context", payload = new { cwd, model, service_tier = tier, approval_policy = "on-request" } });

    public static string CodexTaskStarted(DateTimeOffset t) =>
        Json(new { timestamp = Iso(t), type = "event_msg", payload = new { type = "task_started", model_context_window = 272000 } });

    public static string CodexTaskComplete(DateTimeOffset t, long durationMs) =>
        Json(new { timestamp = Iso(t), type = "event_msg", payload = new { type = "task_complete", duration_ms = durationMs } });

    public static string CodexAborted(DateTimeOffset t) =>
        Json(new { timestamp = Iso(t), type = "event_msg", payload = new { type = "turn_aborted" } });

    public static string CodexUsage(DateTimeOffset t, string responseId, long input, long cached, long written, long output, long reasoning = 0) =>
        Json(new { timestamp = Iso(t), type = "token_usage_record", payload = new { response_id = responseId, session_id = "c-1", usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = written, output_tokens = output, reasoning_output_tokens = reasoning } } });

    public static string CodexTokenCount(DateTimeOffset t, object? rateLimits = null, object? info = null) =>
        Json(new { timestamp = Iso(t), type = "event_msg", payload = new { type = "token_count", info, rate_limits = rateLimits } });

    // ── Copilot CLI ─────────────────────────────────────────────────────
    public static string Copilot(DateTimeOffset t, string type, object data, string? agentId = null) =>
        Json(new Dictionary<string, object?> { ["type"] = type, ["id"] = Guid.NewGuid().ToString(), ["timestamp"] = Iso(t), ["agentId"] = agentId, ["data"] = data });

    // ── Files ───────────────────────────────────────────────────────────
    public static void Write(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    }

    public static void Append(string path, params string[] lines) =>
        File.AppendAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));

    public static List<AgentEntry> Parse(IAgentLogParser parser, params string[] lines)
    {
        var entries = new List<AgentEntry>();
        foreach (var line in lines)
        {
            parser.Parse(Encoding.UTF8.GetBytes(line), T0, entries);
        }

        return entries;
    }
}

/// <summary>A platform for tests: a fake home, controllable network and processes.</summary>
internal sealed class TestAgentPlatform(string root) : PortableAgentUsagePlatform
{
    public Dictionary<string, string> Environment { get; } = [];

    public Dictionary<int, AgentProcessState> Processes { get; } = [];

    public bool Network { get; set; } = true;

    public TimeSpan Awake { get; set; } = TimeSpan.FromHours(1);

    public override string HomeDirectory => Path.Combine(root, "home");

    public override string RoamingAppData => Path.Combine(root, "roaming");

    public override string LocalAppData => Path.Combine(root, "local");

    public override string PidDomain => "win32";

    public override bool IsNetworkAvailable => Network;

    public override TimeSpan AwakeTime => Awake;

    public override string? GetEnvironmentVariable(string name) => Environment.GetValueOrDefault(name);

    public override AgentProcessState ProcessState(int pid, DateTimeOffset? startedBy = null) => Processes.GetValueOrDefault(pid, AgentProcessState.Unknown);
}

/// <summary>A temporary folder deleted after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rivet-agents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
