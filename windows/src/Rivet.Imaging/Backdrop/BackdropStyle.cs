// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.Imaging.Backdrop;

public enum BackdropKind
{
    None,
    Preset,
    Solid,
    Gradient,
    Image,
}

/// <summary>An sRGB colour with components 0…1, stored in JSON as <c>[r,g,b]</c>.</summary>
[JsonConverter(typeof(RgbColorJsonConverter))]
public readonly record struct RgbColor(double R, double G, double B)
{
    public static RgbColor FromHex(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);

    public RgbColor Clamped() => new(Math.Clamp(R, 0, 1), Math.Clamp(G, 0, 1), Math.Clamp(B, 0, 1));

    public uint ToArgb()
    {
        var c = Clamped();
        return 0xFF000000u
               | ((uint)Math.Round(c.R * 255) << 16)
               | ((uint)Math.Round(c.G * 255) << 8)
               | (uint)Math.Round(c.B * 255);
    }
}

internal sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Colour must be [r,g,b].");
        }

        var values = new List<double>(3);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.GetDouble());
        }

        if (values.Count < 3)
        {
            throw new JsonException("Colour needs three components.");
        }

        return new RgbColor(values[0], values[1], values[2]).Clamped();
    }

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(Math.Round(value.R, 4));
        writer.WriteNumberValue(Math.Round(value.G, 4));
        writer.WriteNumberValue(Math.Round(value.B, 4));
        writer.WriteEndArray();
    }
}

/// <summary>
/// The background a capture is placed on, shared by the screenshot editor
/// and the recorder (spec 01 §3.10.10, spec 02 §5.8). Sliders are 0…1.
/// </summary>
public sealed record BackdropStyle
{
    public BackdropKind Kind { get; init; } = BackdropKind.None;

    /// <summary>ocean, sunset, forest, candy or graphite.</summary>
    public string? PresetId { get; init; }

    /// <summary>Solid: one colour. Gradient: start and end colours.</summary>
    public IReadOnlyList<RgbColor>? Colors { get; init; }

    /// <summary>Absolute path of the backdrop image.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Margin around the capture; default 0.5.</summary>
    public double Padding { get; init; } = 0.5;

    /// <summary>Corner rounding of the capture; default 0.</summary>
    public double CornerRadius { get; init; }

    /// <summary>Blur of the backdrop plate; default 0.</summary>
    public double Blur { get; init; }

    public static BackdropStyle None { get; } = new();

    [JsonIgnore]
    public bool HasBackdrop => Kind != BackdropKind.None;

    /// <summary>Clamps the sliders and demotes malformed looks to <see cref="BackdropKind.None"/>.</summary>
    public BackdropStyle Sanitized()
    {
        var padding = double.IsFinite(Padding) ? Math.Clamp(Padding, 0, 1) : 0.5;
        var corner = double.IsFinite(CornerRadius) ? Math.Clamp(CornerRadius, 0, 1) : 0.1;
        var blur = double.IsFinite(Blur) ? Math.Clamp(Blur, 0, 1) : 0;
        var valid = Kind switch
        {
            BackdropKind.None => true,
            BackdropKind.Preset => PresetId is not null && BackdropPresets.Find(PresetId) is not null,
            BackdropKind.Solid => Colors is { Count: 1 },
            BackdropKind.Gradient => Colors is { Count: 2 },
            BackdropKind.Image => !string.IsNullOrWhiteSpace(ImagePath),
            _ => false,
        };

        return valid
            ? this with { Padding = padding, CornerRadius = corner, Blur = blur, Colors = Colors?.Select(c => c.Clamped()).ToList() }
            : new BackdropStyle { Padding = padding, CornerRadius = corner, Blur = blur };
    }

    /// <summary>Same background, ignoring the sliders (swatch selection matching).</summary>
    public bool SameLook(BackdropStyle other) =>
        Kind == other.Kind && Kind switch
        {
            BackdropKind.None => true,
            BackdropKind.Preset => PresetId == other.PresetId,
            BackdropKind.Solid or BackdropKind.Gradient =>
                Colors is not null && other.Colors is not null && Colors.SequenceEqual(other.Colors),
            BackdropKind.Image => string.Equals(ImagePath, other.ImagePath, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>Applies another look while keeping this style's sliders.</summary>
    public BackdropStyle WithLook(BackdropStyle look) =>
        look with { Padding = Padding, CornerRadius = CornerRadius, Blur = Blur };

    /// <summary>Saved presets store the look with neutral sliders (padding 0.5, corners 0.1, blur 0).</summary>
    public BackdropStyle NormalizedForPreset() => this with { Padding = 0.5, CornerRadius = 0.1, Blur = 0 };

    /// <summary>The fill colours: preset or custom; empty for none and image.</summary>
    public IReadOnlyList<RgbColor> ResolvedColors() => Kind switch
    {
        BackdropKind.Preset => BackdropPresets.Find(PresetId!) is { } preset ? [preset.Start, preset.End] : [],
        BackdropKind.Solid or BackdropKind.Gradient => Colors ?? [],
        _ => [],
    };
}

public sealed record BackdropPreset(string Id, RgbColor Start, RgbColor End);

/// <summary>The five built-in diagonal gradients (top-left → bottom-right).</summary>
public static class BackdropPresets
{
    public static IReadOnlyList<BackdropPreset> All { get; } =
    [
        new("ocean", new RgbColor(0.20, 0.47, 0.96), new RgbColor(0.45, 0.83, 0.98)),
        new("sunset", new RgbColor(0.99, 0.36, 0.42), new RgbColor(1.00, 0.75, 0.35)),
        new("forest", new RgbColor(0.07, 0.56, 0.43), new RgbColor(0.62, 0.87, 0.50)),
        new("candy", new RgbColor(0.66, 0.32, 0.95), new RgbColor(0.99, 0.56, 0.65)),
        new("graphite", new RgbColor(0.23, 0.25, 0.31), new RgbColor(0.55, 0.60, 0.70)),
    ];

    public static BackdropPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Starting colours for the custom wells.</summary>
    public static RgbColor DefaultSolid { get; } = new(0.20, 0.47, 0.96);

    public static RgbColor DefaultGradientEnd { get; } = new(0.45, 0.83, 0.98);

    /// <summary>The 20-colour palette under the custom wells (10 columns).</summary>
    public static IReadOnlyList<RgbColor> Palette { get; } =
    [
        new(0.96, 0.26, 0.21), new(1, 0.58, 0), new(1, 0.8, 0), new(0.55, 0.86, 0.25), new(0.2, 0.78, 0.35),
        new(0.1, 0.74, 0.61), new(0.15, 0.78, 0.85), new(0.04, 0.52, 1), new(0.35, 0.34, 0.84), new(0.69, 0.32, 0.87),
        new(1, 0.45, 0.66), new(0.91, 0.12, 0.39), new(0.55, 0.39, 0.29), new(0.11, 0.16, 0.32), new(0.05, 0.05, 0.06),
        new(0.25, 0.25, 0.28), new(0.55, 0.55, 0.58), new(0.85, 0.85, 0.87), new(1, 1, 1), new(0.99, 0.93, 0.85),
    ];

    /// <summary>At most 12 saved custom backgrounds; the oldest are dropped.</summary>
    public const int MaxSavedPresets = 12;
}
