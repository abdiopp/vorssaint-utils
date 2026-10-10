// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Clipboard;
using Rivet.Core.Launcher;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests.CommandBar;

/// <summary>Spec 06 §6.1, §6.3–§6.5, §6.10, §6.15: folding, ranking, learning, links, emoji.</summary>
public class RankingTests
{
    private static CommandRow Row(string id, string title, string keywords = "", string? feature = null, int role = 0, bool live = false) =>
        new() { Id = id, Title = title, Keywords = keywords, FeatureId = feature, Role = role, IsLive = live };

    [Fact]
    public void Command_folding_is_locale_independent_and_collapses_whitespace()
    {
        Assert.Equal("istanbul i", TextFold.ForCommand("İSTANBUL  I"));
        Assert.Equal("cafe tokyo", TextFold.ForCommand("Café\u3000Tōkyō"));
        Assert.Equal("ab", TextFold.ForCommand("a\u200Bb"));
        Assert.Equal("full width", TextFold.ForCommand("ＦＵＬＬ　ｗｉｄｔｈ"));
        Assert.Equal("a  b", TextFold.ForClipboard(" A\t\nB "));
    }

    [Theory]
    [InlineData("screen", "screen", 140)]
    [InlineData("scr", "screen", 80)]
    [InlineData("reen", "screen", 44)]
    [InlineData("scrn", "screen", 24)]
    [InlineData("brilho", "birlho", 16)]
    [InlineData("screan", "screen", 16)]
    public void Token_scores(string token, string word, int expected) =>
        Assert.Equal(expected, CommandBarSearch.TokenScore(token, [word], word));

    [Theory]
    [InlineData("xyz", "screen")]
    [InlineData("12", "123x")]
    [InlineData("sc", "xxsxxc")]
    public void Tokens_that_do_not_match_reject_the_row(string token, string word)
    {
        if (word.Contains(token, StringComparison.Ordinal))
        {
            return;
        }

        Assert.Null(CommandBarSearch.TokenScore(token, [word], word));
    }

    [Fact]
    public void Fuzzy_helpers()
    {
        Assert.True(CommandBarSearch.IsSubsequence("scrn", "screen"));
        Assert.False(CommandBarSearch.IsSubsequence("nrcs", "screen"));
        Assert.True(CommandBarSearch.IsAdjacentTransposition("brilho", "birlho"));
        Assert.False(CommandBarSearch.IsAdjacentTransposition("abc", "abc"));
        Assert.True(CommandBarSearch.IsWithinOneEdit("calendar", "calender"));
        Assert.True(CommandBarSearch.IsWithinOneEdit("calendar", "calendars"));
        Assert.True(CommandBarSearch.IsWithinOneEdit("calendar", "calenar"));
        Assert.False(CommandBarSearch.IsWithinOneEdit("calendar", "kalender"));
    }

    [Fact]
    public void Bonus_and_tier()
    {
        Assert.Equal((1200, 5), CommandBarSearch.Bonus("screenshot", "", "screenshot"));
        Assert.Equal((900, 4), CommandBarSearch.Bonus("screenshot", "", "screen"));
        Assert.Equal((700, 3), CommandBarSearch.Bonus("take a screenshot", "", "screen"));
        Assert.Equal((350, 2), CommandBarSearch.Bonus("capture", "screenshot", "screen"));
        Assert.Equal((0, 1), CommandBarSearch.Bonus("capture", "picture", "pic cap"));
    }

    [Fact]
    public void Exact_titles_beat_prefixes_and_apps_get_a_bias()
    {
        var pool = new List<CommandRow>
        {
            Row("action.notes.open", "Notes manager"),
            Row("app.notes", "Notes"),
            Row("settings.notes", "Notes settings"),
        };
        var ranked = CommandBarSearch.Rank(pool, "notes", RankingContext.Empty);
        Assert.Equal("app.notes", ranked[0].Row.Id);
        Assert.Equal(5, ranked[0].Tier);
    }

    [Fact]
    public void Aliases_lead_with_priority()
    {
        var pool = new List<CommandRow> { Row("app.code", "Visual Studio Code"), Row("action.coffee", "Cofee timer") };
        var context = new RankingContext { Aliases = new Dictionary<string, string> { ["app.code"] = "vs editor" } };
        var ranked = CommandBarSearch.Rank(pool, "editor", context);
        Assert.Equal(2400, ranked[0].Priority);
        Assert.Equal(1100, CommandBarSearch.Rank(pool, "edi", context)[0].Priority);
    }

    [Fact]
    public void Usage_boost_weights_recency()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(12 * 3, CommandBarUsage.Boost(new UsageRecord(3, now.AddMinutes(-5).ToUnixTimeSeconds()), now));
        Assert.Equal(8 * 40, CommandBarUsage.Boost(new UsageRecord(100, now.AddHours(-5).ToUnixTimeSeconds()), now));
        Assert.Equal(5 * 2, CommandBarUsage.Boost(new UsageRecord(2, now.AddDays(-3).ToUnixTimeSeconds()), now));
        Assert.Equal(2, CommandBarUsage.Boost(new UsageRecord(1, now.AddDays(-30).ToUnixTimeSeconds()), now));
        Assert.Equal(0, CommandBarUsage.Boost(null, now));
    }

    [Fact]
    public void Usage_is_capped_and_round_trips()
    {
        var usage = new CommandBarUsage();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1005; i++)
        {
            usage.Record("action.a", now);
        }

        for (var i = 0; i < 210; i++)
        {
            usage.Record("row." + i, now.AddSeconds(i));
        }

        Assert.Equal(CommandBarUsage.MaxRows, usage.Records.Count);
        var loaded = CommandBarUsage.Deserialize(usage.Serialize());
        Assert.Equal(CommandBarUsage.MaxRows, loaded.Records.Count);
        Assert.Equal(999, CommandBarUsage.Deserialize("{\"x\":{\"count\":5000,\"lastUsed\":1}}").Get("x")!.Count);
        Assert.Empty(CommandBarUsage.Deserialize("garbage").Records);
    }

    [Fact]
    public void Query_habits_put_chosen_rows_first_for_the_same_letters()
    {
        var habits = new CommandBarQueryHabits();
        var pool = new List<CommandRow> { Row("action.screen", "Screenshot"), Row("action.scratch", "Scratchpad") };
        var now = DateTimeOffset.UtcNow;
        habits.Record("scr", "action.scratch", now);
        var context = new RankingContext { Habits = habits, Now = now };
        Assert.Equal("action.scratch", CommandBarSearch.Rank(pool, "scr", context)[0].Row.Id);
        Assert.Equal("action.screen", CommandBarSearch.Rank(pool, "scre", context)[0].Row.Id);
        habits.Forget("action.scratch");
        Assert.Equal(0, CommandBarSearch.Rank(pool, "scr", context)[0].Priority);
    }

    [Fact]
    public void Query_memory_is_a_tie_breaker_only()
    {
        var memory = new CommandBarQueryMemory();
        memory.Record("ab", "row.x");
        memory.Record("ab", "row.x");
        memory.Record("ab", "row.x");
        memory.Record("ab", "row.x");
        Assert.Equal(3, memory.Boost("ab", "row.x"));
        Assert.Equal(3, memory.Boost("a", "row.x"));
        Assert.Equal(0, memory.Boost("abc", "row.x"));
    }

    [Fact]
    public void Feature_rows_keep_their_slots_but_main_command_comes_first()
    {
        var pool = new List<CommandRow>
        {
            Row("settings.feature.keepAwake", "Keep awake", feature: "keepAwake", role: 2),
            Row("action.keepAwake.toggle", "Keep awake now", feature: "keepAwake", role: 0),
        };
        var ranked = CommandBarSearch.Rank(pool, "keep awake", RankingContext.Empty);
        Assert.Equal("action.keepAwake.toggle", ranked[0].Row.Id);
    }

    [Fact]
    public void Assembly_applies_caps_answers_and_the_row_limit()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Row($"app.{i}", $"App {i}"))
            .Concat(Enumerable.Range(0, 10).Select(i => Row($"action.{i}", $"Action {i}")))
            .Append(Row("answer.battery", "Battery"))
            .ToList();
        var answer = Row("math.result", "4");
        var assembled = CommandBarSearch.Assemble(answer, null, null, rows, "ap");
        Assert.Equal(CommandBarSearch.MaxRows, assembled.Count);
        Assert.Equal("math.result", assembled[0].Id);
        Assert.Equal(5, assembled.Count(r => r.Id.StartsWith("app.", StringComparison.Ordinal)));
        Assert.DoesNotContain(assembled, r => r.Id == "answer.battery");
        Assert.Contains(CommandBarSearch.Assemble(null, null, null, [Row("answer.battery", "Battery"), Row("app.0", "App")], "batt"), r => r.Id == "answer.battery");
        Assert.DoesNotContain(CommandBarSearch.Assemble(null, null, null, [Row("answer.battery", "Battery")], "ba"), r => r.Id == "answer.battery");
    }

    [Fact]
    public void A_lone_colour_preview_goes_second_when_a_title_contains_it()
    {
        var preview = Row("color.preview", "#1234");
        var rows = new[] { Row("action.issue", "Open issue #1234") };
        Assert.Equal("color.preview", CommandBarSearch.Assemble(null, null, null, rows, "#1234", preview)[1].Id);
        Assert.Equal("color.preview", CommandBarSearch.Assemble(null, null, null, [], "#1234", preview)[0].Id);
    }

    [Theory]
    [InlineData("brightness 40", "brightness", 40)]
    [InlineData("brilho 40%", "brilho", 40)]
    [InlineData("code 1234", "code", 1234)]
    [InlineData("40", "40", null)]
    [InlineData("volume up", "volume up", null)]
    public void Trailing_numbers(string input, string text, int? number)
    {
        var (t, n) = CommandBarSearch.SplitTrailingNumber(input);
        Assert.Equal(text, t);
        Assert.Equal(number, n);
    }

    [Fact]
    public void Argument_values_are_clamped()
    {
        var range = new NumericRange(0, 100, true);
        Assert.Equal(40, CommandBarSearch.ArgumentValue("40%", range));
        Assert.Equal(100, CommandBarSearch.ArgumentValue("250", range));
        Assert.Null(CommandBarSearch.ArgumentValue("12345", range));
        Assert.Null(CommandBarSearch.ArgumentValue("4a", range));
    }

    [Fact]
    public void Highlights_prefer_word_starts()
    {
        Assert.Equal([(5, 4)], CommandBarSearch.Highlights("Take Screen", "scre"));
        Assert.Equal([(0, 3), (5, 3)], CommandBarSearch.Highlights("Café Bar", "caf bar"));
    }

    [Theory]
    [InlineData("gh vorssaint", "gh", "vorssaint")]
    [InlineData("GH  Café Olé", "gh", "Café Olé")]
    [InlineData("my site hello world", "My Site", "hello world")]
    [InlineData("ghost", "gh", null)]
    [InlineData("gh", "gh", null)]
    [InlineData("gh\u3000query", "gh", "query")]
    public void Trailing_arguments(string query, string name, string? expected) =>
        Assert.Equal(expected, CommandBarLinks.TrailingArgument(query, name));

    [Fact]
    public void Link_placeholders_are_escaped_only_for_links()
    {
        var values = new LinkPlaceholderValues("café com leite", "a+b", null, new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero), CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("https://s.com/?q=caf%C3%A9%20com%20leite&c=a%2Bb", CommandBarLinks.Expand("https://s.com/?q={query}&c={clipboard}", CommandBarLinkKind.Link, values));
        Assert.Equal("C:\\notes\\café com leite.txt", CommandBarLinks.Expand("C:\\notes\\{query}.txt", CommandBarLinkKind.Place, values));
        Assert.Equal("7/28/2026", CommandBarLinks.Expand("{date}", CommandBarLinkKind.Place, values));
        Assert.Equal("https://example.com", CommandBarLinks.LinkTarget("example.com"));
        Assert.Equal(Path.Combine("C:\\Users\\me", "Docs"), CommandBarLinks.PlaceTarget("~/Docs", "C:\\Users\\me"));
    }

    [Fact]
    public void Saved_links_drop_empty_entries_and_cap_at_60()
    {
        var links = Enumerable.Range(0, 70).Select(i => new CommandBarLink { Name = "n" + i, Destination = "d" }).ToList();
        links.Insert(0, new CommandBarLink { Name = " ", Destination = "x" });
        Assert.Equal(60, CommandBarLinks.Sanitize(links).Count);
        Assert.True(new CommandBarLink { Destination = "https://s.com/?q={query}" }.IsSearch);
        Assert.False(new CommandBarLink { Kind = CommandBarLinkKind.Script, Destination = "{query}" }.IsSearch);
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("example.com/docs?x=1", "https://example.com/docs?x=1")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("sub.domain.io", "https://sub.domain.io")]
    [InlineData("notes.txt", null)]
    [InlineData("readme.md", null)]
    [InlineData("me@example.com", null)]
    [InlineData("42", null)]
    [InlineData("1.5", null)]
    [InlineData("hello world.com", null)]
    public void Typed_urls(string text, string? expected) => Assert.Equal(expected, CommandBarLinks.TypedUrl(text));

    [Fact]
    public void Script_dispatch_and_refusals()
    {
        Assert.Null(ScriptRunner.StartInfo("C:\\s\\run.vbs", "", windows: true));
        Assert.Equal("powershell.exe", ScriptRunner.StartInfo("C:\\s\\run.ps1", "x", windows: true)!.FileName);
        Assert.Contains("-File", ScriptRunner.StartInfo("C:\\s\\run.ps1", "x", windows: true)!.ArgumentList);
        Assert.Equal("cmd.exe", ScriptRunner.StartInfo("C:\\s\\run.cmd", "hello", windows: true)!.FileName);
        Assert.Null(ScriptRunner.StartInfo("C:\\s\\run.cmd", "a & del *", windows: true));
        Assert.True(ScriptRunner.StartInfo("C:\\s\\tool.exe", "x y", windows: true)!.CreateNoWindow);
    }

    [Fact]
    public void Emoji_data_has_popular_first_and_a_long_tail()
    {
        var all = CommandBarEmoji.All;
        Assert.True(all.Count > 1000);
        Assert.Equal("😀", all[0].Glyph);
        Assert.Equal("grinning face", all[0].Name);
        var joy = all.Single(e => e.Glyph == "😂");
        Assert.Equal("face with tears of joy", joy.Name);
        Assert.Contains("lol", joy.Keywords);
        Assert.Equal(all.Count, all.Select(e => CommandBarEmoji.StripSelectors(e.Glyph)).Distinct().Count());
        Assert.Contains(all, e => e.Name == "shrug");
        Assert.Contains(all, e => e.Glyph == "☠\uFE0F");
    }

    [Fact]
    public void Skin_tones_apply_to_single_modifier_bases_except_the_family()
    {
        Assert.True(CommandBarEmoji.AcceptsSkinTone("👍"));
        Assert.False(CommandBarEmoji.AcceptsSkinTone("👪"));
        Assert.False(CommandBarEmoji.AcceptsSkinTone("😀"));
        Assert.True(CommandBarEmoji.AcceptsSkinTone("✌\uFE0F"));
        Assert.Equal("✌\U0001F3FF", CommandBarEmoji.ApplySkinTone("✌\uFE0F", EmojiSkinTone.Dark));
        Assert.Equal("👍\U0001F3FB", CommandBarEmoji.ApplySkinTone("👍", EmojiSkinTone.Light));
        Assert.Equal("😀", CommandBarEmoji.ApplySkinTone("😀", EmojiSkinTone.Light));
        Assert.Equal(5, CommandBarEmoji.OtherTones("👍", EmojiSkinTone.Default).Count);
        Assert.Equal(EmojiSkinTone.MediumDark, CommandBarEmoji.ParseTone(CommandBarEmoji.StoreTone(EmojiSkinTone.MediumDark)));
    }

    [Fact]
    public void Source_ids_never_change()
    {
        Assert.Equal("macSettings", CommandSources.StorageId(CommandSource.MacSettings));
        Assert.Equal("quitApps", CommandSources.StorageId(CommandSource.QuitApps));
        Assert.Equal(CommandSource.Apps, CommandSources.Of("app.c:\\x.exe"));
        Assert.Equal(CommandSource.Actions, CommandSources.Of("toggle.keepAwake"));
        Assert.Equal(CommandSource.Actions, CommandSources.Of("emoji.browse"));
        Assert.Equal(CommandSource.Emoji, CommandSources.Of("emoji.😀"));
        Assert.Equal(CommandSource.MacSettings, CommandSources.Of("macsettings.ms-settings:display"));
        Assert.Equal(CommandSource.KillProcess, CommandSources.Parse("killProcess"));
    }

    [Fact]
    public void Preferences_pins_aliases_hidden_and_sources()
    {
        var store = SettingsStore.InMemory();
        var preferences = new CommandBarPreferences(store);
        for (var i = 0; i < 35; i++)
        {
            preferences.TogglePin("row." + i);
        }

        Assert.Equal(CommandBarSettings.MaxPins, preferences.Pins.Count);
        Assert.Equal("row.5", preferences.Pins[0]);
        Assert.False(preferences.TogglePin("row.5"));
        Assert.Null(preferences.SetAlias("app.code", "vs editor"));
        Assert.Equal("app.code", preferences.SetAlias("app.other", "Editor"));
        Assert.Null(preferences.SetAlias("app.code", ""));
        Assert.Empty(preferences.Aliases);
        preferences.SetHidden("app.x", true);
        Assert.Contains("app.x", preferences.Hidden);
        preferences.SetSourceEnabled(CommandSource.Emoji, false);
        preferences.SetSourceEnabled(CommandSource.Actions, false);
        Assert.Equal("emoji", store.Get(CommandBarSettings.DisabledSources));
        Assert.True(preferences.IsSourceEnabled(CommandSource.Actions));
        preferences.PositionOffset = (12, -3);
        Assert.Equal((12, -3), preferences.PositionOffset);
        preferences.PositionOffset = (0, 0);
        Assert.Equal(string.Empty, store.Get(CommandBarSettings.PositionOffset));
    }

    [Theory]
    [InlineData("node_modules", true)]
    [InlineData(".hidden", true)]
    [InlineData("debug.log", true)]
    [InlineData("build", true)]
    [InlineData("builder", false)]
    [InlineData("readme.md", false)]
    public void File_ignore_patterns(string name, bool ignored) =>
        Assert.Equal(ignored, CommandBarPreferences.IsIgnored(name, ["*.log", "build"]));

    [Fact]
    public void Engine_home_search_and_categories()
    {
        var store = SettingsStore.InMemory();
        var preferences = new CommandBarPreferences(store);
        var engine = new CommandBarEngine(preferences, new CommandBarUsage())
        {
            Catalog =
            [
                Row("action.screenshot.capture", "Screenshot", feature: "screenshot"),
                Row("action.colorPicker.pick", "Color picker", feature: "colorPicker"),
                Row("settings.clipboard", "Clipboard") ,
            ],
            Apps = [Row("app.notepad", "Notepad")],
            CuratedFeatures = ["screenshot", "colorPicker"],
            Answer = q => CommandBarAnswers.Compute(q, CultureInfo.GetCultureInfo("en-US"), DateTimeOffset.UtcNow) is { } a ? Row(a.RowId, a.Title) : null,
            EmojiRow = e => new CommandRow { Id = "emoji." + e.Glyph, Title = e.Glyph + "  " + e.Name, MatchTitle = e.Name, Keywords = e.Keywords },
        };

        var home = engine.Home();
        Assert.Equal("Suggestions", home[0].Header);
        Assert.Equal("action.screenshot.capture", home[1].Row!.Id);

        preferences.TogglePin("app.notepad");
        Assert.Equal("app.notepad", engine.Home()[1].Row!.Id);

        Assert.Equal("math.result", engine.Search("2+2")[0].Id);
        Assert.Equal("app.notepad", engine.Search("note")[0].Id);
        Assert.Equal("emoji.🔥", engine.Search(":fire")[0].Id);
        Assert.Equal(40, engine.Search(":").Count);

        preferences.SetHidden("app.notepad", true);
        Assert.DoesNotContain(engine.Search("note"), r => r.Id == "app.notepad");
        Assert.Contains(CommandCategory.Apps, engine.AvailableCategories());
        Assert.Contains(CommandCategory.SettingsPages, engine.AvailableCategories());
        Assert.DoesNotContain(CommandCategory.Snippets, engine.AvailableCategories());

        engine.RecordRun(engine.Catalog[1], "col", fromBar: true);
        Assert.Equal("action.colorPicker.pick", engine.Category(CommandCategory.Actions, "")[0].Id);
        engine.ForgetAll();
        Assert.Empty(engine.Usage.Records);
    }

    [Fact]
    public void Quit_rows_need_the_quit_verb() =>
        Assert.True(CommandBarEngine.MentionsQuit("qu notepad") && !CommandBarEngine.MentionsQuit("notepad"));
}
