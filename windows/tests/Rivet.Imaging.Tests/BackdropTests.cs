// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Rivet.Imaging.Backdrop;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests;

public class BackdropTests
{
    [Fact]
    public void Geometry_follows_the_spec_formulas()
    {
        // short = 800: P = max(24, round(800 · (0.035 + 0.14 · 0.5))) = 84; R = round(0.25 · 800 · 0.2) = 40
        var style = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean", Padding = 0.5, CornerRadius = 0.25 };
        var g = BackdropRenderer.Measure(1200, 800, style);
        Assert.Equal(84, g.Padding);
        Assert.Equal(40, g.CornerRadius);
        Assert.Equal(1368, g.CanvasWidth);
        Assert.Equal(968, g.CanvasHeight);

        // Small captures keep the 24 px minimum margin.
        Assert.Equal(24, BackdropRenderer.Measure(100, 80, style with { Padding = 0 }).Padding);
    }

    [Fact]
    public void No_backdrop_keeps_the_capture_size()
    {
        var g = BackdropRenderer.Measure(640, 480, BackdropStyle.None);
        Assert.Equal((640, 480, 0), (g.CanvasWidth, g.CanvasHeight, g.Padding));
    }

    [Fact]
    public void Malformed_styles_demote_to_none()
    {
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "nope" }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [new RgbColor(1, 0, 0)] }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Image, ImagePath = " " }.Sanitized().Kind);
        Assert.Equal(1.0, new BackdropStyle { Padding = 7 }.Sanitized().Padding);
    }

    [Fact]
    public void Styles_round_trip_through_json_with_colour_arrays()
    {
        var style = new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [new RgbColor(0.2, 0.47, 0.96), new RgbColor(1, 1, 1)], Padding = 0.3 };
        var json = JsonSerializer.Serialize(style);
        Assert.Contains("[0.2,0.47,0.96]", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<BackdropStyle>(json)!;
        Assert.True(style.SameLook(back));
        Assert.Equal(0.3, back.Padding);
    }

    [Fact]
    public void Same_look_ignores_sliders()
    {
        var a = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "sunset", Padding = 0.1 };
        var b = a with { Padding = 0.9, Blur = 0.5 };
        Assert.True(a.SameLook(b));
        Assert.Equal(0.1, a.WithLook(b).Padding);
    }

    [Fact]
    public void Renders_a_capture_on_each_preset()
    {
        using var capture = SampleCapture(480, 300);
        var tiles = new List<SKImage>();
        foreach (var preset in BackdropPresets.All)
        {
            var style = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = preset.Id, Padding = 0.5, CornerRadius = 0.15, Blur = 0 };
            var rendered = BackdropRenderer.Render(capture, style);
            Assert.Equal(BackdropRenderer.Measure(480, 300, style).CanvasWidth, rendered.Width);
            // The corner is plate colour, the centre is the capture.
            using var bitmap = SKBitmap.FromImage(rendered);
            Assert.Equal(255, bitmap.GetPixel(2, 2).Alpha);
            tiles.Add(rendered);
        }

        using var sheet = Compose(tiles);
        Snapshots.Save(sheet, "backdrop-presets");
        foreach (var tile in tiles)
        {
            tile.Dispose();
        }
    }

    [Fact]
    public void Rounded_capture_without_backdrop_has_transparent_corners()
    {
        using var capture = SampleCapture(200, 120);
        using var rendered = BackdropRenderer.Render(capture, new BackdropStyle { CornerRadius = 0.5 });
        using var bitmap = SKBitmap.FromImage(rendered);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Equal(255, bitmap.GetPixel(100, 60).Alpha);
    }

    private static SKImage SampleCapture(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.White);
        using var bar = new SKPaint { Color = new SKColor(0xE8, 0xE8, 0xEC) };
        c.DrawRect(0, 0, width, 28, bar);
        using var dot = new SKPaint { IsAntialias = true };
        var colors = new[] { new SKColor(0xFF, 0x5F, 0x57), new SKColor(0xFE, 0xBC, 0x2E), new SKColor(0x28, 0xC8, 0x40) };
        for (var i = 0; i < 3; i++)
        {
            dot.Color = colors[i];
            c.DrawCircle(16 + (i * 18), 14, 6, dot);
        }

        using var text = new SKPaint { Color = new SKColor(0x30, 0x30, 0x38) };
        for (var y = 48; y < height - 16; y += 22)
        {
            c.DrawRect(20, y, (width - 60) * (0.5f + (0.5f * (float)Math.Abs(Math.Sin(y)))), 10, text);
        }

        return surface.Snapshot();
    }

    private static SKImage Compose(IReadOnlyList<SKImage> images)
    {
        var width = images.Sum(i => i.Width) + (16 * (images.Count + 1));
        var height = images.Max(i => i.Height) + 32;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(0x20, 0x20, 0x20));
        var x = 16f;
        foreach (var image in images)
        {
            surface.Canvas.DrawImage(image, x, 16);
            x += image.Width + 16;
        }

        return surface.Snapshot();
    }
}
