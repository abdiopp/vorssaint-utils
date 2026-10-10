// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rivet.Core.Capture;

/// <summary>
/// File names and subfolders for saved captures (spec 01 §6.20), with the
/// Windows rules on top: no <c>\ / : * ? " &lt; &gt; |</c>, no trailing dots
/// or spaces, no reserved device names, and paths kept under MAX_PATH.
/// </summary>
public static partial class CaptureNaming
{
    /// <summary>Longest full path we write (MAX_PATH minus the terminator).</summary>
    public const int MaxPathLength = 259;

    private static readonly string[] MonthNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary><c>Screenshot 2026-10-09 at 14.05.33.png</c> (24-hour clock, dots instead of colons).</summary>
    public static string DefaultName(DateTime local, string prefix = "Screenshot", string extension = ".png") =>
        $"{prefix} {local.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture)}{extension}";

    /// <summary>Replaces the date tokens, longest first: %year %month %y %mo %d %h %mi %s.</summary>
    public static string ExpandDateTokens(string pattern, DateTime local)
    {
        var inv = CultureInfo.InvariantCulture;
        return pattern
            .Replace("%year", local.Year.ToString("0000", inv), StringComparison.Ordinal)
            .Replace("%month", MonthNames[local.Month - 1], StringComparison.Ordinal)
            .Replace("%y", (local.Year % 100).ToString("00", inv), StringComparison.Ordinal)
            .Replace("%mo", local.Month.ToString("00", inv), StringComparison.Ordinal)
            .Replace("%d", local.Day.ToString("00", inv), StringComparison.Ordinal)
            .Replace("%h", local.Hour.ToString("00", inv), StringComparison.Ordinal)
            .Replace("%mi", local.Minute.ToString("00", inv), StringComparison.Ordinal)
            .Replace("%s", local.Second.ToString("00", inv), StringComparison.Ordinal);
    }

    public static bool UsesNumber(string pattern) => pattern.Contains("%#", StringComparison.Ordinal);

    /// <summary>Each run of <c>%#…#</c> becomes the number padded to (run length − 1) digits, minimum 1.</summary>
    public static string ExpandNumberRuns(string pattern, int number)
    {
        var matches = NumberRun().Matches(pattern);
        if (matches.Count == 0)
        {
            return pattern;
        }

        var builder = new StringBuilder(pattern);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var digits = Math.Max(1, match.Length - 1);
            builder.Remove(match.Index, match.Length);
            builder.Insert(match.Index, number.ToString(new string('0', digits), CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The file name for a capture: the pattern (trimmed) with its tokens and
    /// number expanded and illegal characters replaced, or the default name
    /// when the pattern is empty.
    /// </summary>
    public static string FileName(string? pattern, DateTime local, int number, string prefix = "Screenshot", string extension = ".png")
    {
        var trimmed = pattern?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return DefaultName(local, SanitizeComponent(prefix, "Screenshot"), extension);
        }

        var expanded = ExpandNumberRuns(ExpandDateTokens(trimmed, local), number);
        var name = SanitizeComponent(expanded, fallback: DefaultName(local, prefix, string.Empty));
        return name + extension;
    }

    /// <summary>
    /// The dated subfolder (relative), with "/" or "\" as separators. Empty,
    /// "." and ".." components are dropped so the path cannot leave the base folder.
    /// </summary>
    public static string Subfolder(string? pattern, DateTime local)
    {
        var trimmed = pattern?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var parts = ExpandDateTokens(trimmed, local)
            .Split(['/', '\\'], StringSplitOptions.None)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && p != "." && p != "..")
            .Select(p => SanitizeComponent(p, fallback: "_"))
            .Where(p => p.Length > 0 && p != "." && p != "..");
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>One path component that Windows accepts.</summary>
    public static string SanitizeComponent(string text, string fallback)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c is '/' or ':' or '\\' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c);
        }

        var result = builder.ToString().TrimEnd('.', ' ').TrimStart(' ');
        if (result.Length == 0)
        {
            return fallback;
        }

        var stem = result.Split('.')[0];
        if (ReservedNames.Contains(stem.TrimEnd()))
        {
            result = "_" + result;
        }

        return result;
    }

    /// <summary>
    /// <c>name.png</c> when free, else <c>name 2.png</c> … <c>name 9999.png</c>
    /// (then the original name). Also shortens the stem so the full path stays
    /// under <see cref="MaxPathLength"/>.
    /// </summary>
    public static string UniqueName(string folder, string fileName, Func<string, bool> exists)
    {
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var budget = MaxPathLength - folder.Length - 1 - extension.Length - " 9999".Length;
        if (budget > 8 && stem.Length > budget)
        {
            stem = stem[..budget].TrimEnd('.', ' ');
        }

        var candidate = stem + extension;
        if (!exists(Path.Combine(folder, candidate)))
        {
            return candidate;
        }

        for (var i = 2; i <= 9999; i++)
        {
            candidate = $"{stem} {i.ToString(CultureInfo.InvariantCulture)}{extension}";
            if (!exists(Path.Combine(folder, candidate)))
            {
                return candidate;
            }
        }

        return stem + extension;
    }

    [GeneratedRegex("%#+")]
    private static partial Regex NumberRun();
}

/// <summary>
/// The <c>%#</c> counter (spec 01 §6.20): a save consumes the next number and
/// stores next + 1; a failed or discarded save gives it back, but only when
/// nothing else advanced the sequence since.
/// </summary>
public static class FileNumberSequence
{
    public static int Consume(Settings.ISettingsStore settings)
    {
        var next = settings.Get(CaptureSettings.FileNumberNext);
        settings.Set(CaptureSettings.FileNumberNext, next + 1);
        return next;
    }

    public static int Peek(Settings.ISettingsStore settings) => settings.Get(CaptureSettings.FileNumberNext);

    public static bool Rewind(Settings.ISettingsStore settings, int consumed)
    {
        if (settings.Get(CaptureSettings.FileNumberNext) != consumed + 1)
        {
            return false;
        }

        settings.Set(CaptureSettings.FileNumberNext, consumed);
        return true;
    }

    /// <summary>Changing "Starts at" (or Reset) restarts the sequence from the start value.</summary>
    public static void Restart(Settings.ISettingsStore settings) =>
        settings.Set(CaptureSettings.FileNumberNext, settings.Get(CaptureSettings.FileNumberStart));
}
