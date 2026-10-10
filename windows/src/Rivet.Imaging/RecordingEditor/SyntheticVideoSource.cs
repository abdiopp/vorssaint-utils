// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;
using SkiaSharp;

namespace Rivet.Imaging.RecordingEditor;

/// <summary>What the synthetic source draws.</summary>
public enum SyntheticScene
{
    /// <summary>A fake desktop with an app window, text and a moving element (development build).</summary>
    Desktop,

    /// <summary>A fine checkerboard (privacy-blur tests measure its contrast).</summary>
    Checkerboard,

    /// <summary>Flat colour whose red/green encode the frame number (timing tests).</summary>
    FrameCode,
}

/// <summary>
/// Generates frames instead of decoding a file, so the editor, the compositor
/// and the export pipeline run on macOS/Linux and in tests. Frames exist at
/// the nominal rate; <see cref="GetFrame"/> returns the latest at or before the time.
/// </summary>
public sealed class SyntheticVideoSource : IVideoFrameSource
{
    private readonly SyntheticScene _scene;
    private readonly double _fps;

    public SyntheticVideoSource(int width, int height, double duration, double fps, SyntheticScene scene, VideoOpenOptions? options = null)
    {
        VideoWidth = Math.Max(2, width & ~1);
        VideoHeight = Math.Max(2, height & ~1);
        Duration = duration;
        _fps = fps > 0 ? fps : 60;
        _scene = scene;
        FrameWidth = options?.Width is { } w && w > 0 ? w : VideoWidth;
        FrameHeight = options?.Height is { } h && h > 0 ? h : VideoHeight;
    }

    public int VideoWidth { get; }

    public int VideoHeight { get; }

    public int FrameWidth { get; }

    public int FrameHeight { get; }

    public double Duration { get; }

    public double NominalFrameRate => _fps;

    /// <summary>Frames requested so far (tests check sequential decoding).</summary>
    public int FramesRendered { get; private set; }

    public static int FrameNumberAt(double time, double fps) => (int)Math.Floor((time * fps) + 1e-6);

    public VideoFrame? GetFrame(double time, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var last = Math.Max(0, (int)Math.Ceiling(Duration * _fps) - 1);
        var index = Math.Clamp(FrameNumberAt(Math.Max(0, time), _fps), 0, last);
        var frameTime = index / _fps;
        var buffer = new PixelBuffer(FrameWidth, FrameHeight);
        FramesRendered++;
        if (_scene == SyntheticScene.FrameCode)
        {
            for (var i = 0; i < buffer.Pixels.Length; i += 4)
            {
                buffer.Pixels[i] = 128;
                buffer.Pixels[i + 1] = (byte)(index >> 8);
                buffer.Pixels[i + 2] = (byte)(index & 0xFF);
                buffer.Pixels[i + 3] = 255;
            }

            return new VideoFrame(buffer, frameTime);
        }

        using (var pinned = new PinnedSurface(buffer))
        {
            var canvas = pinned.Canvas;
            canvas.Scale(FrameWidth / (float)VideoWidth, FrameHeight / (float)VideoHeight);
            if (_scene == SyntheticScene.Checkerboard)
            {
                DrawCheckerboard(canvas, VideoWidth, VideoHeight);
            }
            else
            {
                DrawDesktop(canvas, VideoWidth, VideoHeight, frameTime);
            }
        }

        return new VideoFrame(buffer, frameTime);
    }

    private static void DrawCheckerboard(SKCanvas canvas, int width, int height)
    {
        canvas.Clear(SKColors.White);
        using var black = new SKPaint { Color = SKColors.Black };
        const int cell = 4;
        for (var y = 0; y < height; y += cell)
        {
            for (var x = (y / cell) % 2 * cell; x < width; x += cell * 2)
            {
                canvas.DrawRect(x, y, cell, cell, black);
            }
        }
    }

    private static void DrawDesktop(SKCanvas canvas, int width, int height, double t)
    {
        using var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
                [new SKColor(0x1E, 0x3A, 0x5F), new SKColor(0x4B, 0x2E, 0x6E)], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, width, height, background);

        var u = Math.Min(width, height) / 100f;
        var window = SKRect.Create(width * 0.08f, height * 0.08f, width * 0.84f, height * 0.8f);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), ImageFilter = SKImageFilter.CreateBlur(3 * u, 3 * u) };
        canvas.DrawRoundRect(window, 1.5f * u, 1.5f * u, shadow);
        using var body = new SKPaint { Color = new SKColor(0xF7, 0xF7, 0xF9), IsAntialias = true };
        canvas.DrawRoundRect(window, 1.5f * u, 1.5f * u, body);
        using var bar = new SKPaint { Color = new SKColor(0xE6, 0xE7, 0xEB), IsAntialias = true };
        canvas.DrawRect(SKRect.Create(window.Left, window.Top, window.Width, 6 * u), bar);

        using var text = new SKPaint { Color = new SKColor(0x2B, 0x2D, 0x33), IsAntialias = true };
        using var font = new SKFont(CaptionRaster.Typeface, 3.2f * u);
        canvas.DrawText("Quarterly report.docx", window.Left + (3 * u), window.Top + (4.2f * u), font, text);

        using var line = new SKPaint { Color = new SKColor(0xC9, 0xCC, 0xD4), IsAntialias = true };
        var y = window.Top + (12 * u);
        var row = 0;
        while (y < window.Bottom - (14 * u))
        {
            var w = window.Width * (0.45f + (0.4f * (float)Math.Abs(Math.Sin((row * 1.7) + 0.3))));
            canvas.DrawRoundRect(SKRect.Create(window.Left + (4 * u), y, w, 1.6f * u), 0.8f * u, 0.8f * u, line);
            y += 4.2f * u;
            row++;
        }

        // A typing caret that advances, and a button that changes colour each second.
        using var accent = new SKPaint { Color = new SKColor(0x0A, 0x84, 0xFF), IsAntialias = true };
        var caret = (float)(t * 18 % (window.Width * 0.6));
        canvas.DrawRect(window.Left + (4 * u) + caret, y, 0.4f * u, 2.4f * u, accent);
        var button = SKRect.Create(window.Right - (22 * u), window.Bottom - (11 * u), 18 * u, 7 * u);
        accent.Color = (int)t % 2 == 0 ? new SKColor(0x0A, 0x84, 0xFF) : new SKColor(0x30, 0xC8, 0x5A);
        canvas.DrawRoundRect(button, 1.5f * u, 1.5f * u, accent);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText("Save", button.MidX, button.MidY + (1.1f * u), SKTextAlign.Center, font, white);

        // A moving dot so motion is visible while playing.
        var cx = window.Left + (window.Width * (0.5f + (0.35f * (float)Math.Sin(t * 1.3))));
        var cy = window.Top + (window.Height * (0.55f + (0.2f * (float)Math.Cos(t * 0.9))));
        using var dot = new SKPaint { Color = new SKColor(0xFF, 0x9F, 0x0A), IsAntialias = true };
        canvas.DrawCircle(cx, cy, 2.2f * u, dot);

        // Time code, bottom left.
        using var code = new SKPaint { Color = new SKColor(255, 255, 255, 200), IsAntialias = true };
        canvas.DrawText($"{t:0.00}s", width * 0.02f, height * 0.97f, font, code);
    }

    public void Dispose()
    {
    }
}

/// <summary>An <see cref="SKSurface"/> drawing straight into a <see cref="PixelBuffer"/>.</summary>
public sealed class PinnedSurface : IDisposable
{
    private System.Runtime.InteropServices.GCHandle _handle;
    private readonly SKSurface _surface;

    public PinnedSurface(PixelBuffer buffer)
    {
        _handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        var info = new SKImageInfo(buffer.Width, buffer.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _handle.AddrOfPinnedObject(), buffer.Stride)
                   ?? throw new InvalidOperationException("Could not create a surface.");
    }

    public SKCanvas Canvas => _surface.Canvas;

    public void Dispose()
    {
        _surface.Flush();
        _surface.Dispose();
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }
}
