// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Modules.RadialMenu;

/// <summary>How a window layout action is applied.</summary>
public enum WindowLayoutApply
{
    /// <summary>Move/resize to <see cref="WindowLayoutResult.Bounds"/>.</summary>
    Bounds,

    /// <summary>Use the native maximized state.</summary>
    Maximize,

    /// <summary>Go back to the size before the last layout action (or un-maximize).</summary>
    Restore,
}

public readonly record struct WindowLayoutResult(WindowLayoutApply Apply, PixelRect Bounds);

/// <summary>
/// The 41 window layout actions a radial slice can run on the window that was
/// in front behind the wheel (spec 07 §3.2.9). The Window Layout feature itself
/// is not ported (Windows has Snap), so the radial menu computes these
/// rectangles itself; ids and labels match the macOS app.
/// </summary>
public static class WindowLayoutActions
{
    public static IReadOnlyList<string> All { get; } =
    [
        "leftHalf", "rightHalf", "topHalf", "bottomHalf", "centerHalf",
        "leftThird", "centerThird", "rightThird", "leftTwoThirds", "centerTwoThirds", "rightTwoThirds",
        "topThird", "middleThird", "bottomThird", "topTwoThirds", "bottomTwoThirds",
        "leftQuarter", "leftMiddleQuarter", "rightMiddleQuarter", "rightQuarter",
        "topQuarter", "upperMiddleQuarter", "lowerMiddleQuarter", "bottomQuarter",
        "topLeftSixth", "topCenterSixth", "topRightSixth", "bottomLeftSixth", "bottomCenterSixth", "bottomRightSixth",
        "topLeft", "topRight", "bottomLeft", "bottomRight",
        "maximize", "marginMaximize", "fullScreen", "center", "previousDisplay", "nextDisplay", "restore",
    ];

    private static readonly HashSet<string> Known = All.ToHashSet(StringComparer.Ordinal);

    public static bool IsKnown(string id) => Known.Contains(id);

    /// <summary>String key of the action's label (the macOS catalog uses the id itself).</summary>
    public static string TitleKey(string id) => $"windowLayout.{id}";

    /// <summary>
    /// The target for <paramref name="id"/>. <paramref name="work"/> is the work area of the
    /// window's monitor; <paramref name="screens"/> (work areas) are needed for the display moves.
    /// </summary>
    public static WindowLayoutResult? Compute(string id, PixelRect window, PixelRect work, IReadOnlyList<PixelRect> screens, int margin)
    {
        int x = work.X, y = work.Y, w = work.Width, h = work.Height;
        PixelRect R(double fx, double fy, double fw, double fh)
        {
            var left = x + (int)Math.Round(w * fx);
            var top = y + (int)Math.Round(h * fy);
            var right = x + (int)Math.Round(w * (fx + fw));
            var bottom = y + (int)Math.Round(h * (fy + fh));
            return new PixelRect(left, top, right - left, bottom - top);
        }

        const double T = 1.0 / 3;
        const double S = 1.0 / 6;
        PixelRect? rect = id switch
        {
            "leftHalf" => R(0, 0, 0.5, 1),
            "rightHalf" => R(0.5, 0, 0.5, 1),
            "topHalf" => R(0, 0, 1, 0.5),
            "bottomHalf" => R(0, 0.5, 1, 0.5),
            "centerHalf" => R(0.25, 0, 0.5, 1),
            "leftThird" => R(0, 0, T, 1),
            "centerThird" => R(T, 0, T, 1),
            "rightThird" => R(2 * T, 0, T, 1),
            "leftTwoThirds" => R(0, 0, 2 * T, 1),
            "centerTwoThirds" => R(S, 0, 2 * T, 1),
            "rightTwoThirds" => R(T, 0, 2 * T, 1),
            "topThird" => R(0, 0, 1, T),
            "middleThird" => R(0, T, 1, T),
            "bottomThird" => R(0, 2 * T, 1, T),
            "topTwoThirds" => R(0, 0, 1, 2 * T),
            "bottomTwoThirds" => R(0, T, 1, 2 * T),
            "leftQuarter" => R(0, 0, 0.25, 1),
            "leftMiddleQuarter" => R(0.25, 0, 0.25, 1),
            "rightMiddleQuarter" => R(0.5, 0, 0.25, 1),
            "rightQuarter" => R(0.75, 0, 0.25, 1),
            "topQuarter" => R(0, 0, 1, 0.25),
            "upperMiddleQuarter" => R(0, 0.25, 1, 0.25),
            "lowerMiddleQuarter" => R(0, 0.5, 1, 0.25),
            "bottomQuarter" => R(0, 0.75, 1, 0.25),
            "topLeftSixth" => R(0, 0, T, 0.5),
            "topCenterSixth" => R(T, 0, T, 0.5),
            "topRightSixth" => R(2 * T, 0, T, 0.5),
            "bottomLeftSixth" => R(0, 0.5, T, 0.5),
            "bottomCenterSixth" => R(T, 0.5, T, 0.5),
            "bottomRightSixth" => R(2 * T, 0.5, T, 0.5),
            "topLeft" => R(0, 0, 0.5, 0.5),
            "topRight" => R(0.5, 0, 0.5, 0.5),
            "bottomLeft" => R(0, 0.5, 0.5, 0.5),
            "bottomRight" => R(0.5, 0.5, 0.5, 0.5),
            "marginMaximize" => new PixelRect(x + margin, y + margin, Math.Max(1, w - (2 * margin)), Math.Max(1, h - (2 * margin))),
            "center" => new PixelRect(x + ((w - Math.Min(window.Width, w)) / 2), y + ((h - Math.Min(window.Height, h)) / 2), Math.Min(window.Width, w), Math.Min(window.Height, h)),
            _ => null,
        };

        if (rect is { } r)
        {
            return new WindowLayoutResult(WindowLayoutApply.Bounds, r);
        }

        return id switch
        {
            "maximize" or "fullScreen" => new WindowLayoutResult(WindowLayoutApply.Maximize, work),
            "restore" => new WindowLayoutResult(WindowLayoutApply.Restore, window),
            "nextDisplay" or "previousDisplay" => MoveToDisplay(window, work, screens, id == "nextDisplay"),
            _ => null,
        };
    }

    /// <summary>Moves the window to the next/previous monitor (ordered left to right, then top to bottom), keeping its relative place and size.</summary>
    private static WindowLayoutResult? MoveToDisplay(PixelRect window, PixelRect work, IReadOnlyList<PixelRect> screens, bool next)
    {
        var ordered = screens.OrderBy(s => s.X).ThenBy(s => s.Y).ToList();
        if (ordered.Count < 2)
        {
            return null;
        }

        var index = ordered.FindIndex(s => s == work);
        if (index < 0)
        {
            index = 0;
        }

        var target = ordered[(index + (next ? 1 : -1) + ordered.Count) % ordered.Count];
        var fx = work.Width == 0 ? 0 : (window.X - work.X) / (double)work.Width;
        var fy = work.Height == 0 ? 0 : (window.Y - work.Y) / (double)work.Height;
        var fw = work.Width == 0 ? 1 : window.Width / (double)work.Width;
        var fh = work.Height == 0 ? 1 : window.Height / (double)work.Height;
        var width = Math.Min(target.Width, (int)Math.Round(fw * target.Width));
        var height = Math.Min(target.Height, (int)Math.Round(fh * target.Height));
        var left = Math.Clamp(target.X + (int)Math.Round(fx * target.Width), target.X, target.Right - width);
        var top = Math.Clamp(target.Y + (int)Math.Round(fy * target.Height), target.Y, target.Bottom - height);
        return new WindowLayoutResult(WindowLayoutApply.Bounds, new PixelRect(left, top, width, height));
    }
}
