// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Sound;
using Xunit;

namespace Rivet.Core.Tests.Sound;

public class VolumeMathTests
{
    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(0.42, 0.42)]
    [InlineData(1.7, 1.0)] // macOS boost values cap at 100 % on Windows
    public void App_volume_is_clamped_to_the_windows_range(double input, double expected) =>
        Assert.Equal(expected, VolumeMath.ClampAppVolume(input), 6);

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.9951, true)]
    [InlineData(1.0049, true)]
    [InlineData(0.994, false)]
    public void Unity_uses_the_spec_tolerance(double value, bool unity) => Assert.Equal(unity, VolumeMath.IsUnity(value));

    [Theory]
    [InlineData("50", "en-US", 0.5)]
    [InlineData("50%", "en-US", 0.5)]
    [InlineData("  75 %  ", "en-US", 0.75)]
    [InlineData("12.5", "en-US", 0.125)]
    [InlineData("12,5", "de-DE", 0.125)]
    [InlineData("150", "en-US", 1.0)]
    [InlineData("-5", "en-US", 0.0)]
    public void Percent_text_parses_like_the_mac_editor(string text, string culture, double expected)
    {
        Assert.True(VolumeMath.TryParsePercent(text, 100, CultureInfo.GetCultureInfo(culture), out var scalar));
        Assert.Equal(expected, scalar, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("loud")]
    [InlineData("%")]
    [InlineData(null)]
    public void Invalid_percent_text_keeps_editing(string? text) =>
        Assert.False(VolumeMath.TryParsePercent(text, 100, CultureInfo.InvariantCulture, out _));

    [Fact]
    public void Percent_formatting_follows_the_language()
    {
        Assert.Equal("42%", VolumeMath.FormatPercent(0.42, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(100, VolumeMath.ToPercent(1.2));
        Assert.Equal(0, VolumeMath.ToPercent(double.NaN));
    }
}

public class DeviceClassifierTests
{
    [Fact]
    public void Normalize_folds_case_diacritics_and_punctuation() =>
        Assert.Equal("ecouteurs airpods pro ", DeviceClassifier.Normalize("Écouteurs AirPods-Pro!"));

    [Theory]
    [InlineData("Headphones (Realtek(R) Audio)", true)]
    [InlineData("Headset Earphone (Jabra Evolve2 65)", true)]
    [InlineData("Galaxy Buds2 Pro", true)]
    [InlineData("Speakers (Realtek(R) Audio)", false)]
    [InlineData("JBL Flip 5", false)]
    [InlineData("LG ULTRAGEAR (NVIDIA High Definition Audio)", false)]
    public void Name_heuristic_matches_the_spec_words(string name, bool expected) =>
        Assert.Equal(expected, DeviceClassifier.NameLooksLikeHeadphones(name, "{0.0.0.00000000}.{guid}", null));

    [Theory]
    [InlineData(AudioFormFactor.Headphones, false, "Realtek", true)]
    [InlineData(AudioFormFactor.Headset, true, "WH-1000XM4", true)]
    [InlineData(AudioFormFactor.Speakers, true, "JBL Flip 5", false)] // Bluetooth speakers are not headphones
    [InlineData(AudioFormFactor.Unknown, true, "LE-Device", true)]
    [InlineData(AudioFormFactor.Speakers, false, "Headphones (USB Audio)", true)] // name fallback
    [InlineData(AudioFormFactor.Speakers, false, "Speakers", false)]
    public void Headphones_prefer_the_form_factor(AudioFormFactor formFactor, bool bluetooth, string name, bool expected) =>
        Assert.Equal(expected, DeviceClassifier.IsHeadphones(formFactor, bluetooth, name, "id", null));

    [Theory]
    [InlineData("HDAUDIO", "Speakers", AudioDeviceTier.BuiltIn)]
    [InlineData("INTELAUDIO", "Speakers", AudioDeviceTier.BuiltIn)]
    [InlineData("USB", "Speakers (USB Audio)", AudioDeviceTier.Hardware)]
    [InlineData("BTHENUM", "Headphones", AudioDeviceTier.Hardware)]
    [InlineData("ROOT", "Line (Something)", AudioDeviceTier.Virtual)]
    [InlineData(null, "CABLE Input (VB-Audio Virtual Cable)", AudioDeviceTier.Virtual)]
    [InlineData(null, "Speakers (Realtek(R) Audio)", AudioDeviceTier.Hardware)]
    public void Tier_comes_from_the_enumerator_then_the_name(string? enumerator, string name, AudioDeviceTier expected) =>
        Assert.Equal(expected, DeviceClassifier.TierFor(enumerator, name));

    [Theory]
    [InlineData(@"{1}.HDAUDIO\FUNC_01&VEN_10EC&DEV_0295&SUBSYS_1028\4&2B5E&0&0001", "HDAUDIO")]
    [InlineData(@"{2}.\\?\usb#vid_046d&pid_0a8f&mi_00#7&1b5", "USB")]
    [InlineData(@"{1}.ROOT\MEDIA\0000", "ROOT")]
    [InlineData(@"{1}.BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID", "BTHENUM")]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void Enumerator_is_read_from_the_parent_instance_path(string? path, string? expected) =>
        Assert.Equal(expected, DeviceClassifier.EnumeratorFromInstancePath(path));

    [Fact]
    public void Form_factor_numbers_match_windows()
    {
        Assert.Equal(AudioFormFactor.Headphones, DeviceClassifier.ParseFormFactor(3));
        Assert.Equal(AudioFormFactor.Headset, DeviceClassifier.ParseFormFactor(5));
        Assert.Equal(AudioFormFactor.Unknown, DeviceClassifier.ParseFormFactor(99));
        Assert.True(DeviceClassifier.IsBluetoothEnumerator("BTHLEDevice"));
        Assert.False(DeviceClassifier.IsBluetoothEnumerator("USB"));
    }
}

public class OutputCycleTests
{
    private static readonly string[] All = ["A", "B", "C"];

    [Theory]
    [InlineData("A", "B")]
    [InlineData("B", "C")]
    [InlineData("C", "A")] // wraps
    [InlineData("X", "A")] // current not a candidate → first
    [InlineData(null, "A")]
    public void Next_moves_to_the_following_candidate(string? current, string expected)
    {
        var result = OutputCycle.Next(All, All, current);
        Assert.Equal(OutputCycleOutcome.Switch, result.Outcome);
        Assert.Equal(expected, result.Target);
    }

    [Fact]
    public void Only_connected_selected_outputs_are_candidates()
    {
        Assert.Equal("C", OutputCycle.Next(["A", "B", "C"], ["A", "C"], "A").Target);
        Assert.Equal(OutputCycleOutcome.NoCandidates, OutputCycle.Next(["A", "B"], ["C"], "C").Outcome);
        Assert.Equal(OutputCycleOutcome.NoCandidates, OutputCycle.Next([], All, "A").Outcome);
        Assert.Equal(OutputCycleOutcome.NoCandidates, OutputCycle.Next(null, All, "A").Outcome);
    }

    [Fact]
    public void A_single_current_candidate_changes_nothing()
    {
        var result = OutputCycle.Next(["A", "B"], ["B"], "B");
        Assert.Equal(OutputCycleOutcome.Unchanged, result.Outcome);
        Assert.Equal("B", result.Target);
    }

    [Fact]
    public void Selection_is_sanitized_and_deduplicated() =>
        Assert.Equal("B", OutputCycle.Next([" ", "A", "A", "B", "bad\nid"], All, "A").Target);

    [Fact]
    public void Selection_keeps_visible_order_then_disconnected_entries()
    {
        var visible = new[] { "A", "B", "C" };
        var selection = OutputCycle.UpdateSelection(["X", "B"], visible, "A", isSelected: true);
        Assert.Equal(["A", "B", "X"], selection);
        Assert.Equal(["A", "X"], OutputCycle.UpdateSelection(selection, visible, "B", isSelected: false));
        Assert.Equal(["C"], OutputCycle.UpdateSelection(null, visible, "C", isSelected: true));
    }
}

public class PriorityListTests
{
    private static AudioDevice D(string id, AudioDeviceTier tier) => TestAudioPlatform.Output(id, id, tier);

    [Fact]
    public void A_new_list_puts_current_first_then_tiers_stably()
    {
        var available = new[] { D("V", AudioDeviceTier.Virtual), D("H", AudioDeviceTier.Hardware), D("B", AudioDeviceTier.BuiltIn), D("H2", AudioDeviceTier.Hardware) };
        Assert.Equal(["H2", "B", "H", "V"], PriorityLists.Initial(available, "H2"));
        Assert.Equal(["B", "H", "H2", "V"], PriorityLists.Initial(available, "gone"));
    }

    [Fact]
    public void New_devices_go_above_the_first_virtual_entry_or_first_when_current()
    {
        var tiers = new Dictionary<string, AudioDeviceTier> { ["B"] = AudioDeviceTier.BuiltIn, ["H"] = AudioDeviceTier.Hardware, ["V"] = AudioDeviceTier.Virtual };
        AudioDeviceTier? TierOf(string id) => tiers.TryGetValue(id, out var t) ? t : null;
        var available = new HashSet<string> { "B", "H", "V", "N" };
        IReadOnlyList<string> list = ["B", "H", "V"];

        Assert.Equal(["B", "H", "N", "V"], PriorityLists.Place(list, "N", AudioDeviceTier.Hardware, false, TierOf, available));
        Assert.Equal(["B", "H", "V", "N"], PriorityLists.Place(list, "N", AudioDeviceTier.Virtual, false, TierOf, available));
        Assert.Equal(["N", "B", "H", "V"], PriorityLists.Place(list, "N", AudioDeviceTier.Virtual, true, TierOf, available));
        Assert.Equal(["B", "H", "N"], PriorityLists.Place(["B", "H"], "N", AudioDeviceTier.Hardware, false, TierOf, available));
        Assert.Same(list, PriorityLists.Place(list, "H", AudioDeviceTier.Hardware, false, TierOf, available));
    }

    [Fact]
    public void Enforcement_targets_the_first_connected_entry_unless_current_is_unranked()
    {
        IReadOnlyList<string> list = ["A", "B", "C"];
        Assert.Equal("B", PriorityLists.Target(list, new HashSet<string> { "C", "B" }));
        Assert.Equal("A", PriorityLists.SwitchTarget(list, new HashSet<string> { "A", "C" }, "C"));
        Assert.Null(PriorityLists.SwitchTarget(list, new HashSet<string> { "A", "C" }, "A"));
        Assert.Null(PriorityLists.SwitchTarget(list, new HashSet<string> { "A", "X" }, "X")); // unranked current: hands off
        Assert.Null(PriorityLists.SwitchTarget(list, new HashSet<string>(), null));
    }

    [Fact]
    public void Lists_hold_at_most_64_entries_dropping_disconnected_ones_first()
    {
        var list = Enumerable.Range(0, 64).Select(i => $"D{i}").ToList();
        var available = list.Where((_, i) => i != 10).Append("NEW").ToHashSet();
        var placed = PriorityLists.Place(list, "NEW", AudioDeviceTier.Hardware, false, _ => AudioDeviceTier.Hardware, available);
        Assert.Equal(64, placed.Count);
        Assert.DoesNotContain("D10", placed);
        Assert.Contains("NEW", placed);
    }

    [Fact]
    public void Move_and_names()
    {
        Assert.Equal(["B", "A", "C"], PriorityLists.Move(["A", "B", "C"], 1, 0));
        IReadOnlyList<string> unchanged = ["A"];
        Assert.Same(unchanged, PriorityLists.Move(unchanged, 0, 3));

        var names = PriorityLists.UpdateNames(
            new Dictionary<string, string> { ["A"] = "Old A", ["Gone"] = "Removed" },
            ["A", "B"],
            [TestAudioPlatform.Output("A", "New A"), TestAudioPlatform.Output("Z", "Not listed")]);
        Assert.Equal("New A", names["A"]);
        Assert.False(names.ContainsKey("Gone"));
        Assert.False(names.ContainsKey("Z"));
    }
}

public class PreciseVolumeGateTests
{
    [Fact]
    public void Presses_closer_than_30_ms_are_dropped()
    {
        var gate = new PreciseVolumeGate();
        Assert.True(gate.Accept(1, 1000));
        Assert.False(gate.Accept(1, 1020));
        Assert.False(gate.Accept(1, 1030));
        Assert.True(gate.Accept(1, 1031));
    }

    [Fact]
    public void A_quick_reversal_needs_three_presses()
    {
        var gate = new PreciseVolumeGate();
        Assert.True(gate.Accept(1, 0));
        Assert.False(gate.Accept(-1, 100));
        Assert.False(gate.Accept(-1, 150));
        Assert.True(gate.Accept(-1, 200));
        Assert.True(gate.Accept(-1, 260));
    }

    [Fact]
    public void A_slow_reversal_is_accepted_at_once()
    {
        var gate = new PreciseVolumeGate();
        Assert.True(gate.Accept(1, 0));
        Assert.True(gate.Accept(-1, 300));
        Assert.False(gate.Accept(0, 900));
    }
}

public class MixerArrangementTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"order\": 5}")]
    public void Invalid_json_is_an_empty_arrangement(string? json)
    {
        var arrangement = MixerArrangement.Parse(json);
        Assert.Empty(arrangement.Order);
        Assert.Empty(arrangement.Pinned);
    }

    [Fact]
    public void Duplicates_and_empty_ids_are_dropped_and_json_round_trips()
    {
        var arrangement = MixerArrangement.Parse("{\"order\":[\"a\",\"a\",\"\",1,\"b\"],\"pinned\":[\"b\",\"b\"]}");
        Assert.Equal(["a", "b"], arrangement.Order);
        Assert.Equal(["b"], arrangement.Pinned);
        var again = MixerArrangement.Parse(arrangement.ToJson());
        Assert.Equal(arrangement.Order, again.Order);
        Assert.Equal(arrangement.Pinned, again.Pinned);
    }

    [Fact]
    public void Pinned_first_then_custom_order_then_unknown_alphabetically()
    {
        var rows = new[] { "chrome", "discord", "edge", "spotify", null, "zoom" };
        var arrangement = new MixerArrangement(["edge", "chrome"], ["spotify"]);
        var ordered = arrangement.Apply(rows, r => r);
        Assert.Equal(["spotify", "edge", "chrome", "discord", null, "zoom"], ordered);
    }

    [Fact]
    public void Pin_and_unpin()
    {
        var arrangement = MixerArrangement.Empty.Pin("a").Pin("b").Pin("a");
        Assert.Equal(["a", "b"], arrangement.Pinned);
        Assert.Equal(["b"], arrangement.Unpin("a").Pinned);
        Assert.Same(arrangement, arrangement.Unpin("missing"));
    }

    [Fact]
    public void Moving_writes_back_only_into_the_visible_slots()
    {
        // x is hidden or closed: it keeps its remembered position.
        var arrangement = new MixerArrangement(["x", "a", "b", "c"], []);
        Assert.Equal(["x", "c", "a", "b"], arrangement.Move(["a", "b", "c"], "c", "a", after: false).Order);
        Assert.Equal(["x", "b", "a", "c"], arrangement.Move(["a", "b", "c"], "a", "b", after: true).Order);

        // A visible app the order did not know yet gets a slot at the end first.
        var withNew = new MixerArrangement(["a", "y", "b"], []);
        Assert.Equal(["n", "y", "a", "b"], withNew.Move(["a", "b", "n"], "n", "a", after: false).Order);
    }

    [Fact]
    public void Moving_onto_itself_or_outside_the_group_changes_nothing()
    {
        var arrangement = new MixerArrangement(["a", "b"], []);
        Assert.Same(arrangement, arrangement.Move(["a", "b"], "a", "a", after: true));
        Assert.Same(arrangement, arrangement.Move(["a", "b"], "a", "zzz", after: true));
    }
}
