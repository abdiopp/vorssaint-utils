// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Modules.RadialMenu;
using Rivet.Imaging.RadialMenu;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.RadialMenu;

public class RadialWheelRendererTests
{
    private static SKImage Render(RadialWheelFrame frame, SKColor background)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 800, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(background);
        canvas.Scale(2);
        RadialWheelRenderer.Draw(canvas, 200, 200, frame);
        return surface.Snapshot();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_wheel_renders_with_a_highlighted_slice(bool dark)
    {
        var frame = new RadialWheelFrame
        {
            Count = 6,
            Highlight = 1,
            WedgeAngle = RadialGeometry.SliceAngle(1, 6),
            WedgeOpacity = 1,
            ChipScale = [1, RadialGeometry.HighlightScale, 1, 1, 1, 1],
            Color = RadialLabels.ColorValue(RadialColor.Purple, dark, 0xFF0067C0),
            Dark = dark,
        };
        using var image = Render(frame, dark ? new SKColor(40, 44, 52) : new SKColor(210, 220, 235));
        Snapshots.Save(image, $"radial-wheel-{(dark ? "dark" : "light")}");

        // The highlighted chip (slice 1, at 2 o'clock... 3 o'clock for six slices) takes the profile colour.
        var (cx, cy) = RadialWheelRenderer.ChipCenter(1, 6, RadialGeometry.ChipRingRadius, 200, 200);
        using var bitmap = SKBitmap.FromImage(image);
        var pixel = bitmap.GetPixel((int)(cx * 2), (int)(cy * 2));
        var expected = new SKColor(frame.Color);
        Assert.InRange(Math.Abs(pixel.Red - expected.Red), 0, 12);
        Assert.InRange(Math.Abs(pixel.Blue - expected.Blue), 0, 12);
    }

    [Fact]
    public void The_canvas_size_renders_twelve_chips()
    {
        var frame = new RadialWheelFrame { Count = 12, Size = RadialWheelSize.Canvas, Dark = true, Color = 0xFF30D158 };
        using var image = Render(frame, new SKColor(20, 23, 28));
        Snapshots.Save(image, "radial-canvas-twelve");
        using var bitmap = SKBitmap.FromImage(image);
        var (x, y) = RadialWheelRenderer.ChipCenter(0, 12, RadialGeometry.CanvasRing, 200, 200);
        Assert.NotEqual(new SKColor(20, 23, 28), bitmap.GetPixel((int)(x * 2), (int)(y * 2)));
    }

    [Fact]
    public void A_closed_wheel_draws_nothing()
    {
        var frame = new RadialWheelFrame { Count = 4, Open = 0 };
        using var image = Render(frame, SKColors.Transparent);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(0, bitmap.GetPixel(400, 400).Alpha);
    }
}
