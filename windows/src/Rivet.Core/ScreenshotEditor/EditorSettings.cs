// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.ScreenshotEditor;

/// <summary>
/// Settings owned by the screenshot editor (spec 01 §4.1). Keys and stored
/// values match the macOS app. Backdrop and watermark styles are JSON strings
/// (as on macOS); <c>screenshotBackdropPresets</c> is shared with the recorder.
/// </summary>
public static class EditorSettings
{
    public static readonly Setting<string> ToolOrderCsv =
        new("screenshotToolOrder", ToolOrder.DefaultCsv, v => ToolOrder.Format(ToolOrder.Parse(v)));

    public static readonly Setting<string> ToolShortcutsCsv =
        new("screenshotToolShortcuts", string.Empty, v => ToolBindings.Parse(v).Format());

    public static readonly Setting<bool> ToolShortcutsEnabled = new("screenshotToolShortcutsEnabled", true);

    /// <summary>Select and Crop are never restored; the fallback is Arrow.</summary>
    public static readonly Setting<string> LastTool =
        new("screenshotLastTool", "arrow", v => EditorTools.TryParse(v, out var t) && EditorTools.IsCreation(t) ? v : "arrow");

    public static readonly Setting<string> LastColor =
        new("screenshotLastColor", "red", Sanitize.OneOfStrings("red", AnnotationColors.All.Select(AnnotationColors.Id).ToArray()));

    public static readonly Setting<string> LastStroke =
        new("screenshotLastStroke", "medium", Sanitize.OneOfStrings("medium", "small", "medium", "large"));

    public static readonly Setting<int> LastTextSize = new("screenshotLastTextSize", TextSizes.Default, TextSizes.Sanitize);

    public static readonly Setting<int> LastBlurLevel =
        new("screenshotLastBlurLevel", BlurStyles.DefaultLevel, Sanitize.Clamp(BlurStyles.MinLevel, BlurStyles.MaxLevel));

    public static readonly Setting<string> LastBlurStyle =
        new("screenshotLastBlurStyle", "pixelate", Sanitize.OneOfStrings("pixelate", "pixelate", "blur", "erase"));

    public static readonly Setting<bool> LastBlurTextOnly = new("screenshotLastBlurTextOnly", false);

    public static readonly Setting<string> LastArrowStyle =
        new("screenshotLastArrowStyle", "filled", Sanitize.OneOfStrings("filled", ArrowStyles.All.Select(ArrowStyles.Id).ToArray()));

    public static readonly Setting<string> LastSticker =
        new("screenshotLastSticker", "check", Sanitize.OneOfStrings("check", Stickers.All.Select(Stickers.Id).ToArray()));

    public static readonly Setting<bool> AnnotationShadows = new("screenshotAnnotationShadows", false);

    /// <summary>JSON BackdropStyle; empty means none.</summary>
    public static readonly Setting<string> BackdropStyleJson = new("screenshotBackdropStyle", string.Empty);

    /// <summary>JSON array of up to 12 BackdropStyle values (shared with the recorder).</summary>
    public static readonly Setting<string> BackdropPresetsJson = new("screenshotBackdropPresets", "[]");

    /// <summary>JSON WatermarkStyle; empty means none.</summary>
    public static readonly Setting<string> WatermarkStyleJson = new("screenshotWatermarkStyle", string.Empty);

    /// <summary>JSON array of up to 12 WatermarkStyle values.</summary>
    public static readonly Setting<string> WatermarkPresetsJson = new("screenshotWatermarkPresets", "[]");

    /// <summary>"Save at 1x size" — owned by the capture settings page; the editor's exports honour it.</summary>
    public static readonly Setting<bool> Downscale = new("screenshotDownscale", false);
}

/// <summary>The style choices the next new mark takes, remembered across editors.</summary>
public sealed record EditorStyle
{
    public AnnotationColor Color { get; init; } = AnnotationColor.Red;

    public StrokeWidth Stroke { get; init; } = StrokeWidth.Medium;

    public int TextSize { get; init; } = TextSizes.Default;

    public ArrowStyle ArrowStyle { get; init; } = ArrowStyle.Filled;

    public StickerKind Sticker { get; init; } = StickerKind.Check;

    public BlurStyle BlurStyle { get; init; } = BlurStyle.Pixelate;

    public int BlurLevel { get; init; } = BlurStyles.DefaultLevel;

    public bool TextOnly { get; init; }

    public static EditorStyle Load(ISettingsStore settings) => new()
    {
        Color = AnnotationColors.Parse(settings.Get(EditorSettings.LastColor)),
        Stroke = StrokeWidths.Parse(settings.Get(EditorSettings.LastStroke)),
        TextSize = TextSizes.Sanitize(settings.Get(EditorSettings.LastTextSize)),
        ArrowStyle = ArrowStyles.Parse(settings.Get(EditorSettings.LastArrowStyle)),
        Sticker = Stickers.Parse(settings.Get(EditorSettings.LastSticker)),
        BlurStyle = BlurStyles.Parse(settings.Get(EditorSettings.LastBlurStyle)),
        BlurLevel = BlurStyles.OpeningLevel(settings.Get(EditorSettings.LastBlurLevel)),
        TextOnly = settings.Get(EditorSettings.LastBlurTextOnly),
    };

    public void Save(ISettingsStore settings)
    {
        settings.Set(EditorSettings.LastColor, AnnotationColors.Id(Color));
        settings.Set(EditorSettings.LastStroke, StrokeWidths.Id(Stroke));
        settings.Set(EditorSettings.LastTextSize, TextSize);
        settings.Set(EditorSettings.LastArrowStyle, ArrowStyles.Id(ArrowStyle));
        settings.Set(EditorSettings.LastSticker, Stickers.Id(Sticker));
        settings.Set(EditorSettings.LastBlurStyle, BlurStyles.Id(BlurStyle));
        settings.Set(EditorSettings.LastBlurLevel, BlurLevel);
        settings.Set(EditorSettings.LastBlurTextOnly, TextOnly);
    }

    /// <summary>The remembered tool; Select and Crop fall back to Arrow.</summary>
    public static EditorTool LoadTool(ISettingsStore settings) =>
        EditorTools.TryParse(settings.Get(EditorSettings.LastTool), out var tool) && EditorTools.IsCreation(tool) ? tool : EditorTool.Arrow;
}
