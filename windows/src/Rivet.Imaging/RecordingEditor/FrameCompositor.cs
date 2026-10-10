// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.Backdrop;
using SkiaSharp;

namespace Rivet.Imaging.RecordingEditor;

/// <summary>
/// The one frame renderer for the preview and the export (spec 02 §6.13).
/// Per frame, from source pixels to the canvas: blurs on the raw picture,
/// click ring then pointer drawn into the picture (so zoom magnifies them),
/// the zoom viewport, the fit into the card, the background plate through the
/// rounded mask, image overlays, then captions on top. Source frames may be
/// decoded smaller than the recording; everything measured in source pixels
/// (blur cells, cursor sizes) scales with them.
/// </summary>
public sealed class FrameCompositor : IDisposable
{
    private readonly CompositorAssets _assets;
    private readonly SKImage? _plate;

    public FrameCompositor(FramePlan plan, CompositorAssets assets)
    {
        Plan = plan;
        _assets = assets;
        _plate = BuildPlate();
    }

    public FramePlan Plan { get; }

    public int Width => Plan.Layout.OutputWidth;

    public int Height => Plan.Layout.OutputHeight;

    /// <summary>Draws the frame at <paramref name="editedTime"/> (output clock, 1×) onto a canvas at output size.</summary>
    public void Render(SKCanvas canvas, SKImage source, double editedTime)
    {
        var layout = Plan.Layout;
        var state = Plan.StateAt(editedTime);
        canvas.Save();
        canvas.ClipRect(SKRect.Create(Width, Height));
        if (_plate is not null)
        {
            canvas.DrawImage(_plate, 0, 0);
        }
        else
        {
            canvas.Clear(SKColors.Black);
        }

        var card = SKRect.Create(layout.Card.X, layout.Card.Y, layout.Card.Width, layout.Card.Height);
        canvas.Save();
        if (layout.CornerRadius > 0)
        {
            canvas.ClipRoundRect(new SKRoundRect(card, layout.CornerRadius), SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            canvas.ClipRect(card);
        }

        var matrix = SourceMatrix(source.Width, source.Height, state);
        canvas.Concat(matrix);
        canvas.DrawImage(source, 0, 0, SamplingFor(matrix));
        DrawBlurs(canvas, source, editedTime);
        DrawPointer(canvas, source, state);
        canvas.Restore();

        DrawImages(canvas, state.PlanIndex);
        DrawCaptions(canvas, state.PlanIndex);
        canvas.Restore();
    }

    /// <summary>Renders into a new raster image of the output size.</summary>
    public SKImage RenderImage(SKImage source, double editedTime)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Render(surface.Canvas, source, editedTime);
        return surface.Snapshot();
    }

    /// <summary>Renders straight into a BGRA buffer of the output size (the export path, no extra copy).</summary>
    public void RenderInto(PixelBuffer target, SKImage source, double editedTime)
    {
        if (target.Width != Width || target.Height != Height)
        {
            throw new ArgumentException("Target size differs from the output size.", nameof(target));
        }

        var handle = GCHandle.Alloc(target.Pixels, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, handle.AddrOfPinnedObject(), target.Stride)
                                ?? throw new InvalidOperationException("Could not create the frame surface.");
            Render(surface.Canvas, source, editedTime);
            surface.Flush();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Decoded source pixels → output pixels: zoom viewport, then the card.</summary>
    public SKMatrix SourceMatrix(int sourceWidth, int sourceHeight, FrameState state)
    {
        var card = Plan.Layout.Card;
        var z = state.Zoom > 1.001 ? state.Zoom : 1;
        var (ox, oy, _) = z > 1 ? CameraMotion.Viewport(z, state.TravelX, state.TravelY) : (0, 0, 1);
        var sx = (float)(z * card.Width / sourceWidth);
        var sy = (float)(z * card.Height / sourceHeight);
        var matrix = SKMatrix.CreateTranslation((float)(-ox * sourceWidth), (float)(-oy * sourceHeight));
        matrix = matrix.PostConcat(SKMatrix.CreateScale(sx, sy));
        return matrix.PostConcat(SKMatrix.CreateTranslation(card.X, card.Y));
    }

    private static SKSamplingOptions SamplingFor(SKMatrix matrix)
    {
        var scale = Math.Abs(matrix.ScaleX);
        if (Math.Abs(scale - 1) < 0.001 && Math.Abs(matrix.TransX - MathF.Round(matrix.TransX)) < 0.001 && Math.Abs(matrix.TransY - MathF.Round(matrix.TransY)) < 0.001)
        {
            return new SKSamplingOptions(SKFilterMode.Nearest);
        }

        return scale < 0.75
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Linear);
    }

    private void DrawBlurs(SKCanvas canvas, SKImage source, double editedTime)
    {
        var layout = Plan.Layout;
        var decodeScale = source.Width / (double)layout.SourceWidth;
        foreach (var blur in Plan.Document.Blurs)
        {
            if (!Plan.BlurActive(blur, editedTime))
            {
                continue;
            }

            var full = BlurMath.PixelRect(blur, layout.SourceWidth, layout.SourceHeight);
            var rect = BlurMath.PixelRect(blur, source.Width, source.Height);
            if (full is null || rect is null)
            {
                continue;
            }

            var block = Math.Max(2, RecorderMath.RoundToInt(BlurMath.BlockSize(full.Value.Width, full.Value.Height, blur.Strength) * decodeScale));
            var r = rect.Value;
            using var hidden = PrivacyBlur.Render(source, new SKRectI(r.X, r.Y, r.Right, r.Bottom), block);
            if (hidden is not null)
            {
                canvas.DrawImage(hidden, SKRect.Create(r.X, r.Y, r.Width, r.Height), new SKSamplingOptions(SKFilterMode.Linear));
            }
            else
            {
                // Never show what a blur must hide: fall back to an opaque cover.
                using var cover = new SKPaint { Color = SKColors.Black };
                canvas.DrawRect(SKRect.Create(r.X, r.Y, r.Width, r.Height), cover);
            }
        }
    }

    private void DrawPointer(SKCanvas canvas, SKImage source, FrameState state)
    {
        if (!state.PointerVisible)
        {
            return;
        }

        var layout = Plan.Layout;
        var metrics = Plan.Metrics;
        var decodeScale = source.Width / (double)layout.SourceWidth;
        var size = Plan.Document.PointerSize;
        var x = (float)(state.PointerX * source.Width);
        var y = (float)(state.PointerY * source.Height);
        var opacity = (float)Math.Clamp(state.PointerOpacity, 0, 1);

        if (state.RingProgress is { } p)
        {
            var eased = 1 - Math.Pow(1 - p, 3);
            var unit = metrics.PointerPixelSize(size) / 34 * decodeScale;
            var radius = ClickTimeline.RingRadius * unit * eased;
            var width = Math.Max(0.6, 2.4 * (1 - p)) * unit;
            var alpha = 0.35 * (1 - (p * p));
            if (radius > 1 && alpha > 0.01)
            {
                using var ring = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)width,
                    Color = new SKColor(255, 255, 255, (byte)Math.Round(alpha * 255)),
                };
                canvas.DrawCircle(x, y, (float)radius, ring);
            }
        }

        var press = state.PressScale;
        var shapes = _assets.Pointer.Shapes;
        var index = state.ShapeIndex < shapes.Count ? state.ShapeIndex : 0;
        var image = _assets.Cursor(index);
        if (image is null && index != 0)
        {
            index = 0;
            image = _assets.Cursor(0);
        }

        if (image is not null)
        {
            var shape = shapes[index];
            var scale = metrics.ShapeScale * size * press * decodeScale;
            var w = (float)(shape.Width * scale);
            var h = (float)(shape.Height * scale);
            var hx = (float)(Math.Clamp(shape.HotX, 0, shape.Width) * scale);
            var hy = (float)(Math.Clamp(shape.HotY, 0, shape.Height) * scale);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)) };
            canvas.DrawImage(image, SKRect.Create(x - hx, y - hy, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            return;
        }

        DrawFallbackArrow(canvas, x, y, metrics.ArrowScale * size * press * decodeScale, opacity);
    }

    /// <summary>The vector arrow used when no cursor image was captured (§6.11); it fades with the pointer.</summary>
    public static void DrawFallbackArrow(SKCanvas canvas, float x, float y, double scale, float opacity)
    {
        using var path = new SKPath();
        path.MoveTo(4.5f, 4.0f);
        path.LineTo(4.5f, 31.5f);
        path.LineTo(10.5f, 25.5f);
        path.LineTo(14.5f, 35.5f);
        path.LineTo(18.5f, 33.8f);
        path.LineTo(14.6f, 24.0f);
        path.LineTo(22.5f, 23.5f);
        path.Close();

        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale((float)scale);
        canvas.Translate(-(float)PointerMetrics.FallbackHotX, -(float)PointerMetrics.FallbackHotY);
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        using var layer = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
        canvas.SaveLayer(layer);
        using (var outline = new SKPaint
               {
                   IsAntialias = true,
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 2.4f,
                   StrokeJoin = SKStrokeJoin.Round,
                   Color = SKColors.White,
                   ImageFilter = SKImageFilter.CreateDropShadow(0, 0.8f, 1.1f, 1.1f, new SKColor(0, 0, 0, 89)),
               })
        {
            canvas.DrawPath(path, outline);
        }

        using (var fill = new SKPaint { IsAntialias = true, Color = SKColors.Black })
        {
            canvas.DrawPath(path, fill);
        }

        canvas.Restore();
        canvas.Restore();
    }

    private void DrawImages(SKCanvas canvas, int planIndex)
    {
        double w = Width, h = Height;
        foreach (var overlay in Plan.Document.Images)
        {
            var opacity = Plan.ImageOpacity(overlay, planIndex);
            if (opacity <= 0.01)
            {
                continue;
            }

            (int Width, int Height) drawn = default;
            var image = _assets.Overlay(overlay.Path, (nw, nh) => drawn = OverlayLayout.ImageSize(nw, nh, overlay.Size, w, h));
            if (image is null)
            {
                continue;
            }

            var (x, y) = OverlayLayout.Place(overlay.Anchor, w, h, drawn.Width, drawn.Height);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255)) };
            canvas.DrawImage(image, SKRect.Create((float)Math.Round(x), (float)Math.Round(y), drawn.Width, drawn.Height), new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
    }

    private void DrawCaptions(SKCanvas canvas, int planIndex)
    {
        double w = Width, h = Height;
        foreach (var text in Plan.Document.Texts)
        {
            if (string.IsNullOrWhiteSpace(text.Text))
            {
                continue;
            }

            var opacity = Plan.TextOpacity(text, planIndex);
            if (opacity <= 0.01)
            {
                continue;
            }

            var align = EditNames.Unit(text.Anchor).X switch
            {
                0 => SKTextAlign.Left,
                1 => SKTextAlign.Right,
                _ => SKTextAlign.Center,
            };
            var raster = _assets.Caption(text.Text, OverlayLayout.CaptionFontSize(h, text.Size), EditNames.Color(text.Palette), align);
            if (raster.Image is null)
            {
                continue;
            }

            var (x, y) = OverlayLayout.Place(text.Anchor, w, h, raster.Width, raster.Height);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255)) };
            canvas.DrawImage(raster.Image, (float)Math.Round(x), (float)Math.Round(y), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
    }

    /// <summary>
    /// §6.12 plate, once per plan at the output size: black, then the background
    /// (blurred if asked), then the card's shadow.
    /// </summary>
    private SKImage? BuildPlate()
    {
        var layout = Plan.Layout;
        if (!layout.NeedsPlate && !layout.HasShadow)
        {
            return null;
        }

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var style = Plan.Document.BackdropStyle;
        if (style.HasBackdrop)
        {
            SKImage? image = null;
            if (style.Kind == RecorderBackdropKind.Image && style.ImagePath is { } path)
            {
                image = _assets.Backdrop(path);
            }

            if (style.Kind != RecorderBackdropKind.Image || image is not null)
            {
                BackdropRenderer.DrawPlate(canvas, ToBackdropStyle(style), image, new BackdropGeometry(0, 0, Width, Height));
            }
        }

        if (layout.HasShadow)
        {
            var card = SKRect.Create(layout.Card.X, layout.Card.Y, layout.Card.Width, layout.Card.Height);
            var sigma = (float)(layout.ShadowBlur / 2);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.Black,
                ImageFilter = SKImageFilter.CreateDropShadow(0, (float)layout.ShadowOffset, sigma, sigma, new SKColor(0, 0, 0, 107)),
            };
            canvas.DrawRoundRect(new SKRoundRect(card, layout.CornerRadius), paint);
        }

        return surface.Snapshot();
    }

    /// <summary>The shared screenshot backdrop type, for <see cref="BackdropRenderer"/>.</summary>
    public static BackdropStyle ToBackdropStyle(RecorderBackdrop style) => new()
    {
        Kind = (BackdropKind)(int)style.Kind,
        PresetId = style.PresetId,
        Colors = style.Colors?.Select(c => new RgbColor(c.R, c.G, c.B)).ToList(),
        ImagePath = style.ImagePath,
        Padding = style.Padding,
        CornerRadius = style.CornerRadius,
        Blur = style.Blur,
    };

    public void Dispose() => _plate?.Dispose();
}

/// <summary>An <see cref="SKImage"/> over a <see cref="PixelBuffer"/> without copying; valid until disposed.</summary>
public sealed class PinnedFrame : IDisposable
{
    private GCHandle _handle;

    public PinnedFrame(PixelBuffer buffer)
    {
        _handle = GCHandle.Alloc(buffer.Pixels, GCHandleType.Pinned);
        var info = new SKImageInfo(buffer.Width, buffer.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Image = SKImage.FromPixels(new SKPixmap(info, _handle.AddrOfPinnedObject(), buffer.Stride))
                ?? throw new InvalidOperationException("Could not wrap the frame.");
    }

    public SKImage Image { get; }

    public void Dispose()
    {
        Image.Dispose();
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }
}
