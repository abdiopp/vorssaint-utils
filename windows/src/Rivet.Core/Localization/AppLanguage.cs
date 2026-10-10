// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Localization;

/// <summary>The 15 UI languages. The code doubles as the stored value of <c>appLanguage</c>.</summary>
public enum AppLanguage
{
    EnUS,
    PtBR,
    Tr,
    Ru,
    Es,
    Sk,
    De,
    Fr,
    It,
    Ja,
    Ko,
    ZhHans,
    ZhTW,
    ZhHK,
    Uk,
}

/// <summary>How a language picks between grammatical number forms.</summary>
public enum CountAgreement
{
    /// <summary>1 → one, everything else → many.</summary>
    OneAndMany,

    /// <summary>Russian/Ukrainian: last digit 1 → one, 2–4 → few, else many; 11–14 → many.</summary>
    ByLastDigits,

    /// <summary>Slovak: 1 → one, 2–4 → few, else many.</summary>
    ByWholeNumber,
}

public enum PluralForm
{
    One,
    Few,
    Many,
}

public static class AppLanguages
{
    public static IReadOnlyList<AppLanguage> All { get; } = Enum.GetValues<AppLanguage>();

    public static string Code(this AppLanguage language) => language switch
    {
        AppLanguage.EnUS => "en-US",
        AppLanguage.PtBR => "pt-BR",
        AppLanguage.Tr => "tr",
        AppLanguage.Ru => "ru",
        AppLanguage.Es => "es",
        AppLanguage.Sk => "sk",
        AppLanguage.De => "de",
        AppLanguage.Fr => "fr",
        AppLanguage.It => "it",
        AppLanguage.Ja => "ja",
        AppLanguage.Ko => "ko",
        AppLanguage.ZhHans => "zh-Hans",
        AppLanguage.ZhTW => "zh-TW",
        AppLanguage.ZhHK => "zh-HK",
        AppLanguage.Uk => "uk",
        _ => "en-US",
    };

    /// <summary>The language's own name, shown in the language menu.</summary>
    public static string NativeName(this AppLanguage language) => language switch
    {
        AppLanguage.EnUS => "English (US)",
        AppLanguage.PtBR => "Português (Brasil)",
        AppLanguage.Tr => "Türkçe",
        AppLanguage.Ru => "Русский",
        AppLanguage.Es => "Español",
        AppLanguage.Sk => "Slovenčina",
        AppLanguage.De => "Deutsch",
        AppLanguage.Fr => "Français",
        AppLanguage.It => "Italiano",
        AppLanguage.Ja => "日本語",
        AppLanguage.Ko => "한국어",
        AppLanguage.ZhHans => "简体中文",
        AppLanguage.ZhTW => "繁體中文（台灣）",
        AppLanguage.ZhHK => "繁體中文（香港）",
        AppLanguage.Uk => "Українська",
        _ => "English (US)",
    };

    public static CultureInfo Culture(this AppLanguage language) => language switch
    {
        AppLanguage.Tr => CultureInfo.GetCultureInfo("tr-TR"),
        AppLanguage.Ru => CultureInfo.GetCultureInfo("ru-RU"),
        AppLanguage.Es => CultureInfo.GetCultureInfo("es-ES"),
        AppLanguage.Sk => CultureInfo.GetCultureInfo("sk-SK"),
        AppLanguage.De => CultureInfo.GetCultureInfo("de-DE"),
        AppLanguage.Fr => CultureInfo.GetCultureInfo("fr-FR"),
        AppLanguage.It => CultureInfo.GetCultureInfo("it-IT"),
        AppLanguage.Ja => CultureInfo.GetCultureInfo("ja-JP"),
        AppLanguage.Ko => CultureInfo.GetCultureInfo("ko-KR"),
        AppLanguage.ZhHans => CultureInfo.GetCultureInfo("zh-CN"),
        AppLanguage.ZhTW => CultureInfo.GetCultureInfo("zh-TW"),
        AppLanguage.ZhHK => CultureInfo.GetCultureInfo("zh-HK"),
        AppLanguage.Uk => CultureInfo.GetCultureInfo("uk-UA"),
        AppLanguage.PtBR => CultureInfo.GetCultureInfo("pt-BR"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };

    public static CountAgreement CountAgreement(this AppLanguage language) => language switch
    {
        AppLanguage.Ru or AppLanguage.Uk => Localization.CountAgreement.ByLastDigits,
        AppLanguage.Sk => Localization.CountAgreement.ByWholeNumber,
        _ => Localization.CountAgreement.OneAndMany,
    };

    public static PluralForm FormFor(this CountAgreement agreement, long count)
    {
        var n = Math.Abs(count);
        switch (agreement)
        {
            case Localization.CountAgreement.ByLastDigits:
                var lastTwo = n % 100;
                var last = n % 10;
                if (lastTwo is >= 11 and <= 14) return PluralForm.Many;
                if (last == 1) return PluralForm.One;
                if (last is >= 2 and <= 4) return PluralForm.Few;
                return PluralForm.Many;
            case Localization.CountAgreement.ByWholeNumber:
                if (n == 1) return PluralForm.One;
                if (n is >= 2 and <= 4) return PluralForm.Few;
                return PluralForm.Many;
            default:
                return n == 1 ? PluralForm.One : PluralForm.Many;
        }
    }

    public static bool TryParseCode(string? code, out AppLanguage language)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Code(), code, StringComparison.OrdinalIgnoreCase))
            {
                language = candidate;
                return true;
            }
        }

        language = AppLanguage.EnUS;
        return false;
    }

    /// <summary>
    /// The default language from the OS preferred UI languages, using the
    /// macOS app's mapping: zh-HK/zh-Hant-HK → zh-HK; zh-TW/zh-Hant-TW/zh-Hant
    /// → zh-TW; then a prefix match on pt, tr, ru, es, sk, de, fr, it, ja, ko,
    /// uk and zh (→ zh-Hans); otherwise English.
    /// </summary>
    public static AppLanguage FromPreferredLanguages(IEnumerable<string> preferred)
    {
        var first = preferred.FirstOrDefault() ?? "en";
        var lower = first.ToLowerInvariant();
        if (lower is "zh-hk" or "zh-hant-hk")
        {
            return AppLanguage.ZhHK;
        }

        if (lower is "zh-tw" or "zh-hant-tw" or "zh-hant" || lower.StartsWith("zh-hant-", StringComparison.Ordinal))
        {
            return AppLanguage.ZhTW;
        }

        (string Prefix, AppLanguage Language)[] prefixes =
        [
            ("pt", AppLanguage.PtBR), ("tr", AppLanguage.Tr), ("ru", AppLanguage.Ru), ("es", AppLanguage.Es),
            ("sk", AppLanguage.Sk), ("de", AppLanguage.De), ("fr", AppLanguage.Fr), ("it", AppLanguage.It),
            ("ja", AppLanguage.Ja), ("ko", AppLanguage.Ko), ("uk", AppLanguage.Uk), ("zh", AppLanguage.ZhHans),
        ];
        foreach (var (prefix, language) in prefixes)
        {
            if (first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        return AppLanguage.EnUS;
    }
}
