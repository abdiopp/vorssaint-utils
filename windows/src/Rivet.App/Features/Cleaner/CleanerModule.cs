// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Cleaner;

/// <summary>The Cleaner: scan, review, clean to the Recycle Bin; automatic cleanup; WhatsApp downloads review.</summary>
public sealed class CleanerModule : IFeatureModule
{
    public const string PageId = "cleaner";

    public string Id => "cleaner";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new CleanerService(
            sp.GetRequiredService<ICleanerFileSystem>(),
            sp.GetRequiredService<ICleanerPlatform>(),
            sp.GetRequiredService<IRecycler>(),
            sp.GetRequiredService<IKnownFolders>(),
            sp.GetRequiredService<ISettingsStore>(),
            () => BackupFolder(sp.GetRequiredService<AppPaths>(), "Cleaner")));
        services.AddSingleton(sp => new CleanerScheduler(
            sp.GetRequiredService<CleanerService>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<INotificationService>(),
            sp.GetService<ISystemClockEvents>()));
        services.AddSingleton<WhatsAppDownloadsService>();
    }

    public void Initialize(ModuleContext context)
    {
        var shell = context.Get<IAppShell>();
        context.Features.RegisterController(FeatureIds.Cleaner, context.Get<CleanerScheduler>());

        context.Actions.Register(new AppAction
        {
            Id = CleanerActions.Open, FeatureId = FeatureIds.Cleaner, TitleKey = "Strings.cleanerName", Icon = "Sparkle",
            Run = _ =>
            {
                shell.OpenSettings(PageId);
                return Task.CompletedTask;
            },
        });
        context.Actions.Register(new AppAction
        {
            Id = CleanerActions.Scan, FeatureId = FeatureIds.Cleaner, TitleKey = "Strings.cleanerScan", Icon = "Sparkle",
            Keywords = ["clean", "temp", "cache", "junk"],
            Run = _ =>
            {
                shell.OpenSettings(PageId);
                return context.Get<CleanerService>().ScanAsync(attended: true);
            },
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "cleaner", FeatureId = FeatureIds.Cleaner, TitleKey = "Strings.cleanerName", CaptionKey = "Strings.cleanerPanelCaption",
            Icon = "Sparkle", Order = 30, SettingsPageId = PageId,
            CreateHostedView = sp => new CleanerView(sp, compact: true),
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId, TitleKey = "Strings.cleanerName", Icon = "Sparkle", Category = SettingsCategory.AppManagement, Order = 0,
            FeatureIds = [FeatureIds.Cleaner], CreateView = sp => new CleanerSettingsPage(sp),
            KeywordKeys = ["Strings.cleanerScheduleTitle", "Strings.cleanerCatCaches", "Strings.cleanerCatTrash", "win.cleaner.whatsAppTitle", "Strings.cleanerCatScreenshots"],
            Keywords = ["temp", "cache", "junk", "recycle", "WhatsApp"],
        });
    }

    /// <summary>A fresh, timestamped folder for .reg backups of removed registry entries.</summary>
    internal static string BackupFolder(AppPaths paths, string feature) =>
        AppPaths.EnsureDirectory(Path.Combine(paths.LocalFolder("Backups"), feature, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
}

/// <summary>Settings › Cleaner.</summary>
public sealed class CleanerSettingsPage : SettingsPage
{
    public CleanerSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var shell = services.GetRequiredService<IShellService>();
        Content = Stack(
            Header("Strings.cleanerName", "win.shell.cleanerDescription"),
            CardText(null, new CleanerView(services, compact: false)),
            CardText(null, new WhatsAppDownloadsView(services)),
            CardText(null,
                Row("Settings", L.Get("win.cleaner.windowsCleanupTitle"), L.Get("win.cleaner.windowsCleanupNote"),
                    ActionButton(L.Get("win.cleaner.openStorageSettings"), () => shell.OpenSystemSettings("ms-settings:storagesense")))));
    }
}
