// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Xunit;

namespace Rivet.Core.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("%@ of %@", new object[] { "a", "b" }, "a of b")]
    [InlineData("%1$d of %2$d features installed", new object[] { 3, 44 }, "3 of 44 features installed")]
    [InlineData("%2$@, %1$@", new object[] { "x", "y" }, "y, x")]
    [InlineData("%.1f GB", new object[] { 2.345 }, "2.3 GB")]
    [InlineData("100%% done", new object[0], "100% done")]
    [InlineData("%ld items", new object[] { 7L }, "7 items")]
    [InlineData("%03d", new object[] { 7 }, "007")]
    [InlineData("%s and %d", new object[] { "s", 2 }, "s and 2")]
    public void Printf_formats_like_apple(string format, object[] args, string expected) =>
        Assert.Equal(expected, PrintfFormatter.Format(format, CultureInfo.InvariantCulture, args));

    [Fact]
    public void Printf_reports_argument_signatures()
    {
        Assert.Equal(["int", "int"], PrintfFormatter.ArgumentSignature("%1$d of %2$d"));
        Assert.Equal(["object", "double"], PrintfFormatter.ArgumentSignature("%@ at %.2f"));
        Assert.Empty(PrintfFormatter.ArgumentSignature("100%% sure"));
    }

    [Theory]
    [InlineData(CountAgreement.OneAndMany, 1, PluralForm.One)]
    [InlineData(CountAgreement.OneAndMany, 0, PluralForm.Many)]
    [InlineData(CountAgreement.OneAndMany, 21, PluralForm.Many)]
    [InlineData(CountAgreement.ByLastDigits, 1, PluralForm.One)]
    [InlineData(CountAgreement.ByLastDigits, 21, PluralForm.One)]
    [InlineData(CountAgreement.ByLastDigits, 3, PluralForm.Few)]
    [InlineData(CountAgreement.ByLastDigits, 12, PluralForm.Many)]
    [InlineData(CountAgreement.ByLastDigits, 111, PluralForm.Many)]
    [InlineData(CountAgreement.ByLastDigits, 25, PluralForm.Many)]
    [InlineData(CountAgreement.ByWholeNumber, 1, PluralForm.One)]
    [InlineData(CountAgreement.ByWholeNumber, 4, PluralForm.Few)]
    [InlineData(CountAgreement.ByWholeNumber, 22, PluralForm.Many)]
    public void Plural_forms_follow_each_language(CountAgreement agreement, long count, PluralForm expected) =>
        Assert.Equal(expected, agreement.FormFor(count));

    [Theory]
    [InlineData("zh-HK", AppLanguage.ZhHK)]
    [InlineData("zh-Hant-TW", AppLanguage.ZhTW)]
    [InlineData("zh-Hant", AppLanguage.ZhTW)]
    [InlineData("zh-CN", AppLanguage.ZhHans)]
    [InlineData("pt-PT", AppLanguage.PtBR)]
    [InlineData("de-AT", AppLanguage.De)]
    [InlineData("nl-NL", AppLanguage.EnUS)]
    public void Default_language_maps_like_macos(string preferred, AppLanguage expected) =>
        Assert.Equal(expected, AppLanguages.FromPreferredLanguages([preferred]));

    [Fact]
    public void Every_language_has_the_same_keys_as_english()
    {
        var english = new Localizer(AppLanguage.EnUS);
        var keys = english.AllKeys().Where(k => !k.StartsWith("win.", StringComparison.Ordinal)).ToHashSet();
        foreach (var language in AppLanguages.All)
        {
            var localizer = new Localizer(language);
            var missing = keys.Where(k => !localizer.TryGet(k, out _)).Take(5).ToList();
            Assert.True(missing.Count == 0, $"{language.Code()} misses {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void Format_strings_keep_their_placeholders_in_every_language()
    {
        var metaPath = Path.Combine(AppContext.BaseDirectory, "Data", "_meta.json");
        using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        var formatKeys = new List<string>();
        foreach (var entry in meta.RootElement.EnumerateObject())
        {
            if (entry.Value.TryGetProperty("args", out var args) && args.GetArrayLength() > 0)
            {
                formatKeys.Add(entry.Name);
            }
        }

        Assert.NotEmpty(formatKeys);
        var english = new Localizer(AppLanguage.EnUS);
        foreach (var language in AppLanguages.All)
        {
            var localizer = new Localizer(language);
            foreach (var key in formatKeys)
            {
                var expected = PrintfFormatter.ArgumentSignature(english.Get(key));
                var actual = PrintfFormatter.ArgumentSignature(localizer.Get(key));
                Assert.True(expected.SequenceEqual(actual), $"{language.Code()} {key}: [{string.Join(',', actual)}] vs [{string.Join(',', expected)}]");
            }
        }
    }

    [Fact]
    public void Feature_titles_and_descriptions_exist_in_every_language()
    {
        foreach (var language in AppLanguages.All)
        {
            var localizer = new Localizer(language);
            foreach (var feature in FeatureCatalog.All)
            {
                Assert.True(localizer.TryGet(feature.TitleKey, out _), $"{language.Code()} {feature.TitleKey}");
                Assert.True(localizer.TryGet(feature.DescriptionKey, out _), $"{language.Code()} {feature.DescriptionKey}");
            }
        }
    }

    [Fact]
    public void Brand_name_is_replaced_by_the_product_name()
    {
        var localizer = new Localizer(AppLanguage.EnUS);
        Assert.DoesNotContain("Vorssaint", localizer.Get("Strings.advancedUninstallButton"), StringComparison.Ordinal);
        Assert.Contains(App.AppIdentity.DisplayName, localizer.Get("Strings.advancedUninstallButton"), StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_overrides_win_over_macos_strings_in_english_only()
    {
        var english = new Localizer(AppLanguage.EnUS);
        Assert.Contains("PC", english.Get("hub.presetsCaption"), StringComparison.Ordinal);
        var german = new Localizer(AppLanguage.De);
        Assert.NotEqual(english.Get("hub.presetsCaption"), german.Get("hub.presetsCaption"));
    }

    [Fact]
    public void Missing_keys_fall_back_to_the_key()
    {
        var localizer = new Localizer(AppLanguage.Fr);
        Assert.Equal("no.such.key", localizer.Get("no.such.key"));
    }

    [Fact]
    public void Observe_pushes_after_language_change()
    {
        var localizer = new Localizer(AppLanguage.EnUS);
        var seen = new List<string>();
        using var _ = localizer.Observe("Strings.panelQuit").Subscribe(new Observer(seen.Add));
        localizer.Language = AppLanguage.De;
        Assert.Equal(2, seen.Count);
        Assert.NotEqual(seen[0], seen[1]);
    }

    private sealed class Observer(Action<string> next) : IObserver<string>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(string value) => next(value);
    }
}
