// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Rivet.Core.Maintenance.PackageManager;

public enum PackageOperationKind
{
    Install,
    Uninstall,
    Upgrade,
    UpgradeAll,
    UpdateSources,
}

public enum OperationPhase
{
    Preparing,
    Downloading,
    Installing,
    Removing,
    Updating,
    Refreshing,
    Finishing,
}

/// <summary>
/// Reads phase, percentage and the last activity line from winget's
/// output (the Homebrew progress rules adapted). Phase words are English;
/// a localized winget still reports progress through percentages and
/// "x MB / y MB" counters, and the activity line is shown as printed.
/// </summary>
public static partial class WingetProgress
{
    public static OperationPhase? PhaseOf(string line, PackageOperationKind kind)
    {
        var text = line.Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return null;
        }

        if (kind == PackageOperationKind.UpdateSources)
        {
            return text.Contains("updating") || text.Contains("done") ? OperationPhase.Refreshing : null;
        }

        if (text.StartsWith("successfully installed", StringComparison.Ordinal)
            || text.StartsWith("successfully uninstalled", StringComparison.Ordinal)
            || text.Contains("restart your pc", StringComparison.Ordinal))
        {
            return OperationPhase.Finishing;
        }

        if (text.StartsWith("downloading", StringComparison.Ordinal) || text.StartsWith("downloaded", StringComparison.Ordinal))
        {
            return OperationPhase.Downloading;
        }

        if (text.StartsWith("starting package uninstall", StringComparison.Ordinal) || text.StartsWith("uninstalling", StringComparison.Ordinal))
        {
            return OperationPhase.Removing;
        }

        if (text.StartsWith("successfully verified installer hash", StringComparison.Ordinal)
            || text.StartsWith("starting package install", StringComparison.Ordinal)
            || text.StartsWith("installing", StringComparison.Ordinal)
            || text.StartsWith("extracting archive", StringComparison.Ordinal))
        {
            return kind switch
            {
                PackageOperationKind.Uninstall => OperationPhase.Removing,
                PackageOperationKind.Upgrade or PackageOperationKind.UpgradeAll => OperationPhase.Updating,
                _ => OperationPhase.Installing,
            };
        }

        return null;
    }

    /// <summary>The last "NN%" or "x MB / y MB" on the line, as 0…1.</summary>
    public static double? ProgressOf(string line)
    {
        double? result = null;
        foreach (Match match in PercentPattern().Matches(line))
        {
            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                result = Math.Clamp(percent / 100.0, 0, 1);
            }
        }

        foreach (Match match in BytesPattern().Matches(line))
        {
            var done = Bytes(match.Groups["done"].Value, match.Groups["doneUnit"].Value);
            var total = Bytes(match.Groups["total"].Value, match.Groups["totalUnit"].Value);
            if (done is { } d && total is > 0 and var t)
            {
                result = Math.Clamp(d / t, 0, 1);
            }
        }

        return result;
    }

    /// <summary>
    /// A progress bar frame: mostly block characters, digits, sizes and
    /// percent signs (more than 85 % of the line, the Homebrew rule).
    /// </summary>
    public static bool IsProgressLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text is "-" or "\\" or "|" or "/")
        {
            return true;
        }

        var symbols = text.Count(c => c is '█' or '▒' or '▓' or '░' or '#' or '=' or '-' or '>' or ' ' or '.' or ':' or '%' or '/' or ',' || char.IsAsciiDigit(c));
        if (symbols > text.Length * 0.85)
        {
            return true;
        }

        // "████▒▒▒  12.0 MB / 58.3 MB"
        var blocks = text.Count(c => c is '█' or '▒' or '▓' or '░');
        return blocks >= 4;
    }

    /// <summary>The line to show under the status: not empty, not a progress frame, without a leading "==>".</summary>
    public static string? ActivityOf(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || IsProgressLine(text))
        {
            return null;
        }

        if (text.StartsWith("==>", StringComparison.Ordinal))
        {
            text = text[3..].TrimStart();
        }
        else if (text.StartsWith("->", StringComparison.Ordinal))
        {
            text = text[2..].TrimStart();
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>"Ns", "Nmin", "Nh Mmin" (the macOS elapsed format).</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = (long)Math.Max(0, elapsed.TotalSeconds);
        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds}s");
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes}min");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{minutes / 60}h {minutes % 60}min");
    }

    private static double? Bytes(string number, string unit)
    {
        if (!double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return unit.ToUpperInvariant() switch
        {
            "KB" => value * 1024,
            "MB" => value * 1024 * 1024,
            "GB" => value * 1024 * 1024 * 1024,
            _ => value,
        };
    }

    [GeneratedRegex(@"(?<![\d.])([0-9]{1,3}(?:\.[0-9]+)?)\s?%")]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"(?<done>[0-9]+(?:[.,][0-9]+)?)\s*(?<doneUnit>B|KB|MB|GB)\s*/\s*(?<total>[0-9]+(?:[.,][0-9]+)?)\s*(?<totalUnit>B|KB|MB|GB)", RegexOptions.IgnoreCase)]
    private static partial Regex BytesPattern();
}
