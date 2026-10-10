// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Rivet.Core.Localization;
using Xunit;

namespace Rivet.App.Tests;

/// <summary>
/// String keys referenced in source must exist in the catalog, so a typo shows
/// up here instead of as raw key text in the UI. Only literals in a
/// localization context are checked — an argument of <c>L.Get/Format/Plural</c>,
/// the page builders, <c>{l:Tr …}</c>, or a property/constant whose name ends in
/// <c>Key</c>/<c>Keys</c> — so ids such as <c>"recorder.toggle"</c> are not mistaken for keys.
/// </summary>
public partial class StringKeyTests
{
    [Fact]
    public void Literal_string_keys_exist()
    {
        var localizer = new Localizer(AppLanguage.EnUS);
        var known = localizer.AllKeys().ToHashSet(StringComparer.Ordinal);
        var prefixes = known.Select(k => k.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        var root = FindRepoRoot();
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in KeyLiteral().Matches(line))
                {
                    var key = match.Groups["key"].Value;
                    var end = match.Index + match.Length;
                    if ((end < line.Length && line[end] == '{') || key.EndsWith('.'))
                    {
                        continue; // interpolated prefix such as $"win.shell.capability{c}"
                    }

                    var before = line[..match.Index];
                    var isTr = match.Value.StartsWith("{l:Tr", StringComparison.Ordinal);
                    var hasCatalogPrefix = prefixes.Contains(key.Split('.')[0]);
                    if (hasCatalogPrefix && (isTr || LocalizationContext().IsMatch(before)) && !known.Contains(key))
                    {
                        missing.Add($"{Path.GetRelativePath(root, file)}: {key}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "Missing string keys:\n" + string.Join('\n', missing.Distinct()));
    }

    [GeneratedRegex("""(?:"|\{l:Tr\s+)(?<key>[A-Za-z][A-Za-z0-9]*\.[A-Za-z][A-Za-z0-9_.]*)""")]
    private static partial Regex KeyLiteral();

    /// <summary>Text before a literal that marks it as a string key.</summary>
    [GeneratedRegex("""(\bL\.(Get|Format|Plural)\(|Localizer\.Current\.(Get|Format|Plural)\(|\b(Header|Card|Toggle|Choice|Slider)\(|\w*Keys?\s*=\s*\[?[^;]*$|\bconst string \w*Key\s*=\s*$)""")]
    private static partial Regex LocalizationContext();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rivet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
