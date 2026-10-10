// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Xunit;

namespace Rivet.Core.Tests.RecordingEditor;

public class PointerPathTests
{
    [Fact]
    public void One_pole_alpha_vector()
    {
        // The spec quotes 0.18899; the formula it gives evaluates to 0.188961 (same to 1e-4).
        Assert.InRange(PointerPath.OnePoleAlpha(2, 1 / 60.0), 0.18889, 0.18909);
        // Widened for the forward+backward passes it is the 0.28 of the step vector.
        Assert.Equal(0.28, PointerPath.OnePoleAlpha(2 * PointerPath.FiltFiltWidening, 1 / 60.0), 2);
    }

    [Fact]
    public void Zero_phase_step_has_no_lag_and_no_overshoot()
    {
        var values = new float[200];
        for (var i = 100; i < 200; i++)
        {
            values[i] = 1;
        }

        PointerPath.ZeroPhase(values, 0.28);
        Assert.InRange(values[99], 0.3, 0.5);
        Assert.InRange(values[100], 0.5, 0.7);
        Assert.True((values[99] + values[100]) / 2 is > 0.45f and < 0.55f, "crosses 0.5 at the step");
        Assert.All(values, v => Assert.InRange(v, -1e-6f, 1 + 1e-6f));
        Assert.Equal(0, values[0], 4);
        Assert.Equal(1, values[^1], 4);
    }

    [Fact]
    public void Resampling_interpolates_positions_and_steps_shapes()
    {
        PointerSample[] samples =
        [
            new(0, 0, 0, 0, true),
            new(1, 1, 0.5f, 1, false),
        ];
        var (x, y, shape, visible) = PointerPath.Resample(samples, 5, 4);
        Assert.Equal([0f, 0.25f, 0.5f, 0.75f, 1f], x);
        Assert.Equal(0.25f, y[2]);
        Assert.Equal([0, 0, 0, 0, 1], shape.Select(s => (int)s));
        Assert.Equal([true, true, true, true, false], visible);

        var (ex, _, _, ev) = PointerPath.Resample([], 3, 30);
        Assert.All(ex, v => Assert.Equal(0, v));
        Assert.All(ev, Assert.True);
    }

    [Fact]
    public void Anchoring_weight_vectors()
    {
        var clicks = new ClickTimeline([new PointerClick(1.0f, true), new PointerClick(1.3f, false)]);
        Assert.Equal(1, clicks.AnchorWeight(1.0));
        Assert.Equal(1, clicks.AnchorWeight(1.15));
        Assert.Equal(0, clicks.AnchorWeight(0.5));
        Assert.InRange(clicks.AnchorWeight(0.94), 0.0001, 0.9999);
        // An unreleased last press anchors to the end.
        Assert.Equal(1, new ClickTimeline([new PointerClick(2, true)]).AnchorWeight(50));
    }

    [Fact]
    public void Punch_and_ring_vectors()
    {
        var clicks = new ClickTimeline([new PointerClick(1.0f, true), new PointerClick(1.2f, false)]);
        Assert.Equal(0.8, clicks.PressScale(1.0), 9);
        Assert.Equal(1, clicks.PressScale(5), 9);
        Assert.InRange(clicks.PressScale(0.93), 0.8, 1);
        Assert.Equal(0, clicks.RingProgress(1.0)!.Value, 9);
        Assert.Null(clicks.RingProgress(2.0));
        // A burst restarts one ring rather than stacking.
        var burst = new ClickTimeline([new PointerClick(1, true), new PointerClick(1.05f, false), new PointerClick(1.2f, true), new PointerClick(1.25f, false)]);
        Assert.Equal(0.25, burst.RingProgress(1.3)!.Value, 5);
    }

    [Fact]
    public void Carried_cursor_matches_the_full_scan_bit_for_bit()
    {
        var random = new Random(7);
        var clicks = new List<PointerClick>();
        var t = 0.2;
        while (t < 90)
        {
            clicks.Add(new PointerClick((float)t, true));
            t += random.NextDouble() * 0.6;
            if (random.NextDouble() < 0.9)
            {
                clicks.Add(new PointerClick((float)t, false));
            }

            t += random.NextDouble() * 1.5;
        }

        var timeline = new ClickTimeline(clicks);
        var cursor = timeline.CreateCursor();
        for (var i = 0; i < 5400; i++)
        {
            var time = i / 60.0;
            Assert.Equal(timeline.PressScale(time), cursor.PressScale(time));
            Assert.Equal(timeline.RingProgress(time), cursor.RingProgress(time));
            Assert.Equal(timeline.AnchorWeight(time), cursor.AnchorWeight(time));
        }
    }

    [Fact]
    public void Idle_fade_vectors()
    {
        // 60 fps, parked 6 s then moving.
        var n = 600;
        var x = new float[n];
        var y = new float[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = i <= 360 ? 0.5f : 0.5f + ((i - 360) * 0.002f);
            y[i] = 0.5f;
        }

        var opacity = PointerPath.IdleOpacity(x, y, 60);
        Assert.True(opacity[10] > 0.95);
        Assert.True(opacity[300] < 0.05);
        Assert.True(opacity[360] > 0.95);

        var moving = Enumerable.Range(0, n).Select(i => i * 0.001f).ToArray();
        Assert.All(PointerPath.IdleOpacity(moving, y, 60), o => Assert.True(o > 0.95));
    }

    [Fact]
    public void Shaking_is_detected_on_the_raw_path()
    {
        var n = 120;
        var x = new float[n];
        var y = new float[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = i is >= 40 and < 80 ? 0.5f + (i % 2 == 0 ? 0.03f : -0.03f) : 0.5f;
            y[i] = 0.5f;
        }

        var shaking = PointerPath.DetectShake(x, y, 60);
        Assert.True(shaking[60]);
        Assert.False(shaking[10]);
        Assert.False(shaking[110]);
    }

    [Fact]
    public void Smoothed_path_is_anchored_at_clicks()
    {
        var samples = new List<PointerSample>();
        for (var i = 0; i <= 500; i++)
        {
            var time = i / 125f;
            // A sharp corner at t = 2 s, where the click happens.
            var px = time < 2 ? time / 4 : 0.5f;
            var py = time < 2 ? 0.5f : 0.5f + ((time - 2) / 4);
            samples.Add(new PointerSample(time, px, py, 0, true));
        }

        var track = new PointerTrack { Samples = samples, Clicks = [new PointerClick(2, true), new PointerClick(2.1f, false)] };
        var path = PointerPath.Build(track, 4, 60, PointerSmoothing.Cinematic, drawn: true);
        var raw = PointerPath.Build(track, 4, 60, PointerSmoothing.Off, drawn: true);
        var i2 = path.IndexOf(2.05);
        Assert.Equal(raw.X[i2], path.X[i2], 5);
        Assert.Equal(raw.Y[i2], path.Y[i2], 5);
        // Away from the click (more than 0.12 s before it) the corner is rounded off.
        var i1 = path.IndexOf(1.86);
        Assert.True(Math.Abs(raw.Y[i1] - path.Y[i1]) > 1e-4);
    }
}
