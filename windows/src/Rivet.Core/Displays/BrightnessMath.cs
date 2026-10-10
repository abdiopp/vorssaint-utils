// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Displays;

/// <summary>The numbers of spec 03 §3.19 and §6.13, as pure functions.</summary>
public static class BrightnessMath
{
    /// <summary>A remembered level is trusted for this long; after that a readable display is read first.</summary>
    public const double TrustWindowSeconds = 3;

    /// <summary>Display configuration changes are coalesced over this window.</summary>
    public const double SettleSeconds = 0.5;

    /// <summary>Monitors need this long after wake before DDC answers.</summary>
    public const double WakeSettleSeconds = 3;

    /// <summary>A dimmed display returning from a connection gap gets at least this level.</summary>
    public const double ReconnectFloor = 0.25;

    /// <summary>The share of the slider that dims the picture under "Extra dimming".</summary>
    public const double ExtendedRange = 0.25;

    /// <summary>Minimum time between whole DDC commands to one display.</summary>
    public const double DdcPacingSeconds = 0.05;

    /// <summary>Level of a write-only monitor before this session wrote one.</summary>
    public const double UnknownLevel = 0.5;

    /// <summary>
    /// The darkest overlay. Software dimming never goes fully black so the
    /// screen stays readable enough to undo it.
    /// </summary>
    public const double MaxOverlayAlpha = 0.92;

    public const int OsdSegments = 16;

    /// <summary>Standard 1/16, half 1/32, quarter 1/64.</summary>
    public static double StepSize(string keyStep) => keyStep switch
    {
        "half" => 1.0 / 32,
        "quarter" => 1.0 / 64,
        _ => 1.0 / 16,
    };

    /// <summary>
    /// The next grid point in <paramref name="direction"/>: levels between
    /// grid points snap to the nearer one on the way (like the OS keys).
    /// </summary>
    public static double Step(double level, int direction, double step)
    {
        if (direction == 0 || step <= 0)
        {
            return Math.Clamp(level, 0, 1);
        }

        const double epsilon = 1e-6;
        var position = Math.Clamp(level, 0, 1) / step;
        var next = direction > 0
            ? (Math.Floor(position + epsilon) + 1) * step
            : (Math.Ceiling(position - epsilon) - 1) * step;
        return Math.Clamp(Math.Round(next, 6), 0, 1);
    }

    /// <summary>Applies <paramref name="steps"/> presses (negative = darker) one after another.</summary>
    public static double Steps(double level, int steps, double step)
    {
        var result = level;
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            result = Step(result, Math.Sign(steps), step);
        }

        return result;
    }

    /// <summary>Monitors that report a maximum of 0 use 100.</summary>
    public static int DdcMaximum(int maximum) => maximum <= 0 ? 100 : maximum;

    public static int ToDdc(double level, int maximum)
    {
        var max = DdcMaximum(maximum);
        return (int)Math.Clamp(Math.Round(Math.Clamp(level, 0, 1) * max, MidpointRounding.AwayFromZero), 0, max);
    }

    public static double FromDdc(int current, int maximum) => Math.Clamp((double)current / DdcMaximum(maximum), 0, 1);

    public static int ToPercent(double level) => (int)Math.Round(Math.Clamp(level, 0, 1) * 100, MidpointRounding.AwayFromZero);

    public static double FromPercent(int percent) => Math.Clamp(percent / 100.0, 0, 1);

    /// <summary>"Extra dimming": the lowest quarter dims the picture once the monitor reaches its minimum.</summary>
    public static (double Hardware, double Picture) SplitExtended(double level)
    {
        level = Math.Clamp(level, 0, 1);
        return level < ExtendedRange
            ? (0, level / ExtendedRange)
            : ((level - ExtendedRange) / (1 - ExtendedRange), 1);
    }

    /// <summary>The slider level for a hardware level and picture factor under "Extra dimming".</summary>
    public static double JoinExtended(double hardware, double picture) =>
        picture < 0.999 || hardware <= 0
            ? Math.Clamp(picture, 0, 1) * ExtendedRange
            : ExtendedRange + (Math.Clamp(hardware, 0, 1) * (1 - ExtendedRange));

    /// <summary>The overlay alpha for a picture factor (1 = no overlay).</summary>
    public static double OverlayAlpha(double factor) => Math.Clamp(1 - Math.Clamp(factor, 0, 1), 0, 1) * MaxOverlayAlpha;

    /// <summary>A factor at or above this restores the picture exactly (the overlay goes away).</summary>
    public static bool IsUndimmed(double factor) => factor >= 0.999;

    /// <summary>Filled OSD segments: <c>ceil(b × 16)</c>, none at 0.</summary>
    public static int FilledSegments(double level) =>
        level <= 0 ? 0 : Math.Min(OsdSegments, (int)Math.Ceiling(Math.Round(Math.Clamp(level, 0, 1) * OsdSegments, 6)));
}
