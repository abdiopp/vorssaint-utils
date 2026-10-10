// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.CleaningMode;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Modules.RadialMenu;

namespace Rivet.App.Features.RadialMenu;

/// <summary>Radial menu: wheels of favourite actions around the pointer (spec 07 §3.2).</summary>
public sealed class RadialMenuModule : IFeatureModule
{
    public const string PageId = "radialMenu";

    public string Id => "radialMenu";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<RadialMenuService>();
        services.AddSingleton<IMouseButtonClaims>(sp => sp.GetRequiredService<RadialMenuService>());
    }

    public void Initialize(ModuleContext context)
    {
        var service = context.Get<RadialMenuService>();
        context.Features.RegisterController(FeatureIds.RadialMenu, service);
        if (context.Services.GetService<CleaningModeService>() is { } cleaning)
        {
            service.AttachCleaningMode(cleaning);
        }

        // One global shortcut per wheel (the first six wheels), through the shared shortcut manager.
        for (var slot = 0; slot < RadialMenuService.ShortcutSlots; slot++)
        {
            var captured = slot;
            context.Actions.Register(new AppAction
            {
                Id = RadialMenuService.SlotActionId(slot),
                FeatureId = FeatureIds.RadialMenu,
                TitleKey = $"win.radialMenu.wheel{slot + 1}Shortcut",
                Icon = "CircleMultipleSubtractCheckmark",
                ClosesPanel = false,
                Run = _ =>
                {
                    service.OnShortcut(captured);
                    return Task.CompletedTask;
                },
            });
            context.Shortcuts.Register(RadialMenuService.SlotRole(slot));
        }

        context.Panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "radialMenu",
            FeatureId = FeatureIds.RadialMenu,
            TitleKey = "radialMenu.pageTitle",
            CaptionKey = "radialMenu.panelCaption",
            Icon = "CircleMultipleSubtractCheckmark",
            Setting = FeatureKeys.RadialMenuEnabled,
            Category = PanelToggleCategory.Input,
            Order = 60,
            SettingsPageId = PageId,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "radialMenu.pageTitle",
            Icon = "CircleMultipleSubtractCheckmark",
            Category = SettingsCategory.Tools,
            Order = 20,
            FeatureIds = [FeatureIds.RadialMenu],
            CreateView = sp => new RadialMenuSettingsPage(sp),
            KeywordKeys = ["radialMenu.enableLabel", "radialMenu.profilesHeader", "radialMenu.actionsHeader", "radialMenu.activationModeLabel", "radialMenu.mouseTriggerLabel"],
            Keywords = ["wheel", "pie menu", "radial", "marking menu"],
        });
    }
}
