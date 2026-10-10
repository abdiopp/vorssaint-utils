// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Launcher;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Launcher;

/// <summary>
/// The Command Bar (spec 06 §3.8): the bar window and its controller, its
/// shortcut role (Alt+Space, off by default), the Utilities tile, the Settings
/// page and one <see cref="Rivet.Core.Contracts.ISearchProvider"/> per built-in
/// source so other surfaces can search the same things.
/// </summary>
public sealed class CommandBarModule : IFeatureModule
{
    public const string PageId = "commandBar";
    public const string RoleId = "commandBar";

    public string Id => "commandBar";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<CommandBarPreferences>();
        services.AddSingleton<CommandBarCatalog>();
        services.AddSingleton<CommandBarController>();
    }

    public void Initialize(ModuleContext context)
    {
        var services = context.Services;
        context.Features.RegisterController(FeatureIds.CommandBar, context.Get<CommandBarController>());

        context.Actions.Register(new AppAction
        {
            Id = CommandBarCatalog.OwnActionId,
            FeatureId = FeatureIds.CommandBar,
            TitleKey = "commandBar.pageTitle",
            Icon = "Search",
            Keywords = ["command", "launcher", "search", "run"],
            Run = ctx =>
            {
                var controller = services.GetRequiredService<CommandBarController>();
                if (ctx.Source == ActionSource.Panel)
                {
                    // Spec 05 §3.4.7: the tray panel closes first, the bar shows 0.15 s later.
                    DispatcherTimer.RunOnce(controller.Toggle, TimeSpan.FromMilliseconds(150));
                }
                else
                {
                    Dispatcher.UIThread.Post(controller.Toggle);
                }

                return Task.CompletedTask;
            },
        });

        context.Shortcuts.Register(new ShortcutRole
        {
            Id = RoleId,
            FeatureId = FeatureIds.CommandBar,
            TitleKey = "commandBar.pageTitle",
            Storage = CommandBarSettings.Shortcut,
            Default = CommandBarSettings.DefaultShortcut,
            RequiredEnableKeys = [CommandBarSettings.ShortcutEnabled],
            ActionId = CommandBarCatalog.OwnActionId,
            Options = HotkeyOptions.OverrideSystem,
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "commandBar",
            FeatureId = FeatureIds.CommandBar,
            TitleKey = "commandBar.pageTitle",
            CaptionKey = "commandBar.panelCaption",
            Icon = "Search",
            Order = 150,
            ActionId = CommandBarCatalog.OwnActionId,
            ShortcutRoleId = RoleId,
            SettingsPageId = PageId,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "commandBar.pageTitle",
            Icon = "Search",
            Category = SettingsCategory.Tools,
            Order = 0,
            FeatureIds = [FeatureIds.CommandBar],
            CreateView = sp => new CommandBarSettingsPage(sp),
            KeywordKeys =
            [
                "commandBar.shortcutToggle", "commandBar.compactModeToggle", "commandBar.sourcesTitle", "commandBar.filesTitle",
                "commandBar.linksTitle", "commandBar.rowShortcutsTitle", "commandBar.pinnedTitle", "commandBar.appCenterTitle",
            ],
            Keywords = ["launcher", "search", "calculator", "emoji", "run", "Alt+Space"],
        });

        foreach (var provider in CommandBarProviders.Create(services))
        {
            context.Search.Add(provider);
        }
    }
}
