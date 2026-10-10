// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Agents;
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Agents;

/// <summary>
/// AI agent usage (spec 07 §3.8): reads the local logs of Claude Code, Codex,
/// OpenCode and GitHub Copilot CLI and shows limits, API value and work in
/// progress in an "AI Agents" panel tab (Windows has no Dynamic Island).
/// </summary>
public sealed class AgentUsageModule : IFeatureModule
{
    public const string PageId = "agentUsage";
    public const string SectionId = "agentUsage";
    public const string ShowActionId = "agentUsage.show";

    public string Id => "agentUsage";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new AgentUsageService(
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetService<IAgentUsagePlatform>() ?? new PortableAgentUsagePlatform(),
            sp.GetRequiredService<AppPaths>(),
            sp.GetService<INotificationService>()));
    }

    public void Initialize(ModuleContext context)
    {
        context.Features.RegisterController(FeatureIds.AgentUsage, context.Get<AgentUsageService>());
        context.Panel.AddSection(new PanelSectionDescriptor
        {
            Id = SectionId,
            TitleKey = "notchAgents.title",
            Icon = "Bot",
            FeatureIds = [FeatureIds.AgentUsage],
            Order = 100,
            CreateView = sp => new AgentUsageSection(sp),
            SettingsPageId = PageId,
        });
        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "notchAgents.title",
            Icon = "Bot",
            Category = SettingsCategory.Tools,
            FeatureIds = [FeatureIds.AgentUsage],
            CreateView = sp => new AgentUsageSettingsPage(sp),
            KeywordKeys = ["notchAgents.limitsCard", "notchAgents.apiValue", "notchAgents.budget", "notchAgents.finishAlert"],
            Keywords = ["Claude", "Codex", "OpenCode", "Copilot", "tokens", "AI"],
        });

        var shell = context.Get<IAppShell>();
        context.Actions.Register(new AppAction
        {
            Id = ShowActionId,
            FeatureId = FeatureIds.AgentUsage,
            TitleKey = "notchAgents.title",
            Icon = "Bot",
            ClosesPanel = false,
            Run = _ =>
            {
                shell.ShowPanel(SectionId);
                return Task.CompletedTask;
            },
        });
    }
}
