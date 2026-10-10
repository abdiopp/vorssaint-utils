// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Imaging.Capture;
using Rivet.Imaging.Skia;
using SkiaSharp;
using ZXing;
using ZXing.QrCode;

namespace Rivet.Platform.Fake.Capture;

/// <summary>
/// A synthetic desktop for the development build: a wallpaper, a few app
/// windows with text, and a QR code, drawn per display at its native size.
/// The fake window list describes the same windows, so picking works.
/// </summary>
public sealed class FakeDesktop(IScreenService screens)
{
    public const string QrPayload = "https://example.com/rivet";

    /// <summary>Windows front to back, in display-local DIPs of the primary display.</summary>
    private static readonly (string Title, double X, double Y, double W, double H, uint Accent)[] Layout =
    [
        ("Notes", 980, 140, 520, 380, 0xFF2D7D46),
        ("Quarterly report - Document", 420, 220, 760, 520, 0xFF2B579A),
        ("Downloads - File Explorer", 120, 90, 700, 460, 0xFFE8A33D),
    ];

    public IReadOnlyList<CaptureWindowInfo> Windows()
    {
        var display = screens.Primary;
        var s = display.Scale;
        var list = new List<CaptureWindowInfo>();
        var handle = 0x1000;
        foreach (var (title, x, y, w, h, _) in Layout)
        {
            list.Add(new CaptureWindowInfo
            {
                Handle = handle++,
                Bounds = new PixelRect(display.Bounds.X + (int)(x * s), display.Bounds.Y + (int)(y * s), (int)(w * s), (int)(h * s)),
                Title = title,
                ProcessId = 4000 + handle,
                Scale = s,
            });
        }

        return list;
    }

    public PixelBuffer Render(ScreenInfo display, bool includeCursor, PixelPoint cursor)
    {
        var width = display.Bounds.Width;
        var height = display.Bounds.Height;
        var s = (float)display.Scale;
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(width, height));
        var canvas = surface.Canvas;
        using (var wallpaper = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
                [new SKColor(0x1F, 0x4E, 0x8C), new SKColor(0x6A, 0x3D, 0x9A), new SKColor(0xD9, 0x6C, 0x4A)], SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawRect(0, 0, width, height, wallpaper);
        }

        // Taskbar at the bottom of the work area.
        using (var taskbar = new SKPaint { Color = new SKColor(0x20, 0x20, 0x20, 0xF0) })
        {
            canvas.DrawRect(0, display.WorkArea.Bottom - display.Bounds.Y, width, height - (display.WorkArea.Bottom - display.Bounds.Y), taskbar);
        }

        if (display.IsPrimary)
        {
            foreach (var window in Windows().Reverse())
            {
                var layout = Layout.First(l => l.Title == window.Title);
                DrawWindow(canvas, window, layout.Accent, display, s);
            }

            DrawQr(canvas, new SKRect(1560 * s, 600 * s, 1760 * s, 800 * s));
        }

        using var font = new SKFont(SKTypeface.Default, 28 * s);
        using var label = new SKPaint { Color = SKColors.White.WithAlpha(180), IsAntialias = true };
        canvas.DrawText(display.FriendlyName, 40 * s, 60 * s, font, label);

        if (includeCursor && display.Bounds.Contains(cursor))
        {
            using var arrow = new SKPath();
            var cx = cursor.X - display.Bounds.X;
            var cy = cursor.Y - display.Bounds.Y;
            arrow.MoveTo(cx, cy);
            arrow.LineTo(cx, cy + (18 * s));
            arrow.LineTo(cx + (5 * s), cy + (13 * s));
            arrow.LineTo(cx + (12 * s), cy + (13 * s));
            arrow.Close();
            using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var edge = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s };
            canvas.DrawPath(arrow, fill);
            canvas.DrawPath(arrow, edge);
        }

        using var image = surface.Snapshot();
        return SkiaConvert.ToPixelBuffer(image, display.Scale);
    }

    private static void DrawWindow(SKCanvas canvas, CaptureWindowInfo window, uint accent, ScreenInfo display, float s)
    {
        var rect = SKRect.Create(window.Bounds.X - display.Bounds.X, window.Bounds.Y - display.Bounds.Y, window.Bounds.Width, window.Bounds.Height);
        using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(70), ImageFilter = SKImageFilter.CreateBlur(14 * s, 14 * s) })
        {
            canvas.DrawRoundRect(rect, 8 * s, 8 * s, shadow);
        }

        using (var body = new SKPaint { Color = new SKColor(0xFA, 0xFA, 0xFA), IsAntialias = true })
        {
            canvas.DrawRoundRect(rect, 8 * s, 8 * s, body);
        }

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(rect, 8 * s, 8 * s), antialias: true);
        using (var bar = new SKPaint { Color = new SKColor(accent) })
        {
            canvas.DrawRect(rect.Left, rect.Top, rect.Width, 34 * s, bar);
        }

        using var titleFont = new SKFont(SKTypeface.Default, 14 * s);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(window.Title, rect.Left + (14 * s), rect.Top + (22 * s), titleFont, white);

        using var textFont = new SKFont(SKTypeface.Default, 15 * s);
        using var ink = new SKPaint { Color = new SKColor(0x24, 0x24, 0x24), IsAntialias = true };
        string[] lines =
        [
            "Screenshots on Windows, with a frozen picker.",
            "Drag to select an area, or click a window.",
            "Press Enter for the whole screen, Esc to cancel.",
            "The loupe shows every pixel: press Z.",
            "Copy text from screen reads this paragraph.",
        ];
        for (var i = 0; i < lines.Length && rect.Top + ((70 + (i * 28)) * s) < rect.Bottom - (10 * s); i++)
        {
            canvas.DrawText(lines[i], rect.Left + (20 * s), rect.Top + ((70 + (i * 28)) * s), textFont, ink);
        }

        canvas.Restore();
    }

    private static void DrawQr(SKCanvas canvas, SKRect rect)
    {
        var matrix = new QRCodeWriter().encode(QrPayload, BarcodeFormat.QR_CODE, 33, 33);
        using var white = new SKPaint { Color = SKColors.White };
        using var black = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(rect, white);
        var cell = Math.Min(rect.Width, rect.Height) / (matrix.Width + 4);
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                if (matrix[x, y])
                {
                    canvas.DrawRect(rect.Left + ((x + 2) * cell), rect.Top + ((y + 2) * cell), cell, cell, black);
                }
            }
        }
    }
}

/// <summary>Captures from the synthetic desktop. Tests can make displays fail.</summary>
public sealed class FakeScreenCapturer(FakeDesktop desktop, IScreenService screens) : IScreenCapturer
{
    public HashSet<string> FailingDisplays { get; } = [];

    public int DisplayCaptureCount { get; private set; }

    public DisplayCaptureOptions? LastOptions { get; private set; }

    public Task<IReadOnlyDictionary<string, PixelBuffer>> CaptureDisplaysAsync(IReadOnlyList<ScreenInfo> displays, DisplayCaptureOptions options, CancellationToken cancellationToken = default)
    {
        DisplayCaptureCount++;
        LastOptions = options;
        var cursor = screens.CursorPosition;
        return Task.Run<IReadOnlyDictionary<string, PixelBuffer>>(() =>
            displays.Where(d => !FailingDisplays.Contains(d.Id)).ToDictionary(d => d.Id, d => desktop.Render(d, options.IncludeCursor, cursor), StringComparer.Ordinal), cancellationToken);
    }

    public Task<PixelBuffer?> CaptureWindowAsync(CaptureWindowInfo window, IReadOnlyList<CaptureWindowInfo> attached, DisplayCaptureOptions options, CancellationToken cancellationToken = default)
    {
        var display = screens.ScreenFromPoint(new PixelPoint(window.Bounds.X + 1, window.Bounds.Y + 1));
        return Task.Run(() =>
        {
            var full = desktop.Render(display, false, default);
            var local = new PixelRect(window.Bounds.X - display.Bounds.X, window.Bounds.Y - display.Bounds.Y, window.Bounds.Width, window.Bounds.Height);
            return CaptureImaging.Crop(full, local);
        }, cancellationToken);
    }

    public IRegionCapture? CreateRegionCapture(ScreenInfo display, PixelRect rect, DisplayCaptureOptions options)
    {
        var local = rect.Intersect(display.Bounds);
        return local.IsEmpty ? null : new RegionCapture(desktop, display, local);
    }

    private sealed class RegionCapture(FakeDesktop desktop, ScreenInfo display, PixelRect rect) : IRegionCapture
    {
        private PixelBuffer? _full;

        public PixelBuffer? CaptureFrame()
        {
            _full ??= desktop.Render(display, false, default);
            return CaptureImaging.Crop(_full, new PixelRect(rect.X - display.Bounds.X, rect.Y - display.Bounds.Y, rect.Width, rect.Height));
        }

        public void Dispose()
        {
        }
    }
}

public sealed class FakeWindowEnumerator(FakeDesktop desktop) : IWindowEnumerator
{
    public IReadOnlyList<CaptureWindowInfo> EnumerateWindows() => desktop.Windows();
}

/// <summary>An in-memory clipboard that counts changes like Windows does.</summary>
public sealed class FakeCaptureClipboard : ICaptureClipboard
{
    private long _changes;

    public long ChangeCount => Interlocked.Read(ref _changes);

    public PixelBuffer? Image { get; private set; }

    public string? FilePath { get; private set; }

    public string? Text { get; private set; }

    public bool FailWrites { get; set; }

    public bool SetImage(PixelBuffer image, byte[] png, string? filePath)
    {
        if (FailWrites)
        {
            return false;
        }

        Image = image;
        FilePath = filePath;
        Text = null;
        Interlocked.Increment(ref _changes);
        return true;
    }

    public bool SetText(string text)
    {
        if (FailWrites)
        {
            return false;
        }

        Text = text;
        Image = null;
        FilePath = null;
        Interlocked.Increment(ref _changes);
        return true;
    }

    public ClipboardImage? ReadImage(long maxPixels) =>
        Image is { } image && (long)image.Width * image.Height <= maxPixels ? new ClipboardImage(image, FilePath) : null;

    /// <summary>Simulates another app writing to the clipboard.</summary>
    public void Touch() => Interlocked.Increment(ref _changes);
}

/// <summary>Recognizes the synthetic desktop's text by returning fixed lines (or nothing, when configured).</summary>
public sealed class FakeOcrEngine : IOcrEngine
{
    public bool Available { get; set; } = true;

    public IReadOnlyList<string> Lines { get; set; } =
    [
        "Screenshots on Windows, with a frozen picker.",
        "Drag to select an area, or click a window.",
    ];

    public OcrStatus GetStatus() => new(Available, Available ? ["English (United States)"] : []);

    public Task<OcrPage?> RecognizeAsync(PixelBuffer image, IReadOnlyList<string> preferredLanguageTags, CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            return Task.FromResult<OcrPage?>(null);
        }

        var lines = Lines.Select((text, i) =>
        {
            var bounds = new RectD(10, 10 + (i * 30), Math.Min(image.Width - 20, text.Length * 8), 20);
            return new OcrLine(text, bounds, text.Split(' ').Select(w => new OcrWord(w, bounds)).ToList());
        }).ToList();
        return Task.FromResult<OcrPage?>(new OcrPage(image.Width, image.Height, lines, "en-US"));
    }
}

/// <summary>Logs instead of touching the host OS; the default folder lives in the temp directory.</summary>
public sealed class FakeCapturePlatform : ICapturePlatform
{
    public string DefaultScreenshotFolder { get; set; } = Path.Combine(Path.GetTempPath(), "rivet-dev-screenshots");

    public PixelPoint? LastCursorPosition { get; private set; }

    public int BeepCount { get; private set; }

    public List<string> SharedFiles { get; } = [];

    public void SetCursorPosition(PixelPoint point) => LastCursorPosition = point;

    public void PlaceWindow(nint hwnd, PixelRect bounds)
    {
    }

    public void Beep()
    {
        BeepCount++;
        Log.Info("capture", "[fake] beep");
    }

    public bool ShareFile(nint ownerWindow, string filePath, string title, Action<bool>? completed)
    {
        SharedFiles.Add(filePath);
        completed?.Invoke(false);
        return true;
    }

    public void SetClickThrough(nint hwnd, bool enabled)
    {
    }
}
