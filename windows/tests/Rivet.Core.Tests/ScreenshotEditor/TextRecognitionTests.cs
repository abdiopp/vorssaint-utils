// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Xunit;

namespace Rivet.Core.Tests.ScreenshotEditor;

public class TextBandTests
{
    [Fact]
    public void A_short_image_is_one_band()
    {
        var bands = TextBands.Compute(1920, 1080);
        Assert.Single(bands);
        Assert.Equal(new TextBand(0, 1080, 0, 1080), bands[0]);
    }

    [Fact]
    public void Tall_images_get_overlapping_bands_whose_owned_rows_meet_exactly()
    {
        // W = 3000: tileH = max(512, min(4096, 4000)) = 4000; overlap = even(min(256, 1000)) = 256
        var bands = TextBands.Compute(3000, 10000);
        Assert.Equal(4000, bands[0].Height);
        Assert.Equal(0, bands[0].OwnedStart);
        for (var i = 1; i < bands.Count; i++)
        {
            Assert.Equal(bands[i - 1].Bottom - 256, bands[i].Y);
            Assert.Equal(bands[i - 1].OwnedEnd, bands[i].OwnedStart);
        }

        Assert.Equal(10000, bands[^1].OwnedEnd);
        Assert.Equal(10000, bands[^1].Bottom);
    }

    [Fact]
    public void Wide_images_keep_at_least_512_rows_per_band()
    {
        var bands = TextBands.Compute(40000, 3000);
        Assert.Equal(512, bands[0].Height);
        Assert.Equal(128, bands[0].Bottom - bands[1].Y);   // overlap = even(min(256, 128))
    }

    [Fact]
    public void Band_height_honours_the_recognizer_limit()
    {
        var bands = TextBands.Compute(1000, 9000, maxTileHeight: 2600);
        Assert.All(bands, b => Assert.True(b.Height <= 2600));
    }
}

public class TextMergeTests
{
    private static RecognizedWord Word(string text, double x, double y, int line, double w = 40, double h = 20) =>
        new(text, new ImgRect(x, y, w, h), line);

    [Fact]
    public void Seam_duplicates_are_kept_once_and_line_order_is_preserved()
    {
        // Two bands: [0, 600) owned [0, 550); [500, 1100) owned [550, 1100).
        var upper = new TextBand(0, 600, 0, 550);
        var lower = new TextBand(500, 600, 550, 1100);
        var bands = new[]
        {
            new BandWords(upper, [Word("top", 10, 100, 0), Word("seam", 10, 520, 1), Word("later", 10, 580, 2, h: 18)], true),
            new BandWords(lower, [Word("seam", 11, 521, 3), Word("later", 10, 581, 4), Word("bottom", 10, 900, 5)], true),
        };
        var merged = TextBandMerger.Merge(bands);
        Assert.Equal(["top", "seam", "later", "bottom"], merged.Words.Select(w => w.Text));
        // Runs keep the dropped lower "seam" too: it lies within a quarter of the shared rows of its band.
        Assert.NotNull(merged.Runs);
        Assert.Equal(5, merged.Runs!.Count);
    }

    [Fact]
    public void A_failed_band_makes_the_runs_null()
    {
        var bands = new[]
        {
            new BandWords(new TextBand(0, 600, 0, 550), [Word("a", 0, 0, 0)], true),
            new BandWords(new TextBand(500, 600, 550, 1100), [], false),
        };
        var merged = TextBandMerger.Merge(bands);
        Assert.Null(merged.Runs);
        Assert.Single(merged.Words);
    }

    [Fact]
    public void Runs_join_words_of_a_line_and_pad_them()
    {
        var words = new[]
        {
            Word("Hello", 10, 10, 0, 50, 20),
            Word("world", 70, 12, 0, 50, 20),      // gap 10 ≤ 1.5·h
            Word("far", 400, 10, 0, 30, 20),       // gap too wide: new run
            Word("next", 10, 50, 1, 40, 20),       // other line
        };
        var runs = TextRuns.FromWords(words);
        Assert.Equal(3, runs.Count);
        // First run = union (10,10)-(120,32), h = 22 → padded by (13.2, 6.6).
        Assert.Equal(10 - 13.2, runs[0].X, 9);
        Assert.Equal(10 - 6.6, runs[0].Y, 9);
        Assert.Equal(110 + 26.4, runs[0].Width, 9);
    }

    [Fact]
    public void Text_only_areas_cover_everything_until_runs_are_known()
    {
        var area = new ImgRect(0, 0, 100, 100);
        Assert.Equal([area], TextRuns.Covering(null, area));
        var runs = new[] { new ImgRect(50, 50, 100, 10), new ImgRect(300, 300, 10, 10) };
        Assert.Equal([new ImgRect(50, 50, 50, 10)], TextRuns.Covering(runs, area));
    }

    [Fact]
    public void Crops_shift_words_and_runs_and_drop_what_falls_outside()
    {
        var recognition = new TextRecognition(
            [Word("in", 120, 120, 0), Word("out", 10, 10, 1)],
            [new ImgRect(115, 115, 50, 30), new ImgRect(0, 0, 20, 20)]);
        var cropped = recognition.Cropped(new ImgRect(100, 100, 200, 200));
        Assert.Equal(["in"], cropped.Words.Select(w => w.Text));
        Assert.Equal(new ImgRect(20, 20, 40, 20), cropped.Words[0].Rect);
        Assert.Equal([new ImgRect(15, 15, 50, 30)], cropped.Runs);
        Assert.Null(TextRecognition.Pending.Cropped(new ImgRect(0, 0, 10, 10)).Runs);
    }
}

public class WordSelectionTests
{
    private static readonly RecognizedWord[] Words =
    [
        new("Copy", new ImgRect(0, 0, 40, 20), 0),
        new("this", new ImgRect(50, 0, 40, 20), 0),
        new("line", new ImgRect(0, 30, 40, 20), 1),
    ];

    [Fact]
    public void A_hairline_drag_still_selects_and_copy_joins_lines_with_newlines()
    {
        var selected = WordSelection.Select(Words, new ImgPoint(10, 10), new ImgPoint(10, 40));
        Assert.Equal([0, 2], selected);
        Assert.Equal("Copy\nline", WordSelection.JoinedText(Words, selected));
        Assert.Equal("Copy this\nline", WordSelection.JoinedText(Words, [2, 1, 0]));
    }

    [Fact]
    public void Word_hits_grow_by_two_points()
    {
        Assert.Equal(1, WordSelection.WordAt(Words, new ImgPoint(91, 10), 1));
        Assert.Null(WordSelection.WordAt(Words, new ImgPoint(93, 10), 1));
    }
}

public class RecognitionRunnerTests
{
    [Fact]
    public async Task Recognizes_each_band_and_maps_boxes_into_image_coordinates()
    {
        var recognizer = new BandRecognizer();
        var runner = new TextRecognitionRunner(recognizer);
        var image = new PixelBuffer(100, 9000);
        var result = await runner.RecognizeAsync(image, CancellationToken.None);
        Assert.True(recognizer.Calls > 1);
        Assert.NotNull(result.Runs);
        Assert.All(result.Words, w => Assert.True(w.Rect!.Value.Y >= 0 && w.Rect.Value.MaxY <= 9000));
        // One word per band at band-relative y = 10 → distinct absolute positions, in order.
        var ys = result.Words.Select(w => w.Rect!.Value.Y).ToList();
        Assert.Equal(ys.OrderBy(y => y), ys);
    }

    [Fact]
    public async Task An_unavailable_recognizer_leaves_runs_null()
    {
        var runner = new TextRecognitionRunner(new BandRecognizer { Available = false });
        var result = await runner.RecognizeAsync(new PixelBuffer(10, 10), CancellationToken.None);
        Assert.Null(result.Runs);
    }

    private sealed class BandRecognizer : ITextRecognizer
    {
        public bool Available { get; init; } = true;

        public int Calls { get; private set; }

        public bool IsAvailable => Available;

        public int? MaxImageDimension => 4096;

        public Task<OcrPage?> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
        {
            Calls++;
            var page = new OcrPage([new OcrLine([new OcrWord($"band{Calls}", new ImgRect(5, 10, 30, 12))])]);
            return Task.FromResult<OcrPage?>(page);
        }
    }
}
