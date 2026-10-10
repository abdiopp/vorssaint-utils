// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>Sample sizes for the pixelate and soft-blur styles (spec 01 §6.10).</summary>
public static class BlurSampleMath
{
    /// <summary>factor(level) = {1: 0.4, 2: 0.65, 3: 1.0, 4: 1.5, 5: 2.2}.</summary>
    public static double Factor(int level) => Math.Clamp(level, BlurStyles.MinLevel, BlurStyles.MaxLevel) switch
    {
        1 => 0.4,
        2 => 0.65,
        3 => 1.0,
        4 => 1.5,
        _ => 2.2,
    };

    /// <summary><c>block = max(2, round(max(10, floor(min(W, H) / 55)) · factor))</c>.</summary>
    public static int BlockSize(int width, int height, int level) =>
        Math.Max(2, SpecMath.RoundToInt(Math.Max(10, Math.Min(width, height) / 55) * Factor(level)));

    /// <summary>Pixelate: <c>sw = max(1, W / block); sh = max(1, H / block)</c>.</summary>
    public static (int Width, int Height) PixelateSampleSize(int width, int height, int block) =>
        (Math.Max(1, width / block), Math.Max(1, height / block));

    /// <summary>Soft blur: <c>step = max(1, block / 4)</c>.</summary>
    public static int SoftStep(int block) => Math.Max(1, block / 4);

    public static (int Width, int Height) SoftSampleSize(int width, int height, int step) =>
        (Math.Max(1, width / step), Math.Max(1, height / step));

    /// <summary>Gaussian radius in sample pixels: <c>0.8 · block / step</c> (≈ 3.2).</summary>
    public static double SoftRadius(int block, int step) => 0.8 * block / step;
}

/// <summary>
/// Low-resolution samples of the base image (never of the annotations), one
/// per (style, level) in use. Noise is fresh on every build, so the same text
/// never samples to the same values twice; samples unused in a frame are dropped.
/// </summary>
public sealed class BlurSampleCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(BlurStyle, int), SKImage> _samples = [];
    private readonly HashSet<(BlurStyle, int)> _usedThisFrame = [];
    private readonly Random _random;
    private bool _framing;

    public BlurSampleCache(SkiaEditorImage image, int? noiseSeed = null)
    {
        Image = image;
        _random = noiseSeed is { } seed ? new Random(seed) : new Random();
    }

    public SkiaEditorImage Image { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>The sample for a pixelate or soft-blur area (built on first use).</summary>
    public SKImage Get(BlurStyle style, int level)
    {
        var key = (style == BlurStyle.Blur ? BlurStyle.Blur : BlurStyle.Pixelate, Math.Clamp(level, BlurStyles.MinLevel, BlurStyles.MaxLevel));
        lock (_gate)
        {
            if (_framing)
            {
                _usedThisFrame.Add(key);
            }

            if (_samples.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }

        var built = key.Item1 == BlurStyle.Blur ? BuildSoft(key.Item2) : BuildPixelate(key.Item2);
        lock (_gate)
        {
            if (_samples.TryGetValue(key, out var raced))
            {
                built.Dispose();
                return raced;
            }

            _samples[key] = built;
            return built;
        }
    }

    /// <summary>Makes sure a sample can be built (the controls snap back when it cannot).</summary>
    public bool TryEnsure(BlurStyle style, int level)
    {
        if (style == BlurStyle.Erase)
        {
            return true;
        }

        try
        {
            Get(style, level);
            return true;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    public void BeginFrame()
    {
        lock (_gate)
        {
            _framing = true;
            _usedThisFrame.Clear();
        }
    }

    /// <summary>Drops samples no area used in the frame that just ended.</summary>
    public void EndFrame()
    {
        lock (_gate)
        {
            _framing = false;
            foreach (var key in _samples.Keys.Where(k => !_usedThisFrame.Contains(k)).ToList())
            {
                _samples[key].Dispose();
                _samples.Remove(key);
            }
        }
    }

    private SKImage BuildPixelate(int level)
    {
        var block = BlurSampleMath.BlockSize(Image.Width, Image.Height, level);
        var (sw, sh) = BlurSampleMath.PixelateSampleSize(Image.Width, Image.Height, block);
        using var bitmap = AreaAverage(Image.PeekPixels(), sw, sh);
        AddNoise(bitmap);
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    private SKImage BuildSoft(int level)
    {
        var block = BlurSampleMath.BlockSize(Image.Width, Image.Height, level);
        var step = BlurSampleMath.SoftStep(block);
        var (sw, sh) = BlurSampleMath.SoftSampleSize(Image.Width, Image.Height, step);
        using var bitmap = AreaAverage(Image.PeekPixels(), sw, sh);
        AddNoise(bitmap);
        using var small = SKImage.FromBitmap(bitmap);
        var sigma = (float)BlurSampleMath.SoftRadius(block, step);
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(sw, sh));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp), BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawImage(small, 0, 0, paint);
        // A raster surface's snapshot is already raster (ToRasterImage would hand back the same wrapper).
        return surface.Snapshot();
    }

    /// <summary>Box-average downscale ("medium quality"): every source pixel lands in exactly one cell.</summary>
    internal static unsafe SKBitmap AreaAverage(SKPixmap source, int width, int height)
    {
        var bitmap = new SKBitmap(SkiaConvert.InfoFor(width, height));
        var srcW = source.Width;
        var srcH = source.Height;
        var srcRow = source.RowBytes;
        var src = (byte*)source.GetPixels();
        var dst = (byte*)bitmap.GetPixels();
        var dstRow = bitmap.RowBytes;
        for (var j = 0; j < height; j++)
        {
            var y0 = (int)((long)j * srcH / height);
            var y1 = Math.Max(y0 + 1, (int)((long)(j + 1) * srcH / height));
            for (var i = 0; i < width; i++)
            {
                var x0 = (int)((long)i * srcW / width);
                var x1 = Math.Max(x0 + 1, (int)((long)(i + 1) * srcW / width));
                long b = 0, g = 0, r = 0, a = 0;
                for (var y = y0; y < y1; y++)
                {
                    var p = src + ((long)y * srcRow) + (x0 * 4);
                    for (var x = x0; x < x1; x++, p += 4)
                    {
                        b += p[0];
                        g += p[1];
                        r += p[2];
                        a += p[3];
                    }
                }

                long n = (long)(x1 - x0) * (y1 - y0);
                var o = dst + ((long)j * dstRow) + (i * 4);
                o[0] = (byte)((b + (n / 2)) / n);
                o[1] = (byte)((g + (n / 2)) / n);
                o[2] = (byte)((r + (n / 2)) / n);
                o[3] = (byte)((a + (n / 2)) / n);
            }
        }

        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    /// <summary><c>n = (randomByte mod 19) − 9</c> added to R, G and B; alpha untouched (colour stays ≤ alpha, premultiplied).</summary>
    private unsafe void AddNoise(SKBitmap bitmap)
    {
        var count = bitmap.Width * bitmap.Height;
        var noise = new byte[count * 3];
        lock (_gate)
        {
            _random.NextBytes(noise);
        }

        var p = (byte*)bitmap.GetPixels();
        var row = bitmap.RowBytes;
        var k = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var q = p + ((long)y * row);
            for (var x = 0; x < bitmap.Width; x++, q += 4)
            {
                var alpha = q[3];
                q[0] = (byte)Math.Clamp(q[0] + ((noise[k++] % 19) - 9), 0, alpha);
                q[1] = (byte)Math.Clamp(q[1] + ((noise[k++] % 19) - 9), 0, alpha);
                q[2] = (byte)Math.Clamp(q[2] + ((noise[k++] % 19) - 9), 0, alpha);
            }
        }

        bitmap.NotifyPixelsChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var sample in _samples.Values)
            {
                sample.Dispose();
            }

            _samples.Clear();
        }
    }
}
