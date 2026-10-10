// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Core.Modules.MediaTools;

public enum ImageOutputFormat
{
    Jpeg,
    Png,
    WebP,
    Heic,
    Pdf,
}

public enum ImageBackground
{
    Transparent,
    White,
    Black,
}

public enum WatermarkKind
{
    Off,
    Text,
    Logo,
    TextAndLogo,
}

public enum WatermarkPosition
{
    TopLeft,
    TopRight,
    Center,
    BottomLeft,
    BottomRight,
}

public sealed record WatermarkOptions(
    WatermarkKind Kind = WatermarkKind.Off,
    string Text = "",
    string LogoPath = "",
    WatermarkPosition Position = WatermarkPosition.BottomRight,
    double Opacity = 0.45,
    int Margin = 32,
    double Scale = 0.18)
{
    /// <summary>Text counts only if non-empty after trimming.</summary>
    public bool HasText => Kind is WatermarkKind.Text or WatermarkKind.TextAndLogo && Text.Trim().Length > 0;

    /// <summary>The logo counts only when a path is set.</summary>
    public bool HasLogo => Kind is WatermarkKind.Logo or WatermarkKind.TextAndLogo && LogoPath.Length > 0;

    public bool IsActive => HasText || HasLogo;
}

/// <summary>Every image option a profile or preset carries.</summary>
public sealed record ImageOptions(
    ImageOutputFormat Format,
    double Quality,
    ImageResize Resize,
    bool StripMetadata,
    WatermarkOptions Watermark,
    string RenamePattern,
    ImageBackground Background,
    bool PreserveModificationDate)
{
    public static ImageOptions Default => new(ImageOutputFormat.Jpeg, 0.72, new ImageResize(ImageResizeKind.MaxDimension), true, new WatermarkOptions(), string.Empty, ImageBackground.Transparent, false);

    public string Extension => MediaImageFormats.Extension(Format);
}

public sealed record ImageProfile(string Id, string Name, ImageOptions Options);

public static class MediaImageFormats
{
    public static string Extension(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.Png => "png",
        ImageOutputFormat.WebP => "webp",
        ImageOutputFormat.Heic => "heic",
        ImageOutputFormat.Pdf => "pdf",
        _ => "jpg",
    };

    public static string StorageValue(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.Png => "png",
        ImageOutputFormat.WebP => "webp",
        ImageOutputFormat.Heic => "heic",
        ImageOutputFormat.Pdf => "pdf",
        _ => "jpeg",
    };

    public static ImageOutputFormat Parse(string? value) => value switch
    {
        "png" => ImageOutputFormat.Png,
        "webp" => ImageOutputFormat.WebP,
        "heic" => ImageOutputFormat.Heic,
        "pdf" => ImageOutputFormat.Pdf,
        _ => ImageOutputFormat.Jpeg,
    };

    /// <summary>JPEG and PDF have no alpha: a transparent background is forced to white.</summary>
    public static ImageBackground EffectiveBackground(ImageOutputFormat format, ImageBackground background) =>
        background == ImageBackground.Transparent && format is ImageOutputFormat.Jpeg or ImageOutputFormat.Pdf ? ImageBackground.White : background;

    /// <summary>Input files the image and text tools accept (PDF is excluded; HEIC/TIFF decode through Windows codecs).</summary>
    public static IReadOnlyList<string> ImageInputExtensions { get; } =
        ["jpg", "jpeg", "jfif", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "ico", "wbmp", "dng", "avif"];

    public static IReadOnlyList<string> VideoInputExtensions { get; } =
        ["mp4", "m4v", "mov", "avi", "wmv", "mkv", "webm", "3gp", "3g2", "mpg", "mpeg", "ts", "mts", "m2ts"];

    public static bool IsImage(string path) => ImageInputExtensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    public static bool IsVideo(string path) => VideoInputExtensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    public static string ResizeStorage(ImageResizeKind kind) => kind switch
    {
        ImageResizeKind.None => "none",
        ImageResizeKind.Width => "width",
        ImageResizeKind.Height => "height",
        ImageResizeKind.Exact => "exact",
        _ => "maxDimension",
    };

    public static ImageResizeKind ParseResize(string? value) => value switch
    {
        "none" => ImageResizeKind.None,
        "width" => ImageResizeKind.Width,
        "height" => ImageResizeKind.Height,
        "exact" => ImageResizeKind.Exact,
        _ => ImageResizeKind.MaxDimension,
    };

    public static string ExactStorage(ExactResizeMode mode) => mode switch
    {
        ExactResizeMode.Fit => "fit",
        ExactResizeMode.Fill => "fill",
        _ => "stretch",
    };

    public static ExactResizeMode ParseExact(string? value) => value switch
    {
        "fit" => ExactResizeMode.Fit,
        "fill" => ExactResizeMode.Fill,
        _ => ExactResizeMode.Stretch,
    };

    public static string WatermarkKindStorage(WatermarkKind kind) => kind switch
    {
        WatermarkKind.Text => "text",
        WatermarkKind.Logo => "logo",
        WatermarkKind.TextAndLogo => "textAndLogo",
        _ => "off",
    };

    public static WatermarkKind ParseWatermarkKind(string? value) => value switch
    {
        "text" => WatermarkKind.Text,
        "logo" => WatermarkKind.Logo,
        "textAndLogo" => WatermarkKind.TextAndLogo,
        _ => WatermarkKind.Off,
    };

    public static string PositionStorage(WatermarkPosition position) => position switch
    {
        WatermarkPosition.TopLeft => "topLeft",
        WatermarkPosition.TopRight => "topRight",
        WatermarkPosition.Center => "center",
        WatermarkPosition.BottomLeft => "bottomLeft",
        _ => "bottomRight",
    };

    public static WatermarkPosition ParsePosition(string? value) => value switch
    {
        "topLeft" => WatermarkPosition.TopLeft,
        "topRight" => WatermarkPosition.TopRight,
        "center" => WatermarkPosition.Center,
        "bottomLeft" => WatermarkPosition.BottomLeft,
        _ => WatermarkPosition.BottomRight,
    };

    public static string BackgroundStorage(ImageBackground background) => background switch
    {
        ImageBackground.White => "white",
        ImageBackground.Black => "black",
        _ => "transparent",
    };

    public static ImageBackground ParseBackground(string? value) => value switch
    {
        "white" => ImageBackground.White,
        "black" => ImageBackground.Black,
        _ => ImageBackground.Transparent,
    };
}

/// <summary>The Web / Social / Docs quick presets: each replaces every image option and turns the watermark off.</summary>
public static class MediaImagePresets
{
    public static ImageOptions Web => new(ImageOutputFormat.Jpeg, 0.72, new ImageResize(ImageResizeKind.MaxDimension, MaxDimension: 1600), true, new WatermarkOptions(), "{name}-web", ImageBackground.Transparent, false);

    public static ImageOptions Social => new(ImageOutputFormat.Png, 0.82, new ImageResize(ImageResizeKind.MaxDimension, MaxDimension: 2048), true, new WatermarkOptions(), "{name}-social", ImageBackground.Transparent, false);

    public static ImageOptions Docs => new(ImageOutputFormat.Pdf, 0.70, new ImageResize(ImageResizeKind.MaxDimension, MaxDimension: 1600), true, new WatermarkOptions(), string.Empty, ImageBackground.White, true);
}

/// <summary>
/// <c>mediaImageProfiles</c>: a JSON array of <c>{id, name, options:{…}}</c>
/// (spec 07 §5.6). macOS fails the whole array on one malformed profile; the
/// port decodes leniently (each field falls back to its default, a broken
/// profile is skipped alone). Blank names become "Profile &lt;position&gt;" and
/// duplicate or blank ids get new UUIDs, on load and on save.
/// </summary>
public static class MediaImageProfiles
{
    public static IReadOnlyList<ImageProfile> Decode(string? json, Func<int, string> defaultName)
    {
        var result = new List<ImageProfile>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return result;
        }

        if (array is null)
        {
            return result;
        }

        foreach (var node in array)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            var options = obj["options"] as JsonObject;
            result.Add(new ImageProfile(Str(obj, "id"), Str(obj, "name"), DecodeOptions(options)));
        }

        return Normalize(result, defaultName);
    }

    public static string Encode(IReadOnlyList<ImageProfile> profiles, Func<int, string> defaultName)
    {
        var array = new JsonArray();
        foreach (var profile in Normalize(profiles, defaultName))
        {
            array.Add(new JsonObject
            {
                ["id"] = profile.Id,
                ["name"] = profile.Name,
                ["options"] = EncodeOptions(profile.Options),
            });
        }

        return array.ToJsonString();
    }

    public static IReadOnlyList<ImageProfile> Normalize(IReadOnlyList<ImageProfile> profiles, Func<int, string> defaultName)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ImageProfile>();
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var id = profile.Id.Trim();
            if (id.Length == 0 || !ids.Add(id))
            {
                id = Guid.NewGuid().ToString("D").ToUpperInvariant();
                ids.Add(id);
            }

            var name = profile.Name.Trim();
            result.Add(profile with { Id = id, Name = name.Length == 0 ? defaultName(i + 1) : name });
        }

        return result;
    }

    /// <summary>Backups clear logo paths: a logo-only watermark becomes off, "Text + logo" becomes text (or off without text).</summary>
    public static ImageOptions ForBackup(ImageOptions options)
    {
        var watermark = options.Watermark;
        var kind = watermark.Kind switch
        {
            WatermarkKind.Logo => WatermarkKind.Off,
            WatermarkKind.TextAndLogo => watermark.Text.Trim().Length > 0 ? WatermarkKind.Text : WatermarkKind.Off,
            _ => watermark.Kind,
        };
        return options with { Watermark = watermark with { Kind = kind, LogoPath = string.Empty } };
    }

    private static ImageOptions DecodeOptions(JsonObject? obj)
    {
        var d = ImageOptions.Default;
        if (obj is null)
        {
            return d;
        }

        var resize = obj["resizeMode"] as JsonObject;
        var watermark = obj["watermark"] as JsonObject;
        var renameNode = obj["renamePattern"];
        var rename = renameNode is JsonObject renameObj ? Str(renameObj, "rawValue") : renameNode is JsonValue ? Str(obj, "renamePattern") : string.Empty;
        return new ImageOptions(
            MediaImageFormats.Parse(Str(obj, "format", "jpeg")),
            Math.Clamp(Num(obj, "quality", d.Quality), 0.1, 1),
            new ImageResize(
                MediaImageFormats.ParseResize(Str(resize, "kind", "maxDimension")),
                ClampInt(Num(resize, "maxDimension", 1600), 1, 20_000),
                ClampInt(Num(resize, "width", 1600), 1, 20_000),
                ClampInt(Num(resize, "height", 1200), 1, 20_000),
                MediaImageFormats.ParseExact(Str(resize, "exactMode", "stretch"))),
            Bool(obj, "stripMetadata", true),
            new WatermarkOptions(
                MediaImageFormats.ParseWatermarkKind(Str(watermark, "kind", "off")),
                Str(watermark, "text"),
                Str(watermark, "logoPath"),
                MediaImageFormats.ParsePosition(Str(watermark, "position", "bottomRight")),
                Math.Clamp(Num(watermark, "opacity", 0.45), 0.1, 1),
                Num(watermark, "margin", 32) is var m && m <= 0 ? 32 : ClampInt(m, 1, 2000),
                Math.Clamp(Num(watermark, "scale", 0.18), 0.05, 0.8)),
            rename,
            MediaImageFormats.ParseBackground(Str(obj, "background", "transparent")),
            Bool(obj, "preserveModificationDate", false));
    }

    private static JsonObject EncodeOptions(ImageOptions o) => new()
    {
        ["quality"] = o.Quality,
        ["maxDimension"] = o.Resize.MaxDimension,
        ["format"] = MediaImageFormats.StorageValue(o.Format),
        ["stripMetadata"] = o.StripMetadata,
        ["resizeMode"] = new JsonObject
        {
            ["kind"] = MediaImageFormats.ResizeStorage(o.Resize.Kind),
            ["maxDimension"] = o.Resize.MaxDimension,
            ["width"] = o.Resize.Width,
            ["height"] = o.Resize.Height,
            ["exactMode"] = MediaImageFormats.ExactStorage(o.Resize.ExactMode),
        },
        ["watermark"] = new JsonObject
        {
            ["kind"] = MediaImageFormats.WatermarkKindStorage(o.Watermark.Kind),
            ["text"] = o.Watermark.Text,
            ["logoPath"] = o.Watermark.LogoPath,
            ["position"] = MediaImageFormats.PositionStorage(o.Watermark.Position),
            ["opacity"] = o.Watermark.Opacity,
            ["margin"] = o.Watermark.Margin,
            ["scale"] = o.Watermark.Scale,
        },
        ["renamePattern"] = new JsonObject { ["rawValue"] = o.RenamePattern },
        ["background"] = MediaImageFormats.BackgroundStorage(o.Background),
        ["preserveModificationDate"] = o.PreserveModificationDate,
    };

    private static string Str(JsonObject? obj, string key, string fallback = "") =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private static double Num(JsonObject? obj, string key, double fallback) =>
        obj?[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : fallback;

    private static bool Bool(JsonObject? obj, string key, bool fallback) =>
        obj?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static int ClampInt(double value, int min, int max) => (int)Math.Clamp(Math.Round(value), min, max);
}
