// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.App.Shell;

/// <summary>
/// Draws the tray glyph at every DPI size. Monochrome glyphs follow the
/// taskbar theme (white on a dark taskbar, near-black on a light one); a
/// tint replaces that colour and a badge adds a red dot.
/// </summary>
public static class TrayIconRenderer
{
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48];

    public static IReadOnlyList<PixelBuffer> Render(bool lightTaskbar, uint? tint = null, bool badge = false) =>
        Sizes.Select(size => RenderOne(size, lightTaskbar, tint, badge)).ToList();

    public static PixelBuffer RenderOne(int size, bool lightTaskbar, uint? tint, bool badge)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(size, size));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var color = tint is { } t ? new SKColor(t) : lightTaskbar ? new SKColor(0x1C, 0x1C, 0x1C) : SKColors.White;
        var center = size / 2f;
        var ring = Math.Max(1.5f, size * 0.13f);
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = ring, Color = color };
        canvas.DrawCircle(center, center, (size / 2f) - (ring / 2f) - (size * 0.04f), stroke);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        canvas.DrawCircle(center, center, size * 0.19f, fill);

        if (badge)
        {
            var r = size * 0.2f;
            using var clear = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear };
            canvas.DrawCircle(size - r, size - r, r + Math.Max(1, size * 0.06f), clear);
            using var red = new SKPaint { IsAntialias = true, Color = new SKColor(0xE8, 0x3B, 0x30) };
            canvas.DrawCircle(size - r, size - r, r, red);
        }

        using var image = surface.Snapshot();
        return SkiaConvert.ToPixelBuffer(image);
    }
}
