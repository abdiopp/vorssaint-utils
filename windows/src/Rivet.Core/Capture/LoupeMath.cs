// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Capture;

/// <summary>
/// The pixel magnifier's math (spec 01 §6.3). Zoom is "source pixels per 13":
/// the sample is an odd square of <c>odd(13 / zoom)</c> pixels (minimum 3) so
/// there is always a true centre pixel. Sizes are in DIPs unless noted.
/// </summary>
public static class LoupeMath
{
    public const int Base = 13;
    public const int MinSide = 3;
    public const double MinZoom = 0.5;
    public const double MaxZoom = Base / (double)MinSide;
    public const double Frame = 132;
    public const double InfoHeight = 24;
    public const double InfoGap = 6;
    public const double Gap = 16;
    public const double Inset = 8;
    public const double FastFactor = 1.15;
    public const int PlainWheelSteps = 3;
    public const double CopiedFeedbackSeconds = 1.4;

    public static readonly double[] DefaultZooms = [0.5, 1, 2, 4];

    public static double BlockHeight => Frame + InfoGap + InfoHeight;

    public static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : 1;

    public static double SanitizeZoom(double zoom) => Clamp(zoom);

    /// <summary>Odd sample side for a zoom: 27 at 0.5×, 13 at 1×, 7 at 2×, 3 at 4× and above.</summary>
    public static int SampleSide(double zoom) => Math.Max(MinSide, (2 * (int)Math.Floor(Base / Clamp(zoom) / 2)) + 1);

    /// <summary>Grid lines between source pixels appear once cells are at least 6 DIPs.</summary>
    public static bool GridVisible(int sampleSide) => sampleSide > 0 && Frame / sampleSide >= 6;

    /// <summary>Fast wheel zoom: ×1.15 per event, three times for a notched (non-continuous) wheel.</summary>
    public static double FastZoom(double zoom, double delta, bool continuous)
    {
        if (delta == 0)
        {
            return Clamp(zoom);
        }

        var factor = delta > 0 ? FastFactor : 1 / FastFactor;
        var steps = continuous ? 1 : PlainWheelSteps;
        var z = Clamp(zoom);
        for (var i = 0; i < steps; i++)
        {
            z = Clamp(z * factor);
        }

        return z;
    }

    /// <summary>Stepped wheel zoom: every notch changes the sample side by exactly 2 px, reversibly.</summary>
    public static double SteppedZoom(double zoom, double delta)
    {
        if (delta == 0)
        {
            return Clamp(zoom);
        }

        var side = SampleSide(zoom);
        var widest = SampleSide(MinZoom);
        var target = delta > 0 ? Math.Max(MinSide, side - 2) : Math.Min(widest, side + 2);
        if (target == side)
        {
            return Clamp(zoom);
        }

        return target == widest ? MinZoom : Clamp(Base / (double)target);
    }

    /// <summary>Stepped is the default when the setting says so; Alt swaps the mode while held.</summary>
    public static bool UseStepped(bool steppedByDefault, bool altHeld) => steppedByDefault ^ altHeld;

    public static double InitialZoom(bool rememberLast, double lastZoom, double defaultZoom) =>
        Clamp(rememberLast ? lastZoom : defaultZoom);

    /// <summary>
    /// The source square around <paramref name="point"/> (image pixels). The
    /// capture loupe centres an odd square on a pixel; the crop loupe centres an
    /// even square on an edge. Near the edges the square slides inward.
    /// </summary>
    public static PixelRect SampleRect(PointD point, int imageWidth, int imageHeight, int side, bool centredOnPixel = true)
    {
        side = Math.Max(1, side);
        var even = side % 2 == 0;
        if (centredOnPixel && even)
        {
            side -= 1;
        }
        else if (!centredOnPixel && !even)
        {
            side += 1;
        }

        side = Math.Max(1, side);
        var w = Math.Min(side, imageWidth);
        var h = Math.Min(side, imageHeight);
        var x = centredOnPixel ? (int)Math.Floor(point.X) - (w / 2) : (int)Math.Floor(point.X - (w / 2.0));
        var y = centredOnPixel ? (int)Math.Floor(point.Y) - (h / 2) : (int)Math.Floor(point.Y - (h / 2.0));
        return new PixelRect(Math.Clamp(x, 0, Math.Max(0, imageWidth - w)), Math.Clamp(y, 0, Math.Max(0, imageHeight - h)), w, h);
    }

    /// <summary>The pixel the picker reads for a point (floored and clamped to the image).</summary>
    public static PixelPoint TargetPixel(PointD point, int imageWidth, int imageHeight) =>
        new(Math.Clamp((int)Math.Floor(point.X), 0, Math.Max(0, imageWidth - 1)), Math.Clamp((int)Math.Floor(point.Y), 0, Math.Max(0, imageHeight - 1)));

    /// <summary>The target pixel's cell inside the magnifier frame (frame units).</summary>
    public static RectD TargetCell(PixelPoint target, PixelRect source, RectD frame)
    {
        var col = Math.Clamp(target.X, source.X, source.Right - 1);
        var row = Math.Clamp(target.Y, source.Y, source.Bottom - 1);
        var cellW = frame.Width / source.Width;
        var cellH = frame.Height / source.Height;
        return new RectD(frame.X + ((col - source.X) * cellW), frame.Y + ((row - source.Y) * cellH), cellW, cellH);
    }

    /// <summary>
    /// Top-left of the loupe block (magnifier plus info bar) in display-local
    /// units: above-right of the pointer, flipped left/below near edges, then
    /// clamped inside the display. All values in the same unit as <paramref name="displaySize"/>.
    /// </summary>
    public static PointD BlockOrigin(PointD pointer, double displayWidth, double displayHeight, double unit = 1)
    {
        var blockW = Frame * unit;
        var blockH = BlockHeight * unit;
        var gap = Gap * unit;
        var inset = Inset * unit;
        var x = pointer.X + gap;
        var y = pointer.Y - blockH - gap;
        if (x + blockW > displayWidth - inset)
        {
            x = pointer.X - blockW - gap;
        }

        if (y < inset)
        {
            y = pointer.Y + gap;
        }

        x = Math.Clamp(x, inset, Math.Max(inset, displayWidth - blockW - inset));
        y = Math.Clamp(y, inset, Math.Max(inset, displayHeight - blockH - inset));
        return new PointD(x, y);
    }

    /// <summary>Arrow-key nudge: one device pixel (ten with Shift).</summary>
    public static int NudgePixels(bool shift) => shift ? 10 : 1;
}
