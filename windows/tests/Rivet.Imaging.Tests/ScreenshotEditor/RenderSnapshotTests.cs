// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Backdrop;
using Rivet.Imaging.ScreenshotEditor;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.ScreenshotEditor;

/// <summary>Rendered sheets for human review (tests/artifacts/snapshots/editor-*.png).</summary>
public class RenderSnapshotTests
{
    /// <summary>Every tool on one capture, with and without annotation shadows.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Annotation_sheet(bool shadows)
    {
        using var window = EditorSampleImages.Window(900, 560);
        var image = new SkiaEditorImage(window);
        var annotations = SampleAnnotations(900, 560);
        var runs = new List<ImgRect> { new(14, 82, 330, 30), new(14, 112, 280, 30) };
        using var rendered = BlurSampleTests.Render(image, annotations, runs, shadows);
        Snapshots.Save(rendered, shadows ? "editor-annotations-shadows" : "editor-annotations");
        Assert.Equal(900, rendered.Width);
    }

    [Fact]
    public void Arrow_styles_at_three_thicknesses()
    {
        using var flat = EditorSampleImages.Flat(760, 300, new SKColor(0xF4, 0xF4, 0xF6));
        var image = new SkiaEditorImage(flat);
        var marks = new List<Annotation>();
        var x = 20.0;
        foreach (var style in ArrowStyles.All)
        {
            var y = 30.0;
            foreach (var stroke in StrokeWidths.All)
            {
                marks.Add(new Annotation
                {
                    Kind = AnnotationKind.Arrow, ArrowStyle = style, Stroke = stroke, Color = AnnotationColor.Red, Seed = 99,
                    Start = new ImgPoint(x, y + 60), End = new ImgPoint(x + 120, y),
                });
                y += 90;
            }

            x += 150;
        }

        using var rendered = BlurSampleTests.Render(image, marks, null);
        Snapshots.Save(rendered, "editor-arrow-styles");
        var samples = ArrowStyles.All.Select(s => EditorSamples.ArrowStyle(s, 3, SKColors.White)).ToList();
        using var sheet = EditorSampleImages.Sheet(samples, 5, new SKColor(0x2B, 0x2B, 0x2B));
        Snapshots.Save(sheet, "editor-arrow-menu-samples");
        samples.ForEach(s => s.Dispose());
    }

    [Fact]
    public void Blur_styles_and_levels()
    {
        using var window = EditorSampleImages.Window(900, 300);
        var image = new SkiaEditorImage(window);
        var tiles = new List<SKImage>();
        foreach (var style in BlurStyles.All)
        {
            foreach (var level in new[] { 1, 3, 5 })
            {
                if (style == BlurStyle.Erase && level != 3)
                {
                    continue;
                }

                var mark = new Annotation { Kind = AnnotationKind.Blur, BlurStyle = style, BlurLevel = level, Rect = new ImgRect(16, 50, 520, 160) };
                using var rendered = BlurSampleTests.Render(image, [mark], null);
                tiles.Add(SkiaConvertCrop(rendered, new SKRectI(0, 30, 560, 240)));
            }
        }

        using var sheet = EditorSampleImages.Sheet(tiles, 3, new SKColor(0x20, 0x20, 0x20));
        Snapshots.Save(sheet, "editor-blur-styles");
        tiles.ForEach(t => t.Dispose());
    }

    [Fact]
    public void Watermark_positions_rotation_and_export_with_backdrop()
    {
        using var window = EditorSampleImages.Window(640, 400);
        var image = new SkiaEditorImage(window);
        var tiles = new List<SKImage>();
        foreach (var anchor in new[] { WatermarkAnchor.TopLeading, WatermarkAnchor.Center, WatermarkAnchor.BottomTrailing })
        {
            var mark = new WatermarkStyle { Kind = WatermarkKind.Text, Text = "Rivet draft", Color = AnnotationColor.Purple, Anchor = anchor, Opacity = 0.8, Size = 0.35, Rotation = anchor == WatermarkAnchor.Center ? 30 : 0 };
            tiles.Add(BlurSampleTests.Render(image, [], null, watermark: mark));
        }

        using (var sheet = EditorSampleImages.Sheet(tiles, 3, new SKColor(0x20, 0x20, 0x20)))
        {
            Snapshots.Save(sheet, "editor-watermarks");
        }

        tiles.ForEach(t => t.Dispose());

        var state = new EditorRenderState
        {
            Image = image,
            Annotations = SampleAnnotations(640, 400).Take(6).ToList(),
            Watermark = new WatermarkStyle { Kind = WatermarkKind.Text, Text = "Rivet", Color = AnnotationColor.White, Opacity = 0.6 },
            Shadows = true,
        };
        var exports = new List<SKImage>();
        foreach (var preset in BackdropPresets.All.Take(3))
        {
            var backdrop = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = preset.Id, Padding = 0.5, CornerRadius = 0.12, Blur = 0 };
            var export = EditorRenderer.Export(state, backdrop, includeBackdrop: true, downscaleTo1x: false);
            exports.Add(export.Image);
        }

        using var exportSheet = EditorSampleImages.Sheet(exports, 3, new SKColor(0x30, 0x30, 0x30));
        Snapshots.Save(exportSheet, "editor-export-backdrops");
        exports.ForEach(e => e.Dispose());
    }

    [Fact]
    public void Canvas_painter_draws_backdrop_card_selection_and_crop()
    {
        using var window = EditorSampleImages.Window(640, 400);
        var image = new SkiaEditorImage(window);
        var annotations = SampleAnnotations(640, 400).Take(6).ToList();
        var state = new EditorRenderState { Image = image, Annotations = annotations, Scale = 1 };
        using var painter = new EditorCanvasPainter();
        using var surface = SKSurface.Create(new SKImageInfo(1400, 520));
        var c = surface.Canvas;
        c.Clear(new SKColor(0x1D, 0x1D, 0x1D));
        var backdrop = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean", Padding = 0.5, CornerRadius = 0.1 };
        painter.Paint(c, new CanvasFrame { State = state, Zoom = 0.9, Origin = new SKPoint(20, 20), Backdrop = backdrop, Selected = annotations[2], Viewport = SKRect.Create(1400, 520) });
        painter.Paint(c, new CanvasFrame
        {
            State = state, Zoom = 0.9, Origin = new SKPoint(740, 60), CropDraft = new ImgRect(60, 40, 420, 280), CropLoupePoint = new ImgPoint(480, 40),
            Viewport = SKRect.Create(1400, 520),
        });
        using var snapshot = surface.Snapshot();
        Snapshots.Save(snapshot, "editor-canvas-painter");
        Assert.NotEqual(new SKColor(0x1D, 0x1D, 0x1D), BlurSampleTests.Pixel(snapshot, 100, 100));
    }

    internal static List<Annotation> SampleAnnotations(int w, int h) =>
    [
        new() { Kind = AnnotationKind.Arrow, Start = new(w * 0.62, h * 0.25), End = new(w * 0.45, h * 0.12), Color = AnnotationColor.Red, Stroke = StrokeWidth.Medium },
        new() { Kind = AnnotationKind.Rect, Rect = new ImgRect(18, 80, 340, 34), Color = AnnotationColor.Orange, Stroke = StrokeWidth.Medium },
        new() { Kind = AnnotationKind.Highlight, Rect = new ImgRect(18, 176, 230, 26), Color = AnnotationColor.Yellow },
        new() { Kind = AnnotationKind.Counter, Rect = new ImgRect(w * 0.55, h * 0.55, 0, 0), Color = AnnotationColor.Blue, Number = 1 },
        new() { Kind = AnnotationKind.Counter, Rect = new ImgRect(w * 0.62, h * 0.55, 0, 0), Color = AnnotationColor.White, Number = 2 },
        new() { Kind = AnnotationKind.Text, Rect = new ImgRect(w * 0.08, h * 0.72, 260, 40), Text = "Check this total", Color = AnnotationColor.Red, TextSize = 24 },
        new() { Kind = AnnotationKind.Blur, Rect = new ImgRect(14, 82, 330, 60), TextOnly = true, BlurLevel = 3 },
        new() { Kind = AnnotationKind.Ellipse, Rect = new ImgRect(w * 0.68, h * 0.18, 160, 90), Color = AnnotationColor.Green, Stroke = StrokeWidth.Large },
        new() { Kind = AnnotationKind.Freehand, Points = Wave(w * 0.66, h * 0.82, 180), Color = AnnotationColor.Purple, Stroke = StrokeWidth.Medium },
        new() { Kind = AnnotationKind.Line, Start = new(w * 0.05, h * 0.92), End = new(w * 0.4, h * 0.92), Color = AnnotationColor.Black, Stroke = StrokeWidth.Small },
        new() { Kind = AnnotationKind.Redact, Rect = new ImgRect(w * 0.45, h * 0.66, 120, 24), Color = AnnotationColor.Black },
        new() { Kind = AnnotationKind.Sticker, Rect = new ImgRect(w * 0.88, h * 0.05, 64, 64), Sticker = StickerKind.Check },
        new() { Kind = AnnotationKind.Sticker, Rect = new ImgRect(w * 0.88, h * 0.2, 64, 64), Sticker = StickerKind.Fire },
        new() { Kind = AnnotationKind.Blur, BlurStyle = BlurStyle.Erase, Rect = new ImgRect(w * 0.3, h * 0.38, 140, 30) },
        new() { Kind = AnnotationKind.Arrow, ArrowStyle = ArrowStyle.Scribbly, Seed = 1234, Start = new(w * 0.2, h * 0.6), End = new(w * 0.42, h * 0.48), Color = AnnotationColor.Blue, Stroke = StrokeWidth.Medium },
    ];

    private static IReadOnlyList<ImgPoint> Wave(double x, double y, double length)
    {
        var points = new List<ImgPoint>();
        for (var i = 0; i <= 40; i++)
        {
            points.Add(new ImgPoint(x + (i * length / 40), y + (Math.Sin(i / 4.0) * 14)));
        }

        return points;
    }

    private static SKImage SkiaConvertCrop(SKImage image, SKRectI rect) => Rivet.Imaging.Skia.SkiaConvert.Crop(image, rect).ToRasterImage(true);
}
