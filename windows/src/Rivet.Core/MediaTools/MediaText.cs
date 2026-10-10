// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Localization;

namespace Rivet.Core.Modules.MediaTools;

/// <summary>Sizes and languages for the media tools.</summary>
public static class MediaText
{
    /// <summary>1000-based units ("12.3 MB"), like macOS ByteCountFormatter (StrFormatByteSize is 1024-based).</summary>
    public static string FormatBytes(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var value = Math.Abs((double)bytes);
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        if (unit == 0)
        {
            return string.Format(culture, "{0} {1}", (long)value, units[0]);
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, culture) + " " + units[unit];
    }

    /// <summary>
    /// OCR languages for the app language, followed by English (spec 07 §6.6).
    /// Windows has real language packs, so Slovak maps to sk-SK instead of English only.
    /// </summary>
    public static IReadOnlyList<string> OcrLanguages(AppLanguage language)
    {
        var primary = language switch
        {
            AppLanguage.PtBR => "pt-BR",
            AppLanguage.Tr => "tr-TR",
            AppLanguage.Ru => "ru-RU",
            AppLanguage.Es => "es-ES",
            AppLanguage.De => "de-DE",
            AppLanguage.Fr => "fr-FR",
            AppLanguage.It => "it-IT",
            AppLanguage.Ja => "ja-JP",
            AppLanguage.Ko => "ko-KR",
            AppLanguage.Uk => "uk-UA",
            AppLanguage.ZhHans => "zh-Hans",
            AppLanguage.ZhTW or AppLanguage.ZhHK => "zh-Hant",
            AppLanguage.Sk => "sk-SK",
            _ => null,
        };
        return primary is null ? ["en-US"] : [primary, "en-US"];
    }

    /// <summary>"A to B" size line plus whether the output grew.</summary>
    public static (string Line, bool Grew, long Delta) SizeLine(long before, long after) =>
        (L.Format("Strings.mediaResultSizeFormat", FormatBytes(before), FormatBytes(after)), after > before, before - after);
}
