// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Displays;
using Xunit;

namespace Rivet.Core.Tests.Displays;

public class BrightnessMathTests
{
    [Theory]
    [InlineData("standard", 1.0 / 16)]
    [InlineData("half", 1.0 / 32)]
    [InlineData("quarter", 1.0 / 64)]
    [InlineData("bogus", 1.0 / 16)]
    public void Step_sizes_follow_the_setting(string setting, double expected) =>
        Assert.Equal(expected, BrightnessMath.StepSize(setting), 9);

    [Theory]
    [InlineData(0.5, 1, 0.5625)]
    [InlineData(0.5, -1, 0.4375)]
    [InlineData(0.53, 1, 0.5625)] // between grid points: snaps to the next one up
    [InlineData(0.53, -1, 0.5)]
    [InlineData(1.0, 1, 1.0)]
    [InlineData(0.0, -1, 0.0)]
    [InlineData(0.0625, -1, 0.0)]
    [InlineData(0.97, 1, 1.0)]
    public void Steps_land_on_the_grid(double level, int direction, double expected) =>
        Assert.Equal(expected, BrightnessMath.Step(level, direction, 1.0 / 16), 6);

    [Fact]
    public void Several_presses_add_up() =>
        Assert.Equal(0.6875, BrightnessMath.Steps(0.5, 3, 1.0 / 16), 6);

    [Theory]
    [InlineData(0.5, 100, 50)]
    [InlineData(0.5, 0, 50)] // a reported maximum of 0 means 100
    [InlineData(0.5, 255, 128)]
    [InlineData(1.2, 100, 100)]
    [InlineData(-1, 100, 0)]
    public void Levels_map_to_device_units(double level, int maximum, int expected) =>
        Assert.Equal(expected, BrightnessMath.ToDdc(level, maximum));

    [Fact]
    public void Device_units_map_back_to_levels()
    {
        Assert.Equal(0.3, BrightnessMath.FromDdc(30, 0), 9);
        Assert.Equal(1.0, BrightnessMath.FromDdc(300, 255), 9);
    }

    [Theory]
    [InlineData(0.1, 0.0, 0.4)]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(0.25, 0.0, 1.0)]
    [InlineData(0.625, 0.5, 1.0)]
    [InlineData(1.0, 1.0, 1.0)]
    public void Extra_dimming_splits_the_slider(double level, double hardware, double picture)
    {
        var split = BrightnessMath.SplitExtended(level);
        Assert.Equal(hardware, split.Hardware, 9);
        Assert.Equal(picture, split.Picture, 9);
        Assert.Equal(level, BrightnessMath.JoinExtended(split.Hardware, split.Picture), 9);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.01, 1)]
    [InlineData(0.0625, 1)]
    [InlineData(0.0626, 2)]
    [InlineData(0.5, 8)]
    [InlineData(1.0, 16)]
    public void Osd_segments_round_up(double level, int filled) =>
        Assert.Equal(filled, BrightnessMath.FilledSegments(level));

    [Fact]
    public void Overlay_never_goes_fully_black()
    {
        Assert.Equal(0, BrightnessMath.OverlayAlpha(1), 9);
        Assert.Equal(BrightnessMath.MaxOverlayAlpha, BrightnessMath.OverlayAlpha(0), 9);
        Assert.Equal(0.46, BrightnessMath.OverlayAlpha(0.5), 9);
        Assert.True(BrightnessMath.IsUndimmed(0.9995));
    }

    [Fact]
    public void Path_lists_keep_the_newest_sixteen()
    {
        var list = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            list = PathList.Add(list, $"p{i}");
        }

        Assert.Equal(16, list.Count);
        Assert.Equal("p4", list[0]);
        list = PathList.Add(list, "P10");
        Assert.Equal("P10", list[^1]);
        Assert.Equal(16, list.Count);
        Assert.Equal(["a", "b"], PathList.Clean(["a", " ", "A", "b", ""]));
    }
}

public class BrightnessServiceTests
{
    [Fact]
    public void Routes_follow_the_kind_of_display()
    {
        var rig = new BrightnessRig(start: false);
        rig.Catalog.Devices.Add(BrightnessRig.Device(BrightnessRig.Tv, "TV", x: 3840));
        rig.Ddc.Modes[BrightnessRig.Tv] = DdcMode.Dead;
        rig.Catalog.Devices.Add(BrightnessRig.Device(@"\\.\DISPLAY4", "Blind", x: 5760));
        rig.Ddc.Modes[@"\\.\DISPLAY4"] = DdcMode.WriteOnly;
        rig.Catalog.Devices.Add(BrightnessRig.Device(@"\\.\DISPLAY5", "Projector", x: 7680));
        rig.Service.Sync(true);

        var panel = rig.Status(BrightnessRig.Panel);
        Assert.Equal(BrightnessRoute.System, panel.Route);
        Assert.Equal(0.6, panel.Level, 9);
        var dell = rig.Status(BrightnessRig.Dell);
        Assert.Equal((BrightnessRoute.Ddc, DdcState.Live), (dell.Route, dell.Ddc));
        Assert.Equal(0.3, dell.Level, 9);
        Assert.True(dell.OffersExtendedDimming);

        // No reply yet: the first write decides between write-only and dead.
        var tv = rig.Status(BrightnessRig.Tv);
        Assert.Equal((BrightnessRoute.Ddc, DdcState.Unknown), (tv.Route, tv.Ddc));
        Assert.Equal(BrightnessMath.UnknownLevel, tv.Level, 9);
        Assert.True(tv.OffersForcedSoftware);

        var projector = rig.Status(@"\\.\DISPLAY5");
        Assert.Equal(BrightnessRoute.Software, projector.Route);
        Assert.Equal(1, projector.Level, 9);

        // Built-in first, then left to right.
        Assert.Equal([BrightnessRig.Panel, BrightnessRig.Dell, BrightnessRig.Tv, @"\\.\DISPLAY4", @"\\.\DISPLAY5"], rig.Service.Displays.Select(d => d.Id));
    }

    [Fact]
    public void A_cached_write_only_connection_is_not_probed_as_unknown()
    {
        var rig = new BrightnessRig(start: false);
        rig.Ddc.Modes[BrightnessRig.Dell] = DdcMode.WriteOnly;
        rig.Settings.Set(DisplaySettings.WriteOnlyPaths, ["path:DELL U2720Q"]);
        rig.Service.Sync(true);
        Assert.Equal(DdcState.WriteOnly, rig.Status(BrightnessRig.Dell).Ddc);
    }

    [Fact]
    public void Slider_drags_fold_into_one_write_of_the_newest_value()
    {
        var rig = new BrightnessRig();
        rig.Worker.AutoRun = false;
        rig.Service.SetLevel(BrightnessRig.Dell, 0.2);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.4);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.7);
        Assert.Equal(0.7, rig.Status(BrightnessRig.Dell).Level, 9); // published at once
        Assert.Equal(1, rig.Worker.Queued);
        rig.Worker.Drain();
        Assert.Equal([(BrightnessRig.Dell, 70)], rig.Ddc.Writes);
    }

    [Fact]
    public void Ddc_commands_to_one_monitor_are_at_least_50_ms_apart()
    {
        var rig = new BrightnessRig();
        rig.Worker.Pauses.Clear();
        rig.Service.SetLevel(BrightnessRig.Dell, 0.5);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.6);
        Assert.Equal(2, rig.Ddc.Writes.Count);
        Assert.Equal(2, rig.Worker.Pauses.Count);
        Assert.All(rig.Worker.Pauses, p => Assert.Equal(BrightnessMath.DdcPacingSeconds, p, 6));

        // After a quiet moment there is nothing to wait for.
        rig.Worker.Pauses.Clear();
        rig.Worker.Advance(1);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.7);
        Assert.Empty(rig.Worker.Pauses);
    }

    [Fact]
    public void Panel_writes_go_through_the_system_in_percent()
    {
        var rig = new BrightnessRig();
        rig.Service.SetLevel(BrightnessRig.Panel, 0.43);
        Assert.Equal([(BrightnessRig.Panel, 43)], rig.System.Writes);
        Assert.Null(rig.Status(BrightnessRig.Panel).Error);
    }

    [Fact]
    public void A_first_accepted_write_marks_the_connection_write_only()
    {
        var rig = new BrightnessRig(start: false);
        rig.Ddc.Modes[BrightnessRig.Dell] = DdcMode.WriteOnly;
        rig.Service.Sync(true);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.8);
        Assert.Equal(DdcState.WriteOnly, rig.Status(BrightnessRig.Dell).Ddc);
        Assert.Contains("path:DELL U2720Q", rig.Settings.Get(DisplaySettings.WriteOnlyPaths));
        Assert.Equal([(BrightnessRig.Dell, 80)], rig.Ddc.Writes);
    }

    [Fact]
    public void A_rejected_first_write_dims_the_picture_instead()
    {
        var rig = new BrightnessRig(start: false);
        rig.Ddc.Modes[BrightnessRig.Dell] = DdcMode.Dead;
        rig.Service.Sync(true);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.4);
        var dell = rig.Status(BrightnessRig.Dell);
        Assert.Equal((BrightnessRoute.Software, DdcState.Dead), (dell.Route, dell.Ddc));
        Assert.Equal(0.4, rig.Dimmer.Active[BrightnessRig.Dell], 9);
        Assert.Empty(rig.Settings.Get(DisplaySettings.WriteOnlyPaths));

        // Later moves only touch the overlay.
        rig.Service.SetLevel(BrightnessRig.Dell, 1);
        Assert.False(rig.Dimmer.Active.ContainsKey(BrightnessRig.Dell));
    }

    [Fact]
    public void A_failed_write_on_a_write_only_connection_forgets_it_and_rebuilds()
    {
        var rig = new BrightnessRig(start: false);
        rig.Ddc.Modes[BrightnessRig.Dell] = DdcMode.WriteOnly;
        rig.Settings.Set(DisplaySettings.WriteOnlyPaths, ["path:DELL U2720Q"]);
        rig.Service.Sync(true);
        var enumerations = rig.Catalog.Enumerations;
        rig.Ddc.Modes[BrightnessRig.Dell] = DdcMode.Dead; // e.g. now behind an adapter
        rig.Service.SetLevel(BrightnessRig.Dell, 0.3);
        Assert.Empty(rig.Settings.Get(DisplaySettings.WriteOnlyPaths));
        Assert.Equal(enumerations + 1, rig.Catalog.Enumerations);
        Assert.Equal(DdcState.Unknown, rig.Status(BrightnessRig.Dell).Ddc);
    }

    [Fact]
    public void Extra_dimming_uses_the_hardware_minimum_then_the_picture()
    {
        var rig = new BrightnessRig(start: false);
        rig.Settings.Set(DisplaySettings.ExtendedDimmingPaths, ["path:DELL U2720Q"]);
        rig.Service.Sync(true);
        Assert.Equal(0.25 + (0.3 * 0.75), rig.Status(BrightnessRig.Dell).Level, 9);
        rig.Log.Clear();

        rig.Service.SetLevel(BrightnessRig.Dell, 0.1);
        Assert.Equal(["ddc \\\\.\\DISPLAY2 0", "dim \\\\.\\DISPLAY2 0.4"], rig.Log);

        // Drags inside the picture range skip DDC.
        rig.Service.SetLevel(BrightnessRig.Dell, 0.05);
        Assert.Equal("dim \\\\.\\DISPLAY2 0.2", rig.Log[^1]);
        Assert.Single(rig.Ddc.Writes);

        // The picture is restored before any brighter hardware write.
        rig.Log.Clear();
        rig.Service.SetLevel(BrightnessRig.Dell, 0.625);
        Assert.Equal(["undim \\\\.\\DISPLAY2", "ddc \\\\.\\DISPLAY2 50"], rig.Log);
    }

    [Fact]
    public void Turning_extra_dimming_off_restores_the_picture()
    {
        var rig = new BrightnessRig(start: false);
        rig.Settings.Set(DisplaySettings.ExtendedDimmingPaths, ["path:DELL U2720Q"]);
        rig.Service.Sync(true);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.1);
        Assert.True(rig.Dimmer.Active.ContainsKey(BrightnessRig.Dell));
        rig.Service.SetExtendedDimming(BrightnessRig.Dell, false);
        Assert.False(rig.Dimmer.Active.ContainsKey(BrightnessRig.Dell));
        Assert.Equal(0, rig.Status(BrightnessRig.Dell).Level, 9); // the monitor sits at its minimum
        Assert.False(rig.Status(BrightnessRig.Dell).ExtendedDimming);
        Assert.Empty(rig.Settings.Get(DisplaySettings.ExtendedDimmingPaths));
    }

    [Fact]
    public void Steps_inside_the_trust_window_use_the_remembered_level()
    {
        var rig = new BrightnessRig();
        var reads = rig.System.Reads;
        rig.Worker.Advance(1);
        rig.Service.Step(+1, BrightnessRig.Panel);
        Assert.Equal(reads, rig.System.Reads);
        Assert.Equal(0.625, rig.Status(BrightnessRig.Panel).Level, 9);
        Assert.Equal((BrightnessRig.Panel, 63), rig.System.Writes[^1]);
    }

    [Fact]
    public void A_step_after_a_pause_reads_the_display_first()
    {
        var rig = new BrightnessRig();
        rig.Worker.Advance(10);
        rig.Ddc.Values[BrightnessRig.Dell] = 80; // changed with the monitor's own buttons
        rig.Service.Step(+1, BrightnessRig.Dell);
        Assert.Equal(0.8125, rig.Status(BrightnessRig.Dell).Level, 9);
        Assert.Equal((BrightnessRig.Dell, 81), rig.Ddc.Writes[^1]);
    }

    [Fact]
    public void Presses_during_a_read_are_added_after_it()
    {
        var rig = new BrightnessRig();
        rig.Worker.Advance(10);
        rig.Worker.AutoRun = false;
        var reads = rig.Ddc.Reads;
        rig.Service.Step(+1, BrightnessRig.Dell);
        rig.Service.Step(+1, BrightnessRig.Dell);
        rig.Service.Step(-1, BrightnessRig.Dell);
        rig.Service.Step(+1, BrightnessRig.Dell);
        Assert.Equal(1, rig.Worker.Queued); // one read for all presses
        rig.Worker.Drain();
        Assert.Equal(reads + 1, rig.Ddc.Reads);
        Assert.Equal(BrightnessMath.Steps(0.3, 2, 1.0 / 16), rig.Status(BrightnessRig.Dell).Level, 9);
    }

    [Fact]
    public void Shortcuts_act_on_the_main_display_or_the_one_under_the_pointer()
    {
        var rig = new BrightnessRig();
        rig.Pointer = BrightnessRig.Dell;
        Assert.Equal(BrightnessRig.Panel, rig.Service.ShortcutTarget());
        rig.Settings.Set(DisplaySettings.FollowPointer, true);
        Assert.Equal(BrightnessRig.Dell, rig.Service.ShortcutTarget());
        rig.Pointer = @"\\.\DISPLAY9";
        Assert.Equal(BrightnessRig.Panel, rig.Service.ShortcutTarget());
    }

    [Fact]
    public void Key_steps_follow_the_setting()
    {
        var rig = new BrightnessRig();
        rig.Settings.Set(DisplaySettings.KeyStep, "quarter");
        rig.Service.Step(-1, BrightnessRig.Panel);
        Assert.Equal(0.59375, rig.Status(BrightnessRig.Panel).Level, 9);
    }

    [Fact]
    public void Adjustments_raise_osd_feedback_only_when_asked()
    {
        var rig = new BrightnessRig();
        var feedback = new List<BrightnessFeedback>();
        rig.Service.Adjusted += (_, f) => feedback.Add(f);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.4);
        Assert.Empty(feedback);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.5, showOsd: true);
        Assert.Equal(0.5, Assert.Single(feedback).Level, 9);
        rig.Settings.Set(DisplaySettings.OsdEnabled, true);
        rig.Service.Step(+1, BrightnessRig.Dell);
        Assert.Equal(2, feedback.Count);
    }

    [Fact]
    public void Stopping_removes_overlays_and_leaves_hardware_levels_alone()
    {
        var rig = new BrightnessRig(start: false);
        rig.Catalog.Devices.Add(BrightnessRig.Device(BrightnessRig.Tv, "TV", x: 3840));
        rig.Service.Sync(true);
        rig.Service.SetLevel(BrightnessRig.Tv, 0.5);
        rig.Service.SetLevel(BrightnessRig.Dell, 0.9);
        Assert.True(rig.Dimmer.Active.ContainsKey(BrightnessRig.Tv));

        rig.Service.Sync(false);
        Assert.Empty(rig.Dimmer.Active);
        Assert.Equal(1, rig.Dimmer.RemoveAllCalls);
        Assert.Equal(90, rig.Ddc.Values[BrightnessRig.Dell]);
        Assert.Equal(1, rig.Ddc.Closes);
        Assert.Empty(rig.Service.Displays);
        Assert.True(rig.Worker.Disposed);

        // Nothing happens while stopped.
        rig.Service.SetLevel(BrightnessRig.Dell, 0.1);
        Assert.Equal(90, rig.Ddc.Values[BrightnessRig.Dell]);
    }

    [Fact]
    public void Dim_the_picture_moves_a_monitor_to_the_overlay_and_back()
    {
        var rig = new BrightnessRig(start: false);
        rig.Ddc.Modes[BrightnessRig.Dell] = DdcMode.WriteOnly;
        rig.Settings.Set(DisplaySettings.WriteOnlyPaths, ["path:DELL U2720Q"]);
        rig.Service.Sync(true);

        rig.Service.SetForcedSoftware(BrightnessRig.Dell, true);
        var dell = rig.Status(BrightnessRig.Dell);
        Assert.Equal(BrightnessRoute.Software, dell.Route);
        Assert.True(dell.ForcedSoftware && dell.OffersForcedSoftware);
        Assert.Equal(1, dell.Level, 9); // the picture is untouched until the slider moves
        rig.Service.SetLevel(BrightnessRig.Dell, 0.4);
        Assert.Equal(0.4, rig.Dimmer.Active[BrightnessRig.Dell], 9);
        Assert.Empty(rig.Ddc.Writes);

        rig.Service.SetForcedSoftware(BrightnessRig.Dell, false);
        Assert.False(rig.Dimmer.Active.ContainsKey(BrightnessRig.Dell));
        Assert.Empty(rig.Settings.Get(DisplaySettings.WriteOnlyPaths)); // probed again
        Assert.Equal((BrightnessRoute.Ddc, DdcState.Unknown), (rig.Status(BrightnessRig.Dell).Route, rig.Status(BrightnessRig.Dell).Ddc));
    }

    [Fact]
    public void A_dimmed_display_returning_from_a_gap_gets_at_least_a_quarter()
    {
        var rig = new BrightnessRig();
        rig.Connect(BrightnessRig.Tv, "TV", DdcMode.None, 3840);
        rig.Service.SetLevel(BrightnessRig.Tv, 0.1);
        Assert.Equal(0.1, rig.Dimmer.Active[BrightnessRig.Tv], 9);

        rig.Disconnect(BrightnessRig.Tv);
        Assert.False(rig.Dimmer.Active.ContainsKey(BrightnessRig.Tv));
        rig.Connect(BrightnessRig.Tv, "TV", DdcMode.None, 3840);
        Assert.Equal(BrightnessMath.ReconnectFloor, rig.Status(BrightnessRig.Tv).Level, 9);
        Assert.Equal(BrightnessMath.ReconnectFloor, rig.Dimmer.Active[BrightnessRig.Tv], 9);
    }

    [Fact]
    public void Display_changes_settle_before_one_rebuild()
    {
        var rig = new BrightnessRig();
        var before = rig.Catalog.Enumerations;
        for (var i = 0; i < 5; i++)
        {
            rig.Catalog.RaiseChanged();
            rig.Worker.Advance(0.05);
        }

        Assert.Equal(before, rig.Catalog.Enumerations);
        rig.Worker.Advance(0.3);
        Assert.Equal(before + 1, rig.Catalog.Enumerations);
        rig.Worker.Advance(2);
        Assert.Equal(before + 1, rig.Catalog.Enumerations);
    }

    [Fact]
    public void Waking_rebuilds_after_the_monitors_settle()
    {
        var rig = new BrightnessRig();
        var before = rig.Catalog.Enumerations;
        rig.Catalog.RaiseResumed();
        rig.Worker.Advance(2.9);
        Assert.Equal(before, rig.Catalog.Enumerations);
        rig.Worker.Advance(0.2);
        Assert.Equal(before + 1, rig.Catalog.Enumerations);
    }

    [Fact]
    public void A_refresh_keeps_probes_and_rereads_readable_levels()
    {
        var rig = new BrightnessRig();
        var opens = rig.Ddc.Opens;
        rig.Worker.Advance(5);
        rig.System.Levels[BrightnessRig.Panel] = 20;
        rig.Ddc.Values[BrightnessRig.Dell] = 90;
        rig.Service.Refresh();
        Assert.Equal(opens, rig.Ddc.Opens);
        Assert.Equal(0.2, rig.Status(BrightnessRig.Panel).Level, 9);
        Assert.Equal(0.9, rig.Status(BrightnessRig.Dell).Level, 9);

        // Refreshes right after a rebuild are skipped.
        var enumerations = rig.Catalog.Enumerations;
        rig.Service.Refresh();
        Assert.Equal(enumerations, rig.Catalog.Enumerations);
    }

    [Fact]
    public void A_new_monitor_on_a_known_display_number_is_probed_again()
    {
        var rig = new BrightnessRig();
        rig.Catalog.Devices[1] = BrightnessRig.Device(BrightnessRig.Dell, "LG UltraFine", x: 1920);
        rig.Ddc.Modes[BrightnessRig.Dell] = DdcMode.WriteOnly;
        rig.Catalog.RaiseChanged();
        rig.Worker.Advance(BrightnessMath.SettleSeconds);
        var status = rig.Status(BrightnessRig.Dell);
        Assert.Equal("LG UltraFine", status.Name);
        Assert.Equal(DdcState.Unknown, status.Ddc);
        Assert.Equal(BrightnessMath.UnknownLevel, status.Level, 9);
    }
}
