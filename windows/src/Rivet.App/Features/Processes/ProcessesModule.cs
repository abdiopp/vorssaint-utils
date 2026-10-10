// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Settings;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.Processes;

/// <summary>Kill Process: find running processes and close, force quit, restart or end their trees.</summary>
public sealed class KillProcessModule : IFeatureModule
{
    public const string PageId = "killProcess";

    public static readonly string OpenActionId = FeatureIds.KillProcess + ".open";

    public string Id => "killProcess";

    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<ProcessService>();
        services.AddSingleton<ProcessSearchProvider>();
    }

    public void Initialize(ModuleContext context)
    {
        var shell = context.Get<IAppShell>();
        context.Actions.Register(new AppAction
        {
            Id = OpenActionId, FeatureId = FeatureIds.KillProcess, TitleKey = "killProcess.pageTitle", Icon = "ErrorCircle",
            Keywords = ["process", "task", "kill", "end task", "force quit"],
            Run = _ =>
            {
                shell.OpenSettings(PageId);
                return Task.CompletedTask;
            },
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "killProcess", FeatureId = FeatureIds.KillProcess, TitleKey = "killProcess.pageTitle", CaptionKey = "killProcess.hubDescription",
            Icon = "ErrorCircle", Order = 38, SettingsPageId = PageId,
            CreateHostedView = sp => new KillProcessView(sp, compact: true),
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId, TitleKey = "killProcess.pageTitle", Icon = "ErrorCircle", Category = SettingsCategory.AppManagement, Order = 40,
            FeatureIds = [FeatureIds.KillProcess], CreateView = sp => new KillProcessSettingsPage(sp),
            KeywordKeys = ["killProcess.groupToggle", "killProcess.commandBarToggle", "killProcess.forceKillButton", "killProcess.killTreeButton"],
            Keywords = ["process", "task manager", "end task", "kill"],
        });

        context.Search.Add(context.Get<ProcessSearchProvider>());
    }
}

/// <summary>Port Manager: listening ports and the processes behind them.</summary>
public sealed class PortManagerModule : IFeatureModule
{
    public const string PageId = "portManager";

    public static readonly string OpenActionId = FeatureIds.PortManager + ".open";

    public string Id => "portManager";

    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<ProcessService>();
        services.TryAddSingleton<PortService>();
    }

    public void Initialize(ModuleContext context)
    {
        var shell = context.Get<IAppShell>();
        context.Actions.Register(new AppAction
        {
            Id = OpenActionId, FeatureId = FeatureIds.PortManager, TitleKey = "portManager.title", Icon = "PlugConnected",
            Keywords = ["port", "listening", "localhost", "server", "tcp"],
            Run = _ =>
            {
                shell.OpenSettings(PageId);
                return Task.CompletedTask;
            },
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "portManager", FeatureId = FeatureIds.PortManager, TitleKey = "portManager.title", CaptionKey = "portManager.listeningCaption",
            Icon = "PlugConnected", Order = 40, SettingsPageId = PageId,
            CreateHostedView = sp => new PortManagerView(sp, compact: true),
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId, TitleKey = "portManager.title", Icon = "PlugConnected", Category = SettingsCategory.AppManagement, Order = 50,
            FeatureIds = [FeatureIds.PortManager], CreateView = sp => new PortManagerSettingsPage(sp),
            KeywordKeys = ["portManager.listeningCaption", "win.processes.showUdp"],
            Keywords = ["port", "localhost", "tcp", "udp", "server"],
        });
    }
}

/// <summary>Settings › Kill Process.</summary>
public sealed class KillProcessSettingsPage : SettingsPage
{
    public KillProcessSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var elevation = services.GetRequiredService<IElevationService>();
        Content = Stack(
            Header("killProcess.pageTitle", "killProcess.hubDescription"),
            Card(null,
                Toggle(ProcessSettings.GroupRelated, "GroupList", "killProcess.groupToggle", "killProcess.groupCaption"),
                Toggle(ProcessSettings.CommandBarEnabled, "Search", "killProcess.commandBarToggle", "killProcess.commandBarCaption"),
                elevation.IsElevated
                    ? null
                    : Row("Shield", L.Get("win.processes.adminTitle"), L.Get("win.processes.adminNote"), ActionButton(L.Get("win.cleaner.restartAsAdmin"), () => elevation.RestartElevated()))),
            CardText(null, new KillProcessView(services, compact: false)));
    }
}

/// <summary>Settings › Port Manager.</summary>
public sealed class PortManagerSettingsPage : SettingsPage
{
    public PortManagerSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        Content = Stack(
            Header("portManager.title", "portManager.hubDescription"),
            Card(null, Toggle(ProcessSettings.PortManagerShowUdp, "ArrowSwap", "win.processes.showUdp", "win.processes.showUdpCaption")),
            CardText(null, new PortManagerView(services, compact: false)));
    }
}

/// <summary>
/// The Command Bar's "Kill Process" category while "Show in Command Bar" is
/// on: running processes whose name contains the text, or whose PID equals
/// it. Activating one asks before closing it.
/// </summary>
public sealed class ProcessSearchProvider(ProcessService processes, ISettingsStore settings, IElevationService elevation) : ISearchProvider
{
    public string Id => "killProcess";

    public string FeatureId => FeatureIds.KillProcess;

    public static double Score(ProcessRow row, string query)
    {
        if (int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid == row.Pid)
        {
            return 1;
        }

        if (row.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) || row.ImageName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0.9;
        }

        return row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || row.ImageName.Contains(query, StringComparison.OrdinalIgnoreCase) ? 0.6 : 0;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var text = query.Trim();
        if (!settings.Get(ProcessSettings.CommandBarEnabled) || text.Length < 2)
        {
            return [];
        }

        await processes.RefreshAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var category = L.Get("killProcess.pageTitle");
        return processes.Processes
            .Where(p => !p.IsProtected)
            .Select(p => (Row: p, Score: Score(p, text)))
            .Where(p => p.Score > 0)
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.Row.MemoryBytes)
            .Take(8)
            .Select(p => new SearchResult
            {
                Id = "killProcess:" + p.Row.Pid.ToString(CultureInfo.InvariantCulture),
                Title = p.Row.Name,
                Subtitle = $"{L.Format("killProcess.pidLabelFormat", p.Row.Pid)}  ·  {Size(p.Row.MemoryBytes)}",
                IconPath = p.Row.Path,
                Icon = p.Row.Path is null ? "ErrorCircle" : null,
                Score = p.Score * 0.3,
                Category = category,
                Activate = () => KillAsync(p.Row),
            })
            .ToList();
    }

    private async Task KillAsync(ProcessRow row)
    {
        if (await ConfirmDialog.ShowAsync(null, L.Format("killProcess.confirmKillFormat", row.Name), row.Path ?? row.ImageName, L.Get("killProcess.killButton"), L.Get("Strings.uninstallerCancel"), destructive: true))
        {
            var report = await processes.KillAsync(row, KillMode.Kill).ConfigureAwait(true);
            if (report.NeedsAdministrator.Count > 0 && !elevation.IsElevated
                && await ConfirmDialog.ShowAsync(null, L.Get("killProcess.killFailedTitle"), L.Format("win.processes.accessDeniedFormat", row.Name), L.Get("win.cleaner.restartAsAdmin"), L.Get("Strings.uninstallerCancel")))
            {
                elevation.RestartElevated();
            }
        }
    }
}
