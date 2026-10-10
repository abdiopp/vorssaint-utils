// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Agents;

/// <summary>
/// Everything read from the logs: deduplicated records, limits, plans and
/// turns (spec 07 §3.8.5 "Merging", §3.8.6). Not thread-safe; the engine
/// serializes access.
/// </summary>
public sealed class AgentUsageStore
{
    private readonly List<AgentFinished> _finished = [];

    public PriceList? Prices { get; set; } = PriceList.Bundled;

    public Dictionary<string, UsageRecord> Records { get; } = new(StringComparer.Ordinal);

    public Dictionary<AgentProvider, ProviderLimits> Limits { get; } = [];

    public AgentPlan? CodexPlan { get; set; }

    public DateTimeOffset CodexPlanObserved { get; set; }

    public AgentPlan? ClaudePlan { get; set; }

    /// <summary>Turns shown as working.</summary>
    public Dictionary<string, AgentTurn> Turns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quiet turns (no activity for 10 min) that may still come back or end.</summary>
    public Dictionary<string, AgentTurn> Waiting { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Off during the initial history read: history never notifies.</summary>
    public bool TransitionsEnabled { get; set; }

    /// <summary>Takes the finished-task events collected since the last call.</summary>
    public List<AgentFinished> DrainFinished()
    {
        var copy = _finished.ToList();
        _finished.Clear();
        return copy;
    }

    /// <summary>Applies one file's entries in order.</summary>
    public void Apply(AgentProvider provider, string source, string turnKey, IEnumerable<AgentEntry> entries, DateTimeOffset now)
    {
        foreach (var entry in entries)
        {
            var key = entry.TurnKey ?? turnKey;
            switch (entry)
            {
                case UsageEntry usage:
                    AddUsage(usage.Record, source, key);
                    break;
                case LimitsEntry limits:
                    SetLimits(limits.Limits);
                    break;
                case PlanEntry plan when plan.Provider == AgentProvider.Codex:
                    if (plan.ObservedAt >= CodexPlanObserved)
                    {
                        CodexPlan = AgentPricing.CodexPlan(Prices, plan.PlanType);
                        CodexPlanObserved = plan.ObservedAt;
                    }

                    break;
                case TurnBeganEntry began:
                    Begin(provider, key, began);
                    break;
                case TurnActiveEntry active:
                    Activity(key, active.At, active.IsReply);
                    break;
                case TurnSettledEntry settled:
                    if (Find(key) is { } settling)
                    {
                        settling.SettledAt = settled.At;
                        Touch(settling, settled.At);
                    }

                    break;
                case TurnContextEntry context:
                    if (Find(key) is { } turn)
                    {
                        if (context.Model.Length > 0) turn.Model = context.Model;
                        if (context.Project.Length > 0) turn.Project = context.Project;
                    }

                    break;
                case RunningCommandEntry command:
                    if (Find(key) is { } running)
                    {
                        if (command.Clear) running.RunningCommands.Clear();
                        if (command.StartedId is { } started) running.RunningCommands.Add(started);
                        if (command.FinishedId is { } finished) running.RunningCommands.Remove(finished);
                    }

                    break;
                case TurnEndedEntry ended:
                    End(key, ended.At, ended.Completed, ended.Duration, now);
                    break;
                case ResetEntry:
                    EndSilently(t => t.Key.StartsWith(source, StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }
    }

    /// <summary>Adds a record (merging a repeated key by per-category maximum) and returns the output and cost it added.</summary>
    public (long Output, double Cost) AddRecord(UsageRecord record, string source)
    {
        record.Sources.Add(source);
        if (Records.TryGetValue(record.Key, out var existing))
        {
            var beforeOutput = existing.Tokens.Output;
            var beforeCost = existing.Cost ?? 0;
            existing.MergeFrom(record);
            if (existing.Date == default)
            {
                existing.Date = record.Date;
            }

            AgentPricing.Reprice(Prices, existing);
            return (existing.Tokens.Output - beforeOutput, (existing.Cost ?? 0) - beforeCost);
        }

        AgentPricing.Reprice(Prices, record);
        Records[record.Key] = record;
        return (record.Tokens.Output, record.Cost ?? 0);
    }

    public void SetLimits(ProviderLimits limits)
    {
        // Newest wins by observed time, whatever the source.
        if (!Limits.TryGetValue(limits.Provider, out var existing) || limits.ObservedAt >= existing.ObservedAt)
        {
            Limits[limits.Provider] = limits;
        }
    }

    /// <summary>The 30 s housekeeping: idle turns wait, waiting turns expire, settled OpenCode turns end, old records go.</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var turn in Turns.Values.ToList())
        {
            if (turn.SettledAt is { } settled && now - settled >= AgentUsageConstants.OpenCodeSettle)
            {
                Turns.Remove(turn.Key);
                continue;
            }

            if (now - turn.LastActivity >= AgentUsageConstants.IdleTurn)
            {
                Turns.Remove(turn.Key);
                Waiting[turn.Key] = turn;
            }
        }

        foreach (var turn in Waiting.Values.ToList())
        {
            if (now - turn.LastActivity >= turn.Provider.ResumeWindow())
            {
                Waiting.Remove(turn.Key);
            }
        }

        var cutoff = now - AgentUsageConstants.Horizon;
        foreach (var record in Records.Values.Where(r => r.Date < cutoff).ToList())
        {
            Records.Remove(record.Key);
        }
    }

    /// <summary>Ends turns silently (dead process, offline, deleted log, reset).</summary>
    public int EndSilently(Func<AgentTurn, bool> predicate)
    {
        var count = 0;
        foreach (var turn in Turns.Values.Where(predicate).ToList())
        {
            Turns.Remove(turn.Key);
            count++;
        }

        foreach (var turn in Waiting.Values.Where(predicate).ToList())
        {
            Waiting.Remove(turn.Key);
            count++;
        }

        return count;
    }

    public void RepriceAll()
    {
        foreach (var record in Records.Values)
        {
            AgentPricing.Reprice(Prices, record);
        }
    }

    /// <summary>Providers with records, limits or a live turn.</summary>
    public bool IsSeen(AgentProvider provider) =>
        Limits.ContainsKey(provider) || Turns.Values.Any(t => t.Provider == provider) || Records.Values.Any(r => r.Provider == provider);

    public void Clear()
    {
        Records.Clear();
        Limits.Clear();
        Turns.Clear();
        Waiting.Clear();
        CodexPlan = null;
        CodexPlanObserved = default;
        ClaudePlan = null;
        _finished.Clear();
        TransitionsEnabled = false;
    }

    private void AddUsage(UsageRecord record, string source, string turnKey)
    {
        var date = record.Date;
        var (output, cost) = AddRecord(record, source);
        if (record.Provider == AgentProvider.Copilot)
        {
            // Copilot never adds tokens or cost to the live turn.
            return;
        }

        var turn = Find(turnKey);
        if (turn is null || date < turn.Started - AgentUsageConstants.ReplayGuard)
        {
            return;
        }

        turn.OutputTokens += output;
        turn.Cost += cost;
        if (record.Model.Length > 0)
        {
            turn.Model = record.Model;
        }

        Touch(turn, date);
    }

    private void Begin(AgentProvider provider, string key, TurnBeganEntry began)
    {
        if (Find(key) is { } existing && Math.Abs((existing.Started - began.At).TotalSeconds) < AgentUsageConstants.ReplayGuard.TotalSeconds)
        {
            // A reread log: this turn is already known.
            return;
        }

        Waiting.Remove(key);
        Turns[key] = new AgentTurn
        {
            Key = key,
            Provider = provider,
            Started = began.At,
            LastActivity = began.At,
            Project = began.Project,
            Model = began.Model,
            Session = began.Session,
        };
    }

    private void Activity(string key, DateTimeOffset at, bool reply)
    {
        if (Find(key) is not { } turn)
        {
            return;
        }

        if (reply)
        {
            turn.LastReply = turn.LastReply is { } last && last > at ? last : at;
        }

        turn.SettledAt = null;
        Touch(turn, at);
    }

    /// <summary>Activity brings a waiting turn back.</summary>
    private void Touch(AgentTurn turn, DateTimeOffset at)
    {
        if (at > turn.LastActivity)
        {
            turn.LastActivity = at;
        }

        if (Waiting.Remove(turn.Key))
        {
            Turns[turn.Key] = turn;
        }
    }

    private void End(string key, DateTimeOffset at, bool completed, TimeSpan? duration, DateTimeOffset now)
    {
        var turn = Find(key);
        if (turn is null)
        {
            return;
        }

        Turns.Remove(key);
        Waiting.Remove(key);
        if (!completed || !TransitionsEnabled || now - at > AgentUsageConstants.LateEnd)
        {
            return;
        }

        var length = duration ?? (at - turn.Started);
        _finished.Add(new AgentFinished(turn.Provider, length < TimeSpan.Zero ? TimeSpan.Zero : length, turn.Cost, turn.OutputTokens, turn.Project));
    }

    private AgentTurn? Find(string key) =>
        Turns.TryGetValue(key, out var turn) ? turn : Waiting.TryGetValue(key, out var waiting) ? waiting : null;
}

/// <summary>
/// Near-limit warnings and renewal notices (spec 07 §3.8.7). The first
/// reading of a window never warns; a window warns when it crosses the
/// threshold or renews above it; a warned window reports "Limit renewed" when
/// its old renewal passes or it renews below the threshold.
/// </summary>
public sealed class LimitAlerts
{
    private readonly Dictionary<string, Reading> _readings = new(StringComparer.Ordinal);

    public List<AgentAlert> Check(ProviderLimits limits, double threshold, DateTimeOffset now)
    {
        var alerts = new List<AgentAlert>();
        foreach (var window in limits.Windows)
        {
            var id = $"{limits.Provider.Id()}:{window.Id}";
            var current = window.Current(now);
            if (!_readings.TryGetValue(id, out var previous))
            {
                _readings[id] = new Reading(current.UsedPercent, window.ResetsAt, false);
                continue;
            }

            var renewed = window.ResetsAt is { } reset && previous.ResetsAt is { } before
                          && reset - before > AgentUsageConstants.LimitCrossingTolerance;
            var warned = previous.Warned;
            if (warned && renewed && current.UsedPercent < threshold)
            {
                alerts.Add(new LimitRenewedAlert(limits.Provider, current));
                warned = false;
            }

            if (current.UsedPercent >= threshold && (previous.UsedPercent < threshold || renewed))
            {
                alerts.Add(new LimitWarningAlert(limits.Provider, current));
                warned = true;
            }

            _readings[id] = new Reading(current.UsedPercent, window.ResetsAt, warned);
        }

        return alerts;
    }

    /// <summary>Time passed: warned windows whose renewal time went by report the renewal.</summary>
    public List<AgentAlert> Tick(IEnumerable<ProviderLimits> all, DateTimeOffset now)
    {
        var alerts = new List<AgentAlert>();
        foreach (var limits in all)
        {
            foreach (var window in limits.Windows)
            {
                var id = $"{limits.Provider.Id()}:{window.Id}";
                if (_readings.TryGetValue(id, out var reading) && reading.Warned && reading.ResetsAt is { } reset && reset <= now)
                {
                    alerts.Add(new LimitRenewedAlert(limits.Provider, window.Current(now)));
                    _readings[id] = reading with { Warned = false, UsedPercent = 0 };
                }
            }
        }

        return alerts;
    }

    public void Clear() => _readings.Clear();

    private sealed record Reading(double UsedPercent, DateTimeOffset? ResetsAt, bool Warned);
}
