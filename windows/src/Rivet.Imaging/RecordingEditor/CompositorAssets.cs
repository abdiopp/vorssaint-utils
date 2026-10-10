// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.RecordingEditor;

/// <summary>
/// Decoded pictures shared by the compositors of one editor (rebuilt on
/// every picture change): cursor bitmaps, background images, overlay images
/// at their drawn size and caption rasters (spec 02 §6.15). Use one instance
/// per thread; the export uses its own.
/// </summary>
public sealed class CompositorAssets : IDisposable
{
    public const int MaxCaptions = 48;
    public const long OverlayBudgetPixels = 24_000_000;
    public const int MaxOverlaySide = 20_000;
    public const long MaxOverlayPixels = 8192L * 8192L;

    private readonly Dictionary<int, SKImage?> _cursors = [];
    private readonly Dictionary<string, SKImage?> _backdrops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SKImage?> _overlays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CaptionRaster> _captions = new(StringComparer.Ordinal);
    private readonly PointerTrack _pointer;
    private long _overlayPixels;

    public CompositorAssets(PointerTrack pointer)
    {
        _pointer = pointer;
    }

    public PointerTrack Pointer => _pointer;

    /// <summary>The decoded cursor bitmap for a shape index (null when it cannot be decoded).</summary>
    public SKImage? Cursor(int index)
    {
        if (index < 0 || index >= _pointer.Shapes.Count)
        {
            return null;
        }

        if (!_cursors.TryGetValue(index, out var image))
        {
            try
            {
                using var data = SKData.CreateCopy(_pointer.Shapes[index].Png);
                using var codec = SKCodec.Create(data);
                if (codec is not null && codec.Info.Width <= 1024 && codec.Info.Height <= 1024)
                {
                    using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                    image = bitmap is null ? null : SKImage.FromBitmap(bitmap);
                }
            }
            catch (ArgumentException)
            {
                image = null;
            }

            _cursors[index] = image;
        }

        return image;
    }

    /// <summary>A background image decoded at most 4096 px on its long side.</summary>
    public SKImage? Backdrop(string path)
    {
        var key = path + "|" + Stamp(path);
        if (!_backdrops.TryGetValue(key, out var image))
        {
            if (_backdrops.Count > 4)
            {
                ClearImages(_backdrops);
            }

            image = OrientedImage.Load(path, 4096);
            _backdrops[key] = image;
        }

        return image;
    }

    /// <summary>
    /// An overlay at exactly its drawn size (high-quality resize, own
    /// transparency kept). The key includes file size and modification time so
    /// a replaced file is a new picture. Pictures over 20,000 px per side or
    /// 8192² pixels are refused.
    /// </summary>
    public SKImage? Overlay(string path, Func<int, int, (int Width, int Height)> sizeFor)
    {
        var natural = NaturalSize(path);
        if (natural is null)
        {
            return null;
        }

        var (w, h) = sizeFor(natural.Value.Width, natural.Value.Height);
        var key = $"{path}|{w}x{h}|{Stamp(path)}";
        if (_overlays.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_overlayPixels + ((long)w * h) > OverlayBudgetPixels)
        {
            ClearImages(_overlays);
            _overlayPixels = 0;
        }

        SKImage? result = null;
        using (var decoded = OrientedImage.Load(path, Math.Max(w, h) * 2))
        {
            if (decoded is not null)
            {
                result = SkiaConvert.Resize(decoded, Math.Max(1, w), Math.Max(1, h));
            }
        }

        _overlays[key] = result;
        _overlayPixels += (long)w * h;
        return result;
    }

    private readonly Dictionary<string, (int Width, int Height)?> _naturalSizes = new(StringComparer.Ordinal);

    /// <summary>The picture's own size, or null when unreadable or too large.</summary>
    public (int Width, int Height)? NaturalSize(string path)
    {
        var key = path + "|" + Stamp(path);
        if (_naturalSizes.TryGetValue(key, out var size))
        {
            return size;
        }

        size = null;
        try
        {
            if (File.Exists(path))
            {
                using var codec = SKCodec.Create(path);
                if (codec is not null && codec.Info.Width is > 0 and <= MaxOverlaySide && codec.Info.Height is > 0 and <= MaxOverlaySide
                    && (long)codec.Info.Width * codec.Info.Height <= MaxOverlayPixels)
                {
                    var origin = codec.EncodedOrigin;
                    var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
                    size = swap ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            size = null;
        }

        _naturalSizes[key] = size;
        return size;
    }

    /// <summary>A caption rasterized once per (text, size, colour, alignment); the cache clears past 48 entries.</summary>
    public CaptionRaster Caption(string text, int fontSize, RgbValue color, SKTextAlign align)
    {
        var key = $"{fontSize}|{color}|{align}|{text}";
        if (_captions.TryGetValue(key, out var raster))
        {
            return raster;
        }

        if (_captions.Count >= MaxCaptions)
        {
            foreach (var item in _captions.Values)
            {
                item.Dispose();
            }

            _captions.Clear();
        }

        raster = CaptionRaster.Create(text, fontSize, color, align);
        _captions[key] = raster;
        return raster;
    }

    private static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "error";
        }
    }

    private static void ClearImages(Dictionary<string, SKImage?> images)
    {
        foreach (var image in images.Values)
        {
            image?.Dispose();
        }

        images.Clear();
    }

    public void Dispose()
    {
        foreach (var image in _cursors.Values)
        {
            image?.Dispose();
        }

        _cursors.Clear();
        ClearImages(_backdrops);
        ClearImages(_overlays);
        foreach (var caption in _captions.Values)
        {
            caption.Dispose();
        }

        _captions.Clear();
    }
}

/// <summary>A rasterized caption: semibold UI font, soft shadow, padding of half the font size.</summary>
public sealed class CaptionRaster : IDisposable
{
    private CaptionRaster(SKImage? image, int width, int height)
    {
        Image = image;
        Width = width;
        Height = height;
    }

    public SKImage? Image { get; }

    public int Width { get; }

    public int Height { get; }

    private static SKTypeface? _typeface;

    /// <summary>Segoe UI Variable / Segoe UI Semibold on Windows; the platform UI font elsewhere.</summary>
    public static SKTypeface Typeface
    {
        get
        {
            if (_typeface is null)
            {
                var style = new SKFontStyle(SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                foreach (var family in new[] { "Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI", "SF Pro Text", "Helvetica Neue", "Inter" })
                {
                    var candidate = SKFontManager.Default.MatchFamily(family, style);
                    if (candidate is not null)
                    {
                        _typeface = candidate;
                        break;
                    }
                }

                _typeface ??= SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
            }

            return _typeface;
        }
    }

    public static CaptionRaster Create(string text, int fontSize, RgbValue color, SKTextAlign align)
    {
        var lines = text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.All(string.IsNullOrWhiteSpace))
        {
            return new CaptionRaster(null, 0, 0);
        }

        using var font = new SKFont(Typeface, fontSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var widths = lines.Select(l => font.MeasureText(l)).ToArray();
        var lineHeight = font.Spacing;
        var contentWidth = widths.Max();
        var contentHeight = lineHeight * lines.Length;
        var padding = 0.5f * fontSize;
        var width = Math.Max(1, (int)Math.Ceiling(contentWidth + (2 * padding)));
        var height = Math.Max(1, (int)Math.Ceiling(contentHeight + (2 * padding)));

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var sigma = 0.18f * fontSize / 2f;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor((byte)Math.Round(color.R * 255), (byte)Math.Round(color.G * 255), (byte)Math.Round(color.B * 255)),
            ImageFilter = SKImageFilter.CreateDropShadow(0, 0.04f * fontSize, sigma, sigma, new SKColor(0, 0, 0, 140)),
        };
        var metrics = font.Metrics;
        for (var i = 0; i < lines.Length; i++)
        {
            var x = align switch
            {
                SKTextAlign.Center => padding + ((contentWidth - widths[i]) / 2),
                SKTextAlign.Right => padding + (contentWidth - widths[i]),
                _ => padding,
            };
            var baseline = padding + (i * lineHeight) - metrics.Ascent;
            canvas.DrawText(lines[i], x, baseline, SKTextAlign.Left, font, paint);
        }

        return new CaptionRaster(surface.Snapshot(), width, height);
    }

    public void Dispose() => Image?.Dispose();
}
