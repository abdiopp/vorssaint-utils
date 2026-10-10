// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text;
using Rivet.Core.ScreenshotEditor;
using SkiaSharp;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>
/// Fonts for annotation text, counters, stickers and text watermarks. The
/// same engine measures and draws, so text boxes always fit what is drawn.
/// Characters the primary face lacks (CJK, emoji, symbols) fall back to a
/// face the system font manager matches, run by run.
/// </summary>
public sealed class AnnotationFonts : ITextMeasurer
{
    /// <summary>Families tried in order: the Windows system font first, then development-host fallbacks.</summary>
    private static readonly string[] Families = ["Segoe UI", "Segoe UI Variable Text", "Helvetica Neue", "Helvetica", "Arial", "DejaVu Sans"];

    private readonly ConcurrentDictionary<(int Rune, int Weight), SKTypeface> _fallbacks = new();

    public AnnotationFonts()
    {
        Semibold = Resolve(SKFontStyleWeight.SemiBold);
        Bold = Resolve(SKFontStyleWeight.Bold);
    }

    public static AnnotationFonts Shared { get; } = new();

    /// <summary>The annotation and watermark face ("system semibold").</summary>
    public SKTypeface Semibold { get; }

    /// <summary>The counter label face.</summary>
    public SKTypeface Bold { get; }

    public (double Width, double Height) Measure(string text, double fontSizePixels)
    {
        var size = MeasureRuns(text, (float)fontSizePixels, Semibold);
        return (size.Width, size.Height);
    }

    /// <summary>Width of the shaped runs; height is the primary face's line height.</summary>
    public SKSize MeasureRuns(string text, float size, SKTypeface primary)
    {
        using var font = NewFont(primary, size);
        var width = 0f;
        foreach (var (face, run) in Runs(text, primary))
        {
            using var runFont = ReferenceEquals(face, primary) ? null : NewFont(face, size);
            width += (runFont ?? font).MeasureText(run);
        }

        return new SKSize(width, LineHeight(font));
    }

    /// <summary>Draws <paramref name="text"/> with its top-left corner at (<paramref name="x"/>, <paramref name="top"/>).</summary>
    public void DrawTopLeft(SKCanvas canvas, string text, float x, float top, float size, SKTypeface primary, SKPaint paint)
    {
        using var font = NewFont(primary, size);
        var baseline = top - font.Metrics.Ascent;
        var penX = x;
        foreach (var (face, run) in Runs(text, primary))
        {
            using var runFont = ReferenceEquals(face, primary) ? null : NewFont(face, size);
            var f = runFont ?? font;
            canvas.DrawText(run, penX, baseline, SKTextAlign.Left, f, paint);
            penX += f.MeasureText(run);
        }
    }

    /// <summary>Draws text centred on (<paramref name="cx"/>, <paramref name="cy"/>), vertically by the cap height (digits look centred).</summary>
    public void DrawCentered(SKCanvas canvas, string text, float cx, float cy, float size, SKTypeface primary, SKPaint paint)
    {
        using var font = NewFont(primary, size);
        var width = MeasureRuns(text, size, primary).Width;
        var metrics = font.Metrics;
        var capHeight = metrics.CapHeight > 0 ? metrics.CapHeight : -metrics.Ascent * 0.7f;
        var baseline = cy + (capHeight / 2);
        var penX = cx - (width / 2);
        foreach (var (face, run) in Runs(text, primary))
        {
            using var runFont = ReferenceEquals(face, primary) ? null : NewFont(face, size);
            var f = runFont ?? font;
            canvas.DrawText(run, penX, baseline, SKTextAlign.Left, f, paint);
            penX += f.MeasureText(run);
        }
    }

    /// <summary>Draws an emoji glyph centred in <paramref name="rect"/> at <paramref name="size"/> pixels.</summary>
    public void DrawEmoji(SKCanvas canvas, string glyph, SKRect rect, float size, SKPaint paint)
    {
        // Variation selectors need shaping to apply; colour emoji faces map the base code point to the colour glyph.
        var text = glyph.Replace("️", string.Empty, StringComparison.Ordinal);
        var face = FaceFor(Rune.GetRuneAt(text, 0).Value, SKFontStyleWeight.Normal, Semibold);
        using var font = NewFont(face, size);
        font.Edging = SKFontEdging.Antialias;
        var width = font.MeasureText(text, out var bounds);
        // Centre the glyph's ink box when it has one, else its advance and line box.
        float x;
        float baseline;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            x = rect.MidX - bounds.MidX;
            baseline = rect.MidY - bounds.MidY;
        }
        else
        {
            x = rect.MidX - (width / 2);
            baseline = rect.MidY - ((font.Metrics.Ascent + font.Metrics.Descent) / 2);
        }

        canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
    }

    public static float LineHeight(SKFont font)
    {
        var m = font.Metrics;
        var height = m.Descent - m.Ascent + m.Leading;
        return height > 0 ? height : font.Size * 1.2f;
    }

    public static SKFont NewFont(SKTypeface face, float size) => new(face, size)
    {
        Edging = SKFontEdging.Antialias,
        Subpixel = true,
        Hinting = SKFontHinting.None,
    };

    /// <summary>Splits text into runs that share a typeface (primary, or a fallback for missing characters).</summary>
    public IReadOnlyList<(SKTypeface Face, string Text)> Runs(string text, SKTypeface primary)
    {
        var runs = new List<(SKTypeface, string)>();
        if (string.IsNullOrEmpty(text))
        {
            return runs;
        }

        var builder = new StringBuilder();
        SKTypeface? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            // Joiners and variation selectors stay with the previous character.
            var face = current is not null && IsCombining(rune) ? current : FaceFor(rune.Value, (SKFontStyleWeight)primary.FontWeight, primary);
            if (current is not null && !ReferenceEquals(face, current))
            {
                runs.Add((current, builder.ToString()));
                builder.Clear();
            }

            current = face;
            builder.Append(rune.ToString());
        }

        if (current is not null && builder.Length > 0)
        {
            runs.Add((current, builder.ToString()));
        }

        return runs;
    }

    private static bool IsCombining(Rune rune) =>
        rune.Value is 0x200D or (>= 0xFE00 and <= 0xFE0F) or (>= 0x1F3FB and <= 0x1F3FF) or (>= 0xE0020 and <= 0xE007F)
        || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark;

    private SKTypeface FaceFor(int codepoint, SKFontStyleWeight weight, SKTypeface primary)
    {
        if (codepoint < 0x80 || primary.ContainsGlyph(codepoint))
        {
            return primary;
        }

        return _fallbacks.GetOrAdd((codepoint, (int)weight), key =>
            SKFontManager.Default.MatchCharacter(primary.FamilyName, new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), null, key.Rune)
            ?? SKFontManager.Default.MatchCharacter(key.Rune)
            ?? primary);
    }

    private static SKTypeface Resolve(SKFontStyleWeight weight)
    {
        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        foreach (var family in Families)
        {
            var face = SKFontManager.Default.MatchFamily(family, style);
            if (face is not null)
            {
                return face;
            }
        }

        return SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
    }
}
