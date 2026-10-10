// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Imaging.Capture;
using Xunit;

namespace Rivet.Imaging.Tests.Capture;

/// <summary>Scrolling capture on synthetic pages (spec 01 §6.16, Appendix B "Scrolling").</summary>
public class ScrollStitchingTests
{
    /// <summary>A tall page of text-like blocks: every 6-row band has its own pattern across 8 px columns.</summary>
    internal static PixelBuffer Page(int width, int height, int seed = 7)
    {
        var page = new PixelBuffer(width, height);
        var random = new Random(seed);
        var band = new byte[(width / 8) + 1];
        for (var y = 0; y < height; y++)
        {
            if (y % 6 == 0)
            {
                for (var i = 0; i < band.Length; i++)
                {
                    band[i] = (byte)random.Next(30, 230);
                }
            }

            for (var x = 0; x < width; x++)
            {
                var v = (byte)Math.Clamp(band[x / 8] + ((x * 7 + y * 3) % 11) - 5, 0, 255);
                Set(page, x, y, v, v, (byte)(255 - v));
            }
        }

        return page;
    }

    /// <summary>The viewport showing page rows [offset, offset + height).</summary>
    internal static PixelBuffer Frame(PixelBuffer page, int offset, int height) =>
        CaptureImaging.Crop(page, new PixelRect(0, offset, page.Width, height))!;

    [Fact]
    public void Forward_scroll_finds_the_exact_overlap()
    {
        var page = Page(400, 1600);
        var a = ScrollSample.From(Frame(page, 0, 600))!;
        var b = ScrollSample.From(Frame(page, 137, 600))!;
        var t = ScrollMatcher.Transition(a, b);
        Assert.Equal(ScrollTransitionKind.Advanced, t.Kind);
        Assert.True(t.Forward);
        Assert.Equal(600 - 137, t.Overlap);
    }

    [Fact]
    public void Backward_scroll_is_recognized()
    {
        var page = Page(400, 1600);
        var a = ScrollSample.From(Frame(page, 300, 600))!;
        var b = ScrollSample.From(Frame(page, 210, 600))!;
        var t = ScrollMatcher.Transition(a, b);
        Assert.Equal(ScrollTransitionKind.Advanced, t.Kind);
        Assert.False(t.Forward);
        Assert.Equal(600 - 90, t.Overlap);
    }

    [Fact]
    public void Identical_frames_are_stable()
    {
        var page = Page(300, 900);
        var a = ScrollSample.From(Frame(page, 100, 500))!;
        var b = ScrollSample.From(Frame(page, 100, 500))!;
        Assert.Equal(ScrollTransitionKind.End, ScrollMatcher.Transition(a, b).Kind);
    }

    [Fact]
    public void Blank_bands_are_ambiguous_and_never_invent_a_seam()
    {
        // Text only in the first 30 rows, blank below: every offset matches the
        // compared (blank) rows equally well, so no seam may be chosen.
        var text = Page(300, 30, seed: 3);
        var page = new PixelBuffer(300, 1200);
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < 300; x++)
            {
                Set(page, x, y, 245, 245, 245);
            }
        }

        for (var y = 0; y < 30; y++)
        {
            Buffer.BlockCopy(text.Pixels, y * text.Stride, page.Pixels, y * page.Stride, 300 * 4);
        }

        var a = ScrollSample.From(Frame(page, 0, 500))!;
        var b = ScrollSample.From(Frame(page, 25, 500))!;
        Assert.Equal(ScrollTransitionKind.Unmatched, ScrollMatcher.Transition(a, b).Kind);
    }

    [Fact]
    public void Periodic_content_takes_the_smallest_consistent_offset()
    {
        // A page that repeats every 60 rows: the shortest shift explaining the change wins.
        var tile = Page(300, 60, seed: 3);
        var page = new PixelBuffer(300, 1200);
        for (var y = 0; y < page.Height; y++)
        {
            Buffer.BlockCopy(tile.Pixels, (y % 60) * tile.Stride, page.Pixels, y * page.Stride, 300 * 4);
        }

        var a = ScrollSample.From(Frame(page, 0, 500))!;
        var b = ScrollSample.From(Frame(page, 25, 500))!;
        var t = ScrollMatcher.Transition(a, b);
        Assert.Equal(ScrollTransitionKind.Advanced, t.Kind);
        Assert.Equal(500 - 25, t.Overlap);
    }

    [Fact]
    public void Unrelated_content_is_unmatched()
    {
        var a = ScrollSample.From(Frame(Page(300, 600, seed: 1), 0, 500))!;
        var b = ScrollSample.From(Frame(Page(300, 600, seed: 2), 0, 500))!;
        Assert.Equal(ScrollTransitionKind.Unmatched, ScrollMatcher.Transition(a, b).Kind);
    }

    [Fact]
    public void Stitching_reproduces_the_page_exactly()
    {
        var page = Page(320, 2000);
        var stitcher = ScrollStitcher.Start(Frame(page, 0, 500))!;
        foreach (var offset in new[] { 120, 260, 400, 560 })
        {
            Assert.Equal(ScrollFeedKind.Appended, stitcher.Feed(Frame(page, offset, 500)));
        }

        var result = stitcher.Stitch();
        Assert.Equal(560 + 500, result.Height);
        AssertRowsEqual(page, 0, result, 0, result.Height, 0, result.Width);
    }

    [Fact]
    public void Back_and_forth_never_duplicates()
    {
        var page = Page(320, 1500);
        var stitcher = ScrollStitcher.Start(Frame(page, 0, 400))!;
        Assert.Equal(ScrollFeedKind.Appended, stitcher.Feed(Frame(page, 100, 400)));
        Assert.Equal(ScrollFeedKind.Backward, stitcher.Feed(Frame(page, 50, 400)));
        Assert.Equal(ScrollFeedKind.Appended, stitcher.Feed(Frame(page, 180, 400)));
        var result = stitcher.Stitch();
        Assert.Equal(180 + 400, result.Height);
        AssertRowsEqual(page, 0, result, 0, result.Height, 0, result.Width);
    }

    [Fact]
    public void Fixed_footer_is_kept_once_at_the_end()
    {
        var page = Page(320, 1500);
        var footer = Page(320, 40, seed: 99);
        PixelBuffer WithFooter(int offset)
        {
            var frame = Frame(page, offset, 400);
            for (var y = 0; y < 40; y++)
            {
                Buffer.BlockCopy(footer.Pixels, y * footer.Stride, frame.Pixels, (360 + y) * frame.Stride, 320 * 4);
            }

            return frame;
        }

        var stitcher = ScrollStitcher.Start(WithFooter(0))!;
        Assert.Equal(ScrollFeedKind.Appended, stitcher.Feed(WithFooter(90)));
        Assert.Equal(ScrollFeedKind.Appended, stitcher.Feed(WithFooter(200)));
        var result = stitcher.Stitch();

        // Content rows 0 ..< 200 + 360, then the 40 footer rows once.
        Assert.Equal(200 + 360 + 40, result.Height);
        AssertRowsEqual(page, 0, result, 0, 560, 0, result.Width);
        AssertRowsEqual(footer, 0, result, 560, 40, 0, result.Width);
    }

    [Fact]
    public void Fixed_side_column_is_cut_off()
    {
        var page = Page(400, 1500);
        var sidebar = Page(80, 400, seed: 42);
        PixelBuffer WithSidebar(int offset)
        {
            var frame = Frame(page, offset, 400);
            for (var y = 0; y < 400; y++)
            {
                Buffer.BlockCopy(sidebar.Pixels, y * sidebar.Stride, frame.Pixels, y * frame.Stride, 80 * 4);
            }

            return frame;
        }

        var stitcher = ScrollStitcher.Start(WithSidebar(0))!;
        Assert.Equal(ScrollFeedKind.Appended, stitcher.Feed(WithSidebar(110)));
        var result = stitcher.Stitch();
        Assert.True(result.Width <= 400 - 64, $"sidebar kept: width {result.Width}");
        Assert.Equal(110 + 400, result.Height);

        // The kept columns are the page's own columns.
        var left = 400 - result.Width;
        AssertRowsEqual(page, 0, result, 0, result.Height, left, result.Width);
    }

    [Fact]
    public void Transparent_padding_never_becomes_a_seam()
    {
        var page = Page(300, 1200);
        PixelBuffer Padded(int offset)
        {
            var inner = Frame(page, offset, 400);
            var frame = new PixelBuffer(300, 440);
            for (var y = 0; y < 400; y++)
            {
                Buffer.BlockCopy(inner.Pixels, y * inner.Stride, frame.Pixels, (20 + y) * frame.Stride, 300 * 4);
            }

            return frame; // 20 transparent rows above and below
        }

        var stitcher = ScrollStitcher.Start(Padded(0))!;
        Assert.Equal(ScrollFeedKind.Appended, stitcher.Feed(Padded(150)));
        var result = stitcher.Stitch();
        Assert.Equal(150 + 400, result.Height);
        AssertRowsEqual(page, 0, result, 0, result.Height, 0, result.Width);
    }

    [Fact]
    public void Exact_overlap_at_high_dpi_heights_stays_fast()
    {
        var page = Page(1200, 4000);
        var a = ScrollSample.From(Frame(page, 0, 1340))!;
        var b = ScrollSample.From(Frame(page, 233, 1340))!;
        ScrollMatcher.Transition(a, b); // warm up the JIT

        // Guards against an algorithmic regression (about 150 ms on a laptop), not against a busy
        // CI machine: the best of three runs filters out scheduling noise from parallel test runs.
        var best = TimeSpan.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var watch = Stopwatch.StartNew();
            var t = ScrollMatcher.Transition(a, b);
            watch.Stop();
            Assert.Equal(1340 - 233, t.Overlap);
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        Assert.True(best < TimeSpan.FromSeconds(2), $"best of three took {best.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task Engine_completes_without_input_and_returns_the_first_frame_when_nothing_scrolled()
    {
        var page = Page(300, 800);
        var clock = TimeSpan.Zero;
        ScrollCaptureEngine? engine = null;

        // The fake clock only moves inside the engine's own waits, so finish from there once a
        // second has passed: a separate polling task can lose the race on a slow machine and let
        // the engine run into its two-minute limit in fake time.
        engine = new ScrollCaptureEngine(() => Frame(page, 0, 400), () => clock, (wait, _) =>
        {
            clock += wait;
            if (clock >= TimeSpan.FromSeconds(1))
            {
                engine!.RequestFinish();
            }

            return Task.CompletedTask;
        });
        var result = await engine.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ScrollCaptureOutcome.Success, result.Outcome);
        Assert.Equal(400, result.Image!.Height);
    }

    [Fact]
    public async Task Engine_stitches_frames_as_they_scroll()
    {
        var page = Page(300, 2000);
        var clock = TimeSpan.Zero;
        var offsets = new Queue<int>([0, 0, 90, 180, 270, 270, 270, 270, 270]);
        var current = 0;
        var engine = new ScrollCaptureEngine(
            () =>
            {
                if (offsets.Count > 0)
                {
                    current = offsets.Dequeue();
                }

                return Frame(page, current, 400);
            },
            () => clock,
            (wait, _) =>
            {
                clock += wait;
                return Task.CompletedTask;
            });
        var heights = new List<int>();
        engine.Progress += heights.Add;
        engine.FrameProcessed += _ =>
        {
            if (offsets.Count == 0)
            {
                engine.RequestFinish();
            }
        };
        var result = await engine.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ScrollCaptureOutcome.Success, result.Outcome);
        Assert.Equal(270 + 400, result.Image!.Height);
        Assert.Equal(670, heights[^1]);
    }

    [Fact]
    public async Task Cancel_never_delivers()
    {
        var page = Page(300, 800);
        using var cts = new CancellationTokenSource();
        var engine = new ScrollCaptureEngine(() => Frame(page, 0, 400), delay: async (wait, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
        });
        var result = await engine.RunAsync(cts.Token);
        Assert.Equal(ScrollCaptureOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Image);
    }

    private static void Set(PixelBuffer image, int x, int y, byte r, byte g, byte b)
    {
        var i = (y * image.Stride) + (x * 4);
        image.Pixels[i] = b;
        image.Pixels[i + 1] = g;
        image.Pixels[i + 2] = r;
        image.Pixels[i + 3] = 255;
    }

    private static void AssertRowsEqual(PixelBuffer expected, int expectedY, PixelBuffer actual, int actualY, int rows, int expectedX, int width)
    {
        for (var y = 0; y < rows; y++)
        {
            var e = expected.Pixels.AsSpan(((expectedY + y) * expected.Stride) + (expectedX * 4), width * 4);
            var a = actual.Pixels.AsSpan((actualY + y) * actual.Stride, width * 4);
            Assert.True(e.SequenceEqual(a), $"row {y} differs");
        }
    }
}
