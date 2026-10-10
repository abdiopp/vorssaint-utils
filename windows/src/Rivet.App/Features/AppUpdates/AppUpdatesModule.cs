// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rivet.App.Controls;
using Rivet.App.Features.PackageManager;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.AppUpdates;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.AppUpdates;

/// <summary>App updates through winget, the Microsoft Store and Electron apps' own feeds.</summary>
public sealed class AppUpdatesModule : IFeatureModule
{
    public const string PageId = "appUpdates";

    public string Id => "appUpdates";

    public void ConfigureServices(IServiceCollection services)
    {
        PackageManagerModule.AddWinget(services);
        services.TryAddSingleton<IFeedFetcher, HttpFeedFetcher>();
        services.TryAddSingleton<OnlineUpdateSource>();
        services.AddSingleton<AppUpdatesService>();
    }

    public void Initialize(ModuleContext context)
    {
        var shell = context.Get<IAppShell>();
        var updates = context.Get<AppUpdatesService>();
        context.Features.RegisterController(FeatureIds.AppUpdates, updates);

        context.Actions.Register(new AppAction
        {
            Id = AppUpdatesActions.Open, FeatureId = FeatureIds.AppUpdates, TitleKey = "appUpdates.pageTitle", Icon = "ArrowDownload",
            Run = _ =>
            {
                shell.OpenSettings(PageId);
                return Task.CompletedTask;
            },
        });
        context.Actions.Register(new AppAction
        {
            Id = AppUpdatesActions.Check, FeatureId = FeatureIds.AppUpdates, TitleKey = "appUpdates.checkNow", Icon = "ArrowSync",
            Keywords = ["update", "upgrade", "winget", "version"],
            Run = _ =>
            {
                shell.OpenSettings(PageId);
                return updates.CheckAsync();
            },
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "appUpdates", FeatureId = FeatureIds.AppUpdates, TitleKey = "appUpdates.pageTitle", CaptionKey = "appUpdates.panelCaption",
            Icon = "ArrowDownload", Order = 34, SettingsPageId = PageId,
            CreateHostedView = sp => new AppUpdatesView(sp, compact: true),
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId, TitleKey = "appUpdates.pageTitle", Icon = "ArrowDownload", Category = SettingsCategory.AppManagement, Order = 20,
            FeatureIds = [FeatureIds.AppUpdates], CreateView = sp => new AppUpdatesSettingsPage(sp),
            KeywordKeys = ["appUpdates.frequencyLabel", "appUpdates.sourcesTitle", "appUpdates.rulesTitle", "appUpdates.notifyToggle"],
            Keywords = ["update", "upgrade", "winget", "Microsoft Store"],
        });
    }
}

/// <summary>Settings › App updates.</summary>
public sealed class AppUpdatesSettingsPage : SettingsPage
{
    private readonly AppUpdatesService _updates;
    private readonly INotificationService? _notifications;
    private readonly TextBlock _next = new() { Classes = { "caption" } };
    private readonly List<(Setting<bool> Setting, ToggleSwitch Toggle)> _sources = [];
    private ToggleSwitch? _notify;

    public AppUpdatesSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _updates = services.GetRequiredService<AppUpdatesService>();
        _notifications = services.GetService<INotificationService>();
        var shell = services.GetRequiredService<IShellService>();

        var frequency = Choice(AppUpdatesSettings.CheckFrequency, "CalendarClock", "appUpdates.frequencyLabel", null,
        [
            ("off", L.Get("appUpdates.frequencyOff")),
            ("daily", L.Get("appUpdates.frequencyDaily")),
            ("weekly", L.Get("appUpdates.frequencyWeekly")),
        ]);
        var notify = Toggle(AppUpdatesSettings.Notify, "Alert", "appUpdates.notifyToggle");
        _notify = notify.Content as ToggleSwitch;
        var notificationsOff = _notifications is { IsEnabled: false }
            ? Row(null, L.Get("win.appUpdates.notificationsOff"), null, ActionButton(L.Get("Strings.cleanerNotifOpenSettings"), () => shell.OpenSystemSettings("ms-settings:notifications")))
            : null;

        var winget = Toggle(AppUpdatesSettings.IncludePackageManager, "Box", "appUpdates.includeHomebrewToggle", "win.appUpdates.includeWingetCaption", _ => KeepOneSource());
        var store = Toggle(AppUpdatesSettings.IncludeStore, "StoreMicrosoft", "appUpdates.includeStoreToggle", "appUpdates.includeStoreCaption", _ => KeepOneSource());
        var online = Toggle(AppUpdatesSettings.IncludeOnline, "Globe", "appUpdates.includeOnlineToggle", "appUpdates.includeOnlineCaption", _ => KeepOneSource());
        _sources.Add((AppUpdatesSettings.IncludePackageManager, (ToggleSwitch)winget.Content!));
        _sources.Add((AppUpdatesSettings.IncludeStore, (ToggleSwitch)store.Content!));
        _sources.Add((AppUpdatesSettings.IncludeOnline, (ToggleSwitch)online.Content!));

        Content = Stack(
            Header("appUpdates.pageTitle", "appUpdates.caption"),
            CardText(null, new AppUpdatesView(services, compact: false)),
            Card("appUpdates.frequencyLabel", frequency, Row(null, string.Empty, null, _next), notify, notificationsOff),
            Card("appUpdates.sourcesTitle", winget, store, online, Note(L.Get("win.appUpdates.lastSource"))));

        Track(Settings.Observe(() => Dispatcher.UIThread.Post(Refresh), AppUpdatesSettings.CheckFrequency, AppUpdatesSettings.LastCheck));
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(EnableSources), AppUpdatesSettings.IncludePackageManager, AppUpdatesSettings.IncludeStore, AppUpdatesSettings.IncludeOnline));
        Refresh();
        EnableSources();
    }

    /// <summary>"Next check …" and the notification switch, which only means something while checks are scheduled.</summary>
    private void Refresh()
    {
        var next = _updates.NextCheckUtc();
        _next.Text = next is { } utc ? L.Format("appUpdates.nextCheckFormat", When(utc)) : string.Empty;
        _next.IsVisible = next is not null;
        if (_notify is not null)
        {
            _notify.IsEnabled = Settings.Get(AppUpdatesSettings.CheckFrequency) != "off";
        }
    }

    /// <summary>The last source that is on cannot be switched off.</summary>
    private void EnableSources()
    {
        var on = _sources.Count(s => Settings.Get(s.Setting));
        foreach (var (setting, toggle) in _sources)
        {
            toggle.IsEnabled = !(on == 1 && Settings.Get(setting));
        }
    }

    private void KeepOneSource()
    {
        if (_sources.All(s => !Settings.Get(s.Setting)))
        {
            Settings.Set(AppUpdatesSettings.IncludePackageManager, true);
        }

        EnableSources();
    }
}
