// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Modules.MediaTools;
using Rivet.Imaging.MediaTools;
using Rivet.Imaging.Skia;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.MediaTools;

public sealed class MediaImageProcessorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-media-image-" + Guid.NewGuid().ToString("N"));

    public MediaImageProcessorTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Photo(int width, int height, string name = "photo.png", SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(width, height));
        var canvas = surface.Canvas;
        using (var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.Teal, SKColors.Gold], SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        // A red marker in the top-left corner to check orientation.
        using (var red = new SKPaint { Color = SKColors.Red })
        {
            canvas.DrawRect(0, 0, width / 4f, height / 4f, red);
        }

        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, SkiaConvert.Encode(surface.Snapshot(), format, 95));
        return path;
    }

    [Fact]
    public void Resizes_converts_and_draws_a_watermark()
    {
        var input = Photo(1600, 1000);
        var output = Path.Combine(_folder, "out.jpg");
        var options = ImageOptions.Default with
        {
            Resize = new ImageResize(ImageResizeKind.MaxDimension, MaxDimension: 800),
            Watermark = new WatermarkOptions(WatermarkKind.Text, "© Rivet", Position: WatermarkPosition.BottomRight, Opacity: 0.9),
        };
        var size = MediaImageProcessor.Process(input, output, options, logo: null);
        Assert.Equal(new MediaSize(800, 500), size);
        using var codec = SKCodec.Create(output);
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(800, codec.Info.Width);
        using var image = SKImage.FromEncodedData(output);
        Snapshots.Save(image, "media-image-watermark");
    }

    [Fact]
    public void Pdf_output_embeds_the_jpeg_at_pixel_size()
    {
        var input = Photo(300, 200);
        var output = Path.Combine(_folder, "out.pdf");
        MediaImageProcessor.Process(input, output, MediaImagePresets.Docs with { Resize = new ImageResize(ImageResizeKind.None) }, null);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(output));
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.Contains("/MediaBox [0 0 300 200]", text, StringComparison.Ordinal);
        Assert.Contains("/Filter /DCTDecode", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith("xref", text[startxref..], StringComparison.Ordinal);
    }

    [Fact]
    public void Png_keeps_transparency_and_webp_is_written()
    {
        var input = Photo(64, 64);
        var png = Path.Combine(_folder, "out.png");
        MediaImageProcessor.Process(input, png, ImageOptions.Default with { Format = ImageOutputFormat.Png }, null);
        using (var codec = SKCodec.Create(png))
        {
            Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        }

        var webp = Path.Combine(_folder, "out.webp");
        MediaImageProcessor.Process(input, webp, ImageOptions.Default with { Format = ImageOutputFormat.WebP }, null);
        using var webpCodec = SKCodec.Create(webp);
        Assert.Equal(SKEncodedImageFormat.Webp, webpCodec.EncodedFormat);
    }

    [Fact]
    public void Too_large_targets_are_refused()
    {
        var input = Photo(100, 100);
        var ex = Assert.Throws<MediaImageProcessor.ProcessException>(() =>
            MediaImageProcessor.Process(input, Path.Combine(_folder, "x.png"), ImageOptions.Default with { Resize = new ImageResize(ImageResizeKind.Exact, Width: 9000, Height: 9000) }, null));
        Assert.Equal("tooLarge", ex.Message);
    }

    [Fact]
    public void Unsupported_files_are_reported()
    {
        var bogus = Path.Combine(_folder, "notes.jpg");
        File.WriteAllText(bogus, "not an image");
        var ex = Assert.Throws<MediaImageProcessor.ProcessException>(() => MediaImageProcessor.Process(bogus, Path.Combine(_folder, "y.jpg"), ImageOptions.Default, null));
        Assert.Equal("unsupported", ex.Message);
    }

    [Theory]
    [InlineData(SKEncodedOrigin.RightTop)]
    [InlineData(SKEncodedOrigin.LeftBottom)]
    [InlineData(SKEncodedOrigin.BottomRight)]
    [InlineData(SKEncodedOrigin.TopRight)]
    [InlineData(SKEncodedOrigin.LeftTop)]
    [InlineData(SKEncodedOrigin.RightBottom)]
    public void Orientation_transforms_move_the_corner_marker(SKEncodedOrigin origin)
    {
        // Source 40×20 with a red pixel at (0,0).
        using var source = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.Erase(SKColors.Blue);
        source.SetPixel(0, 0, SKColors.Red);
        using var upright = MediaImageProcessor.ApplyOrigin(source, origin);
        var swap = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
        Assert.Equal(swap ? 20 : 40, upright.Width);
        // Where the stored (0,0) pixel must land after the EXIF transform.
        var (x, y) = origin switch
        {
            SKEncodedOrigin.TopRight => (39, 0),
            SKEncodedOrigin.BottomRight => (39, 19),
            SKEncodedOrigin.BottomLeft => (0, 19),
            SKEncodedOrigin.LeftTop => (0, 0),
            SKEncodedOrigin.RightTop => (19, 0),
            SKEncodedOrigin.RightBottom => (19, 39),
            SKEncodedOrigin.LeftBottom => (0, 39),
            _ => (0, 0),
        };
        Assert.Equal(SKColors.Red, upright.GetPixel(x, y));
    }

    [Fact]
    public void Exif_is_copied_with_orientation_reset_and_new_dimensions()
    {
        // Minimal big-endian Exif: IFD0 with Orientation=6 and an ExifIFD pointer; ExifIFD with PixelX/YDimension (LONG).
        var tiff = new List<byte>();
        tiff.AddRange("MM"u8.ToArray());
        tiff.AddRange([0x00, 0x2A, 0x00, 0x00, 0x00, 0x08]);
        tiff.AddRange([0x00, 0x02]); // 2 entries
        tiff.AddRange([0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00]);
        tiff.AddRange([0x87, 0x69, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x26]);
        tiff.AddRange([0x00, 0x00, 0x00, 0x00]); // next IFD
        tiff.AddRange([0x00, 0x02]);
        tiff.AddRange([0xA0, 0x02, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x10, 0x00]);
        tiff.AddRange([0xA0, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x0C, 0x00]);
        tiff.AddRange([0x00, 0x00, 0x00, 0x00]);
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var app1 = new byte[] { 0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF) }.Concat(payload).ToArray();
        var jpeg = new byte[] { 0xFF, 0xD8 }.Concat(app1).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();

        var extracted = JpegExif.ExtractApp1(jpeg);
        Assert.NotNull(extracted);
        var target = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var merged = JpegExif.Insert(target, extracted!, 800, 600);
        var copied = JpegExif.ExtractApp1(merged)!;
        // Orientation value (big-endian SHORT at entry + 8).
        Assert.Equal(0x00, copied[10 + 8 + 2 + 8]);
        Assert.Equal(0x01, copied[10 + 8 + 2 + 9]);
        var width = (copied[10 + 0x26 + 2 + 8 + 2] << 8) | copied[10 + 0x26 + 2 + 8 + 3];
        Assert.Equal(800, width);
    }
}
