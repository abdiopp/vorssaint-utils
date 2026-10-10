// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rivet.Core.Modules.MediaTools;

/// <summary>
/// Output names and collision handling (spec 07 §3.6.8), with the Windows
/// rules added: <c>&lt; &gt; : " | ? *</c> and control characters become "-",
/// reserved device names get a suffix, and trailing dots or spaces go.
/// </summary>
public static partial class MediaNaming
{
    public const int MaxNameBytes = 255;
    public const int SuffixReserve = 4;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The input name without extension, trimmed, leading dots removed;
    /// "Output" if nothing remains. ".Clip.mov" → "Clip", ".mov" → "mov", "..." → "Output".
    /// </summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        var lastDot = name.LastIndexOf('.');
        var stem = lastDot > 0 ? name[..lastDot] : name;
        stem = stem.Trim().TrimStart('.');
        return stem.Length == 0 ? "Output" : stem;
    }

    /// <summary>Makes a file name stem safe for Windows (no extension handling).</summary>
    public static string Sanitize(string stem)
    {
        var builder = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            builder.Append(c is '/' or ':' or '\\' or '<' or '>' or '"' or '|' or '?' or '*' || char.IsControl(c) ? '-' : c);
        }

        var cleaned = builder.ToString().Trim().Trim('.', '-', ' ');
        if (cleaned.Length == 0)
        {
            return "Output";
        }

        if (ReservedNames.Contains(cleaned) || (cleaned.IndexOf('.') is var dot and > 0 && ReservedNames.Contains(cleaned[..dot])))
        {
            cleaned += "_";
        }

        return cleaned;
    }

    /// <summary>Cuts <paramref name="stem"/> on character boundaries so stem + "." + extension (+ a 4-byte number suffix) fits 255 UTF-8 bytes.</summary>
    public static string Truncate(string stem, string extension, int reserve = SuffixReserve)
    {
        var limit = MaxNameBytes - (extension.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(extension) + 1) - reserve;
        if (Encoding.UTF8.GetByteCount(stem) <= limit)
        {
            return stem;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(stem);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > limit)
            {
                break;
            }

            builder.Append(element);
            bytes += size;
        }

        var result = builder.ToString().TrimEnd('.', ' ');
        return result.Length == 0 ? "Output" : result;
    }

    /// <summary>A safe, truncated file name: sanitized stem + "." + extension.</summary>
    public static string FileName(string stem, string extension) =>
        extension.Length == 0 ? Truncate(Sanitize(stem), extension) : $"{Truncate(Sanitize(stem), extension)}.{extension}";

    /// <summary>
    /// A free path in <paramref name="folder"/>: the name itself, else " 2", " 3", …
    /// (unbounded), re-truncating the stem so the suffix fits. <paramref name="usedInRun"/>
    /// holds names already chosen in this batch.
    /// </summary>
    public static string Unique(string folder, string stem, string extension, ISet<string>? usedInRun = null, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var safe = Sanitize(stem);
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? string.Empty : $" {n.ToString(CultureInfo.InvariantCulture)}";
            var reserve = Math.Max(SuffixReserve, Encoding.UTF8.GetByteCount(suffix));
            var name = Truncate(safe, extension, reserve) + suffix + (extension.Length == 0 ? string.Empty : "." + extension);
            var path = Path.Combine(folder, name);
            if (path.Length > 259 && n == 1 && !LongPathsLikelyEnabled)
            {
                // Keep within MAX_PATH by shortening the stem further.
                var excess = path.Length - 259;
                safe = safe.Length > excess + 1 ? safe[..^(excess + 1)].TrimEnd('.', ' ') : "Output";
                n = 0;
                continue;
            }

            if ((usedInRun is null || !usedInRun.Contains(name)) && !exists(path))
            {
                usedInRun?.Add(name);
                return path;
            }
        }
    }

    /// <summary>Set by the platform when Windows long paths are enabled.</summary>
    public static bool LongPathsLikelyEnabled { get; set; } = !OperatingSystem.IsWindows();

    public static string DefaultVideoStem(string input) => BaseName(input) + "-compressed";

    public static string DefaultGifStem(string input) => BaseName(input);

    public static string DefaultTextStem(string input) => BaseName(input) + "-text";

    /// <summary>
    /// Expands a rename pattern (spec 07 §3.6.6): {name}, {index}, {index:0N},
    /// {counter}, {counter:0N} (N 2–6), {date}, {time}, {datetime} (UTC),
    /// {width}, {height}, {format}, {ext}. An empty pattern means {name};
    /// unknown tokens stay literal. The result is not yet sanitized.
    /// </summary>
    public static string ExpandPattern(string pattern, string inputPath, int index, int width, int height, string extension, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "{name}";
        }

        return TokenRegex().Replace(pattern, match =>
        {
            var token = match.Groups["token"].Value;
            var pad = match.Groups["pad"].Success ? int.Parse(match.Groups["pad"].Value, CultureInfo.InvariantCulture) : 0;
            return token switch
            {
                "name" when pad == 0 => BaseName(inputPath),
                "index" or "counter" when pad == 0 || pad is >= 2 and <= 6 => index.ToString(pad == 0 ? "D" : "D" + pad, CultureInfo.InvariantCulture),
                "date" when pad == 0 => utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                "time" when pad == 0 => utcNow.ToString("HHmmss", CultureInfo.InvariantCulture),
                "datetime" when pad == 0 => utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                "width" when pad == 0 => width.ToString(CultureInfo.InvariantCulture),
                "height" when pad == 0 => height.ToString(CultureInfo.InvariantCulture),
                "format" or "ext" when pad == 0 => extension,
                _ => match.Value,
            };
        });
    }

    /// <summary>Tokens offered by the "{…}" menu.</summary>
    public static IReadOnlyList<string> Tokens { get; } =
        ["{name}", "{index}", "{index:03}", "{counter}", "{date}", "{time}", "{datetime}", "{width}", "{height}", "{format}"];

    [GeneratedRegex(@"\{(?<token>[a-z]+)(?::0(?<pad>\d))?\}")]
    private static partial Regex TokenRegex();
}
