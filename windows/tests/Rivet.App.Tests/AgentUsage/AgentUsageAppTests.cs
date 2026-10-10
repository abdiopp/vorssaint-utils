// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Agents;
using Rivet.App.Modules;
using Rivet.App.Tests.Input;
using Rivet.Core.Agents;
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.Agents;
using Rivet.Platform.Fake.Shell;
using Xunit;

namespace Rivet.App.Tests.Agents;

public class AgentUsageAppTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 15, 30, 0, TimeSpan.Zero);

    private static ThemeVariant Theme(string theme) => theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>Runs the real engine over synthetic sample logs (never the user's own).</summary>
    private static (AgentUsageSnapshot Snapshot, string Root) SampleSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rivet-agent-app-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.ForTemporaryDirectory(root);
        var platform = new FakeAgentUsagePlatform(paths, createSamples: false);
        AgentSampleLogs.Write(platform.HomeDirectory, Now);
        var engine = new AgentUsageEngine(platform, new AgentUsagePaths(platform), () => new AgentUsageOptions { Providers = AgentProviders.All.ToHashSet(), Build = "test" }, null, clock: () => Now, zone: TimeZoneInfo.Utc);
        engine.Initialize();
        return (engine.BuildSnapshot(), root);
    }

    private static Control Panel(Control content)
    {
        var surface = new Border { Padding = new Thickness(12), Child = content, Width = 340 };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        return surface;
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_panel_tab_renders_the_cards(string theme)
    {
        _ = TestApp.Host;
        var (snapshot, _) = SampleSnapshot();
        Assert.True(snapshot.Loaded);
        Assert.True(snapshot.AnySeen);
        Assert.Single(snapshot.Live);
        var codex = snapshot.Providers.Single(p => p.Provider == AgentProvider.Codex);
        Assert.Equal(2, codex.Windows.Count);
        Assert.Equal("Plus", codex.Plan?.Name);

        var settings = SettingsStore.InMemory();
        TestApp.Snapshot(Panel(AgentUsageCards.Build(snapshot, settings, Now)), $"agents-panel-{theme}", 364, theme: Theme(theme));
        settings.Set(AgentUsageSettings.Period, "month");
        settings.Set(AgentUsageSettings.LimitDisplay, "used");
        settings.Set(AgentUsageSettings.HiddenCards, "limits,live");
        TestApp.Snapshot(Panel(AgentUsageCards.Build(snapshot, settings, Now)), $"agents-panel-month-{theme}", 364, theme: Theme(theme));
    }

    [AvaloniaFact]
    public void Loading_and_empty_states_render()
    {
        _ = TestApp.Host;
        var settings = SettingsStore.InMemory();
        var empty = new AgentUsageSnapshot { Loaded = true, Providers = [new AgentProviderStatus { Provider = AgentProvider.Claude, Enabled = true }] };
        var stack = new StackPanel { Spacing = 12, Children = { AgentUsageCards.Build(AgentUsageSnapshot.Loading, settings, Now), AgentUsageCards.Build(empty, settings, Now) } };
        TestApp.Snapshot(Panel(stack), "agents-panel-empty", 364);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_settings_page_renders(string theme)
    {
        _ = TestApp.Host;
        var root = Path.Combine(Path.GetTempPath(), "rivet-agent-page-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.ForTemporaryDirectory(root);
        var platform = new FakeAgentUsagePlatform(paths, createSamples: false);
        AgentSampleLogs.Write(platform.HomeDirectory, Now);
        var settings = SettingsStore.InMemory();
        settings.Set(AgentUsageSettings.DailyBudget, 25);
        var service = new AgentUsageService(settings, platform, paths, new FakeNotifications());
        var services = new OverlayServices(TestApp.Host.Services).With<ISettingsStore>(settings).With(service);
        var page = new Border { Padding = new Thickness(28, 22, 32, 32), Child = new AgentUsageSettingsPage(services) };
        TestApp.Snapshot(page, $"agents-settings-{theme}", 900, 1900, theme: Theme(theme));
        service.Dispose();
    }

    [AvaloniaFact]
    public void The_section_page_and_icons_are_registered()
    {
        var host = TestApp.Host;
        var section = host.Services.GetRequiredService<PanelRegistry>().Sections.Single(s => s.Id == AgentUsageModule.SectionId);
        Assert.Equal(100, section.Order);
        Assert.Equal([FeatureIds.AgentUsage], section.FeatureIds);
        var page = host.Services.GetRequiredService<SettingsPageRegistry>().Find(AgentUsageModule.PageId);
        Assert.Equal(SettingsCategory.Tools, page!.Category);
        foreach (var icon in new[] { "Bot", "AlertOn", "Gauge", "Warning", "Money", "DataBarHorizontal", "ArrowSync", "Timer", "ArrowDownload", "Fire" })
        {
            Assert.True(IconConverter.IsKnown(icon), icon);
        }
    }

    [Fact]
    public void Finished_tasks_and_alerts_become_notifications_per_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "rivet-agent-notify-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.ForTemporaryDirectory(root);
        var settings = SettingsStore.InMemory();
        var notifications = new FakeNotifications();
        var platform = new FakeAgentUsagePlatform(paths, createSamples: false);
        using var service = new AgentUsageService(settings, platform, paths, notifications);
        var raise = typeof(AgentUsageService).GetMethod("OnTaskFinished", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var alert = typeof(AgentUsageService).GetMethod("OnAlerted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        raise.Invoke(service, [null, new AgentFinished(AgentProvider.Claude, TimeSpan.FromSeconds(252), 1.234, 900, "app")]);
        var shown = Assert.Single(notifications.Shown);
        Assert.Equal("Claude finished", shown.Title);
        Assert.Equal("app · 4m 12s · $1.23", shown.Body);

        raise.Invoke(service, [null, new AgentFinished(AgentProvider.Codex, TimeSpan.FromSeconds(20), 0, 1, "")]);
        settings.Set(AgentUsageSettings.FinishMinimum, 0);
        raise.Invoke(service, [null, new AgentFinished(AgentProvider.Codex, TimeSpan.FromSeconds(20), 0, 1, "")]);
        Assert.Equal("20s", notifications.Shown[^1].Body);
        settings.Set(AgentUsageSettings.FinishAlert, false);
        raise.Invoke(service, [null, new AgentFinished(AgentProvider.Codex, TimeSpan.FromSeconds(200), 0, 1, "")]);
        Assert.Equal(2, notifications.Shown.Count);

        alert.Invoke(service, [null, new LimitWarningAlert(AgentProvider.Codex, new LimitWindow { Id = "codex.300", Kind = LimitWindowKind.Session, UsedPercent = 83 })]);
        Assert.Equal("Codex · Session", notifications.Shown[^1].Title);
        Assert.Equal("17% left", notifications.Shown[^1].Body);
        alert.Invoke(service, [null, new LimitRenewedAlert(AgentProvider.Claude, new LimitWindow { Id = "claude.week.opus", Kind = LimitWindowKind.Weekly, Scope = "Opus" })]);
        Assert.Equal("Claude · Week · Opus", notifications.Shown[^1].Title);
        Assert.Equal("Limit renewed", notifications.Shown[^1].Body);
        alert.Invoke(service, [null, new BudgetAlert(26.5)]);
        Assert.Equal("Daily budget", notifications.Shown[^1].Title);
        settings.Set(AgentUsageSettings.LimitAlert, false);
        var count = notifications.Shown.Count;
        alert.Invoke(service, [null, new LimitWarningAlert(AgentProvider.Codex, new LimitWindow { Id = "codex.300", UsedPercent = 90 })]);
        Assert.Equal(count, notifications.Shown.Count);
    }

    [Fact]
    public void Uninstalling_stops_the_reader_and_the_last_agent_stays_on()
    {
        var root = Path.Combine(Path.GetTempPath(), "rivet-agent-life-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.ForTemporaryDirectory(root);
        var settings = SettingsStore.InMemory();
        var platform = new FakeAgentUsagePlatform(paths, createSamples: false);
        using var service = new AgentUsageService(settings, platform, paths);
        service.Sync(true);
        Assert.True(service.IsRunning);
        service.Sync(false);
        Assert.False(service.IsRunning);
        Assert.False(service.Snapshot.Loaded);
        Assert.Equal(4, service.EnabledProviders.Count);
    }
}

public class ModuleWiringTests
{
    [AvaloniaFact]
    public async Task Every_service_resolves_from_the_host_and_the_agents_reader_loads_the_samples()
    {
        var services = TestApp.Host.Services;
        Assert.NotNull(services.GetRequiredService<Rivet.App.Features.Input.ClickFilterService>());
        Assert.NotNull(services.GetRequiredService<Rivet.App.Features.Input.KeyDebounceService>());
        Assert.NotNull(services.GetRequiredService<Rivet.App.Features.Input.MouseButtonShortcutsService>());
        Assert.NotNull(services.GetRequiredService<Rivet.App.Features.Input.SmoothScrollService>());
        Assert.NotNull(services.GetRequiredService<Rivet.App.Features.Input.ScrollInverterService>());
        Assert.NotNull(services.GetRequiredService<Rivet.App.Features.Input.SuperKeyService>());
        Assert.NotNull(services.GetRequiredService<Rivet.App.Features.Input.QuitProtectionService>());
        Assert.NotNull(services.GetRequiredService<Rivet.Core.Input.IInputFixesControl>());
        var agents = services.GetRequiredService<AgentUsageService>();
        var runtime = services.GetRequiredService<FeatureRuntime>();
        runtime.SetAvailable(FeatureIds.AgentUsage, true);
        Assert.True(agents.IsRunning);
        for (var i = 0; i < 100 && !agents.Snapshot.Loaded; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(agents.Snapshot.Loaded);
        Assert.True(agents.Snapshot.AnySeen);
    }
}
