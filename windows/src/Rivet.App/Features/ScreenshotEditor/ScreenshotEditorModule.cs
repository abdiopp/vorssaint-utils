// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.ScreenshotEditor;
using Rivet.Core.Settings;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The screenshot editor (feature "screenshot", editor part): implements
/// <see cref="IScreenshotEditor"/> and the "Screenshot editor" Settings page.
/// "Edit latest screenshot" and "Edit clipboard image" belong to the capture
/// module, which owns the latest-capture store and the capture clipboard.
/// </summary>
public sealed class ScreenshotEditorModule : IFeatureModule
{
    public const string PageId = "screenshotEditor";

    public string Id => "screenshotEditor";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ScreenshotEditorService>();
        services.AddSingleton<IScreenshotEditor>(sp => sp.GetRequiredService<ScreenshotEditorService>());
    }

    public void Initialize(ModuleContext context)
    {
        var editor = context.Get<ScreenshotEditorService>();
        context.Features.RegisterController(FeatureIds.Screenshot, editor);
        RegisterBackupSanitizers();

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "win.screenshotEditor.pageTitle",
            Icon = "ImageEdit",
            Category = SettingsCategory.Capture,
            Order = 10,
            // The capture module's page is PrimaryFor Screenshot, so the Features hub opens that one.
            FeatureIds = [FeatureIds.Screenshot],
            CreateView = sp => new EditorSettingsPage(sp),
            KeywordKeys = ["screenshot.toolShortcutsTitle", "screenshot.toolShortcutsToggle", "screenshot.toolArrow", "screenshot.toolBlur", "screenshot.toolCrop", "screenshot.shadowLabel"],
            Keywords = ["editor", "annotate", "markup", "tools"],
        });

        _ = Task.Run(ScreenshotEditorService.CleanUpAtLaunch);
    }

    /// <summary>
    /// Image watermarks and image backgrounds point at files on this PC, so
    /// backups leave them out: text watermarks and colour looks travel.
    /// </summary>
    private static void RegisterBackupSanitizers()
    {
        static System.Text.Json.Nodes.JsonNode? AsString(string value) => System.Text.Json.Nodes.JsonValue.Create(value);
        static string? Read(System.Text.Json.Nodes.JsonNode? node) =>
            node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        Rivet.Core.Settings.SettingsBackup.RegisterExportSanitizer(EditorSettings.WatermarkStyleJson.Key, node =>
        {
            var style = WatermarkCodec.Decode(Read(node));
            return AsString(style.Kind == WatermarkKind.Image ? WatermarkCodec.Encode(WatermarkStyle.None) : WatermarkCodec.Encode(style));
        });
        Rivet.Core.Settings.SettingsBackup.RegisterExportSanitizer(EditorSettings.WatermarkPresetsJson.Key, node =>
            AsString(WatermarkCodec.EncodePresets(WatermarkCodec.DecodePresets(Read(node)).Where(p => p.Kind != WatermarkKind.Image))));
        Rivet.Core.Settings.SettingsBackup.RegisterExportSanitizer(EditorSettings.BackdropStyleJson.Key, node =>
        {
            var style = Rivet.Imaging.Backdrop.BackdropCodec.Decode(Read(node));
            return AsString(Rivet.Imaging.Backdrop.BackdropCodec.Encode(style.Kind == Rivet.Imaging.Backdrop.BackdropKind.Image
                ? new Rivet.Imaging.Backdrop.BackdropStyle { Padding = style.Padding, CornerRadius = style.CornerRadius, Blur = style.Blur }
                : style));
        });
        Rivet.Core.Settings.SettingsBackup.RegisterExportSanitizer(EditorSettings.BackdropPresetsJson.Key, node =>
            AsString(Rivet.Imaging.Backdrop.BackdropCodec.EncodePresets(
                Rivet.Imaging.Backdrop.BackdropCodec.DecodePresets(Read(node)).Where(p => p.Kind != Rivet.Imaging.Backdrop.BackdropKind.Image))));
    }
}

/// <summary>The "Screenshot editor" Settings page: tool order and tool shortcuts, plus annotation shadows.</summary>
internal sealed class EditorSettingsPage : SettingsPage
{
    public EditorSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        Content = Stack(
            Header("win.screenshotEditor.pageTitle", "win.screenshotEditor.pageCaption"),
            Card("screenshot.toolShortcutsTitle", new ToolOrderEditor(services, compact: false)),
            Card(null, Toggle(EditorSettings.AnnotationShadows, "SquareShadow", "screenshot.shadowLabel", "win.screenshotEditor.shadowsCaption")));
    }
}
