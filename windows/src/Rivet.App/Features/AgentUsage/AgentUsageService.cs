// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Agents;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.App.Features.Agents;

/// <summary>
/// The AI agents feature controller (spec 07 §3.8.2, decoupled from the
/// Dynamic Island): while the feature is installed it runs the usage engine,
/// keeps the latest snapshot for the panel section and Settings, and turns
/// finished tasks, near-limit warnings, renewals and the daily budget into
/// Windows notifications. Uninstalling stops it and deletes the archive; a
/// change of the agent set restarts it with a fresh read.
/// </summary>
public sealed class AgentUsageService : IFeatureController, IDisposable
{
    private readonly object _gate = new();
    private readonly ISettingsStore _settings;
    private readonly IAgentUsagePlatform _platform;
    private readonly INotificationService? _notifications;
    private readonly string _archivePath;
    private readonly IDisposable _providerObserver;
    private readonly IDisposable _rootObserver;
    private AgentUsageEngine? _engine;
    private string _providerKey = string.Empty;
    private bool _available;
    private volatile AgentUsageSnapshot _snapshot = AgentUsageSnapshot.Loading;

    public AgentUsageService(ISettingsStore settings, IAgentUsagePlatform platform, AppPaths paths, INotificationService? notifications = null)
    {
        _settings = settings;
        _platform = platform;
        _notifications = notifications;
        _archivePath = Path.Combine(paths.Cache, "AgentUsage", "agent-usage.bin");
        Prices = new AgentPriceManager(Path.Combine(paths.LocalRoot, "agent-prices.json"), settings);
        Paths = new AgentUsagePaths(platform, provider => settings.Get(AgentUsageSettings.RootKey(provider)));
        _providerObserver = settings.Observe(() => Restart(freshRead: true),
            AgentUsageSettings.Claude, AgentUsageSettings.Codex, AgentUsageSettings.OpenCode, AgentUsageSettings.Copilot);
        _rootObserver = settings.Observe(() => Restart(freshRead: true),
            AgentUsageSettings.ClaudeRoot, AgentUsageSettings.CodexRoot, AgentUsageSettings.OpenCodeRoot, AgentUsageSettings.CopilotRoot);
    }

    /// <summary>Raised on the UI thread after a new snapshot.</summary>
    public event EventHandler? SnapshotChanged;

    public AgentUsageSnapshot Snapshot => _snapshot;

    public AgentUsagePaths Paths { get; }

    public AgentPriceManager Prices { get; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _engine is not null;
            }
        }
    }

    /// <summary>The agents switched on (the last one cannot be switched off in Settings).</summary>
    public IReadOnlySet<AgentProvider> EnabledProviders =>
        AgentProviders.All.Where(p => _settings.Get(AgentUsageSettings.EnabledKey(p))).ToHashSet();

    public void Sync(bool available)
    {
        lock (_gate)
        {
            _available = available;
            if (available && _engine is null)
            {
                StartLocked();
            }
            else if (!available && _engine is not null)
            {
                StopLocked(keepArchive: false);
                _snapshot = AgentUsageSnapshot.Loading;
            }
        }
    }

    public void Dispose()
    {
        _providerObserver.Dispose();
        _rootObserver.Dispose();
        lock (_gate)
        {
            StopLocked(keepArchive: true);
        }
    }

    /// <summary>The label of a limit window: "Session", "Week", "Week · Opus", or a duration.</summary>
    public static string WindowLabel(LimitWindow window)
    {
        var label = window.Kind switch
        {
            LimitWindowKind.Session => L.Get("notchAgents.session"),
            LimitWindowKind.Weekly => L.Get("notchAgents.weekly"),
            _ => window.Minutes is { } minutes ? AgentFormat.Duration(TimeSpan.FromMinutes(minutes)) : L.Get("notchAgents.limitsCard"),
        };
        return window.Scope is { Length: > 0 } scope ? $"{label} · {scope}" : label;
    }

    /// <summary>"4m 12s · $1.23" (the cost is left out when zero).</summary>
    public static string FinishedDetail(AgentFinished finished) =>
        finished.Cost > 0 ? $"{AgentFormat.Duration(finished.Duration)} · {AgentFormat.Cost(finished.Cost)}" : AgentFormat.Duration(finished.Duration);

    private AgentUsageOptions Options() => new()
    {
        Providers = EnabledProviders,
        LimitThreshold = _settings.Get(AgentUsageSettings.LimitThreshold),
        DailyBudget = _settings.Get(AgentUsageSettings.DailyBudget),
        Build = AppIdentity.VersionString,
    };

    private void Restart(bool freshRead)
    {
        lock (_gate)
        {
            if (!_available || _engine is null)
            {
                return;
            }

            var key = string.Join(',', EnabledProviders.OrderBy(p => p)) + "|" + string.Join('|', AgentProviders.All.Select(p => _settings.Get(AgentUsageSettings.RootKey(p))));
            if (key == _providerKey)
            {
                return;
            }

            // A different agent set or location: stop without keeping progress, then read everything again.
            StopLocked(keepArchive: !freshRead);
            _snapshot = AgentUsageSnapshot.Loading;
            StartLocked();
        }

        UiThread.Post(() => SnapshotChanged?.Invoke(this, EventArgs.Empty));
    }

    private void StartLocked()
    {
        _providerKey = string.Join(',', EnabledProviders.OrderBy(p => p)) + "|" + string.Join('|', AgentProviders.All.Select(p => _settings.Get(AgentUsageSettings.RootKey(p))));
        var engine = new AgentUsageEngine(_platform, Paths, Options, _archivePath, Prices);
        engine.Published += OnPublished;
        engine.TaskFinished += OnTaskFinished;
        engine.Alerted += OnAlerted;
        _engine = engine;
        engine.Start();
    }

    private void StopLocked(bool keepArchive)
    {
        if (_engine is not { } engine)
        {
            return;
        }

        _engine = null;
        engine.Published -= OnPublished;
        engine.TaskFinished -= OnTaskFinished;
        engine.Alerted -= OnAlerted;
        try
        {
            engine.Stop(keepArchive);
        }
        catch (Exception ex)
        {
            Log.Warn("agents", "Stopping the usage reader failed.", ex);
        }
    }

    private void OnPublished(object? sender, AgentUsageSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _engine))
            {
                return;
            }
        }

        _snapshot = snapshot;
        UiThread.Post(() => SnapshotChanged?.Invoke(this, EventArgs.Empty));
    }

    private void OnTaskFinished(object? sender, AgentFinished finished)
    {
        if (!_settings.Get(AgentUsageSettings.FinishAlert)
            || !_settings.Get(AgentUsageSettings.EnabledKey(finished.Provider))
            || finished.Duration.TotalSeconds < _settings.Get(AgentUsageSettings.FinishMinimum))
        {
            return;
        }

        Notify(L.Format("notchAgents.finishedFormat", finished.Provider.DisplayName()),
            string.IsNullOrEmpty(finished.Project) ? FinishedDetail(finished) : $"{finished.Project} · {FinishedDetail(finished)}",
            $"agents-finished-{finished.Provider.Id()}");
    }

    private void OnAlerted(object? sender, AgentAlert alert)
    {
        switch (alert)
        {
            case LimitWarningAlert warning when _settings.Get(AgentUsageSettings.LimitAlert):
            {
                var used = warning.Window.UsedPercent;
                var detail = _settings.Get(AgentUsageSettings.LimitDisplay) == "used"
                    ? L.Format("notchAgents.usedFormat", AgentFormat.Percent(used))
                    : L.Format("notchAgents.leftFormat", AgentFormat.Percent(100 - used));
                Notify($"{warning.Provider.DisplayName()} · {WindowLabel(warning.Window)}", detail, $"agents-limit-{warning.Window.Id}");
                break;
            }

            case LimitRenewedAlert renewed when _settings.Get(AgentUsageSettings.LimitAlert):
                Notify($"{renewed.Provider.DisplayName()} · {WindowLabel(renewed.Window)}", L.Get("notchAgents.limitRenewed"), $"agents-limit-{renewed.Window.Id}");
                break;
            case BudgetAlert budget:
                Notify(L.Get("notchAgents.budgetTitle"), AgentFormat.Cost(budget.Spent), "agents-budget");
                break;
        }
    }

    private void Notify(string title, string body, string tag)
    {
        try
        {
            _notifications?.Show(new NotificationRequest { Title = title, Body = body, Tag = tag, ClickActionId = AgentUsageModule.ShowActionId });
        }
        catch (Exception ex)
        {
            Log.Warn("agents", "Could not show an agent notification.", ex);
        }
    }
}
