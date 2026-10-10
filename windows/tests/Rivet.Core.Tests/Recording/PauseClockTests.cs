// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording.Engine;
using Xunit;

namespace Rivet.Core.Tests.Recording;

public class PauseClockTests
{
    [Fact]
    public void Spec_vectors()
    {
        var clock = new PauseClock();
        Assert.True(clock.Begin(0));
        Assert.True(clock.Pause(3));
        Assert.Equal(3, clock.Elapsed(7), 9);
        Assert.Null(clock.SampleTime(5, 0));

        Assert.True(clock.Resume(8));
        Assert.Equal(2, clock.SampleTime(2, 0)!.Value, 9);
        Assert.Equal(3, clock.SampleTime(8, 0)!.Value, 9);
        Assert.Equal(5, clock.EventTime(10)!.Value, 9);
        Assert.Null(clock.SampleTime(2.99, 0.02));
        Assert.Null(clock.SampleTime(-0.01, 0));

        Assert.True(clock.Pause(12));
        Assert.True(clock.Resume(14));
        Assert.Null(clock.EventTime(13));
        Assert.Equal(9, clock.Elapsed(16), 9);
    }

    [Fact]
    public void A_later_begin_cannot_move_the_origin()
    {
        var clock = new PauseClock();
        Assert.True(clock.Begin(100));
        Assert.False(clock.Begin(50));
        Assert.Equal(100, clock.Origin);
        Assert.Equal(5, clock.Elapsed(105), 9);
        Assert.Equal(0, clock.Elapsed(90), 9);
    }

    [Fact]
    public void Pause_and_resume_are_accepted_only_in_order()
    {
        var clock = new PauseClock();
        Assert.False(clock.Pause(1)); // no origin yet
        Assert.False(clock.Begin(double.NaN));
        clock.Begin(0);
        Assert.False(clock.Resume(1)); // not paused
        Assert.True(clock.Pause(2));
        Assert.False(clock.Pause(3)); // already paused
        Assert.True(clock.IsPaused);
        Assert.True(clock.Resume(4));
        Assert.False(clock.IsPaused);
    }

    [Fact]
    public void Elapsed_is_frozen_while_paused()
    {
        var clock = new PauseClock();
        clock.Begin(10);
        clock.Pause(15);
        Assert.Equal(5, clock.Elapsed(15), 9);
        Assert.Equal(5, clock.Elapsed(60), 9);
        clock.Resume(60);
        Assert.Equal(6, clock.Elapsed(61), 9);
    }

    [Fact]
    public void Events_inside_an_open_pause_are_dropped_and_samples_straddling_the_edge_too()
    {
        var clock = new PauseClock();
        clock.Begin(0);
        clock.Pause(5);
        Assert.Null(clock.EventTime(5));
        Assert.Null(clock.EventTime(6));
        Assert.Null(clock.SampleTime(4.995, 0.01));
        Assert.Equal(4.98, clock.SampleTime(4.98, 0.01)!.Value, 9);
    }

    [Fact]
    public void Is_safe_to_use_from_many_threads()
    {
        var clock = new PauseClock();
        clock.Begin(0);
        Parallel.For(0, 10_000, i =>
        {
            if (i % 1000 == 0)
            {
                clock.Pause(i);
                clock.Resume(i + 0.5);
            }

            _ = clock.SampleTime(i, 0.01);
        });
        Assert.True(clock.Elapsed(20_000) < 20_000);
    }
}
