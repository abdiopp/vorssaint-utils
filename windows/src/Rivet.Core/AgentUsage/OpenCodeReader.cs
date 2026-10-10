// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Agents;

/// <summary>
/// OpenCode usage from its SQLite database (spec 07 §3.8.5 "OpenCode"),
/// opened read-only with a 2 s busy timeout. Rows are read incrementally by
/// rowid; replies still being written are re-queried until they complete;
/// a tail of the last 64 rows detects reverts. With SQLite's JSON functions
/// only counters and ids leave SQL (message text never does); without them
/// the message metadata JSON is parsed here and the tool-call/compaction
/// flags are unavailable. Nothing from OpenCode is archived: the database is
/// read again (91 days) at each start.
/// </summary>
public sealed class OpenCodeReader
{
    public const int TailSize = 64;
    public static readonly TimeSpan OpenReplyLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan SettleQuiet = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IdleQuiet = TimeSpan.FromMinutes(10);
    private const int BatchSize = 2000;

    private readonly LinkedList<(long RowId, string Id)> _tail = new();
    private readonly Dictionary<string, OpenReply> _openReplies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private long _offset = -1;
    private ulong _identity;
    private bool? _json;
    private bool? _hasParts;

    public OpenCodeReader(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    /// <summary>Turn keys look like "&lt;db path&gt;#&lt;root session&gt;".</summary>
    public string TurnKey(string rootSession) => $"{DatabasePath}#{rootSession}";

    /// <summary>Reads new and changed rows. Returns false when the database could not be read.</summary>
    public bool Read(List<AgentEntry> output, Func<string, ulong> identityOf, DateTimeOffset now, CancellationToken cancel = default)
    {
        if (!SqliteNative.IsAvailable || !File.Exists(DatabasePath))
        {
            return false;
        }

        try
        {
            using var db = SqliteDatabase.OpenReadOnly(DatabasePath);
            _json ??= db.TryScalar("SELECT json_extract('{\"a\":1}','$.a')", out var one) && one == "1";
            _hasParts ??= db.TryScalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name='part'", out var hasPart) && hasPart == "1";

            var identity = identityOf(DatabasePath);
            if (_offset < 0 || (identity != 0 && _identity != 0 && identity != _identity))
            {
                if (_offset >= 0)
                {
                    Restart(output);
                }

                _offset = FirstRowInHorizon(db, now - AgentUsageConstants.Horizon) - 1;
            }

            _identity = identity;
            CheckForRevert(db, output, now);
            RequeryOpenReplies(db, output, now);
            while (!cancel.IsCancellationRequested)
            {
                var rows = Query(db, "WHERE m.rowid > ?1 ORDER BY m.rowid LIMIT " + BatchSize.ToString(CultureInfo.InvariantCulture), s => s.Bind(1, _offset));
                foreach (var row in rows)
                {
                    Apply(row, output, now);
                    _offset = row.RowId;
                    _tail.AddLast((row.RowId, row.Id));
                    if (_tail.Count > TailSize)
                    {
                        _tail.RemoveFirst();
                    }
                }

                if (rows.Count < BatchSize)
                {
                    break;
                }
            }

            return true;
        }
        catch (SqliteException ex)
        {
            Log.Warn("agents", "OpenCode database could not be read.", ex);
            return false;
        }
    }

    private void Restart(List<AgentEntry> output)
    {
        output.Add(new ResetEntry());
        _tail.Clear();
        _openReplies.Clear();
        _sessions.Clear();
        _offset = -1;
    }

    /// <summary>Binary search for the first rowid created inside the horizon (rowids grow with time).</summary>
    private static long FirstRowInHorizon(SqliteDatabase db, DateTimeOffset since)
    {
        using var bounds = db.Prepare("SELECT min(rowid), max(rowid) FROM message");
        if (!bounds.Step() || bounds.IsNull(0))
        {
            return 1;
        }

        long low = bounds.Int64(0);
        long high = bounds.Int64(1);
        var target = since.ToUnixTimeMilliseconds();
        using var probe = db.Prepare("SELECT rowid, time_created FROM message WHERE rowid >= ?1 ORDER BY rowid LIMIT 1");
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            probe.Reset();
            probe.Bind(1, mid);
            if (!probe.Step())
            {
                high = mid;
                continue;
            }

            var rowId = probe.Int64(0);
            if (probe.Int64(1) >= target)
            {
                high = Math.Min(high, rowId);
            }
            else
            {
                low = rowId + 1;
            }
        }

        return low;
    }

    /// <summary>A revert removed recent rows: resume after the newest row still present, or start over.</summary>
    private void CheckForRevert(SqliteDatabase db, List<AgentEntry> output, DateTimeOffset now)
    {
        if (_tail.Count == 0)
        {
            return;
        }

        using var check = db.Prepare("SELECT id FROM message WHERE rowid = ?1");
        for (var node = _tail.Last; node is not null; node = node.Previous)
        {
            check.Reset();
            check.Bind(1, node.Value.RowId);
            if (check.Step() && check.Text(0) == node.Value.Id)
            {
                if (node != _tail.Last)
                {
                    while (_tail.Last != node)
                    {
                        _tail.RemoveLast();
                    }

                    _offset = node.Value.RowId;
                }

                return;
            }
        }

        Restart(output);
        _offset = FirstRowInHorizon(db, now - AgentUsageConstants.Horizon) - 1;
    }

    private void RequeryOpenReplies(SqliteDatabase db, List<AgentEntry> output, DateTimeOffset now)
    {
        foreach (var (id, reply) in _openReplies.ToList())
        {
            if (now - reply.SeenAt > OpenReplyLifetime)
            {
                _openReplies.Remove(id);
            }
        }

        if (_openReplies.Count == 0)
        {
            return;
        }

        foreach (var chunk in _openReplies.Keys.Chunk(200))
        {
            var placeholders = string.Join(',', chunk.Select((_, i) => "?" + (i + 1).ToString(CultureInfo.InvariantCulture)));
            var rows = Query(db, $"WHERE m.id IN ({placeholders})", s =>
            {
                for (var i = 0; i < chunk.Length; i++)
                {
                    s.Bind(i + 1, chunk[i]);
                }
            });
            foreach (var row in rows)
            {
                if (_openReplies.TryGetValue(row.Id, out var known) && (known.TimeUpdated != row.TimeUpdated || known.Length != row.DataLength))
                {
                    Apply(row, output, now);
                }
            }
        }
    }

    private List<Row> Query(SqliteDatabase db, string where, Action<SqliteStatement> bind)
    {
        var rows = new List<Row>();
        var sql = _json == true ? JsonSelect() + " " + where : PlainSelect + " " + where;
        using var statement = db.Prepare(sql);
        bind(statement);
        while (statement.Step())
        {
            rows.Add(_json == true ? ReadJsonRow(statement) : ReadPlainRow(statement));
        }

        return rows;
    }

    private string JsonSelect()
    {
        const string data = "m.data";
        var toolCalls = _hasParts == true
            ? $"(json_extract({data},'$.role')='assistant' AND coalesce(json_extract({data},'$.finish'),'') NOT IN ('tool-calls','unknown') AND EXISTS(SELECT 1 FROM part p WHERE p.message_id=m.id AND json_extract(p.data,'$.type')='tool' AND coalesce(json_extract(p.data,'$.metadata.providerExecuted'),json_extract(p.data,'$.providerExecuted'),0)=0 AND coalesce(json_extract(p.data,'$.state.status'),'')<>'error'))"
            : "0";
        var compaction = _hasParts == true
            ? $"(coalesce(json_extract({data},'$.summary'),0) AND EXISTS(SELECT 1 FROM part p WHERE p.message_id=json_extract({data},'$.parentID') AND json_extract(p.data,'$.type')='compaction' AND coalesce(json_extract(p.data,'$.auto'),0)))"
            : "0";
        return "SELECT m.rowid, m.id, m.session_id, m.time_created, m.time_updated, " +
               $"json_extract({data},'$.role'), json_extract({data},'$.parentID'), " +
               $"coalesce(json_extract({data},'$.model_id'), json_extract({data},'$.modelID'), json_extract({data},'$.model.modelID'), json_extract({data},'$.model.id'), CASE WHEN json_type({data},'$.model')='text' THEN json_extract({data},'$.model') END), " +
               $"json_extract({data},'$.tokens.input'), json_extract({data},'$.tokens.output'), json_extract({data},'$.tokens.reasoning'), " +
               $"json_extract({data},'$.tokens.cache.read'), json_extract({data},'$.tokens.cache.write'), " +
               $"json_extract({data},'$.cost'), json_extract({data},'$.finish'), json_extract({data},'$.time.completed') IS NOT NULL, " +
               $"json_extract({data},'$.path.cwd'), coalesce(json_type({data},'$.error'),'null')<>'null', " +
               $"s.parent_id, s.directory, {toolCalls}, {compaction}, length({data}) " +
               "FROM message m LEFT JOIN session s ON s.id = m.session_id";
    }

    private const string PlainSelect =
        "SELECT m.rowid, m.id, m.session_id, m.time_created, m.time_updated, m.data, s.parent_id, s.directory " +
        "FROM message m LEFT JOIN session s ON s.id = m.session_id";

    private static Row ReadJsonRow(SqliteStatement s) => new(
        s.Int64(0), s.Text(1) ?? string.Empty, s.Text(2) ?? string.Empty, s.Int64(3), s.Int64(4),
        s.Text(5), s.Text(6), s.Text(7),
        Count(s.Double(8)), Count(s.Double(9)), Count(s.Double(10)), Count(s.Double(11)), Count(s.Double(12)),
        s.Double(13), s.Text(14), s.Int64(15) != 0, s.Text(16), s.Int64(17) != 0,
        s.Text(18), s.Text(19), s.Int64(20) != 0, s.Int64(21) != 0, (int)s.Int64(22));

    private static Row ReadPlainRow(SqliteStatement s)
    {
        var json = s.Text(5) ?? "{}";
        string? role = null, parent = null, model = null, finish = null, cwd = null;
        long input = 0, output = 0, reasoning = 0, read = 0, write = 0;
        double? cost = null;
        var completed = false;
        var error = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var d = document.RootElement;
            role = d.Get("role").String();
            parent = d.Get("parentID").String();
            model = d.Get("model_id").String() ?? d.Get("modelID").String() ?? d.Path("model", "modelID").String() ?? d.Path("model", "id").String() ?? d.Get("model").String();
            input = d.Path("tokens", "input").Count();
            output = d.Path("tokens", "output").Count();
            reasoning = d.Path("tokens", "reasoning").Count();
            read = d.Path("tokens", "cache", "read").Count();
            write = d.Path("tokens", "cache", "write").Count();
            cost = d.Get("cost").Number();
            finish = d.Get("finish").String();
            completed = d.Path("time", "completed") is { ValueKind: not JsonValueKind.Null };
            cwd = d.Path("path", "cwd").String();
            error = d.Get("error") is { ValueKind: not JsonValueKind.Null };
        }
        catch (JsonException)
        {
        }

        return new Row(s.Int64(0), s.Text(1) ?? string.Empty, s.Text(2) ?? string.Empty, s.Int64(3), s.Int64(4),
            role, parent, model, input, output, reasoning, read, write, cost, finish, completed, cwd, error,
            s.Text(6), s.Text(7), false, false, json.Length);
    }

    private static long Count(double? value) => value is { } v && double.IsFinite(v) && v > 0 ? (long)Math.Min(v, AgentJson.NumberCap) : 0;

    private void Apply(Row row, List<AgentEntry> output, DateTimeOffset now)
    {
        var rootSession = string.IsNullOrEmpty(row.ParentSession) ? row.SessionId : row.ParentSession!;
        var isSubagent = !string.IsNullOrEmpty(row.ParentSession);
        var key = TurnKey(rootSession);
        var time = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, row.TimeCreated));
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, Math.Max(row.TimeCreated, row.TimeUpdated)));
        var project = AgentLogParsers.ProjectName(row.Cwd ?? row.Directory);
        if (!_sessions.TryGetValue(rootSession, out var state))
        {
            _sessions[rootSession] = state = new SessionState();
        }

        if (row.Role == "user")
        {
            if (isSubagent)
            {
                if (state.TurnOpen)
                {
                    output.Add(new TurnActiveEntry(time) { TurnKey = key });
                }

                return;
            }

            if (!state.SeePrompt(row.Id))
            {
                return;
            }

            var quiet = (state.SettledAt is { } settled && time - settled >= SettleQuiet)
                        || (!state.Writing && time - state.LastActivity >= IdleQuiet);
            if (state.TurnOpen && !quiet)
            {
                output.Add(new TurnActiveEntry(time) { TurnKey = key });
                state.LastActivity = time;
                return;
            }

            if (state.TurnOpen)
            {
                output.Add(new TurnEndedEntry(time, false, null) { TurnKey = key });
            }

            output.Add(new TurnBeganEntry(time, project, string.Empty, rootSession) { TurnKey = key });
            state.TurnOpen = true;
            state.ActivePrompt = row.Id;
            state.SettledAt = null;
            state.Writing = false;
            state.LastActivity = time;
            return;
        }

        if (row.Role != "assistant")
        {
            return;
        }

        var tokens = new TokenCounts(row.TokensInput, row.CacheWrite, row.CacheRead, row.TokensOutput + row.TokensReasoning, row.TokensReasoning);
        if (tokens.Total > 0 || row.Cost is > 0)
        {
            output.Add(new UsageEntry(new UsageRecord
            {
                Key = $"opencode:{row.SessionId}:{row.Id}",
                Provider = AgentProvider.OpenCode,
                Date = time,
                Model = row.Model ?? string.Empty,
                Project = project,
                Session = rootSession,
                Tokens = tokens,
                ReportedCost = row.Cost is > 0 ? row.Cost : null,
            }) { TurnKey = key });
        }

        if (!row.Completed)
        {
            _openReplies[row.Id] = new OpenReply(row.TimeUpdated, row.DataLength, now);
        }
        else
        {
            _openReplies.Remove(row.Id);
        }

        if (isSubagent || !state.TurnOpen)
        {
            return;
        }

        if (row.ParentId != state.ActivePrompt)
        {
            // A reply to a newer prompt while the older reply never completed: the task restarts there.
            if (row.ParentId is { } newer && state.Writing && state.HasSeen(newer))
            {
                output.Add(new TurnEndedEntry(updated, false, null) { TurnKey = key });
                output.Add(new TurnBeganEntry(time, project, row.Model ?? string.Empty, rootSession) { TurnKey = key });
                state.ActivePrompt = newer;
                state.SettledAt = null;
            }
            else
            {
                return;
            }
        }

        state.LastActivity = updated;
        state.Writing = !row.Completed;
        if (row.Error)
        {
            output.Add(new TurnEndedEntry(updated, false, null) { TurnKey = key });
            state.TurnOpen = false;
            return;
        }

        var endsTurn = row.Finish is { } finish
            ? finish is not ("tool-calls" or "unknown") && !row.ToolCalls && !row.AutoCompaction && row.Completed
            : row.Completed;
        if (endsTurn)
        {
            output.Add(new TurnEndedEntry(updated, true, null) { TurnKey = key });
            state.TurnOpen = false;
            return;
        }

        if (row.Completed)
        {
            output.Add(new TurnSettledEntry(updated) { TurnKey = key });
            state.SettledAt = updated;
        }
        else
        {
            output.Add(new TurnActiveEntry(updated, IsReply: true) { TurnKey = key });
            state.SettledAt = null;
        }
    }

    private sealed record Row(
        long RowId, string Id, string SessionId, long TimeCreated, long TimeUpdated,
        string? Role, string? ParentId, string? Model,
        long TokensInput, long TokensOutput, long TokensReasoning, long CacheRead, long CacheWrite,
        double? Cost, string? Finish, bool Completed, string? Cwd, bool Error,
        string? ParentSession, string? Directory, bool ToolCalls, bool AutoCompaction, int DataLength);

    private sealed record OpenReply(long TimeUpdated, int Length, DateTimeOffset SeenAt);

    private sealed class SessionState
    {
        private readonly Queue<string> _recentPrompts = new();
        private readonly HashSet<string> _recentSet = new(StringComparer.Ordinal);

        public bool TurnOpen { get; set; }

        public string? ActivePrompt { get; set; }

        public DateTimeOffset? SettledAt { get; set; }

        public bool Writing { get; set; }

        public DateTimeOffset LastActivity { get; set; }

        public bool HasSeen(string promptId) => _recentSet.Contains(promptId);

        /// <summary>Remembers about 100 prompt ids per session; false for a repeat.</summary>
        public bool SeePrompt(string id)
        {
            if (!_recentSet.Add(id))
            {
                return false;
            }

            _recentPrompts.Enqueue(id);
            if (_recentPrompts.Count > 100)
            {
                _recentSet.Remove(_recentPrompts.Dequeue());
            }

            return true;
        }
    }
}
