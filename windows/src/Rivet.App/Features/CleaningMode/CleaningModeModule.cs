// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Modules.CleaningMode;
using Rivet.Core.Settings;

namespace Rivet.App.Features.CleaningMode;

/// <summary>Cleaning Mode: lock the keyboard (Esc ×5 unlocks), from the panel, tray, Settings, Command Bar and radial menu.</summary>
public sealed class CleaningModeModule : IFeatureModule
{
    public const string LockActionId = "cleaningMode.lock";
    public const string PageId = "cleaningMode";

    public string Id => "cleaningMode";

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<CleaningModeService>();

    public void Initialize(ModuleContext context)
    {
        var service = context.Get<CleaningModeService>();

        // Uninstalling while locked ends the lock (macOS leaves this unhandled).
        context.Features.RegisterController(FeatureIds.CleaningMode, new DelegateFeatureController(available =>
        {
            if (!available && service.IsActive)
            {
                service.Controller.ForceEnd(CleaningEndReason.Shutdown);
            }
        }));

        context.Actions.Register(new AppAction
        {
            Id = LockActionId,
            FeatureId = FeatureIds.CleaningMode,
            TitleKey = "Strings.cleaningMenuItem",
            SubtitleKey = "Strings.cleaningPanelCaption",
            Icon = "Broom",
            Keywords = ["clean", "keyboard", "lock", "wipe"],
            Run = async ctx =>
            {
                // Let the launching surface disappear first (launcher and Command Bar 0.1 s).
                if (ctx.Source is ActionSource.CommandBar or ActionSource.QuickPanel)
                {
                    await Task.Delay(CleaningModeConstants.LauncherDelay).ConfigureAwait(true);
                }

                service.Activate();
            },
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "cleaningMode",
            FeatureId = FeatureIds.CleaningMode,
            TitleKey = "Strings.cleaningMenuItem",
            CaptionKey = "Strings.cleaningPanelCaption",
            Icon = "Broom",
            Order = 70,
            ActionId = LockActionId,
            SettingsPageId = PageId,
        });

        context.TrayMenu.Add(new TrayMenuItem
        {
            Id = "cleaningMode",
            Title = () => L.Get("Strings.cleaningMenuItem"),
            Icon = "Broom",
            Order = -30,
            FeatureId = FeatureIds.CleaningMode,
            Invoke = service.Activate,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "Strings.cleaningMenuItem",
            Icon = "Broom",
            Category = SettingsCategory.Tools,
            Order = 40,
            FeatureIds = [FeatureIds.CleaningMode],
            CreateView = sp => new CleaningModeSettingsPage(sp),
            KeywordKeys = ["Strings.cleaningStartNow", "Strings.cleaningKeepScreenVisibleToggle"],
            Keywords = ["clean", "keyboard", "lock", "wipe"],
        });
    }
}

/// <summary>Settings › Tools › Cleaning Mode.</summary>
public sealed class CleaningModeSettingsPage : SettingsPage
{
    public CleaningModeSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var service = services.GetRequiredService<CleaningModeService>();
        var lockButton = ActionButton(L.Get("Strings.cleaningStartNow"), service.Activate, "LockClosed", accent: true);
        Content = Stack(
            Header("Strings.cleaningMenuItem", "hub.descCleaningMode"),
            Card(null,
                Row("Broom", L.Get("Strings.cleaningStartNow"), L.Get("Strings.cleaningPanelCaption"), lockButton),
                Toggle(CleaningModeSettings.KeepScreenVisible, "Eye", "Strings.cleaningKeepScreenVisibleToggle", "Strings.cleaningKeepScreenVisibleCaption")),
            Card("win.cleaningMode.howTitle",
                Row("Keyboard", L.Get("win.cleaningMode.overlaySubtitle"), L.Get("win.cleaningMode.unlockCaption")),
                Row("CursorClick", L.Get("win.cleaningMode.mouseHint"), null),
                Row("Warning", L.Get("win.cleaningMode.limitsTitle"), L.Get("win.cleaningMode.limits"))));
    }
}
