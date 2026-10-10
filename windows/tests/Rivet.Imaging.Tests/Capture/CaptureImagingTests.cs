// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Platform;
using Rivet.Imaging.Capture;
using Rivet.Imaging.Skia;
using SkiaSharp;
using Xunit;
using ZXing;
using ZXing.Common;

namespace Rivet.Imaging.Tests.Capture;

/// <summary>PNG density (spec 01 §6.15), 1x export, pixel helpers, QR detection (§3.17) and the overlay renderer.</summary>
public class CaptureImagingTests
{
    private static PixelBuffer Solid(int width, int height, byte r, byte g, byte b, byte a = 255, double scale = 1)
    {
        var buffer = new PixelBuffer(width, height) { Scale = scale };
        for (var i = 0; i < buffer.Pixels.Length; i += 4)
        {
            buffer.Pixels[i] = (byte)(b * a / 255);
            buffer.Pixels[i + 1] = (byte)(g * a / 255);
            buffer.Pixels[i + 2] = (byte)(r * a / 255);
            buffer.Pixels[i + 3] = a;
        }

        return buffer;
    }

    [Fact]
    public void Pngs_carry_96_dpi_times_the_scale_and_read_back()
    {
        var png = CaptureImaging.EncodePng(Solid(40, 20, 10, 20, 30, scale: 1.5));
        Assert.True(PngMetadata.IsPng(png));
        Assert.Equal(144, PngMetadata.ReadDpi(png)!.Value, 0);
        var decoded = CaptureImaging.Decode(png, requireDpi: true)!;
        Assert.Equal((40, 20, 1.5), (decoded.Width, decoded.Height, Math.Round(decoded.Scale, 3)));

        // Replacing an existing pHYs keeps exactly one.
        var twice = PngMetadata.WithDpi(png, 192);
        Assert.Equal(192, PngMetadata.ReadDpi(twice)!.Value, 0);
        Assert.Equal(png.Length, twice.Length);
    }

    [Fact]
    public void A_png_without_valid_density_is_rejected_when_density_is_required()
    {
        using var image = SkiaConvert.ToImage(Solid(8, 8, 1, 2, 3));
        var plain = SkiaConvert.EncodePng(image);
        Assert.Null(PngMetadata.ReadDpi(plain));
        Assert.Null(CaptureImaging.Decode(plain, requireDpi: true));
        Assert.Equal(1, CaptureImaging.Decode(plain)!.Scale);
        Assert.Null(CaptureImaging.Decode(PngMetadata.WithDpi(plain, 20), requireDpi: true)); // 0.21× is out of range
    }

    [Fact]
    public void One_x_export_halves_a_2x_capture_and_reports_scale_1()
    {
        var retina = Solid(400, 300, 200, 100, 50, scale: 2);
        var oneX = CaptureImaging.ToOneX(retina);
        Assert.Equal((200, 150, 1.0), (oneX.Width, oneX.Height, oneX.Scale));
        Assert.Equal(96, PngMetadata.ReadDpi(CaptureImaging.EncodePng(oneX))!.Value, 0);
        Assert.Same(retina, CaptureImaging.ForOutput(retina, downscale: false));

        var fractional = CaptureImaging.ToOneX(Solid(1000, 500, 0, 0, 0, scale: 1.25));
        Assert.Equal((800, 400), (fractional.Width, fractional.Height));
        var plain = Solid(10, 10, 0, 0, 0);
        Assert.Same(plain, CaptureImaging.ToOneX(plain));
    }

    [Fact]
    public void Pixels_read_back_unpremultiplied()
    {
        var translucent = Solid(4, 4, 200, 100, 50, a: 128);
        var argb = CaptureImaging.ReadPixel(translucent, 2, 2);
        Assert.Equal(128u, argb >> 24);
        Assert.InRange((int)((argb >> 16) & 0xFF), 198, 200);
        Assert.InRange((int)((argb >> 8) & 0xFF), 98, 100);
        Assert.Equal(0xFF0A141Eu, CaptureImaging.ReadPixel(Solid(2, 2, 10, 20, 30), 99, 99));
    }

    [Fact]
    public void Crops_are_clamped_and_keep_the_scale()
    {
        var image = Solid(100, 80, 1, 2, 3, scale: 1.75);
        var crop = CaptureImaging.Crop(image, new PixelRect(90, 70, 50, 50))!;
        Assert.Equal((10, 10, 1.75), (crop.Width, crop.Height, crop.Scale));
        Assert.Null(CaptureImaging.Crop(image, new PixelRect(200, 200, 10, 10)));
    }

    [Fact]
    public void Blank_detection_and_opaque_fill()
    {
        Assert.True(CaptureImaging.IsBlank(new PixelBuffer(10, 10)));
        Assert.True(CaptureImaging.IsBlank(Solid(10, 10, 0, 0, 0)));
        Assert.False(CaptureImaging.IsBlank(Solid(10, 10, 0, 0, 1)));
        var transparent = new PixelBuffer(2, 2);
        CaptureImaging.MakeOpaque(transparent);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(255, transparent.Pixels[(i * 4) + 3]));
    }

    [Fact]
    public void Layers_are_composited_at_their_offsets()
    {
        var baseImage = Solid(100, 100, 255, 255, 255);
        var layer = Solid(10, 10, 255, 0, 0);
        var result = CaptureImaging.Composite(baseImage, [(layer, new PixelPoint(20, 30))]);
        Assert.Equal(0xFFFF0000u, CaptureImaging.ReadPixel(result, 25, 35));
        Assert.Equal(0xFFFFFFFFu, CaptureImaging.ReadPixel(result, 5, 5));
    }

    private static PixelBuffer WithCode(string payload, BarcodeFormat format, int left, int top, int size, PixelBuffer? canvas = null)
    {
        var writer = new MultiFormatWriter();
        var matrix = writer.encode(payload, format, size, format == BarcodeFormat.PDF_417 ? size / 3 : size, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 2 });
        canvas ??= Solid(800, 600, 245, 245, 245);
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                var i = ((top + y) * canvas.Stride) + ((left + x) * 4);
                var v = matrix[x, y] ? (byte)0 : (byte)255;
                canvas.Pixels[i] = v;
                canvas.Pixels[i + 1] = v;
                canvas.Pixels[i + 2] = v;
            }
        }

        return canvas;
    }

    [Theory]
    [InlineData(BarcodeFormat.QR_CODE)]
    [InlineData(BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeFormat.AZTEC)]
    [InlineData(BarcodeFormat.PDF_417)]
    public void Two_dimensional_codes_are_detected(BarcodeFormat format)
    {
        var image = WithCode("https://example.com/rivet", format, 100, 100, 240);
        var codes = QrDetector.Detect(image);
        Assert.Single(codes);
        Assert.Equal("https://example.com/rivet", codes[0].Payload);
        Assert.NotNull(QrPayloads.OpenableUrl(codes));
    }

    [Fact]
    public void Striped_ui_with_one_dimensional_barcodes_is_ignored()
    {
        var image = WithCode("123456789012", BarcodeFormat.CODE_128, 50, 50, 300);
        Assert.Empty(QrDetector.Detect(image));
        Assert.Empty(QrDetector.Detect(Solid(300, 200, 255, 255, 255)));
    }

    [Fact]
    public void Several_codes_are_found_in_reading_order()
    {
        var image = WithCode("bottom", BarcodeFormat.QR_CODE, 80, 330, 200);
        WithCode("top", BarcodeFormat.QR_CODE, 450, 40, 200, image);
        var codes = QrDetector.Detect(image);
        Assert.Equal(2, codes.Count);
        Assert.Equal("top\nbottom", QrPayloads.Join(codes, image.Height));
        Assert.Null(QrPayloads.OpenableUrl(codes));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void The_overlay_renders_selection_badge_highlight_and_loupe(double scale)
    {
        var width = (int)(960 * scale);
        var height = (int)(540 * scale);
        var desktop = ScrollStitchingTests.Page(width, height, seed: 11);
        using var background = SkiaConvert.ToImage(desktop);
        var s = scale;
        var scene = new OverlayScene
        {
            Width = width,
            Height = height,
            Scale = scale,
            Background = background,
            Selection = new RectD(120 * s, 100 * s, 360 * s, 220 * s),
            Ghost = new RectD(600 * s, 300 * s, 200 * s, 120 * s),
            Badge = $"{(int)(360 * s)} × {(int)(220 * s)}",
            Loupe = new LoupeScene
            {
                Pointer = new PointD(480 * s, 320 * s),
                Source = background,
                Sample = LoupeMath.SampleRect(new PointD(480 * s, 320 * s), width, height, 13),
                Target = new PixelPoint((int)(480 * s), (int)(320 * s)),
                Color = CaptureImaging.ReadPixel(desktop, (int)(480 * s), (int)(320 * s)),
                Value = "#1E90FF",
            },
        };
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(width, height));
        OverlayRenderer.Draw(surface.Canvas, scene);
        using var snapshot = surface.Snapshot();
        Snapshots.Save(snapshot, $"capture-overlay-render-{scale:0.0}x");

        // The selection is undimmed, the outside is dimmed.
        var rendered = SkiaConvert.ToPixelBuffer(snapshot);
        var inside = CaptureImaging.ReadPixel(rendered, (int)(200 * s), (int)(200 * s));
        Assert.Equal(CaptureImaging.ReadPixel(desktop, (int)(200 * s), (int)(200 * s)), inside);
        var outside = CaptureImaging.ReadPixel(rendered, (int)(40 * s), (int)(500 * s));
        Assert.NotEqual(CaptureImaging.ReadPixel(desktop, (int)(40 * s), (int)(500 * s)), outside);
    }
}
