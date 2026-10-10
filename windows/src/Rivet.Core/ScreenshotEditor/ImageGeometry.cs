// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace Rivet.Core.ScreenshotEditor;

/// <summary>A point in image space: top-left origin, measured in pixels of the captured image.</summary>
public readonly record struct ImgPoint(double X, double Y)
{
    public static ImgPoint operator +(ImgPoint a, ImgPoint b) => new(a.X + b.X, a.Y + b.Y);

    public static ImgPoint operator -(ImgPoint a, ImgPoint b) => new(a.X - b.X, a.Y - b.Y);

    public static ImgPoint operator *(ImgPoint a, double k) => new(a.X * k, a.Y * k);

    [JsonIgnore]
    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public static double Distance(ImgPoint a, ImgPoint b) => (a - b).Length;

    public static ImgPoint Mid(ImgPoint a, ImgPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    public ImgPoint Offset(double dx, double dy) => new(X + dx, Y + dy);
}

/// <summary>
/// A rectangle in image space. The helpers follow CoreGraphics semantics,
/// which the macOS constants were tuned against: <see cref="Contains"/> is
/// half-open, <see cref="Inset"/> with negative values grows, and an inset
/// that would turn the size negative yields an empty rectangle.
/// </summary>
public readonly record struct ImgRect(double X, double Y, double Width, double Height)
{
    public static ImgRect Empty => default;

    [JsonIgnore]
    public double MinX => X;

    [JsonIgnore]
    public double MinY => Y;

    [JsonIgnore]
    public double MaxX => X + Width;

    [JsonIgnore]
    public double MaxY => Y + Height;

    [JsonIgnore]
    public double MidX => X + (Width / 2);

    [JsonIgnore]
    public double MidY => Y + (Height / 2);

    [JsonIgnore]
    public ImgPoint Center => new(MidX, MidY);

    [JsonIgnore]
    public ImgPoint Origin => new(X, Y);

    [JsonIgnore]
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The rectangle spanned by two corners, in any order.</summary>
    public static ImgRect FromPoints(ImgPoint a, ImgPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static ImgRect FromEdges(double minX, double minY, double maxX, double maxY) =>
        FromPoints(new ImgPoint(minX, minY), new ImgPoint(maxX, maxY));

    /// <summary>Half-open containment, like <c>CGRect.contains</c>.</summary>
    public bool Contains(ImgPoint p) => p.X >= MinX && p.X < MaxX && p.Y >= MinY && p.Y < MaxY;

    /// <summary>Shrinks by (dx, dy) on every side; negative values grow. Empty when it would invert.</summary>
    public ImgRect Inset(double dx, double dy)
    {
        var w = Width - (2 * dx);
        var h = Height - (2 * dy);
        return w < 0 || h < 0 ? Empty : new ImgRect(X + dx, Y + dy, w, h);
    }

    public ImgRect Inflate(double dx, double dy) => Inset(-dx, -dy);

    public ImgRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>Positive-area overlap only (touching edges do not intersect).</summary>
    public bool Intersects(ImgRect other) =>
        other.MinX < MaxX && other.MaxX > MinX && other.MinY < MaxY && other.MaxY > MinY;

    public ImgRect Intersect(ImgRect other)
    {
        var x1 = Math.Max(MinX, other.MinX);
        var y1 = Math.Max(MinY, other.MinY);
        var x2 = Math.Min(MaxX, other.MaxX);
        var y2 = Math.Min(MaxY, other.MaxY);
        return x2 > x1 && y2 > y1 ? new ImgRect(x1, y1, x2 - x1, y2 - y1) : Empty;
    }

    /// <summary>The smallest rectangle containing both (zero-size rectangles count as points).</summary>
    public ImgRect Union(ImgRect other)
    {
        var x1 = Math.Min(MinX, other.MinX);
        var y1 = Math.Min(MinY, other.MinY);
        return new ImgRect(x1, y1, Math.Max(MaxX, other.MaxX) - x1, Math.Max(MaxY, other.MaxY) - y1);
    }

    /// <summary>Rounds outward to whole pixels (floor the min edges, ceil the max edges), like <c>CGRect.integral</c>.</summary>
    public ImgRect Integral()
    {
        var x1 = Math.Floor(MinX);
        var y1 = Math.Floor(MinY);
        return new ImgRect(x1, y1, Math.Ceiling(MaxX) - x1, Math.Ceiling(MaxY) - y1);
    }

    /// <summary>Same size, moved by the smallest amount that puts it inside <paramref name="bounds"/>.</summary>
    public ImgRect MovedInside(ImgRect bounds)
    {
        var x = Math.Clamp(X, bounds.MinX, Math.Max(bounds.MinX, bounds.MaxX - Width));
        var y = Math.Clamp(Y, bounds.MinY, Math.Max(bounds.MinY, bounds.MaxY - Height));
        return new ImgRect(x, y, Width, Height);
    }

    public static ImgRect Square(ImgPoint center, double side) =>
        new(center.X - (side / 2), center.Y - (side / 2), side, side);

    /// <summary>Equal within <paramref name="epsilon"/> on every edge.</summary>
    public bool NearlyEquals(ImgRect other, double epsilon = 1e-6) =>
        Math.Abs(X - other.X) <= epsilon && Math.Abs(Y - other.Y) <= epsilon
        && Math.Abs(Width - other.Width) <= epsilon && Math.Abs(Height - other.Height) <= epsilon;
}

/// <summary>Path verbs shared by the arrow and pen geometry; the renderer turns them into a Skia path.</summary>
public enum PathVerb
{
    Move,
    Line,
    Quad,

    /// <summary>A circular arc around <see cref="PathCommand.Point"/> (centre) from <see cref="PathCommand.StartAngle"/>, sweeping <see cref="PathCommand.Sweep"/> radians (positive = clockwise on screen).</summary>
    Arc,
    Close,
}

/// <summary>One path command. For quads, <see cref="Control"/> is the control point and <see cref="Point"/> the end.</summary>
public readonly record struct PathCommand(PathVerb Verb, ImgPoint Point, ImgPoint Control = default, double Radius = 0, double StartAngle = 0, double Sweep = 0);

/// <summary>A device-independent vector path in image space.</summary>
public sealed class VectorPath
{
    private readonly List<PathCommand> _commands = [];

    public IReadOnlyList<PathCommand> Commands => _commands;

    public bool IsEmpty => _commands.Count == 0;

    public VectorPath MoveTo(ImgPoint p)
    {
        _commands.Add(new PathCommand(PathVerb.Move, p));
        return this;
    }

    public VectorPath LineTo(ImgPoint p)
    {
        _commands.Add(new PathCommand(PathVerb.Line, p));
        return this;
    }

    public VectorPath QuadTo(ImgPoint control, ImgPoint end)
    {
        _commands.Add(new PathCommand(PathVerb.Quad, end, control));
        return this;
    }

    public VectorPath ArcAround(ImgPoint center, double radius, double startAngle, double sweep)
    {
        _commands.Add(new PathCommand(PathVerb.Arc, center, default, radius, startAngle, sweep));
        return this;
    }

    public VectorPath Close()
    {
        _commands.Add(new PathCommand(PathVerb.Close, default));
        return this;
    }

    /// <summary>Appends a polyline as its own subpath.</summary>
    public VectorPath Polyline(IReadOnlyList<ImgPoint> points, bool closed = false)
    {
        if (points.Count == 0)
        {
            return this;
        }

        MoveTo(points[0]);
        for (var i = 1; i < points.Count; i++)
        {
            LineTo(points[i]);
        }

        if (closed)
        {
            Close();
        }

        return this;
    }

    /// <summary>Every point the path visits (end points, controls and arc extremes), for bounds and tests.</summary>
    public IEnumerable<ImgPoint> SamplePoints()
    {
        foreach (var c in _commands)
        {
            switch (c.Verb)
            {
                case PathVerb.Move or PathVerb.Line:
                    yield return c.Point;
                    break;
                case PathVerb.Quad:
                    yield return c.Control;
                    yield return c.Point;
                    break;
                case PathVerb.Arc:
                    const int steps = 8;
                    for (var i = 0; i <= steps; i++)
                    {
                        var a = c.StartAngle + (c.Sweep * i / steps);
                        yield return new ImgPoint(c.Point.X + (c.Radius * Math.Cos(a)), c.Point.Y + (c.Radius * Math.Sin(a)));
                    }

                    break;
            }
        }
    }
}
