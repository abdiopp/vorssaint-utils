// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Rivet.Core.Tests;

/// <summary>Each module adds its own i18n-win file; two files must never define the same key.</summary>
public class WindowsStringsTests
{
    [Fact]
    public void Windows_string_files_do_not_redefine_each_others_keys()
    {
        var assembly = typeof(Localization.Localizer).Assembly;
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var clashes = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("i18n-win/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            var language = name.Split('.')[^2];
            foreach (var key in map.Keys)
            {
                var id = $"{language}:{key}";
                if (owners.TryGetValue(id, out var first))
                {
                    clashes.Add($"{key} in {first} and {name}");
                }
                else
                {
                    owners[id] = name;
                }
            }
        }

        Assert.True(clashes.Count == 0, "Duplicate keys:\n" + string.Join('\n', clashes));
    }

    [Fact]
    public void Windows_keys_are_namespaced_or_override_existing_macos_keys()
    {
        var english = new Localization.Localizer(Localization.AppLanguage.EnUS, l => LoadMac(l), _ => new Dictionary<string, string>());
        var macKeys = english.AllKeys().ToHashSet(StringComparer.Ordinal);
        var assembly = typeof(Localization.Localizer).Assembly;
        var stray = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("i18n-win/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            foreach (var key in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!.Keys)
            {
                if (!key.StartsWith("win.", StringComparison.Ordinal) && !macKeys.Contains(key))
                {
                    stray.Add($"{name}: {key}");
                }
            }
        }

        Assert.True(stray.Count == 0, "Keys that neither start with win. nor override a macOS key:\n" + string.Join('\n', stray));
    }

    [Fact]
    public void Translated_windows_strings_match_their_english_keys_and_placeholders()
    {
        var assembly = typeof(Localization.Localizer).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("i18n-win/", StringComparison.Ordinal)).ToList();
        var problems = new List<string>();
        foreach (var name in names.Where(n => !n.EndsWith(".en-US.json", StringComparison.Ordinal)))
        {
            var module = name["i18n-win/".Length..].Split('.')[0];
            var englishName = $"i18n-win/{module}.en-US.json";
            if (!names.Contains(englishName))
            {
                problems.Add($"{name}: no {englishName}");
                continue;
            }

            var english = Read(assembly, englishName);
            foreach (var (key, value) in Read(assembly, name))
            {
                if (!english.TryGetValue(key, out var source))
                {
                    problems.Add($"{name}: {key} is not in {englishName}");
                }
                else if (!Placeholders(source).SequenceEqual(Placeholders(value)))
                {
                    problems.Add($"{name}: {key} placeholders [{string.Join(' ', Placeholders(source))}] became [{string.Join(' ', Placeholders(value))}]");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void Windows_strings_do_not_mention_the_mac()
    {
        // Overrides exist to replace macOS wording; a translation that brings it back is a bug.
        var mac = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z])Macs?(?![a-z])|macOS|Finder|⌘|⌥");
        var assembly = typeof(Localization.Localizer).Assembly;
        var hits = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("i18n-win/", StringComparison.Ordinal))
            .SelectMany(n => Read(assembly, n).Where(e => mac.IsMatch(e.Value)).Select(e => $"{n}: {e.Key} = {e.Value}"))
            .ToList();
        Assert.True(hits.Count == 0, string.Join('\n', hits));
    }

    /// <summary>
    /// Conversion types in argument order (positional %2$@ counts as argument 2), ignoring %%.
    /// The space flag is not recognised: captions write "200 % and" as plain text.
    /// </summary>
    private static List<string> Placeholders(string text)
    {
        var result = new List<(int Index, string Type)>();
        var next = 1;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     text, @"%(?:(\d+)\$)?[-+#0]*\d*(?:\.\d+)?(?:hh|h|ll|l|q|z)?([@dDiuUxXoOfFeEgGcCsSaAp%])"))
        {
            if (m.Groups[2].Value == "%")
            {
                continue;
            }

            var index = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : next++;
            var type = m.Groups[2].Value.ToLowerInvariant() is "i" or "u" ? "d" : m.Groups[2].Value.ToLowerInvariant();
            result.Add((index, type));
        }

        return result.OrderBy(r => r.Index).Select(r => $"{r.Index}:{r.Type}").ToList();
    }

    private static Dictionary<string, string> Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    private static IReadOnlyDictionary<string, string> LoadMac(Localization.AppLanguage language)
    {
        var assembly = typeof(Localization.Localizer).Assembly;
        using var stream = assembly.GetManifestResourceStream($"i18n/{Localization.AppLanguages.Code(language)}.json");
        return stream is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
