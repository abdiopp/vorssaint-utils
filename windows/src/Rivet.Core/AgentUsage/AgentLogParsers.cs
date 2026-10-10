// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rivet.Core.Agents;

/// <summary>
/// Turns complete log lines into <see cref="AgentEntry"/> values. One instance
/// per file; its small state (session, project, open turn…) is saved in the
/// archive so a resumed read continues exactly where it stopped.
/// </summary>
public interface IAgentLogParser
{
    AgentProvider Provider { get; }

    void Parse(ReadOnlyMemory<byte> line, DateTimeOffset fileTime, List<AgentEntry> output);

    /// <summary>The store ended this file's turn silently (dead process, offline): the next prompt opens a new one.</summary>
    void ForgetTurn();

    string SaveState();

    void LoadState(string state);
}

public static class AgentLogParsers
{
    internal static readonly JsonSerializerOptions StateJson = new() { IncludeFields = false };

    public static IAgentLogParser Create(AgentProvider provider, string path) => provider switch
    {
        AgentProvider.Claude => new ClaudeTranscriptParser(path),
        AgentProvider.Codex => new CodexRolloutParser(path),
        AgentProvider.Copilot => new CopilotEventParser(path),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>
    /// The folder name of a working directory, cut before
    /// <c>.claude/worktrees/</c>; both separators are understood.
    /// </summary>
    public static string ProjectName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return string.Empty;
        }

        var path = cwd.Trim().Replace('\\', '/');
        var worktree = path.IndexOf("/.claude/worktrees/", StringComparison.OrdinalIgnoreCase);
        if (worktree >= 0)
        {
            path = path[..worktree];
        }

        path = path.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.EndsWith(':') ? string.Empty : name;
    }

    internal static bool Contains(ReadOnlySpan<byte> line, ReadOnlySpan<byte> marker) => line.IndexOf(marker) >= 0;

    internal static JsonDocument? TryParse(ReadOnlyMemory<byte> line)
    {
        try
        {
            return JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Unix(DateTimeOffset time) => time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}

/// <summary>Claude Code transcripts (spec 07 §3.8.5 "Claude Code").</summary>
public sealed class ClaudeTranscriptParser : IAgentLogParser
{
    private static readonly byte[] AssistantMarker = "\"type\":\"assistant\""u8.ToArray();
    private static readonly byte[] UserMarker = "\"type\":\"user\""u8.ToArray();
    private ParserState _state = new();

    public ClaudeTranscriptParser(string path)
    {
        // Subagent transcripts (…/<session>/subagents/*.jsonl) count usage but never drive turns.
        TracksTurns = !path.Replace('\\', '/').Contains("/subagents/", StringComparison.OrdinalIgnoreCase);
    }

    public AgentProvider Provider => AgentProvider.Claude;

    public bool TracksTurns { get; }

    public void Parse(ReadOnlyMemory<byte> line, DateTimeOffset fileTime, List<AgentEntry> output)
    {
        var span = line.Span;
        var assistant = AgentLogParsers.Contains(span, AssistantMarker);
        if (!assistant && !AgentLogParsers.Contains(span, UserMarker))
        {
            return;
        }

        using var document = AgentLogParsers.TryParse(line);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var root = document.RootElement;
        switch (root.Get("type").String())
        {
            case "assistant":
                Assistant(root, fileTime, output);
                break;
            case "user":
                User(root, fileTime, output);
                break;
        }
    }

    public void ForgetTurn() => _state.TurnOpen = false;

    public string SaveState() => JsonSerializer.Serialize(_state, AgentLogParsers.StateJson);

    public void LoadState(string state) => _state = JsonSerializer.Deserialize<ParserState>(state, AgentLogParsers.StateJson) ?? new ParserState();

    private void Adopt(JsonElement root)
    {
        if (root.Get("sessionId").String() is { Length: > 0 } session)
        {
            _state.Session = session;
        }

        if (root.Get("cwd").String() is { Length: > 0 } cwd)
        {
            _state.Project = AgentLogParsers.ProjectName(cwd);
        }
    }

    private void Assistant(JsonElement root, DateTimeOffset fileTime, List<AgentEntry> output)
    {
        Adopt(root);
        var time = root.Get("timestamp").Time() ?? fileTime;
        var message = root.Get("message");
        var model = message.Get("model").String() ?? string.Empty;
        if (message.Get("usage") is { ValueKind: JsonValueKind.Object } usage && model.Length > 0 && !model.StartsWith('<'))
        {
            var id = message.Get("id").String() ?? string.Empty;
            var request = root.Get("requestId").String() ?? string.Empty;
            var key = id.Length == 0 && request.Length == 0
                ? $"claude:{_state.Session}:{AgentLogParsers.Unix(time)}"
                : $"claude:{id}:{request}";
            output.Add(new UsageEntry(new UsageRecord
            {
                Key = key,
                Provider = AgentProvider.Claude,
                Date = time,
                Model = model,
                Project = _state.Project,
                Session = _state.Session,
                Tokens = new TokenCounts(
                    usage.Get("input_tokens").Count(),
                    usage.Get("cache_creation_input_tokens").Count(),
                    usage.Get("cache_read_input_tokens").Count(),
                    usage.Get("output_tokens").Count(),
                    usage.Path("output_tokens_details", "thinking_tokens").Count()),
                LongCacheWrite = usage.Path("cache_creation", "ephemeral_1h_input_tokens").Count(),
                Fast = usage.Get("speed").String() == "fast",
                UsOnly = usage.Get("inference_geo").String() == "us",
                WebSearches = usage.Path("server_tool_use", "web_search_requests").Count(),
            }));
        }

        if (root.Get("isSidechain").IsTrue() || !TracksTurns)
        {
            return;
        }

        var stop = message.Get("stop_reason").String();
        if (stop is "end_turn" or "stop_sequence" or "max_tokens" or "refusal")
        {
            if (_state.TurnOpen)
            {
                var completed = !root.Get("isApiErrorMessage").IsTrue() && !model.StartsWith('<');
                output.Add(new TurnEndedEntry(time, completed, null));
                _state.TurnOpen = false;
            }

            return;
        }

        if (!_state.TurnOpen)
        {
            output.Add(new TurnBeganEntry(time, _state.Project, model, _state.Session));
            _state.TurnOpen = true;
        }
        else
        {
            output.Add(new TurnActiveEntry(time, IsReply: true));
        }

        if (model.Length > 0)
        {
            output.Add(new TurnContextEntry(model, _state.Project));
        }

        if (message.Get("content") is { ValueKind: JsonValueKind.Array } content)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.Get("type").String() == "tool_use" && block.Get("name").String() == "Bash" && block.Get("id").String() is { } toolId)
                {
                    output.Add(new RunningCommandEntry(toolId, null, false));
                }
            }
        }
    }

    private void User(JsonElement root, DateTimeOffset fileTime, List<AgentEntry> output)
    {
        Adopt(root);
        if (root.Get("isSidechain").IsTrue() || !TracksTurns)
        {
            return;
        }

        var time = root.Get("timestamp").Time() ?? fileTime;
        var content = root.Path("message", "content");
        var text = FirstText(content);
        if (text is not null && (text.StartsWith("[Request interrupted by user", StringComparison.Ordinal) || text.StartsWith("<local-command-std", StringComparison.Ordinal)))
        {
            if (_state.TurnOpen)
            {
                output.Add(new TurnEndedEntry(time, false, null));
                _state.TurnOpen = false;
            }

            return;
        }

        if (root.Get("toolEndsTurn").IsTrue())
        {
            if (_state.TurnOpen)
            {
                output.Add(new TurnEndedEntry(time, true, null));
                _state.TurnOpen = false;
            }

            return;
        }

        if (_state.TurnOpen)
        {
            output.Add(new TurnActiveEntry(fileTime));
            var sawResult = false;
            if (content is { ValueKind: JsonValueKind.Array } blocks)
            {
                foreach (var block in blocks.EnumerateArray())
                {
                    if (block.Get("type").String() == "tool_result")
                    {
                        sawResult = true;
                        output.Add(new RunningCommandEntry(null, block.Get("tool_use_id").String(), false));
                    }
                }
            }

            if (!sawResult)
            {
                output.Add(new RunningCommandEntry(null, null, true));
            }

            return;
        }

        if (!root.Get("isMeta").IsTrue())
        {
            output.Add(new TurnBeganEntry(time, _state.Project, string.Empty, _state.Session));
            _state.TurnOpen = true;
        }
    }

    private static string? FirstText(JsonElement? content)
    {
        if (content is { ValueKind: JsonValueKind.String } s)
        {
            return s.GetString();
        }

        if (content is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var block in array.EnumerateArray())
            {
                if (block.Get("type").String() == "text")
                {
                    return block.Get("text").String();
                }
            }
        }

        return null;
    }

    private sealed class ParserState
    {
        public string Session { get; set; } = string.Empty;

        public string Project { get; set; } = string.Empty;

        public bool TurnOpen { get; set; }
    }
}

/// <summary>Codex rollouts (spec 07 §3.8.5 "Codex").</summary>
public sealed class CodexRolloutParser : IAgentLogParser
{
    private static readonly byte[] TypeMarker = "\"type\":\""u8.ToArray();
    private static readonly HashSet<string> RecordTypes = new(StringComparer.Ordinal) { "token_usage_record", "turn_context", "session_meta", "thread_settings_applied" };
    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal) { "token_count", "task_started", "task_complete", "turn_aborted", "thread_settings_applied" };
    private ParserState _state = new();

    public CodexRolloutParser(string path)
    {
        // Side threads (file names with "_") count usage but never drive turns.
        TracksTurns = !Path.GetFileName(path.Replace('\\', '/').Split('/')[^1]).Contains('_');
    }

    public AgentProvider Provider => AgentProvider.Codex;

    public bool TracksTurns { get; }

    /// <summary>
    /// The first <c>"type":"…"</c> of the line (and, for event_msg, the next
    /// one) must name a record this reader understands, so records quoted
    /// inside history or tool output are never read.
    /// </summary>
    public static (string? Type, string? Inner) Sniff(ReadOnlySpan<byte> line)
    {
        var first = ReadType(line, out var after);
        if (first is null)
        {
            return (null, null);
        }

        if (first == "event_msg")
        {
            var inner = ReadType(line[after..], out _);
            return inner is not null && EventTypes.Contains(inner) ? (first, inner) : (null, null);
        }

        return RecordTypes.Contains(first) ? (first, null) : (null, null);
    }

    public void Parse(ReadOnlyMemory<byte> line, DateTimeOffset fileTime, List<AgentEntry> output)
    {
        var (type, inner) = Sniff(line.Span);
        if (type is null)
        {
            return;
        }

        using var document = AgentLogParsers.TryParse(line);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var root = document.RootElement;
        var time = root.Get("timestamp").Time() ?? fileTime;
        var payload = root.Get("payload");
        if (payload is not { ValueKind: JsonValueKind.Object } p)
        {
            return;
        }

        switch (inner ?? type)
        {
            case "session_meta":
                if (p.Get("id").String() is { Length: > 0 } id) _state.Session = id;
                if (p.Get("cwd").String() is { Length: > 0 } cwd) _state.Project = AgentLogParsers.ProjectName(cwd);
                break;
            case "turn_context":
                if (p.Get("model").String() is { Length: > 0 } model) _state.Model = model;
                if (p.Get("cwd").String() is { Length: > 0 } turnCwd) _state.Project = AgentLogParsers.ProjectName(turnCwd);
                if (p.Get("service_tier").String() is { } tier) _state.Fast = IsFastTier(tier);
                output.Add(new TurnContextEntry(_state.Model, _state.Project));
                break;
            case "thread_settings_applied":
                if (p.Path("thread_settings", "service_tier").String() is { } threadTier) _state.Fast = IsFastTier(threadTier);
                break;
            case "token_usage_record":
                UsageRecord(p, time, output);
                break;
            case "token_count":
                TokenCount(p, time, output);
                break;
            case "task_started":
                if (TracksTurns)
                {
                    output.Add(new TurnBeganEntry(p.Get("started_at").Time() ?? time, _state.Project, _state.Model, _state.Session));
                }

                break;
            case "task_complete":
            case "turn_aborted":
                if (TracksTurns)
                {
                    TimeSpan? duration = p.Get("duration_ms").Number() is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
                    output.Add(new TurnEndedEntry(p.Get("completed_at").Time() ?? time, (inner ?? type) == "task_complete", duration));
                }

                break;
        }
    }

    public void ForgetTurn()
    {
        // Codex turns are opened by task_started, not by parser state.
    }

    public string SaveState() => JsonSerializer.Serialize(_state, AgentLogParsers.StateJson);

    public void LoadState(string state) => _state = JsonSerializer.Deserialize<ParserState>(state, AgentLogParsers.StateJson) ?? new ParserState();

    /// <summary>Limits from <c>rate_limits</c>: the main bucket only; primary/secondary slots, else the individual limit.</summary>
    public static ProviderLimits? Limits(JsonElement rateLimits, DateTimeOffset lineTime)
    {
        if (rateLimits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var limitId = rateLimits.Get("limit_id").String();
        if (!string.IsNullOrEmpty(limitId) && limitId != "codex")
        {
            return null;
        }

        var windows = new List<LimitWindow>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (rateLimits.Get(slot) is { ValueKind: JsonValueKind.Object } s && Window(s, lineTime) is { } window)
            {
                windows.Add(window);
            }
        }

        if (windows.Count == 0 && rateLimits.Get("individual_limit") is { ValueKind: JsonValueKind.Object } individual
            && individual.Get("remaining_percent") is { ValueKind: JsonValueKind.Number } remaining && remaining.TryGetDouble(out var left))
        {
            windows.Add(new LimitWindow
            {
                Id = "codex.individual",
                Kind = LimitWindowKind.Other,
                UsedPercent = Math.Clamp(100 - left, 0, 100),
                ResetsAt = individual.Get("resets_at").Time(),
            });
        }

        return windows.Count == 0 ? null : new ProviderLimits(AgentProvider.Codex, windows, lineTime, LimitSource.Log);
    }

    /// <summary>Codex window kind by length: ≤ 720 min session; 8,640–11,520 weekly; else other.</summary>
    public static LimitWindowKind KindOf(int minutes) => minutes switch
    {
        <= 720 => LimitWindowKind.Session,
        >= 8640 and <= 11520 => LimitWindowKind.Weekly,
        _ => LimitWindowKind.Other,
    };

    private static LimitWindow? Window(JsonElement slot, DateTimeOffset lineTime)
    {
        if (slot.Get("used_percent") is not { ValueKind: JsonValueKind.Number } used || !used.TryGetDouble(out var percent))
        {
            return null;
        }

        var minutes = slot.Get("window_minutes").Number() is { } m ? (int)m : (int?)null;
        var resets = slot.Get("resets_at").Time();
        if (resets is null && slot.Get("resets_in_seconds").Number() is { } inSeconds)
        {
            resets = lineTime + TimeSpan.FromSeconds(inSeconds);
        }

        return new LimitWindow
        {
            Id = minutes is { } length ? $"codex.{length}" : "codex.window",
            Kind = minutes is { } len ? KindOf(len) : LimitWindowKind.Other,
            Minutes = minutes,
            UsedPercent = Math.Clamp(percent, 0, 100),
            ResetsAt = resets,
        };
    }

    private static bool IsFastTier(string tier) =>
        tier.Equals("fast", StringComparison.OrdinalIgnoreCase) || tier.Equals("priority", StringComparison.OrdinalIgnoreCase);

    private static string? ReadType(ReadOnlySpan<byte> line, out int end)
    {
        end = 0;
        var at = line.IndexOf(TypeMarker);
        if (at < 0)
        {
            return null;
        }

        var start = at + TypeMarker.Length;
        var length = line[start..].IndexOf((byte)'"');
        if (length <= 0 || length > 64)
        {
            return null;
        }

        end = start + length + 1;
        return Encoding.UTF8.GetString(line.Slice(start, length));
    }

    private void UsageRecord(JsonElement p, DateTimeOffset time, List<AgentEntry> output)
    {
        _state.HasRecords = true;
        if (string.IsNullOrEmpty(_state.Session) && p.Get("session_id").String() is { Length: > 0 } sid)
        {
            _state.Session = sid;
        }

        var usage = p.Get("usage");
        var tokens = Map(usage.Get("input_tokens").Count(), usage.Get("cached_input_tokens").Count(), usage.Get("cache_write_input_tokens").Count(),
            usage.Get("output_tokens").Count(), usage.Get("reasoning_output_tokens").Count());
        var response = p.Get("response_id").String();
        var key = string.IsNullOrEmpty(response) ? $"codex:{_state.Session}:{AgentLogParsers.Unix(time)}" : $"codex:{response}";
        output.Add(new UsageEntry(NewRecord(key, time, tokens)));
    }

    private void TokenCount(JsonElement p, DateTimeOffset time, List<AgentEntry> output)
    {
        if (p.Get("rate_limits") is { ValueKind: JsonValueKind.Object } rateLimits)
        {
            if (Limits(rateLimits, time) is { } limits)
            {
                output.Add(new LimitsEntry(limits));
            }

            if (rateLimits.Get("plan_type").String() is { Length: > 0 } plan)
            {
                output.Add(new PlanEntry(AgentProvider.Codex, plan, time));
            }
        }

        if (p.Get("plan_type").String() is { Length: > 0 } payloadPlan)
        {
            output.Add(new PlanEntry(AgentProvider.Codex, payloadPlan, time));
        }

        // Legacy fallback while this file has shown no per-response records.
        if (_state.HasRecords || p.Get("info") is not { ValueKind: JsonValueKind.Object } info)
        {
            return;
        }

        var totalUsage = info.Get("total_token_usage");
        if (totalUsage is not { ValueKind: JsonValueKind.Object })
        {
            return;
        }

        var total = totalUsage.Get("total_tokens").Count();
        if (total == 0)
        {
            total = totalUsage.Get("input_tokens").Count() + totalUsage.Get("output_tokens").Count();
        }

        if (total <= _state.LastTotal)
        {
            return;
        }

        var totals = Raw(totalUsage);
        var last = info.Get("last_token_usage");
        var delta = last is { ValueKind: JsonValueKind.Object } ? Raw(last) : totals - _state.LastTotals;
        _state.LastTotal = total;
        _state.LastTotals = totals;
        var tokens = Map(delta.Input, delta.CacheRead, delta.CacheWrite, delta.Output, delta.Reasoning);
        output.Add(new UsageEntry(NewRecord($"codex:{_state.Session}:total:{total.ToString(CultureInfo.InvariantCulture)}", time, tokens)));
    }

    /// <summary>Raw OpenAI counters (input includes cached/written) carried in a TokenCounts.</summary>
    private static TokenCounts Raw(JsonElement? usage) => new(
        usage.Get("input_tokens").Count(),
        usage.Get("cache_write_input_tokens").Count(),
        usage.Get("cached_input_tokens").Count(),
        usage.Get("output_tokens").Count(),
        usage.Get("reasoning_output_tokens").Count());

    /// <summary>OpenAI input includes cached and written tokens: split them out (spec "Token mapping").</summary>
    public static TokenCounts Map(long inputTokens, long cached, long written, long output, long reasoning)
    {
        var cacheRead = Math.Min(inputTokens, cached);
        var cacheWrite = Math.Min(Math.Max(0, inputTokens - cacheRead), written);
        var input = Math.Max(0, inputTokens - cacheRead - cacheWrite);
        return new TokenCounts(input, cacheWrite, cacheRead, output, reasoning);
    }

    private UsageRecord NewRecord(string key, DateTimeOffset time, TokenCounts tokens) => new()
    {
        Key = key,
        Provider = AgentProvider.Codex,
        Date = time,
        Model = _state.Model,
        Project = _state.Project,
        Session = _state.Session,
        Tokens = tokens,
        Fast = _state.Fast,
    };

    private sealed class ParserState
    {
        public string Session { get; set; } = string.Empty;

        public string Project { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public bool Fast { get; set; }

        public bool HasRecords { get; set; }

        public long LastTotal { get; set; }

        public TokenCounts LastTotals { get; set; }
    }
}

/// <summary>GitHub Copilot CLI event logs (spec 07 §3.8.5 "GitHub Copilot CLI").</summary>
public sealed class CopilotEventParser : IAgentLogParser
{
    private ParserState _state = new();

    public CopilotEventParser(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        _state.Session = parts.Length >= 2 ? parts[^2] : string.Empty;
    }

    public AgentProvider Provider => AgentProvider.Copilot;

    /// <summary>
    /// The strict envelope check: one complete object whose top-level keys are
    /// unique and at most 256 bytes, nested at most 128 levels.
    /// </summary>
    public static bool IsValidEnvelope(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 128 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    // Nothing but whitespace may follow.
                    return !reader.Read();
                }

                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    var length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
                    if (length > 256 || !keys.Add(reader.GetString()!))
                    {
                        return false;
                    }

                    reader.Skip();
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Parse(ReadOnlyMemory<byte> line, DateTimeOffset fileTime, List<AgentEntry> output)
    {
        if (!IsValidEnvelope(line.Span))
        {
            return;
        }

        using var document = AgentLogParsers.TryParse(line);
        if (document is null)
        {
            return;
        }

        var root = document.RootElement;
        var type = root.Get("type").String();
        var time = root.Get("timestamp").Time() ?? fileTime;
        var data = root.Get("data");
        switch (type)
        {
            case "session.start":
                if (data.Get("sessionId").String() is { Length: > 0 } session) _state.Session = session;
                if (data.Get("selectedModel").String() is { Length: > 0 } selected) _state.Model = selected;
                UpdateProject(data.Get("context"));
                break;
            case "session.context_changed":
                UpdateProject(data.Get("context") ?? data);
                break;
            case "session.model_change":
                if (data.Get("newModel").String() is { Length: > 0 } model) _state.Model = model;
                output.Add(new TurnContextEntry(_state.Model, _state.Project));
                break;
            case "user.message":
            case "assistant.turn_start":
                if (type == "assistant.turn_start" && data.Get("turnId").String() is { } turnId)
                {
                    _state.TurnId = turnId;
                }

                if (_state.TurnOpen)
                {
                    output.Add(new TurnActiveEntry(time));
                }
                else
                {
                    output.Add(new TurnBeganEntry(time, _state.Project, _state.Model, _state.Session));
                    _state.TurnOpen = true;
                }

                _state.FinalResponse = false;
                break;
            case "assistant.message":
                Message(root, data, time, output);
                break;
            case "assistant.turn_end":
                if (_state.TurnOpen && _state.FinalResponse && data.Get("turnId").String() == _state.TurnId)
                {
                    output.Add(new TurnEndedEntry(time, true, null));
                    _state.TurnOpen = false;
                }

                break;
            case "abort":
                if (_state.TurnOpen)
                {
                    output.Add(new TurnEndedEntry(time, false, null));
                    _state.TurnOpen = false;
                }

                break;
            case "session.shutdown":
                Shutdown(root, data, time, output);
                if (_state.TurnOpen)
                {
                    output.Add(new TurnEndedEntry(time, false, null));
                    _state.TurnOpen = false;
                }

                break;
        }
    }

    public void ForgetTurn() => _state.TurnOpen = false;

    public string SaveState() => JsonSerializer.Serialize(_state, AgentLogParsers.StateJson);

    public void LoadState(string state) => _state = JsonSerializer.Deserialize<ParserState>(state, AgentLogParsers.StateJson) ?? new ParserState();

    private void UpdateProject(JsonElement? context)
    {
        var project = context.Get("gitRoot").String() ?? context.Get("cwd").String() ?? context.Get("repository").String();
        if (!string.IsNullOrEmpty(project))
        {
            _state.Project = AgentLogParsers.ProjectName(project);
        }
    }

    private void Message(JsonElement root, JsonElement? data, DateTimeOffset time, List<AgentEntry> output)
    {
        var agent = root.Get("agentId").String() is { Length: > 0 } agentId ? $"agent:{agentId}" : "root";
        var call = data.Get("apiCallId").String() is { Length: > 0 } api
            ? $"api:{api}"
            : $"message:{data.Get("messageId").String() ?? root.Get("id").String() ?? AgentLogParsers.Unix(time)}";
        var model = _state.Model;
        _state.ActivityRequests[model] = _state.ActivityRequests.GetValueOrDefault(model) + 1;
        output.Add(new UsageEntry(new UsageRecord
        {
            Key = $"copilot:{_state.Session}:{agent}:{call}",
            Provider = AgentProvider.Copilot,
            Date = time,
            Model = model,
            Project = _state.Project,
            Session = _state.Session,
            Requests = 1,
            IsAggregate = true,
        }));
        var tools = data.Get("toolRequests");
        var phase = data.Get("phase").String();
        _state.FinalResponse = (tools is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0)
                               && phase is not ("thinking" or "commentary");
        if (_state.TurnOpen)
        {
            output.Add(new TurnActiveEntry(time, IsReply: true));
        }
    }

    private void Shutdown(JsonElement root, JsonElement? data, DateTimeOffset time, List<AgentEntry> output)
    {
        if (data.Get("modelMetrics") is not { ValueKind: JsonValueKind.Object } metrics)
        {
            return;
        }

        var shutdownId = root.Get("id").String() ?? AgentLogParsers.Unix(time);
        foreach (var model in metrics.EnumerateObject())
        {
            var m = model.Value;
            var details = m.Get("tokenDetails");
            TokenCounts totals;
            if (details is { ValueKind: JsonValueKind.Object })
            {
                totals = new TokenCounts(
                    details.Path("input", "tokenCount").Count(),
                    details.Path("cache_write", "tokenCount").Count(),
                    details.Path("cache_read", "tokenCount").Count(),
                    details.Path("output", "tokenCount").Count());
            }
            else
            {
                var usage = m.Get("usage");
                totals = new TokenCounts(usage.Get("inputTokens").Count(), usage.Get("cacheWriteTokens").Count(), usage.Get("cacheReadTokens").Count(), usage.Get("outputTokens").Count());
            }

            var requests = m.Path("requests", "count").Count();

            // Cumulative per session: only growth since the previous shutdown counts; a drop means new totals.
            var previous = _state.Shutdowns.GetValueOrDefault(model.Name);
            var grew = totals.Total >= previous.Tokens.Total && requests >= previous.Requests;
            var tokenDelta = grew ? totals - previous.Tokens : totals;
            var requestDelta = grew ? requests - previous.Requests : requests;
            _state.Shutdowns[model.Name] = new ShutdownTotals(totals, requests);

            // Requests already counted as activity records are not counted twice.
            var counted = _state.ActivityRequests.GetValueOrDefault(model.Name);
            var extraRequests = Math.Max(0, requestDelta - counted);
            _state.ActivityRequests[model.Name] = Math.Max(0, counted - requestDelta);
            if (tokenDelta.Total == 0 && extraRequests == 0)
            {
                continue;
            }

            output.Add(new UsageEntry(new UsageRecord
            {
                Key = $"copilot:{_state.Session}:{shutdownId}:{model.Name}",
                Provider = AgentProvider.Copilot,
                Date = time,
                Model = model.Name,
                Project = _state.Project,
                Session = _state.Session,
                Requests = extraRequests,
                Tokens = tokenDelta,
                IsAggregate = true,
            }));
        }
    }

    public readonly record struct ShutdownTotals(TokenCounts Tokens, long Requests);

    private sealed class ParserState
    {
        public string Session { get; set; } = string.Empty;

        public string Project { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string? TurnId { get; set; }

        public bool TurnOpen { get; set; }

        public bool FinalResponse { get; set; }

        public Dictionary<string, ShutdownTotals> Shutdowns { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> ActivityRequests { get; set; } = new(StringComparer.Ordinal);
    }
}
