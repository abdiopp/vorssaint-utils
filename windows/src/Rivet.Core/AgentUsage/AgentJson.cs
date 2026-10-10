// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rivet.Core.Agents;

/// <summary>
/// The shared number and time rules of every parser (spec 07 §3.8.5):
/// numbers count only when finite and above zero, capped at 1e12, and
/// booleans are never numbers; timestamps are ISO-8601 with Z or ±HH:MM
/// (optional fraction) or Unix time, where values above 1e11 are milliseconds.
/// </summary>
public static partial class AgentJson
{
    public const double NumberCap = 1e12;

    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    public static JsonElement? Get(this JsonElement? element, string name) =>
        element is { } e ? e.Get(name) : null;

    public static JsonElement? Path(this JsonElement element, params string[] path)
    {
        JsonElement? current = element;
        foreach (var name in path)
        {
            current = current.Get(name);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public static JsonElement? Path(this JsonElement? element, params string[] path) =>
        element is { } e ? e.Path(path) : null;

    public static string? String(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public static bool IsTrue(this JsonElement? element) => element is { ValueKind: JsonValueKind.True };

    /// <summary>A positive, finite number (capped), or null.</summary>
    public static double? Number(this JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Number } e || !e.TryGetDouble(out var value) || !double.IsFinite(value) || value <= 0)
        {
            return null;
        }

        return Math.Min(value, NumberCap);
    }

    /// <summary>A token count: a positive number, else 0.</summary>
    public static long Count(this JsonElement? element) => element.Number() is { } v ? (long)v : 0;

    /// <summary>Parses a timestamp value (string or number).</summary>
    public static DateTimeOffset? Time(this JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.String } e => ParseTime(e.GetString()),
        { ValueKind: JsonValueKind.Number } e when e.TryGetDouble(out var n) => FromUnix(n),
        _ => null,
    };

    public static DateTimeOffset? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !ZoneSuffix().IsMatch(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value.ToUniversalTime()
            : null;
    }

    /// <summary>Unix seconds, or milliseconds when above 1e11.</summary>
    public static DateTimeOffset? FromUnix(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return null;
        }

        var ms = value > 1e11 ? value : value * 1000;
        if (ms > 253402300799999)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
    }

    [GeneratedRegex(@"(Z|z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex ZoneSuffix();
}

/// <summary>What a parser extracts from one log line (spec 07 §3.8.5 "entries").</summary>
public abstract record AgentEntry
{
    /// <summary>The turn this entry belongs to when it is not the file's own (OpenCode sessions).</summary>
    public string? TurnKey { get; init; }
}

public sealed record UsageEntry(UsageRecord Record) : AgentEntry;

public sealed record LimitsEntry(ProviderLimits Limits) : AgentEntry;

public sealed record PlanEntry(AgentProvider Provider, string PlanType, DateTimeOffset ObservedAt) : AgentEntry;

/// <summary>A turn began (a prompt) at <paramref name="At"/>.</summary>
public sealed record TurnBeganEntry(DateTimeOffset At, string Project, string Model, string Session) : AgentEntry;

/// <summary>The turn is still busy (activity) at <paramref name="At"/>; <paramref name="IsReply"/> marks model output.</summary>
public sealed record TurnActiveEntry(DateTimeOffset At, bool IsReply = false) : AgentEntry;

/// <summary>A step completed without ending the turn (OpenCode).</summary>
public sealed record TurnSettledEntry(DateTimeOffset At) : AgentEntry;

/// <summary>The turn ended; <paramref name="Duration"/> is the agent's own measurement when it has one.</summary>
public sealed record TurnEndedEntry(DateTimeOffset At, bool Completed, TimeSpan? Duration) : AgentEntry;

/// <summary>Context for the current turn (model, project) changed.</summary>
public sealed record TurnContextEntry(string Model, string Project) : AgentEntry;

/// <summary>A Bash command started (id) or a tool result arrived (removes the id); null id clears the set.</summary>
public sealed record RunningCommandEntry(string? StartedId, string? FinishedId, bool Clear) : AgentEntry;

/// <summary>The source was rewritten from scratch: every turn of it ends silently.</summary>
public sealed record ResetEntry : AgentEntry;
