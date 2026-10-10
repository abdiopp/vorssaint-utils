// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Rivet.Core.Capture;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Imaging.Capture;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using CoreOcrLine = Rivet.Core.Capture.OcrLine;
using CoreOcrWord = Rivet.Core.Capture.OcrWord;

namespace Rivet.Platform.Windows.Capture;

/// <summary>
/// Text recognition with Windows.Media.Ocr. Windows recognizes one language
/// per engine and only with an installed OCR language pack, so the engines
/// are tried in order: the app language, English, then the user's profile
/// languages, stopping at the first that finds lines (the macOS "fast retry"
/// equivalent). Small screen text is upscaled 2× first, which Windows OCR
/// reads far more reliably; boxes are mapped back to the original pixels.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    public OcrStatus GetStatus()
    {
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages;
            return new OcrStatus(languages.Count > 0, languages.Select(l => l.DisplayName).ToList());
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException)
        {
            Log.Warn("capture", "Windows OCR is unavailable.", ex);
            return new OcrStatus(false, []);
        }
    }

    public async Task<OcrPage?> RecognizeAsync(PixelBuffer image, IReadOnlyList<string> preferredLanguageTags, CancellationToken cancellationToken = default)
    {
        var engines = Engines(preferredLanguageTags);
        if (engines.Count == 0)
        {
            return null;
        }

        var (prepared, factor) = Prepare(image);
        using var bitmap = ToSoftwareBitmap(prepared);
        foreach (var engine in engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrResult result;
            try
            {
                result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                Log.Warn("capture", $"OCR failed for {engine.RecognizerLanguage?.LanguageTag}.", ex);
                continue;
            }

            if (result.Lines.Count == 0)
            {
                continue;
            }

            var lines = new List<CoreOcrLine>(result.Lines.Count);
            foreach (var line in result.Lines)
            {
                var words = line.Words
                    .Select(w => new CoreOcrWord(w.Text, new RectD(w.BoundingRect.X / factor, w.BoundingRect.Y / factor, w.BoundingRect.Width / factor, w.BoundingRect.Height / factor)))
                    .ToList();
                var bounds = words.Count == 0 ? default : Union(words.Select(w => w.Bounds));
                lines.Add(new CoreOcrLine(line.Text, bounds, words));
            }

            return new OcrPage(image.Width, image.Height, lines, engine.RecognizerLanguage?.LanguageTag);
        }

        return new OcrPage(image.Width, image.Height, []);
    }

    private static List<OcrEngine> Engines(IReadOnlyList<string> preferredTags)
    {
        var engines = new List<OcrEngine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var tag in preferredTags)
            {
                var language = new Language(tag);
                if (!OcrEngine.IsLanguageSupported(language))
                {
                    // "zh-Hant" style tags may need the region form the pack registers.
                    language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith(tag.Split('-')[0], StringComparison.OrdinalIgnoreCase)
                                                                                          && (tag.Length <= 2 || l.LanguageTag.Contains(tag.Split('-')[^1], StringComparison.OrdinalIgnoreCase)))
                               ?? language;
                }

                if (OcrEngine.IsLanguageSupported(language) && OcrEngine.TryCreateFromLanguage(language) is { } engine && seen.Add(engine.RecognizerLanguage.LanguageTag))
                {
                    engines.Add(engine);
                }
            }

            if (OcrEngine.TryCreateFromUserProfileLanguages() is { } profile && seen.Add(profile.RecognizerLanguage.LanguageTag))
            {
                engines.Add(profile);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or TypeLoadException)
        {
            Log.Warn("capture", "Could not create an OCR engine.", ex);
        }

        return engines;
    }

    /// <summary>Upscales small, low-DPI captures 2× and keeps the image within OcrEngine.MaxImageDimension.</summary>
    private static (PixelBuffer Image, double Factor) Prepare(PixelBuffer image)
    {
        var max = (int)OcrEngine.MaxImageDimension;
        var factor = image.Scale < 1.5 && Math.Max(image.Width, image.Height) * 2 <= Math.Min(max, 4000) ? 2.0 : 1.0;
        var longest = Math.Max(image.Width, image.Height) * factor;
        if (longest > max)
        {
            factor = max / (double)Math.Max(image.Width, image.Height);
        }

        if (Math.Abs(factor - 1) < 0.001)
        {
            return (image, 1);
        }

        var w = Math.Max(1, (int)Math.Round(image.Width * factor));
        var h = Math.Max(1, (int)Math.Round(image.Height * factor));
        return (CaptureImaging.Resize(image, w, h, image.Scale * factor), factor);
    }

    private static SoftwareBitmap ToSoftwareBitmap(PixelBuffer image)
    {
        var rowBytes = image.Width * 4;
        byte[] packed;
        if (image.Stride == rowBytes)
        {
            packed = image.Pixels;
        }
        else
        {
            packed = new byte[rowBytes * image.Height];
            for (var y = 0; y < image.Height; y++)
            {
                Buffer.BlockCopy(image.Pixels, y * image.Stride, packed, y * rowBytes, rowBytes);
            }
        }

        return SoftwareBitmap.CreateCopyFromBuffer(packed.AsBuffer(0, rowBytes * image.Height), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
    }

    private static RectD Union(IEnumerable<RectD> rects)
    {
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
        foreach (var r in rects)
        {
            x1 = Math.Min(x1, r.X);
            y1 = Math.Min(y1, r.Y);
            x2 = Math.Max(x2, r.Right);
            y2 = Math.Max(y2, r.Bottom);
        }

        return x2 > x1 && y2 > y1 ? new RectD(x1, y1, x2 - x1, y2 - y1) : default;
    }
}
