// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Displays;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Displays;

/// <summary>
/// Displays: brightness for laptop panels (WMI), external monitors (DDC/CI)
/// and everything else (dimming overlays); the brightness OSD; the two
/// display brightness shortcuts; the panel section and Settings page.
/// Runs only while the feature is installed and "Control displays" is on.
/// </summary>
public sealed class DisplaysModule : IFeatureModule
{
    public const string SectionId = "brightness";
    public const string SettingsPageId = "displays";
    public const string DecreaseRoleId = "displayBrightnessDecrease";
    public const string IncreaseRoleId = "displayBrightnessIncrease";
    public const string DecreaseActionId = "displayBrightness.decrease";
    public const string IncreaseActionId = "displayBrightness.increase";

    public string Id => "displays";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new DimmingOverlays(sp.GetService<IScreenService>()));
        services.AddSingleton<ISoftwareDimmer>(sp => sp.GetRequiredService<DimmingOverlays>());
        services.AddSingleton(sp =>
        {
            var screens = sp.GetService<IScreenService>();
            return new BrightnessService(
                sp.GetRequiredService<ISettingsStore>(),
                sp.GetRequiredService<IDisplayCatalog>(),
                sp.GetRequiredService<ISystemBrightness>(),
                sp.GetRequiredService<IDdcChannel>(),
                sp.GetRequiredService<ISoftwareDimmer>(),
                screens is null ? null : () => screens.ScreenFromPoint(screens.CursorPosition).Id);
        });
        services.AddSingleton(sp => new BrightnessOsd(sp.GetRequiredService<BrightnessService>(), sp.GetService<IScreenService>()));
    }

    public void Initialize(ModuleContext context)
    {
        var settings = context.Get<ISettingsStore>();
        var service = context.Get<BrightnessService>();
        var osd = context.Get<BrightnessOsd>();
        context.Features.RegisterController(FeatureIds.Brightness, new DelegateFeatureController(available =>
        {
            // The runtime re-syncs when "Control displays" changes.
            var on = available && settings.Get(DisplaySettings.Enabled);
            service.Sync(on);
            osd.Sync(on);
        }));

        foreach (var (actionId, roleId, titleKey, direction, key) in new[]
        {
            (DecreaseActionId, DecreaseRoleId, "brightness.displayBrightnessDecrease", -1, VirtualKeys.OemMinus),
            (IncreaseActionId, IncreaseRoleId, "brightness.displayBrightnessIncrease", +1, VirtualKeys.OemPlus),
        })
        {
            var step = direction;
            context.Actions.Register(new AppAction
            {
                Id = actionId,
                FeatureId = FeatureIds.Brightness,
                TitleKey = titleKey,
                Icon = step < 0 ? "BrightnessLow" : "BrightnessHigh",
                ClosesPanel = false,
                Keywords = ["brightness", "display", "monitor", "dim"],
                Run = _ =>
                {
                    service.Step(step);
                    return Task.CompletedTask;
                },
            });
            context.Shortcuts.Register(new ShortcutRole
            {
                Id = roleId,
                FeatureId = FeatureIds.Brightness,
                TitleKey = titleKey,
                Storage = step < 0 ? DisplaySettings.DecreaseShortcut : DisplaySettings.IncreaseShortcut,
                Default = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, key),
                RequiredEnableKeys = [DisplaySettings.Enabled, DisplaySettings.ShortcutsEnabled],
                ActionId = actionId,
            });
        }

        var section = new PanelSectionDescriptor
        {
            Id = SectionId,
            TitleKey = "brightness.pageTitle",
            Icon = "Desktop",
            FeatureIds = [FeatureIds.Brightness],
            Order = 5,
            IsVisible = () => settings.Get(DisplaySettings.Enabled),
            CreateView = sp => new DisplaysSection(sp),
            SettingsPageId = SettingsPageId,
        };
        context.Panel.AddSection(section);

        // The panel re-evaluates IsVisible when the registry says it changed.
        settings.Observe(() => context.Panel.Invalidate(), DisplaySettings.Enabled);

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageId,
            TitleKey = "brightness.pageTitle",
            Icon = "Desktop",
            Category = SettingsCategory.EnergyDisplay,
            Order = 1,
            FeatureIds = [FeatureIds.Brightness],
            CreateView = sp => new DisplaysSettingsPage(sp),
            KeywordKeys =
            [
                "brightness.enable", "brightness.osdToggle", "brightness.keyStep", "brightness.displayBrightnessShortcuts",
                "brightness.softwareDimming", "brightness.extendedDimming",
            ],
            Keywords = ["brightness", "monitor", "DDC", "dim", "display"],
        });
    }
}
