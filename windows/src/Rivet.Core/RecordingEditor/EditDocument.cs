// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Core.RecordingEditor;

/// <summary>
/// The editor's whole state for one take: <c>edit.json</c> beside the master
/// (spec 02 §5.6). It is a value: undo keeps whole snapshots. Every key is
/// optional on read and falls back to its default, unknown keys are ignored,
/// and an unreadable file opens the recording untouched. All times are source
/// seconds, all rectangles and points are normalized with a top-left origin.
/// </summary>
public sealed record EditDocument
{
    public const string FileName = "edit.json";

    public double TrimStart { get; init; }

    /// <summary>0 means "to the end"; after <see cref="Sanitized"/> it holds the real end.</summary>
    public double TrimEnd { get; init; }

    public ExportQuality Quality { get; init; } = ExportQuality.Balanced;

    public double ExportSpeed { get; init; } = 1;

    public bool KeepsSystemAudio { get; init; } = true;

    public GifSize GifSize { get; init; } = GifSize.Medium;

    public int GifFrameRate { get; init; } = 12;

    /// <summary>JSON of a <see cref="RecorderBackdrop"/>; "" means none.</summary>
    public string Backdrop { get; init; } = string.Empty;

    public CanvasAspect Aspect { get; init; } = CanvasAspect.Original;

    public bool ShowsPointer { get; init; } = true;

    public PointerSmoothing PointerSmoothing { get; init; } = PointerSmoothing.Smooth;

    public double PointerSize { get; init; } = 1;

    public bool ShowsClickRing { get; init; } = true;

    public bool ZoomEnabled { get; init; } = true;

    public double ZoomAmount { get; init; } = ZoomSegment.DefaultAmount;

    public bool ZoomsOnTyping { get; init; }

    public IReadOnlyList<CutRange> Cuts { get; init; } = [];

    public IReadOnlyList<ZoomSegment> ZoomSegments { get; init; } = [];

    public bool ZoomsGenerated { get; init; }

    public IReadOnlyList<TextOverlay> Texts { get; init; } = [];

    public IReadOnlyList<ImageOverlay> Images { get; init; } = [];

    public IReadOnlyList<BlurRegion> Blurs { get; init; } = [];

    public bool KeepsMicrophone { get; init; } = true;

    public double SystemAudioGain { get; init; } = 1;

    public double MicrophoneGain { get; init; } = 1;

    public RecorderBackdrop BackdropStyle => RecorderBackdrop.Parse(Backdrop).Sanitized();

    /// <summary>A new take's document, seeded from Settings (§3.20.2).</summary>
    public static EditDocument NewTake(ExportQuality quality, bool keepsSystemAudio, GifSize gifSize, int gifFrameRate, bool zoomEnabled) => new()
    {
        Quality = quality,
        KeepsSystemAudio = keepsSystemAudio,
        GifSize = gifSize,
        GifFrameRate = SanitizeGifFrameRate(gifFrameRate),
        ZoomEnabled = zoomEnabled,
    };

    public static int SanitizeGifFrameRate(int fps) => fps is 8 or 12 or 15 ? fps : 12;

    public static double SanitizeGain(double gain) => RecorderMath.ClampOr(gain, 0, 1, 1);

    public static double SanitizePointerSize(double size) => RecorderMath.ClampOr(size, 0.5, 2.0, 1);

    // ── Change classification (§3.32) ─────────────────────────────────

    /// <summary>Background, shape, pointer, ring, zooms, cuts and overlays.</summary>
    public static bool AffectsPicture(EditDocument a, EditDocument b) =>
        a.Backdrop != b.Backdrop || a.Aspect != b.Aspect || a.ShowsPointer != b.ShowsPointer
        || a.PointerSmoothing != b.PointerSmoothing || a.PointerSize != b.PointerSize
        || a.ShowsClickRing != b.ShowsClickRing || a.ZoomEnabled != b.ZoomEnabled || a.ZoomAmount != b.ZoomAmount
        || !a.ZoomSegments.SequenceEqual(b.ZoomSegments) || !a.Cuts.SequenceEqual(b.Cuts)
        || !a.Texts.SequenceEqual(b.Texts) || !a.Images.SequenceEqual(b.Images) || !a.Blurs.SequenceEqual(b.Blurs);

    /// <summary>Trim and cuts.</summary>
    public static bool AffectsTiming(EditDocument a, EditDocument b) =>
        a.TrimStart != b.TrimStart || a.TrimEnd != b.TrimEnd || !a.Cuts.SequenceEqual(b.Cuts);

    /// <summary>Which tracks are kept and their gains.</summary>
    public static bool AffectsAudio(EditDocument a, EditDocument b) =>
        a.KeepsSystemAudio != b.KeepsSystemAudio || a.KeepsMicrophone != b.KeepsMicrophone
        || a.SystemAudioGain != b.SystemAudioGain || a.MicrophoneGain != b.MicrophoneGain;

    public bool Equals(EditDocument? other) =>
        other is not null
        && TrimStart.Equals(other.TrimStart) && TrimEnd.Equals(other.TrimEnd) && Quality == other.Quality
        && ExportSpeed.Equals(other.ExportSpeed) && KeepsSystemAudio == other.KeepsSystemAudio
        && GifSize == other.GifSize && GifFrameRate == other.GifFrameRate && ZoomsOnTyping == other.ZoomsOnTyping
        && ZoomsGenerated == other.ZoomsGenerated && KeepsMicrophone == other.KeepsMicrophone
        && SystemAudioGain.Equals(other.SystemAudioGain) && MicrophoneGain.Equals(other.MicrophoneGain)
        && !AffectsPicture(this, other);

    public override int GetHashCode() =>
        HashCode.Combine(TrimStart, TrimEnd, Backdrop, ZoomSegments.Count, Texts.Count, Images.Count, Blurs.Count, Cuts.Count);

    // ── Sanitizing (§5.6) ─────────────────────────────────────────────

    /// <summary>Every field within its range, against the recording's duration.</summary>
    public EditDocument Sanitized(double duration)
    {
        var d = double.IsFinite(duration) && duration > 0 ? duration : 0;
        var trim = EditTimeline.SanitizedTrim(TrimStart, TrimEnd, d);
        return this with
        {
            TrimStart = trim.Start,
            TrimEnd = trim.End,
            ExportSpeed = RecordingEditor.ExportSpeed.Sanitize(ExportSpeed),
            GifFrameRate = SanitizeGifFrameRate(GifFrameRate),
            Backdrop = RecorderBackdrop.Parse(Backdrop).ToDocumentString(),
            PointerSize = SanitizePointerSize(PointerSize),
            ZoomAmount = ZoomSegment.SanitizeAmount(ZoomAmount),
            Cuts = EditTimeline.NormalizeCuts(Cuts, d),
            ZoomSegments = SanitizeZooms(ZoomSegments, d),
            Texts = SanitizeTexts(Texts, d),
            Images = SanitizeImages(Images, d),
            Blurs = SanitizeBlurs(Blurs, d),
            SystemAudioGain = SanitizeGain(SystemAudioGain),
            MicrophoneGain = SanitizeGain(MicrophoneGain),
        };
    }

    /// <summary>Clamped, amount sanitized, focus 0…1, ≥ 0.4 s, sorted, overlaps resolved by moving the later start.</summary>
    public static IReadOnlyList<ZoomSegment> SanitizeZooms(IEnumerable<ZoomSegment> zooms, double duration)
    {
        var clamped = new List<ZoomSegment>();
        foreach (var z in zooms)
        {
            if (!double.IsFinite(z.Start) || !double.IsFinite(z.End))
            {
                continue;
            }

            var s = Math.Clamp(z.Start, 0, duration);
            var e = Math.Clamp(z.End, 0, duration);
            var aimed = z.FocusX is { } fx && z.FocusY is { } fy && double.IsFinite(fx) && double.IsFinite(fy);
            var item = z with
            {
                Id = ValidId(z.Id),
                Start = s,
                End = e,
                Amount = ZoomSegment.SanitizeAmount(z.Amount),
                FocusX = aimed ? RecorderMath.Clamp01(z.FocusX!.Value) : null,
                FocusY = aimed ? RecorderMath.Clamp01(z.FocusY!.Value) : null,
            };
            if (item.Length >= ZoomSegment.MinimumLength)
            {
                clamped.Add(item);
            }
        }

        clamped.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new List<ZoomSegment>();
        foreach (var z in clamped)
        {
            var item = z;
            if (result.Count > 0 && item.Start < result[^1].End)
            {
                item = item with { Start = result[^1].End };
            }

            if (item.Length >= ZoomSegment.MinimumLength)
            {
                result.Add(item);
            }
        }

        return result;
    }

    public static IReadOnlyList<TextOverlay> SanitizeTexts(IEnumerable<TextOverlay> texts, double duration) =>
        texts.Select(t => (Item: t, Range: ClampRange(t.Start, t.End, duration)))
            .Where(x => x.Range is not null)
            .Select(x => x.Item with
            {
                Id = ValidId(x.Item.Id),
                Text = Truncate(x.Item.Text ?? string.Empty, TextOverlay.MaxLength),
                Start = x.Range!.Value.Start,
                End = x.Range!.Value.End,
                Size = RecorderMath.ClampOr(x.Item.Size, TextOverlay.MinSize, TextOverlay.MaxSize, TextOverlay.DefaultSize),
            })
            .OrderBy(t => t.Start)
            .ToList();

    public static IReadOnlyList<ImageOverlay> SanitizeImages(IEnumerable<ImageOverlay> images, double duration) =>
        images.Where(i => IsAbsolutePath(i.Path))
            .Select(i => (Item: i, Range: ClampRange(i.Start, i.End, duration)))
            .Where(x => x.Range is not null)
            .Select(x => x.Item with
            {
                Id = ValidId(x.Item.Id),
                Start = x.Range!.Value.Start,
                End = x.Range!.Value.End,
                Size = RecorderMath.ClampOr(x.Item.Size, ImageOverlay.MinSize, ImageOverlay.MaxSize, ImageOverlay.DefaultSize),
                Opacity = RecorderMath.ClampOr(x.Item.Opacity, ImageOverlay.MinOpacity, 1, 1),
            })
            .OrderBy(i => i.Start)
            .ToList();

    public static IReadOnlyList<BlurRegion> SanitizeBlurs(IEnumerable<BlurRegion> blurs, double duration) =>
        blurs.Select(b => (Item: b, Range: ClampRange(b.Start, b.End, duration)))
            .Where(x => x.Range is not null)
            .Select(x => SanitizeRect(x.Item) with
            {
                Id = ValidId(x.Item.Id),
                Start = x.Range!.Value.Start,
                End = x.Range!.Value.End,
                Strength = Math.Clamp(x.Item.Strength, 1, 5),
            })
            .OrderBy(b => b.Start)
            .ToList();

    /// <summary>The rectangle clamped to the picture, each side at least 0.01.</summary>
    public static BlurRegion SanitizeRect(BlurRegion blur)
    {
        if (!double.IsFinite(blur.X) || !double.IsFinite(blur.Y) || !double.IsFinite(blur.Width) || !double.IsFinite(blur.Height))
        {
            return blur with { X = 0.35, Y = 0.40, Width = 0.30, Height = 0.20 };
        }

        var (x0, x1) = ClampSpan(blur.X, blur.X + blur.Width);
        var (y0, y1) = ClampSpan(blur.Y, blur.Y + blur.Height);
        return blur with { X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 };

        static (double, double) ClampSpan(double a, double b)
        {
            var lo = RecorderMath.Clamp01(Math.Min(a, b));
            var hi = RecorderMath.Clamp01(Math.Max(a, b));
            if (hi - lo < 0.01)
            {
                lo = Math.Min(lo, 0.99);
                hi = lo + 0.01;
            }

            return (lo, hi);
        }
    }

    /// <summary>Clamped to [0, D] and at least 0.2 s long; null when nothing is left.</summary>
    private static TimeRange? ClampRange(double start, double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end))
        {
            return null;
        }

        var s = Math.Clamp(start, 0, duration);
        var e = Math.Clamp(end, 0, duration);
        return e - s >= 0.2 ? new TimeRange(s, e) : null;
    }

    public static bool IsAbsolutePath(string? path) =>
        !string.IsNullOrEmpty(path) && !path.Contains('\0') && Path.IsPathFullyQualified(path);

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        // Do not split a surrogate pair.
        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut];
    }

    private static string ValidId(string? id) => string.IsNullOrWhiteSpace(id) ? NewId() : id;

    public static string NewId() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    // ── JSON (§5.6) ───────────────────────────────────────────────────

    /// <summary>Compact JSON with the macOS key names.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("trimStart", TrimStart);
            w.WriteNumber("trimEnd", TrimEnd);
            w.WriteString("quality", EditNames.Of(Quality));
            w.WriteNumber("exportSpeed", ExportSpeed);
            w.WriteBoolean("keepsSystemAudio", KeepsSystemAudio);
            w.WriteBoolean("keepsMicrophone", KeepsMicrophone);
            w.WriteNumber("systemAudioGain", SystemAudioGain);
            w.WriteNumber("microphoneGain", MicrophoneGain);
            w.WriteString("gifSize", EditNames.Of(GifSize));
            w.WriteNumber("gifFrameRate", GifFrameRate);
            w.WriteString("backdrop", Backdrop);
            w.WriteString("aspect", EditNames.Of(Aspect));
            w.WriteBoolean("showsPointer", ShowsPointer);
            w.WriteString("pointerSmoothing", EditNames.Of(PointerSmoothing));
            w.WriteNumber("pointerSize", PointerSize);
            w.WriteBoolean("showsClickRing", ShowsClickRing);
            w.WriteBoolean("zoomEnabled", ZoomEnabled);
            w.WriteNumber("zoomAmount", ZoomAmount);
            w.WriteBoolean("zoomsOnTyping", ZoomsOnTyping);
            w.WriteBoolean("zoomsGenerated", ZoomsGenerated);

            w.WriteStartArray("cuts");
            foreach (var c in Cuts)
            {
                w.WriteStartObject();
                w.WriteNumber("start", c.Start);
                w.WriteNumber("end", c.End);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("zoomSegments");
            foreach (var z in ZoomSegments)
            {
                w.WriteStartObject();
                w.WriteString("id", z.Id);
                w.WriteNumber("start", z.Start);
                w.WriteNumber("end", z.End);
                w.WriteNumber("amount", z.Amount);
                if (z.IsAimed)
                {
                    w.WriteNumber("focusX", z.FocusX!.Value);
                    w.WriteNumber("focusY", z.FocusY!.Value);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("texts");
            foreach (var t in Texts)
            {
                w.WriteStartObject();
                w.WriteString("id", t.Id);
                w.WriteString("text", t.Text);
                w.WriteNumber("start", t.Start);
                w.WriteNumber("end", t.End);
                w.WriteString("anchor", EditNames.Of(t.Anchor));
                w.WriteNumber("size", t.Size);
                w.WriteString("palette", EditNames.Of(t.Palette));
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("images");
            foreach (var i in Images)
            {
                WriteImage(w, i);
            }

            w.WriteEndArray();

            w.WriteStartArray("blurs");
            foreach (var b in Blurs)
            {
                w.WriteStartObject();
                w.WriteString("id", b.Id);
                w.WriteNumber("start", b.Start);
                w.WriteNumber("end", b.End);
                w.WriteNumber("x", b.X);
                w.WriteNumber("y", b.Y);
                w.WriteNumber("width", b.Width);
                w.WriteNumber("height", b.Height);
                w.WriteNumber("strength", b.Strength);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void WriteImage(Utf8JsonWriter w, ImageOverlay i)
    {
        w.WriteStartObject();
        w.WriteString("id", i.Id);
        w.WriteString("path", i.Path);
        w.WriteNumber("start", i.Start);
        w.WriteNumber("end", i.End);
        w.WriteString("anchor", EditNames.Of(i.Anchor));
        w.WriteNumber("size", i.Size);
        w.WriteNumber("opacity", i.Opacity);
        w.WriteEndObject();
    }

    /// <summary>Reads a document; null when the JSON is unreadable or not an object.</summary>
    public static EditDocument? FromJson(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject obj ? FromNode(obj) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Every key optional: a missing or mistyped value takes its default.</summary>
    public static EditDocument FromNode(JsonObject o)
    {
        var defaults = new EditDocument();
        return new EditDocument
        {
            TrimStart = Num(o, "trimStart", 0),
            TrimEnd = Num(o, "trimEnd", 0),
            Quality = EditNames.Quality(Str(o, "quality")) ?? defaults.Quality,
            ExportSpeed = Num(o, "exportSpeed", 1),
            KeepsSystemAudio = Bool(o, "keepsSystemAudio", true),
            GifSize = EditNames.Gif(Str(o, "gifSize")) ?? defaults.GifSize,
            GifFrameRate = (int)Num(o, "gifFrameRate", 12),
            Backdrop = Str(o, "backdrop") ?? string.Empty,
            Aspect = EditNames.Aspect(Str(o, "aspect")) ?? defaults.Aspect,
            ShowsPointer = Bool(o, "showsPointer", true),
            PointerSmoothing = EditNames.Smoothing(Str(o, "pointerSmoothing")) ?? defaults.PointerSmoothing,
            PointerSize = Num(o, "pointerSize", 1),
            ShowsClickRing = Bool(o, "showsClickRing", true),
            ZoomEnabled = Bool(o, "zoomEnabled", true),
            ZoomAmount = Num(o, "zoomAmount", ZoomSegment.DefaultAmount),
            ZoomsOnTyping = Bool(o, "zoomsOnTyping", false),
            Cuts = Items(o, "cuts", c => new CutRange(Num(c, "start", double.NaN), Num(c, "end", double.NaN))),
            ZoomSegments = Items(o, "zoomSegments", z => new ZoomSegment
            {
                Id = Str(z, "id") ?? NewId(),
                Start = Num(z, "start", double.NaN),
                End = Num(z, "end", double.NaN),
                Amount = Num(z, "amount", ZoomSegment.DefaultAmount),
                FocusX = z.ContainsKey("focusX") && z.ContainsKey("focusY") ? Num(z, "focusX", double.NaN) : null,
                FocusY = z.ContainsKey("focusX") && z.ContainsKey("focusY") ? Num(z, "focusY", double.NaN) : null,
            }),
            ZoomsGenerated = Bool(o, "zoomsGenerated", false),
            Texts = Items(o, "texts", t => new TextOverlay
            {
                Id = Str(t, "id") ?? NewId(),
                Text = Str(t, "text") ?? string.Empty,
                Start = Num(t, "start", double.NaN),
                End = Num(t, "end", double.NaN),
                Anchor = EditNames.Anchor(Str(t, "anchor")) ?? OverlayAnchor.Bottom,
                Size = Num(t, "size", TextOverlay.DefaultSize),
                Palette = EditNames.Palette(Str(t, "palette")) ?? CaptionPalette.White,
            }),
            Images = ReadImages(o, "images") ?? [],
            Blurs = Items(o, "blurs", b => new BlurRegion
            {
                Id = Str(b, "id") ?? NewId(),
                Start = Num(b, "start", double.NaN),
                End = Num(b, "end", double.NaN),
                X = Num(b, "x", double.NaN),
                Y = Num(b, "y", double.NaN),
                Width = Num(b, "width", double.NaN),
                Height = Num(b, "height", double.NaN),
                Strength = (int)RecorderMath.ClampOr(Num(b, "strength", BlurRegion.DefaultStrength), 1, 5, BlurRegion.DefaultStrength),
            }),
            KeepsMicrophone = Bool(o, "keepsMicrophone", true),
            SystemAudioGain = Num(o, "systemAudioGain", 1),
            MicrophoneGain = Num(o, "microphoneGain", 1),
        };
    }

    /// <summary>An image list, or null when the key is absent (legacy presets).</summary>
    internal static IReadOnlyList<ImageOverlay>? ReadImages(JsonObject o, string key)
    {
        if (!o.ContainsKey(key))
        {
            return null;
        }

        return Items(o, key, i => new ImageOverlay
        {
            Id = Str(i, "id") ?? NewId(),
            Path = Str(i, "path") ?? string.Empty,
            Start = Num(i, "start", double.NaN),
            End = Num(i, "end", double.NaN),
            Anchor = EditNames.Anchor(Str(i, "anchor")) ?? OverlayAnchor.BottomTrailing,
            Size = Num(i, "size", ImageOverlay.DefaultSize),
            Opacity = Num(i, "opacity", 1),
        });
    }

    internal static IReadOnlyList<T> Items<T>(JsonObject o, string key, Func<JsonObject, T> read)
    {
        if (!o.TryGetPropertyValue(key, out var node) || node is not JsonArray array)
        {
            return [];
        }

        var list = new List<T>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonObject obj)
            {
                list.Add(read(obj));
            }
        }

        return list;
    }

    internal static double Num(JsonObject o, string key, double fallback)
    {
        if (o.TryGetPropertyValue(key, out var node) && node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                return d;
            }
        }

        return fallback;
    }

    internal static bool Bool(JsonObject o, string key, bool fallback) =>
        o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    internal static string? Str(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ── Persistence (§5.6: atomic whole-file write on every committed change) ──

    /// <summary>Reads <c>edit.json</c> from a take folder; null when missing or unreadable.</summary>
    public static EditDocument? Read(string takeFolder)
    {
        var path = System.IO.Path.Combine(takeFolder, FileName);
        try
        {
            return File.Exists(path) ? FromJson(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes to a temporary sibling, then replaces the file.</summary>
    public void Write(string takeFolder)
    {
        var path = System.IO.Path.Combine(takeFolder, FileName);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, ToJson(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
