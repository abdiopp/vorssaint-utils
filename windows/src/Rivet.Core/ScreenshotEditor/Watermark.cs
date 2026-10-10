// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Core.ScreenshotEditor;

public enum WatermarkKind
{
    None,
    Text,
    Image,
}

/// <summary>Where the mark sits, as a unit point (leading = left, trailing = right).</summary>
public enum WatermarkAnchor
{
    TopLeading,
    Top,
    TopTrailing,
    Leading,
    Center,
    Trailing,
    BottomLeading,
    Bottom,
    BottomTrailing,
}

/// <summary>A text or picture watermark (spec 01 §3.10.11, §5.3).</summary>
public sealed record WatermarkStyle
{
    public const int MaxTextLength = 120;
    public const int MaxSavedPresets = 12;
    public const double DefaultSize = 0.3;
    public const double DefaultOpacity = 0.4;
    public const double MinOpacity = 0.05;

    public WatermarkKind Kind { get; init; }

    public string Text { get; init; } = string.Empty;

    public string? ImagePath { get; init; }

    public AnnotationColor Color { get; init; } = AnnotationColor.White;

    public WatermarkAnchor Anchor { get; init; } = WatermarkAnchor.BottomTrailing;

    /// <summary>0…1.</summary>
    public double Size { get; init; } = DefaultSize;

    /// <summary>0.05…1.</summary>
    public double Opacity { get; init; } = DefaultOpacity;

    /// <summary>−90…90 degrees; positive tilts up to the right (counter-clockwise on screen).</summary>
    public double Rotation { get; init; }

    public static WatermarkStyle None { get; } = new();

    /// <summary>Trims and caps the text, clamps sliders, and demotes an empty text or missing path to none (other fields kept).</summary>
    public WatermarkStyle Sanitized()
    {
        var text = (Text ?? string.Empty).Trim();
        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength].TrimEnd();
        }

        var kind = Kind switch
        {
            WatermarkKind.Text when text.Length == 0 => WatermarkKind.None,
            WatermarkKind.Image when string.IsNullOrWhiteSpace(ImagePath) => WatermarkKind.None,
            WatermarkKind.Text or WatermarkKind.Image => Kind,
            _ => WatermarkKind.None,
        };
        return this with
        {
            Kind = kind,
            Text = text,
            Size = double.IsFinite(Size) ? Math.Clamp(Size, 0, 1) : DefaultSize,
            Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, MinOpacity, 1) : DefaultOpacity,
            Rotation = double.IsFinite(Rotation) ? Math.Clamp(Rotation, -90, 90) : 0,
        };
    }

    /// <summary>A mark is drawn (text with text, or a picture path).</summary>
    public bool IsDrawn => Sanitized().Kind != WatermarkKind.None;

    /// <summary>Same mark, ignoring placement (preset duplicate check).</summary>
    public bool SameMark(WatermarkStyle other)
    {
        var a = Sanitized();
        var b = other.Sanitized();
        return a.Kind == b.Kind && a.Kind switch
        {
            WatermarkKind.Text => a.Text == b.Text && a.Color == b.Color,
            WatermarkKind.Image => string.Equals(a.ImagePath, b.ImagePath, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }
}

/// <summary>JSON with the macOS field names (kind, text, imagePath, color, anchor, size, opacity, rotation).</summary>
public static class WatermarkCodec
{
    public static string AnchorId(WatermarkAnchor anchor) => anchor switch
    {
        WatermarkAnchor.TopLeading => "topLeading",
        WatermarkAnchor.Top => "top",
        WatermarkAnchor.TopTrailing => "topTrailing",
        WatermarkAnchor.Leading => "leading",
        WatermarkAnchor.Center => "center",
        WatermarkAnchor.Trailing => "trailing",
        WatermarkAnchor.BottomLeading => "bottomLeading",
        WatermarkAnchor.Bottom => "bottom",
        _ => "bottomTrailing",
    };

    public static WatermarkAnchor ParseAnchor(string? id) =>
        Enum.GetValues<WatermarkAnchor>().FirstOrDefault(a => AnchorId(a) == id, WatermarkAnchor.BottomTrailing);

    public static JsonObject ToJson(WatermarkStyle style)
    {
        var s = style.Sanitized();
        var json = new JsonObject
        {
            ["kind"] = s.Kind.ToString().ToLowerInvariant(),
            ["text"] = s.Text,
            ["color"] = AnnotationColors.Id(s.Color),
            ["anchor"] = AnchorId(s.Anchor),
            ["size"] = s.Size,
            ["opacity"] = s.Opacity,
            ["rotation"] = s.Rotation,
        };
        if (s.ImagePath is not null)
        {
            json["imagePath"] = s.ImagePath;
        }

        return json;
    }

    public static string Encode(WatermarkStyle style) => ToJson(style).ToJsonString();

    /// <summary>Tolerant reading: unknown or undecodable values fall back to the defaults (none).</summary>
    public static WatermarkStyle Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return WatermarkStyle.None;
        }

        try
        {
            return FromJson(JsonNode.Parse(json));
        }
        catch (JsonException)
        {
            return WatermarkStyle.None;
        }
    }

    public static WatermarkStyle FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return WatermarkStyle.None;
        }

        var kind = Str(obj, "kind") switch
        {
            "text" => WatermarkKind.Text,
            "image" => WatermarkKind.Image,
            _ => WatermarkKind.None,
        };
        return new WatermarkStyle
        {
            Kind = kind,
            Text = Str(obj, "text") ?? string.Empty,
            ImagePath = Str(obj, "imagePath"),
            Color = AnnotationColors.Parse(Str(obj, "color"), AnnotationColor.White),
            Anchor = ParseAnchor(Str(obj, "anchor")),
            Size = Num(obj, "size") ?? WatermarkStyle.DefaultSize,
            Opacity = Num(obj, "opacity") ?? WatermarkStyle.DefaultOpacity,
            Rotation = Num(obj, "rotation") ?? 0,
        }.Sanitized();
    }

    public static string EncodePresets(IEnumerable<WatermarkStyle> presets) =>
        new JsonArray(SanitizePresets(presets).Select(p => (JsonNode)ToJson(p)).ToArray()).ToJsonString();

    public static IReadOnlyList<WatermarkStyle> DecodePresets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) is JsonArray array ? SanitizePresets(array.Select(FromJson)) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Sanitized, none entries dropped, the last 12 kept.</summary>
    public static IReadOnlyList<WatermarkStyle> SanitizePresets(IEnumerable<WatermarkStyle> presets)
    {
        var list = presets.Select(p => p.Sanitized()).Where(p => p.Kind != WatermarkKind.None).ToList();
        return list.Count > WatermarkStyle.MaxSavedPresets ? list[^WatermarkStyle.MaxSavedPresets..] : list;
    }

    private static string? Str(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<double>(out var d))
        {
            return d;
        }

        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}

/// <summary>Where a watermark lands inside the image.</summary>
public readonly record struct WatermarkPlacement(ImgPoint Center, double Fit, double RotationRadians, double Margin);

/// <summary>Watermark geometry (spec 01 §6.14), in image pixels.</summary>
public static class WatermarkGeometry
{
    /// <summary><c>fontPx = max(8, round(short · (0.02 + 0.14·size)))</c>.</summary>
    public static double TextFontPixels(int imageWidth, int imageHeight, double size) =>
        Math.Max(8, SpecMath.Round(Math.Min(imageWidth, imageHeight) * (0.02 + (0.14 * size))));

    /// <summary><c>imageW = max(1, round(W · (0.05 + 0.45·size))); imageH = imageW · srcH / srcW</c>.</summary>
    public static (double Width, double Height) ImageSize(int imageWidth, double size, int sourceWidth, int sourceHeight)
    {
        var w = Math.Max(1, SpecMath.Round(imageWidth * (0.05 + (0.45 * size))));
        return (w, w * sourceHeight / Math.Max(1.0, sourceWidth));
    }

    public static (double X, double Y) AnchorUnit(WatermarkAnchor anchor) => anchor switch
    {
        WatermarkAnchor.TopLeading => (0, 0),
        WatermarkAnchor.Top => (0.5, 0),
        WatermarkAnchor.TopTrailing => (1, 0),
        WatermarkAnchor.Leading => (0, 0.5),
        WatermarkAnchor.Center => (0.5, 0.5),
        WatermarkAnchor.Trailing => (1, 0.5),
        WatermarkAnchor.BottomLeading => (0, 1),
        WatermarkAnchor.Bottom => (0.5, 1),
        _ => (1, 1),
    };

    /// <summary>
    /// <code>
    /// bounds = (|cw·cos| + |ch·sin|, |cw·sin| + |ch·cos|)
    /// r = clamp(R, 0, short/2); roundedInset = r &gt; 0 ? ceil(r·(1 − √0.5)) + 1 : 0
    /// margin = min(0.45·short, max(0.05·short, roundedInset))
    /// fit = min(1, avail.w/bounds.w, avail.h/bounds.h)
    /// center = (margin + (avail.w − fitted.w)·ux + fitted.w/2, margin + (avail.h − fitted.h)·uy + fitted.h/2)
    /// </code>
    /// </summary>
    public static WatermarkPlacement Place(int imageWidth, int imageHeight, double contentWidth, double contentHeight, WatermarkAnchor anchor, double rotationDegrees, double cornerRadius)
    {
        double shortSide = Math.Min(imageWidth, imageHeight);
        var rad = rotationDegrees * Math.PI / 180;
        var bw = (Math.Abs(contentWidth * Math.Cos(rad)) + Math.Abs(contentHeight * Math.Sin(rad)));
        var bh = (Math.Abs(contentWidth * Math.Sin(rad)) + Math.Abs(contentHeight * Math.Cos(rad)));
        var r = Math.Clamp(cornerRadius, 0, shortSide / 2);
        var roundedInset = r > 0 ? Math.Ceiling(r * (1 - Math.Sqrt(0.5))) + 1 : 0;
        var margin = Math.Min(0.45 * shortSide, Math.Max(0.05 * shortSide, roundedInset));
        var availW = imageWidth - (2 * margin);
        var availH = imageHeight - (2 * margin);
        var fit = Math.Min(1, Math.Min(availW / Math.Max(1e-9, bw), availH / Math.Max(1e-9, bh)));
        fit = Math.Max(0, fit);
        var fittedW = bw * fit;
        var fittedH = bh * fit;
        var (ux, uy) = AnchorUnit(anchor);
        var center = new ImgPoint(
            margin + ((availW - fittedW) * ux) + (fittedW / 2),
            margin + ((availH - fittedH) * uy) + (fittedH / 2));
        return new WatermarkPlacement(center, fit, rad, margin);
    }
}
