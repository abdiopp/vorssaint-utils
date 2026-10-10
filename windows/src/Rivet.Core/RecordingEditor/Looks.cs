// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Core.Recording;

namespace Rivet.Core.RecordingEditor;

/// <summary>The three one-click looks (spec 02 §3.28). A look is only a set of values.</summary>
public enum RecorderLook
{
    /// <summary>"Original" (raw): no background, unsmoothed pointer, no ring, no zoom.</summary>
    Original,

    /// <summary>"Smooth" (clean): smoothed pointer, click ring, zooms.</summary>
    Smooth,

    /// <summary>"Studio": Smooth on the graphite background with margin and rounded corners.</summary>
    Studio,
}

public static class Looks
{
    public static RecorderBackdrop StudioBackdrop { get; } = new()
    {
        Kind = RecorderBackdropKind.Preset,
        PresetId = "graphite",
        Padding = 0.45,
        CornerRadius = 0.35,
        Blur = 0,
    };

    /// <summary>
    /// Applies a look's fields only; trim, cuts, captions, images, blurs, audio,
    /// quality and speed are untouched. Automatic zooms are restored afterwards
    /// by <see cref="RestoreAutomaticZooms"/>.
    /// </summary>
    public static EditDocument Apply(RecorderLook look, EditDocument doc) => look switch
    {
        RecorderLook.Original => doc with
        {
            Backdrop = string.Empty,
            Aspect = CanvasAspect.Original,
            ShowsPointer = true,
            PointerSmoothing = PointerSmoothing.Off,
            ShowsClickRing = false,
            ZoomEnabled = false,
        },
        RecorderLook.Smooth => doc with
        {
            Backdrop = string.Empty,
            Aspect = CanvasAspect.Original,
            ShowsPointer = true,
            PointerSmoothing = PointerSmoothing.Smooth,
            ShowsClickRing = true,
            ZoomEnabled = true,
        },
        _ => doc with
        {
            Backdrop = StudioBackdrop.ToJson(),
            ShowsPointer = true,
            PointerSmoothing = PointerSmoothing.Smooth,
            ShowsClickRing = true,
            ZoomEnabled = true,
        },
    };

    /// <summary>A look card is selected when the document's look fields equal its values.</summary>
    public static bool Matches(RecorderLook look, EditDocument doc)
    {
        var backdrop = doc.BackdropStyle;
        return look switch
        {
            RecorderLook.Original => !backdrop.HasBackdrop && doc.Aspect == CanvasAspect.Original && doc.ShowsPointer
                                     && doc.PointerSmoothing == PointerSmoothing.Off && !doc.ShowsClickRing && !doc.ZoomEnabled,
            RecorderLook.Smooth => !backdrop.HasBackdrop && doc.Aspect == CanvasAspect.Original && doc.ShowsPointer
                                   && doc.PointerSmoothing == PointerSmoothing.Smooth && doc.ShowsClickRing && doc.ZoomEnabled,
            _ => backdrop.SameStyle(StudioBackdrop) && doc.ShowsPointer
                 && doc.PointerSmoothing == PointerSmoothing.Smooth && doc.ShowsClickRing && doc.ZoomEnabled,
        };
    }

    /// <summary>Zoom on and the lane empty → regenerate from the clicks (and typing when on).</summary>
    public static EditDocument RestoreAutomaticZooms(EditDocument doc, PointerTrack pointer, TypingTrack typing, double duration)
    {
        if (!doc.ZoomEnabled || doc.ZoomSegments.Count > 0 || pointer.IsEmpty)
        {
            return doc;
        }

        return doc with
        {
            ZoomSegments = AutoZoom.Generate(pointer.Clicks, doc.ZoomsOnTyping ? typing.Times : null, duration, doc.ZoomAmount),
            ZoomsGenerated = true,
        };
    }
}

/// <summary>
/// A user edit preset (spec 02 §5.7): the look fields plus image overlays.
/// Images live in a private store; settings backups never carry their paths.
/// </summary>
public sealed record EditPreset
{
    public const int MaxCount = 12;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Backdrop { get; init; } = string.Empty;

    public CanvasAspect Aspect { get; init; } = CanvasAspect.Original;

    public bool ShowsPointer { get; init; } = true;

    public PointerSmoothing PointerSmoothing { get; init; } = PointerSmoothing.Smooth;

    public double PointerSize { get; init; } = 1;

    public bool ShowsClickRing { get; init; } = true;

    public bool ZoomEnabled { get; init; } = true;

    public double ZoomAmount { get; init; } = ZoomSegment.DefaultAmount;

    /// <summary>Null for legacy presets (and presets restored on another PC): applying leaves images alone.</summary>
    public IReadOnlyList<ImageOverlay>? Images { get; init; }

    public bool Equals(EditPreset? other) =>
        other is not null && Id == other.Id && Name == other.Name && Backdrop == other.Backdrop && Aspect == other.Aspect
        && ShowsPointer == other.ShowsPointer && PointerSmoothing == other.PointerSmoothing && PointerSize.Equals(other.PointerSize)
        && ShowsClickRing == other.ShowsClickRing && ZoomEnabled == other.ZoomEnabled && ZoomAmount.Equals(other.ZoomAmount)
        && (Images is null ? other.Images is null : other.Images is not null && Images.SequenceEqual(other.Images));

    public override int GetHashCode() => HashCode.Combine(Id, Name, Backdrop, Aspect);

    public static EditPreset Capture(string name, EditDocument doc, string? id = null) => new()
    {
        Id = id ?? EditDocument.NewId(),
        Name = name,
        Backdrop = doc.Backdrop,
        Aspect = doc.Aspect,
        ShowsPointer = doc.ShowsPointer,
        PointerSmoothing = doc.PointerSmoothing,
        PointerSize = doc.PointerSize,
        ShowsClickRing = doc.ShowsClickRing,
        ZoomEnabled = doc.ZoomEnabled,
        ZoomAmount = doc.ZoomAmount,
        Images = doc.Images,
    };

    /// <summary>The look fields onto a document (images are handled by the caller, who copies the files).</summary>
    public EditDocument ApplyLook(EditDocument doc) => doc with
    {
        Backdrop = RecorderBackdrop.Parse(Backdrop).ToDocumentString(),
        Aspect = Aspect,
        ShowsPointer = ShowsPointer,
        PointerSmoothing = PointerSmoothing,
        PointerSize = EditDocument.SanitizePointerSize(PointerSize),
        ShowsClickRing = ShowsClickRing,
        ZoomEnabled = ZoomEnabled,
        ZoomAmount = ZoomSegment.SanitizeAmount(ZoomAmount),
    };

    public EditPreset Sanitized() => this with
    {
        Name = Name.Trim(),
        Backdrop = RecorderBackdrop.Parse(Backdrop).ToDocumentString(),
        PointerSize = EditDocument.SanitizePointerSize(PointerSize),
        ZoomAmount = ZoomSegment.SanitizeAmount(ZoomAmount),
    };

    /// <summary>Names compare without case or diacritics ("Café" = "cafe").</summary>
    public static bool SameName(string a, string b) =>
        string.Compare(a.Trim(), b.Trim(), CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    /// <summary>Replace a same-named preset in place (keeping its id) or append; at most 12, oldest dropped.</summary>
    public static IReadOnlyList<EditPreset> Upsert(IReadOnlyList<EditPreset> presets, EditPreset preset)
    {
        var list = presets.ToList();
        var index = list.FindIndex(p => SameName(p.Name, preset.Name));
        if (index >= 0)
        {
            list[index] = preset with { Id = list[index].Id };
        }
        else
        {
            list.Add(preset);
        }

        while (list.Count > MaxCount)
        {
            list.RemoveAt(0);
        }

        return list;
    }

    /// <summary>Portable JSON for the <c>recorderEditorPresets</c> setting: never any image paths.</summary>
    public static string SerializeList(IEnumerable<EditPreset> presets)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var p in presets)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("name", p.Name);
                w.WriteString("backdrop", p.Backdrop);
                w.WriteString("aspect", EditNames.Of(p.Aspect));
                w.WriteBoolean("showsPointer", p.ShowsPointer);
                w.WriteString("pointerSmoothing", EditNames.Of(p.PointerSmoothing));
                w.WriteNumber("pointerSize", p.PointerSize);
                w.WriteBoolean("showsClickRing", p.ShowsClickRing);
                w.WriteBoolean("zoomEnabled", p.ZoomEnabled);
                w.WriteNumber("zoomAmount", p.ZoomAmount);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads presets from a JSON array (or a string holding one). Images come
    /// from the machine-local map; an <c>images</c> key inside the portable
    /// value (macOS layout) is honoured too.
    /// </summary>
    public static IReadOnlyList<EditPreset> ParseList(JsonNode? node, IReadOnlyDictionary<string, IReadOnlyList<ImageOverlay>>? localImages = null)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return [];
            }
        }

        if (node is not JsonArray array)
        {
            return [];
        }

        var result = new List<EditPreset>();
        foreach (var item in array.OfType<JsonObject>())
        {
            var id = EditDocument.Str(item, "id");
            var name = EditDocument.Str(item, "name")?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            IReadOnlyList<ImageOverlay>? images = EditDocument.ReadImages(item, "images");
            if (localImages is not null && localImages.TryGetValue(id, out var local))
            {
                images = local;
            }

            result.Add(new EditPreset
            {
                Id = id,
                Name = name,
                Backdrop = EditDocument.Str(item, "backdrop") ?? string.Empty,
                Aspect = EditNames.Aspect(EditDocument.Str(item, "aspect")) ?? CanvasAspect.Original,
                ShowsPointer = EditDocument.Bool(item, "showsPointer", true),
                PointerSmoothing = EditNames.Smoothing(EditDocument.Str(item, "pointerSmoothing")) ?? PointerSmoothing.Smooth,
                PointerSize = EditDocument.Num(item, "pointerSize", 1),
                ShowsClickRing = EditDocument.Bool(item, "showsClickRing", true),
                ZoomEnabled = EditDocument.Bool(item, "zoomEnabled", true),
                ZoomAmount = EditDocument.Num(item, "zoomAmount", ZoomSegment.DefaultAmount),
                Images = images,
            }.Sanitized());
        }

        return result.TakeLast(MaxCount).ToList();
    }

    /// <summary>The machine-local image map: <c>{"&lt;presetId&gt;":[ImageOverlay…]}</c>.</summary>
    public static string SerializeImageMap(IEnumerable<EditPreset> presets)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var p in presets.Where(p => p.Images is not null))
            {
                w.WriteStartArray(p.Id);
                foreach (var image in p.Images!)
                {
                    EditDocument.WriteImage(w, image);
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<ImageOverlay>> ParseImageMap(string? json)
    {
        var result = new Dictionary<string, IReadOnlyList<ImageOverlay>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
            {
                foreach (var (id, _) in obj)
                {
                    if (EditDocument.ReadImages(obj, id) is { } images)
                    {
                        result[id] = images.Where(i => EditDocument.IsAbsolutePath(i.Path)).ToList();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }
}
