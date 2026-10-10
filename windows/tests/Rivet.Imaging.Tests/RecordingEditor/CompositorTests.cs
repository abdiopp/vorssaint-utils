// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.RecordingEditor;

public sealed class CompositorTests : IDisposable
{
    private const int W = 1280;
    private const int H = 800;
    private const double D = 12;
    private readonly string _folder = TestTakes.TempFolder("compositor");
    private readonly PointerTrack _pointer = SampleTake.Pointer(D);
    private readonly SyntheticVideoSource _source = new(W, H, D, 60, SyntheticScene.Desktop);

    public CompositorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _source.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private SKImage Render(EditDocument doc, double editedTime, double scale = 0.375, PointerTrack? pointer = null, IVideoFrameSource? source = null)
    {
        var track = pointer ?? _pointer;
        var plan = FramePlan.Build(doc, D, W, H, 60, track, 4, scale);
        using var assets = new CompositorAssets(track);
        using var compositor = new FrameCompositor(plan, assets);
        var frame = (source ?? _source).GetFrame(plan.Timeline.SourceTime(editedTime))!;
        using var pinned = new PinnedFrame(frame.Pixels);
        return compositor.RenderImage(pinned.Image, editedTime);
    }

    private string Logo()
    {
        var path = Path.Combine(_folder, "logo.png");
        if (!File.Exists(path))
        {
            using var surface = SKSurface.Create(new SKImageInfo(240, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            c.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0x45, 0x3A) };
            c.DrawRoundRect(SKRect.Create(0, 0, 240, 120), 28, 28, paint);
            paint.Color = SKColors.White;
            using var font = new SKFont(CaptionRaster.Typeface, 54);
            c.DrawText("LOGO", 120, 80, SKTextAlign.Center, font, paint);
            using var image = surface.Snapshot();
            SkiaConvert.SavePng(image, path);
        }

        return path;
    }

    private static EditDocument Base => new()
    {
        ZoomEnabled = false,
        ShowsClickRing = false,
        PointerSmoothing = PointerSmoothing.Smooth,
        ZoomsGenerated = true,
    };

    [Fact]
    public void Renders_the_look_matrix_contact_sheet()
    {
        var zoom = new ZoomSegment { Id = "z", Start = 1.0, End = 4.5, Amount = 2 };
        var aimed = new ZoomSegment { Id = "a", Start = 1.0, End = 4.5, Amount = 2.5, FocusX = 0.15, FocusY = 0.15 };
        var tiles = new List<(string Label, SKImage Image)>
        {
            ("Original look", Render(Looks.Apply(RecorderLook.Original, Base), 2.25)),
            ("Zoom 2x, click ring", Render(Base with { ZoomEnabled = true, ZoomSegments = [zoom], ShowsClickRing = true }, 2.25)),
            ("Aimed zoom 2.5x", Render(Base with { ZoomEnabled = true, ZoomSegments = [aimed] }, 3.0)),
            ("Blur strength 3", Render(Base with { Blurs = [new BlurRegion { Id = "b", Start = 0, End = D, X = 0.12, Y = 0.22, Width = 0.5, Height = 0.32 }] }, 2.25)),
            ("Captions + image", Render(Base with
            {
                Texts =
                [
                    new TextOverlay { Id = "t1", Text = "Click Save", Start = 0, End = D, Anchor = OverlayAnchor.Bottom, Size = 0.08 },
                    new TextOverlay { Id = "t2", Text = "Step 2\nType a name", Start = 0, End = D, Anchor = OverlayAnchor.TopLeading, Palette = CaptionPalette.Yellow },
                ],
                Images = [new ImageOverlay { Id = "i", Path = Logo(), Start = 0, End = D, Anchor = OverlayAnchor.BottomTrailing, Size = 0.18 }],
            }, 2.25)),
            ("Studio look", Render(Looks.Apply(RecorderLook.Studio, Base), 2.25)),
            ("Wide, framed (sunset)", Render(Base with
            {
                Aspect = CanvasAspect.Wide,
                Backdrop = new RecorderBackdrop { Kind = RecorderBackdropKind.Preset, PresetId = "sunset", Padding = 0.7, CornerRadius = 0.6 }.ToJson(),
            }, 2.25, 0.3)),
            ("Square, cropped", Render(Base with { Aspect = CanvasAspect.Square }, 2.25)),
            ("Tall, framed (ocean, blur)", Render(Base with
            {
                Aspect = CanvasAspect.Vertical,
                Backdrop = new RecorderBackdrop { Kind = RecorderBackdropKind.Gradient, Colors = [new RgbValue(0.04, 0.52, 1), new RgbValue(0.69, 0.32, 0.87)], Padding = 0.4, CornerRadius = 1, Blur = 0.5 }.ToJson(),
            }, 2.25, 0.25)),
            ("Fallback arrow (no shapes)", Render(Base, 2.25, pointer: new PointerTrack { Samples = _pointer.Samples, Clicks = _pointer.Clicks, DisplayScale = 1.5f })),
            ("Click ring on the Save button", Render(Base with { ShowsClickRing = true, PointerSize = 1.6 }, 10.32)),
            ("Square, framed (graphite)", Render(Base with { Aspect = CanvasAspect.Square, Backdrop = Looks.StudioBackdrop.ToJson() }, 2.25, 0.3)),
        };

        using var sheet = ContactSheet(tiles, 3);
        Snapshots.Save(sheet, "recording-compositor-looks");
        foreach (var (_, image) in tiles)
        {
            image.Dispose();
        }
    }

    [Fact]
    public void Plain_document_reproduces_the_source_pixels()
    {
        var doc = Base with { ShowsPointer = false };
        using var rendered = Render(doc, 1.0, scale: 1);
        var frame = _source.GetFrame(1.0)!;
        using var expected = SkiaConvert.ToImage(frame.Pixels);
        Assert.Equal((W, H), (rendered.Width, rendered.Height));
        using var a = SKBitmap.FromImage(rendered);
        using var b = SKBitmap.FromImage(expected);
        foreach (var (x, y) in new[] { (10, 10), (640, 400), (1200, 700), (300, 260) })
        {
            Assert.Equal(b.GetPixel(x, y), a.GetPixel(x, y));
        }
    }

    [Fact]
    public void Studio_plate_shows_the_gradient_and_rounds_the_card()
    {
        using var rendered = Render(Looks.Apply(RecorderLook.Studio, Base), 1.0, scale: 0.5);
        using var bitmap = SKBitmap.FromImage(rendered);
        var corner = bitmap.GetPixel(2, 2);
        Assert.True(corner.Red > 40 && corner.Blue > 50, $"plate corner {corner}");
        var layout = CanvasLayout.Compute(W, H, Looks.StudioBackdrop, CanvasAspect.Original, 0.5);
        // The card's own corner pixel is outside the rounded rect: plate or shadow, not the recording.
        var cardCorner = bitmap.GetPixel(layout.Card.X, layout.Card.Y);
        var inside = bitmap.GetPixel(layout.Card.X + (layout.Card.Width / 2), layout.Card.Y + (layout.Card.Height / 2));
        Assert.NotEqual(inside, cardCorner);
    }

    [Fact]
    public void Blur_hides_the_checkerboard_and_its_edges()
    {
        using var checker = new SyntheticVideoSource(640, 400, 4, 60, SyntheticScene.Checkerboard);
        var blur = new BlurRegion { Id = "b", Start = 1, End = 2, X = 0.25, Y = 0.25, Width = 0.5, Height = 0.5 };
        var doc = Base with { ShowsPointer = false, Blurs = [blur] };
        var plan = FramePlan.Build(doc, 4, 640, 400, 60, PointerTrack.Empty, 4, 1);
        using var assets = new CompositorAssets(PointerTrack.Empty);
        using var compositor = new FrameCompositor(plan, assets);
        var buffer = new PixelBuffer(640, 400);
        var rect = new SKRectI(160, 100, 480, 300);
        var inner = new SKRectI(161, 101, 479, 299);

        using (var frame = new PinnedFrame(checker.GetFrame(1.5)!.Pixels))
        {
            compositor.RenderInto(buffer, frame.Image, 1.5);
        }

        Assert.True(TestTakes.Contrast(buffer, inner) < 40, "obscured inside");
        Assert.True(TestTakes.Contrast(buffer, new SKRectI(0, 0, 150, 90)) > 100, "clear outside");

        using (var frame = new PinnedFrame(checker.GetFrame(2.5)!.Pixels))
        {
            compositor.RenderInto(buffer, frame.Image, 2.5);
        }

        Assert.True(TestTakes.Contrast(buffer, rect) > 100, "clear after the block ends");
    }

    [Fact]
    public void Zoom_magnifies_the_picture_but_not_captions()
    {
        var zoom = new ZoomSegment { Id = "z", Start = 0, End = D, Amount = 3, FocusX = 0.5, FocusY = 0.5 };
        var caption = new TextOverlay { Id = "t", Text = "Hello", Start = 0, End = D, Anchor = OverlayAnchor.Top, Size = 0.1 };
        var plain = Base with { ShowsPointer = false };
        var zoomed = plain with { ZoomEnabled = true, ZoomSegments = [zoom] };

        // Where the caption changes pixels is the same with and without the zoom.
        var plainBox = ChangedBox(Render(plain, 6, 0.5), Render(plain with { Texts = [caption] }, 6, 0.5));
        var zoomBox = ChangedBox(Render(zoomed, 6, 0.5), Render(zoomed with { Texts = [caption] }, 6, 0.5));
        Assert.InRange(Math.Abs(plainBox.Left - zoomBox.Left), 0, 3);
        Assert.InRange(Math.Abs(plainBox.Right - zoomBox.Right), 0, 3);
        Assert.InRange(Math.Abs(plainBox.Top - zoomBox.Top), 0, 3);

        // The picture itself is magnified.
        using var a = Render(plain, 6, 0.5);
        using var b = Render(zoomed, 6, 0.5);
        using var ba = SKBitmap.FromImage(a);
        using var bb = SKBitmap.FromImage(b);
        var differing = 0;
        for (var x = 0; x < a.Width; x += 16)
        {
            if (ba.GetPixel(x, a.Height * 3 / 4) != bb.GetPixel(x, b.Height * 3 / 4))
            {
                differing++;
            }
        }

        Assert.True(differing > 10);
    }

    private static SKRectI ChangedBox(SKImage without, SKImage with)
    {
        using var a = SKBitmap.FromImage(without);
        using var b = SKBitmap.FromImage(with);
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        without.Dispose();
        with.Dispose();
        Assert.True(right >= 0, "the caption changed some pixels");
        return new SKRectI(left, top, right, bottom);
    }

    [Fact]
    public void Plan_flags_follow_the_compositor_rule()
    {
        var plain = new EditDocument { ShowsPointer = false, ZoomEnabled = false };
        Assert.False(FramePlan.Build(plain, D, W, H, 60, PointerTrack.Empty, 4, 1).NeedsCompositor);
        Assert.True(FramePlan.Build(plain with { ShowsPointer = true }, D, W, H, 60, _pointer, 4, 1).NeedsCompositor);
        Assert.True(FramePlan.Build(plain with { Cuts = [new CutRange(1, 2)] }, D, W, H, 60, PointerTrack.Empty, 4, 1).NeedsCompositor);
        Assert.True(FramePlan.Build(plain with { Aspect = CanvasAspect.Square }, D, W, H, 60, PointerTrack.Empty, 4, 1).NeedsCompositor);
        Assert.True(FramePlan.Build(plain with { Blurs = [new BlurRegion { Id = "b", Start = 0, End = 1 }] }, D, W, H, 60, PointerTrack.Empty, 4, 1).NeedsCompositor);
    }

    [Fact]
    public void Imported_takes_without_a_pointer_aim_follow_zooms_at_the_centre()
    {
        var zoom = new ZoomSegment { Id = "z", Start = 0, End = D, Amount = 2 };
        var plan = FramePlan.Build(new EditDocument { ZoomSegments = [zoom] }, D, W, H, 60, PointerTrack.Empty, 4, 1);
        var state = plan.StateAt(6);
        Assert.Equal(0.5, state.TravelX, 3);
        Assert.Equal(0.5, state.TravelY, 3);
        Assert.False(state.PointerVisible);
    }

    private static SKImage ContactSheet(IReadOnlyList<(string Label, SKImage Image)> tiles, int columns)
    {
        const int gap = 18;
        const int label = 26;
        var cellW = tiles.Max(t => t.Image.Width);
        var rows = (tiles.Count + columns - 1) / columns;
        var rowHeights = Enumerable.Range(0, rows)
            .Select(r => tiles.Skip(r * columns).Take(columns).Max(t => t.Image.Height) + label)
            .ToArray();
        using var surface = SKSurface.Create(new SKImageInfo((columns * (cellW + gap)) + gap, rowHeights.Sum() + ((rows + 1) * gap)));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x1C, 0x1C, 0x1E));
        using var font = new SKFont(CaptionRaster.Typeface, 15);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var y = gap;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns && (r * columns) + c < tiles.Count; c++)
            {
                var (title, image) = tiles[(r * columns) + c];
                var x = gap + (c * (cellW + gap));
                canvas.DrawText(title, x, y + 16, font, text);
                canvas.DrawImage(image, x + ((cellW - image.Width) / 2f), y + label);
            }

            y += rowHeights[r] + gap;
        }

        return surface.Snapshot();
    }
}
