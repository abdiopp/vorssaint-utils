// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Clipboard;
using Xunit;

namespace Rivet.Core.Tests.Clipboard;

/// <summary>Spec 06 §6.11: the rule data and every input/output pair pinned by the macOS tests.</summary>
public class UrlCleaningTests
{
    [Fact]
    public void Rule_data_matches_the_macOS_source()
    {
        Assert.Equal(31, UrlCleaning.TrackedParameters.Count);
        Assert.Equal(10, UrlCleaning.HostParameters.Count);
        Assert.Equal(78, UrlCleaning.HostParameters.Values.Sum(v => v.Count));
        Assert.Equal(["si", "pp", "feature", "kw"], UrlCleaning.HostParameters["youtube.com"]);
        Assert.Contains("$deep_link", UrlCleaning.HostParameters["reddit.com"]);
        Assert.Equal("", UrlCleaning.AllSites);
        Assert.Equal("utm_*", UrlCleaning.UtmWildcard);
    }

    public static TheoryData<string, string, string[]> PinnedCases => new()
    {
        { "https://example.com/path?utm_source=news&id=42&fbclid=abc", "https://example.com/path?id=42", ["utm_source", "fbclid"] },
        { " https://example.com/?GCLID=one&utm_campaign=x#section ", "https://example.com/#section", ["GCLID", "utm_campaign"] },
        { "https://example.com/?id=42", "https://example.com/?id=42", [] },
        { "https://youtu.be/TImSMeurR84?si=Xq1&t=42", "https://youtu.be/TImSMeurR84?t=42", ["si"] },
        { "https://x.com/user/status/1?s=20&t=abc", "https://x.com/user/status/1", ["s", "t"] },
        { "https://example.com/?si=keep&t=keep&s=keep", "https://example.com/?si=keep&t=keep&s=keep", [] },
        { "https://open.spotify.com/track/abc?si=xyz", "https://open.spotify.com/track/abc", ["si"] },
        { "https://www.reddit.com/r/swift/comments/abc/?%24deep_link=true&%243p=x&share_id=y&sort=new", "https://www.reddit.com/r/swift/comments/abc/?sort=new", ["$deep_link", "$3p", "share_id"] },
        { "https://www.bilibili.com/video/BV1xx411c7mD/?p=3&t=90&vd_source=abc", "https://www.bilibili.com/video/BV1xx411c7mD/?p=3&t=90", ["vd_source"] },
        { "https://example.com/?flag&utm_medium&utm_source=news&id=1", "https://example.com/?flag&id=1", ["utm_medium", "utm_source"] },
        { "https://example.com/?%75tm_source=news&id=1", "https://example.com/?id=1", ["utm_source"] },
        { "https://example.com/?redirect=https%3A%2F%2Fexample.org%2F%3Futm_source%3Dkeepme%26z%3D9&gclid=1", "https://example.com/?redirect=https%3A%2F%2Fexample.org%2F%3Futm_source%3Dkeepme%26z%3D9", ["gclid"] },
        { "https://example.com/p?utm_source=x&", "https://example.com/p?", ["utm_source"] },
        { "https://example.com/p#fragment?utm_source=x", "https://example.com/p#fragment?utm_source=x", [] },
    };

    [Theory]
    [MemberData(nameof(PinnedCases))]
    public void Pinned_cases_clean_byte_exactly(string input, string expected, string[] removed)
    {
        var result = UrlCleaning.Clean(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Url);
        Assert.Equal(removed, result.Removed);
    }

    [Fact]
    public void A_switched_off_site_rule_stays_in_the_link()
    {
        var rules = UrlCleanerRules.FromStorage("", "", "youtube.com|si");
        var result = UrlCleaning.Clean("https://www.youtube.com/watch?v=1&si=x&feature=share", rules)!;
        Assert.Equal("https://www.youtube.com/watch?v=1&si=x", result.Url);
        Assert.Equal(["feature"], result.Removed);
    }

    [Fact]
    public void Two_links_are_not_one_link()
    {
        Assert.Null(UrlCleaning.Clean("https://a.example/x?utm_source=x\nhttps://b.example/y"));
        Assert.Null(UrlCleaning.Clean("not a url"));
        Assert.Null(UrlCleaning.Clean("ftp://example.com/?utm_source=x"));
    }

    [Theory]
    [InlineData("https://example.com/?")]
    [InlineData("https://example.com/?a=1&a=2")]
    [InlineData("https://example.com/?q=a+b&r=%5B%5D&s=%20x")]
    [InlineData("https://user:pw@example.com:8080/x?y=1")]
    [InlineData("https://[::1]:8443/x?y=1")]
    [InlineData("https://example.com/café/näive?x=1")]
    public void Links_with_nothing_to_remove_come_back_byte_for_byte(string input)
    {
        var result = UrlCleaning.Clean(input)!;
        Assert.Equal(input, result.Url);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Malformed_percent_names_still_hit_the_utm_prefix()
    {
        var result = UrlCleaning.Clean("https://example.com/?utm_%ff=1&id=2")!;
        Assert.Equal("https://example.com/?id=2", result.Url);
        Assert.Equal(["utm_%ff"], result.Removed);
    }

    [Fact]
    public void Switching_off_utm_wildcard_keeps_utm_names()
    {
        var rules = UrlCleanerRules.FromStorage("", "", "|utm_*");
        var result = UrlCleaning.Clean("https://example.com/?utm_source=x&gclid=1", rules)!;
        Assert.Equal("https://example.com/?utm_source=x", result.Url);
    }

    [Fact]
    public void User_names_apply_globally_and_per_site()
    {
        var rules = UrlCleanerRules.FromStorage("ref, Campaign", "example.com|src", "");
        Assert.Equal("https://a.example.com/?keep=1", UrlCleaning.Clean("https://a.example.com/?ref=1&campaign=2&src=3&keep=1", rules)!.Url);
        Assert.Equal("https://other.org/?src=3", UrlCleaning.Clean("https://other.org/?ref=1&src=3", rules)!.Url);
    }

    [Fact]
    public void Rule_storage_round_trips_sorted()
    {
        var rules = UrlCleanerRules.FromStorage("zeta,\nalpha", "youtube.com|zz, a.com|b", "|ref,youtube.com|si");
        Assert.Equal("alpha, zeta", rules.CustomParametersStorage());
        Assert.Equal("a.com|b,youtube.com|zz", rules.SiteParametersStorage());
        Assert.Equal("|ref,youtube.com|si", rules.DisabledParametersStorage());
        var again = UrlCleanerRules.FromStorage(rules.CustomParametersStorage(), rules.SiteParametersStorage(), rules.DisabledParametersStorage());
        Assert.Equal(rules.DisabledParametersStorage(), again.DisabledParametersStorage());
    }

    [Fact]
    public void Rule_groups_list_all_sites_first_with_utm_wildcard()
    {
        var rules = UrlCleanerRules.FromStorage("", "example.com|tag", "|fbclid");
        var groups = UrlCleaning.RuleGroups(rules);
        Assert.Equal("", groups[0].Site);
        Assert.Equal("utm_*", groups[0].Entries[0].Name);
        Assert.False(groups[0].Entries.Single(e => e.Name == "fbclid").IsEnabled);
        Assert.Equal(groups[0].Entries.Count - 1, groups[0].EnabledCount);
        var sites = groups.Skip(1).Select(g => g.Site).ToList();
        Assert.Equal(sites.OrderBy(s => s, StringComparer.Ordinal), sites);
        var example = groups.Single(g => g.Site == "example.com");
        Assert.Equal("tag", example.Entries.Single().Name);
        Assert.False(example.Entries.Single().IsBuiltIn);
    }

    [Fact]
    public void Rule_edits_are_stored_as_a_difference()
    {
        var rules = UrlCleanerRules.None;
        rules = UrlCleaning.SetEnabled(rules, "youtube.com", "si", false);
        Assert.Equal("youtube.com|si", rules.DisabledParametersStorage());
        rules = UrlCleaning.AddName(rules, "youtube.com", "si");
        Assert.Equal("", rules.DisabledParametersStorage());
        rules = UrlCleaning.AddName(rules, "", "myref");
        Assert.Equal("myref", rules.CustomParametersStorage());
        rules = UrlCleaning.SetSiteEnabled(rules, "x.com", false);
        Assert.Equal(7, rules.DisabledFor("x.com").Count);
        rules = UrlCleaning.SetSiteEnabled(rules, "x.com", true);
        Assert.Empty(rules.DisabledFor("x.com"));
        rules = UrlCleaning.DeleteName(rules, "", "myref");
        Assert.Equal("", rules.CustomParametersStorage());
    }

    [Theory]
    [InlineData(" UTM_Foo ", "utm_foo")]
    [InlineData("a b", null)]
    [InlineData("a|b", null)]
    [InlineData("a=b", null)]
    [InlineData("", null)]
    public void Parameter_names_are_validated(string input, string? expected) =>
        Assert.Equal(expected, UrlCleaning.ValidParameterName(input));

    [Theory]
    [InlineData("https://www.Example.com/path?x", "example.com")]
    [InlineData("sub.example.co.uk", "sub.example.co.uk")]
    [InlineData("localhost", null)]
    [InlineData(".example.com", null)]
    [InlineData("exa mple.com", null)]
    public void Site_keys_are_validated(string input, string? expected) =>
        Assert.Equal(expected, UrlCleaning.ValidSiteKey(input));

    [Fact]
    public void Outcomes_map_to_the_shared_messages()
    {
        Assert.Equal(UrlCleanOutcomeKind.NotAUrl, UrlCleanOutcome.From("hello", UrlCleaning.Clean("hello")).Kind);
        Assert.Equal(UrlCleanOutcomeKind.Unchanged, UrlCleanOutcome.From("https://a.com/?x=1", UrlCleaning.Clean("https://a.com/?x=1")).Kind);
        var removed = UrlCleanOutcome.From("https://a.com/?utm_source=1", UrlCleaning.Clean("https://a.com/?utm_source=1"));
        Assert.Equal(UrlCleanOutcomeKind.Removed, removed.Kind);
        Assert.Equal("Removed utm_source", UrlCleanerService.Message(removed));
    }

    [Fact]
    public void Html_flavour_must_be_only_the_link()
    {
        const string link = "https://example.com/?utm_source=x";
        Assert.True(RichText.HtmlIsOnlyLink($"<html><head><title>t</title></head><body><a href=\"{link}\">{link}</a></body></html>", link));
        Assert.True(RichText.HtmlIsOnlyLink("<span></span>", link));
        Assert.False(RichText.HtmlIsOnlyLink($"<a href=\"{link}\">x</a><img src=a.png>", link));
        Assert.False(RichText.HtmlIsOnlyLink($"<a href=\"https://other.org/\">{link}</a>", link));
        Assert.False(RichText.HtmlIsOnlyLink("<p>Some other text</p>", link));
        Assert.True(RichText.HtmlIsOnlyLink("<a href='HTTPS://EXAMPLE.COM?utm_source=x'>go</a>", "https://example.com/?utm_source=x"));
    }

    [Fact]
    public void Rewrite_safety_refuses_files_images_and_unknown_formats()
    {
        Assert.True(ClipboardFormats.CanRewrite(["CF_UNICODETEXT", "CF_TEXT", "CF_LOCALE", "CF_OEMTEXT"]));
        Assert.True(ClipboardFormats.CanRewrite(["CF_UNICODETEXT", "HTML Format", "Chromium internal source URL"]));
        Assert.False(ClipboardFormats.CanRewrite(["CF_UNICODETEXT", "CF_HDROP"]));
        Assert.False(ClipboardFormats.CanRewrite(["CF_UNICODETEXT", "PNG"]));
        Assert.False(ClipboardFormats.CanRewrite(["CF_UNICODETEXT", "Some App Private Format"]));
        Assert.False(ClipboardFormats.CanRewrite([]));
    }
}
