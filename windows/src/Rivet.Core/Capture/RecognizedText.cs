// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Capture;

/// <summary>One recognized word with its box in image pixels.</summary>
public sealed record OcrWord(string Text, RectD Bounds);

/// <summary>One recognized line (box = union of its words, image pixels).</summary>
public sealed record OcrLine(string Text, RectD Bounds, IReadOnlyList<OcrWord> Words);

/// <summary>A recognition result for one image.</summary>
public sealed record OcrPage(int ImageWidth, int ImageHeight, IReadOnlyList<OcrLine> Lines, string? LanguageTag = null);

/// <summary>
/// Joins recognized lines for "Copy text from screen" (spec 01 §6.17):
/// reading order by row (50 rows per image height, half a row of tolerance)
/// then by x; one line per row, or one paragraph without line breaks where
/// CJK text never gets a space between characters.
/// </summary>
public static class OcrTextJoiner
{
    public static string Join(OcrPage page, bool removeLineBreaks)
    {
        var height = Math.Max(1, page.ImageHeight);
        var lines = page.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => (Text: CleanCjkSpacing(l.Text), MinX: l.Bounds.X, RowKey: l.Bounds.MidY / height * 50))
            .ToList();
        lines.Sort((a, b) =>
        {
            if (Math.Abs(a.RowKey - b.RowKey) >= 0.5)
            {
                return a.RowKey.CompareTo(b.RowKey);
            }

            return a.MinX.CompareTo(b.MinX);
        });

        if (!removeLineBreaks)
        {
            return string.Join("\n", lines.Select(l => l.Text));
        }

        var builder = new StringBuilder();
        foreach (var text in lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0))
        {
            if (builder.Length > 0)
            {
                var previous = LastRune(builder);
                var next = Rune.GetRuneAt(text, 0);
                if (!(IsTightScript(previous) && IsTightScript(next)))
                {
                    builder.Append(' ');
                }
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// CJK punctuation, kana and Han ("tight scripts" written without spaces).
    /// Hangul is deliberately excluded: Korean uses spaces.
    /// </summary>
    public static bool IsTightScript(Rune rune)
    {
        var v = rune.Value;
        return v is >= 0x3000 and <= 0x312F
            or >= 0x3190 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFF01 and <= 0xFF9F
            or >= 0x20000 and <= 0x2FA1F;
    }

    /// <summary>
    /// Windows OCR separates every CJK character with a space; drop a space
    /// whose neighbours are both tight-script characters.
    /// </summary>
    public static string CleanCjkSpacing(string text)
    {
        if (text.IndexOf(' ') < 0)
        {
            return text;
        }

        var runes = text.EnumerateRunes().ToList();
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < runes.Count; i++)
        {
            var rune = runes[i];
            if (rune.Value == ' ' && i > 0 && i < runes.Count - 1 && IsTightScript(runes[i - 1]) && IsTightScript(runes[i + 1]))
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>Preferred OCR languages for the app language (§6.17), each followed by en-US.</summary>
    public static IReadOnlyList<string> PreferredLanguages(string appLanguageCode)
    {
        var primary = appLanguageCode switch
        {
            "pt-BR" => "pt-BR",
            "tr" => "tr-TR",
            "ru" => "ru-RU",
            "es" => "es-ES",
            "de" => "de-DE",
            "fr" => "fr-FR",
            "it" => "it-IT",
            "ja" => "ja-JP",
            "ko" => "ko-KR",
            "uk" => "uk-UA",
            "sk" => "sk-SK",
            "zh-Hans" => "zh-Hans",
            "zh-TW" or "zh-HK" => "zh-Hant",
            _ => null,
        };
        return primary is null ? ["en-US"] : [primary, "en-US"];
    }

    private static Rune LastRune(StringBuilder builder)
    {
        var last = builder[^1];
        if (char.IsLowSurrogate(last) && builder.Length >= 2 && char.IsHighSurrogate(builder[^2]))
        {
            return new Rune(builder[^2], last);
        }

        return Rune.IsValid(last) ? new Rune(last) : Rune.ReplacementChar;
    }
}

/// <summary>A decoded 2-D code with its box in image pixels.</summary>
public sealed record DetectedCode(string Payload, RectD Bounds, string Format);

/// <summary>QR payload handling (spec 01 §3.17, §6.18).</summary>
public static class QrPayloads
{
    /// <summary>Payloads in reading order (same row rule as OCR), whitespace-only dropped, joined by newlines.</summary>
    public static string Join(IReadOnlyList<DetectedCode> codes, int imageHeight)
    {
        var height = Math.Max(1, imageHeight);
        var ordered = codes
            .Where(c => !string.IsNullOrWhiteSpace(c.Payload))
            .Select(c => (c.Payload, c.Bounds.X, RowKey: c.Bounds.MidY / height * 50))
            .ToList();
        ordered.Sort((a, b) => Math.Abs(a.RowKey - b.RowKey) >= 0.5 ? a.RowKey.CompareTo(b.RowKey) : a.X.CompareTo(b.X));
        return string.Join("\n", ordered.Select(o => o.Payload));
    }

    /// <summary>The link to offer: only for exactly one code whose payload is one http(s) URL with a host.</summary>
    public static Uri? OpenableUrl(IReadOnlyList<DetectedCode> codes)
    {
        var usable = codes.Where(c => !string.IsNullOrWhiteSpace(c.Payload)).ToList();
        return usable.Count == 1 ? OpenableUrl(usable[0].Payload) : null;
    }

    public static Uri? OpenableUrl(string payload)
    {
        var trimmed = payload.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host) ? uri : null;
    }
}

/// <summary>Formats "5 min ago" style times for the history list.</summary>
public static class RelativeTime
{
    public static (string Unit, long Value) Bucket(DateTimeOffset then, DateTimeOffset now)
    {
        var elapsed = now - then;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return ("now", 0);
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return ("minutes", (long)elapsed.TotalMinutes);
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return ("hours", (long)elapsed.TotalHours);
        }

        return ("days", (long)elapsed.TotalDays);
    }

    public static string Date(DateTimeOffset then, CultureInfo culture) => then.LocalDateTime.ToString("d", culture);
}
