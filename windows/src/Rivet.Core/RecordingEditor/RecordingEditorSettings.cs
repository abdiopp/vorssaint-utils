// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Core.Settings;

namespace Rivet.Core.RecordingEditor;

/// <summary>
/// The settings the recording editor reads and writes (spec 02 §4). Keys,
/// types and defaults match the macOS app. The recorder's Settings page and
/// the recording engine own the user-facing rows for most of them; defining
/// the same key with the same type in two modules is harmless.
/// </summary>
public static class RecordingEditorSettings
{
    /// <summary>Initial export quality of new takes.</summary>
    public static readonly Setting<string> Quality =
        new("recorderQuality", "balanced", Sanitize.OneOfStrings("balanced", "small", "balanced", "high"));

    /// <summary>Record system audio; also the initial "keep system sound" of new takes.</summary>
    public static readonly Setting<bool> SystemAudio = new("recorderSystemAudio", true);

    /// <summary>New takes start with automatic zooms.</summary>
    public static readonly Setting<bool> AutomaticZoom = new("recorderAutomaticZoom", true);

    /// <summary>GIF long edge for new takes: small (420), medium (600), large (800).</summary>
    public static readonly Setting<string> GifSize =
        new("recorderGIFSize", "medium", Sanitize.OneOfStrings("medium", "small", "medium", "large"));

    /// <summary>GIF frame rate for new takes: 8, 12 or 15.</summary>
    public static readonly Setting<int> GifFrameRate =
        new("recorderGIFFrameRate", 12, Sanitize.OneOf(12, 8, 12, 15));

    /// <summary>Save destination; "" means the default folder. Machine-local (a path).</summary>
    public static readonly Setting<string> SaveFolder = new("recorderSaveFolder", string.Empty, machineState: true);

    /// <summary>User edit presets (JSON array, at most 12). Portable: image paths never travel.</summary>
    public static readonly Setting<string> EditorPresets = new("recorderEditorPresets", "[]");

    /// <summary>
    /// Windows-only, machine-local: each preset's image overlays (paths into
    /// the private preset image store). Kept apart from the portable presets so
    /// a settings backup carries the presets without their images (§3.28).
    /// </summary>
    public static readonly Setting<string> EditorPresetImages = new("recorderEditorPresetImages", "{}", machineState: true);

    /// <summary>Saved custom backgrounds, shared with the screenshot editor (JSON array, at most 12).</summary>
    public const string BackdropPresetsKey = "screenshotBackdropPresets";

    public static ExportQuality QualityValue(ISettingsStore settings) =>
        EditNames.Quality(settings.Get(Quality)) ?? ExportQuality.Balanced;

    public static GifSize GifSizeValue(ISettingsStore settings) =>
        EditNames.Gif(settings.Get(GifSize)) ?? RecordingEditor.GifSize.Medium;

    /// <summary>A new take's document seeded from Settings (§3.20.2).</summary>
    public static EditDocument NewTakeDocument(ISettingsStore settings) =>
        EditDocument.NewTake(
            QualityValue(settings),
            settings.Get(SystemAudio),
            GifSizeValue(settings),
            settings.Get(GifFrameRate),
            settings.Get(AutomaticZoom));

    // ── Edit presets ─────────────────────────────────────────────────

    public static IReadOnlyList<EditPreset> ReadPresets(ISettingsStore settings) =>
        EditPreset.ParseList(
            settings.GetRaw(EditorPresets.Key) ?? JsonValue.Create(EditorPresets.Default),
            EditPreset.ParseImageMap(settings.Get(EditorPresetImages)));

    public static void WritePresets(ISettingsStore settings, IReadOnlyList<EditPreset> presets)
    {
        var list = presets.TakeLast(EditPreset.MaxCount).ToList();
        settings.Set(EditorPresets, EditPreset.SerializeList(list));
        settings.Set(EditorPresetImages, EditPreset.SerializeImageMap(list));
    }

    // ── Shared custom backgrounds ────────────────────────────────────

    /// <summary>Saved custom backgrounds; tolerant of a JSON string or an array, drops none entries, last 12.</summary>
    public static IReadOnlyList<RecorderBackdrop> ReadBackdropPresets(ISettingsStore settings)
    {
        var node = settings.GetRaw(BackdropPresetsKey);
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

        return array.Select(RecorderBackdrop.FromNode)
            .OfType<RecorderBackdrop>()
            .Select(b => b.Sanitized())
            .Where(b => b.HasBackdrop)
            .TakeLast(12)
            .ToList();
    }

    /// <summary>Writes the list in the form already stored (string by default, as on macOS).</summary>
    public static void WriteBackdropPresets(ISettingsStore settings, IReadOnlyList<RecorderBackdrop> presets)
    {
        var array = new JsonArray();
        foreach (var preset in presets.Where(p => p.Sanitized().HasBackdrop).TakeLast(12))
        {
            array.Add(preset.ToNode());
        }

        var existing = settings.GetRaw(BackdropPresetsKey);
        settings.SetRaw(BackdropPresetsKey, existing is JsonArray ? array : JsonValue.Create(array.ToJsonString()));
    }

    /// <summary>Normalizes, de-duplicates by look and keeps the newest 12 (§3.29).</summary>
    public static IReadOnlyList<RecorderBackdrop> AddBackdropPreset(IReadOnlyList<RecorderBackdrop> presets, RecorderBackdrop style)
    {
        var normalized = style.Sanitized().NormalizedForPreset();
        if (!normalized.HasBackdrop || presets.Any(p => p.SameLook(normalized)))
        {
            return presets;
        }

        return presets.Append(normalized).TakeLast(12).ToList();
    }
}
