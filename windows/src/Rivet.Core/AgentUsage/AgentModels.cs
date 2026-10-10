// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Agents;

/// <summary>The coding agents whose local logs are read.</summary>
public enum AgentProvider
{
    Claude,
    Codex,
    OpenCode,
    Copilot,
}

public static class AgentProviders
{
    public static IReadOnlyList<AgentProvider> All { get; } = [AgentProvider.Claude, AgentProvider.Codex, AgentProvider.OpenCode, AgentProvider.Copilot];

    /// <summary>Stable id used in keys and the archive.</summary>
    public static string Id(this AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "claude",
        AgentProvider.Codex => "codex",
        AgentProvider.OpenCode => "opencode",
        _ => "copilot",
    };

    public static AgentProvider? FromId(string? id) => id switch
    {
        "claude" => AgentProvider.Claude,
        "codex" => AgentProvider.Codex,
        "opencode" => AgentProvider.OpenCode,
        "copilot" => AgentProvider.Copilot,
        _ => null,
    };

    /// <summary>Product names (not translated).</summary>
    public static string DisplayName(this AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "Claude",
        AgentProvider.Codex => "Codex",
        AgentProvider.OpenCode => "OpenCode",
        _ => "GitHub Copilot",
    };

    /// <summary>The tint of each agent (spec 07 §3.8.10), RGB 0–1.</summary>
    public static (double R, double G, double B) Tint(this AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => (0.85, 0.47, 0.34),
        AgentProvider.Codex => (0.49, 0.60, 1.0),
        AgentProvider.OpenCode => (0.06, 0.73, 0.51),
        _ => (0.30, 0.78, 0.68),
    };

    /// <summary>How long a quiet ("waiting") turn may come back (1 h Claude, 6 h others).</summary>
    public static TimeSpan ResumeWindow(this AgentProvider provider) =>
        provider == AgentProvider.Claude ? TimeSpan.FromHours(1) : TimeSpan.FromHours(6);
}

/// <summary>Token counters of one request (or an aggregate). Reasoning is informational: it is already inside output.</summary>
public readonly record struct TokenCounts(long Input, long CacheWrite, long CacheRead, long Output, long Reasoning = 0)
{
    public long Total => Input + CacheWrite + CacheRead + Output;

    /// <summary>Everything that was sent as prompt.</summary>
    public long Prompt => Input + CacheWrite + CacheRead;

    public bool IsEmpty => Total == 0 && Reasoning == 0;

    /// <summary>Per-category maximum: streamed partial duplicates of one request collapse into it.</summary>
    public TokenCounts Max(TokenCounts other) => new(
        Math.Max(Input, other.Input),
        Math.Max(CacheWrite, other.CacheWrite),
        Math.Max(CacheRead, other.CacheRead),
        Math.Max(Output, other.Output),
        Math.Max(Reasoning, other.Reasoning));

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) =>
        new(a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output, a.Reasoning + b.Reasoning);

    public static TokenCounts operator -(TokenCounts a, TokenCounts b) =>
        new(Math.Max(0, a.Input - b.Input), Math.Max(0, a.CacheWrite - b.CacheWrite), Math.Max(0, a.CacheRead - b.CacheRead),
            Math.Max(0, a.Output - b.Output), Math.Max(0, a.Reasoning - b.Reasoning));
}

/// <summary>
/// One priced unit of usage (a request, or an aggregate such as a Copilot
/// shutdown summary), keyed for deduplication. Only counters, model names,
/// times, session ids and folder names are kept, never prompts or replies.
/// </summary>
public sealed class UsageRecord
{
    public required string Key { get; init; }

    public required AgentProvider Provider { get; init; }

    public DateTimeOffset Date { get; set; }

    public string Model { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string Session { get; set; } = string.Empty;

    public long Requests { get; set; } = 1;

    public TokenCounts Tokens { get; set; }

    /// <summary>Of <see cref="TokenCounts.CacheWrite"/>, how many went to the 1-hour cache.</summary>
    public long LongCacheWrite { get; set; }

    public bool Fast { get; set; }

    /// <summary>Claude "inference_geo: us" (×1.1).</summary>
    public bool UsOnly { get; set; }

    public long WebSearches { get; set; }

    /// <summary>A summary record (Copilot): never gets the long-context premium.</summary>
    public bool IsAggregate { get; set; }

    /// <summary>The cost the agent itself recorded (OpenCode), used when the model has no list price.</summary>
    public double? ReportedCost { get; set; }

    /// <summary>API value in USD; null when the model has no known price.</summary>
    public double? Cost { get; set; }

    public double Savings { get; set; }

    /// <summary>The log files this record was read from (for resuming from the archive).</summary>
    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPriced => Cost is not null;

    /// <summary>Merges a repeated key: per-category maximum, flags by max/OR. The caller reprices.</summary>
    public void MergeFrom(UsageRecord other)
    {
        Tokens = Tokens.Max(other.Tokens);
        Requests = Math.Max(Requests, other.Requests);
        LongCacheWrite = Math.Max(LongCacheWrite, other.LongCacheWrite);
        WebSearches = Math.Max(WebSearches, other.WebSearches);
        Fast |= other.Fast;
        UsOnly |= other.UsOnly;
        IsAggregate |= other.IsAggregate;
        if (other.ReportedCost is { } reported)
        {
            ReportedCost = Math.Max(ReportedCost ?? 0, reported);
        }

        if (string.IsNullOrEmpty(Model)) Model = other.Model;
        if (string.IsNullOrEmpty(Project)) Project = other.Project;
        if (string.IsNullOrEmpty(Session)) Session = other.Session;
        if (other.Date > Date && Date == default) Date = other.Date;
        Sources.UnionWith(other.Sources);
    }

    public UsageRecord Clone()
    {
        var copy = new UsageRecord
        {
            Key = Key, Provider = Provider, Date = Date, Model = Model, Project = Project, Session = Session,
            Requests = Requests, Tokens = Tokens, LongCacheWrite = LongCacheWrite, Fast = Fast, UsOnly = UsOnly,
            WebSearches = WebSearches, IsAggregate = IsAggregate, ReportedCost = ReportedCost, Cost = Cost, Savings = Savings,
        };
        copy.Sources.UnionWith(Sources);
        return copy;
    }
}

public enum LimitWindowKind
{
    Session,
    Weekly,
    Other,
}

public enum LimitSource
{
    Log,
    ClaudeApp,
    Account,
}

/// <summary>One plan-limit window: a share used and when it renews.</summary>
public sealed record LimitWindow
{
    public required string Id { get; init; }

    public LimitWindowKind Kind { get; init; }

    public int? Minutes { get; init; }

    /// <summary>A model family the window applies to ("Opus", "Sonnet"), when scoped.</summary>
    public string? Scope { get; init; }

    public double UsedPercent { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>A window whose renewal has passed shows 0 % used and no renewal date.</summary>
    public LimitWindow Current(DateTimeOffset now) =>
        ResetsAt is { } reset && reset <= now ? this with { UsedPercent = 0, ResetsAt = null } : this;

    /// <summary>Elapsed fraction of the window ("pace" tick), only while it runs.</summary>
    public double? Pace(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset || Minutes is not { } minutes || minutes <= 0 || reset <= now)
        {
            return null;
        }

        var length = TimeSpan.FromMinutes(minutes);
        var start = reset - length;
        return Math.Clamp((now - start) / length, 0, 1);
    }
}

/// <summary>Limits of one provider as last observed.</summary>
public sealed record ProviderLimits(AgentProvider Provider, IReadOnlyList<LimitWindow> Windows, DateTimeOffset ObservedAt, LimitSource Source)
{
    /// <summary>The most used window; on a tie the later renewal.</summary>
    public LimitWindow? Binding(DateTimeOffset now) => Windows
        .Select(w => w.Current(now))
        .OrderByDescending(w => w.UsedPercent)
        .ThenByDescending(w => w.ResetsAt ?? DateTimeOffset.MinValue)
        .FirstOrDefault();
}

/// <summary>A recognized subscription plan.</summary>
public sealed record AgentPlan(string Name, double? Monthly);

/// <summary>A unit of work in progress (a "turn" from prompt to final reply).</summary>
public sealed class AgentTurn
{
    public required string Key { get; init; }

    public required AgentProvider Provider { get; init; }

    public DateTimeOffset Started { get; set; }

    public DateTimeOffset LastActivity { get; set; }

    public string Model { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string Session { get; set; } = string.Empty;

    /// <summary>Output tokens written so far in this turn.</summary>
    public long OutputTokens { get; set; }

    public double Cost { get; set; }

    /// <summary>OpenCode: the step completed without ending the turn (ends silently after 30 s).</summary>
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>Claude: Bash commands still running (memory only, never archived).</summary>
    public HashSet<string> RunningCommands { get; } = new(StringComparer.Ordinal);

    /// <summary>Claude: the model replied after the network dropped (a local model).</summary>
    public DateTimeOffset? LastReply { get; set; }

    public AgentTurn Clone()
    {
        var copy = new AgentTurn
        {
            Key = Key, Provider = Provider, Started = Started, LastActivity = LastActivity, Model = Model, Project = Project,
            Session = Session, OutputTokens = OutputTokens, Cost = Cost, SettledAt = SettledAt, LastReply = LastReply,
        };
        copy.RunningCommands.UnionWith(RunningCommands);
        return copy;
    }
}

/// <summary>A long task finished (for the notification).</summary>
public sealed record AgentFinished(AgentProvider Provider, TimeSpan Duration, double Cost, long OutputTokens, string Project);

/// <summary>Plan-limit and budget events raised after the initial read.</summary>
public abstract record AgentAlert;

public sealed record LimitWarningAlert(AgentProvider Provider, LimitWindow Window) : AgentAlert;

public sealed record LimitRenewedAlert(AgentProvider Provider, LimitWindow Window) : AgentAlert;

public sealed record BudgetAlert(double Spent) : AgentAlert;
