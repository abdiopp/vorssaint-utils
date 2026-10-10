// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Agents;

/// <summary>What the archive keeps so the next start reads only what was appended (spec 07 §3.8.11).</summary>
public sealed class AgentArchiveData
{
    public string Build { get; set; } = string.Empty;

    public List<string> Providers { get; set; } = [];

    public List<ArchivedRecord> Records { get; set; } = [];

    public List<ArchivedLimits> Limits { get; set; } = [];

    public AgentPlan? CodexPlan { get; set; }

    public DateTimeOffset CodexPlanObserved { get; set; }

    public List<ArchivedTurn> Turns { get; set; } = [];

    public List<ArchivedTurn> Waiting { get; set; } = [];

    public List<LogCursor> Cursors { get; set; } = [];
}

public sealed record ArchivedRecord(
    string Key, string Provider, DateTimeOffset Date, string Model, string Project, string Session, long Requests,
    long Input, long CacheWrite, long CacheRead, long Output, long Reasoning,
    long LongCacheWrite, bool Fast, bool UsOnly, long WebSearches, bool IsAggregate, List<string> Sources)
{
    public static ArchivedRecord From(UsageRecord r) => new(
        r.Key, r.Provider.Id(), r.Date, r.Model, r.Project, r.Session, r.Requests,
        r.Tokens.Input, r.Tokens.CacheWrite, r.Tokens.CacheRead, r.Tokens.Output, r.Tokens.Reasoning,
        r.LongCacheWrite, r.Fast, r.UsOnly, r.WebSearches, r.IsAggregate, [.. r.Sources.Order(StringComparer.OrdinalIgnoreCase)]);

    public UsageRecord? ToRecord()
    {
        if (AgentProviders.FromId(Provider) is not { } provider)
        {
            return null;
        }

        var record = new UsageRecord
        {
            Key = Key, Provider = provider, Date = Date, Model = Model, Project = Project, Session = Session, Requests = Requests,
            Tokens = new TokenCounts(Input, CacheWrite, CacheRead, Output, Reasoning),
            LongCacheWrite = LongCacheWrite, Fast = Fast, UsOnly = UsOnly, WebSearches = WebSearches, IsAggregate = IsAggregate,
        };
        record.Sources.UnionWith(Sources);
        return record;
    }
}

public sealed record ArchivedLimits(string Provider, List<LimitWindow> Windows, DateTimeOffset ObservedAt, LimitSource Source);

public sealed record ArchivedTurn(string Key, string Provider, DateTimeOffset Started, DateTimeOffset LastActivity, string Model, string Project, string Session, long OutputTokens, double Cost)
{
    public static ArchivedTurn From(AgentTurn t) => new(t.Key, t.Provider.Id(), t.Started, t.LastActivity, t.Model, t.Project, t.Session, t.OutputTokens, t.Cost);

    public AgentTurn? ToTurn() => AgentProviders.FromId(Provider) is { } provider
        ? new AgentTurn { Key = Key, Provider = provider, Started = Started, LastActivity = LastActivity, Model = Model, Project = Project, Session = Session, OutputTokens = OutputTokens, Cost = Cost }
        : null;
}

/// <summary>
/// The archive file: magic "RAUA", a format byte, the payload length, a UTF-8
/// JSON payload, and an FNV-1a-64 checksum of everything before it. Any
/// damage (a flipped bit, a short or long file, a duplicate record key) reads
/// as "no archive", which only costs a full reread. Never stored: anything
/// from OpenCode, reported costs, running commands, the process registry.
/// </summary>
public static class AgentUsageArchive
{
    public const byte Format = 1;
    private static readonly byte[] Magic = "RAUA"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(AgentArchiveData data)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        var buffer = new byte[Magic.Length + 1 + 4 + payload.Length + 8];
        Magic.CopyTo(buffer, 0);
        buffer[4] = Format;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(5), payload.Length);
        payload.CopyTo(buffer, 9);
        var checksum = LogCursorReader.Fnv(0xCBF29CE484222325, buffer.AsSpan(0, 9 + payload.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(9 + payload.Length), checksum);
        return buffer;
    }

    public static AgentArchiveData? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 17 || !bytes[..4].SequenceEqual(Magic) || bytes[4] != Format)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]);
        if (length < 0 || length != bytes.Length - 17)
        {
            return null;
        }

        var expected = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(9 + length)..]);
        if (LogCursorReader.Fnv(0xCBF29CE484222325, bytes[..(9 + length)]) != expected)
        {
            return null;
        }

        try
        {
            var data = JsonSerializer.Deserialize<AgentArchiveData>(bytes.Slice(9, length), Json);
            if (data is null || data.Records.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != data.Records.Count
                || data.Cursors.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Cursors.Count)
            {
                return null;
            }

            return data;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static AgentArchiveData? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("agents", "Could not read the usage archive.", ex);
            return null;
        }
    }

    public static void Save(string path, AgentArchiveData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, Encode(data));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("agents", "Could not save the usage archive.", ex);
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string ProvidersKey(IEnumerable<AgentProvider> providers) =>
        string.Join(',', providers.Select(p => p.Id()).Order(StringComparer.Ordinal));
}
