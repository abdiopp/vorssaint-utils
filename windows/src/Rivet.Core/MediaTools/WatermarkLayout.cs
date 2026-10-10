// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.MediaTools;

/// <summary>Where each part of a watermark goes on the canvas (pixels, y down).</summary>
public sealed record WatermarkPlacement(
    double LogoX, double LogoY, double LogoWidth, double LogoHeight,
    double TextX, double TextY, double TextWidth, double TextHeight,
    double FontSize, double ShadowBlur, double Opacity, double ShadowOpacity)
{
    public bool HasLogo => LogoWidth > 0 && LogoHeight > 0;

    public bool HasText => TextWidth > 0;
}

/// <summary>
/// Watermark geometry (spec 07 §6.6, converted to y-down): side = min(W, H);
/// gap = max(6, side·0.015); the logo fits a square of max(12, side·scale);
/// text is semibold at clamp(side·0.045, 12, 96) px with a shadow blur of
/// max(2, size·0.14), 1 px down, black at 0.45·opacity. Content wider than
/// W − 2m shrinks (font ≥ 8, blur ≥ 1). Logo first, text after it, vertically centred.
/// </summary>
public static class WatermarkLayout
{
    /// <param name="measure">Text size in pixels at a font size: (width, height).</param>
    public static WatermarkPlacement? Compute(int width, int height, WatermarkOptions options, MediaSize? logo, Func<double, (double Width, double Height)> measure)
    {
        var hasText = options.HasText;
        var hasLogo = options.HasLogo && logo is { Width: > 0, Height: > 0 };
        if (!hasText && !hasLogo)
        {
            return null;
        }

        double side = Math.Min(width, height);
        double m = options.Margin <= 0 ? 32 : options.Margin;
        var gap = Math.Max(6, side * 0.015);

        double logoW = 0, logoH = 0;
        if (hasLogo)
        {
            var box = Math.Max(12, side * options.Scale);
            var l = logo!.Value;
            var fit = Math.Min(box / l.Width, box / l.Height);
            logoW = l.Width * fit;
            logoH = l.Height * fit;
        }

        var fontSize = Math.Clamp(side * 0.045, 12, 96);
        var blur = Math.Max(2, fontSize * 0.14);
        var (textW, textH) = hasText ? measure(fontSize) : (0, 0);

        var contentW = logoW + textW + (hasLogo && hasText ? gap : 0);
        var available = width - (2 * m);
        if (contentW > available && contentW > 0 && available > 0)
        {
            var factor = available / contentW;
            logoW *= factor;
            logoH *= factor;
            gap *= factor;
            fontSize = Math.Max(8, fontSize * factor);
            blur = Math.Max(1, blur * factor);
            (textW, textH) = hasText ? measure(fontSize) : (0, 0);
            contentW = logoW + textW + (hasLogo && hasText ? gap : 0);
        }

        var contentH = Math.Max(logoH, textH);
        var maxX = Math.Max(m, width - contentW - m);
        var bottomY = Math.Max(m, height - contentH - m);
        var (x, y) = options.Position switch
        {
            WatermarkPosition.TopLeft => (m, m),
            WatermarkPosition.TopRight => (maxX, m),
            WatermarkPosition.Center => (Math.Max(m, (width - contentW) / 2), Math.Max(m, (height - contentH) / 2)),
            WatermarkPosition.BottomLeft => (m, bottomY),
            _ => (maxX, bottomY),
        };

        var logoX = x;
        var logoY = y + ((contentH - logoH) / 2);
        var textX = x + logoW + (hasLogo && hasText ? gap : 0);
        var textY = y + ((contentH - textH) / 2);
        return new WatermarkPlacement(logoX, logoY, logoW, logoH, textX, textY, textW, textH, fontSize, blur, options.Opacity, 0.45 * options.Opacity);
    }
}
