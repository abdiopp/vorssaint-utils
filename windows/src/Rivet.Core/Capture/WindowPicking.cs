// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Capture;

/// <summary>An on-screen top-level window as the window enumerator reports it.</summary>
public sealed record CaptureWindowInfo
{
    public required nint Handle { get; init; }

    /// <summary>Visible frame (DWM extended frame bounds), physical pixels.</summary>
    public required PixelRect Bounds { get; init; }

    public string Title { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    /// <summary>The owner window (dialogs are owned by their main window), 0 when none.</summary>
    public nint Owner { get; init; }

    /// <summary>Belongs to this app.</summary>
    public bool IsOwnProcess { get; init; }

    /// <summary>
    /// One of this app's workflow surfaces (overlays, HUDs, preview), which
    /// carry capture exclusion permanently and are never picked.
    /// </summary>
    public bool IsProtected { get; init; }

    /// <summary>DPI scale of the monitor showing most of the window.</summary>
    public double Scale { get; init; } = 1;
}

/// <summary>Window list policy for picking a window (spec 01 §3.4.5, §6.22).</summary>
public static class WindowPicking
{
    /// <summary>Windows smaller than this (DIPs, either side) are not pickable.</summary>
    public const double MinimumSideDip = 40;

    /// <summary>
    /// Pickable windows, front to back: big enough, not decorations (focus
    /// borders of other apps framing a neighbour), and not ours when our
    /// windows are hidden, nor one of our protected surfaces.
    /// </summary>
    public static List<CaptureWindowInfo> Pickable(IReadOnlyList<CaptureWindowInfo> frontToBack, bool hideOwnWindows, ISet<nint>? alsoProtected = null)
    {
        var result = new List<CaptureWindowInfo>(frontToBack.Count);
        for (var i = 0; i < frontToBack.Count; i++)
        {
            var w = frontToBack[i];
            if (w.Bounds.Width < MinimumSideDip * w.Scale || w.Bounds.Height < MinimumSideDip * w.Scale)
            {
                continue;
            }

            if (w.IsOwnProcess && (hideOwnWindows || w.IsProtected || (alsoProtected?.Contains(w.Handle) ?? false)))
            {
                continue;
            }

            if (IsDecoration(frontToBack, i))
            {
                continue;
            }

            result.Add(w);
        }

        return result;
    }

    /// <summary>
    /// An untitled window next to another in z-order, from a different
    /// process, whose four margins around that neighbour all lie within
    /// 1…32 DIPs and differ from each other by at most 1 DIP.
    /// </summary>
    public static bool IsDecoration(IReadOnlyList<CaptureWindowInfo> frontToBack, int index)
    {
        var w = frontToBack[index];
        if (!string.IsNullOrEmpty(w.Title))
        {
            return false;
        }

        foreach (var neighbour in new[] { index - 1, index + 1 })
        {
            if (neighbour < 0 || neighbour >= frontToBack.Count)
            {
                continue;
            }

            var x = frontToBack[neighbour];
            if (x.ProcessId == w.ProcessId)
            {
                continue;
            }

            var scale = Math.Max(1, w.Scale);
            double[] margins =
            [
                (x.Bounds.X - w.Bounds.X) / scale,
                (x.Bounds.Y - w.Bounds.Y) / scale,
                (w.Bounds.Right - x.Bounds.Right) / scale,
                (w.Bounds.Bottom - x.Bounds.Bottom) / scale,
            ];
            if (margins.All(m => m >= 1 && m <= 32) && margins.Max() - margins.Min() <= 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first window (front to back) containing the point.</summary>
    public static CaptureWindowInfo? HitTest(IReadOnlyList<CaptureWindowInfo> frontToBack, PixelPoint point) =>
        frontToBack.FirstOrDefault(w => w.Bounds.Contains(point));

    /// <summary>
    /// Attached sheets and dialogs of <paramref name="target"/>: windows in
    /// front of it, of the same process (or owned by it), lying entirely within
    /// its frame. Returned back to front, the order to draw them over the target.
    /// </summary>
    public static List<CaptureWindowInfo> AttachedWindows(IReadOnlyList<CaptureWindowInfo> frontToBack, CaptureWindowInfo target)
    {
        var index = -1;
        for (var i = 0; i < frontToBack.Count; i++)
        {
            if (frontToBack[i].Handle == target.Handle)
            {
                index = i;
                break;
            }
        }

        if (index <= 0)
        {
            return [];
        }

        var attached = new List<CaptureWindowInfo>();
        for (var i = index - 1; i >= 0; i--)
        {
            var w = frontToBack[i];
            var related = w.ProcessId == target.ProcessId || w.Owner == target.Handle;
            if (related && Contains(target.Bounds, w.Bounds) && w.Handle != target.Handle)
            {
                attached.Add(w);
            }
        }

        return attached;
    }

    private static bool Contains(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}

/// <summary>Clipboard image scale (spec 01 §6.23).</summary>
public static class ClipboardImageScale
{
    /// <summary>Pixels per DIP from an image's logical and pixel sizes; 1 unless both axes agree within 5 % and land in 0.5…4.</summary>
    public static double Infer(double pixelWidth, double pixelHeight, double logicalWidth, double logicalHeight)
    {
        if (!(pixelWidth > 0 && pixelHeight > 0 && logicalWidth > 0 && logicalHeight > 0)
            || !double.IsFinite(pixelWidth) || !double.IsFinite(pixelHeight) || !double.IsFinite(logicalWidth) || !double.IsFinite(logicalHeight))
        {
            return 1;
        }

        var h = pixelWidth / logicalWidth;
        var v = pixelHeight / logicalHeight;
        if (Math.Abs(h - v) > 0.05 * Math.Max(h, v))
        {
            return 1;
        }

        var scale = (h + v) / 2;
        return scale is >= 0.5 and <= 4 ? scale : 1;
    }

    /// <summary>Scale from an image's DPI (96 DPI = 1×); invalid or out-of-range values mean null.</summary>
    public static double? FromDpi(double dpi)
    {
        if (!double.IsFinite(dpi) || dpi <= 0)
        {
            return null;
        }

        // PNG stores whole pixels per metre: round away the conversion error (191.9986 DPI is 2×).
        var scale = Math.Round(dpi / 96.0, 2);
        return scale is >= 0.5 and <= 4 ? scale : null;
    }
}

/// <summary>The countdown ring's progress (spec 01 §6.24).</summary>
public static class CountdownRing
{
    public const double DrainSeconds = 0.92;
    public const double Start = 0.04;
    public const double Span = 0.92;

    /// <summary>1 at the start of each second, emptying to 0 after 0.92 s; driven by time so late frames catch up.</summary>
    public static double Progress(double elapsedSeconds) => 1 - Math.Clamp(elapsedSeconds / DrainSeconds, 0, 1);

    /// <summary>Arc start and end as fractions of the circle (12 o'clock = 0).</summary>
    public static (double From, double To) Arc(double progress) => (Start, Start + (Span * Math.Clamp(progress, 0, 1)));
}
