// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.ScreenshotEditor;

/// <summary>Resize handles, in the order the spec lists them.</summary>
public enum ResizeHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>Editor hit testing (spec 01 §6.8). Tolerances are image pixels scaled by the capture scale.</summary>
public static class HitTesting
{
    public static double Tolerance(double scale) => 10 * scale;

    public static double HandleTolerance(double scale) => 12 * scale;

    public static double CropHandleTolerance(double scale) => 14 * scale;

    public static double EndpointTolerance(double scale) => 12 * scale;

    public static double WordTolerance(double scale) => 2 * scale;

    /// <summary>
    /// Whether <paramref name="p"/> hits the annotation. For <paramref name="creationTap"/>, area shapes
    /// only count a ring near the edge, so their interior stays free for new text, stickers and counters.
    /// </summary>
    public static bool Hits(Annotation a, ImgPoint p, double scale, int imageWidth, int imageHeight, bool creationTap = false)
    {
        var tol = Tolerance(scale);
        switch (a.Kind)
        {
            case AnnotationKind.Arrow or AnnotationKind.Line:
                return DistanceToSegment(p, a.Start, a.End) <= tol + (StrokeWidths.Points(a.Stroke) * scale / 2);
            case AnnotationKind.Freehand:
                return a.Points.Any(q => ImgPoint.Distance(p, q) <= tol);
            case AnnotationKind.Counter:
                return ImgPoint.Distance(p, a.Rect.Center) <= (AnnotationMetrics.CounterDiameter(imageWidth, imageHeight) / 2) + (4 * scale);
            case AnnotationKind.Text or AnnotationKind.Sticker:
                return a.Rect.Inflate(tol / 2, tol / 2).Contains(p);
            default:
                if (!a.Rect.Inflate(tol / 2, tol / 2).Contains(p))
                {
                    return false;
                }

                if (!creationTap)
                {
                    return true;
                }

                var inner = a.Rect.Inset(tol, tol);
                return inner.IsEmpty || !inner.Contains(p);
        }
    }

    /// <summary>The topmost annotation under <paramref name="p"/> (reverse drawing order).</summary>
    public static Annotation? TopmostAt(IReadOnlyList<Annotation> annotations, ImgPoint p, double scale, int imageWidth, int imageHeight, bool creationTap = false)
    {
        for (var i = annotations.Count - 1; i >= 0; i--)
        {
            if (Hits(annotations[i], p, scale, imageWidth, imageHeight, creationTap))
            {
                return annotations[i];
            }
        }

        return null;
    }

    /// <summary>Distance to segment a→b; a degenerate segment means the distance to a.</summary>
    public static double DistanceToSegment(ImgPoint p, ImgPoint a, ImgPoint b)
    {
        var ab = b - a;
        var lengthSquared = (ab.X * ab.X) + (ab.Y * ab.Y);
        if (lengthSquared <= 0)
        {
            return ImgPoint.Distance(p, a);
        }

        var t = Math.Clamp((((p.X - a.X) * ab.X) + ((p.Y - a.Y) * ab.Y)) / lengthSquared, 0, 1);
        return ImgPoint.Distance(p, a + (ab * t));
    }

    /// <summary>Handle positions: TL, T, TR, R, BR, B, BL, L.</summary>
    public static ImgPoint HandlePoint(ImgRect r, ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => new(r.MinX, r.MinY),
        ResizeHandle.Top => new(r.MidX, r.MinY),
        ResizeHandle.TopRight => new(r.MaxX, r.MinY),
        ResizeHandle.Right => new(r.MaxX, r.MidY),
        ResizeHandle.BottomRight => new(r.MaxX, r.MaxY),
        ResizeHandle.Bottom => new(r.MidX, r.MaxY),
        ResizeHandle.BottomLeft => new(r.MinX, r.MaxY),
        _ => new(r.MinX, r.MidY),
    };

    public static IReadOnlyList<ResizeHandle> AllHandles { get; } = Enum.GetValues<ResizeHandle>();

    /// <summary>The first handle (in spec order) within a square of ±<paramref name="tolerance"/>.</summary>
    public static ResizeHandle? HandleAt(ImgRect r, ImgPoint p, double tolerance)
    {
        foreach (var handle in AllHandles)
        {
            var h = HandlePoint(r, handle);
            if (Math.Abs(h.X - p.X) <= tolerance && Math.Abs(h.Y - p.Y) <= tolerance)
            {
                return handle;
            }
        }

        return null;
    }

    /// <summary>Moves the edges the handle touches to <paramref name="p"/>, then normalizes so the rect flips instead of going negative.</summary>
    public static ImgRect Resize(ImgRect r, ResizeHandle handle, ImgPoint p)
    {
        double minX = r.MinX, minY = r.MinY, maxX = r.MaxX, maxY = r.MaxY;
        switch (handle)
        {
            case ResizeHandle.TopLeft: minX = p.X; minY = p.Y; break;
            case ResizeHandle.Top: minY = p.Y; break;
            case ResizeHandle.TopRight: maxX = p.X; minY = p.Y; break;
            case ResizeHandle.Right: maxX = p.X; break;
            case ResizeHandle.BottomRight: maxX = p.X; maxY = p.Y; break;
            case ResizeHandle.Bottom: maxY = p.Y; break;
            case ResizeHandle.BottomLeft: minX = p.X; maxY = p.Y; break;
            case ResizeHandle.Left: minX = p.X; break;
        }

        return ImgRect.FromEdges(minX, minY, maxX, maxY);
    }

    /// <summary>0 for the tail (start), 1 for the tip (end), or null; Euclidean distance below 12·scale.</summary>
    public static int? EndpointAt(Annotation a, ImgPoint p, double scale)
    {
        if (!a.IsSegment)
        {
            return null;
        }

        var tol = EndpointTolerance(scale);
        if (ImgPoint.Distance(p, a.Start) < tol)
        {
            return 0;
        }

        return ImgPoint.Distance(p, a.End) < tol ? 1 : null;
    }

    /// <summary>Whether a press lands on the selected annotation's body, a handle or an endpoint.</summary>
    public static bool OnSelection(Annotation a, ImgPoint p, double scale, int imageWidth, int imageHeight) =>
        Hits(a, p, scale, imageWidth, imageHeight)
        || (a.IsResizable && HandleAt(a.Rect, p, HandleTolerance(scale)) is not null)
        || EndpointAt(a, p, scale) is not null;
}

/// <summary>Crop draft math (spec 01 §3.10.12, §6.8).</summary>
public static class CropMath
{
    /// <summary>A crop needs at least 8 × 8 pixels.</summary>
    public const double MinimumSide = 8;

    /// <summary>Each edge rounded to the nearest whole pixel (not outward), then intersected with the image.</summary>
    public static ImgRect Snap(ImgRect r, ImgRect imageBounds)
    {
        var snapped = ImgRect.FromEdges(
            SpecMath.Round(r.MinX), SpecMath.Round(r.MinY), SpecMath.Round(r.MaxX), SpecMath.Round(r.MaxY));
        return snapped.Intersect(imageBounds);
    }

    /// <summary>Moves without resizing, stopping at the image edges; a rect larger than the bounds is intersected instead.</summary>
    public static ImgRect Move(ImgRect r, double dx, double dy, ImgRect bounds)
    {
        if (r.Width > bounds.Width || r.Height > bounds.Height)
        {
            return r.Offset(dx, dy).Intersect(bounds);
        }

        var x = Math.Clamp(r.MinX + dx, bounds.MinX, bounds.MaxX - r.Width);
        var y = Math.Clamp(r.MinY + dy, bounds.MinY, bounds.MaxY - r.Height);
        return new ImgRect(x, y, r.Width, r.Height);
    }

    /// <summary>A press starts a new crop rectangle when it is inside the image and either the draft is the whole image or the press is outside the draft.</summary>
    public static bool StartsNewSelection(ImgRect draft, ImgRect bounds, ImgPoint p) =>
        bounds.Contains(p) && (draft.NearlyEquals(bounds) || !draft.Contains(p));

    public static bool IsLargeEnough(ImgRect r) => r.Width >= MinimumSide && r.Height >= MinimumSide;

    /// <summary>The integer pixel rectangle of a snapped crop draft.</summary>
    public static (int X, int Y, int Width, int Height) PixelRect(ImgRect r)
    {
        var x = SpecMath.RoundToInt(r.MinX);
        var y = SpecMath.RoundToInt(r.MinY);
        return (x, y, SpecMath.RoundToInt(r.MaxX) - x, SpecMath.RoundToInt(r.MaxY) - y);
    }

    /// <summary>
    /// The crop loupe sample (spec 01 §6.3 <c>sampleRect</c> with <c>centredOnPixel = false</c>):
    /// an even side so the edge falls in the middle, clamped inside the image.
    /// </summary>
    public static (int X, int Y, int Width, int Height) LoupeSample(ImgPoint p, int imageWidth, int imageHeight, int side = 14)
    {
        side = Math.Max(1, side);
        if (side % 2 != 0)
        {
            side += 1;
        }

        var w = Math.Min(side, imageWidth);
        var h = Math.Min(side, imageHeight);
        var x = (int)Math.Floor(p.X - (w / 2.0));
        var y = (int)Math.Floor(p.Y - (h / 2.0));
        return (Math.Clamp(x, 0, imageWidth - w), Math.Clamp(y, 0, imageHeight - h), w, h);
    }
}

/// <summary>Editor window size and canvas zoom (spec 01 §6.5). Sizes are DIPs; content sizes image pixels.</summary>
public static class EditorLayoutMath
{
    public const double ChromeWidth = 96;
    public const double ChromeHeight = 140;
    public const double CanvasMargin = 14;
    public const double MinZoom = 0.05;
    public const double MaxZoom = 3;
    public const double ZoomStepIn = 1.25;
    public const double ZoomStepOut = 0.8;

    /// <summary>Minimum and maximum content size for a visible display area of <paramref name="vw"/> × <paramref name="vh"/>.</summary>
    public static (double MinW, double MinH, double MaxW, double MaxH) WindowLimits(double vw, double vh)
    {
        var minW = Math.Min(vw, Math.Min(980, Math.Max(760, 0.76 * vw)));
        var minH = Math.Min(vh, Math.Min(680, Math.Max(560, 0.72 * vh)));
        var maxW = Math.Max(minW, 0.90 * vw);
        var maxH = Math.Max(minH, 0.88 * vh);
        return (minW, minH, maxW, maxH);
    }

    /// <summary>Initial content size: the image (in points) plus chrome, between the limits.</summary>
    public static (double Width, double Height) InitialSize(double vw, double vh, double imagePointWidth, double imagePointHeight)
    {
        var (minW, minH, maxW, maxH) = WindowLimits(vw, vh);
        var fit = Math.Min(1, Math.Min((maxW - ChromeWidth) / Math.Max(1, imagePointWidth), (maxH - ChromeHeight) / Math.Max(1, imagePointHeight)));
        var w = Math.Clamp((imagePointWidth * fit) + ChromeWidth, minW, maxW);
        var h = Math.Clamp((imagePointHeight * fit) + ChromeHeight, minH, maxH);
        return (w, h);
    }

    /// <summary>Fit zoom: <c>min(1, (availW − 28)/contentW, (availH − 28)/contentH)</c>, each available side at least 80.</summary>
    public static double FitZoom(double availableWidth, double availableHeight, double contentPixelWidth, double contentPixelHeight)
    {
        var w = Math.Max(80, availableWidth - (2 * CanvasMargin));
        var h = Math.Max(80, availableHeight - (2 * CanvasMargin));
        return Math.Min(1, Math.Min(w / Math.Max(1, contentPixelWidth), h / Math.Max(1, contentPixelHeight)));
    }

    public static double ClampZoom(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : 1;

    /// <summary>Ctrl + wheel: <c>1 + clamp(deltaY, −24, 24) · 0.014</c>.</summary>
    public static double WheelFactor(double deltaY) => 1 + (Math.Clamp(deltaY, -24, 24) * 0.014);

    /// <summary>The percentage label: <c>round(zoom · scale · 100)</c>.</summary>
    public static int ZoomPercent(double zoom, double scale) => SpecMath.RoundToInt(zoom * scale * 100);

    /// <summary>"Actual size" shows one image pixel per device pixel.</summary>
    public static double ActualSizeZoom(double scale) => 1 / Math.Max(0.01, scale);

    /// <summary>Canvas view point → image point, with <paramref name="padding"/> the backdrop margin in image pixels.</summary>
    public static ImgPoint ViewToImage(double viewX, double viewY, double zoom, double padding) =>
        new((viewX / zoom) - padding, (viewY / zoom) - padding);
}
