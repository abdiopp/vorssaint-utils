// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Imaging.RecordingEditor;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.RecordingEditor;

public class GifTests
{
    private static PixelBuffer Flat(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var buffer = new PixelBuffer(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = color(x, y);
                var o = (y * buffer.Stride) + (x * 4);
                buffer.Pixels[o] = b;
                buffer.Pixels[o + 1] = g;
                buffer.Pixels[o + 2] = r;
                buffer.Pixels[o + 3] = 255;
            }
        }

        return buffer;
    }

    [Fact]
    public void Few_colours_round_trip_losslessly_with_loop_and_delays()
    {
        var a = Flat(37, 21, (x, y) => ((byte)(x % 8 * 30), (byte)(y % 4 * 60), (byte)((x + y) % 2 == 0 ? 255 : 0)));
        var b = Flat(37, 21, (x, _) => x < 18 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));
        var encoder = new GifEncoder(37, 21, loopCount: 0);
        encoder.AddFrame(a, 8, dither: false);
        encoder.AddFrame(b, 13, dither: true);
        var bytes = encoder.Finish();

        var decoded = GifDecoder.Decode(bytes)!;
        Assert.Equal((37, 21, 0), (decoded.Width, decoded.Height, decoded.LoopCount));
        Assert.Equal(2, decoded.Frames.Count);
        Assert.Equal([8, 13], decoded.Frames.Select(f => f.DelayCentiseconds));
        Assert.Equal(Opaque(a), decoded.Frames[0].Bgra);
        Assert.Equal(Opaque(b), decoded.Frames[1].Bgra);
        Assert.True(GifDecoder.LooksLikeGif(bytes, out var count) && count == 2);
    }

    [Fact]
    public void Many_colours_compress_and_stay_close()
    {
        // A smooth gradient with far more than 256 colours exercises median cut, dithering and long LZW runs.
        var gradient = Flat(320, 200, (x, y) => ((byte)(x * 255 / 319), (byte)(y * 255 / 199), (byte)(((x * 3) + y) % 256)));
        foreach (var dither in new[] { false, true })
        {
            var encoder = new GifEncoder(320, 200);
            encoder.AddFrame(gradient, 8, dither);
            var bytes = encoder.Finish();
            var frame = GifDecoder.Decode(bytes)!.Frames.Single();
            Assert.True(MeanError(gradient.Pixels, frame.Bgra) < 12, $"mean error with dither={dither}");
            Assert.True(bytes.Length < 320 * 200 * 2);
        }
    }

    [Fact]
    public void Long_runs_and_table_resets_decode_exactly()
    {
        // 256 colours in a pattern long enough to fill the 4096-entry table several times.
        var noise = new Random(3);
        var palette = Enumerable.Range(0, 256).Select(i => ((byte)i, (byte)(255 - i), (byte)((i * 7) % 256))).ToArray();
        var buffer = Flat(400, 300, (_, _) => palette[noise.Next(256)]);
        var encoder = new GifEncoder(400, 300);
        encoder.AddFrame(buffer, 4, dither: false);
        var decoded = GifDecoder.Decode(encoder.Finish())!.Frames.Single();
        Assert.Equal(Opaque(buffer), decoded.Bgra);
    }

    [Fact]
    public void Not_a_gif_is_refused()
    {
        Assert.False(GifDecoder.LooksLikeGif("\x89PNG\r\n\x1a\nrest"u8, out _));
        Assert.Null(GifDecoder.Decode([1, 2, 3]));
        var headerOnly = new GifEncoder(4, 4).Finish();
        Assert.False(GifDecoder.LooksLikeGif(headerOnly, out var frames));
        Assert.Equal(0, frames);
    }

    private static byte[] Opaque(PixelBuffer buffer)
    {
        var copy = (byte[])buffer.Pixels.Clone();
        for (var i = 3; i < copy.Length; i += 4)
        {
            copy[i] = 255;
        }

        return copy;
    }

    private static double MeanError(byte[] a, byte[] b)
    {
        double sum = 0;
        var n = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            n += 3;
        }

        return sum / n;
    }
}
