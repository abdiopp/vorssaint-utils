// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rivet.App.Controls;
using Rivet.App.Features.Cleaner;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.App;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Uninstaller;

/// <summary>The Uninstaller: run an app's own uninstaller, then review and recycle what it left behind.</summary>
public sealed class UninstallerModule : IFeatureModule
{
    public const string PageId = "uninstaller";

    public static readonly string OpenActionId = FeatureIds.Uninstaller + ".open";

    public string Id => "uninstaller";

    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<ProcessService>();
        services.AddSingleton(sp => new UninstallerService(
            sp.GetRequiredService<IInstalledAppsProvider>(),
            sp.GetRequiredService<IUninstallerPlatform>(),
            sp.GetRequiredService<ICleanerPlatform>(),
            sp.GetRequiredService<ICleanerFileSystem>(),
            sp.GetRequiredService<IRecycler>(),
            sp.GetRequiredService<IKnownFolders>(),
            sp.GetRequiredService<IProcessPlatform>(),
            sp.GetRequiredService<ProcessService>(),
            sp.GetRequiredService<ISettingsStore>(),
            () => CleanerModule.BackupFolder(sp.GetRequiredService<AppPaths>(), "Uninstaller")));
        services.AddSingleton<UninstallerSearchProvider>();
    }

    public void Initialize(ModuleContext context)
    {
        var shell = context.Get<IAppShell>();
        void Open()
        {
            var service = context.Get<UninstallerService>();
            if (!service.IsBusy && service.Phase == UninstallerPhase.Done)
            {
                service.Reset();
            }

            shell.OpenSettings(PageId);
        }

        context.Actions.Register(new AppAction
        {
            Id = OpenActionId, FeatureId = FeatureIds.Uninstaller, TitleKey = "Strings.uninstallerMenuItem", Icon = "Delete",
            Keywords = ["uninstall", "remove", "app"],
            Run = _ =>
            {
                Open();
                return Task.CompletedTask;
            },
        });

        context.TrayMenu.Add(new TrayMenuItem
        {
            Id = "uninstaller", Title = () => L.Get("Strings.uninstallerMenuItem"), Icon = "Delete", Order = 60,
            FeatureId = FeatureIds.Uninstaller, Invoke = Open,
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "uninstaller", FeatureId = FeatureIds.Uninstaller, TitleKey = "Strings.uninstallerName", CaptionKey = "hub.descUninstaller",
            Icon = "Delete", Order = 32, SettingsPageId = PageId,
            CreateHostedView = sp => new UninstallerView(sp, compact: true),
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId, TitleKey = "Strings.uninstallerName", Icon = "Delete", Category = SettingsCategory.AppManagement, Order = 10,
            FeatureIds = [FeatureIds.Uninstaller], CreateView = sp => new UninstallerSettingsPage(sp),
            KeywordKeys = ["Strings.uninstallerCommandBarToggle", "win.uninstaller.quiet"],
            Keywords = ["uninstall", "remove", "leftovers", "Add or remove programs"],
        });

        context.Search.Add(context.Get<UninstallerSearchProvider>());
    }
}

/// <summary>Settings › Uninstaller.</summary>
public sealed class UninstallerSettingsPage : SettingsPage
{
    public UninstallerSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        Content = Stack(
            Header("Strings.uninstallerName", "Strings.uninstallerEnableCaption"),
            CardText(null, new UninstallerView(services, compact: false)),
            Card(null,
                Toggle(UninstallerSettings.QuietMode, "Speaker0", "win.uninstaller.quiet", "win.uninstaller.quietCaption"),
                Toggle(UninstallerSettings.CommandBarEnabled, "Search", "Strings.uninstallerCommandBarToggle", "Strings.uninstallerCommandBarCaption")));
    }
}

/// <summary>
/// Command Bar rows while "Show in Command Bar" is on: "Uninstall Application"
/// (opens the list) and "Uninstall &lt;app&gt;…" for apps whose name matches.
/// </summary>
public sealed class UninstallerSearchProvider(UninstallerService service, ISettingsStore settings, IAppShell shell) : ISearchProvider
{
    public string Id => "uninstaller";

    public string FeatureId => FeatureIds.Uninstaller;

    /// <summary>1 for a name prefix, 0.8 for a word prefix, 0.6 anywhere, 0 for no match.</summary>
    public static double Score(string name, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 1;
        }

        var words = name.Split([' ', '-', '_', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            return 0.8;
        }

        return name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 0.6 : 0;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var text = query.Trim();
        if (!settings.Get(UninstallerSettings.CommandBarEnabled) || text.Length < 2)
        {
            return [];
        }

        await service.LoadAppsAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var category = L.Get("Strings.uninstallerName");
        var results = new List<SearchResult>();
        var browse = L.Get("Strings.uninstallerCommandBarBrowseTitle");
        var browseScore = Math.Max(Score(browse, text), Score("uninstall", text));
        if (browseScore > 0)
        {
            results.Add(new SearchResult
            {
                Id = "uninstaller:browse", Title = browse, Icon = "Delete", Score = browseScore * 0.5, Category = category,
                Activate = () => Run(null),
            });
        }

        foreach (var app in service.Apps)
        {
            var score = Score(app.DisplayName, text);
            if (score <= 0)
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Id = "uninstaller:" + app.Key,
                Title = L.Format("commandBar.uninstallAppFormat", app.DisplayName),
                Subtitle = UninstallerView.Details(app),
                Icon = "Delete",
                Score = score * 0.4,
                Category = category,
                Activate = () => Run(app),
            });
        }

        return results.OrderByDescending(r => r.Score).Take(8).ToList();
    }

    private async Task Run(InstalledApp? app)
    {
        if (service.IsBusy)
        {
            shell.OpenSettings(UninstallerModule.PageId);
            return;
        }

        service.Reset();
        shell.OpenSettings(UninstallerModule.PageId);
        if (app is not null)
        {
            await service.SelectAsync(app).ConfigureAwait(false);
        }
    }
}
