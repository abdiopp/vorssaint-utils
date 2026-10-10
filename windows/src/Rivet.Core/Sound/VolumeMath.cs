// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Sound;

/// <summary>Volume constants and conversions shared by the mixer, the pickers and the keys (spec §3.9.3, §6.11, §6.14).</summary>
public static class VolumeMath
{
    /// <summary>|v − 1| below this is "100 %"; storing it removes the saved entry.</summary>
    public const double UnityTolerance = 0.005;

    /// <summary>At or below this an app counts as silent (the crossed speaker).</summary>
    public const double MuteThreshold = 0.001;

    /// <summary>
    /// Highest per-app volume. macOS boosts to 200 % through process taps;
    /// Windows' session volume (ISimpleAudioVolume) stops at 1.0 and there is
    /// no per-app gain stage without a driver, so the slider ends at 100 %.
    /// </summary>
    public const double MaxAppVolume = 1.0;

    /// <summary>Non-finite values become 100 %; the rest is clamped to 0…<see cref="MaxAppVolume"/>.</summary>
    public static double ClampAppVolume(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, MaxAppVolume) : 1.0;

    public static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public static bool IsUnity(double value) => Math.Abs(value - 1.0) < UnityTolerance;

    public static bool IsSilent(double value) => value <= MuteThreshold;

    public static int ToPercent(double value) => (int)Math.Round(Clamp01(value) * 100, MidpointRounding.AwayFromZero);

    /// <summary>"42%" in the UI language's percent style ("42 %" in French).</summary>
    public static string FormatPercent(double value, CultureInfo culture) =>
        (ToPercent(value) / 100.0).ToString("P0", culture);

    /// <summary>
    /// Parses the percent editor's text (§6.14): trim, drop a trailing "%",
    /// accept the culture's decimal separator, require a finite number, clamp
    /// to 0…<paramref name="maxPercent"/>, divide by 100. False means "beep
    /// and keep editing".
    /// </summary>
    public static bool TryParsePercent(string? text, double maxPercent, CultureInfo culture, out double scalar)
    {
        scalar = 0;
        if (text is null)
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.EndsWith('%'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        var separator = culture.NumberFormat.NumberDecimalSeparator;
        if (separator != ".")
        {
            trimmed = trimmed.Replace(separator, ".", StringComparison.Ordinal);
        }

        if (trimmed.Length == 0
            || !double.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
        {
            return false;
        }

        scalar = Math.Clamp(number, 0, maxPercent) / 100.0;
        return true;
    }
}
