// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>The editor's styles and theme brushes (added to each editor window).</summary>
public sealed class EditorStyles : Avalonia.Styling.Styles
{
    public EditorStyles() => AvaloniaXamlLoader.Load(this);
}

/// <summary>Colours the custom-drawn timeline and stage use, per theme.</summary>
public sealed record EditorPalette(bool Dark, Color Accent)
{
    public static readonly Color TextPurple = Color.FromRgb(0xAF, 0x52, 0xDE);
    public static readonly Color ImageOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    public static readonly Color BlurTeal = Color.FromRgb(0x30, 0xB0, 0xC7);
    public static readonly Color CutRed = Color.FromRgb(0xFF, 0x3B, 0x30);
    public static readonly Color SeamOrange = Color.FromRgb(0xFF, 0x95, 0x00);
    public static readonly Color SystemBlue = Color.FromRgb(0x0A, 0x84, 0xFF);

    public Color Ink => Dark ? Colors.White : Colors.Black;

    public Color KindAccent(LaneKind kind) => kind switch
    {
        LaneKind.Text => TextPurple,
        LaneKind.Image => ImageOrange,
        LaneKind.Blur => BlurTeal,
        _ => Accent,
    };

    /// <summary>Ink at an alpha (0…1): white in dark mode, black in light mode.</summary>
    public IBrush InkBrush(double alpha) => new SolidColorBrush(WithAlpha(Ink, alpha));

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    public static EditorPalette For(StyledElement element)
    {
        var dark = element.ActualThemeVariant == ThemeVariant.Dark;
        var accent = SystemBlue;
        if (Application.Current?.TryGetResource("SystemAccentColor", Application.Current.ActualThemeVariant, out var value) == true && value is Color color)
        {
            accent = color;
        }

        return new EditorPalette(dark, accent);
    }
}
