// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.QuickPanel;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.QuickPanel;

/// <summary>
/// The quick panel (feature quickLauncher, spec 06 §3.9): its window, the
/// Ctrl+Alt+Win+Q shortcut role, the Utilities tile and the Settings page.
/// </summary>
public sealed class QuickPanelModule : IFeatureModule
{
    public const string PageId = "quickLauncher";
    public const string RoleId = "quickLauncher";
    public const string ShowActionId = "quickLauncher.show";

    public string Id => "quickPanel";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<QuickPanelCatalog>();
        services.AddSingleton<QuickPanelHost>();
    }

    public void Initialize(ModuleContext context)
    {
        var services = context.Services;
        context.Features.RegisterController(FeatureIds.QuickLauncher, context.Get<QuickPanelHost>());

        context.Actions.Register(new AppAction
        {
            Id = ShowActionId,
            FeatureId = FeatureIds.QuickLauncher,
            TitleKey = "Strings.launcherName",
            Icon = "Grid",
            Keywords = ["quick panel", "launcher", "tools", "favorites"],
            Run = ctx =>
            {
                var host = services.GetRequiredService<QuickPanelHost>();
                if (ctx.Source == ActionSource.Panel)
                {
                    // Spec 05 §3.4.7: the tray panel closes first, the quick panel shows 0.15 s later.
                    DispatcherTimer.RunOnce(host.Toggle, TimeSpan.FromMilliseconds(150));
                }
                else
                {
                    Dispatcher.UIThread.Post(host.Toggle);
                }

                return Task.CompletedTask;
            },
        });

        context.Shortcuts.Register(new ShortcutRole
        {
            Id = RoleId,
            FeatureId = FeatureIds.QuickLauncher,
            TitleKey = "Strings.launcherName",
            Storage = QuickPanelSettings.Shortcut,
            Default = QuickPanelSettings.DefaultShortcut,
            RequiredEnableKeys = [QuickPanelSettings.ShortcutEnabled],
            ActionId = ShowActionId,
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "quickLauncher",
            FeatureId = FeatureIds.QuickLauncher,
            TitleKey = "Strings.launcherName",
            CaptionKey = "Strings.launcherCaption",
            Icon = "Grid",
            Order = 15,
            ActionId = ShowActionId,
            ShortcutRoleId = RoleId,
            SettingsPageId = PageId,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "Strings.launcherName",
            Icon = "Grid",
            Category = SettingsCategory.Tools,
            Order = 10,
            FeatureIds = [FeatureIds.QuickLauncher],
            CreateView = sp => new QuickPanelSettingsPage(sp),
            KeywordKeys = ["Strings.launcherCaption", "Strings.launcherOpenNow"],
            Keywords = ["quick panel", "launcher", "favorites", "tools", "grid"],
        });
    }
}

/// <summary>Owns the quick panel window (created on first use) and closes it when the feature goes away.</summary>
public sealed class QuickPanelHost(IServiceProvider services) : IFeatureController
{
    private QuickPanelWindow? _window;

    public QuickPanelWindow Window => _window ??= new QuickPanelWindow(services);

    public bool IsOpen => _window?.IsVisible == true;

    public void Toggle() => Window.Open();

    public void Close()
    {
        if (_window is { IsVisible: true })
        {
            _window.Dismiss(FloatingCloseReason.Action);
        }
    }

    public void Sync(bool available)
    {
        if (!available)
        {
            Dispatcher.UIThread.Post(Close);
        }
    }
}
