// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Imaging.ScreenshotEditor;
using SkiaSharp;

namespace Rivet.Imaging.Tests.ScreenshotEditor;

/// <summary>Synthetic captures for renderer tests: a window with a title bar, text lines and a photo-like panel.</summary>
internal static class EditorSampleImages
{
    public static SKImage Window(int width, int height, bool translucent = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(translucent ? new SKColor(255, 255, 255, 140) : new SKColor(0xF7, 0xF7, 0xF9));
        using var bar = new SKPaint { Color = new SKColor(0xE6, 0xE6, 0xEB) };
        c.DrawRect(0, 0, width, 36, bar);
        using var dot = new SKPaint { IsAntialias = true };
        var colors = new[] { new SKColor(0xFF, 0x5F, 0x57), new SKColor(0xFE, 0xBC, 0x2E), new SKColor(0x28, 0xC8, 0x40) };
        for (var i = 0; i < 3; i++)
        {
            dot.Color = colors[i];
            c.DrawCircle(20 + (i * 20), 18, 6, dot);
        }

        var fonts = AnnotationFonts.Shared;
        using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0x22, 0x22, 0x28) };
        string[] lines =
        [
            "Invoice #2026-0419 for Rivet contributors",
            "Account: 1234 5678 9012 3456",
            "Contact: someone@example.com",
            "Total due: 1,280.00 EUR",
            "Ship to: 42 Example Street, Springfield",
        ];
        var y = 56f;
        foreach (var line in lines)
        {
            fonts.DrawTopLeft(c, line, 24, y, 18, fonts.Semibold, ink);
            y += 30;
        }

        // A gradient "photo" panel on the right.
        using var photo = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(width * 0.6f, 60), new SKPoint(width - 20, height - 20),
                [new SKColor(0x3A, 0x7B, 0xD5), new SKColor(0x00, 0xD2, 0xFF)], SKShaderTileMode.Clamp),
        };
        c.DrawRoundRect(SKRect.Create(width * 0.6f, 60, (width * 0.4f) - 20, height - 80), 10, 10, photo);
        return surface.Snapshot();
    }

    /// <summary>A flat colour (erase must reproduce it exactly).</summary>
    public static SKImage Flat(int width, int height, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    /// <summary>A horizontal gradient: column x has grey value x mod 256 scaled across the width.</summary>
    public static SKImage HorizontalGradient(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var x = 0; x < width; x++)
        {
            var v = (byte)Math.Round(x * 255.0 / (width - 1));
            for (var y = 0; y < height; y++)
            {
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        }

        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    public static SKImage Sheet(IReadOnlyList<SKImage> images, int columns, SKColor background, int gap = 16)
    {
        var rows = (images.Count + columns - 1) / columns;
        var cellW = images.Max(i => i.Width);
        var cellH = images.Max(i => i.Height);
        using var surface = SKSurface.Create(new SKImageInfo((columns * cellW) + ((columns + 1) * gap), (rows * cellH) + ((rows + 1) * gap)));
        surface.Canvas.Clear(background);
        for (var i = 0; i < images.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            surface.Canvas.DrawImage(images[i], gap + (col * (cellW + gap)), gap + (row * (cellH + gap)));
        }

        return surface.Snapshot();
    }
}
