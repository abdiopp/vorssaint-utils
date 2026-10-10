// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Rivet.App.Controls;

/// <summary>
/// The app's working-codename mark (a rivet head: ring plus centre dot).
/// Kept deliberately simple; replace it with the real logo once the app is named.
/// </summary>
public sealed class AppGlyph : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<AppGlyph, IBrush?>(nameof(Foreground));

    static AppGlyph()
    {
        AffectsRender<AppGlyph>(ForegroundProperty);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var brush = Foreground ?? Brushes.Gray;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var ringWidth = size * 0.14;
        context.DrawEllipse(null, new Pen(brush, ringWidth), center, (size / 2) - (ringWidth / 2), (size / 2) - (ringWidth / 2));
        context.DrawEllipse(brush, null, center, size * 0.2, size * 0.2);
    }
}
