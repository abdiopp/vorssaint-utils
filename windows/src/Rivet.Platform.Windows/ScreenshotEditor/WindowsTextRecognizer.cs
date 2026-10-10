// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices.WindowsRuntime;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Skia;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using CoreOcrLine = Rivet.Core.ScreenshotEditor.OcrLine;
using CoreOcrWord = Rivet.Core.ScreenshotEditor.OcrWord;
using WinLanguage = Windows.Globalization.Language;

namespace Rivet.Platform.Windows.ScreenshotEditor;

/// <summary>
/// On-device text recognition with Windows.Media.Ocr. The engine prefers the
/// app's language, then the Windows display languages; with no installed OCR
/// language pack it is unavailable and the editor offers no word selection
/// (text-only blurs then cover their whole area). Images larger than the
/// engine's limit are scaled down and the boxes scaled back.
/// </summary>
public sealed class WindowsTextRecognizer : ITextRecognizer
{
    private readonly Lazy<OcrEngine?> _engine = new(CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAvailable => _engine.Value is not null;

    public int? MaxImageDimension
    {
        get
        {
            try
            {
                return (int)OcrEngine.MaxImageDimension;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or PlatformNotSupportedException)
            {
                return null;
            }
        }
    }

    public async Task<OcrPage?> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
    {
        var engine = _engine.Value;
        if (engine is null)
        {
            return null;
        }

        var limit = MaxImageDimension ?? int.MaxValue;
        var factor = Math.Min(1.0, Math.Min((double)limit / image.Width, (double)limit / image.Height));
        var input = factor < 1 ? Downscale(image, factor) : Packed(image);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            input.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, input.Width, input.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        var scaleX = image.Width / (double)input.Width;
        var scaleY = image.Height / (double)input.Height;
        var lines = new List<CoreOcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = new List<CoreOcrWord>(line.Words.Count);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new CoreOcrWord(word.Text, new ImgRect(r.X * scaleX, r.Y * scaleY, r.Width * scaleX, r.Height * scaleY)));
            }

            lines.Add(new CoreOcrLine(words));
        }

        return new OcrPage(lines);
    }

    private static OcrEngine? CreateEngine()
    {
        try
        {
            var code = Localizer.Current.Language.Code();
            var app = new WinLanguage(code);
            if (OcrEngine.IsLanguageSupported(app) && OcrEngine.TryCreateFromLanguage(app) is { } preferred)
            {
                return preferred;
            }

            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                Log.Info("screenshotEditor", "No Windows OCR language is installed; word selection is off.");
            }

            return engine;
        }
        catch (Exception ex)
        {
            Log.Warn("screenshotEditor", "Windows OCR is unavailable.", ex);
            return null;
        }
    }

    private static PixelBuffer Packed(PixelBuffer image)
    {
        if (image.Stride == image.Width * 4)
        {
            return image;
        }

        var rowBytes = image.Width * 4;
        var pixels = new byte[rowBytes * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            Buffer.BlockCopy(image.Pixels, y * image.Stride, pixels, y * rowBytes, rowBytes);
        }

        return new PixelBuffer(image.Width, image.Height, pixels) { Scale = image.Scale };
    }

    private static PixelBuffer Downscale(PixelBuffer image, double factor)
    {
        using var source = SkiaConvert.ToImage(image);
        using var resized = SkiaConvert.Resize(source, Math.Max(1, (int)(image.Width * factor)), Math.Max(1, (int)(image.Height * factor)));
        return SkiaConvert.ToPixelBuffer(resized, image.Scale);
    }
}
