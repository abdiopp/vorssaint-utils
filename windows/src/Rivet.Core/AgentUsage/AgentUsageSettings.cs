// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Agents;

/// <summary>Timing constants of the reading pipeline (spec 07 §6.8).</summary>
public static class AgentUsageConstants
{
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(91);
    public static readonly TimeSpan MainTick = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PollWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PublishDebounce = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RootRecheck = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LateEnd = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan IdleTurn = TimeSpan.FromSeconds(600);
    public static readonly TimeSpan OpenCodeSettle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RunningWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ReplayGuard = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LimitCrossingTolerance = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Settings of the AI agents feature. Keys shared with the macOS app keep
/// their "notchAgents" names (the data was shown in the Dynamic Island there);
/// the Windows-only log locations are new keys.
/// </summary>
public static class AgentUsageSettings
{
    public static readonly Setting<bool> Claude = new("notchAgentsClaude", true);
    public static readonly Setting<bool> Codex = new("notchAgentsCodex", true);
    public static readonly Setting<bool> OpenCode = new("notchAgentsOpenCode", true);
    public static readonly Setting<bool> Copilot = new("notchAgentsCopilot", true);

    /// <summary>Card order (comma list of limits, spend, live, trend, models, projects, activity); unknown and duplicate ids ignored.</summary>
    public static readonly Setting<string> CardOrder = new("notchAgentsCardOrder", string.Empty);

    /// <summary>Hidden cards, written sorted.</summary>
    public static readonly Setting<string> HiddenCards = new("notchAgentsHiddenCards", string.Empty);

    /// <summary>Period for Spending, Trend, Models and Projects: today, week (7 days) or month (30 days).</summary>
    public static readonly Setting<string> Period = new("notchAgentsPeriod", "today", Sanitize.OneOfStrings("today", "today", "week", "month"));

    /// <summary>Show limits as "remaining" (left) or "used".</summary>
    public static readonly Setting<string> LimitDisplay = new("notchAgentsLimitDisplay", "remaining", Sanitize.OneOfStrings("remaining", "remaining", "used"));

    public static readonly Setting<bool> FinishAlert = new("notchAgentsFinishAlert", true);

    /// <summary>Minimum task length (s) for the "finished" notice; menu 0/30/60/120/300.</summary>
    public static readonly Setting<double> FinishMinimum = new("notchAgentsFinishMinimum", 60, v => double.IsFinite(v) ? Math.Clamp(v, 0, 3600) : 60);

    /// <summary>"Near a plan limit" (also gates the renewal notices).</summary>
    public static readonly Setting<bool> LimitAlert = new("notchAgentsLimitAlert", true);

    /// <summary>Warn at this share used (%); menu 50/75/80/90/95.</summary>
    public static readonly Setting<double> LimitThreshold = new("notchAgentsLimitThreshold", 80, v => double.IsFinite(v) ? Math.Clamp(v, 1, 100) : 80);

    /// <summary>Daily API value budget in USD; 0 or less is off.</summary>
    public static readonly Setting<double> DailyBudget = new("notchAgentsDailyBudget", 0, v => double.IsFinite(v) && v > 0 ? Math.Min(v, 1_000_000) : 0);

    /// <summary>"Keep prices up to date": the public price list is downloaded once a day.</summary>
    public static readonly Setting<bool> PriceUpdates = new("notchAgentsPriceUpdates", true);

    // ── Windows: where the logs live (empty = automatic, see AgentUsagePaths) ──
    public static readonly Setting<string> ClaudeRoot = new("agentUsageClaudeRoot", string.Empty, Trim);
    public static readonly Setting<string> CodexRoot = new("agentUsageCodexRoot", string.Empty, Trim);
    public static readonly Setting<string> OpenCodeRoot = new("agentUsageOpenCodeRoot", string.Empty, Trim);
    public static readonly Setting<string> CopilotRoot = new("agentUsageCopilotRoot", string.Empty, Trim);

    /// <summary>Last price-list download attempt (Unix ms) and whether it failed (machine state, never backed up).</summary>
    public static readonly Setting<long> PriceLastAttempt = new("agentUsagePriceLastAttempt", 0L, machineState: true);
    public static readonly Setting<bool> PriceLastFailed = new("agentUsagePriceLastFailed", false, machineState: true);

    public static readonly IReadOnlyList<string> CardIds = ["limits", "spend", "live", "trend", "models", "projects", "activity"];

    public static Setting<bool> EnabledKey(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => Claude,
        AgentProvider.Codex => Codex,
        AgentProvider.OpenCode => OpenCode,
        _ => Copilot,
    };

    public static Setting<string> RootKey(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ClaudeRoot,
        AgentProvider.Codex => CodexRoot,
        AgentProvider.OpenCode => OpenCodeRoot,
        _ => CopilotRoot,
    };

    /// <summary>The card order: known ids from the setting first, missing ones appended.</summary>
    public static IReadOnlyList<string> OrderedCards(string stored)
    {
        var order = new List<string>();
        foreach (var id in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (CardIds.Contains(id) && !order.Contains(id))
            {
                order.Add(id);
            }
        }

        order.AddRange(CardIds.Where(id => !order.Contains(id)));
        return order;
    }

    public static IReadOnlySet<string> ParseHidden(string stored) =>
        stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(CardIds.Contains).ToHashSet(StringComparer.Ordinal);

    public static string FormatHidden(IEnumerable<string> hidden) =>
        string.Join(',', hidden.Where(CardIds.Contains).Distinct().OrderBy(id => id, StringComparer.Ordinal));

    private static string Trim(string value) => value.Trim();
}
