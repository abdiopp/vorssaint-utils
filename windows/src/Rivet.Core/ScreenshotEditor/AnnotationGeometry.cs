// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.ScreenshotEditor;

/// <summary>Rounding as the spec means it: half away from zero (not .NET's default banker's rounding).</summary>
public static class SpecMath
{
    public static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    public static int RoundToInt(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>The three points of an arrow head, its base midpoint and the shaft angle.</summary>
public readonly record struct ArrowHead(ImgPoint Left, ImgPoint Right, ImgPoint Base, double Length, double Angle);

/// <summary>
/// Arrow geometry (spec 01 §6.6). All values are image pixels, <c>w = stroke × scale</c>.
/// <code>
/// θ = atan2(tip − tail); dist = |tip − tail|
/// L = min(max(10, 3.4w), max(0.72·dist, min(1.2w, dist)));  φ = π/7
/// left = tip − L·(cos(θ−φ), sin(θ−φ)); right = tip − L·(cos(θ+φ), sin(θ+φ)); base = (left+right)/2
/// </code>
/// </summary>
public static class ArrowGeometry
{
    public const double HeadAngle = Math.PI / 7;

    /// <summary>Fixed seed of the scribbly sample in the style menu ("SCRIBBLY").</summary>
    public const ulong MenuSampleSeed = 0x5343524942424C59;

    public static double HeadLength(double width, double distance) =>
        Math.Min(Math.Max(10, 3.4 * width), Math.Max(0.72 * distance, Math.Min(1.2 * width, distance)));

    public static ArrowHead Head(ImgPoint tail, ImgPoint tip, double width)
    {
        var theta = Math.Atan2(tip.Y - tail.Y, tip.X - tail.X);
        var length = HeadLength(width, ImgPoint.Distance(tail, tip));
        var left = tip - (new ImgPoint(Math.Cos(theta - HeadAngle), Math.Sin(theta - HeadAngle)) * length);
        var right = tip - (new ImgPoint(Math.Cos(theta + HeadAngle), Math.Sin(theta + HeadAngle)) * length);
        return new ArrowHead(left, right, ImgPoint.Mid(left, right), length, theta);
    }

    /// <summary>Solid is filled; every other style is stroked with width <c>w</c>, round caps and joins.</summary>
    public static bool IsFilled(ArrowStyle style) => style == ArrowStyle.Filled;

    /// <summary>The whole arrow as one path, so a drop shadow is cast once and the head never shades its own shaft.</summary>
    public static VectorPath Path(ArrowStyle style, ImgPoint tail, ImgPoint tip, double width, ulong seed) => style switch
    {
        ArrowStyle.Outline => Outline(tail, tip, width),
        ArrowStyle.Open => Open(tail, tip, width),
        ArrowStyle.DoubleEnded => DoubleEnded(tail, tip, width),
        ArrowStyle.Scribbly => Scribbly(tail, tip, width, seed),
        _ => Solid(tail, tip, width),
    };

    /// <summary>
    /// Shaft, round tail cap and head as one closed contour (nonzero fill):
    /// tail+n → base+n → left → tip → right → base−n → tail−n → half circle behind the tail.
    /// </summary>
    public static VectorPath Solid(ImgPoint tail, ImgPoint tip, double width)
    {
        var head = Head(tail, tip, width);
        var theta = head.Angle;
        var n = new ImgPoint(-Math.Sin(theta), Math.Cos(theta)) * (width / 2);
        return new VectorPath()
            .MoveTo(tail + n)
            .LineTo(head.Base + n)
            .LineTo(head.Left)
            .LineTo(tip)
            .LineTo(head.Right)
            .LineTo(head.Base - n)
            .LineTo(tail - n)
            .ArcAround(tail, width / 2, theta - (Math.PI / 2), -Math.PI)
            .Close();
    }

    public static VectorPath Outline(ImgPoint tail, ImgPoint tip, double width)
    {
        var head = Head(tail, tip, width);
        return new VectorPath()
            .MoveTo(tail).LineTo(head.Base)
            .MoveTo(head.Left).LineTo(tip).LineTo(head.Right).LineTo(head.Left).Close();
    }

    public static VectorPath Open(ImgPoint tail, ImgPoint tip, double width)
    {
        var head = Head(tail, tip, width);
        return new VectorPath()
            .MoveTo(tail).LineTo(tip)
            .MoveTo(head.Left).LineTo(tip).LineTo(head.Right);
    }

    public static VectorPath DoubleEnded(ImgPoint tail, ImgPoint tip, double width)
    {
        var path = Open(tail, tip, width);
        var back = Head(tip, tail, width);
        return path.MoveTo(back.Left).LineTo(tail).LineTo(back.Right);
    }

    /// <summary>Hand-drawn wobble: the shaft and both wings as rough polylines, seeded per arrow.</summary>
    public static VectorPath Scribbly(ImgPoint tail, ImgPoint tip, double width, ulong seed)
    {
        var (shaft, leftWing, rightWing) = ScribblyPolylines(tail, tip, width, seed);
        return new VectorPath().Polyline(shaft).Polyline(leftWing).Polyline(rightWing);
    }

    /// <summary>The three scribbly polylines (shaft tail → base, wing left → tip, wing right → tip).</summary>
    public static (IReadOnlyList<ImgPoint> Shaft, IReadOnlyList<ImgPoint> LeftWing, IReadOnlyList<ImgPoint> RightWing)
        ScribblyPolylines(ImgPoint tail, ImgPoint tip, double width, ulong seed)
    {
        var head = Head(tail, tip, width);
        var dist = ImgPoint.Distance(tail, tip);
        var dir = new ImgPoint(Math.Cos(head.Angle), Math.Sin(head.Angle));
        var perp = new ImgPoint(-Math.Sin(head.Angle), Math.Cos(head.Angle));
        var rng = new ScribbleRandom(seed);

        var shaftSteps = Math.Clamp((int)Math.Ceiling(dist / Math.Max(10, 3 * width)), 4, 24);
        var shaftWobble = Math.Min(Math.Max(1, 0.35 * width), 0.025 * dist);
        var shaft = Rough(tail, head.Base, shaftSteps, shaftWobble, ref rng, dir, perp);

        var wingWobble = Math.Min(Math.Max(0.8, 0.22 * width), 0.035 * dist);
        var left = Rough(head.Left, tip, 3, wingWobble, ref rng);
        var right = Rough(head.Right, tip, 3, wingWobble, ref rng);
        return (shaft, left, right);
    }

    /// <summary>
    /// <c>rough(a, b, n, wobble)</c>: n+1 points from a to b; interior points are pushed sideways by
    /// <c>rng·wobble·sin(πp)</c> and forward by <c>rng·wobble·0.28·sin(πp)</c> (side drawn first).
    /// </summary>
    public static List<ImgPoint> Rough(ImgPoint a, ImgPoint b, int steps, double wobble, ref ScribbleRandom rng, ImgPoint? dir = null, ImgPoint? perp = null)
    {
        var segment = b - a;
        var length = segment.Length;
        var d = dir ?? (length > 0 ? segment * (1 / length) : new ImgPoint(1, 0));
        var normal = perp ?? new ImgPoint(-d.Y, d.X);
        var points = new List<ImgPoint>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var p = (double)i / steps;
            var basePoint = a + (segment * p);
            if (i == 0 || i == steps)
            {
                points.Add(basePoint);
                continue;
            }

            var envelope = Math.Sin(Math.PI * p);
            var side = rng.Next() * wobble * envelope;
            var forward = rng.Next() * wobble * 0.28 * envelope;
            points.Add(basePoint + (normal * side) + (d * forward));
        }

        return points;
    }
}

/// <summary>
/// The scribbly arrow's deterministic generator (a 64-bit LCG):
/// <c>state = state·2862933555777941757 + 3037000493 (mod 2^64)</c>, returning
/// <c>state / (2^64 − 1) · 2 − 1</c> in [−1, 1]. Seed 0 means 0x9E3779B97F4A7C15.
/// </summary>
public struct ScribbleRandom(ulong seed)
{
    private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public double Next()
    {
        unchecked
        {
            _state = (_state * 2862933555777941757UL) + 3037000493UL;
        }

        return (_state / (double)ulong.MaxValue * 2) - 1;
    }

    /// <summary>A fresh random seed for a new scribbly arrow.</summary>
    public static ulong NewSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        Random.Shared.NextBytes(bytes);
        var seed = BitConverter.ToUInt64(bytes);
        return seed == 0 ? 1 : seed;
    }
}

/// <summary>
/// Pen smoothing (spec 01 §6.7): quadratic curves through the midpoints, every sample kept.
/// <code>move(p0); for i in 1..&lt;n: quad(to: mid(p[i−1], p[i]), control: p[i−1]); line(to: p[n−1])</code>
/// </summary>
public static class PenGeometry
{
    public static VectorPath Smooth(IReadOnlyList<ImgPoint> points)
    {
        var path = new VectorPath();
        if (points.Count < 2)
        {
            return path;
        }

        path.MoveTo(points[0]);
        for (var i = 1; i < points.Count; i++)
        {
            path.QuadTo(points[i - 1], ImgPoint.Mid(points[i - 1], points[i]));
        }

        path.LineTo(points[^1]);
        return path;
    }
}

/// <summary>Measures annotation text with the same engine that draws it (system semibold, size in pixels).</summary>
public interface ITextMeasurer
{
    (double Width, double Height) Measure(string text, double fontSizePixels);
}

/// <summary>Counter, sticker and text box metrics (spec 01 §6.9), in image pixels.</summary>
public static class AnnotationMetrics
{
    /// <summary><c>d = max(22, min(W, H) / 24)</c> (not multiplied by the scale).</summary>
    public static double CounterDiameter(int imageWidth, int imageHeight) => Math.Max(22, Math.Min(imageWidth, imageHeight) / 24.0);

    public static double CounterRingWidth(double diameter) => Math.Max(1.5, 0.05 * diameter);

    public static double CounterFontSize(double diameter) => 0.52 * diameter;

    /// <summary><c>minimum = min(52·scale, 0.45·short); side = max(1, max(minimum, min(0.16·short, 128·scale)))</c>.</summary>
    public static double StickerSide(int imageWidth, int imageHeight, double scale)
    {
        double shortSide = Math.Min(imageWidth, imageHeight);
        var minimum = Math.Min(52 * scale, 0.45 * shortSide);
        return Math.Max(1, Math.Max(minimum, Math.Min(0.16 * shortSide, 128 * scale)));
    }

    /// <summary>A square centred on the tap, shrunk to fit and moved inside the image.</summary>
    public static ImgRect StickerRect(ImgPoint center, double side, ImgRect bounds)
    {
        var s = Math.Min(Math.Max(1, side), Math.Min(bounds.Width, bounds.Height));
        return ImgRect.Square(center, s).MovedInside(bounds);
    }

    public static double StickerFontSize(ImgRect rect, double scale) => Math.Max(10 * scale, 0.82 * Math.Min(rect.Width, rect.Height));

    public static double TextFontPixels(int textSize, double scale) => TextSizes.Sanitize(textSize) * scale;

    /// <summary><c>rect = (origin, ceil(m.w) + 4, ceil(m.h))</c>, measuring a space for empty text.</summary>
    public static ImgRect TextBounds(string text, ImgPoint origin, int textSize, double scale, ITextMeasurer measurer)
    {
        var (w, h) = measurer.Measure(text.Length == 0 ? " " : text, TextFontPixels(textSize, scale));
        return new ImgRect(origin.X, origin.Y, Math.Ceiling(w) + 4, Math.Ceiling(h));
    }

    /// <summary>Text is drawn 2 px right of the box origin.</summary>
    public static ImgPoint TextDrawOrigin(ImgRect box) => new(box.MinX + 2, box.MinY);

    /// <summary>White labels, except near-black on white circles.</summary>
    public static Rgb CounterLabelColor(AnnotationColor fill) =>
        fill == AnnotationColor.White ? new Rgb(0.09, 0.09, 0.11) : new Rgb(1, 1, 1);
}
