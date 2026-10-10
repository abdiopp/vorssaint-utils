// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor;

/// <summary>
/// Small numeric helpers shared by the recorder's algorithms (spec 02 §6).
/// Rounding follows the macOS build (<c>.rounded()</c> = half away from
/// zero), not .NET's default banker's rounding, so the spec's vectors port
/// exactly.
/// </summary>
public static class RecorderMath
{
    /// <summary>Half away from zero, like Swift's <c>rounded()</c>.</summary>
    public static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    public static int RoundToInt(double value) =>
        double.IsFinite(value) ? (int)Math.Clamp(Round(value), int.MinValue, int.MaxValue) : 0;

    public static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    /// <summary><c>x²(3 − 2x)</c> with x clamped to [0, 1].</summary>
    public static double Smoothstep(double x)
    {
        x = Clamp01(x);
        return x * x * (3 - (2 * x));
    }

    /// <summary><c>x³(10 − 15x + 6x²)</c> with x clamped to [0, 1].</summary>
    public static double Smootherstep(double x)
    {
        x = Clamp01(x);
        return x * x * x * (10 - (15 * x) + (6 * x * x));
    }

    /// <summary>§6.1 <c>evenSide</c>: whole, even and at least 32.</summary>
    public static int EvenSide(double v)
    {
        if (!double.IsFinite(v))
        {
            return 32;
        }

        var r = (int)Math.Max(32, Math.Floor(Math.Min(v, 1_000_000)));
        return r % 2 == 0 ? r : r - 1;
    }

    /// <summary>Clamps to [min, max]; non-finite values become <paramref name="fallback"/>.</summary>
    public static double ClampOr(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
