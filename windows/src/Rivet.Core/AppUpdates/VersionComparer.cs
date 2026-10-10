// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Maintenance.AppUpdates;

/// <summary>
/// Version comparison of the macOS App updates (spec §3.3.10), ported
/// verbatim: parts split on anything that is not a letter or digit, numbers
/// compared by value at any length, a bare number beats the same number with
/// a suffix ("5" &gt; "5beta"), missing trailing zeros are equal.
/// </summary>
public static class VersionComparer
{
    /// <summary>Trim; drop a leading v/V only before a digit; cut at the first comma (package revisions).</summary>
    public static string Core(string? version)
    {
        var text = (version ?? string.Empty).Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsAsciiDigit(text[1]))
        {
            text = text[1..];
        }

        var comma = text.IndexOf(',');
        return comma >= 0 ? text[..comma] : text;
    }

    public static bool IsUncomparable(string? version)
    {
        var core = Core(version);
        return core.Length == 0 || core.Equals("latest", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Negative when <paramref name="a"/> is older, positive when newer, 0 when the same.</summary>
    public static int Compare(string? a, string? b)
    {
        var left = Split(Core(a));
        var right = Split(Core(b));
        var count = Math.Max(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            var result = ComparePart(i < left.Length ? left[i] : string.Empty, i < right.Length ? right[i] : string.Empty);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    /// <summary>The candidate is a newer release than what is installed.</summary>
    public static bool IsNewer(string? candidate, string? installed) => Compare(candidate, installed) > 0;

    private static string[] Split(string version) =>
        version.Split(c => !char.IsLetterOrDigit(c));

    private static int ComparePart(string a, string b)
    {
        var aDigits = IsDigits(a);
        var bDigits = IsDigits(b);
        if (aDigits && bDigits)
        {
            return CompareNumbers(a, b);
        }

        if (a.Length == 0)
        {
            return bDigits && IsAllZeros(b) ? 0 : -1;
        }

        if (b.Length == 0)
        {
            return aDigits && IsAllZeros(a) ? 0 : 1;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 0;
        }

        var na = LeadingDigits(a);
        var nb = LeadingDigits(b);
        if ((na.Length == 0) != (nb.Length == 0))
        {
            return na.Length == 0 ? -1 : 1;
        }

        if (na.Length > 0)
        {
            var numeric = CompareNumbers(na, nb);
            if (numeric != 0)
            {
                return numeric;
            }

            var aBare = na.Length == a.Length;
            var bBare = nb.Length == b.Length;
            if (aBare != bBare)
            {
                return aBare ? 1 : -1;
            }
        }

        return Math.Sign(string.Compare(a.ToLowerInvariant(), b.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static int CompareNumbers(string a, string b)
    {
        var x = a.TrimStart('0');
        var y = b.TrimStart('0');
        if (x.Length != y.Length)
        {
            return x.Length < y.Length ? -1 : 1;
        }

        return Math.Sign(string.Compare(x, y, StringComparison.Ordinal));
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    private static bool IsAllZeros(string s) => s.All(c => c == '0');

    private static string LeadingDigits(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            i++;
        }

        return s[..i];
    }
}

internal static class StringSplitExtensions
{
    /// <summary>Splits on characters matching <paramref name="isSeparator"/>, dropping empty parts (Swift's split).</summary>
    public static string[] Split(this string text, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (isSeparator(text[i]))
            {
                if (start >= 0)
                {
                    parts.Add(text[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            parts.Add(text[start..]);
        }

        return [.. parts];
    }
}
