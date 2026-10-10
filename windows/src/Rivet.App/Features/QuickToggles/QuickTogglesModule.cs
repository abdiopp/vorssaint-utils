// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Toggles;

namespace Rivet.App.Features.Toggles;

/// <summary>
/// Quick toggles (spec 06 §3.10, spec 05 §3.4.9): the tray panel section,
/// the list hosted by the quick panel, one action per row and the Settings page.
/// </summary>
public sealed class QuickTogglesModule : IFeatureModule
{
    public const string PageId = "quickToggles";
    public const string SectionId = "toggles";

    /// <summary>Action id prefix: <c>quickToggle.darkMode</c>, <c>quickToggle.emptyTrash</c>…</summary>
    public const string ActionPrefix = FeatureIds.QuickToggles + "."; // "<feature>.<id>", as the radial menu resolves them

    public string Id => "quickToggles";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<QuickTogglesService>();
        services.AddSingleton<QuickToggleCatalog>();
    }

    public void Initialize(ModuleContext context)
    {
        var services = context.Services;
        context.Panel.AddSection(new PanelSectionDescriptor
        {
            Id = SectionId,
            TitleKey = "quickToggles.pageTitle",
            Icon = "ToggleMultiple",
            FeatureIds = [FeatureIds.QuickToggles],
            Order = 90,
            KeepsPanelOpen = true,
            SettingsPageId = PageId,
            CreateView = sp => new QuickTogglesView(sp, editable: true),
        });

        foreach (var id in Enum.GetValues<QuickToggleId>().Where(i => i != QuickToggleId.MicMute))
        {
            var toggle = id;
            context.Actions.Register(new AppAction
            {
                Id = ActionId(toggle),
                FeatureId = FeatureIds.QuickToggles,
                TitleKey = ActionTitleKey(toggle),
                Icon = ActionIcon(toggle),
                Keywords = ["toggle", QuickToggleSettings.StorageId(toggle)],
                ClosesPanel = QuickToggleCatalog.ClosesSurface(toggle),
                Run = ctx =>
                {
                    var tcs = new TaskCompletionSource();
                    Dispatcher.UIThread.Post(async () =>
                    {
                        try
                        {
                            await services.GetRequiredService<QuickToggleCatalog>().RunAsync(toggle, owner: null, closeSurface: null, ctx.Source).ConfigureAwait(true);
                        }
                        finally
                        {
                            tcs.TrySetResult();
                        }
                    });
                    return tcs.Task;
                },
            });
        }

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "quickToggles.pageTitle",
            Icon = "ToggleMultiple",
            Category = SettingsCategory.Tools,
            Order = 30,
            FeatureIds = [FeatureIds.QuickToggles],
            CreateView = sp => new QuickTogglesSettingsPage(sp),
            KeywordKeys = ["quickToggles.darkModeToDark", "quickToggles.emptyTrashTitle", "quickToggles.lockScreenTitle", "diskExclusions.listTitle"],
            Keywords = ["dark mode", "recycle bin", "hidden files", "lock", "eject"],
        });
    }

    public static string ActionId(QuickToggleId id) => ActionPrefix + QuickToggleSettings.StorageId(id);

    /// <summary>Static titles for the shortcut/radial lists (the rows themselves follow the system state).</summary>
    private static string ActionTitleKey(QuickToggleId id) => id switch
    {
        QuickToggleId.DarkMode => "win.quickToggles.darkModeAction",
        QuickToggleId.HiddenFiles => "win.quickToggles.hiddenFilesAction",
        QuickToggleId.FileExtensions => "win.quickToggles.extensionsAction",
        QuickToggleId.DesktopIcons => "win.quickToggles.desktopIconsAction",
        QuickToggleId.EmptyTrash => "quickToggles.emptyTrashTitle",
        QuickToggleId.EjectDisks => "quickToggles.ejectTitle",
        QuickToggleId.LockScreen => "quickToggles.lockScreenTitle",
        QuickToggleId.DisplayOff => "quickToggles.displayOffTitle",
        QuickToggleId.ScreenSaver => "quickToggles.screenSaverTitle",
        _ => "commandBar.powerSleep",
    };

    private static string ActionIcon(QuickToggleId id) => id switch
    {
        QuickToggleId.DarkMode => "DarkTheme",
        QuickToggleId.EmptyTrash => "Delete",
        QuickToggleId.EjectDisks => "ArrowEject",
        QuickToggleId.HiddenFiles => "Eye",
        QuickToggleId.FileExtensions => "DocumentText",
        QuickToggleId.DesktopIcons => "Desktop",
        QuickToggleId.LockScreen => "LockClosed",
        QuickToggleId.DisplayOff => "DesktopOff",
        QuickToggleId.ScreenSaver => "SlideMultiple",
        _ => "WeatherMoon",
    };
}
