// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Capture;

/// <summary>What the routing pipeline decided about the quick preview.</summary>
public readonly record struct PreviewDecision(bool Show, TimeSpan? Timeout)
{
    /// <summary>A shown preview with no timer ("Until dismissed").</summary>
    public bool IsPersistent => Show && Timeout is null;
}

/// <summary>Quick preview rules (spec 01 §3.7 step 8, §3.9).</summary>
public static class PreviewPolicy
{
    public static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Preview size in DIPs.</summary>
    public const double Width = 350;
    public const double Height = 210;

    /// <summary>
    /// Ask each time → shown with the 12 s recovery timer; Edit → not shown;
    /// an automatic action that fully succeeded → the confirmation preview
    /// (if on) for its duration (0 = until dismissed); a failed or partial
    /// action → the recovery preview so the failed half can be retried.
    /// </summary>
    public static PreviewDecision Decide(ScreenshotDefaultAction action, bool fullySucceeded, bool confirmationEnabled, int confirmationSeconds)
    {
        switch (action)
        {
            case ScreenshotDefaultAction.Ask:
                return new PreviewDecision(true, RecoveryTimeout);
            case ScreenshotDefaultAction.Edit:
                return new PreviewDecision(false, null);
        }

        if (!fullySucceeded)
        {
            return new PreviewDecision(true, RecoveryTimeout);
        }

        if (!confirmationEnabled)
        {
            return new PreviewDecision(false, null);
        }

        return confirmationSeconds <= 0
            ? new PreviewDecision(true, null)
            : new PreviewDecision(true, TimeSpan.FromSeconds(confirmationSeconds));
    }

    /// <summary>A timed preview takes keyboard focus when the preference is on; a persistent one never does.</summary>
    public static bool TakesFocus(PreviewDecision decision, bool preference) => decision.Show && decision.Timeout is not null && preference;

    /// <summary>
    /// Where the preview goes, in physical pixels (§6.4). Fixed corners sit
    /// 16 DIPs inside the work area; Automatic is the bottom-right corner once
    /// an action already ran, otherwise beside the capture (14 DIP gap, 10 DIP
    /// inset), falling back to the pointer.
    /// </summary>
    public static PixelPoint Place(
        PreviewPosition position,
        bool actionRan,
        PixelRect anchor,
        PixelPoint pointer,
        PixelRect workArea,
        double scale,
        double widthDip = Width,
        double heightDip = Height)
    {
        var w = widthDip * scale;
        var h = heightDip * scale;
        if (position == PreviewPosition.Automatic && (actionRan || anchor.IsEmpty))
        {
            position = PreviewPosition.BottomRight;
        }

        if (position != PreviewPosition.Automatic)
        {
            var inset = 16 * scale;
            var minX = workArea.X + inset;
            var maxX = Math.Max(minX, workArea.Right - inset - w);
            var minY = workArea.Y + inset;
            var maxY = Math.Max(minY, workArea.Bottom - inset - h);
            var (x, y) = position switch
            {
                PreviewPosition.TopLeft => (minX, minY),
                PreviewPosition.TopRight => (maxX, minY),
                PreviewPosition.BottomLeft => (minX, maxY),
                _ => (maxX, maxY),
            };
            return new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        }

        var usableInset = 10 * scale;
        var gap = 14 * scale;
        var uMinX = workArea.X + usableInset;
        var uMaxX = workArea.Right - usableInset;
        var uMinY = workArea.Y + usableInset;
        var uMaxY = workArea.Bottom - usableInset;

        double px = anchor.Right + gap;
        if (px + w > uMaxX)
        {
            px = anchor.X - w - gap;
        }

        if (px < uMinX || px + w > uMaxX)
        {
            px = pointer.X + gap;
        }

        double py = anchor.Y + (anchor.Height / 2.0) - (h / 2);
        if (py < uMinY || py + h > uMaxY)
        {
            // Below the area, else above it, else centred on the pointer (top-left origin).
            if (anchor.Bottom + gap + h <= uMaxY)
            {
                py = anchor.Bottom + gap;
            }
            else if (anchor.Y - gap - h >= uMinY)
            {
                py = anchor.Y - gap - h;
            }
            else
            {
                py = pointer.Y - (h / 2);
            }
        }

        px = Math.Clamp(px, uMinX, Math.Max(uMinX, uMaxX - w));
        py = Math.Clamp(py, uMinY, Math.Max(uMinY, uMaxY - h));
        return new PixelPoint((int)Math.Round(px), (int)Math.Round(py));
    }
}
