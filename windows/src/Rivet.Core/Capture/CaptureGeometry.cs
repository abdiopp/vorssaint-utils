// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Capture;

/// <summary>A point with fractional coordinates (physical pixels unless stated otherwise).</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);

    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);

    public PixelPoint Floor() => new((int)Math.Floor(X), (int)Math.Floor(Y));
}

/// <summary>A rectangle with fractional coordinates (physical pixels unless stated otherwise).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double MidX => X + (Width / 2);

    public double MidY => Y + (Height / 2);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD From(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    public bool Contains(PointD p) => p.X >= X && p.Y >= Y && p.X < Right && p.Y < Bottom;

    public RectD Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public RectD Inflate(double dx, double dy) => new(X - dx, Y - dy, Width + (2 * dx), Height + (2 * dy));

    public RectD Intersect(RectD other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(Right, other.Right);
        var y2 = Math.Min(Bottom, other.Bottom);
        return x2 > x1 && y2 > y1 ? new RectD(x1, y1, x2 - x1, y2 - y1) : default;
    }

    /// <summary>Integral rectangle: min edges floored, max edges ceiled (rounded outward).</summary>
    public PixelRect RoundOutward()
    {
        var x1 = (int)Math.Floor(X);
        var y1 = (int)Math.Floor(Y);
        var x2 = (int)Math.Ceiling(Right);
        var y2 = (int)Math.Ceiling(Bottom);
        return new PixelRect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }
}

/// <summary>Selection, mapping and region math of the capture selector (spec 01 §6.1, §6.2, §3.1).</summary>
public static class CaptureGeometry
{
    /// <summary>A press that moved less than this (in DIPs, both axes) is a click.</summary>
    public const double ClickThresholdDip = 4;

    /// <summary>Smaller selections (in DIPs) are ignored.</summary>
    public const double MinimumSelectionDip = 2;

    /// <summary>Minimum side of a geometry region (recording, scrolling), physical pixels.</summary>
    public const int MinimumRegionSide = 32;

    /// <summary>
    /// The drag rectangle (§6.2): Shift makes it square in the drag's direction,
    /// Alt grows it from the origin as centre, and the result is clamped to the display.
    /// </summary>
    public static RectD SelectionRect(PointD origin, PointD current, bool square, bool fromCenter, RectD bounds)
    {
        var dx = current.X - origin.X;
        var dy = current.Y - origin.Y;
        if (square)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = (dx < 0 ? -1 : 1) * side;
            dy = (dy < 0 ? -1 : 1) * side;
        }

        var rect = fromCenter
            ? new RectD(origin.X - Math.Abs(dx), origin.Y - Math.Abs(dy), 2 * Math.Abs(dx), 2 * Math.Abs(dy))
            : new RectD(Math.Min(origin.X, origin.X + dx), Math.Min(origin.Y, origin.Y + dy), Math.Abs(dx), Math.Abs(dy));
        return rect.Intersect(bounds);
    }

    /// <summary>True when a press/release pair is a click rather than a drag (§3.4.4).</summary>
    public static bool IsClick(PointD down, PointD up, double scale) =>
        Math.Abs(up.X - down.X) < ClickThresholdDip * scale && Math.Abs(up.Y - down.Y) < ClickThresholdDip * scale;

    public static bool IsTooSmall(RectD selection, double scale) =>
        selection.Width < MinimumSelectionDip * scale || selection.Height < MinimumSelectionDip * scale;

    /// <summary>
    /// A selection (global physical pixels) as a pixel rectangle inside a
    /// display image: rounded outward, clamped to the image (§6.1).
    /// </summary>
    public static PixelRect ToImageRect(RectD selection, PixelRect displayBounds, int imageWidth, int imageHeight)
    {
        // The frozen still has the display's native size; scale anyway in case they differ.
        var sx = imageWidth / (double)Math.Max(1, displayBounds.Width);
        var sy = imageHeight / (double)Math.Max(1, displayBounds.Height);
        var local = new RectD((selection.X - displayBounds.X) * sx, (selection.Y - displayBounds.Y) * sy, selection.Width * sx, selection.Height * sy);
        return local.RoundOutward().Intersect(new PixelRect(0, 0, imageWidth, imageHeight));
    }

    /// <summary>
    /// A region for recording and scrolling capture (§3.1): whole pixels, even
    /// width and height, at least <see cref="MinimumRegionSide"/> px, inside
    /// the display. A selection that runs off the display is shrunk, not shifted;
    /// one that is too small grows around its centre (staying inside the display).
    /// </summary>
    public static PixelRect SnapRegion(PixelRect rect, PixelRect display)
    {
        var r = rect.Intersect(display);
        if (r.IsEmpty)
        {
            r = new PixelRect(display.X, display.Y, Math.Min(MinimumRegionSide, display.Width), Math.Min(MinimumRegionSide, display.Height));
        }

        (var x, var w) = SnapAxis(r.X, r.Width, display.X, display.Width);
        (var y, var h) = SnapAxis(r.Y, r.Height, display.Y, display.Height);
        return new PixelRect(x, y, w, h);
    }

    private static (int Start, int Length) SnapAxis(int start, int length, int min, int extent)
    {
        var limit = extent - (extent % 2);
        if (length < MinimumRegionSide)
        {
            var grown = Math.Min(MinimumRegionSide, limit);
            var center = start + (length / 2.0);
            start = (int)Math.Round(center - (grown / 2.0));
            length = grown;
            start = Math.Clamp(start, min, min + extent - length);
        }

        if (length % 2 != 0)
        {
            length -= 1;
        }

        length = Math.Max(Math.Min(length, limit), Math.Min(2, limit));
        if (start + length > min + extent)
        {
            length = (min + extent - start) & ~1;
        }

        return (start, length);
    }

    /// <summary>The display holding the largest part of <paramref name="rect"/>, ties broken by <paramref name="pointer"/>.</summary>
    public static ScreenInfo? DisplayWithLargestOverlap(IReadOnlyList<ScreenInfo> screens, PixelRect rect, PixelPoint pointer)
    {
        if (rect.IsEmpty)
        {
            return null;
        }

        ScreenInfo? best = null;
        long bestArea = 0;
        foreach (var screen in screens)
        {
            var overlap = screen.Bounds.Intersect(rect);
            var area = (long)overlap.Width * overlap.Height;
            if (area <= 0)
            {
                continue;
            }

            if (area > bestArea || (area == bestArea && screen.Bounds.Contains(pointer)))
            {
                best = screen;
                bestArea = area;
            }
        }

        return best;
    }
}
