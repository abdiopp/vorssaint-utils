// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Agents;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Agents;

/// <summary>
/// Settings › AI Agents: which agents are read (with their log folders),
/// alerts, how limits are shown, the panel cards, Claude plan limits and the
/// price list.
/// </summary>
public sealed class AgentUsageSettingsPage : SettingsPage
{
    private readonly AgentUsageService? _service;
    private readonly IShellService? _shell;
    private readonly StackPanel _claudeStatus = new() { Spacing = 6 };
    private readonly TextBlock _priceDate = new() { Classes = { "caption" }, Margin = new Thickness(40, 0, 0, 0) };
    private readonly List<(AgentProvider Provider, ToggleSwitch Toggle, SettingsRow Row)> _agentRows = [];

    public AgentUsageSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _service = services.GetService<AgentUsageService>();
        _shell = services.GetService<IShellService>();
        var notifications = services.GetService<INotificationService>();

        var agents = new List<Control?>();
        foreach (var provider in AgentProviders.All)
        {
            agents.Add(AgentRow(provider));
        }

        agents.Add(Note(L.Get("win.agentUsage.copilotNote")));
        agents.Add(Note(L.Get("win.agentUsage.wslNote")));

        var finishChoices = new (double, string)[] { (0, L.Get("notchAgents.anyLength")), (30, "30 s"), (60, "1 min"), (120, "2 min"), (300, "5 min") };
        var thresholdChoices = new (double, string)[] { (50, "50%"), (75, "75%"), (80, "80%"), (90, "90%"), (95, "95%") };
        var budgetChoices = new (double, string)[] { (0, L.Get("notchAgents.off")), (5, "$5"), (10, "$10"), (25, "$25"), (50, "$50"), (100, "$100"), (250, "$250") };
        var notificationsOff = notifications is { IsEnabled: false } ? Note(L.Get("win.agentUsage.notificationsOff"), "WarningBrush") : null;

        var cards = new StackPanel { Spacing = 2, Margin = new Thickness(28, 0, 0, 0) };
        var hidden = Track(Settings.Bind(AgentUsageSettings.HiddenCards));
        foreach (var (id, key) in new[] { ("limits", "notchAgents.limitsCard"), ("spend", "notchAgents.spendCard"), ("live", "notchAgents.liveCard"), ("trend", "notchAgents.trendCard"), ("models", "notchAgents.modelsCard"), ("projects", "notchAgents.projectsCard"), ("activity", "notchAgents.activityCard") })
        {
            var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = !AgentUsageSettings.ParseHidden(hidden.Value).Contains(id) };
            toggle.IsCheckedChanged += (_, _) =>
            {
                var set = AgentUsageSettings.ParseHidden(hidden.Value).ToHashSet();
                if (toggle.IsChecked == true) set.Remove(id); else set.Add(id);
                hidden.Value = AgentUsageSettings.FormatHidden(set);
            };
            AutomationProperties.SetName(toggle, L.Get(key));
            cards.Children.Add(Row(null, L.Get(key), null, toggle));
        }

        Content = Stack(
            Header("notchAgents.title", "notchAgents.settingsDescription"),
            Card("notchAgents.agents", [.. agents]),
            Card("notchAgents.alerts",
                Toggle(AgentUsageSettings.FinishAlert, "AlertOn", "notchAgents.finishAlert"),
                Choice(AgentUsageSettings.FinishMinimum, "Timer", "notchAgents.finishAfter", null, finishChoices),
                Toggle(AgentUsageSettings.LimitAlert, "Gauge", "notchAgents.limitAlert"),
                Choice(AgentUsageSettings.LimitThreshold, "Warning", "notchAgents.limitAt", null, thresholdChoices),
                Choice(AgentUsageSettings.DailyBudget, "Money", "notchAgents.budget", null, budgetChoices),
                notificationsOff),
            Card("win.agentUsage.displayTitle",
                Choice(AgentUsageSettings.LimitDisplay, "DataBarHorizontal", "notchAgents.limitsAs", null, [("remaining", L.Get("notchAgents.remaining")), ("used", L.Get("notchAgents.used"))]),
                new TextBlock { Text = L.Get("notchAgents.cardsTitle"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(40, 6, 0, 0) },
                cards),
            Card("notchAgents.claudeLimitsTitle", _claudeStatus),
            Card("win.agentUsage.pricesTitle",
                Toggle(AgentUsageSettings.PriceUpdates, "ArrowSync", "notchAgents.priceUpdates", "notchAgents.priceUpdatesHint"),
                _priceDate),
            Note(L.Get("notchAgents.valueNote")));

        if (_service is not null)
        {
            EventHandler handler = (_, _) => Dispatcher.UIThread.Post(Refresh);
            _service.SnapshotChanged += handler;
            Track(new Releaser(() => _service.SnapshotChanged -= handler));
        }

        Track(Settings.Observe(() => Dispatcher.UIThread.Post(Refresh), AgentUsageSettings.Claude, AgentUsageSettings.Codex, AgentUsageSettings.OpenCode, AgentUsageSettings.Copilot));
        Refresh();
    }

    private Control AgentRow(AgentProvider provider)
    {
        var key = AgentUsageSettings.EnabledKey(provider);
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = Settings.Get(key) };
        toggle.IsCheckedChanged += (_, _) =>
        {
            var value = toggle.IsChecked == true;
            if (!value && AgentProviders.All.Count(p => Settings.Get(AgentUsageSettings.EnabledKey(p))) <= 1)
            {
                // The last agent cannot be switched off.
                toggle.IsChecked = true;
                return;
            }

            Settings.Set(key, value);
        };
        AutomationProperties.SetName(toggle, provider.DisplayName());

        var location = new TextBox { PlaceholderText = _service?.Paths.Roots(provider).FirstOrDefault() ?? string.Empty, Text = Settings.Get(AgentUsageSettings.RootKey(provider)), MinWidth = 260 };
        location.LostFocus += (_, _) => Settings.Set(AgentUsageSettings.RootKey(provider), location.Text ?? string.Empty);
        AutomationProperties.SetName(location, $"{provider.DisplayName()} · {L.Get("win.agentUsage.locationLabel")}");
        var choose = new Button { Content = L.Get("win.agentUsage.choose") };
        choose.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                location.Text = path;
                Settings.Set(AgentUsageSettings.RootKey(provider), path);
            }
        };

        var details = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = L.Get("win.agentUsage.locationLabel"), FontSize = 12 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { location, choose } },
                new TextBlock { Text = L.Get("win.agentUsage.locationCaption"), Classes = { "caption" } },
            },
        };
        var disclosure = new Input.Disclosure(L.Get("win.agentUsage.locationLabel"), details) { Margin = new Thickness(36, 0, 0, 0) };
        var row = Row("Bot", provider.DisplayName(), null, toggle);
        _agentRows.Add((provider, toggle, row));
        return new StackPanel { Spacing = 0, Children = { row, disclosure } };
    }

    private void Refresh()
    {
        var snapshot = _service?.Snapshot ?? AgentUsageSnapshot.Loading;
        var paths = _service?.Paths;
        foreach (var (provider, toggle, row) in _agentRows)
        {
            toggle.IsChecked = Settings.Get(AgentUsageSettings.EnabledKey(provider));
            var found = snapshot.Providers.FirstOrDefault(p => p.Provider == provider)?.Found ?? paths?.IsFound(provider) ?? false;
            row.Description = provider == AgentProvider.OpenCode && found && !SqliteNative.IsAvailable
                ? L.Get("win.agentUsage.opencodeUnavailable")
                : found ? L.Get("notchAgents.found") : L.Get("notchAgents.notFound");
        }

        _claudeStatus.Children.Clear();
        var claude = snapshot.Providers.FirstOrDefault(p => p.Provider == AgentProvider.Claude);
        switch (snapshot.ClaudeLimits)
        {
            case ClaudeLimitsStatus.Fresh:
                _claudeStatus.Children.Add(Note(L.Format("notchAgents.claudeLimitsCurrentFormat", AgentUsageCards.Ago(DateTimeOffset.UtcNow - (claude?.LimitsObservedAt ?? DateTimeOffset.UtcNow))), "MetricGreenBrush"));
                break;
            case ClaudeLimitsStatus.Stale:
                if (claude?.LimitsObservedAt is { } observed)
                {
                    _claudeStatus.Children.Add(Note(L.Format("notchAgents.claudeLimitsStaleFormat", AgentUsageCards.Ago(DateTimeOffset.UtcNow - observed)), "WarningBrush"));
                }

                _claudeStatus.Children.Add(Note(L.Get("notchAgents.claudeLimitsMenuBar")));
                break;
            default:
                _claudeStatus.Children.Add(Note(L.Get("notchAgents.claudeLimitsNoApp")));
                _claudeStatus.Children.Add(ActionButton(L.Get("notchAgents.getClaude"), () => _shell?.OpenUrl("https://claude.ai/download"), "ArrowDownload"));
                break;
        }

        _claudeStatus.Children.Add(Note(L.Get("notchAgents.claudeLimitsPrivacy")));
        var updated = snapshot.PricesUpdated ?? _service?.Prices.Current.Updated ?? PriceList.Bundled.Updated;
        _priceDate.Text = L.Format("notchAgents.pricesFromFormat", updated.ToString("D", Localizer.Current.Culture));
    }

    private sealed class Releaser(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
