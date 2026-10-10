// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json.Nodes;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.Core.Tests.RadialMenu;

public class RadialGeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(Math.PI / 2, 1)]
    [InlineData(Math.PI, 2)]
    [InlineData(3 * Math.PI / 2, 3)]
    public void Four_slices_by_angle(double angle, int expected) => Assert.Equal(expected, RadialGeometry.SliceIndex(angle, 4));

    [Fact]
    public void Twelve_slices_wrap_around_twelve_oclock()
    {
        Assert.Equal(0, RadialGeometry.SliceIndex((2 * Math.PI) - 0.01, 12));
        Assert.Equal(11, RadialGeometry.SliceIndex((2 * Math.PI) - 0.3, 12));
    }

    [Fact]
    public void The_dead_zone_has_no_highlight_and_there_is_no_outer_limit()
    {
        Assert.Null(RadialGeometry.Highlight(10, 0, 4));
        Assert.Equal(1, RadialGeometry.Highlight(50, 0, 4));
        Assert.Equal(1, RadialGeometry.Highlight(5000, 0, 4));
        Assert.Equal(0, RadialGeometry.Highlight(0, 41, 4));
    }

    [Fact]
    public void Angles_grow_clockwise_from_twelve_oclock()
    {
        Assert.Equal(0, RadialGeometry.Angle(0, 1), 9);
        Assert.Equal(Math.PI / 2, RadialGeometry.Angle(1, 0), 9);
        Assert.Equal(Math.PI, RadialGeometry.Angle(0, -1), 9);
    }

    [Fact]
    public void Keyboard_rotation_starts_at_slice_zero_or_the_last()
    {
        Assert.Equal(0, RadialGeometry.Rotate(null, +1, 6));
        Assert.Equal(5, RadialGeometry.Rotate(null, -1, 6));
        Assert.Equal(0, RadialGeometry.Rotate(5, +1, 6));
        Assert.Equal(5, RadialGeometry.Rotate(0, -1, 6));
    }

    [Fact]
    public void The_wedge_takes_the_shortest_arc()
    {
        var target = RadialGeometry.WedgeTarget(0.1, (2 * Math.PI) - 0.1);
        Assert.Equal(-0.1, target, 9);
    }

    [Fact]
    public void Clicks_close_step_back_or_run_by_distance()
    {
        Assert.Equal(RadialClickAction.Close, RadialGeometry.ClickAction(151));
        Assert.Equal(RadialClickAction.StepBack, RadialGeometry.ClickAction(39));
        Assert.Equal(RadialClickAction.RunHighlighted, RadialGeometry.ClickAction(100));
    }

    [Fact]
    public void Chips_sit_on_the_ring_with_slice_zero_straight_up()
    {
        var (x, y) = RadialGeometry.ChipOffset(0, 6);
        Assert.Equal(0, x, 6);
        Assert.Equal(112, y, 6);
        var (x1, _) = RadialGeometry.ChipOffset(1, 4);
        Assert.Equal(112, x1, 6);
    }

    [Fact]
    public void Placement_stays_200_dip_inside_the_work_area()
    {
        var (x, y) = RadialGeometry.Placement(true, (5, 5), (0, 0, 1920, 1040), 1.0);
        Assert.Equal((200d, 200d), (x, y));
        var (cx, cy) = RadialGeometry.Placement(false, (5, 5), (0, 0, 1920, 1040), 1.0);
        Assert.Equal((960d, 520d), (cx, cy));
    }

    [Fact]
    public void Spring_constants_match_the_swiftui_responses()
    {
        var open = RadialSpring.FromResponse(0.27, 0.86);
        Assert.Equal(541.5, open.Stiffness, 0);
        Assert.Equal(40.0, open.Damping, 0);
        var wedge = RadialSpring.FromResponse(0.2, 0.86);
        Assert.Equal(987.0, wedge.Stiffness, 0);
    }
}

public class RadialSessionTests
{
    private static IReadOnlyList<RadialItem> Items(int count) =>
        Enumerable.Range(0, count).Select(i => new RadialItem { Kind = RadialItemKind.Media, Payload = RadialMediaIds.PlayPause, Name = $"item{i}" }).ToList();

    [Theory]
    [InlineData(RadialTriggerKind.Shortcut, true, RadialActivationMode.PressOrHold, true)]
    [InlineData(RadialTriggerKind.Shortcut, true, RadialActivationMode.Press, false)]
    [InlineData(RadialTriggerKind.Shortcut, true, RadialActivationMode.Hold, true)]
    [InlineData(RadialTriggerKind.Shortcut, false, RadialActivationMode.PressOrHold, false)]
    [InlineData(RadialTriggerKind.Shortcut, false, RadialActivationMode.Hold, false)]
    [InlineData(RadialTriggerKind.MouseButton, false, RadialActivationMode.PressOrHold, true)]
    [InlineData(RadialTriggerKind.MouseButton, false, RadialActivationMode.Press, false)]
    [InlineData(RadialTriggerKind.MouseButton, false, RadialActivationMode.Hold, true)]
    [InlineData(RadialTriggerKind.TryIt, true, RadialActivationMode.Hold, false)]
    public void Which_sessions_start_held(RadialTriggerKind trigger, bool modifiers, RadialActivationMode mode, bool held) =>
        Assert.Equal(held, RadialSession.StartsHeld(trigger, modifiers, mode));

    [Fact]
    public void A_quick_press_stays_open_in_press_or_hold_and_closes_in_hold()
    {
        var pressOrHold = new RadialSession(Items(4), RadialTriggerKind.Shortcut, true, RadialActivationMode.PressOrHold, (0, 0));
        Assert.Equal(RadialOutcomeKind.Changed, pressOrHold.Release().Kind);
        Assert.Equal(RadialPhase.Sticky, pressOrHold.Phase);

        var hold = new RadialSession(Items(4), RadialTriggerKind.Shortcut, true, RadialActivationMode.Hold, (0, 0));
        Assert.Equal(RadialOutcomeKind.Close, hold.Release().Kind);
    }

    [Fact]
    public void Moves_are_ignored_until_the_pointer_travelled_8_dip()
    {
        var session = new RadialSession(Items(4), RadialTriggerKind.Shortcut, true, RadialActivationMode.PressOrHold, (0, 0));
        Assert.Equal(RadialOutcomeKind.None, session.PointerMoved(0, -7).Kind);
        Assert.Null(session.Highlight);
        session.PointerMoved(0, -60);
        Assert.Equal(0, session.Highlight);
        session.PointerMoved(60, 0);
        Assert.Equal(1, session.Highlight);
    }

    [Fact]
    public void Releasing_with_a_highlight_runs_it()
    {
        var session = new RadialSession(Items(4), RadialTriggerKind.MouseButton, false, RadialActivationMode.PressOrHold, (0, 0));
        session.PointerMoved(0, 80);
        var outcome = session.Release();
        Assert.Equal(RadialOutcomeKind.Run, outcome.Kind);
        Assert.Equal("item2", outcome.Item!.Name);
        Assert.Equal(RadialPhase.Closed, session.Phase);
    }

    [Fact]
    public void Arrows_rotate_and_make_the_session_sticky_except_in_hold_mode()
    {
        var session = new RadialSession(Items(6), RadialTriggerKind.Shortcut, true, RadialActivationMode.PressOrHold, (0, 0));
        session.Key(RadialKey.Right);
        Assert.Equal(0, session.Highlight);
        Assert.Equal(RadialPhase.Sticky, session.Phase);
        session.Key(RadialKey.Left);
        Assert.Equal(5, session.Highlight);

        var hold = new RadialSession(Items(6), RadialTriggerKind.Shortcut, true, RadialActivationMode.Hold, (0, 0));
        hold.Key(RadialKey.Down);
        Assert.Equal(RadialPhase.Held, hold.Phase);
        Assert.Equal(RadialOutcomeKind.Run, hold.Release().Kind);
    }

    [Fact]
    public void Digits_run_slices_one_to_nine()
    {
        var session = new RadialSession(Items(12), RadialTriggerKind.TryIt, false, RadialActivationMode.PressOrHold, (0, 0));
        Assert.Equal(RadialOutcomeKind.None, session.Digit(0).Kind);
        var outcome = session.Digit(3);
        Assert.Equal("item2", outcome.Item!.Name);
    }

    [Fact]
    public void Submenus_push_one_level_and_escape_steps_back()
    {
        var child = new RadialItem { Kind = RadialItemKind.Media, Payload = RadialMediaIds.NextTrack, Name = "child" };
        var submenu = new RadialItem { Kind = RadialItemKind.Submenu, Name = "More", Children = [child] };
        var session = new RadialSession([submenu], RadialTriggerKind.Shortcut, true, RadialActivationMode.Hold, (0, 0));
        session.PointerMoved(0, -60);
        Assert.Equal(RadialOutcomeKind.Changed, session.Release().Kind);
        Assert.True(session.InSubmenu);
        Assert.Equal("More", session.SubmenuName);
        Assert.Equal(RadialPhase.Sticky, session.Phase);
        Assert.Null(session.Highlight);
        // Re-armed from the current pointer: a small move changes nothing.
        Assert.Equal(RadialOutcomeKind.None, session.PointerMoved(0, -63).Kind);
        Assert.Equal(RadialOutcomeKind.Changed, session.Key(RadialKey.Escape).Kind);
        Assert.False(session.InSubmenu);
        Assert.Equal(RadialOutcomeKind.Close, session.Key(RadialKey.Escape).Kind);
    }

    [Fact]
    public void Clicks_beyond_150_close_and_in_the_hub_step_back()
    {
        var session = new RadialSession(Items(4), RadialTriggerKind.TryIt, false, RadialActivationMode.PressOrHold, (0, 0));
        Assert.Equal(RadialOutcomeKind.None, session.Click(100).Kind);
        Assert.Equal(RadialOutcomeKind.Close, session.Click(20).Kind);
    }
}

public class RadialProfilesTests
{
    private static readonly RadialLegacySeeds NoLegacy = new(null, null, null);

    [Fact]
    public void A_missing_value_migrates_the_starter_wheel_with_the_default_shortcut()
    {
        var result = RadialProfilesCodec.Decode(null, NoLegacy);
        Assert.True(result.Migrated);
        var profile = Assert.Single(result.Profiles);
        Assert.Equal(6, profile.Items.Count);
        Assert.Equal(RadialMenuSettings.DefaultShortcut.ToStorageString(), profile.Shortcut);
        Assert.Equal(RadialItemKind.Media, profile.Items[0].Kind);
        Assert.Equal("~/Downloads", profile.Items[3].Payload);
    }

    [Fact]
    public void Legacy_items_present_but_empty_stay_empty()
    {
        var result = RadialProfilesCodec.Decode(null, new RadialLegacySeeds("control+option+command:49", "back", new JsonArray()));
        var profile = Assert.Single(result.Profiles);
        Assert.Empty(profile.Items);
        Assert.Equal(RadialMouseTrigger.Back, profile.MouseButton.Button);
        Assert.Equal("ctrl+alt+win:0x20", profile.Shortcut);
    }

    [Fact]
    public void Presets_have_the_documented_sizes()
    {
        Assert.Equal([6, 4, 6, 5, 5, 0], RadialPresets.All.Select(p => RadialPresets.Items(p).Count));
    }

    [Fact]
    public void Round_trip_keeps_profiles_and_items()
    {
        var profiles = new List<RadialProfile>
        {
            RadialProfilesCodec.DefaultProfile() with { Name = "Main", Color = RadialColor.Mint, MouseButton = new RadialMouseTrigger(RadialMouseTrigger.Forward) },
            RadialPresets.NewProfile(RadialPreset.Media, 1, k => k),
        };
        var encoded = RadialProfilesCodec.Encode(profiles);
        var decoded = RadialProfilesCodec.Decode(encoded, NoLegacy);
        Assert.False(decoded.Migrated);
        Assert.Equal(2, decoded.Profiles.Count);
        Assert.Equal("Main", decoded.Profiles[0].Name);
        Assert.Equal(RadialColor.Mint, decoded.Profiles[0].Color);
        Assert.Equal(RadialMouseTrigger.Forward, decoded.Profiles[0].MouseButton.Button);
        Assert.Equal(profiles[0].Items.Select(i => i.Id), decoded.Profiles[0].Items.Select(i => i.Id));
        Assert.Equal(RadialPreset.Media, decoded.Profiles[1].Preset);
    }

    [Fact]
    public void A_base64_value_from_a_macos_backup_decodes()
    {
        var json = RadialProfilesCodec.Encode([RadialProfilesCodec.DefaultProfile() with { Name = "Mac" }]).ToJsonString();
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var decoded = RadialProfilesCodec.Decode(JsonValue.Create(base64), NoLegacy);
        Assert.Equal("Mac", decoded.Profiles[0].Name);
    }

    [Fact]
    public void Broken_profiles_and_unknown_kinds_are_dropped_alone()
    {
        var array = new JsonArray
        {
            new JsonObject { ["id"] = "not-a-guid", ["name"] = "bad id" },
            new JsonObject { ["name"] = 5 },
            new JsonObject { ["color"] = "chartreuse" },
            new JsonObject
            {
                ["name"] = "ok",
                ["items"] = new JsonArray
                {
                    new JsonObject { ["kind"] = "teleport", ["payload"] = "x" },
                    new JsonObject { ["kind"] = "url", ["payload"] = "example.com" },
                },
            },
        };
        var decoded = RadialProfilesCodec.Decode(array, NoLegacy);
        var profile = Assert.Single(decoded.Profiles);
        Assert.Equal("ok", profile.Name);
        var item = Assert.Single(profile.Items);
        Assert.Equal("https://example.com", item.Payload);
    }

    [Fact]
    public void Cleanup_enforces_limits_and_trigger_exclusivity()
    {
        var many = Enumerable.Range(0, 15).Select(_ => new RadialItem { Kind = RadialItemKind.Media, Payload = RadialMediaIds.NextTrack }).ToList();
        var nested = new RadialItem
        {
            Kind = RadialItemKind.Submenu,
            Children = [new RadialItem { Kind = RadialItemKind.Submenu }, new RadialItem { Kind = RadialItemKind.App, Payload = "  C:\\a.exe  " }],
        };
        var first = new RadialProfile { Name = new string('n', 80), MouseButton = new RadialMouseTrigger(3), Shortcut = "ctrl+alt:0x70", Items = many };
        var second = new RadialProfile { MouseButton = new RadialMouseTrigger(3), Shortcut = "ctrl+alt:0x70", Items = [nested, new RadialItem { Kind = RadialItemKind.Shortcut, Payload = "garbage" }] };
        var clean = RadialProfilesCodec.Clean([first, second]);
        Assert.Equal(60, clean[0].Name.Length);
        Assert.Equal(12, clean[0].Items.Count);
        Assert.True(clean[1].MouseButton.IsOff);
        Assert.Equal(string.Empty, clean[1].Shortcut);
        var sub = Assert.Single(clean[1].Items);
        var leaf = Assert.Single(sub.Children);
        Assert.Equal("C:\\a.exe", leaf.Payload);
    }

    [Fact]
    public void An_empty_list_becomes_the_default_profile()
    {
        var decoded = RadialProfilesCodec.Decode(new JsonArray(), NoLegacy);
        Assert.Single(decoded.Profiles);
        Assert.False(decoded.Migrated);
    }

    [Theory]
    [InlineData("off", 0)]
    [InlineData("back", 3)]
    [InlineData("forward", 4)]
    [InlineData("button:3", 3)]
    [InlineData("button:31", 31)]
    [InlineData("button:2", 0)]
    [InlineData("button:32", 0)]
    [InlineData("nonsense", 0)]
    public void Mouse_triggers_parse(string value, int button) => Assert.Equal(button, RadialMouseTrigger.Parse(value).Button);

    [Fact]
    public void Mouse_triggers_round_trip_from_3_to_31()
    {
        for (var n = 3; n <= 31; n++)
        {
            var trigger = new RadialMouseTrigger(n);
            Assert.Equal(n, RadialMouseTrigger.Parse(trigger.ToStorage()).Button);
        }
    }

    [Fact]
    public void Duplicates_get_new_ids_a_2_suffix_and_no_triggers()
    {
        var profile = RadialProfilesCodec.DefaultProfile() with { Name = "Work", MouseButton = new RadialMouseTrigger(4) };
        var copy = RadialPresets.Duplicate(profile, "General");
        Assert.NotEqual(profile.Id, copy.Id);
        Assert.Equal("Work 2", copy.Name);
        Assert.Equal(string.Empty, copy.Shortcut);
        Assert.True(copy.MouseButton.IsOff);
        Assert.NotEqual(profile.Items[0].Id, copy.Items[0].Id);
    }

    [Fact]
    public void New_general_profiles_are_numbered()
    {
        Assert.Equal("General 3", RadialPresets.NewProfile(RadialPreset.General, 2, _ => "General").Name);
        Assert.Equal("Media", RadialPresets.NewProfile(RadialPreset.Media, 2, k => k.EndsWith("Media", StringComparison.Ordinal) ? "Media" : k).Name);
    }

    [Fact]
    public void Reset_finds_the_preset_from_the_name_when_none_is_stored()
    {
        Func<string, string> localize = key => key.Replace("radialMenu.preset", string.Empty, StringComparison.Ordinal);
        Assert.Equal(RadialPreset.Tools, RadialPresets.PresetFor(new RadialProfile { Name = "Tools" }, localize));
        Assert.Equal(RadialPreset.General, RadialPresets.PresetFor(new RadialProfile { Name = "General 4" }, localize));
        Assert.Null(RadialPresets.PresetFor(new RadialProfile { Name = "Something" }, localize));
    }

    [Fact]
    public void Mac_shortcuts_translate_to_windows_chords()
    {
        Assert.True(MacShortcutTranslator.TryTranslate("control+option+command:49", out var chord));
        Assert.Equal(KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Space), chord);
        Assert.True(MacShortcutTranslator.TryTranslate("control+option+command:2", out var d));
        Assert.Equal(VirtualKeys.Letter('D'), d.VirtualKey);
        Assert.False(MacShortcutTranslator.TryTranslate("hyper:2", out _));
    }
}

public class RadialLinksTests
{
    [Theory]
    [InlineData("example.com:8080/x", "https://example.com:8080/x")]
    [InlineData("tel:5551234", "tel:5551234")]
    [InlineData("localhost:3000", "https://localhost:3000")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("https://example.com/a?b", "https://example.com/a?b")]
    [InlineData("mailto:me@example.com", "mailto:me@example.com")]
    public void Links_normalize(string input, string expected) => Assert.Equal(expected, RadialLinks.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("http://")]
    public void Invalid_links_are_rejected(string input) => Assert.Null(RadialLinks.Normalize(input));

    [Fact]
    public void The_favicon_is_fetched_from_the_same_origin()
    {
        Assert.Equal("https://example.com:8443/favicon.ico", RadialLinks.FaviconUri("https://example.com:8443/a/b?c#d")!.ToString());
        Assert.Equal("https://example.com/favicon.ico", RadialLinks.FaviconUri("https://example.com/x")!.ToString());
        Assert.Null(RadialLinks.FaviconUri("tel:1"));
    }

    [Fact]
    public void Labels_fall_back_by_kind()
    {
        Func<string, string> loc = k => k;
        Assert.Equal("example.com", RadialLabels.Label(new RadialItem { Kind = RadialItemKind.Url, Payload = "https://example.com/x" }, loc, p => p, _ => null));
        Assert.Equal("Custom", RadialLabels.Label(new RadialItem { Kind = RadialItemKind.Url, Payload = "https://e.x", Name = "Custom" }, loc, p => p, _ => null));
        Assert.Equal("radialMenu.mediaPlayPause", RadialLabels.Label(new RadialItem { Kind = RadialItemKind.Media, Payload = RadialMediaIds.PlayPause }, loc, p => p, _ => null));
        Assert.Equal("Ctrl+Alt+Win+Space", RadialLabels.Label(new RadialItem { Kind = RadialItemKind.Shortcut, Payload = "ctrl+alt+win:0x20" }, loc, p => p, _ => null));
        var playing = new NowPlayingSnapshot { Title = "Song", Artist = "Band", IsPlaying = true };
        Assert.Equal("Song\nBand", RadialLabels.Label(new RadialItem { Kind = RadialItemKind.Media, Payload = RadialMediaIds.NowPlaying }, loc, p => p, _ => null, null, NowPlayingState.Playing, playing));
    }

    [Fact]
    public void Now_playing_text_is_sanitized()
    {
        var snapshot = NowPlayingSanitizer.Sanitize(new NowPlayingSnapshot { Title = "  A\u0007B  ", Artist = new string('x', 400), IsPlaying = true });
        Assert.Equal("AB", snapshot!.Title);
        Assert.Equal(300, snapshot.Artist.Length);
        Assert.Null(NowPlayingSanitizer.Sanitize(new NowPlayingSnapshot { Title = "paused", IsPlaying = false }));
    }
}

public class WindowLayoutTests
{
    private static readonly PixelRect Work = new(0, 0, 1200, 900);

    [Fact]
    public void There_are_41_actions()
    {
        Assert.Equal(41, WindowLayoutActions.All.Count);
        Assert.Equal(41, WindowLayoutActions.All.Distinct().Count());
    }

    [Theory]
    [InlineData("leftHalf", 0, 0, 600, 900)]
    [InlineData("rightTwoThirds", 400, 0, 800, 900)]
    [InlineData("bottomRight", 600, 450, 600, 450)]
    [InlineData("topCenterSixth", 400, 0, 400, 450)]
    [InlineData("lowerMiddleQuarter", 0, 450, 1200, 225)]
    public void Rectangles_follow_the_work_area(string id, int x, int y, int w, int h)
    {
        var result = WindowLayoutActions.Compute(id, new PixelRect(10, 10, 300, 200), Work, [Work], 16);
        Assert.Equal(new PixelRect(x, y, w, h), result!.Value.Bounds);
    }

    [Fact]
    public void Center_keeps_the_size_and_display_moves_need_two_monitors()
    {
        var center = WindowLayoutActions.Compute("center", new PixelRect(0, 0, 400, 300), Work, [Work], 0)!.Value;
        Assert.Equal(new PixelRect(400, 300, 400, 300), center.Bounds);
        Assert.Null(WindowLayoutActions.Compute("nextDisplay", new PixelRect(0, 0, 400, 300), Work, [Work], 0));
        var second = new PixelRect(1200, 0, 2400, 1800);
        var moved = WindowLayoutActions.Compute("nextDisplay", new PixelRect(0, 0, 600, 450), Work, [Work, second], 0)!.Value;
        Assert.Equal(new PixelRect(1200, 0, 1200, 900), moved.Bounds);
        Assert.Equal(WindowLayoutApply.Maximize, WindowLayoutActions.Compute("maximize", Work, Work, [Work], 0)!.Value.Apply);
    }
}
