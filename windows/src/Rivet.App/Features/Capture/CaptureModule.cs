// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.ColorPicking;
using Rivet.App.Features.Capture.Output;
using Rivet.App.Features.Capture.Pins;
using Rivet.App.Features.Capture.Preview;
using Rivet.App.Features.Capture.Recent;
using Rivet.App.Features.Capture.Scrolling;
using Rivet.App.Features.Capture.Settings;
using Rivet.App.Features.Capture.Text;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Util;

namespace Rivet.App.Features.Capture;

/// <summary>
/// The capture tools: screenshots (selector, outputs, quick preview, pins,
/// recent captures, scrolling capture), Copy text from screen and the colour
/// picker. Owns the shared selector (<see cref="ICaptureSelector"/>), the
/// recent-captures history (<see cref="IRecentCaptures"/>) and the save/copy
/// service (<see cref="ICaptureOutput"/>).
/// </summary>
public sealed class CaptureModule : IFeatureModule
{
    // Action ids are "<feature>.<verb>"; built from parts so they are not mistaken for string keys.
    public const string CaptureActionId = ScreenshotFeature + ".capture";
    public const string FullScreenActionId = ScreenshotFeature + ".fullScreen";
    public const string ScrollingActionId = ScreenshotFeature + ".scrolling";
    public const string EditLatestActionId = ScreenshotFeature + ".editLatest";
    public const string EditClipboardActionId = ScreenshotFeature + ".editClipboard";
    public const string ClosePinsActionId = ScreenshotFeature + ".closePins";
    public const string RecentActionId = RecentCaptureActions.ShowPalette;
    public const string OcrActionId = "screenOCR.capture";
    public const string ColorActionId = "colorPicker.pick";

    public const string ScreenshotRoleId = "screenshot";
    public const string FullScreenRoleId = "screenshotFullScreen";
    public const string LastCaptureRoleId = "screenshotLastCapture";
    public const string ClipboardRoleId = "screenshotClipboard";
    public const string RecentCapturesRoleId = "recentCaptures";
    public const string PrintScreenRoleId = "screenshotPrintScreen";
    public const string OcrRoleId = "screenOCR";
    public const string ColorRoleId = "colorPicker";

    public const string ScreenshotPageId = "screenshot";
    public const string OcrPageId = "screenOCR";
    public const string ColorPageId = "colorPicker";

    private const string ScreenshotFeature = FeatureIds.Screenshot;

    private const KeyModifiers Hyper = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win;

    public string Id => "capture";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<CaptureOutputService>();
        services.AddSingleton<ICaptureOutput>(sp => sp.GetRequiredService<CaptureOutputService>());
        services.AddSingleton<RecentCapturesStore>();
        services.AddSingleton<IRecentCaptures>(sp => sp.GetRequiredService<RecentCapturesStore>());
        services.AddSingleton<IRecorderLink, RecorderLink>();
        services.AddSingleton<CaptureCoordinator>();
        services.AddSingleton<ICaptureSelector>(sp => sp.GetRequiredService<CaptureCoordinator>());
        services.AddSingleton<LatestCaptureStore>();
        services.AddSingleton<CaptureRouter>();
        services.AddSingleton<QuickPreviewController>();
        services.AddSingleton<PinManager>();
        services.AddSingleton<ScreenTextService>();
        services.AddSingleton<ColorPickService>();
        services.AddSingleton<ScrollingCaptureController>();
        services.AddSingleton<RecentCapturesActions>();
        services.AddSingleton<CaptureEditActions>();
    }

    public void Initialize(ModuleContext context)
    {
        var services = context.Services;
        var coordinator = context.Get<CaptureCoordinator>();
        RegisterActions(context, coordinator);
        RegisterShortcuts(context);
        RegisterPanel(context);
        RegisterSettingsPages(context);
        RegisterControllers(context, coordinator);

        // The latest-capture store exists only while "Edit latest screenshot" is on.
        context.Settings.Observe(CaptureSettings.LastCaptureShortcutEnabled.Key, () =>
        {
            if (!context.Settings.Get(CaptureSettings.LastCaptureShortcutEnabled))
            {
                services.GetRequiredService<LatestCaptureStore>().Clear();
            }
        });

        _ = Task.Run(CaptureTempFiles.CleanLeftovers);
    }

    private static void RegisterActions(ModuleContext context, CaptureCoordinator coordinator)
    {
        var services = context.Services;
        context.Actions.Register(new AppAction
        {
            Id = CaptureActionId, FeatureId = FeatureIds.Screenshot, TitleKey = "screenshot.pageTitle", SubtitleKey = "screenshot.panelCaption",
            Icon = "Screenshot", Keywords = ["screenshot", "capture", "snip", "Print Screen"],
            Run = ctx => Fire(() => coordinator.StartAsync(CaptureTool.Screenshot, ctx.Source)),
        });
        context.Actions.Register(new AppAction
        {
            Id = FullScreenActionId, FeatureId = FeatureIds.Screenshot, TitleKey = "screenshot.fullScreenShortcutTitle",
            Icon = "FullScreenMaximize", Keywords = ["screenshot", "full screen"],
            Run = _ => Fire(coordinator.FullScreenAsync),
        });
        context.Actions.Register(new AppAction
        {
            Id = ScrollingActionId, FeatureId = FeatureIds.Screenshot, TitleKey = "screenshot.scrollingCaptureTitle", SubtitleKey = "screenshot.scrollingCaptureCaption",
            Icon = "ArrowSortDownLines", Keywords = ["screenshot", "scrolling", "long"],
            Run = _ => Fire(coordinator.ScrollingAsync),
        });
        context.Actions.Register(new AppAction
        {
            Id = EditLatestActionId, FeatureId = FeatureIds.Screenshot, TitleKey = "screenshot.editLastCapture", Icon = "ImageEdit",
            Run = _ => Fire(services.GetRequiredService<CaptureEditActions>().EditLatestAsync),
        });
        context.Actions.Register(new AppAction
        {
            Id = EditClipboardActionId, FeatureId = FeatureIds.Screenshot, TitleKey = "screenshot.editClipboardImage", Icon = "ClipboardImage",
            Run = _ => Fire(services.GetRequiredService<CaptureEditActions>().EditClipboardAsync),
        });
        context.Actions.Register(new AppAction
        {
            Id = RecentActionId, FeatureId = FeatureIds.Screenshot, TitleKey = "recentCaptures.title", Icon = "History",
            Keywords = ["history", "recent"],
            Run = _ =>
            {
                services.GetRequiredService<RecentCapturesActions>().ShowPalette();
                return Task.CompletedTask;
            },
        });
        context.Actions.Register(new AppAction
        {
            Id = ClosePinsActionId, FeatureId = FeatureIds.Screenshot, TitleKey = "screenshot.pinCloseAll", Icon = "PinOff", ClosesPanel = false,
            Run = _ =>
            {
                services.GetRequiredService<PinManager>().CloseAll();
                return Task.CompletedTask;
            },
        });
        context.Actions.Register(new AppAction
        {
            Id = OcrActionId, FeatureId = FeatureIds.ScreenOcr, TitleKey = "Strings.ocrName", SubtitleKey = "Strings.ocrCaption",
            Icon = "ScanText", Keywords = ["OCR", "QR", "text", "copy text"],
            Run = ctx => Fire(() => coordinator.StartAsync(CaptureTool.Text, ctx.Source)),
        });
        context.Actions.Register(new AppAction
        {
            Id = ColorActionId, FeatureId = FeatureIds.ColorPicker, TitleKey = "Strings.colorPickerName", SubtitleKey = "Strings.colorPickerCaption",
            Icon = "Eyedropper", Keywords = ["color", "colour", "eyedropper", "HEX", "RGB"],
            Run = ctx => Fire(() => coordinator.StartAsync(CaptureTool.Color, ctx.Source)),
        });
        context.Actions.Register(new AppAction
        {
            Id = ScreenTextService.OpenLanguageSettingsActionId, FeatureId = FeatureIds.ScreenOcr, TitleKey = "win.capture.openLanguageSettings",
            Icon = "LocalLanguage", ClosesPanel = false,
            Run = _ =>
            {
                services.GetRequiredService<ScreenTextService>().OpenLanguageSettings();
                return Task.CompletedTask;
            },
        });
    }

    private static void RegisterShortcuts(ModuleContext context)
    {
        ShortcutRole Role(string id, string featureId, string titleKey, Rivet.Core.Settings.Setting<string> storage, KeyChord chord, Rivet.Core.Settings.Setting<bool> enabled, string actionId, HotkeyOptions options = HotkeyOptions.None) => new()
        {
            Id = id,
            FeatureId = featureId,
            TitleKey = titleKey,
            Storage = storage,
            Default = chord,
            RequiredEnableKeys = [enabled],
            ActionId = actionId,
            Options = options,
            GroupId = "screenCapture",
        };

        var shortcuts = context.Shortcuts;
        shortcuts.Register(Role(ScreenshotRoleId, FeatureIds.Screenshot, "screenshot.pageTitle", CaptureSettings.ScreenshotShortcut, KeyChord.Of(Hyper, VirtualKeys.Digit(4)), CaptureSettings.ScreenshotShortcutEnabled, CaptureActionId));
        shortcuts.Register(Role(FullScreenRoleId, FeatureIds.Screenshot, "screenshot.fullScreenShortcutTitle", CaptureSettings.FullScreenShortcut, KeyChord.Of(Hyper, VirtualKeys.Digit(3)), CaptureSettings.FullScreenShortcutEnabled, FullScreenActionId));
        shortcuts.Register(Role(LastCaptureRoleId, FeatureIds.Screenshot, "screenshot.editLastCapture", CaptureSettings.LastCaptureShortcut, KeyChord.Of(Hyper, VirtualKeys.Letter('E')), CaptureSettings.LastCaptureShortcutEnabled, EditLatestActionId));
        shortcuts.Register(Role(ClipboardRoleId, FeatureIds.Screenshot, "screenshot.editClipboardImage", CaptureSettings.ClipboardShortcut, KeyChord.Of(Hyper, VirtualKeys.Letter('P')), CaptureSettings.ClipboardShortcutEnabled, EditClipboardActionId));
        shortcuts.Register(Role(RecentCapturesRoleId, FeatureIds.Screenshot, "recentCaptures.title", CaptureSettings.RecentCapturesShortcut, KeyChord.Of(Hyper, VirtualKeys.Letter('H')), CaptureSettings.RecentCapturesShortcutEnabled, RecentActionId));
        shortcuts.Register(Role(OcrRoleId, FeatureIds.ScreenOcr, "Strings.ocrName", CaptureSettings.ScreenOcrShortcut, KeyChord.Of(Hyper, VirtualKeys.Letter('T')), CaptureSettings.ScreenOcrShortcutEnabled, OcrActionId));
        shortcuts.Register(Role(ColorRoleId, FeatureIds.ColorPicker, "Strings.colorPickerName", CaptureSettings.ColorPickerShortcut, KeyChord.Of(Hyper, VirtualKeys.Letter('C')), CaptureSettings.ColorPickerShortcutEnabled, ColorActionId));

        // Print Screen belongs to the Snipping Tool on Windows 11: take it over with the shared hook, only when asked.
        shortcuts.Register(Role(PrintScreenRoleId, FeatureIds.Screenshot, "win.capture.printScreenRole", CaptureSettings.PrintScreenShortcut, KeyChord.Of(KeyModifiers.None, VirtualKeys.Snapshot), CaptureSettings.PrintScreenEnabled, CaptureActionId, HotkeyOptions.OverrideSystem));
    }

    private static void RegisterPanel(ModuleContext context)
    {
        var panel = context.Panel;
        panel.AddTile(new PanelTileDescriptor
        {
            Id = "screenshot", FeatureId = FeatureIds.Screenshot, TitleKey = "screenshot.pageTitle", CaptionKey = "screenshot.panelCaption",
            Icon = "Screenshot", Order = 0, ActionId = CaptureActionId, ShortcutRoleId = ScreenshotRoleId, SettingsPageId = ScreenshotPageId,
            // As on macOS: a "Recent captures" button under the row shows the history inside the panel.
            Accessory = new PanelTileAccessory
            {
                TitleKey = "recentCaptures.title", Icon = "History",
                CreateHostedView = sp => new RecentCapturesView(sp, showHeader: false),
            },
        });
        panel.AddTile(new PanelTileDescriptor
        {
            Id = "screenOCR", FeatureId = FeatureIds.ScreenOcr, TitleKey = "Strings.ocrName", CaptionKey = "Strings.ocrCaption",
            Icon = "ScanText", Order = 110, ActionId = OcrActionId, ShortcutRoleId = OcrRoleId, SettingsPageId = OcrPageId,
        });
        panel.AddTile(new PanelTileDescriptor
        {
            Id = "colorPicker", FeatureId = FeatureIds.ColorPicker, TitleKey = "Strings.colorPickerName", CaptionKey = "Strings.colorPickerCaption",
            Icon = "Eyedropper", Order = 120, ActionId = ColorActionId, ShortcutRoleId = ColorRoleId, SettingsPageId = ColorPageId,
        });
    }

    private static void RegisterSettingsPages(ModuleContext context)
    {
        var pages = context.SettingsPages;
        pages.Add(new SettingsPageDescriptor
        {
            Id = ScreenshotPageId, TitleKey = "screenshot.pageTitle", Icon = "Screenshot", Category = SettingsCategory.Capture, Order = 0,
            FeatureIds = [FeatureIds.Screenshot],
            PrimaryFor = [FeatureIds.Screenshot],
            CreateView = sp => new ScreenshotSettingsPage(sp),
            KeywordKeys = ["screenshot.freezeToggle", "screenshot.defaultActionLabel", "screenshot.folderLabel", "screenshot.fileNamePatternLabel",
                "screenshot.scrollingCaptureButton", "screenshot.previewPositionLabel", "recentCaptures.title", "screenshot.loupeDefaultZoomLabel"],
            Keywords = ["screenshot", "capture", "Print Screen", "PrtScn", "snip", "magnifier", "loupe", "pin"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = OcrPageId, TitleKey = "Strings.ocrName", Icon = "ScanText", Category = SettingsCategory.Capture, Order = 20,
            FeatureIds = [FeatureIds.ScreenOcr],
            PrimaryFor = [FeatureIds.ScreenOcr],
            CreateView = sp => new ScreenOcrSettingsPage(sp),
            KeywordKeys = ["Strings.ocrRemoveLineBreaksToggle", "Strings.ocrQRToggle"],
            Keywords = ["OCR", "QR", "text recognition"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = ColorPageId, TitleKey = "Strings.colorPickerName", Icon = "Eyedropper", Category = SettingsCategory.Capture, Order = 30,
            FeatureIds = [FeatureIds.ColorPicker],
            PrimaryFor = [FeatureIds.ColorPicker],
            CreateView = sp => new ColorPickerSettingsPage(sp),
            KeywordKeys = ["Strings.colorPickerFormatLabel", "Strings.colorPickerBareHexToggle"],
            Keywords = ["color", "colour", "eyedropper", "HEX", "RGB", "HSL"],
        });
    }

    private static void RegisterControllers(ModuleContext context, CaptureCoordinator coordinator)
    {
        var services = context.Services;
        var runtime = context.Features;

        // Uninstalling Screenshot ends everything it owns: countdown, chooser, scrolling, preview, pins.
        runtime.RegisterController(FeatureIds.Screenshot, new DelegateFeatureController(available =>
        {
            if (available)
            {
                return;
            }

            coordinator.CancelAll();
            services.GetRequiredService<QuickPreviewController>().Close();
            services.GetRequiredService<PinManager>().CloseAll();
            services.GetRequiredService<RecentCapturesActions>().ClosePalette();
            services.GetRequiredService<LatestCaptureStore>().Clear();
        }));

        // A change in the set of installed tools cancels an open chooser or countdown (§3.4.12).
        foreach (var feature in new[] { FeatureIds.Screenshot, FeatureIds.ScreenRecorder, FeatureIds.ScreenOcr, FeatureIds.ColorPicker })
        {
            bool? last = null;
            runtime.RegisterController(feature, new DelegateFeatureController(available =>
            {
                if (last is { } previous && previous != available && coordinator.IsActive)
                {
                    coordinator.CancelAll();
                }

                last = available;
            }));
        }
    }

    /// <summary>Starts a long flow without blocking the caller (hotkeys, tiles); failures are logged.</summary>
    private static Task Fire(Func<Task> flow)
    {
        UiThread.Post(async () =>
        {
            try
            {
                await flow();
            }
            catch (Exception ex)
            {
                Log.Error("capture", "A capture action failed.", ex);
            }
        });
        return Task.CompletedTask;
    }
}
