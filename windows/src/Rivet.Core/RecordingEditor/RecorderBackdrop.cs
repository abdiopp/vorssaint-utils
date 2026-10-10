// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Core.RecordingEditor;

public enum RecorderBackdropKind
{
    None,
    Preset,
    Solid,
    Gradient,
    Image,
}

/// <summary>An sRGB colour, components 0…1.</summary>
public readonly record struct RgbValue(double R, double G, double B)
{
    public bool IsFinite => double.IsFinite(R) && double.IsFinite(G) && double.IsFinite(B);

    public RgbValue Clamped() => new(RecorderMath.Clamp01(R), RecorderMath.Clamp01(G), RecorderMath.Clamp01(B));
}

/// <summary>
/// The background style as the edit document and the shared preset list store
/// it (spec 02 §5.8, spec 01 §5.3). Core keeps its own copy of the schema so
/// the document model does not depend on the imaging library; the imaging
/// side converts it to <c>Rivet.Imaging.Backdrop.BackdropStyle</c> to render.
/// <code>
/// {"kind":"preset","presetID":"graphite","padding":0.45,"cornerRadius":0.35,"blur":0}
/// </code>
/// Reading is tolerant (any key case, kind as a name or a number); writing
/// uses the macOS key names.
/// </summary>
public sealed record RecorderBackdrop
{
    public static IReadOnlyList<string> PresetIds { get; } = ["ocean", "sunset", "forest", "candy", "graphite"];

    public RecorderBackdropKind Kind { get; init; } = RecorderBackdropKind.None;

    public string? PresetId { get; init; }

    public IReadOnlyList<RgbValue>? Colors { get; init; }

    public string? ImagePath { get; init; }

    public double Padding { get; init; } = 0.5;

    public double CornerRadius { get; init; }

    public double Blur { get; init; }

    public static RecorderBackdrop None { get; } = new();

    public bool HasBackdrop => Kind != RecorderBackdropKind.None;

    /// <summary>Clamps the sliders and demotes malformed styles to none (§5.8).</summary>
    public RecorderBackdrop Sanitized()
    {
        var padding = RecorderMath.ClampOr(Padding, 0, 1, 0.5);
        var corner = RecorderMath.ClampOr(CornerRadius, 0, 1, 0.1);
        var blur = RecorderMath.ClampOr(Blur, 0, 1, 0);
        var valid = Kind switch
        {
            RecorderBackdropKind.None => true,
            RecorderBackdropKind.Preset => PresetId is not null && PresetIds.Contains(PresetId, StringComparer.Ordinal),
            RecorderBackdropKind.Solid => Colors is { Count: 1 } && Colors.All(c => c.IsFinite),
            RecorderBackdropKind.Gradient => Colors is { Count: 2 } && Colors.All(c => c.IsFinite),
            RecorderBackdropKind.Image => !string.IsNullOrWhiteSpace(ImagePath) && !ImagePath.Contains('\0'),
            _ => false,
        };
        if (!valid)
        {
            return new RecorderBackdrop { Padding = padding, CornerRadius = corner, Blur = blur };
        }

        return Kind switch
        {
            RecorderBackdropKind.None => new RecorderBackdrop { Padding = padding, CornerRadius = corner, Blur = blur },
            RecorderBackdropKind.Preset => new RecorderBackdrop { Kind = Kind, PresetId = PresetId, Padding = padding, CornerRadius = corner, Blur = blur },
            RecorderBackdropKind.Image => new RecorderBackdrop { Kind = Kind, ImagePath = ImagePath, Padding = padding, CornerRadius = corner, Blur = blur },
            _ => new RecorderBackdrop { Kind = Kind, Colors = Colors!.Select(c => c.Clamped()).ToList(), Padding = padding, CornerRadius = corner, Blur = blur },
        };
    }

    /// <summary>Same background, ignoring the sliders (swatch selection matching).</summary>
    public bool SameLook(RecorderBackdrop other) =>
        Kind == other.Kind && Kind switch
        {
            RecorderBackdropKind.None => true,
            RecorderBackdropKind.Preset => PresetId == other.PresetId,
            RecorderBackdropKind.Solid or RecorderBackdropKind.Gradient =>
                Colors is not null && other.Colors is not null && Colors.Count == other.Colors.Count
                && Colors.Zip(other.Colors).All(p => Near(p.First.R, p.Second.R) && Near(p.First.G, p.Second.G) && Near(p.First.B, p.Second.B)),
            RecorderBackdropKind.Image => string.Equals(ImagePath, other.ImagePath, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>Same look and same sliders (within rounding).</summary>
    public bool SameStyle(RecorderBackdrop other) =>
        SameLook(other) && Near(Padding, other.Padding) && Near(CornerRadius, other.CornerRadius) && Near(Blur, other.Blur);

    /// <summary>Another look with this style's sliders (picking a swatch keeps margin, corners and blur).</summary>
    public RecorderBackdrop WithLook(RecorderBackdrop look) =>
        look with { Padding = Padding, CornerRadius = CornerRadius, Blur = Blur };

    /// <summary>Saved customs keep the look with neutral sliders (padding 0.5, corners 0.1, blur 0).</summary>
    public RecorderBackdrop NormalizedForPreset() => this with { Padding = 0.5, CornerRadius = 0.1, Blur = 0 };

    /// <summary>The two gradient stops (or one solid colour); empty for none and image.</summary>
    public IReadOnlyList<RgbValue> ResolvedColors() => Kind switch
    {
        RecorderBackdropKind.Preset => PresetColors(PresetId),
        RecorderBackdropKind.Solid or RecorderBackdropKind.Gradient => Colors ?? [],
        _ => [],
    };

    public static IReadOnlyList<RgbValue> PresetColors(string? id) => id switch
    {
        "ocean" => [new(0.20, 0.47, 0.96), new(0.45, 0.83, 0.98)],
        "sunset" => [new(0.99, 0.36, 0.42), new(1.00, 0.75, 0.35)],
        "forest" => [new(0.07, 0.56, 0.43), new(0.62, 0.87, 0.50)],
        "candy" => [new(0.66, 0.32, 0.95), new(0.99, 0.56, 0.65)],
        "graphite" => [new(0.23, 0.25, 0.31), new(0.55, 0.60, 0.70)],
        _ => [],
    };

    /// <summary>Reads a style; empty or undecodable input means none (§5.6 "broken → none").</summary>
    public static RecorderBackdrop Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        try
        {
            return FromNode(JsonNode.Parse(json)) ?? None;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return None;
        }
    }

    /// <summary>Reads one style object; null when it is not an object.</summary>
    public static RecorderBackdrop? FromNode(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        var values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in obj)
        {
            values[key] = value;
        }

        var kind = RecorderBackdropKind.None;
        if (values.TryGetValue("kind", out var kindNode) && kindNode is JsonValue kindValue)
        {
            if (kindValue.TryGetValue<string>(out var name) && Enum.TryParse<RecorderBackdropKind>(name, ignoreCase: true, out var parsed))
            {
                kind = parsed;
            }
            else if (kindValue.TryGetValue<int>(out var number) && Enum.IsDefined(typeof(RecorderBackdropKind), number))
            {
                kind = (RecorderBackdropKind)number;
            }
            else
            {
                // An unknown kind is malformed: demote by marking the preset invalid.
                kind = RecorderBackdropKind.Preset;
                values["presetId"] = JsonValue.Create("\u0000invalid");
            }
        }

        List<RgbValue>? colors = null;
        if (values.TryGetValue("colors", out var colorsNode) && colorsNode is JsonArray array)
        {
            colors = [];
            foreach (var item in array)
            {
                if (item is JsonArray rgb && rgb.Count >= 3 && TryDouble(rgb[0], out var r) && TryDouble(rgb[1], out var g) && TryDouble(rgb[2], out var b))
                {
                    colors.Add(new RgbValue(r, g, b));
                }
                else
                {
                    colors.Add(new RgbValue(double.NaN, double.NaN, double.NaN));
                }
            }
        }

        return new RecorderBackdrop
        {
            Kind = kind,
            PresetId = GetString(values, "presetId"),
            Colors = colors,
            ImagePath = GetString(values, "imagePath"),
            Padding = GetDouble(values, "padding", 0.5),
            CornerRadius = GetDouble(values, "cornerRadius", 0),
            Blur = GetDouble(values, "blur", 0),
        };
    }

    /// <summary>The macOS JSON form (sanitized first).</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public JsonObject ToNode() => (JsonObject)JsonNode.Parse(ToJson())!;

    public void WriteTo(Utf8JsonWriter writer)
    {
        var s = Sanitized();
        writer.WriteStartObject();
        writer.WriteString("kind", s.Kind.ToString().ToLowerInvariant());
        if (s.Kind == RecorderBackdropKind.Preset)
        {
            writer.WriteString("presetID", s.PresetId);
        }

        if (s.Kind is RecorderBackdropKind.Solid or RecorderBackdropKind.Gradient && s.Colors is not null)
        {
            writer.WriteStartArray("colors");
            foreach (var c in s.Colors)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(c.R, 4));
                writer.WriteNumberValue(Math.Round(c.G, 4));
                writer.WriteNumberValue(Math.Round(c.B, 4));
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        if (s.Kind == RecorderBackdropKind.Image)
        {
            writer.WriteString("imagePath", s.ImagePath);
        }

        writer.WriteNumber("padding", s.Padding);
        writer.WriteNumber("cornerRadius", s.CornerRadius);
        writer.WriteNumber("blur", s.Blur);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The edit document's string: "" for the default none, otherwise the JSON
    /// (a none style keeps its sliders so picking None and back keeps them).
    /// </summary>
    public string ToDocumentString()
    {
        var s = Sanitized();
        return !s.HasBackdrop && Near(s.Padding, 0.5) && Near(s.CornerRadius, 0) && Near(s.Blur, 0) ? string.Empty : s.ToJson();
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.0005;

    private static string? GetString(Dictionary<string, JsonNode?> values, string key) =>
        values.TryGetValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double GetDouble(Dictionary<string, JsonNode?> values, string key, double fallback) =>
        values.TryGetValue(key, out var node) && TryDouble(node, out var d) ? d : fallback;

    private static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v)
        {
            return false;
        }

        if (v.TryGetValue<double>(out value))
        {
            return true;
        }

        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
