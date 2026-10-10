// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.PackageManager;

/// <summary>The Homebrew manager rebuilt on winget: search, install, update, uninstall, sources.</summary>
public sealed class PackageManagerModule : IFeatureModule
{
    public const string PageId = "packageManager";

    public static readonly string OpenActionId = FeatureIds.PackageManager + ".open";

    public string Id => "packageManager";

    public void ConfigureServices(IServiceCollection services) => AddWinget(services);

    /// <summary>
    /// winget's client and its single operation lane are shared with App
    /// updates (one winget operation at a time across both features).
    /// </summary>
    internal static void AddWinget(IServiceCollection services)
    {
        services.TryAddSingleton<WingetClient>();
        services.TryAddSingleton<PackageOperationLane>();
        services.TryAddSingleton<PackageManagerService>();
    }

    public void Initialize(ModuleContext context)
    {
        var shell = context.Get<IAppShell>();
        context.Actions.Register(new AppAction
        {
            Id = OpenActionId, FeatureId = FeatureIds.PackageManager, TitleKey = "win.shell.packageManagerTitle", Icon = "Box",
            Keywords = ["winget", "install", "package", "update"],
            Run = _ =>
            {
                shell.OpenSettings(PageId);
                return Task.CompletedTask;
            },
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "packageManager", FeatureId = FeatureIds.PackageManager, TitleKey = "win.shell.packageManagerTitle",
            CaptionKey = "win.shell.packageManagerDescription", Icon = "Box", Order = 36, SettingsPageId = PageId,
            CreateHostedView = sp => new PackageManagerView(sp, compact: true),
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId, TitleKey = "win.shell.packageManagerTitle", Icon = "Box", Category = SettingsCategory.AppManagement, Order = 30,
            FeatureIds = [FeatureIds.PackageManager], CreateView = sp => new PackageManagerSettingsPage(sp),
            KeywordKeys = ["win.packageManager.sourcesTitle", "Strings.homebrewUpdates", "Strings.homebrewInstall", "Strings.homebrewUninstall"],
            Keywords = ["winget", "App Installer", "package", "install"],
        });
    }
}

/// <summary>Settings › Package manager.</summary>
public sealed class PackageManagerSettingsPage : SettingsPage
{
    public PackageManagerSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        Content = Stack(
            Header("win.shell.packageManagerTitle", "win.shell.packageManagerDescription"),
            CardText(null, new PackageManagerView(services, compact: false)),
            Card("win.packageManager.sourcesTitle", new WingetSourcesView(services)));
    }
}

/// <summary>The configured winget sources and "Update sources".</summary>
public sealed class WingetSourcesView : UserControl
{
    private readonly PackageManagerService _manager;
    private (IReadOnlyList<WingetSource>, WingetAvailability, bool, bool) _shown;
    private bool _requested;

    public WingetSourcesView(IServiceProvider services)
    {
        _manager = services.GetRequiredService<PackageManagerService>();
        Build();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _manager.Changed += OnChanged;
        if (!_requested && _manager.Availability == WingetAvailability.Available && _manager.AgreementsAccepted)
        {
            _requested = true;
            _ = _manager.LoadSourcesAsync();
        }

        Build();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _manager.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_requested && _manager.Availability == WingetAvailability.Available && _manager.AgreementsAccepted)
        {
            _requested = true;
            _ = _manager.LoadSourcesAsync();
        }

        Build();
    });

    private void Build()
    {
        var state = (_manager.Sources, _manager.Availability, _manager.AgreementsAccepted, _manager.Lane.IsRunning);
        if (Content is not null && _shown.Equals(state))
        {
            return;
        }

        _shown = state;
        var stack = VStack(6, Caption(L.Get("win.packageManager.sourcesCaption")));
        foreach (var source in _manager.Sources)
        {
            stack.Children.Add(Columns("Auto,Auto,*", 8, Icon("Database", 14), Text(source.Name, 12, FontWeight.SemiBold, wrap: false), PathText(source.Argument)));
        }

        var update = Button(L.Get("win.packageManager.updateSources"), () => _ = UpdateAsync(), "ArrowSync");
        update.IsEnabled = _manager.Availability == WingetAvailability.Available && _manager.AgreementsAccepted && !_manager.Lane.IsRunning;
        stack.Children.Add(update);
        Content = stack;
    }

    private async Task UpdateAsync()
    {
        if (await ConfirmAsync(this, L.Get("win.packageManager.confirmSourcesTitle"), L.Get("win.packageManager.confirmSourcesMessage"), L.Get("win.packageManager.updateSources")))
        {
            await _manager.RunAsync(PackageOperationKind.UpdateSources, null);
        }
    }
}
