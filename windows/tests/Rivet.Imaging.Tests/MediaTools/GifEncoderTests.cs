// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Imaging.MediaTools;
using Rivet.Imaging.Skia;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.MediaTools;

public class GifEncoderTests
{
    /// <summary>A moving gradient with a bouncing square: realistic enough for palette and diffing.</summary>
    private static PixelBuffer Frame(int width, int height, int t)
    {
        var info = SkiaConvert.InfoFor(width, height);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        using (var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.MidnightBlue, SKColors.Orange], SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        using (var square = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            canvas.DrawRect(10 + (t * 6), height / 3f, 20, 20, square);
        }

        return SkiaConvert.ToPixelBuffer(surface.Snapshot());
    }

    [Fact]
    public void Frames_delays_and_looping_survive_a_decode()
    {
        var frames = Enumerable.Range(0, 8).Select(t => Frame(120, 80, t)).ToList();
        var palette = GifPalette.Build(frames.Where((_, i) => i % 2 == 0));
        using var stream = new MemoryStream();
        using (var encoder = new GifEncoder(stream, 120, 80, palette, new GifEncoderOptions { Loop = true }))
        {
            foreach (var frame in frames)
            {
                encoder.AddFrame(frame, delayCentiseconds: 8);
            }
        }

        stream.Position = 0;
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Gif, codec.EncodedFormat);
        Assert.Equal(8, codec.FrameCount);
        Assert.All(codec.FrameInfo, f => Assert.Equal(80, f.Duration));
        Assert.Equal(-1, codec.RepetitionCount);
    }

    [Fact]
    public void Playing_once_omits_the_loop_block()
    {
        var frame = Frame(32, 32, 0);
        using var stream = new MemoryStream();
        using (var encoder = new GifEncoder(stream, 32, 32, GifPalette.Build([frame]), new GifEncoderOptions { Loop = false }))
        {
            encoder.AddFrame(frame, 10);
            encoder.AddFrame(Frame(32, 32, 1), 10);
        }

        var bytes = stream.ToArray();
        Assert.DoesNotContain("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        stream.Position = 0;
        using var codec = SKCodec.Create(stream);
        Assert.Equal(2, codec.FrameCount); // parse every frame first: the loop count is known only then
        Assert.Equal(0, codec.RepetitionCount);
    }

    [Fact]
    public void Decoded_pixels_stay_close_to_the_source()
    {
        var frames = Enumerable.Range(0, 4).Select(t => Frame(96, 64, t)).ToList();
        var palette = GifPalette.Build(frames);
        using var stream = new MemoryStream();
        using (var encoder = new GifEncoder(stream, 96, 64, palette))
        {
            foreach (var frame in frames)
            {
                encoder.AddFrame(frame, 5);
            }
        }

        stream.Position = 0;
        using var codec = SKCodec.Create(stream);
        var info = new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        for (var index = 0; index < frames.Count; index++)
        {
            var options = new SKCodecOptions(index, index == 0 ? -1 : index - 1);
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels(), options));
            var decoded = SkiaConvert.ToPixelBuffer(bitmap);
            var source = frames[index];
            double totalError = 0;
            for (var i = 0; i < decoded.Pixels.Length; i += 4)
            {
                totalError += Math.Abs(decoded.Pixels[i] - source.Pixels[i]) + Math.Abs(decoded.Pixels[i + 1] - source.Pixels[i + 1]) + Math.Abs(decoded.Pixels[i + 2] - source.Pixels[i + 2]);
            }

            var meanError = totalError / (96 * 64 * 3);
            Assert.True(meanError < 12, $"frame {index}: mean channel error {meanError:F1}");
        }
    }

    [Fact]
    public void Lzw_round_trips_long_and_repetitive_data()
    {
        var random = new Random(7);
        var data = new byte[200_000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = i % 3000 < 1500 ? (byte)(i % 7) : (byte)random.Next(256);
        }

        using var stream = new MemoryStream();
        GifLzw.Encode(stream, data, 8);
        var decoded = DecodeLzw(stream.ToArray());
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Lzw_handles_tiny_inputs()
    {
        foreach (var length in new[] { 1, 2, 3, 255, 256, 511, 512, 513 })
        {
            var data = Enumerable.Range(0, length).Select(i => (byte)(i * 37)).ToArray();
            using var stream = new MemoryStream();
            GifLzw.Encode(stream, data, 8);
            Assert.Equal(data, DecodeLzw(stream.ToArray()));
        }
    }

    [Fact]
    public void Palettes_have_at_most_255_colours_and_map_exact_colours()
    {
        var frame = Frame(200, 200, 3);
        var palette = GifPalette.Build([frame]);
        Assert.InRange(palette.Colors.Count, 2, 255);
        var small = new GifPalette([(255, 0, 0), (0, 255, 0), (0, 0, 255)]);
        Assert.Equal(0, small.Map(250, 10, 10));
        Assert.Equal(2, small.Map(0, 0, 240));
    }

    /// <summary>A straightforward GIF LZW decoder (giflib logic) for testing the encoder.</summary>
    private static byte[] DecodeLzw(byte[] encoded)
    {
        var minCodeSize = encoded[0];
        var data = new List<byte>();
        var position = 1;
        while (position < encoded.Length)
        {
            var count = encoded[position++];
            if (count == 0)
            {
                break;
            }

            data.AddRange(encoded.AsSpan(position, count).ToArray());
            position += count;
        }

        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var codeSize = minCodeSize + 1;
        var dictionary = new List<byte[]>();
        void Reset()
        {
            dictionary.Clear();
            for (var i = 0; i < clear; i++)
            {
                dictionary.Add([(byte)i]);
            }

            dictionary.Add([]);
            dictionary.Add([]);
            codeSize = minCodeSize + 1;
        }

        Reset();
        var output = new List<byte>();
        var bits = 0;
        var bitCount = 0;
        var index = 0;
        byte[]? previous = null;
        while (true)
        {
            while (bitCount < codeSize && index < data.Count)
            {
                bits |= data[index++] << bitCount;
                bitCount += 8;
            }

            if (bitCount < codeSize)
            {
                break;
            }

            var code = bits & ((1 << codeSize) - 1);
            bits >>= codeSize;
            bitCount -= codeSize;
            if (code == clear)
            {
                Reset();
                previous = null;
                continue;
            }

            if (code == end)
            {
                break;
            }

            byte[] entry;
            if (code < dictionary.Count)
            {
                entry = dictionary[code];
                if (previous is not null && dictionary.Count < 4096)
                {
                    dictionary.Add([.. previous, entry[0]]);
                }
            }
            else
            {
                entry = [.. previous!, previous![0]];
                if (dictionary.Count < 4096)
                {
                    dictionary.Add(entry);
                }
            }

            output.AddRange(entry);
            previous = entry;
            if (dictionary.Count == (1 << codeSize) && codeSize < 12)
            {
                codeSize++;
            }
        }

        return output.ToArray();
    }
}
