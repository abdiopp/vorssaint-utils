// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;

namespace Rivet.Core.Input;

/// <summary>
/// An app as the input features identify it on Windows: the full path of its
/// executable (packaged apps resolve to the process behind
/// ApplicationFrameHost). Compared case-insensitively.
/// </summary>
public sealed record AppIdentityInfo(string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path.Replace('/', '\\').Split('\\')[^1]);

    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>
/// "Apps to leave alone" (spec 07 §3.7.8): one list per feature, entries are
/// executable paths (or bare file names such as <c>blender.exe</c>). Matching
/// is case-insensitive and tolerates version folders, so
/// <c>…\Discord\app-1.0.9005\Discord.exe</c> keeps matching after an update.
/// macOS bundle ids imported from a backup are kept but never match.
/// </summary>
public sealed partial class AppExclusionList
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _versionless = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase);

    public AppExclusionList(IEnumerable<string>? entries)
    {
        Entries = Sanitize(entries?.ToArray() ?? []);
        foreach (var entry in Entries)
        {
            if (IsPath(entry))
            {
                var normalized = NormalizePath(entry);
                _paths.Add(normalized);
                _versionless.Add(VersionlessKey(normalized));
            }
            else if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                _fileNames.Add(entry);
            }
        }
    }

    public static AppExclusionList Empty { get; } = new([]);

    public IReadOnlyList<string> Entries { get; }

    public bool IsEmpty => Entries.Count == 0;

    public bool Matches(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath) || IsEmpty)
        {
            return false;
        }

        var normalized = NormalizePath(processPath);
        if (_paths.Contains(normalized))
        {
            return true;
        }

        if (_fileNames.Count > 0 && _fileNames.Contains(FileNameOf(normalized)))
        {
            return true;
        }

        return _versionless.Count > 0 && _versionless.Contains(VersionlessKey(normalized));
    }

    /// <summary>Trimmed, blanks and duplicates (case-insensitive) dropped, order kept.</summary>
    public static string[] Sanitize(string[]? entries)
    {
        if (entries is null || entries.Length == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(entries.Length);
        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (!string.IsNullOrEmpty(entry) && seen.Add(entry))
            {
                result.Add(entry);
            }
        }

        return [.. result];
    }

    /// <summary>The quit-protection lists: sanitized, then sorted.</summary>
    public static string[] SanitizeSorted(string[]? entries) =>
        [.. Sanitize(entries).OrderBy(e => e, StringComparer.OrdinalIgnoreCase)];

    public static bool IsPath(string entry) => entry.Contains('\\') || entry.Contains('/');

    public static string NormalizePath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        return p;
    }

    /// <summary>Lower-cased path with dotted version runs replaced by "*".</summary>
    public static string VersionlessKey(string normalizedPath)
    {
        var builder = new StringBuilder(normalizedPath.Length);
        foreach (var segment in normalizedPath.Split('\\'))
        {
            if (builder.Length > 0)
            {
                builder.Append('\\');
            }

            builder.Append(VersionRun().Replace(segment.ToLowerInvariant(), "*"));
        }

        return builder.ToString();
    }

    private static string FileNameOf(string normalizedPath)
    {
        var slash = normalizedPath.LastIndexOf('\\');
        return slash >= 0 ? normalizedPath[(slash + 1)..] : normalizedPath;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionRun();
}
