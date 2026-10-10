// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.RadialMenu;

/// <summary>What a click on the wheel window does, by distance from the centre.</summary>
public enum RadialClickAction
{
    Close,
    StepBack,
    RunHighlighted,
}

/// <summary>
/// The wheel's geometry and hit testing (spec 07 §3.2.6, §6.2). Angles are
/// measured from 12 o'clock, clockwise, with dyUp positive towards the top of
/// the screen (on Windows dyUp = centre.y − cursor.y). All lengths in DIPs.
/// </summary>
public static class RadialGeometry
{
    public const double WindowSize = 400;
    public const double DiscDiameter = 300;
    public const double ChipRingRadius = 112;
    public const double ChipSize = 52;
    public const double HubDiameter = 76;
    public const double DeadZone = 40;
    public const double WedgeInnerRadius = 40;
    public const double WedgeOuterRadius = 146;
    public const double GuideInnerRadius = 47;
    public const double GuideOuterRadius = 143;
    public const double ArmingTravel = 8;
    public const double CloseClickRadius = 150;
    public const double OutsideClickTolerance = 2;
    public const double HighlightScale = 1.14;
    public const double PlacementInset = 200;
    public const int MaxItems = 12;
    public const int MaxDepth = 2;

    // The settings canvas (smaller wheel on a dark stage).
    public const double CanvasStageHeight = 330;
    public const double CanvasDisc = 250;
    public const double CanvasRing = 92;
    public const double CanvasHub = 68;
    public const double CanvasDeadZone = 36;
    public const double CanvasChip = 44;
    public const double CanvasDragThreshold = 6;

    public static readonly TimeSpan ToolDelay = TimeSpan.FromSeconds(0.15);

    /// <summary>atan2(dx, dyUp) mapped to [0, 2π).</summary>
    public static double Angle(double dx, double dyUp)
    {
        var angle = Math.Atan2(dx, dyUp);
        return angle < 0 ? angle + (2 * Math.PI) : angle;
    }

    /// <summary>Slice i covers [i·step − step/2, i·step + step/2); slice 0 points straight up.</summary>
    public static int SliceIndex(double angle, int count)
    {
        if (count <= 0)
        {
            return -1;
        }

        var step = 2 * Math.PI / count;
        var shifted = (angle + (step / 2)) % (2 * Math.PI);
        if (shifted < 0)
        {
            shifted += 2 * Math.PI;
        }

        return Math.Clamp((int)Math.Floor(shifted / step), 0, count - 1);
    }

    /// <summary>The highlighted slice for a pointer offset, or null inside the dead zone. There is no outer limit.</summary>
    public static int? Highlight(double dx, double dyUp, int count, double deadZone = DeadZone)
    {
        if (count <= 0 || Math.Sqrt((dx * dx) + (dyUp * dyUp)) < deadZone)
        {
            return null;
        }

        return SliceIndex(Angle(dx, dyUp), count);
    }

    /// <summary>Centre of chip i relative to the wheel centre, y up.</summary>
    public static (double X, double YUp) ChipOffset(int index, int count, double ring = ChipRingRadius)
    {
        var angle = 2 * Math.PI * index / Math.Max(1, count);
        return (ring * Math.Sin(angle), ring * Math.Cos(angle));
    }

    /// <summary>Centre angle of slice i (radians from 12 o'clock, clockwise).</summary>
    public static double SliceAngle(int index, int count) => 2 * Math.PI * index / Math.Max(1, count);

    /// <summary>Arrow keys: ((cur + δ) mod n + n) mod n with cur = highlight ?? (δ &gt; 0 ? −1 : 0).</summary>
    public static int Rotate(int? current, int delta, int count)
    {
        if (count <= 0)
        {
            return -1;
        }

        var cur = current ?? (delta > 0 ? -1 : 0);
        return (((cur + delta) % count) + count) % count;
    }

    /// <summary>The wedge moves along the shortest arc: old + remainder(new − old, 2π).</summary>
    public static double WedgeTarget(double oldAngle, double newAngle) =>
        oldAngle + Math.IEEERemainder(newAngle - oldAngle, 2 * Math.PI);

    public static RadialClickAction ClickAction(double distance) => distance switch
    {
        > CloseClickRadius => RadialClickAction.Close,
        < DeadZone => RadialClickAction.StepBack,
        _ => RadialClickAction.RunHighlighted,
    };

    /// <summary>
    /// Wheel centre (physical pixels): at the pointer or the work-area centre,
    /// then kept 200 DIP inside the work area when it is larger than 400×400 DIP.
    /// </summary>
    public static (double X, double Y) Placement(bool atPointer, (double X, double Y) pointer, (double X, double Y, double Width, double Height) workArea, double scale)
    {
        var (x, y) = atPointer ? pointer : (workArea.X + (workArea.Width / 2), workArea.Y + (workArea.Height / 2));
        var inset = PlacementInset * scale;
        if (workArea.Width > 2 * inset)
        {
            x = Math.Clamp(x, workArea.X + inset, workArea.X + workArea.Width - inset);
        }

        if (workArea.Height > 2 * inset)
        {
            y = Math.Clamp(y, workArea.Y + inset, workArea.Y + workArea.Height - inset);
        }

        return (x, y);
    }

    /// <summary>Stagger of the chip bloom: 0.17·i/(n−1) seconds.</summary>
    public static double BloomDelay(int index, int count) => count <= 1 ? 0 : 0.17 * index / (count - 1);
}

/// <summary>Spring constants for non-SwiftUI animation (mass 1), spec 07 §6.2.</summary>
public readonly record struct RadialSpring(double Stiffness, double Damping)
{
    public static RadialSpring Open => new(541.5, 40.0);

    public static RadialSpring Bloom => new(304.6, 26.2);

    public static RadialSpring Highlight => new(685.4, 37.7);

    public static RadialSpring Wedge => new(987.0, 54.0);

    public static RadialSpring CanvasSwap => new(631.7, 40.2);

    /// <summary>From a SwiftUI (response, dampingFraction) pair.</summary>
    public static RadialSpring FromResponse(double response, double dampingFraction)
    {
        var omega = 2 * Math.PI / response;
        return new RadialSpring(omega * omega, 2 * dampingFraction * omega);
    }

    /// <summary>One semi-implicit Euler step towards <paramref name="target"/>.</summary>
    public (double Value, double Velocity) Step(double value, double velocity, double target, double dt)
    {
        var acceleration = (-Stiffness * (value - target)) - (Damping * velocity);
        velocity += acceleration * dt;
        value += velocity * dt;
        return (value, velocity);
    }
}
