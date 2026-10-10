// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Backdrop;
using Rivet.Imaging.ScreenshotEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.ScreenshotEditor;

public class BlurSampleTests
{
    [Fact]
    public void Each_level_coarsens_and_level_three_is_the_legacy_strength()
    {
        // min(W, H) = 1100 → legacy block = max(10, floor(1100 / 55)) = 20
        Assert.Equal(20, BlurSampleMath.BlockSize(1600, 1100, 3));
        var blocks = Enumerable.Range(1, 5).Select(l => BlurSampleMath.BlockSize(1600, 1100, l)).ToList();
        Assert.Equal([8, 13, 20, 30, 44], blocks);
        Assert.Equal(blocks.Order(), blocks);
        // Small captures keep a 10 px base and a 2 px floor.
        Assert.Equal(4, BlurSampleMath.BlockSize(100, 100, 1));
        Assert.Equal((80, 55), BlurSampleMath.PixelateSampleSize(1600, 1100, 20));
        Assert.Equal(5, BlurSampleMath.SoftStep(20));
        Assert.Equal(3.2, BlurSampleMath.SoftRadius(20, 5), 9);
    }

    [Fact]
    public void Noise_stays_within_nine_levels_and_alpha()
    {
        using var flat = EditorSampleImages.Flat(330, 220, new SKColor(120, 130, 140));
        var image = new SkiaEditorImage(flat);
        using var cache = new BlurSampleCache(image, noiseSeed: 7);
        var sample = cache.Get(BlurStyle.Pixelate, 3);
        using var bitmap = SKBitmap.FromImage(sample);
        var any = false;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                Assert.InRange(p.Red, 111, 129);
                Assert.InRange(p.Green, 121, 139);
                Assert.InRange(p.Blue, 131, 149);
                Assert.Equal(255, p.Alpha);
                any |= p.Red != 120;
            }
        }

        Assert.True(any, "noise was applied");
    }

    [Fact]
    public void Only_levels_used_in_a_frame_keep_samples()
    {
        using var window = EditorSampleImages.Window(400, 300);
        var image = new SkiaEditorImage(window);
        using var cache = new BlurSampleCache(image);
        cache.BeginFrame();
        cache.Get(BlurStyle.Pixelate, 2);
        cache.Get(BlurStyle.Blur, 4);
        cache.EndFrame();
        Assert.Equal(2, cache.Count);
        cache.BeginFrame();
        cache.Get(BlurStyle.Blur, 4);
        cache.EndFrame();
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Text_only_areas_cover_the_whole_area_before_recognition_and_only_runs_after()
    {
        using var window = EditorSampleImages.Window(500, 260);
        var image = new SkiaEditorImage(window);
        var area = new ImgRect(20, 50, 300, 100);
        var blur = new Annotation { Kind = AnnotationKind.Blur, Rect = area, TextOnly = true, BlurLevel = 3 };
        var pending = Render(image, [blur], runs: null);
        var known = Render(image, [blur], runs: [new ImgRect(20, 50, 300, 30)]);

        // A pixel inside the area but outside the run: covered while pending, original once runs are known.
        var original = Pixel(window, 200, 130);
        Assert.NotEqual(original, Pixel(pending, 200, 130));
        Assert.Equal(original, Pixel(known, 200, 130));
        // Inside the run: covered in both.
        Assert.NotEqual(Pixel(window, 30, 60), Pixel(known, 30, 60));
        pending.Dispose();
        known.Dispose();
    }

    [Fact]
    public void Translucent_captures_never_show_glyphs_through_a_blur()
    {
        using var window = EditorSampleImages.Window(400, 240, translucent: true);
        var image = new SkiaEditorImage(window);
        var area = new ImgRect(0, 40, 400, 120);
        using var rendered = Render(image, [new Annotation { Kind = AnnotationKind.Blur, Rect = area, BlurLevel = 5 }], null);
        using var cache = new BlurSampleCache(image, 1);
        // Every pixel in the area comes from the sample (replace mode), so the area is uniform per mosaic cell:
        // two pixels in the same cell are equal even where the source had glyph edges.
        var block = BlurSampleMath.BlockSize(400, 240, 5);
        var cellX = 24 / block * block;
        Assert.Equal(Pixel(rendered, cellX + 1, 60), Pixel(rendered, cellX + 2, 60));
        Assert.Equal(Pixel(rendered, cellX + 1, 60), Pixel(rendered, cellX + 1, 61));
    }

    internal static SKImage Render(SkiaEditorImage image, IReadOnlyList<Annotation> annotations, IReadOnlyList<ImgRect>? runs, bool shadows = false, WatermarkStyle? watermark = null, double scale = 1)
    {
        var state = new EditorRenderState { Image = image, Annotations = annotations, TextRuns = runs, Shadows = shadows, Watermark = watermark ?? WatermarkStyle.None, Scale = scale };
        using var caches = new EditorRenderCaches();
        return EditorRenderer.Flatten(state, caches);
    }

    internal static SKColor Pixel(SKImage image, int x, int y)
    {
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(x, y);
    }
}

public class EraseTests
{
    [Fact]
    public void A_flat_background_is_reproduced_exactly()
    {
        var color = new SKColor(0x2E, 0x8B, 0x57);
        using var flat = EditorSampleImages.Flat(300, 200, color);
        var image = new SkiaEditorImage(flat);
        using var rendered = BlurSampleTests.Render(image, [new Annotation { Kind = AnnotationKind.Blur, BlurStyle = BlurStyle.Erase, Rect = new ImgRect(80, 50, 120, 90) }], null);
        foreach (var (x, y) in new[] { (80, 50), (140, 95), (199, 139), (120, 60) })
        {
            Assert.Equal(color, BlurSampleTests.Pixel(rendered, x, y));
        }
    }

    [Fact]
    public void A_straight_gradient_is_reproduced()
    {
        using var gradient = EditorSampleImages.HorizontalGradient(512, 200);
        var image = new SkiaEditorImage(gradient);
        var patch = EraseFill.Build(image, new ImgRect(150, 60, 200, 80), null);
        Assert.NotNull(patch);
        using var rendered = BlurSampleTests.Render(image, [new Annotation { Kind = AnnotationKind.Blur, BlurStyle = BlurStyle.Erase, Rect = new ImgRect(150, 60, 200, 80) }], null);
        for (var x = 160; x < 340; x += 20)
        {
            var expected = BlurSampleTests.Pixel(gradient, x, 100).Red;
            Assert.InRange(BlurSampleTests.Pixel(rendered, x, 100).Red, expected - 4, expected + 4);
        }

        patch!.Dispose();
    }

    [Fact]
    public void The_cache_is_reused_and_invalidated_by_new_runs()
    {
        using var window = EditorSampleImages.Window(400, 300);
        var image = new SkiaEditorImage(window);
        using var cache = new EraseCache();
        var runs = new List<ImgRect> { new(20, 50, 200, 30) };
        var first = cache.Get(image, new ImgRect(10, 100, 100, 50), runs);
        Assert.Same(first, cache.Get(image, new ImgRect(10.2, 100.4, 99.5, 49.1), runs));
        Assert.Equal(1, cache.Count);
        var other = new List<ImgRect>(runs);
        cache.Get(image, new ImgRect(10, 100, 100, 50), other);
        Assert.Equal(1, cache.Count);   // new runs list: cache cleared, one fresh entry
    }

    [Fact]
    public void Areas_under_a_pixel_have_no_fill()
    {
        using var flat = EditorSampleImages.Flat(50, 50, SKColors.Red);
        Assert.Null(EraseFill.Build(new SkiaEditorImage(flat), new ImgRect(60, 60, 10, 10), null));
    }
}

public class ArrowRenderTests
{
    [Fact]
    public void The_filled_silhouette_has_no_holes_at_the_tail_cap_or_head_junction()
    {
        using var flat = EditorSampleImages.Flat(240, 80, SKColors.White);
        var image = new SkiaEditorImage(flat);
        var arrow = new Annotation { Kind = AnnotationKind.Arrow, Start = new(20, 40), End = new(220, 40), Stroke = StrokeWidth.Large, Color = AnnotationColor.Black };
        using var rendered = BlurSampleTests.Render(image, [arrow], null);
        var red = SkiaPaths.Color(AnnotationColor.Black);
        Assert.Equal(red, BlurSampleTests.Pixel(rendered, 18, 40));   // round tail cap behind the tail
        var head = ArrowGeometry.Head(arrow.Start, arrow.End, 7);
        Assert.Equal(red, BlurSampleTests.Pixel(rendered, (int)head.Base.X, 40));   // head junction
        Assert.Equal(red, BlurSampleTests.Pixel(rendered, (int)head.Base.X + 1, 41));
        Assert.Equal(SKColors.White, BlurSampleTests.Pixel(rendered, 120, 10));
    }
}

public class ExportTests
{
    [Fact]
    public void High_dpi_exports_keep_pixels_and_density_and_1x_halves_them()
    {
        using var window = EditorSampleImages.Window(400, 300);
        var image = new SkiaEditorImage(window);
        var state = new EditorRenderState { Image = image, Annotations = [], Scale = 2 };
        using var full = EditorRenderer.Export(state, BackdropStyle.None, includeBackdrop: true, downscaleTo1x: false);
        Assert.Equal((400, 300, 2.0), (full.Image.Width, full.Image.Height, full.Scale));
        var png = PngWriter.Encode(full.Image, full.Scale);
        Assert.Equal(192, PngWriter.ReadDpi(png)!.Value, 0);
        Assert.Equal(2, PngWriter.ReadScale(png));
        using var decoded = SKImage.FromEncodedData(png);
        Assert.Equal(400, decoded.Width);

        using var half = EditorRenderer.Export(state, BackdropStyle.None, includeBackdrop: true, downscaleTo1x: true);
        Assert.Equal((200, 150, 1.0), (half.Image.Width, half.Image.Height, half.Scale));
        Assert.Equal(96, PngWriter.ReadDpi(PngWriter.Encode(half.Image, half.Scale))!.Value, 0);

        var fractional = state with { Scale = 1.5 };
        using var third = EditorRenderer.Export(fractional, BackdropStyle.None, true, true);
        Assert.Equal((267, 200), (third.Image.Width, third.Image.Height));
    }

    [Fact]
    public void Backdrops_compose_and_pins_keep_only_the_rounded_corners()
    {
        using var window = EditorSampleImages.Window(400, 300);
        var image = new SkiaEditorImage(window);
        var state = new EditorRenderState { Image = image, Annotations = [] };
        var backdrop = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "sunset", Padding = 0.5, CornerRadius = 0.2 };
        using var full = EditorRenderer.Export(state, backdrop, includeBackdrop: true, downscaleTo1x: false);
        var g = BackdropRenderer.Measure(400, 300, backdrop);
        Assert.Equal((g.CanvasWidth, g.CanvasHeight), (full.Image.Width, full.Image.Height));

        using var pin = EditorRenderer.Export(state, backdrop, includeBackdrop: false, downscaleTo1x: false);
        Assert.Equal((400, 300), (pin.Image.Width, pin.Image.Height));
        Assert.Equal(0, BlurSampleTests.Pixel(pin.Image, 0, 0).Alpha);
        Assert.Equal(255, BlurSampleTests.Pixel(pin.Image, 200, 150).Alpha);
    }

    [Fact]
    public void Png_density_survives_reencoding_and_replaces_an_existing_chunk()
    {
        using var flat = EditorSampleImages.Flat(10, 10, SKColors.Blue);
        var png = PngWriter.Encode(flat, 1.25);
        Assert.Equal(1.25, PngWriter.ReadScale(png));
        var again = PngWriter.WithDpi(png, 288);
        Assert.Equal(3, PngWriter.ReadScale(again));
        Assert.Equal(png.Length, again.Length);
        Assert.Null(PngWriter.ReadScale(PngWriter.WithDpi(png, 960)));   // 10× is outside 0.5…4
    }
}

public class WatermarkRenderTests
{
    [Fact]
    public void A_missing_picture_draws_nothing()
    {
        using var flat = EditorSampleImages.Flat(200, 100, SKColors.White);
        var image = new SkiaEditorImage(flat);
        var mark = new WatermarkStyle { Kind = WatermarkKind.Image, ImagePath = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".png"), Opacity = 1 };
        using var rendered = BlurSampleTests.Render(image, [], null, watermark: mark);
        using var bitmap = SKBitmap.FromImage(rendered);
        Assert.All(bitmap.Pixels, p => Assert.Equal(SKColors.White, p));
    }

    [Fact]
    public void Text_marks_land_in_their_corner_inside_the_image()
    {
        using var flat = EditorSampleImages.Flat(600, 400, SKColors.White);
        var image = new SkiaEditorImage(flat);
        var mark = new WatermarkStyle { Kind = WatermarkKind.Text, Text = "DRAFT", Color = AnnotationColor.Black, Opacity = 1, Size = 0.4 };
        using var rendered = BlurSampleTests.Render(image, [], null, watermark: mark);
        using var bitmap = SKBitmap.FromImage(rendered);
        var dark = new List<(int X, int Y)>();
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Red < 128)
                {
                    dark.Add((x, y));
                }
            }
        }

        Assert.NotEmpty(dark);
        // Bottom-right by default, kept off the 5 % margin (20 px of 400).
        Assert.True(dark.Min(p => p.X) > 300);
        Assert.True(dark.Max(p => p.X) <= 580);
        Assert.True(dark.Max(p => p.Y) <= 380);
    }
}

public class BackdropCodecTests
{
    [Fact]
    public void Styles_round_trip_with_mac_field_names()
    {
        var style = new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [new RgbColor(0.2, 0.47, 0.96), new RgbColor(1, 1, 1)], Padding = 0.3, CornerRadius = 0.1, Blur = 0.2 };
        var json = BackdropCodec.Encode(style);
        Assert.Contains("\"kind\":\"gradient\"", json, StringComparison.Ordinal);
        Assert.Contains("[0.2,0.47,0.96]", json, StringComparison.Ordinal);
        var back = BackdropCodec.Decode(json);
        Assert.True(style.SameLook(back));
        Assert.Equal((0.3, 0.1, 0.2), (back.Padding, back.CornerRadius, back.Blur));
        Assert.Equal(string.Empty, BackdropCodec.Encode(BackdropStyle.None));
        Assert.Equal("preset", BackdropCodec.ToJson(new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean" })["kind"]!.GetValue<string>());
        Assert.Contains("\"presetID\":\"ocean\"", BackdropCodec.Encode(new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean" }), StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_is_tolerant_and_bad_values_demote_to_none()
    {
        Assert.Equal("sunset", BackdropCodec.Decode("{\"Kind\":1,\"PresetId\":\"sunset\",\"Padding\":0.7}").PresetId);
        Assert.Equal(BackdropKind.Preset, BackdropCodec.Decode("\"{\\\"kind\\\":\\\"preset\\\",\\\"presetID\\\":\\\"candy\\\"}\"").Kind);
        Assert.Equal(BackdropKind.None, BackdropCodec.Decode("{\"kind\":\"preset\",\"presetID\":\"nope\"}").Kind);
        Assert.Equal(BackdropKind.None, BackdropCodec.Decode("{\"kind\":\"solid\",\"colors\":[[1,0]]}").Kind);
        Assert.Equal(BackdropKind.None, BackdropCodec.Decode("not json").Kind);
        Assert.Equal(0.5, BackdropCodec.Decode("{\"kind\":\"none\"}").Padding);
    }

    [Fact]
    public void Presets_are_capped_at_twelve_normalized_and_deduplicated()
    {
        IReadOnlyList<BackdropStyle> presets = [];
        for (var i = 0; i < 14; i++)
        {
            presets = BackdropCodec.AddPreset(presets, new BackdropStyle { Kind = BackdropKind.Solid, Colors = [new RgbColor(i / 20.0, 0, 0)], Padding = 0.9, Blur = 0.4 });
        }

        Assert.Equal(12, presets.Count);
        Assert.Equal(2 / 20.0, presets[0].Colors![0].R, 9);
        Assert.All(presets, p => Assert.Equal((0.5, 0.1, 0.0), (p.Padding, p.CornerRadius, p.Blur)));
        Assert.Same(presets, BackdropCodec.AddPreset(presets, presets[3] with { Padding = 0.2 }));
        var decoded = BackdropCodec.DecodePresets(BackdropCodec.EncodePresets(presets.Append(BackdropStyle.None)));
        Assert.Equal(12, decoded.Count);
    }
}

public class QrTests
{
    [Fact]
    public void Detects_a_qr_code_and_offers_a_single_https_link()
    {
        using var image = QrImage("https://example.com/hello", 600, 400, 40, 40);
        var result = QrDetector.Detect(image);
        Assert.NotNull(result);
        Assert.Equal("https://example.com/hello", result!.Payload);
        Assert.Equal("https://example.com/hello", result.Url!.ToString());
    }

    [Fact]
    public void Non_link_payloads_are_never_opened()
    {
        using var image = QrImage("mailto:someone@example.com", 400, 300, 20, 20);
        var result = QrDetector.Detect(image);
        Assert.NotNull(result);
        Assert.Null(result!.Url);
    }

    [Fact]
    public void A_plain_capture_has_no_code()
    {
        using var window = EditorSampleImages.Window(500, 300);
        Assert.Null(QrDetector.Detect(window));
    }

    private static SKImage QrImage(string text, int width, int height, int x, int y)
    {
        var matrix = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, 200, 200);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        using var black = new SKPaint { Color = SKColors.Black };
        for (var j = 0; j < matrix.Height; j++)
        {
            for (var i = 0; i < matrix.Width; i++)
            {
                if (matrix[i, j])
                {
                    surface.Canvas.DrawRect(x + i, y + j, 1, 1, black);
                }
            }
        }

        return surface.Snapshot();
    }
}
