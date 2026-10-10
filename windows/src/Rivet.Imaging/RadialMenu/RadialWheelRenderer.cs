// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Modules.RadialMenu;
using SkiaSharp;

namespace Rivet.Imaging.RadialMenu;

/// <summary>Everything the wheel needs to draw one frame (animated values included).</summary>
public sealed record RadialWheelFrame
{
    public int Count { get; init; }

    /// <summary>Highlighted slice, or null.</summary>
    public int? Highlight { get; init; }

    /// <summary>Current (animated) wedge centre angle in radians from 12 o'clock, clockwise.</summary>
    public double WedgeAngle { get; init; }

    /// <summary>0…1: the wedge fades in and out in 0.12 s.</summary>
    public double WedgeOpacity { get; init; }

    /// <summary>0…1 open progress (window fade and 0.9 → 1 scale).</summary>
    public double Open { get; init; } = 1;

    /// <summary>Per-chip bloom progress 0…1 (scale 0.35 → 1, radius 8 → ring, opacity 0 → 1).</summary>
    public IReadOnlyList<double>? Bloom { get; init; }

    /// <summary>Per-chip highlight scale (1 → 1.14).</summary>
    public IReadOnlyList<double>? ChipScale { get; init; }

    /// <summary>Profile colour, 0xAARRGGBB.</summary>
    public uint Color { get; init; } = 0xFF0067C0;

    public bool Dark { get; init; }

    public bool HighContrast { get; init; }

    /// <summary>Use an opaque disc (Windows "transparency effects" off).</summary>
    public bool SolidDisc { get; init; }

    /// <summary>Geometry set: the 400 DIP wheel or the smaller settings canvas.</summary>
    public RadialWheelSize Size { get; init; } = RadialWheelSize.Wheel;
}

public sealed record RadialWheelSize(double Disc, double Ring, double Chip, double Hub, double DeadZone, double WedgeInner, double WedgeOuter, double GuideInner, double GuideOuter)
{
    public static RadialWheelSize Wheel { get; } = new(
        RadialGeometry.DiscDiameter, RadialGeometry.ChipRingRadius, RadialGeometry.ChipSize, RadialGeometry.HubDiameter,
        RadialGeometry.DeadZone, RadialGeometry.WedgeInnerRadius, RadialGeometry.WedgeOuterRadius, RadialGeometry.GuideInnerRadius, RadialGeometry.GuideOuterRadius);

    /// <summary>The settings canvas wheel: disc 250, ring 92, chips 44, hub 68, dead zone 36 (other radii scaled).</summary>
    public static RadialWheelSize Canvas { get; } = new(
        RadialGeometry.CanvasDisc, RadialGeometry.CanvasRing, RadialGeometry.CanvasChip, RadialGeometry.CanvasHub,
        RadialGeometry.CanvasDeadZone, RadialGeometry.CanvasDeadZone, 146 * 250 / 300.0, 47 * 250 / 300.0, 143 * 250 / 300.0);
}

/// <summary>
/// Draws the radial menu's disc, slice guides, highlight wedge, chips and hub
/// (spec 07 §3.2.10, §6.2) with SkiaSharp. Glyphs, images and labels are laid
/// over it by the UI at <see cref="ChipCenter"/>. Coordinates are DIPs with
/// the wheel centred at (cx, cy).
/// </summary>
public static class RadialWheelRenderer
{
    public static void Draw(SKCanvas canvas, float cx, float cy, RadialWheelFrame frame)
    {
        var size = frame.Size;
        var open = (float)Math.Clamp(frame.Open, 0, 1);
        var scale = 0.9f + (0.1f * open);
        var save = canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(scale, scale);

        var discRadius = (float)(size.Disc / 2);
        var alpha = (byte)Math.Round(255 * open);

        // Shadow: black 22 % (light) / 55 % (dark), blur 24, 8 down.
        using (var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black.WithAlpha((byte)Math.Round((frame.Dark ? 0.55 : 0.22) * alpha)),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12),
        })
        {
            canvas.DrawCircle(0, 8, discRadius, shadow);
        }

        // Disc tint. Without the macOS blur material the tint is made more opaque for legibility.
        var tint = frame.Dark
            ? new SKColor(24, 24, 26, (byte)Math.Round((frame.SolidDisc ? 1.0 : 0.9) * alpha))
            : new SKColor(250, 250, 252, (byte)Math.Round((frame.SolidDisc ? 1.0 : 0.92) * alpha));
        using (var disc = new SKPaint { IsAntialias = true, Color = tint })
        {
            canvas.DrawCircle(0, 0, discRadius, disc);
        }

        // Rim: 1.2 gradient, white 95 % → 12 % (light) or 30 % → 4 % (dark), top to bottom.
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f })
        {
            var top = SKColors.White.WithAlpha((byte)Math.Round((frame.Dark ? 0.30 : 0.95) * alpha));
            var bottom = SKColors.White.WithAlpha((byte)Math.Round((frame.Dark ? 0.04 : 0.12) * alpha));
            rim.Shader = SKShader.CreateLinearGradient(new SKPoint(0, -discRadius), new SKPoint(0, discRadius), [top, bottom], SKShaderTileMode.Clamp);
            canvas.DrawCircle(0, 0, discRadius - 1.2f, rim);
        }

        // Border hairline: 0.8, black 9 % (light) / white 11 % (dark); stronger with high contrast.
        using (var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = frame.HighContrast ? 1.5f : 0.8f })
        {
            var strength = frame.HighContrast ? (frame.Dark ? 0.28 : 0.24) : (frame.Dark ? 0.11 : 0.09);
            border.Color = (frame.Dark ? SKColors.White : SKColors.Black).WithAlpha((byte)Math.Round(strength * alpha));
            canvas.DrawCircle(0, 0, discRadius - 0.4f, border);
        }

        var count = frame.Count;
        var profile = new SKColor(frame.Color);

        // Highlight wedge: radial gradient of the profile colour under the highlighted slice.
        if (count > 0 && frame.WedgeOpacity > 0.001)
        {
            var step = (float)(360.0 / count);
            var centre = (float)(frame.WedgeAngle * 180 / Math.PI);
            var inner = (float)size.WedgeInner;
            var outer = (float)size.WedgeOuter;
            using var path = new SKPath();
            var start = centre - (step / 2) - 90; // Skia measures from 3 o'clock
            path.ArcTo(new SKRect(-outer, -outer, outer, outer), start, step, forceMoveTo: true);
            path.ArcTo(new SKRect(-inner, -inner, inner, inner), start + step, -step, forceMoveTo: false);
            path.Close();
            var wedgeAlpha = frame.WedgeOpacity * open;
            var near = profile.WithAlpha((byte)Math.Round((frame.Dark ? 0.08 : 0.05) * 255 * wedgeAlpha));
            var far = profile.WithAlpha((byte)Math.Round((frame.Dark ? 0.30 : 0.20) * 255 * wedgeAlpha));
            using var wedge = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(SKPoint.Empty, (float)(size.WedgeOuter + 4), [near, far], [inner / (float)(size.WedgeOuter + 4), 1f], SKShaderTileMode.Clamp),
            };
            canvas.DrawPath(path, wedge);
        }

        // Slice guides: hairlines at the slice boundaries.
        if (count > 1)
        {
            using var guide = new SKPaint
            {
                IsAntialias = true,
                StrokeWidth = 1,
                Color = (frame.Dark ? SKColors.White : SKColors.Black).WithAlpha((byte)Math.Round(0.07 * alpha)),
            };
            for (var i = 0; i < count; i++)
            {
                var angle = 2 * Math.PI * (i + 0.5) / count;
                var (sx, sy) = Polar(angle, size.GuideInner);
                var (ex, ey) = Polar(angle, size.GuideOuter);
                canvas.DrawLine(sx, sy, ex, ey, guide);
            }
        }

        // Chips.
        for (var i = 0; i < count; i++)
        {
            var bloom = frame.Bloom is { } b && i < b.Count ? Math.Clamp(b[i], 0, 1.2) : 1.0;
            var chipScale = frame.ChipScale is { } s && i < s.Count ? s[i] : 1.0;
            var highlighted = frame.Highlight == i;
            var (x, y) = ChipCenter(i, count, size.Ring * bloom + (8 * (1 - Math.Min(1, bloom))), 0, 0);
            var radius = (float)(size.Chip / 2 * chipScale * (0.35 + (0.65 * Math.Min(1, bloom))));
            var chipAlpha = Math.Min(1, bloom) * open;

            if (highlighted)
            {
                // Glow: radius 13, 4 down, 45 % of the profile colour.
                using var glow = new SKPaint
                {
                    IsAntialias = true,
                    Color = profile.WithAlpha((byte)Math.Round(0.45 * 255 * chipAlpha)),
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6.5f),
                };
                canvas.DrawCircle((float)x, (float)y + 4, radius, glow);
            }

            var fill = highlighted
                ? profile.WithAlpha((byte)Math.Round(255 * chipAlpha))
                : SKColors.White.WithAlpha((byte)Math.Round((frame.Dark ? 0.14 : 0.88) * 255 * chipAlpha));
            using (var chip = new SKPaint { IsAntialias = true, Color = fill })
            {
                canvas.DrawCircle((float)x, (float)y, radius, chip);
            }

            if (!highlighted && !frame.Dark)
            {
                using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, Color = SKColors.Black.WithAlpha((byte)Math.Round(0.08 * 255 * chipAlpha)) };
                canvas.DrawCircle((float)x, (float)y, radius, edge);
            }
        }

        // Hub.
        using (var hub = new SKPaint
        {
            IsAntialias = true,
            Color = frame.Dark ? SKColors.White.WithAlpha((byte)Math.Round(0.08 * alpha)) : SKColors.Black.WithAlpha((byte)Math.Round(0.04 * alpha)),
        })
        {
            canvas.DrawCircle(0, 0, (float)(size.Hub / 2), hub);
        }

        canvas.RestoreToCount(save);
    }

    /// <summary>Centre of chip i (DIPs, y down) for a wheel centred at (cx, cy).</summary>
    public static (double X, double Y) ChipCenter(int index, int count, double ring, double cx, double cy)
    {
        var (x, yUp) = RadialGeometry.ChipOffset(index, count, ring);
        return (cx + x, cy - yUp);
    }

    private static (float X, float Y) Polar(double angle, double radius) =>
        ((float)(radius * Math.Sin(angle)), (float)(-radius * Math.Cos(angle)));
}
