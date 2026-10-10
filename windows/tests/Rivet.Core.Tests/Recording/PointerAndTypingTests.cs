// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Recording.Engine.Pointer;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.Core.Tests.Recording;

public class PointerRecorderTests
{
    [Fact]
    public void Samples_are_normalized_to_the_region_and_timed_by_the_pause_clock()
    {
        var host = new ManualClock(500);
        var clock = new PauseClock();
        clock.Begin(500);
        var cursor = new TestCursor();
        var hooks = new TestHooks();
        var recorder = new PointerRecorder(cursor, new FixedPointerRegion(new PixelRect(100, 100, 200, 100)), clock, host, new TestSystem(), hooks, 1.5);
        recorder.Start(startThread: false);

        cursor.Reading = new CursorReading(new PixelPoint(200, 150), true, 1);
        host.Advance(0.008);
        recorder.SampleOnce();
        cursor.Reading = new CursorReading(new PixelPoint(50, 300), false, 1);
        host.Advance(0.008);
        recorder.SampleOnce();

        var track = recorder.Stop();
        Assert.Equal(2, track.Samples.Count);
        Assert.Equal(0.5f, track.Samples[0].X, 5);
        Assert.Equal(0.5f, track.Samples[0].Y, 5);
        Assert.Equal(0.008f, track.Samples[0].Time, 5);
        Assert.Equal(-0.25f, track.Samples[1].X, 5); // outside the region is allowed
        Assert.Equal(2f, track.Samples[1].Y, 5);
        Assert.False(track.Samples[1].Visible);
        Assert.Equal(1.5f, track.DisplayScale);
        Assert.Equal(1f, track.SystemScale);
        Assert.Equal(0, hooks.MouseSubscribers);
    }

    [Fact]
    public void Shapes_are_read_on_handle_changes_and_deduplicated_by_picture()
    {
        var host = new ManualClock();
        var clock = new PauseClock();
        clock.Begin(host.Now);
        var cursor = new TestCursor();
        var recorder = new PointerRecorder(cursor, new FixedPointerRegion(new PixelRect(0, 0, 100, 100)), clock, host, new TestSystem(), new TestHooks(), 1);
        recorder.Start(startThread: false);

        foreach (var handle in new nint[] { 1, 1, 2, 2, 3, 1, 99 })
        {
            cursor.Reading = cursor.Reading with { Handle = handle };
            host.Advance(0.008);
            recorder.SampleOnce();
        }

        var track = recorder.Stop();
        Assert.Equal(2, track.Shapes.Count); // pictures A and B; handle 3 shows A again
        Assert.Equal([0, 0, 1, 1, 0, 0, 0], track.Samples.Select(s => (int)s.ShapeIndex));
        Assert.Equal(5, cursor.Captures); // only on changes: 1, 2, 3, 1, 99
    }

    [Fact]
    public void Clicks_and_samples_inside_a_pause_are_dropped()
    {
        var host = new ManualClock(0);
        var clock = new PauseClock();
        clock.Begin(0);
        var hooks = new TestHooks();
        var recorder = new PointerRecorder(new TestCursor(), new FixedPointerRegion(new PixelRect(0, 0, 10, 10)), clock, host, new TestSystem(), hooks, 1);
        recorder.Start(startThread: false);

        host.Now = 1;
        hooks.Mouse(MouseHookKind.LeftDown);
        host.Now = 1.1;
        hooks.Mouse(MouseHookKind.LeftUp);
        hooks.Mouse(MouseHookKind.MiddleDown); // not a click for the track
        recorder.SampleOnce();
        clock.Pause(2);
        host.Now = 2.5;
        hooks.Mouse(MouseHookKind.RightDown);
        recorder.SampleOnce();
        clock.Resume(3);
        host.Now = 4;
        hooks.Mouse(MouseHookKind.RightUp);
        recorder.SampleOnce();

        var track = recorder.Stop();
        Assert.Equal([(1f, true), (1.1f, false), (3f, false)], track.Clicks.Select(c => (MathF.Round(c.Time, 3), c.IsDown)));
        Assert.Equal([1.1f, 3f], track.Samples.Select(s => MathF.Round(s.Time, 3)));
    }

    [Fact]
    public void Window_recordings_follow_the_window_and_pick_the_rectangle_matching_the_capture()
    {
        var system = new TestSystem { Window = new WindowRects(new PixelRect(93, 93, 1214, 814), new PixelRect(100, 100, 1200, 800)) };
        var region = new WindowPointerRegion(system, 42, () => (1200, 800), 1200, 800, new PixelRect(100, 100, 1200, 800));
        Assert.Equal((0.5, 0.5), region.Normalize(new PixelPoint(700, 500)));

        system.Window = new WindowRects(new PixelRect(-7, -7, 1214, 814), new PixelRect(0, 0, 1200, 800));
        Assert.Equal((0.5, 0.5), region.Normalize(new PixelPoint(600, 400)));

        // Windows 10 captures can include the invisible borders: then the full window rectangle matches.
        Assert.Equal(new PixelRect(93, 93, 1214, 814), WindowPointerRegion.Choose(new WindowRects(new PixelRect(93, 93, 1214, 814), new PixelRect(100, 100, 1200, 800)), (1214, 814)));
    }

    [Fact]
    public void The_sampler_thread_runs_at_about_125_hz()
    {
        var clock = new PauseClock();
        clock.Begin(QpcClock.Instance.Now);
        var recorder = new PointerRecorder(new TestCursor(), new FixedPointerRegion(new PixelRect(0, 0, 10, 10)), clock, QpcClock.Instance, new TestSystem(), new TestHooks(), 1);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        recorder.Start();
        Thread.Sleep(400);
        var track = recorder.Stop();
        elapsed.Stop();

        // Judge the rate over the time that really passed: a busy CI machine can oversleep a lot.
        var hz = track.Samples.Count / elapsed.Elapsed.TotalSeconds;
        Assert.True(track.Samples.Count >= 10, $"only {track.Samples.Count} samples in {elapsed.Elapsed.TotalMilliseconds:0} ms");
        Assert.InRange(hz, 25, 160);
        Assert.True(track.Samples.Zip(track.Samples.Skip(1)).All(p => p.Second.Time > p.First.Time));
    }
}

public class CursorCatalogTests
{
    [Fact]
    public void Keeps_first_appearance_order_and_deduplicates()
    {
        var catalog = new CursorCatalog();
        Assert.Equal((ushort)0, catalog.Add(new CursorShapeSnapshot(7, 32, 32, 2, 3, [1])));
        Assert.Equal((ushort)1, catalog.Add(new CursorShapeSnapshot(9, 48, 48, 24, 24, [2])));
        Assert.Equal((ushort)0, catalog.Add(new CursorShapeSnapshot(7, 32, 32, 2, 3, [1])));
        Assert.Null(catalog.Add(new CursorShapeSnapshot(11, 0, 32, 0, 0, [3]))); // unusable
        var shapes = catalog.ToPointerShapes();
        Assert.Equal(2, shapes.Count);
        Assert.Equal(48, shapes[1].Width);
    }

    [Fact]
    public void Hot_spots_are_clamped_inside_the_picture()
    {
        var catalog = new CursorCatalog();
        catalog.Add(new CursorShapeSnapshot(1, 32, 32, 40, -3, [1]));
        var shape = catalog.ToPointerShapes()[0];
        Assert.Equal(32, shape.HotX);
        Assert.Equal(0, shape.HotY);
    }
}

public class TypingRecorderTests
{
    [Fact]
    public void Records_times_only_once_per_press_and_ignores_modifiers()
    {
        var host = new ManualClock(0);
        var clock = new PauseClock();
        clock.Begin(0);
        var hooks = new TestHooks();
        var typing = new TypingRecorder(hooks, clock, host);
        typing.Start();

        host.Now = 1;
        hooks.Key(VirtualKeys.A, KeyAction.Down);
        host.Now = 1.5;
        hooks.Key(VirtualKeys.A, KeyAction.Down); // auto-repeat
        hooks.Key(VirtualKeys.A, KeyAction.Up);
        host.Now = 2;
        hooks.Key(VirtualKeys.LShift, KeyAction.Down); // modifier alone
        hooks.Key(VirtualKeys.Capital, KeyAction.Down);
        hooks.Key(VirtualKeys.Letter('b'), KeyAction.Down);
        clock.Pause(3);
        host.Now = 3.5;
        hooks.Key(VirtualKeys.Letter('c'), KeyAction.Down); // in a pause
        clock.Resume(4);
        host.Now = 5;
        hooks.Key(VirtualKeys.A, KeyAction.Down);

        var times = typing.Stop();
        Assert.Equal([1.0, 2.0, 4.0], times);
        Assert.Equal(0, hooks.KeyboardSubscribers);
    }
}
