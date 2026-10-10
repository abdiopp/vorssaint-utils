// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Modules.MediaTools;

public enum MediaTool
{
    Video,
    Gif,
    Image,
    Text,
}

public enum MediaSizingMode
{
    Resolution,
    TargetSize,
}

/// <summary>Media tools preferences (raw keys and defaults from spec 07 §4.6).</summary>
public static class MediaSettings
{
    public static readonly Setting<string> LastTool = new("mediaLastTool", "videoCompressor",
        Sanitize.OneOfStrings("videoCompressor", "videoCompressor", "gifMaker", "imageCompressor", "textExtractor"));

    // Video
    public static readonly Setting<double> VideoStart = new("mediaVideoStart", 0, NonNegative);
    public static readonly Setting<double> VideoEnd = new("mediaVideoEnd", 0, NonNegative);
    public static readonly Setting<double> VideoQuality = new("mediaVideoQuality", 0.68, v => double.IsFinite(v) ? Math.Clamp(v, 0.1, 1) : 0.7);
    public static readonly Setting<int> VideoMaxDimension = new("mediaVideoMaxDimension", 1280, v => SnapStep(v, 640, 3840, 320, 1280));
    public static readonly Setting<string> VideoSizing = new("mediaVideoSizing", "resolution", Sanitize.OneOfStrings("resolution", "resolution", "targetSize"));
    public static readonly Setting<int> VideoTargetMegabytes = new("mediaVideoTargetMegabytes", 20, Sanitize.Clamp(1, 512));

    // GIF
    public static readonly Setting<double> GifStart = new("mediaGIFStart", 0, NonNegative);
    public static readonly Setting<double> GifEnd = new("mediaGIFEnd", 0, NonNegative);
    public static readonly Setting<int> GifWidth = new("mediaGIFWidth", 720, v => SnapStep(v, 160, 1600, 80, 720));
    public static readonly Setting<double> GifFps = new("mediaGIFFPS", 12, v => double.IsFinite(v) && v >= 1 ? Math.Clamp(Math.Round(v), 1, 30) : 12);
    public static readonly Setting<bool> GifLoops = new("mediaGIFLoops", true);
    public static readonly Setting<string> GifSizing = new("mediaGIFSizing", "resolution", Sanitize.OneOfStrings("resolution", "resolution", "targetSize"));
    public static readonly Setting<int> GifTargetMegabytes = new("mediaGIFTargetMegabytes", 10, Sanitize.Clamp(1, 512));

    // Image
    public static readonly Setting<double> ImageQuality = new("mediaImageQuality", 0.72, v => double.IsFinite(v) ? Math.Clamp(v, 0.1, 1) : 0.72);
    public static readonly Setting<int> ImageMaxDimension = new("mediaImageMaxDimension", 1600, v => v <= 0 ? 1600 : Math.Clamp(v, 64, 20_000));
    public static readonly Setting<string> ImageFormat = new("mediaImageFormat", "jpeg", Sanitize.OneOfStrings("jpeg", "jpeg", "heic", "png", "pdf", "webp"));
    public static readonly Setting<bool> ImageStripMetadata = new("mediaImageStripMetadata", true);
    public static readonly Setting<string> ImageResizeKind = new("mediaImageResizeKind", "maxDimension",
        Sanitize.OneOfStrings("maxDimension", "none", "maxDimension", "width", "height", "exact"));
    public static readonly Setting<int> ImageResizeWidth = new("mediaImageResizeWidth", 1600, Sanitize.Clamp(1, 20_000));
    public static readonly Setting<int> ImageResizeHeight = new("mediaImageResizeHeight", 1200, Sanitize.Clamp(1, 20_000));
    public static readonly Setting<string> ImageExactMode = new("mediaImageExactResizeMode", "stretch", Sanitize.OneOfStrings("stretch", "stretch", "fit", "fill"));
    public static readonly Setting<string> WatermarkKind = new("mediaImageWatermarkKind", "off", Sanitize.OneOfStrings("off", "off", "text", "logo", "textAndLogo"));
    public static readonly Setting<string> WatermarkText = new("mediaImageWatermarkText", string.Empty, Sanitize.MaxLength(500));

    /// <summary>Absolute path of the logo; machine-specific, never in backups.</summary>
    public static readonly Setting<string> WatermarkLogoPath = new("mediaImageWatermarkLogoPath", string.Empty, machineState: true);

    public static readonly Setting<string> WatermarkPosition = new("mediaImageWatermarkPosition", "bottomRight",
        Sanitize.OneOfStrings("bottomRight", "topLeft", "topRight", "center", "bottomLeft", "bottomRight"));

    public static readonly Setting<double> WatermarkOpacity = new("mediaImageWatermarkOpacity", 0.45, v => double.IsFinite(v) ? Math.Clamp(v, 0.1, 1) : 0.45);

    /// <summary>Quirk kept from macOS: a stored 0 (or less) becomes 32.</summary>
    public static readonly Setting<int> WatermarkMargin = new("mediaImageWatermarkMargin", 32, v => v <= 0 ? 32 : Math.Min(v, 2000));

    public static readonly Setting<double> WatermarkScale = new("mediaImageWatermarkScale", 0.18, v => double.IsFinite(v) ? Math.Clamp(v, 0.05, 0.8) : 0.18);
    public static readonly Setting<string> ImageRenamePattern = new("mediaImageRenamePattern", string.Empty, Sanitize.MaxLength(255));
    public static readonly Setting<string> ImageBackground = new("mediaImageBackground", "transparent", Sanitize.OneOfStrings("transparent", "transparent", "white", "black"));
    public static readonly Setting<bool> ImagePreserveModificationDate = new("mediaImagePreserveModificationDate", false);
    public static readonly Setting<bool> ImageSaveInSubfolder = new("mediaImageSaveInSubfolder", false);

    /// <summary>Saved image profiles as a JSON string (decoded leniently on Windows).</summary>
    public static readonly Setting<string> ImageProfiles = new("mediaImageProfiles", "[]");

    public static readonly Setting<string> ImageSelectedProfileId = new("mediaImageSelectedProfileID", string.Empty);

    // Text
    public static readonly Setting<bool> TextAccurate = new("mediaTextAccurate", true);

    public static MediaTool ParseTool(string value) => value switch
    {
        "gifMaker" => MediaTool.Gif,
        "imageCompressor" => MediaTool.Image,
        "textExtractor" => MediaTool.Text,
        _ => MediaTool.Video,
    };

    public static string ToStorage(MediaTool tool) => tool switch
    {
        MediaTool.Gif => "gifMaker",
        MediaTool.Image => "imageCompressor",
        MediaTool.Text => "textExtractor",
        _ => "videoCompressor",
    };

    public static MediaSizingMode ParseSizing(string value) => value == "targetSize" ? MediaSizingMode.TargetSize : MediaSizingMode.Resolution;

    public static string ToStorage(MediaSizingMode mode) => mode == MediaSizingMode.TargetSize ? "targetSize" : "resolution";

    private static double NonNegative(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    private static int SnapStep(int value, int min, int max, int step, int fallback)
    {
        if (value <= 0)
        {
            return fallback;
        }

        var clamped = Math.Clamp(value, min, max);
        return min + ((int)Math.Round((clamped - min) / (double)step) * step);
    }
}

/// <summary>The three compression buttons (spec 07 §3.6.2) shared by Video and Image.</summary>
public static class MediaCompression
{
    public const double Low = 0.88;
    public const double Medium = 0.68;
    public const double High = 0.28;

    public static IReadOnlyList<double> Levels { get; } = [Low, Medium, High];

    /// <summary>The highlighted button: the level nearest the stored quality.</summary>
    public static double Nearest(double quality) => Levels.OrderBy(l => Math.Abs(l - quality)).First();

    public static string TitleKey(double level) => level switch
    {
        Low => "Strings.mediaCompressionLow",
        Medium => "Strings.mediaCompressionMedium",
        _ => "Strings.mediaCompressionHigh",
    };

    public static string DescriptionKey(double level) => level switch
    {
        Low => "Strings.mediaCompressionLowDescription",
        Medium => "Strings.mediaCompressionMediumDescription",
        _ => "Strings.mediaCompressionHighDescription",
    };
}
