// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Xunit;

namespace Rivet.Core.Tests.RecordingEditor;

public class TimelineTests
{
    [Fact]
    public void Trim_and_cut_vectors()
    {
        var timeline = new EditTimeline(20, 0, 20, [new CutRange(5, 8)]);
        Assert.Equal([new TimeRange(0, 5), new TimeRange(8, 20)], timeline.KeptRanges);
        Assert.Equal(17, timeline.OutputDuration, 9);
        Assert.Equal(4, timeline.SourceTime(4), 9);
        Assert.Equal(9, timeline.SourceTime(6), 9);
        Assert.Null(timeline.OutputTime(6.5));
        Assert.Equal(9, timeline.OutputTime(12)!.Value, 9);
    }

    [Fact]
    public void Clocks_are_inverse_where_the_moment_survives()
    {
        var timeline = new EditTimeline(30, 1.5, 27, [new CutRange(4, 6), new CutRange(10, 12.5)]);
        for (var s = 0.0; s <= 30; s += 0.137)
        {
            if (timeline.OutputTime(s) is { } o)
            {
                Assert.Equal(s, timeline.SourceTime(o), 9);
            }
        }
    }

    [Fact]
    public void Cut_normalization_merges_and_drops()
    {
        Assert.Equal([new CutRange(2, 9)], EditTimeline.NormalizeCuts([new CutRange(2, 6), new CutRange(5, 9)], 20));
        Assert.Empty(EditTimeline.NormalizeCuts([new CutRange(3, 3.02)], 20));
        Assert.Equal([new CutRange(15, 20)], EditTimeline.NormalizeCuts([new CutRange(15, 40)], 20));
    }

    [Fact]
    public void Trim_keeps_at_least_two_tenths()
    {
        var trim = EditTimeline.SanitizedTrim(8, 8, 12);
        Assert.Equal(0.2, trim.Length, 9);
        Assert.Equal((0.0, 12.0), (EditTimeline.SanitizedTrim(0, 0, 12).Start, EditTimeline.SanitizedTrim(0, 0, 12).End));
        Assert.Equal(11.8, EditTimeline.SanitizedTrim(12, 0, 12).Start, 9);
        Assert.Equal((0.0, 0.0), (EditTimeline.SanitizedTrim(1, 2, 0).Start, EditTimeline.SanitizedTrim(1, 2, 0).End));
    }

    [Fact]
    public void Export_speed_vectors()
    {
        var timeline = new EditTimeline(20, 2, 12, [new CutRange(4, 6)]);
        Assert.Equal([new TimeRange(2, 4), new TimeRange(6, 12)], timeline.KeptRanges);
        Assert.Equal(8, timeline.OutputDuration, 9);
        Assert.Equal(6.4, ExportSpeed.ExportDuration(timeline.OutputDuration, 1.25), 9);
        var edited = ExportSpeed.EditedTime(2.4, 1.25);
        Assert.Equal(3.0, edited, 9);
        Assert.Equal(7.0, timeline.SourceTime(edited), 9);

        var all = new EditTimeline(10, 0, 10, [new CutRange(0, 10)]);
        Assert.Equal(0, ExportSpeed.ExportDuration(all.OutputDuration, 4));
        Assert.Equal(1.25, ExportSpeed.Rounded(1.2549));
        Assert.Equal(4, ExportSpeed.Sanitize(10));
        Assert.Equal(1, ExportSpeed.Sanitize(double.NaN));
    }

    [Fact]
    public void Seeking_into_a_removed_moment_goes_to_the_nearest_kept_moment()
    {
        var timeline = new EditTimeline(20, 1, 18, [new CutRange(5, 9)]);
        Assert.Equal(0, timeline.NearestOutputTime(0.2), 9);                    // before the trim → start
        Assert.Equal(4, timeline.NearestOutputTime(6), 9);                      // nearer the cut's start → end of the first range
        Assert.Equal(4, timeline.NearestOutputTime(8.5), 9);                    // nearer the next range → its start (same output time)
        Assert.Equal(timeline.OutputDuration, timeline.NearestOutputTime(19.5), 9); // past the trim end → clamped
    }

    [Fact]
    public void Cut_out_needs_room_left()
    {
        var timeline = new EditTimeline(2, 0, 2, []);
        Assert.True(timeline.OutputDurationWith(new CutRange(0, 1.7)) < EditTimeline.MinimumOutput);
        Assert.True(timeline.OutputDurationWith(new CutRange(0, 1.5)) >= EditTimeline.MinimumOutput);
        Assert.Equal(0, EditTimeline.CutIndexAt([new CutRange(3, 4)], 2.9));
        Assert.Equal(-1, EditTimeline.CutIndexAt([new CutRange(3, 4)], 2.8));
    }
}

public class AutoZoomTests
{
    private static PointerClick[] Presses(params double[] times) =>
        times.SelectMany(t => new[] { new PointerClick((float)t, true), new PointerClick((float)t + 0.1f, false) }).ToArray();

    [Fact]
    public void Close_presses_become_one_zoom()
    {
        var zooms = AutoZoom.Generate(Presses(2.0, 2.4, 3.1), null, 20, 1.8);
        var zoom = Assert.Single(zooms);
        Assert.Equal(1.7, zoom.Start, 5);
        Assert.Equal(1.8, zoom.Amount);
        Assert.False(zoom.IsAimed);
    }

    [Fact]
    public void Distant_presses_become_two()
    {
        Assert.Equal(2, AutoZoom.Generate(Presses(2, 12), null, 20, 1.8).Count);
        Assert.Single(AutoZoom.Generate(Presses(2, 7.29), null, 20, 1.8));   // up to 5.3 s apart → one zoom
    }

    [Fact]
    public void The_stopping_click_is_ignored()
    {
        Assert.Empty(AutoZoom.Generate(Presses(9.6), null, 10, 1.8));
    }

    [Fact]
    public void Typing_extends_but_never_creates()
    {
        Assert.True(AutoZoom.Generate(Presses(2), [4, 5, 6], 20, 1.8)[0].End >= 7.4 - 1e-9);
        Assert.True(AutoZoom.Generate(Presses(2), [9, 10], 20, 1.8)[0].End >= 11.4 - 1e-9);
        Assert.Empty(AutoZoom.Generate([], [1, 2, 3], 20, 1.8));
    }

    [Fact]
    public void Every_zoom_ends_before_the_end_margin()
    {
        foreach (var zoom in AutoZoom.Generate(Presses(1, 5, 9.5, 14, 18.5), [15, 16, 17, 18], 20, 2.2))
        {
            Assert.True(zoom.End <= 20 - 0.8 + 1e-9);
            Assert.True(zoom.Length >= 0.9 - 1e-9);
        }
    }
}

public class CameraTests
{
    [Fact]
    public void Ramps_ease_in_and_out_and_owners_win()
    {
        var a = new ZoomSegment { Id = "a", Start = 1, End = 3, Amount = 2 };
        var b = new ZoomSegment { Id = "b", Start = 3, End = 5, Amount = 2.5 };
        Assert.Equal(0, ZoomRamps.StateAt(0.5, [a]).Progress);
        Assert.Equal(0, ZoomRamps.StateAt(1, [a]).Progress, 9);
        Assert.Equal(1, ZoomRamps.StateAt(1.42, [a]).Progress, 9);
        Assert.Equal(1, ZoomRamps.StateAt(2, [a]).Progress);
        Assert.InRange(ZoomRamps.StateAt(3.3, [a]).Progress, 0.01, 0.99);
        Assert.Equal(0, ZoomRamps.StateAt(3.66, [a]).Progress);
        // Touching zooms hand over directly: b owns 3.2.
        Assert.Equal(2.5, ZoomRamps.StateAt(3.2, [a, b]).Amount);
        // A short zoom ramps in over half its length.
        var shortZoom = new ZoomSegment { Id = "s", Start = 0, End = 0.6, Amount = 2 };
        Assert.Equal(1, ZoomRamps.StateAt(0.3, [shortZoom]).Progress, 9);
    }

    [Fact]
    public void Spring_settles_without_much_overshoot()
    {
        var targets = Enumerable.Repeat(1.0, 400).ToArray();
        targets[0] = 0;
        // 400 substeps of 1/240 toward 1 starting from 0 → within 0.02.
        var steps = CameraMotion.Spring(targets.Select((t, i) => i == 0 ? 0.0 : 1.0).ToArray(), 240);
        Assert.InRange(steps[^1], 0.98, 1.02);

        var step = new double[120];
        Array.Fill(step, 1.0, 1, 119);
        var sixty = CameraMotion.Spring(step, 60);
        Assert.True(sixty[^1] > 0.9);
        Assert.True(sixty.Max() <= 1.2);
        // Frame-rate independence: the same moment at 30 fps lands at about the same value.
        var thirty = CameraMotion.Spring(step.Take(60).ToArray(), 30);
        Assert.InRange(Math.Abs(thirty[30] - sixty[60]), 0, 0.02);
    }

    [Fact]
    public void Travel_targets()
    {
        Assert.Equal(0.5, CameraMotion.AimedTravel(0.5, 2), 9);
        Assert.Equal(0, CameraMotion.AimedTravel(0.1, 2), 9);     // flush left
        Assert.Equal(1, CameraMotion.AimedTravel(0.95, 2), 9);    // flush right
        Assert.Equal(0.5, CameraMotion.AimedTravel(0.3, 1), 9);   // no zoom → centred
        Assert.Equal(0, CameraMotion.FollowTravel(0.2), 9);
        Assert.Equal(0.5, CameraMotion.FollowTravel(0.5), 9);
        Assert.Equal(1, CameraMotion.FollowTravel(0.9), 9);
        var (ox, oy, visible) = CameraMotion.Viewport(2, 1, 0);
        Assert.Equal((0.5, 0.0, 0.5), (ox, oy, visible));
    }

    [Fact]
    public void Clusters_group_nearby_points_and_aim_at_their_centre()
    {
        var xs = new float[] { 0.1f, 0.12f, 0.14f, 0.8f, 0.82f };
        var ys = new float[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
        var clusters = FocusClusters.Build(xs, ys, 1, 1.8);
        Assert.Equal(2, clusters.Count);
        Assert.Equal(0.12, clusters.FocusAt(0).X, 5);
        Assert.Equal(0.12, clusters.FocusAt(2.9).X, 5);
        Assert.Equal(0.81, clusters.FocusAt(3).X, 5);
        Assert.Equal((0.5, 0.5), FocusClusters.Empty.FocusAt(1));
    }
}
