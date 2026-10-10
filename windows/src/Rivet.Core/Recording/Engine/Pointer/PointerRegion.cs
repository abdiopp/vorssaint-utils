// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Recording.Engine.Pointer;

/// <summary>Maps a screen position (physical virtual-screen pixels) to normalized picture coordinates (0…1, top-left, may leave the range).</summary>
public interface IPointerRegion
{
    (double X, double Y) Normalize(PixelPoint point);
}

/// <summary>Area and display recordings: the region chosen at start.</summary>
public sealed class FixedPointerRegion(PixelRect region) : IPointerRegion
{
    public PixelRect Region { get; } = region;

    public (double X, double Y) Normalize(PixelPoint point) =>
        (
            (point.X - Region.X) / (double)Math.Max(1, Region.Width),
            (point.Y - Region.Y) / (double)Math.Max(1, Region.Height));
}

/// <summary>
/// Window recordings: the window's current position with each sample, mapped
/// with the same fit the video uses, so moving or resizing the window keeps the
/// drawn pointer on its content. Of the window's two rectangles (full and
/// visible frame) the one whose size matches the captured content is used,
/// because Windows 10 and 11 differ in whether captures include the invisible
/// resize borders.
/// </summary>
public sealed class WindowPointerRegion : IPointerRegion
{
    private readonly IRecorderSystem _system;
    private readonly nint _window;
    private readonly Func<(int Width, int Height)> _contentSize;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private PixelRect _last;

    public WindowPointerRegion(IRecorderSystem system, nint window, Func<(int Width, int Height)> contentSize, int outputWidth, int outputHeight, PixelRect initial)
    {
        _system = system;
        _window = window;
        _contentSize = contentSize;
        _outputWidth = Math.Max(1, outputWidth);
        _outputHeight = Math.Max(1, outputHeight);
        _last = initial;
    }

    public (double X, double Y) Normalize(PixelPoint point)
    {
        if (_system.GetWindowRects(_window) is { } rects)
        {
            _last = Choose(rects, _contentSize());
        }

        return WindowFit.Normalize(point.X, point.Y, _last, _outputWidth, _outputHeight);
    }

    /// <summary>The rectangle whose size is closest to the captured content (the frame bounds on a tie).</summary>
    public static PixelRect Choose(WindowRects rects, (int Width, int Height) content)
    {
        if (content.Width <= 0 || content.Height <= 0)
        {
            return rects.Frame.IsEmpty ? rects.Window : rects.Frame;
        }

        static int Distance(PixelRect r, (int Width, int Height) c) => Math.Abs(r.Width - c.Width) + Math.Abs(r.Height - c.Height);
        if (rects.Frame.IsEmpty)
        {
            return rects.Window;
        }

        return Distance(rects.Window, content) < Distance(rects.Frame, content) ? rects.Window : rects.Frame;
    }
}
