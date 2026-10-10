// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Update;

/// <summary>
/// Semantic version with the comparison the updater uses: numeric
/// major.minor.patch, then a release beats any prerelease of the same
/// version, then prerelease identifiers compare dot by dot (numbers
/// numerically and below text, text ordinally, shorter lists first).
/// A leading "v" is accepted; build metadata after "+" is ignored.
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string Prerelease = "") : IComparable<SemVer>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        var prerelease = string.Empty;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SemVer(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Not a version: '{text}'");

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;

        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNum = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            int part;
            if (aNum && bNum) part = an.CompareTo(bn);
            else if (aNum) part = -1;
            else if (bNum) part = 1;
            else part = string.CompareOrdinal(a[i], b[i]);
            if (part != 0) return part;
        }

        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(SemVer l, SemVer r) => l.CompareTo(r) < 0;
    public static bool operator >(SemVer l, SemVer r) => l.CompareTo(r) > 0;
    public static bool operator <=(SemVer l, SemVer r) => l.CompareTo(r) <= 0;
    public static bool operator >=(SemVer l, SemVer r) => l.CompareTo(r) >= 0;

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}
