// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Clipboard;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Clipboard;

/// <summary>
/// Clipboard history, auto clear, paste as plain text and Clean URL (spec 06
/// §3.1–§3.5), plus the shared clipboard lane and caret helpers the snippet
/// and Command Bar modules also use.
/// </summary>
public sealed class ClipboardModule : IFeatureModule
{
    public const string ClipboardPageId = "clipboard";
    public const string UrlCleanerPageId = "urlCleaner";

    public string Id => "clipboard";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ClipboardLane>();
        services.AddSingleton<ClipboardWatcher>();
        services.AddSingleton<ClipboardHistoryService>();
        services.AddSingleton<ClipboardAutoClearService>();
        services.AddSingleton<UrlCleanerService>();
        services.AddSingleton<SyntheticInput>();
        services.AddSingleton<TransientPaste>();
        services.AddSingleton<TextInserter>();
        services.AddSingleton<CaretActions>();
        services.AddSingleton<PastePlainService>();
        services.AddSingleton<ClipboardWindowHost>();
    }

    public void Initialize(ModuleContext context)
    {
        var features = context.Features;
        features.RegisterController(FeatureIds.ClipboardHistory, context.Get<ClipboardHistoryService>());
        features.RegisterController(FeatureIds.ClipboardHistory, context.Get<ClipboardAutoClearService>());
        features.RegisterController(FeatureIds.UrlCleaner, context.Get<UrlCleanerService>());

        var actions = context.Actions;
        var services = context.Services;
        actions.Register(new AppAction
        {
            Id = "clipboardHistory.show",
            FeatureId = FeatureIds.ClipboardHistory,
            TitleKey = "clipboard.title",
            Icon = "ClipboardPaste",
            Keywords = ["clipboard", "history", "paste"],
            Run = _ =>
            {
                Dispatcher.UIThread.Post(() => services.GetRequiredService<ClipboardWindowHost>().Toggle());
                return Task.CompletedTask;
            },
        });
        actions.Register(new AppAction
        {
            Id = "pastePlain.paste",
            FeatureId = FeatureIds.PastePlain,
            TitleKey = "Strings.pastePlainName",
            Icon = "DocumentText",
            Keywords = ["paste", "plain", "text"],
            Run = _ => services.GetRequiredService<PastePlainService>().PasteAsync(),
        });
        actions.Register(new AppAction
        {
            Id = "urlCleaner.cleanClipboard",
            FeatureId = FeatureIds.UrlCleaner,
            TitleKey = "commandBar.actionCleanURL",
            Icon = "LinkDismiss",
            Keywords = ["Clean URL", "link", "tracking"],
            ClosesPanel = false,
            Run = async _ =>
            {
                var message = await services.GetRequiredService<UrlCleanerService>().CleanClipboardAsync().ConfigureAwait(true);
                services.GetRequiredService<IHud>().Show(message, HudStyle.Info, "Link");
            },
        });

        var shortcuts = context.Shortcuts;
        shortcuts.Register(new ShortcutRole
        {
            Id = "clipboard",
            FeatureId = FeatureIds.ClipboardHistory,
            TitleKey = "clipboard.title",
            Storage = ClipboardSettings.Shortcut,
            Default = ClipboardSettings.DefaultShortcut,
            RequiredEnableKeys = [ClipboardSettings.Enabled, ClipboardSettings.ShortcutEnabled],
            ActionId = "clipboardHistory.show",
        });
        shortcuts.Register(new ShortcutRole
        {
            Id = "pastePlain",
            FeatureId = FeatureIds.PastePlain,
            TitleKey = "Strings.pastePlainName",
            Storage = ClipboardSettings.PastePlainShortcut,
            Default = ClipboardSettings.DefaultPastePlainShortcut,
            RequiredEnableKeys = [ClipboardSettings.PastePlainEnabled],
            ActionId = "pastePlain.paste",
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "clipboard",
            FeatureId = FeatureIds.ClipboardHistory,
            TitleKey = "clipboard.title",
            CaptionKey = "clipboard.caption",
            Icon = "ClipboardPaste",
            Order = 60,
            ShortcutRoleId = "clipboard",
            SettingsPageId = ClipboardPageId,
            CreateHostedView = sp => new ClipboardPanelView(sp),
            LiveCaption = () => context.Settings.Get(ClipboardSettings.Enabled) ? null : L.Get("clipboard.disabled"),
        });
        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "cleanURL",
            FeatureId = FeatureIds.UrlCleaner,
            TitleKey = "Strings.urlCleanerName",
            CaptionKey = "hub.descURLCleaner",
            Icon = "Link",
            Order = 90,
            SettingsPageId = UrlCleanerPageId,
            CreateHostedView = sp => new UrlCleanerView(sp, showAutomaticSwitch: true),
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = ClipboardPageId,
            TitleKey = "clipboard.title",
            Icon = "ClipboardPaste",
            Category = SettingsCategory.ClipboardFiles,
            Order = 0,
            FeatureIds = [FeatureIds.ClipboardHistory, FeatureIds.PastePlain],
            CreateView = sp => new ClipboardSettingsPage(sp),
            KeywordKeys = ["clipboard.enable", "clipboard.autoClearEnable", "Strings.pastePlainName", "clipboardIgnoredApps.listTitle", "clipboard.skipSensitive"],
            Keywords = ["clipboard", "history", "paste", "plain"],
        });
        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = UrlCleanerPageId,
            TitleKey = "Strings.urlCleanerName",
            Icon = "Link",
            Category = SettingsCategory.ClipboardFiles,
            Order = 2,
            FeatureIds = [FeatureIds.UrlCleaner],
            CreateView = sp => new UrlCleanerSettingsPage(sp),
            KeywordKeys = ["Strings.urlCleanerEnable", "Strings.urlCleanerRulesTitle"],
            Keywords = ["utm", "tracking", "url", "link"],
        });
    }
}

/// <summary>Owns the clipboard history window (created on first use, on the UI thread).</summary>
public sealed class ClipboardWindowHost(IServiceProvider services)
{
    private ClipboardHistoryWindow? _window;

    public ClipboardHistoryWindow Window => _window ??= new ClipboardHistoryWindow(services);

    public void Toggle() => Window.Open();
}
