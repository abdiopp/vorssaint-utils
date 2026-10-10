// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace Rivet.Core.ScreenshotEditor;

public enum AnnotationKind
{
    Arrow,
    Line,
    Rect,
    Ellipse,
    Freehand,
    Highlight,
    Redact,
    Blur,
    Text,
    Sticker,
    Counter,
}

/// <summary>
/// One mark on the capture. Immutable: every edit produces a new value, so an
/// undo snapshot is simply the list of annotations at that moment. All
/// geometry is in image pixels (top-left origin). Each kind uses a subset of
/// the fields:
/// <list type="bullet">
/// <item>arrow, line: <see cref="Start"/> (tail) → <see cref="End"/> (tip), colour, stroke; arrows add style and seed</item>
/// <item>freehand: <see cref="Points"/>, colour, stroke</item>
/// <item>rect, ellipse: <see cref="Rect"/>, colour, stroke</item>
/// <item>highlight, redact: <see cref="Rect"/>, colour</item>
/// <item>blur: <see cref="Rect"/>, blur style, level, text-only</item>
/// <item>text: <see cref="Rect"/> (text box), text, colour, text size</item>
/// <item>sticker: <see cref="Rect"/> (square), sticker</item>
/// <item>counter: zero-size <see cref="Rect"/> at the centre, colour, number</item>
/// </list>
/// </summary>
public sealed record Annotation
{
    /// <summary>Identity across snapshots (selection survives undo of an unrelated change).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public required AnnotationKind Kind { get; init; }

    public ImgPoint Start { get; init; }

    public ImgPoint End { get; init; }

    public IReadOnlyList<ImgPoint> Points { get; init; } = [];

    public ImgRect Rect { get; init; }

    public AnnotationColor Color { get; init; } = AnnotationColor.Red;

    public StrokeWidth Stroke { get; init; } = StrokeWidth.Medium;

    /// <summary>Points at 1x; multiplied by the capture scale for pixels.</summary>
    public int TextSize { get; init; } = TextSizes.Default;

    public string Text { get; init; } = string.Empty;

    public ArrowStyle ArrowStyle { get; init; } = ArrowStyle.Filled;

    /// <summary>Random seed of a scribbly arrow, so redraws and exports are stable.</summary>
    public ulong Seed { get; init; }

    public BlurStyle BlurStyle { get; init; } = BlurStyle.Pixelate;

    public int BlurLevel { get; init; } = BlurStyles.DefaultLevel;

    public bool TextOnly { get; init; }

    public StickerKind Sticker { get; init; } = StickerKind.Check;

    /// <summary>Counter label, always 1…n in drawing order.</summary>
    public int Number { get; init; }

    [JsonIgnore]
    public bool UsesColor => Kind is not (AnnotationKind.Sticker or AnnotationKind.Blur);

    /// <summary>Thickness has a visible effect only on stroked marks.</summary>
    [JsonIgnore]
    public bool UsesStroke => Kind is AnnotationKind.Arrow or AnnotationKind.Line or AnnotationKind.Rect
        or AnnotationKind.Ellipse or AnnotationKind.Freehand;

    [JsonIgnore]
    public bool IsSegment => Kind is AnnotationKind.Arrow or AnnotationKind.Line;

    /// <summary>Rectangle-like marks whose interior stays free for creation taps.</summary>
    [JsonIgnore]
    public bool IsAreaShape => Kind is AnnotationKind.Rect or AnnotationKind.Ellipse or AnnotationKind.Highlight
        or AnnotationKind.Blur or AnnotationKind.Redact;

    /// <summary>Marks with eight resize handles.</summary>
    [JsonIgnore]
    public bool IsResizable => IsAreaShape || Kind == AnnotationKind.Sticker;

    /// <summary>The mark moved by (dx, dy): rectangle, endpoints or every pen point.</summary>
    public Annotation Translated(double dx, double dy) => Kind switch
    {
        AnnotationKind.Arrow or AnnotationKind.Line => this with { Start = Start.Offset(dx, dy), End = End.Offset(dx, dy) },
        AnnotationKind.Freehand => this with { Points = Points.Select(p => p.Offset(dx, dy)).ToArray() },
        _ => this with { Rect = Rect.Offset(dx, dy) },
    };

    /// <summary>
    /// What the selection box surrounds: the segment's span, the pen points'
    /// bounding box (the macOS app drew a tiny box at the origin here), the
    /// counter's circle, or the stored rectangle.
    /// </summary>
    public ImgRect Bounds(int imageWidth, int imageHeight) => Kind switch
    {
        AnnotationKind.Arrow or AnnotationKind.Line => ImgRect.FromPoints(Start, End),
        AnnotationKind.Freehand => PointsBounds(Points),
        AnnotationKind.Counter => ImgRect.Square(Rect.Center, AnnotationMetrics.CounterDiameter(imageWidth, imageHeight)),
        _ => Rect,
    };

    public static ImgRect PointsBounds(IReadOnlyList<ImgPoint> points)
    {
        if (points.Count == 0)
        {
            return ImgRect.Empty;
        }

        double minX = points[0].X, minY = points[0].Y, maxX = minX, maxY = minY;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return new ImgRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Structural equality, comparing pen points element by element.</summary>
    public bool Equals(Annotation? other) =>
        other is not null
        && Id == other.Id && Kind == other.Kind && Start == other.Start && End == other.End
        && Rect == other.Rect && Color == other.Color && Stroke == other.Stroke && TextSize == other.TextSize
        && Text == other.Text && ArrowStyle == other.ArrowStyle && Seed == other.Seed && BlurStyle == other.BlurStyle
        && BlurLevel == other.BlurLevel && TextOnly == other.TextOnly && Sticker == other.Sticker && Number == other.Number
        && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Id, Kind, Rect, Start, End, Text, Points.Count);
}
