// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Clipboard;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Snippets;
using Rivet.Core.Tests.Clipboard;
using Xunit;

namespace Rivet.Core.Tests.Snippets;

/// <summary>Spec 06 §3.6, §6.12: triggers, matching, the buffer and every variable token.</summary>
public class SnippetTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateTimeOffset Moment = new(2025, 7, 1, 12, 30, 15, TimeSpan.Zero);

    private static TextSnippet Snip(string trigger, string text = "x", SnippetExpansion mode = SnippetExpansion.AfterDelimiter, bool ignoreCase = false) =>
        new() { Trigger = trigger, Replacement = text, Expansion = mode, IgnoresCase = ignoreCase };

    [Fact]
    public void Triggers_lose_whitespace_and_are_cut_to_40()
    {
        Assert.Equal(";email", SnippetSettings.SanitizeTrigger(" ;em ail\t"));
        Assert.Equal(40, SnippetSettings.SanitizeTrigger(new string('a', 50)).Length);
    }

    [Fact]
    public void Validation_rules()
    {
        var existing = new[] { Snip(";email"), Snip(";Sig", ignoreCase: true) };
        Assert.Equal(SnippetValidation.TriggerTooShort, SnippetValidator.Validate(Snip(";"), existing));
        Assert.Equal(SnippetValidation.DuplicateTrigger, SnippetValidator.Validate(Snip(";email"), existing));
        Assert.Equal(SnippetValidation.Valid, SnippetValidator.Validate(Snip(";EMAIL"), existing));
        Assert.Equal(SnippetValidation.DuplicateTrigger, SnippetValidator.Validate(Snip(";sig"), existing));
        Assert.Equal(SnippetValidation.EmptyText, SnippetValidator.Validate(Snip(";new", ""), existing));
        var self = existing[0];
        Assert.Equal(SnippetValidation.Valid, SnippetValidator.Validate(self, existing));
    }

    [Fact]
    public void Longest_trigger_wins_and_modes_do_not_cross()
    {
        var snippets = new[] { Snip(";email", "a"), Snip(";email2", "b"), Snip(";now", "c", SnippetExpansion.Immediate) };
        Assert.Equal(";email2", SnippetEngine.BestMatch(snippets, "x;email2")!.Trigger);
        Assert.Equal(";email", SnippetEngine.BestMatch(snippets, "x;email")!.Trigger);
        var engine = new SnippetEngine();
        engine.Configure(snippets);
        foreach (var c in ";now")
        {
            var match = engine.Process(new SnippetKey(SnippetKeyKind.Character, c.ToString()));
            if (c == 'w')
            {
                Assert.NotNull(match);
                Assert.Equal(";now", match!.Snippet.Trigger);
                Assert.Equal(3, match.DeleteCount); // the last keystroke never reached the app
                Assert.Null(match.TrailingKey);
            }
            else
            {
                Assert.Null(match);
            }
        }

        foreach (var c in ";email")
        {
            Assert.Null(engine.Process(new SnippetKey(SnippetKeyKind.Character, c.ToString())));
        }

        var delimited = engine.Process(new SnippetKey(SnippetKeyKind.Character, " ", VirtualKeys.Space));
        Assert.NotNull(delimited);
        Assert.Equal(6, delimited!.DeleteCount);
        Assert.Equal(VirtualKeys.Space, delimited.TrailingKey);
        Assert.Equal(" ", delimited.TrailingText);
        Assert.Equal(string.Empty, engine.Buffer);
    }

    [Fact]
    public void Ignore_case_compares_the_tail_case_insensitively()
    {
        var engine = new SnippetEngine();
        engine.Configure([Snip(";Addr", "1 Main St", ignoreCase: true)]);
        foreach (var c in "x;aDDR")
        {
            engine.Process(new SnippetKey(SnippetKeyKind.Character, c.ToString()));
        }

        Assert.NotNull(engine.Process(new SnippetKey(SnippetKeyKind.Character, "\r", VirtualKeys.Return)));
    }

    [Fact]
    public void Buffer_is_capped_and_backspace_and_navigation_behave()
    {
        var engine = new SnippetEngine();
        engine.Configure([Snip(";x")]);
        for (var i = 0; i < 100; i++)
        {
            engine.Process(new SnippetKey(SnippetKeyKind.Character, "a"));
        }

        Assert.Equal(SnippetEngine.BufferLimit, engine.Buffer.Length);
        engine.Process(new SnippetKey(SnippetKeyKind.Backspace));
        Assert.Equal(SnippetEngine.BufferLimit - 1, engine.Buffer.Length);
        engine.Process(new SnippetKey(SnippetKeyKind.Navigation));
        Assert.Equal(string.Empty, engine.Buffer);
        engine.Process(new SnippetKey(SnippetKeyKind.Character, ";"));
        engine.Process(new SnippetKey(SnippetKeyKind.Shortcut));
        engine.Process(new SnippetKey(SnippetKeyKind.Character, "x"));
        Assert.Null(engine.Process(new SnippetKey(SnippetKeyKind.Character, " ", VirtualKeys.Space)));
    }

    [Fact]
    public void Disabled_and_empty_triggers_never_match()
    {
        var engine = new SnippetEngine();
        engine.Configure([Snip(";off") with { Enabled = false }, Snip("")]);
        Assert.False(engine.HasSnippets);
    }

    [Fact]
    public void Plain_variables_use_the_medium_date_and_short_time()
    {
        // ICU puts U+202F (narrow no-break space) before AM/PM; macOS does the same.
        var text = SnippetVariables.Expand("{{date}}|{{time}}|{{datetime}}", Moment, EnUs, TimeZoneInfo.Utc, null).Replace('\u202F', ' ');
        Assert.Equal("Jul 1, 2025|12:30 PM|Jul 1, 2025 12:30 PM", text);
        Assert.Equal("no tags", SnippetVariables.Expand("no tags", Moment, EnUs, TimeZoneInfo.Utc, null));
    }

    [Fact]
    public void Pattern_tokens_follow_icu_and_respect_time_zones()
    {
        string Expand(string s, CultureInfo? culture = null) => SnippetVariables.Expand(s, Moment, culture ?? EnUs, TimeZoneInfo.Utc, "clip");
        Assert.Equal("2025-07-01", Expand("{{date:yyyy-MM-dd}}"));
        Assert.Equal("2025", Expand("{{datetime:yyyy}}"));
        Assert.Equal("julho", Expand("{{date:MMMM}}", CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Equal("2025-07-01 21:30", Expand("{{datetime-tz(Asia/Tokyo):yyyy-MM-dd HH:mm}}"));
        Assert.Equal("21:30|12:30", Expand("{{time-tz(Asia/Tokyo):HH:mm}}|{{time:HH:mm}}"));
        Assert.Equal("12:30:15", Expand("{{time:HH:mm:ss}}"));
        Assert.Equal("2025-07-01T21:30:15+09:00", Expand("{{datetime-tz(Asia/Tokyo):yyyy-MM-dd'T'HH:mm:ssXXX}}"));
        Assert.Equal("12:30:15Z", Expand("{{time:HH:mm:ssXXX}}"));
        Assert.Equal("Tuesday, July 1, 2025 at 12:30 PM", Expand("{{datetime:EEEE, MMMM d, y 'at' h:mm a}}"));
        Assert.Equal("Tue Jul 1 PM", Expand("{{date:EEE MMM d a}}"));
        Assert.Equal("it's 2025", Expand("{{date:'it''s' y}}"));
    }

    [Theory]
    [InlineData("{{date:}}")]
    [InlineData("{{foo:yyyy}}")]
    [InlineData("{{date:yyyy")]
    [InlineData("{{unknown}}")]
    [InlineData("{{datetime-tz(Not/ARealZone):yyyy}}")]
    [InlineData("{{date-tz(+02:00):yyyy}}")]
    public void Malformed_tags_stay_literal(string text) =>
        Assert.Equal(text, SnippetVariables.Expand(text, Moment, EnUs, TimeZoneInfo.Utc, null));

    [Fact]
    public void A_malformed_tag_never_swallows_a_later_valid_one()
    {
        var text = SnippetVariables.Expand("{{date:yyyy and {{time:HH}}", Moment, EnUs, TimeZoneInfo.Utc, null);
        Assert.Equal("{{date:yyyy and 12", text);
    }

    [Fact]
    public void Clipboard_goes_in_last_and_is_never_expanded()
    {
        var text = SnippetVariables.Expand("[{{clipboard}}]", Moment, EnUs, TimeZoneInfo.Utc, "{{date:yyyy}}");
        Assert.Equal("[{{date:yyyy}}]", text);
        Assert.Equal("[]", SnippetVariables.Expand("[{{clipboard}}]", Moment, EnUs, TimeZoneInfo.Utc, null));
        Assert.True(SnippetVariables.UsesClipboard("a {{clipboard}}"));
    }

    [Fact]
    public void Date_tokens_are_found_under_the_caret()
    {
        const string text = "Hi {{date-tz(Europe/Paris):yyyy}} and {{time:HH:mm}}";
        var token = SnippetVariables.TokenAt(text, 8)!;
        Assert.Equal(DateTokenKind.Date, token.Kind);
        Assert.Equal("Europe/Paris", token.TimeZoneId);
        Assert.Equal("yyyy", token.Pattern);
        Assert.Null(SnippetVariables.TokenAt(text, 3));
        Assert.Equal("HH:mm", SnippetVariables.TokenAt(text, text.Length - 3)!.Pattern);
        Assert.Equal("{{datetime-tz(Asia/Tokyo):HH}}", DateToken.Build(DateTokenKind.DateTime, "HH", "Asia/Tokyo"));
    }

    [Fact]
    public void Named_styles_freeze_icu_patterns()
    {
        Assert.Equal("yyyy-MM-dd'T'HH:mm:ssXXX", DateStyles.Pattern(DateTokenKind.DateTime, DateStyle.Iso8601, EnUs));
        Assert.Equal("MMM d, y", DateStyles.Pattern(DateTokenKind.Date, DateStyle.Medium, EnUs));
        Assert.Equal("EEEE, MMMM d, y", DateStyles.Pattern(DateTokenKind.Date, DateStyle.Full, EnUs));
        Assert.Equal("h:mm a", DateStyles.Pattern(DateTokenKind.Time, DateStyle.Short, EnUs).Replace('\u202F', ' '));
        Assert.Equal(DateStyle.Medium, DateStyles.StyleOf(DateTokenKind.Date, "MMM d, y", EnUs));
        Assert.Equal(DateStyle.Custom, DateStyles.StyleOf(DateTokenKind.Date, "dd/MM", EnUs));
        Assert.Equal("d. MMMM yyyy", DateStyles.WithoutWeekday("dddd, d. MMMM yyyy"));
    }

    [Fact]
    public void Time_zone_search_ranks_and_resolves()
    {
        Assert.Equal("America/Los_Angeles", TimeZones.Resolve("PST"));
        Assert.Equal("Asia/Tokyo", TimeZones.Resolve("Asia/Tokyo"));
        Assert.Equal("Asia/Tokyo", TimeZones.Search("tokyo")[0]);
        Assert.Equal("America/New_York", TimeZones.Search("new york")[0]);
        Assert.Null(TimeZones.Find("+02:00"));
        Assert.Null(TimeZones.Find("Not/ARealZone"));
        Assert.NotNull(TimeZones.Find("Europe/Lisbon"));
        Assert.Contains("Asia/Kolkata", TimeZones.Identifiers);
    }

    [Fact]
    public void Library_groups_folders_then_loose_snippets()
    {
        var snippets = new[]
        {
            Snip(";a") with { Name = "Alpha", Folder = "Work" },
            Snip(";b") with { Name = "Beta" },
            Snip(";c") with { Name = "Gamma", Folder = "Personal", ShowsInLibrary = false },
            Snip(";d") with { Name = "Delta", Folder = "Personal" },
            Snip(";e") with { Name = "Café notes" },
        };
        var sections = SnippetLibrary.Sections(snippets, "", EnUs);
        Assert.Equal(["Personal", "Work", ""], sections.Select(s => s.Folder));
        Assert.Equal(["Beta", "Café notes"], sections[2].Snippets.Select(s => s.Name));
        Assert.Single(SnippetLibrary.Sections(snippets, "CAFE", EnUs).SelectMany(s => s.Snippets));
        Assert.Single(SnippetLibrary.Sections(snippets, "work", EnUs));
    }

    [Fact]
    public void Snippets_round_trip_through_settings_json()
    {
        var store = SettingsStore.InMemory();
        var snippet = new TextSnippet { Name = " Mail ", Trigger = " ;ma il", Replacement = "a{{date:yyyy}}", Expansion = SnippetExpansion.Immediate, Folder = " Work " };
        store.Set(SnippetSettings.Snippets, [snippet]);
        var raw = store.GetRaw(SnippetSettings.Snippets.Key)!.ToJsonString();
        Assert.Contains("\"expansion\":\"immediate\"", raw);
        Assert.Contains("\"showsInLibrary\":true", raw);
        var loaded = store.Get(SnippetSettings.Snippets).Single();
        Assert.Equal(";mail", loaded.Trigger);
        Assert.Equal("Mail", loaded.Name);
        Assert.Equal("Work", loaded.Folder);
        store.SetRaw(SnippetSettings.Snippets.Key, System.Text.Json.Nodes.JsonNode.Parse("[{\"trigger\":\";old\",\"replacement\":\"x\",\"expansion\":\"afterDelimiter\",\"enabled\":true}]"));
        var old = store.Get(SnippetSettings.Snippets).Single();
        Assert.False(old.IgnoresCase);
        Assert.True(old.ShowsInLibrary);
        Assert.Equal(string.Empty, old.Folder);
    }

    [Fact]
    public void Expansion_service_swallows_the_last_key_and_types_the_text()
    {
        var settings = SettingsStore.InMemory();
        settings.Set(SnippetSettings.ExpansionEnabled, true);
        settings.Set(SnippetSettings.Snippets, [Snip(";hi", "Hello there")]);
        var hooks = new TestInputHooks();
        var clipboard = new TestClipboard();
        var watcher = new ClipboardWatcher(new ClipboardLane(clipboard));
        var input = new SyntheticInput(hooks);
        var transient = new TransientPaste(watcher, input);
        var foreground = new TestForeground();
        var service = new SnippetExpansionService(settings, hooks, new UsTranslator(), foreground, new TextInserter(input, transient), transient, watcher.Lane, new SilentSounds());
        service.Sync(true);
        Assert.True(service.IsRunning);

        foreach (var c in ";hi")
        {
            Assert.False(hooks.Press(Key(c)));
        }

        Assert.True(hooks.Press(new KeyboardHookEvent { VirtualKey = VirtualKeys.Space, Action = KeyAction.Down }));
        Assert.Equal("keys:08d,08u,08d,08u,08d,08u", hooks.Sent[0]);
        Assert.Equal("text:Hello there", hooks.Sent[1]);
        Assert.Equal("keys:20d,20u", hooks.Sent[2]);

        // A click resets the buffer; a password field suspends expansion.
        hooks.Press(Key(';'));
        hooks.Click();
        hooks.Press(Key('h'));
        hooks.Press(Key('i'));
        Assert.False(hooks.Press(new KeyboardHookEvent { VirtualKey = VirtualKeys.Space, Action = KeyAction.Down }));
        foreground.IsPasswordFieldFocused = true;
        foreach (var c in ";hi")
        {
            hooks.Press(Key(c));
        }

        Assert.False(hooks.Press(new KeyboardHookEvent { VirtualKey = VirtualKeys.Space, Action = KeyAction.Down }));
        using (service.Suspend())
        {
            foreground.IsPasswordFieldFocused = false;
            foreach (var c in ";hi")
            {
                hooks.Press(Key(c));
            }

            Assert.False(hooks.Press(new KeyboardHookEvent { VirtualKey = VirtualKeys.Space, Action = KeyAction.Down }));
        }

        settings.Set(SnippetSettings.ExpansionEnabled, false);
        Assert.False(service.IsRunning);
        Assert.Equal(0, hooks.KeyboardSubscribers);
    }

    [Fact]
    public void Text_chunks_never_split_surrogate_pairs()
    {
        var text = new string('a', 19) + "😀" + "b";
        var chunks = SyntheticInput.Chunks(text).ToList();
        Assert.Equal(new string('a', 19), chunks[0]);
        Assert.Equal("😀b", chunks[1]);
        Assert.True(SyntheticInput.HasLineBreak("a\nb"));
        Assert.True(SyntheticInput.HasLineBreak("a" + (char)0x2028 + "b"));
        Assert.False(SyntheticInput.HasLineBreak("ab"));
    }

    private static KeyboardHookEvent Key(char c) =>
        new() { VirtualKey = char.IsLetter(c) ? char.ToUpperInvariant(c) : VirtualKeys.OemSemicolon, Action = KeyAction.Down };

    private sealed class UsTranslator : IKeyTranslator
    {
        public string Translate(int virtualKey, int scanCode, KeyModifiers modifiers) => virtualKey switch
        {
            >= 'A' and <= 'Z' => ((char)(modifiers.HasFlag(KeyModifiers.Shift) ? virtualKey : virtualKey + 32)).ToString(),
            VirtualKeys.OemSemicolon => ";",
            VirtualKeys.Space => " ",
            VirtualKeys.Return => "\r",
            VirtualKeys.Tab => "\t",
            _ => string.Empty,
        };
    }

    private sealed class SilentSounds : ISnippetSounds
    {
        public IReadOnlyList<string> Available() => [];

        public void Play(string name)
        {
        }
    }
}
