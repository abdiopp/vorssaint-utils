// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Imaging.Recording;
using Rivet.Imaging.Skia;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.Recording;

public class CursorBitmapTests
{
    private static readonly byte[] White = [255, 255, 255, 0];
    private static readonly byte[] Black = [0, 0, 0, 0];

    [Fact]
    public void Monochrome_cursors_map_and_xor_to_black_white_and_transparent()
    {
        // 2×2: AND rows then XOR rows.  (and,xor): (0,0) black, (0,1) white, (1,0) transparent, (1,1) invert.
        var mask = Pixels(
            Black, Black,   // AND row 0
            White, White,   // AND row 1
            Black, White,   // XOR row 0
            Black, White);  // XOR row 1
        var image = CursorBitmapDecoder.Decode(new CursorBitmapSource { Width = 2, Height = 2, Mask = mask })!;
        Assert.Equal((0, 0, 0, 255), Pixel(image, 0, 0));       // black
        Assert.Equal((255, 255, 255, 255), Pixel(image, 1, 0)); // white
        // (1,0) at row 1 col 0 is transparent but touches the inverting pixel next to it: outlined white.
        Assert.Equal((255, 255, 255, 255), Pixel(image, 0, 1));
        Assert.Equal((0, 0, 0, 255), Pixel(image, 1, 1));       // inverting → black ink
    }

    [Fact]
    public void An_inverting_i_beam_becomes_black_with_a_white_outline()
    {
        const int size = 9;
        var and = new byte[size * size][];
        var xor = new byte[size * size][];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var stem = x == 4 && y is >= 1 and <= 7;
                and[(y * size) + x] = White;            // transparent or inverting everywhere
                xor[(y * size) + x] = stem ? White : Black;
            }
        }

        var image = CursorBitmapDecoder.Decode(new CursorBitmapSource { Width = size, Height = size, Mask = Pixels([.. and, .. xor]) })!;
        Assert.Equal((0, 0, 0, 255), Pixel(image, 4, 4));       // the stem
        Assert.Equal((255, 255, 255, 255), Pixel(image, 3, 4)); // outline left
        Assert.Equal((255, 255, 255, 255), Pixel(image, 5, 4)); // outline right
        Assert.Equal((255, 255, 255, 255), Pixel(image, 4, 0)); // outline above
        Assert.Equal(0, Pixel(image, 0, 0).A);                  // far away: transparent
        Snapshots.Save(Enlarge(image, 12), "recording-cursor-ibeam");
    }

    [Fact]
    public void Colour_cursors_with_alpha_are_premultiplied()
    {
        var color = Pixels([200, 100, 50, 128], [10, 20, 30, 255]);
        var mask = Pixels(Black, Black);
        var image = CursorBitmapDecoder.Decode(new CursorBitmapSource { Width = 2, Height = 1, Color = color, Mask = mask })!;
        Assert.Equal((100, 50, 25, 128), Pixel(image, 0, 0));
        Assert.Equal((10, 20, 30, 255), Pixel(image, 1, 0));
    }

    [Fact]
    public void Colour_cursors_without_alpha_use_the_and_mask()
    {
        var color = Pixels([10, 20, 30, 0], Black, [9, 9, 9, 0]);
        var mask = Pixels(Black, White, White); // opaque, transparent, inverting
        var image = CursorBitmapDecoder.Decode(new CursorBitmapSource { Width = 3, Height = 1, Color = color, Mask = mask })!;
        Assert.Equal((10, 20, 30, 255), Pixel(image, 0, 0));
        Assert.Equal((255, 255, 255, 255), Pixel(image, 1, 0)); // transparent next to ink: outline
        Assert.Equal((0, 0, 0, 255), Pixel(image, 2, 0));       // inverting: ink
    }

    [Fact]
    public void Broken_or_huge_bitmaps_are_refused()
    {
        Assert.Null(CursorBitmapDecoder.Decode(new CursorBitmapSource { Width = 0, Height = 2, Mask = [] }));
        Assert.Null(CursorBitmapDecoder.Decode(new CursorBitmapSource { Width = 2, Height = 2, Mask = new byte[4] }));
        Assert.Null(CursorBitmapDecoder.Decode(new CursorBitmapSource { Width = 5000, Height = 5000, Mask = [] }));
    }

    [Fact]
    public void Shape_snapshots_carry_drawn_size_hot_spot_identity_and_a_png()
    {
        var arrow = Arrow(32);
        var a = CursorShapeFactory.Create(arrow, 2, 3, 1.5);
        Assert.Equal(48, a.Width);
        Assert.Equal(48, a.Height);
        Assert.Equal(3, a.HotX);
        Assert.Equal(4.5, a.HotY);
        var decoded = CursorShapeFactory.DecodePng(a.Png)!;
        Assert.Equal(48, decoded.Width); // scaled to the drawn size when there is no sharper copy

        var same = CursorShapeFactory.Create(Arrow(32), 2, 3, 1.5);
        Assert.Equal(a.Identity, same.Identity);
        Assert.NotEqual(a.Identity, CursorShapeFactory.Create(arrow, 2, 4, 1.5).Identity);

        var sharp = CursorShapeFactory.Create(arrow, 2, 3, 1, sharper: Arrow(96));
        Assert.Equal(96, CursorShapeFactory.DecodePng(sharp.Png)!.Width);
        Assert.Equal(32, sharp.Width);
        Assert.NotEqual(a.Identity, sharp.Identity); // different drawn size
    }

    [Fact]
    public void A_sharper_copy_must_match_the_aspect_and_be_larger()
    {
        var baseImage = Arrow(32);
        Assert.True(CursorShapeFactory.IsUsableSharper(baseImage, Arrow(64)));
        Assert.False(CursorShapeFactory.IsUsableSharper(baseImage, Arrow(32)));
        Assert.False(CursorShapeFactory.IsUsableSharper(baseImage, new PixelBuffer(64, 40)));
        Assert.False(CursorShapeFactory.IsUsableSharper(baseImage, null));
    }

    private static PixelBuffer Arrow(int size)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(size, size));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(size * 0.1f, size * 0.1f);
        path.LineTo(size * 0.1f, size * 0.8f);
        path.LineTo(size * 0.6f, size * 0.55f);
        path.Close();
        surface.Canvas.DrawPath(path, paint);
        using var image = surface.Snapshot();
        return SkiaConvert.ToPixelBuffer(image);
    }

    private static byte[] Pixels(params byte[][] pixels) => pixels.SelectMany(p => p).ToArray();

    private static (int B, int G, int R, int A) Pixel(PixelBuffer image, int x, int y)
    {
        var o = (y * image.Stride) + (x * 4);
        return (image.Pixels[o], image.Pixels[o + 1], image.Pixels[o + 2], image.Pixels[o + 3]);
    }

    private static SKImage Enlarge(PixelBuffer image, int factor)
    {
        using var source = SkiaConvert.ToImage(image);
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(image.Width * factor, image.Height * factor));
        surface.Canvas.Clear(new SKColor(0x80, 0x80, 0x80));
        surface.Canvas.DrawImage(source, new SKRect(0, 0, image.Width * factor, image.Height * factor), new SKSamplingOptions(SKFilterMode.Nearest));
        return surface.Snapshot();
    }
}
