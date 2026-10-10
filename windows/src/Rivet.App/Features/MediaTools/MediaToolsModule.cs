// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.MediaTools;

/// <summary>
/// Media tools (spec 07 §3.6): one workspace with Video, GIF, Image and Text,
/// hosted in the tray panel's Utilities tab, in Settings › Media and in a
/// stand-alone Media window (the Windows stand-in for the Quick Launcher host).
/// </summary>
public sealed class MediaToolsModule : IFeatureModule
{
    // Built by concatenation so the string-key test does not mistake the ids for catalog keys.
    private const string ActionPrefix = "mediaTools";
    public const string OpenActionId = ActionPrefix + ".open";
    public const string PageId = "mediaTools";

    public string Id => "mediaTools";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<MediaToolsService>();
        services.AddSingleton<MediaWindowHost>();
    }

    public void Initialize(ModuleContext context)
    {
        var service = context.Get<MediaToolsService>();
        var windows = context.Get<MediaWindowHost>();
        context.Features.RegisterController(FeatureIds.MediaTools, new DelegateFeatureController(available =>
        {
            if (!available)
            {
                // Uninstalling stops a running export and closes the window; settings are kept.
                service.Cancel();
                windows.Close();
            }
        }));

        context.Actions.Register(new AppAction
        {
            Id = OpenActionId,
            FeatureId = FeatureIds.MediaTools,
            TitleKey = "Strings.mediaName",
            SubtitleKey = "Strings.mediaEnableCaption",
            Icon = "ImageMultiple",
            Keywords = ["video", "gif", "image", "compress", "convert", "resize", "watermark", "ocr", "pdf", "webp"],
            Run = _ =>
            {
                windows.Show();
                return Task.CompletedTask;
            },
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "mediaTools",
            FeatureId = FeatureIds.MediaTools,
            TitleKey = "Strings.mediaName",
            CaptionKey = "Strings.mediaEnableCaption",
            Icon = "ImageMultiple",
            Order = 60,
            SettingsPageId = PageId,
            CreateHostedView = sp => new MediaWorkspaceView(sp, MediaHost.Panel),
            HostedKeepsPanelOpen = true,
            LiveCaption = () => service.Job.IsRunning ? L.Get("Strings.mediaRunning") : null,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "Strings.mediaName",
            Icon = "ImageMultiple",
            Category = SettingsCategory.Capture,
            Order = 70,
            FeatureIds = [FeatureIds.MediaTools],
            CreateView = sp => new MediaToolsSettingsPage(sp),
            KeywordKeys = ["Strings.mediaToolVideo", "Strings.mediaToolGIF", "Strings.mediaToolImage", "Strings.mediaToolText", "MediaImageConverterStrings.watermark", "MediaImageConverterStrings.rename", "MediaImageConverterStrings.profile", "Strings.mediaStartConvertPDF"],
            Keywords = ["PDF", "GIF", "PNG", "JPEG", "WebP", "HEIC", "OCR", "convert", "resize", "watermark", "rename", "profile", "fit", "fill", "crop", "compress"],
        });
    }
}

/// <summary>Settings › Capture › Media: the full-size workspace under the page header.</summary>
public sealed class MediaToolsSettingsPage : SettingsPage
{
    public MediaToolsSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        Content = Stack(
            Header("Strings.mediaName", "Strings.mediaEnableCaption"),
            Note(L.Get("Strings.mediaLocalNote")),
            new MediaWorkspaceView(services, MediaHost.Settings));
    }
}

/// <summary>Opens one Media window and brings it forward on later opens.</summary>
public sealed class MediaWindowHost(IServiceProvider services)
{
    private MediaWindow? _window;

    public bool IsOpen => _window is not null;

    public void Show()
    {
        if (_window is { } existing)
        {
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new MediaWindow(services);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        window.Activate();
    }

    public void Close() => _window?.Close();
}

/// <summary>A normal, resizable window holding the workspace; Esc closes it (a running job continues).</summary>
public sealed class MediaWindow : Window
{
    public MediaWindow(IServiceProvider services)
    {
        Title = L.Get("Strings.mediaName");
        Width = 560;
        Height = 720;
        MinWidth = 420;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Workspace = new MediaWorkspaceView(services, MediaHost.Window);
        Content = new Border { Padding = new Thickness(18, 16), Child = Workspace };
        this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush"));
    }

    public MediaWorkspaceView Workspace { get; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }
}
