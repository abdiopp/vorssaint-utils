// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Sound;

/// <summary>Small builders shared by the sound views (panel and Settings).</summary>
internal static class SoundUi
{
    private static readonly ConditionalWeakTable<PixelBuffer, Bitmap> Bitmaps = new();

    public static SymbolIcon Icon(string name, double size = 16, string? brushKey = null)
    {
        var icon = new SymbolIcon { Symbol = IconConverter.Parse(name), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (brushKey is not null)
        {
            icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable(brushKey).ToBinding());
        }

        return icon;
    }

    public static Button IconButton(string icon, string tooltip, Action onClick, double size = 15)
    {
        var button = new Button { Classes = { "icon" }, Content = Icon(icon, size), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static void SetIcon(Button button, string icon, double size = 15) => button.Content = Icon(icon, size);

    public static TextBlock Text(string text, string? classes = null, double? size = null)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (classes is not null)
        {
            foreach (var c in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                block.Classes.Add(c);
            }
        }

        if (size is { } fontSize)
        {
            block.FontSize = fontSize;
        }

        return block;
    }

    public static TextBlock Caption(string text, string brushKey = "TextSecondaryBrush")
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brushKey).ToBinding());
        return block;
    }

    public static TextBlock SectionTitle(string key) =>
        new() { Text = L.Get(key).ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, Margin = new Thickness(4, 0) };

    public static Border Card(Control child, Thickness? padding = null) =>
        new() { Classes = { "card" }, Padding = padding ?? new Thickness(10, 8), Child = child };

    /// <summary>A label and a compact switch; returns the switch so callers can bind it.</summary>
    public static Grid SwitchRow(string title, string? caption, ToggleSwitch toggle, string? icon = null)
    {
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        if (!string.IsNullOrEmpty(caption))
        {
            texts.Children.Add(Caption(caption));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(icon is null ? "*,Auto" : "22,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 3) };
        var column = 0;
        if (icon is not null)
        {
            var symbol = Icon(icon, 15, "AccentBrush");
            symbol.VerticalAlignment = VerticalAlignment.Top;
            symbol.Margin = new Thickness(0, 2, 0, 0);
            grid.Children.Add(symbol);
            column = 1;
        }

        Grid.SetColumn(texts, column);
        grid.Children.Add(texts);
        toggle.Classes.Add("compact");
        AutomationProperties.SetName(toggle, title);
        Grid.SetColumn(toggle, column + 1);
        grid.Children.Add(toggle);
        return grid;
    }

    public static Bitmap? Bitmap(PixelBuffer? pixels)
    {
        if (pixels is null)
        {
            return null;
        }

        return Bitmaps.GetValue(pixels, static p => ImageInterop.ToBitmap(p));
    }

    public static IBrush? Brush(Control owner, string key) =>
        owner.TryFindResource(key, owner.ActualThemeVariant, out var value) ? value as IBrush : null;
}
