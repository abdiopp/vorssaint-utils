// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rivet.Core.Maintenance.PackageManager;

/// <summary>A table winget printed: header tokens with their display columns, and row cells.</summary>
public sealed record WingetTable(IReadOnlyList<string> Headers, IReadOnlyList<int> Starts, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Which command printed a table (decides how extra columns are read).</summary>
public enum WingetTableKind
{
    List,
    Upgrade,
    Search,
}

/// <summary>
/// Defensive parsing of winget's console output. The CLI localizes headers
/// and messages, truncates long cells with "…" to fit 120 columns when its
/// output is redirected, and prints spinner frames and progress bars over
/// each other with carriage returns. Columns are therefore taken from the
/// header's token positions (in display cells) on the line above the dashes,
/// and rows end at the first line that is not a valid row.
/// </summary>
public static partial class WingetOutput
{
    // Header words of the optional columns in the languages winget ships (best effort;
    // unknown headers fall back to the column's content).
    private static readonly HashSet<string> AvailableHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Available", "Verfügbar", "Disponible", "Disponibile", "Disponível", "Доступно", "Dostupné",
        "Kullanılabilir", "利用可能", "可用", "Dostępne", "Beschikbaar", "Доступна",
    };

    private static readonly HashSet<string> SourceHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Source", "Quelle", "Origen", "Fuente", "Origine", "Fonte", "Источник", "Zdroj", "Kaynak",
        "ソース", "원본", "源", "來源", "来源", "Źródło", "Bron",
    };

    private static readonly HashSet<string> MatchHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Match", "Übereinstimmung", "Coincidencia", "Correspondance", "Corrispondenza", "Correspondência",
        "Совпадение", "Zhoda", "Eşleşme", "一致", "일치", "匹配", "相符項目",
    };

    /// <summary>
    /// Output lines as a terminal would show them: ANSI sequences removed,
    /// backspaces applied, and of several carriage-return frames on one line
    /// only the last visible one kept.
    /// </summary>
    public static IReadOnlyList<string> CleanLines(string output)
    {
        var text = AnsiPattern().Replace(output ?? string.Empty, string.Empty);
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains('\r'))
            {
                var frames = line.Split('\r');
                line = frames.LastOrDefault(f => !string.IsNullOrWhiteSpace(f)) ?? string.Empty;
            }

            if (line.Contains('\b'))
            {
                line = ApplyBackspaces(line);
            }

            lines.Add(line.TrimEnd());
        }

        return lines;
    }

    public static IReadOnlyList<WingetTable> ParseTables(string output)
    {
        var lines = CleanLines(output);
        var tables = new List<WingetTable>();
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (!IsDashLine(lines[i + 1]) || string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var header = lines[i];
            var starts = TextColumns.TokenStarts(header);
            if (starts.Count < 2)
            {
                continue;
            }

            var headers = new List<string>();
            for (var c = 0; c < starts.Count; c++)
            {
                headers.Add(Cell(header, starts, c));
            }

            var rows = new List<IReadOnlyList<string>>();
            var r = i + 2;
            for (; r < lines.Count; r++)
            {
                var line = lines[r];
                if (string.IsNullOrWhiteSpace(line) || (r + 1 < lines.Count && IsDashLine(lines[r + 1])))
                {
                    break;
                }

                // Cells are padded and truncated to their width, so the cell just before
                // every column start is a separator space. Text that runs across a column
                // start (the "N upgrades available." summary under a narrow Name column,
                // a message) is not a row.
                if (starts.Skip(1).Any(start => !TextColumns.IsBlankAt(line, start - 1)))
                {
                    break;
                }

                var cells = new List<string>(starts.Count);
                for (var c = 0; c < starts.Count; c++)
                {
                    cells.Add(Cell(line, starts, c));
                }

                // A row always has a name and an id; a short summary line has only a name.
                if (cells[0].Length == 0 || cells[1].Length == 0)
                {
                    break;
                }

                rows.Add(cells);
            }

            tables.Add(new WingetTable(headers, starts, rows));
            i = r - 1;
        }

        return tables;
    }

    /// <summary>Packages of every table; for <c>upgrade</c>, rows after the first table require explicit targeting.</summary>
    public static IReadOnlyList<WingetPackage> ParsePackages(string output, WingetTableKind kind)
    {
        var packages = new List<WingetPackage>();
        var tables = ParseTables(output);
        for (var t = 0; t < tables.Count; t++)
        {
            var table = tables[t];
            if (table.Headers.Count < 2)
            {
                continue;
            }

            var roles = Roles(table, kind);
            foreach (var row in table.Rows)
            {
                string? Get(ColumnRole role)
                {
                    var index = Array.IndexOf(roles, role);
                    return index >= 0 && index < row.Count && row[index].Length > 0 ? row[index] : null;
                }

                var id = Get(ColumnRole.Id);
                var name = Get(ColumnRole.Name);
                if (id is null || name is null)
                {
                    continue;
                }

                packages.Add(new WingetPackage
                {
                    Name = name,
                    Id = id,
                    Version = Get(ColumnRole.Version) ?? string.Empty,
                    Available = Get(ColumnRole.Available),
                    Source = Get(ColumnRole.Source),
                    Match = Get(ColumnRole.Match),
                    RequiresExplicitUpgrade = kind == WingetTableKind.Upgrade && t > 0,
                });
            }
        }

        return packages;
    }

    /// <summary>
    /// <c>winget show</c>: "Found Name [Id]" then "Key: value" lines; an
    /// indented line continues the previous value (descriptions wrap).
    /// </summary>
    public static WingetDetails ParseShow(string output, string id)
    {
        var fields = new List<KeyValuePair<string, string>>();
        string? name = null;
        string? lastKey = null;
        string? pendingKey = null;
        var pending = new List<string>();

        void FlushPending()
        {
            // "Key:" followed by indented lines: a wrapped value, unless the lines are
            // "Sub key: value" pairs of a nested section (Installer, Agreements).
            if (pendingKey is not null && pending.Count > 0 && !pending.All(l => l.IndexOf(':') is > 0 and <= 40))
            {
                fields.Add(new KeyValuePair<string, string>(pendingKey, string.Join(' ', pending)));
            }

            pendingKey = null;
            pending.Clear();
        }

        foreach (var line in CleanLines(output))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var found = FoundPattern().Match(line);
            if (name is null && found.Success && found.Groups["id"].Value.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                name = found.Groups["name"].Value.Trim();
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                if (pendingKey is not null)
                {
                    pending.Add(line.Trim());
                }
                else if (lastKey is not null && fields.Count > 0 && fields[^1].Key == lastKey)
                {
                    var last = fields[^1];
                    fields[^1] = new KeyValuePair<string, string>(last.Key, (last.Value + " " + line.Trim()).Trim());
                }

                continue;
            }

            FlushPending();
            var colon = line.IndexOf(':');
            if (colon <= 0 || colon > 40)
            {
                lastKey = null;
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            lastKey = key;
            if (value.Length > 0)
            {
                fields.Add(new KeyValuePair<string, string>(key, value));
            }
            else
            {
                pendingKey = key;
            }
        }

        FlushPending();

        string? Field(params string[] keys) =>
            fields.FirstOrDefault(f => keys.Contains(f.Key, StringComparer.OrdinalIgnoreCase)).Value;

        var homepage = Field("Homepage", "Publisher Url");
        if (homepage is not null && !IsWebUrl(homepage))
        {
            homepage = null;
        }

        return new WingetDetails
        {
            Id = id,
            Name = name,
            Version = Field("Version"),
            Publisher = Field("Publisher"),
            Description = Field("Description", "Short Description"),
            Homepage = homepage ?? fields.Select(f => f.Value).FirstOrDefault(IsWebUrl),
            License = Field("License"),
            Fields = fields,
        };
    }

    /// <summary><c>winget source list</c>: a Name / Argument table.</summary>
    public static IReadOnlyList<WingetSource> ParseSources(string output)
    {
        var table = ParseTables(output).FirstOrDefault();
        if (table is null)
        {
            return [];
        }

        return table.Rows.Where(r => r.Count >= 2).Select(r => new WingetSource(r[0], r[1])).ToList();
    }

    /// <summary><c>winget --version</c> prints e.g. "v1.6.3133".</summary>
    public static Version? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output ?? string.Empty);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    /// <summary>
    /// <c>winget export --include-versions</c> writes full package ids per
    /// source. Used to resolve ids the tables truncated.
    /// </summary>
    public static IReadOnlyList<(string Id, string Source)> ParseExport(string json)
    {
        var result = new List<(string, string)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var source in sources.EnumerateArray())
            {
                var sourceName = source.TryGetProperty("SourceDetails", out var details) && details.TryGetProperty("Name", out var n)
                    ? n.GetString() ?? string.Empty
                    : string.Empty;
                if (!source.TryGetProperty("Packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var package in packages.EnumerateArray())
                {
                    if (package.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { Length: > 0 } value)
                    {
                        result.Add((value, sourceName));
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    /// <summary>
    /// Completes truncated ids ("Microsoft.VCRedist.2015+.…") from the full id
    /// list; an id stays truncated unless exactly one candidate matches.
    /// </summary>
    public static IReadOnlyList<WingetPackage> ResolveTruncatedIds(IReadOnlyList<WingetPackage> packages, IReadOnlyList<(string Id, string Source)> fullIds)
    {
        if (!packages.Any(p => p.IdTruncated))
        {
            return packages;
        }

        return packages.Select(p =>
        {
            if (!p.IdTruncated)
            {
                return p;
            }

            var prefix = p.Id.TrimEnd(WingetPackage.Ellipsis);
            var candidates = fullIds
                .Where(f => f.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && (string.IsNullOrEmpty(p.Source) || string.IsNullOrEmpty(f.Source) || string.Equals(f.Source, p.Source, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return candidates.Count == 1 ? p with { Id = candidates[0] } : p;
        }).ToList();
    }

    /// <summary>The last few meaningful lines (for error messages): no progress bars, no blanks.</summary>
    public static IReadOnlyList<string> MeaningfulTail(string output, int count = 3) =>
        CleanLines(output)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !WingetProgress.IsProgressLine(l))
            .TakeLast(count)
            .ToList();

    public static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private enum ColumnRole
    {
        Name,
        Id,
        Version,
        Available,
        Source,
        Match,
        Unknown,
    }

    private static ColumnRole[] Roles(WingetTable table, WingetTableKind kind)
    {
        var roles = new ColumnRole[table.Headers.Count];
        for (var c = 0; c < roles.Length; c++)
        {
            roles[c] = c switch
            {
                0 => ColumnRole.Name,
                1 => ColumnRole.Id,
                2 => ColumnRole.Version,
                _ => ColumnRole.Unknown,
            };
        }

        for (var c = 3; c < roles.Length; c++)
        {
            var header = table.Headers[c];
            if (AvailableHeaders.Contains(header))
            {
                roles[c] = ColumnRole.Available;
            }
            else if (SourceHeaders.Contains(header))
            {
                roles[c] = ColumnRole.Source;
            }
            else if (MatchHeaders.Contains(header))
            {
                roles[c] = ColumnRole.Match;
            }
            else
            {
                roles[c] = GuessRole(table, c, kind);
            }
        }

        // Older outputs without a Version column: never treat a source as a version.
        return roles;
    }

    private static ColumnRole GuessRole(WingetTable table, int column, WingetTableKind kind)
    {
        var values = table.Rows.Select(r => column < r.Count ? r[column] : string.Empty).Where(v => v.Length > 0).ToList();
        var isLast = column == table.Headers.Count - 1;
        if (values.Count == 0)
        {
            // Nothing to learn from: the last column is the source, an earlier one "available".
            return isLast ? ColumnRole.Source : (kind == WingetTableKind.Search ? ColumnRole.Match : ColumnRole.Available);
        }

        if (values.All(v => v.Contains(": ", StringComparison.Ordinal)))
        {
            return ColumnRole.Match;
        }

        if (values.All(v => v.Any(char.IsAsciiDigit)) && !isLast)
        {
            return ColumnRole.Available;
        }

        if (values.All(v => SourceNamePattern().IsMatch(v)))
        {
            return ColumnRole.Source;
        }

        return values.All(v => v.Any(char.IsAsciiDigit)) ? ColumnRole.Available : ColumnRole.Unknown;
    }

    private static string Cell(string line, IReadOnlyList<int> starts, int index)
    {
        var start = starts[index];
        var end = index + 1 < starts.Count ? starts[index + 1] : -1;
        return TextColumns.Slice(line, start, end).Trim();
    }

    private static bool IsDashLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 8 && trimmed.All(c => c == '-' || c == '─');
    }

    private static string ApplyBackspaces(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c == '\b')
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]")]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(@"^\S.*?\s(?<name>\S.*)\s\[(?<id>[^\]]+)\]\s*$")]
    private static partial Regex FoundPattern();

    [GeneratedRegex(@"v?(\d+\.\d+(?:\.\d+){0,2})")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]*$")]
    private static partial Regex SourceNamePattern();
}
