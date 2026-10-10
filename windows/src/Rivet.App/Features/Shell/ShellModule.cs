// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.App.Settings.Pages;
using Rivet.App.Shell;
using Rivet.App.Shell.Sections;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Shell;

/// <summary>The app shell's own contributions: panel sections, built-in Settings pages and shell actions.</summary>
public sealed class ShellModule : IFeatureModule
{
    public string Id => "shell";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<AppShell>();
        services.AddSingleton<IAppShell>(sp => sp.GetRequiredService<AppShell>());
        services.AddSingleton<TrayController>();
        services.AddSingleton<ITrayPresence>(sp => sp.GetRequiredService<TrayController>());
        services.AddSingleton<HudService>();
        services.AddSingleton<IHud>(sp => sp.GetRequiredService<HudService>());
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ThemeController>();
        services.AddSingleton<SettingsNavigationState>();
    }

    public void Initialize(ModuleContext context)
    {
        var panel = context.Panel;
        var runtime = context.Features;
        var settings = context.Settings;
        panel.AddSection(new PanelSectionDescriptor
        {
            Id = "utilities",
            TitleKey = "Strings.utilitiesSection",
            Icon = "WrenchScrewdriver",
            FeatureIds = [],
            Order = 60, // spec 05 §3.4.3: after the monitor sections (Power is 40)
            CreateView = sp => new UtilitiesSection(sp),
            IsVisible = () => UtilitiesSection.VisibleTiles(panel, runtime, settings).Count > 0,
        });
        panel.AddSection(new PanelSectionDescriptor
        {
            Id = "controls",
            TitleKey = "Strings.quickControlsSection",
            Icon = "ToggleLeft",
            FeatureIds = [],
            Order = 80,
            CreateView = sp => new ControlsSection(sp),
            IsVisible = () => panel.Toggles.Any(t => runtime.IsAvailable(t.FeatureId)),
        });

        var pages = context.SettingsPages;
        pages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageIds.General, TitleKey = "Strings.tabGeneral", Icon = "Settings",
            Category = SettingsCategory.Essentials, Order = 0, CreateView = sp => new GeneralPage(sp),
            KeywordKeys = ["Strings.languageLabel", "Strings.launchAtLogin", "appearance.label", "appearance.dark", "appearance.light"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageIds.TrayPanel, TitleKey = "win.shell.trayPanelTitle", Icon = "PanelBottom",
            Category = SettingsCategory.Essentials, Order = 1, CreateView = sp => new TrayPanelPage(sp),
            KeywordKeys = ["Strings.utilitiesSection"], Keywords = ["tray", "taskbar", "panel"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageIds.Features, TitleKey = "hub.pageTitle", Icon = "Grid",
            Category = SettingsCategory.Essentials, Order = 2, CreateView = sp => new FeaturesPage(sp),
            KeywordKeys = ["hub.presetsTitle", "hub.installAllButton", "hub.uninstallAllButton"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageIds.Shortcuts, TitleKey = "Strings.shortcutsPageTitle", Icon = "Keyboard",
            Category = SettingsCategory.App, Order = 0, CreateView = sp => new ShortcutsPage(sp),
            Keywords = ["hotkey", "shortcut"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageIds.Advanced, TitleKey = "Strings.tabAdvanced", Icon = "WrenchScrewdriver",
            Category = SettingsCategory.App, Order = 1, CreateView = sp => new AdvancedPage(sp),
            KeywordKeys = ["backup.title", "backup.exportButton", "backup.importButton"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageIds.About, TitleKey = "Strings.tabAbout", Icon = "Info",
            Category = SettingsCategory.App, Order = 2, CreateView = sp => new AboutPage(sp),
            KeywordKeys = ["Strings.updatesSection", "Strings.autoCheckToggle"], Keywords = ["version", "update"],
        });

        var shell = context.Get<IAppShell>();
        context.Actions.Register(new AppAction { Id = "shell.openSettings", FeatureId = "", TitleKey = "Strings.menuSettings", Icon = "Settings", Run = _ => { shell.OpenSettings(); return Task.CompletedTask; } });
        context.Actions.Register(new AppAction { Id = "shell.openAbout", FeatureId = "", TitleKey = "Strings.tabAbout", Icon = "Info", Run = _ => { shell.OpenSettings(SettingsPageIds.About); return Task.CompletedTask; } });
        context.Actions.Register(new AppAction { Id = "shell.openFeatures", FeatureId = "", TitleKey = "hub.pageTitle", Icon = "Grid", Run = _ => { shell.OpenSettings(SettingsPageIds.Features); return Task.CompletedTask; } });
        context.Actions.Register(new AppAction { Id = "shell.showPanel", FeatureId = "", TitleKey = "win.shell.trayIconTitle", Icon = "PanelBottom", ClosesPanel = false, Run = _ => { shell.ShowPanel(); return Task.CompletedTask; } });
        context.Actions.Register(new AppAction { Id = "shell.quit", FeatureId = "", TitleKey = "Strings.panelQuit", Icon = "Power", Run = _ => { shell.Quit(); return Task.CompletedTask; } });
    }
}
