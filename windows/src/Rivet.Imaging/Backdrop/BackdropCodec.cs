// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Rivet.Imaging.Backdrop;

/// <summary>
/// Backdrop styles as stored in <c>screenshotBackdropStyle</c> and
/// <c>screenshotBackdropPresets</c>: JSON with the macOS field names (kind,
/// presetID, colors, imagePath, padding, cornerRadius, blur). Reading is
/// tolerant (any key casing, numeric or string kinds, double-encoded JSON),
/// so values written by another module's serializer still load. Shared by
/// the screenshot editor and the recording editor (§3.29: one set of saved
/// custom backgrounds).
/// </summary>
public static class BackdropCodec
{
    public static JsonObject ToJson(BackdropStyle style)
    {
        var s = style.Sanitized();
        var json = new JsonObject
        {
            ["kind"] = s.Kind.ToString().ToLowerInvariant(),
            ["padding"] = Math.Round(s.Padding, 4),
            ["cornerRadius"] = Math.Round(s.CornerRadius, 4),
            ["blur"] = Math.Round(s.Blur, 4),
        };
        switch (s.Kind)
        {
            case BackdropKind.Preset:
                json["presetID"] = s.PresetId;
                break;
            case BackdropKind.Solid or BackdropKind.Gradient when s.Colors is { } colors:
                json["colors"] = new JsonArray(colors.Select(c => (JsonNode)new JsonArray(Math.Round(c.R, 4), Math.Round(c.G, 4), Math.Round(c.B, 4))).ToArray());
                break;
            case BackdropKind.Image:
                json["imagePath"] = s.ImagePath;
                break;
        }

        return json;
    }

    /// <summary>Empty string for none, as on macOS.</summary>
    public static string Encode(BackdropStyle style) =>
        style.Sanitized() is { Kind: BackdropKind.None, CornerRadius: 0 } ? string.Empty : ToJson(style).ToJsonString();

    public static BackdropStyle Decode(string? json) => FromJson(Parse(json));

    public static BackdropStyle FromJson(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var nested))
        {
            node = Parse(nested);
        }

        if (node is not JsonObject obj)
        {
            return BackdropStyle.None;
        }

        var kind = ParseKind(Get(obj, "kind"));
        List<RgbColor>? colors = null;
        if (Get(obj, "colors") is JsonArray array)
        {
            colors = [];
            foreach (var item in array)
            {
                if (item is JsonArray c && c.Count >= 3 && Num(c[0]) is { } r && Num(c[1]) is { } g && Num(c[2]) is { } b
                    && double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b))
                {
                    colors.Add(new RgbColor(r, g, b).Clamped());
                }
                else
                {
                    colors = null;
                    break;
                }
            }
        }

        return new BackdropStyle
        {
            Kind = kind,
            PresetId = Str(Get(obj, "presetID")),
            Colors = colors,
            ImagePath = Str(Get(obj, "imagePath")),
            Padding = Num(Get(obj, "padding")) ?? 0.5,
            CornerRadius = Num(Get(obj, "cornerRadius")) ?? 0,
            Blur = Num(Get(obj, "blur")) ?? 0,
        }.Sanitized();
    }

    public static string EncodePresets(IEnumerable<BackdropStyle> presets) =>
        new JsonArray(SanitizePresets(presets).Select(p => (JsonNode)ToJson(p)).ToArray()).ToJsonString();

    public static IReadOnlyList<BackdropStyle> DecodePresets(string? json)
    {
        var node = Parse(json);
        if (node is JsonValue v && v.TryGetValue<string>(out var nested))
        {
            node = Parse(nested);
        }

        return node is JsonArray array ? SanitizePresets(array.Select(FromJson)) : [];
    }

    /// <summary>Sanitized, none entries dropped, the last 12 kept.</summary>
    public static IReadOnlyList<BackdropStyle> SanitizePresets(IEnumerable<BackdropStyle> presets)
    {
        var list = presets.Select(p => p.Sanitized()).Where(p => p.HasBackdrop).ToList();
        return list.Count > BackdropPresets.MaxSavedPresets ? list[^BackdropPresets.MaxSavedPresets..] : list;
    }

    /// <summary>Adds the look (with neutral sliders) unless an identical one is saved; the oldest drop past 12.</summary>
    public static IReadOnlyList<BackdropStyle> AddPreset(IReadOnlyList<BackdropStyle> presets, BackdropStyle look)
    {
        var normalized = look.Sanitized().NormalizedForPreset();
        if (!normalized.HasBackdrop || presets.Any(p => p.SameLook(normalized)))
        {
            return presets;
        }

        return SanitizePresets(presets.Append(normalized));
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Get(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var exact))
        {
            return exact;
        }

        foreach (var (k, value) in obj)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static BackdropKind ParseKind(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s) && Enum.TryParse<BackdropKind>(s, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                return parsed;
            }

            if (v.TryGetValue<int>(out var i) && Enum.IsDefined((BackdropKind)i))
            {
                return (BackdropKind)i;
            }
        }

        return BackdropKind.None;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v)
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
