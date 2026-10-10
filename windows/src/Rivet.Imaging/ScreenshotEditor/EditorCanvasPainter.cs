// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Backdrop;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>Everything the live canvas draws in one frame. Coordinates are view DIPs unless noted.</summary>
public sealed record CanvasFrame
{
    public required EditorRenderState State { get; init; }

    /// <summary>View DIPs per image pixel.</summary>
    public required double Zoom { get; init; }

    /// <summary>Top-left of the content (backdrop or image) in view DIPs.</summary>
    public required SKPoint Origin { get; init; }

    /// <summary>Device pixels per DIP of the window (crisp 1:1 and pixel snapping).</summary>
    public double RenderScaling { get; init; } = 1;

    public BackdropStyle Backdrop { get; init; } = BackdropStyle.None;

    public Annotation? Selected { get; init; }

    public ImgRect? CropDraft { get; init; }

    public ImgPoint? CropLoupePoint { get; init; }

    public IReadOnlyList<RecognizedWord> Words { get; init; } = [];

    public IReadOnlyList<int> SelectedWords { get; init; } = [];

    /// <summary>The visible part of the canvas in view DIPs (the loupe stays inside it).</summary>
    public SKRect Viewport { get; init; }
}

/// <summary>
/// Paints the editor canvas at display scale (spec 01 §3.10.3): card
/// shadows, the live backdrop, the image card, the content through the
/// shared <see cref="EditorRenderer"/>, then selection, word and crop chrome.
/// </summary>
public sealed class EditorCanvasPainter : IDisposable
{
    private static readonly SKColor Blue = new(10, 133, 255);

    private PlateCache? _plate;

    public EditorRenderCaches Caches { get; } = new();

    /// <summary>The content rectangle (backdrop included) in view DIPs.</summary>
    public static SKRect ContentRect(CanvasFrame frame, out BackdropGeometry geometry)
    {
        geometry = BackdropRenderer.Measure(frame.State.Image.Width, frame.State.Image.Height, frame.Backdrop);
        var z = (float)frame.Zoom;
        return SKRect.Create(frame.Origin.X, frame.Origin.Y, geometry.CanvasWidth * z, geometry.CanvasHeight * z);
    }

    /// <summary>The image card in view DIPs.</summary>
    public static SKRect ImageRect(CanvasFrame frame)
    {
        var content = ContentRect(frame, out var geometry);
        var z = (float)frame.Zoom;
        return SKRect.Create(content.Left + (geometry.Padding * z), content.Top + (geometry.Padding * z), frame.State.Image.Width * z, frame.State.Image.Height * z);
    }

    public void Paint(SKCanvas canvas, CanvasFrame frame)
    {
        var state = frame.State;
        var backdrop = frame.Backdrop.Sanitized();
        var plateImage = backdrop.Kind == BackdropKind.Image ? Caches.Images.Get(backdrop.ImagePath, 4096) : null;
        if (backdrop.Kind == BackdropKind.Image && plateImage is null)
        {
            // An image backdrop whose file disappeared renders as no backdrop, quietly.
            backdrop = new BackdropStyle { CornerRadius = backdrop.CornerRadius };
        }

        var effective = frame with { Backdrop = backdrop };
        var content = ContentRect(effective, out var geometry);
        var image = ImageRect(effective);
        var z = (float)frame.Zoom;
        var hasBackdrop = backdrop.HasBackdrop;
        var cardRadius = hasBackdrop ? 6f : Math.Max(4f, geometry.CornerRadius * z);
        var contentRRect = new SKRoundRect(content, cardRadius);

        // Card presentation: two soft shadows under the whole content.
        using (var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black })
        {
            shadow.ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 10, 12, 12, new SKColor(0, 0, 0, 77));
            canvas.DrawRoundRect(contentRRect, shadow);
            shadow.ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 1, 1.5f, 1.5f, new SKColor(0, 0, 0, 46));
            canvas.DrawRoundRect(contentRRect, shadow);
        }

        canvas.Save();
        canvas.ClipRoundRect(contentRRect, SKClipOperation.Intersect, antialias: true);
        SKRoundRect imageRRect;
        if (hasBackdrop)
        {
            DrawLivePlate(canvas, backdrop, plateImage, geometry, content, (float)frame.RenderScaling);
            imageRRect = new SKRoundRect(image, geometry.CornerRadius * z);
            using var cardShadow = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.Black,
                ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 5, 9, 9, new SKColor(0, 0, 0, 97)),
            };
            canvas.Save();
            canvas.ClipRoundRect(imageRRect, SKClipOperation.Difference, antialias: true);
            canvas.DrawRoundRect(imageRRect, cardShadow);
            canvas.Restore();
        }
        else
        {
            imageRRect = contentRRect;
        }

        canvas.Save();
        canvas.ClipRoundRect(imageRRect, SKClipOperation.Intersect, antialias: true);
        canvas.Translate(image.Left, image.Top);
        canvas.Scale(z);
        // Replace-mode blur areas must not punch through to the backdrop or shadow: draw in an isolated layer.
        var isolated = state.HasBlurAreas;
        if (isolated)
        {
            canvas.SaveLayer(state.Image.Bounds, null);
        }

        Caches.BeginFrame(state.Image);
        DrawBase(canvas, state, frame);
        foreach (var annotation in state.Annotations)
        {
            AnnotationRenderer.Draw(canvas, annotation, state, Caches);
        }

        WatermarkRenderer.Draw(canvas, state with { CornerRadius = geometry.CornerRadius }, Caches);
        Caches.EndFrame();
        if (isolated)
        {
            canvas.Restore();
        }

        canvas.Restore();
        canvas.Restore();

        using (var hairline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 255, 255, 36) })
        {
            canvas.DrawRoundRect(new SKRoundRect(SKRect.Inflate(content, -0.5f, -0.5f), Math.Max(0, cardRadius - 0.5f)), hairline);
        }

        // Chrome in image space (not clipped, so handles at the edges stay visible).
        canvas.Save();
        canvas.Translate(image.Left, image.Top);
        canvas.Scale(z);
        DrawWordHighlights(canvas, frame);
        if (frame.CropDraft is { } draft)
        {
            DrawCropChrome(canvas, state, draft);
        }
        else if (frame.Selected is { } selected && selected.Id != state.HiddenAnnotation)
        {
            DrawSelection(canvas, selected, state);
        }

        canvas.Restore();

        if (frame.CropDraft is not null && frame.CropLoupePoint is { } point)
        {
            DrawCropLoupe(canvas, frame, image, point);
        }
    }

    /// <summary>The base image: crisp nearest-neighbour at 1:1 and when magnified, smooth mipmapped when shrunk.</summary>
    private static void DrawBase(SKCanvas canvas, EditorRenderState state, CanvasFrame frame)
    {
        var device = frame.Zoom * frame.RenderScaling;
        var sampling = device >= 0.99
            ? (device <= 1.01 || device >= 1.5 ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        canvas.DrawImage(state.Image.Image, state.Image.Bounds, sampling);
    }

    /// <summary>The live backdrop: scaled by 1 + 0.08·blur (hides the blur's edge fade) and blurred by radius × zoom.</summary>
    private void DrawLivePlate(SKCanvas canvas, BackdropStyle backdrop, SKImage? plateImage, BackdropGeometry geometry, SKRect content, float renderScaling)
    {
        // Rendered once per look and size at device resolution, then reused every frame.
        var deviceW = Math.Clamp((int)Math.Ceiling(content.Width * renderScaling), 1, 8192);
        var deviceH = Math.Clamp((int)Math.Ceiling(content.Height * renderScaling), 1, 8192);
        var key = PlateKey(backdrop, plateImage, geometry, deviceW, deviceH);
        if (_plate is null || _plate.Key != key)
        {
            _plate?.Dispose();
            _plate = new PlateCache(key, RenderPlate(backdrop, plateImage, geometry, deviceW, deviceH));
        }

        var grow = 1 + (0.08f * (float)backdrop.Blur);
        var dest = SKRect.Create(content.MidX - (content.Width * grow / 2), content.MidY - (content.Height * grow / 2), content.Width * grow, content.Height * grow);
        canvas.DrawImage(_plate.Image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    }

    private static string PlateKey(BackdropStyle b, SKImage? plateImage, BackdropGeometry g, int w, int h) =>
        string.Join('|', b.Kind, b.PresetId, string.Join(';', b.Colors?.Select(c => $"{c.R:F4},{c.G:F4},{c.B:F4}") ?? []), b.ImagePath,
            plateImage?.UniqueId, b.Blur.ToString("F4", System.Globalization.CultureInfo.InvariantCulture), g.CanvasWidth, g.CanvasHeight, w, h);

    private static SKImage RenderPlate(BackdropStyle backdrop, SKImage? plateImage, BackdropGeometry geometry, int width, int height)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(width, height));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(width / (float)geometry.CanvasWidth, height / (float)geometry.CanvasHeight);
        // The plate blur is in image pixels; drawn here at plate scale it becomes radius × zoom on screen.
        BackdropRenderer.DrawPlate(c, backdrop, plateImage, geometry);
        return surface.Snapshot();
    }

    private static void DrawWordHighlights(SKCanvas canvas, CanvasFrame frame)
    {
        if (frame.SelectedWords.Count == 0)
        {
            return;
        }

        var s = (float)frame.State.Scale;
        using var paint = new SKPaint { IsAntialias = true, Color = Blue.WithAlpha(82) };
        foreach (var index in frame.SelectedWords)
        {
            if (index >= 0 && index < frame.Words.Count && frame.Words[index].Rect is { } r)
            {
                canvas.DrawRoundRect(SKRect.Inflate(SkiaPaths.R(r), 1.5f * s, 1.5f * s), 2 * s, 2 * s, paint);
            }
        }
    }

    /// <summary>
    /// Selection chrome in image space: blue 90 %, 1.5·scale wide, dashed 4/3·scale.
    /// Segments get endpoint dots; counters a box around the circle; resizable marks eight handles.
    /// </summary>
    public static void DrawSelection(SKCanvas canvas, Annotation a, EditorRenderState state)
    {
        var s = (float)state.Scale;
        using var dashed = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * s,
            Color = Blue.WithAlpha(230),
            PathEffect = SKPathEffect.CreateDash([4 * s, 3 * s], 0),
        };
        using var handleFill = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var handleStroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, Color = Blue.WithAlpha(230) };

        if (a.IsSegment)
        {
            foreach (var p in new[] { a.Start, a.End })
            {
                canvas.DrawCircle(SkiaPaths.P(p), 4 * s, handleFill);
                canvas.DrawCircle(SkiaPaths.P(p), 4 * s, handleStroke);
            }

            return;
        }

        var bounds = a.Bounds(state.Image.Width, state.Image.Height);
        var box = a.Kind == AnnotationKind.Counter ? bounds.Inflate(3, 3) : bounds.Inflate(3 * s, 3 * s);
        canvas.DrawRect(SkiaPaths.R(box), dashed);
        if (!a.IsResizable)
        {
            return;
        }

        foreach (var handle in HitTesting.AllHandles)
        {
            var p = SkiaPaths.P(HitTesting.HandlePoint(a.Rect, handle));
            canvas.DrawCircle(p, 3.5f * s, handleFill);
            canvas.DrawCircle(p, 3.5f * s, handleStroke);
        }
    }

    /// <summary>Crop chrome: the outside darkened 45 %, a white 95 % border and grip dots sitting on the edges.</summary>
    public static void DrawCropChrome(SKCanvas canvas, EditorRenderState state, ImgRect draft)
    {
        var s = (float)state.Scale;
        var bounds = state.Image.Bounds;
        var r = SkiaPaths.R(draft);
        using (var shade = new SKPaint { Color = new SKColor(0, 0, 0, 115) })
        {
            canvas.Save();
            canvas.ClipRect(bounds);
            canvas.ClipRect(r, SKClipOperation.Difference);
            canvas.DrawRect(bounds, shade);
            canvas.Restore();
        }

        using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, Color = SKColors.White.WithAlpha(242) };
        canvas.DrawRect(r, border);
        using var grip = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var gripEdge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f * s, Color = new SKColor(0, 0, 0, 90) };
        foreach (var handle in HitTesting.AllHandles)
        {
            var p = SkiaPaths.P(HitTesting.HandlePoint(draft, handle));
            canvas.DrawCircle(p, 4 * s, grip);
            canvas.DrawCircle(p, 4 * s, gripEdge);
        }
    }

    /// <summary>
    /// The crop loupe: 72 × 72 view points showing a 14 × 14 pixel sample centred on the edge,
    /// nearest-neighbour, with a crosshair and a 7-point ring at the exact point; placed 14 points
    /// right of and above the grip, flipping to stay inside the canvas.
    /// </summary>
    private static void DrawCropLoupe(SKCanvas canvas, CanvasFrame frame, SKRect image, ImgPoint point)
    {
        const float side = 72;
        const float gap = 14;
        var state = frame.State;
        var (sx, sy, sw, sh) = CropMath.LoupeSample(point, state.Image.Width, state.Image.Height);
        if (sw <= 0 || sh <= 0)
        {
            return;
        }

        var z = (float)frame.Zoom;
        var grip = new SKPoint(image.Left + (float)(point.X * z), image.Top + (float)(point.Y * z));
        var viewport = frame.Viewport.IsEmpty ? SKRect.Create(0, 0, 1e6f, 1e6f) : frame.Viewport;
        var x = grip.X + gap;
        var y = grip.Y - gap - side;
        if (x + side > viewport.Right - 4)
        {
            x = grip.X - gap - side;
        }

        if (y < viewport.Top + 4)
        {
            y = grip.Y + gap;
        }

        x = Math.Clamp(x, viewport.Left + 4, Math.Max(viewport.Left + 4, viewport.Right - side - 4));
        y = Math.Clamp(y, viewport.Top + 4, Math.Max(viewport.Top + 4, viewport.Bottom - side - 4));
        var frameRect = SKRect.Create(x, y, side, side);
        var rrect = new SKRoundRect(frameRect, 9);
        using (var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black, ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 2, 4, 4, new SKColor(0, 0, 0, 100)) })
        {
            canvas.DrawRoundRect(rrect, shadow);
        }

        canvas.Save();
        canvas.ClipRoundRect(rrect, SKClipOperation.Intersect, antialias: true);
        canvas.DrawImage(state.Image.Image, SKRect.Create(sx, sy, sw, sh), frameRect, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        var px = x + (float)((point.X - sx) / sw * side);
        var py = y + (float)((point.Y - sy) / sh * side);
        using (var cross = new SKPaint { Color = new SKColor(0, 0, 0, 184), StrokeWidth = 1, IsAntialias = false })
        {
            canvas.DrawLine(frameRect.Left, py, frameRect.Right, py, cross);
            canvas.DrawLine(px, frameRect.Top, px, frameRect.Bottom, cross);
        }

        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.White })
        {
            canvas.DrawCircle(px, py, 3.5f, ring);
        }

        canvas.Restore();
        using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.White.WithAlpha(230) };
        canvas.DrawRoundRect(rrect, border);
    }

    public void Dispose()
    {
        _plate?.Dispose();
        Caches.Dispose();
    }

    private sealed class PlateCache(string key, SKImage image) : IDisposable
    {
        public string Key { get; } = key;

        public SKImage Image { get; } = image;

        public void Dispose() => Image.Dispose();
    }
}
