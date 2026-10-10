// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Capture.Text;

/// <summary>
/// The QR result panel (spec 01 §3.17): 320 DIPs wide, the decoded payload
/// in a selectable box, Copy, and Open link for a single http(s) URL. Esc or
/// a click elsewhere closes it.
/// </summary>
internal sealed class QrResultWindow : Window
{
    public const double PanelWidth = 320;
    private const double Shadow = 12;

    public QrResultWindow(string payload, Uri? link)
    {
        CaptureUi.MakeFloating(this);
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth + (2 * Shadow);
        ShowActivated = true;

        var icon = new SymbolIcon { Symbol = Symbol.QrCode, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable("AccentBrush").ToBinding());
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { icon, new TextBlock { Text = L.Get("Strings.qrResultTitle"), FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center } },
        };

        var text = new SelectableTextBlock { Text = payload, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        var box = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = new ScrollViewer { MaxHeight = 132, Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable("ChipBrush").ToBinding());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = CaptureUi.TextButton(L.Get("Strings.qrResultCopy"), () => CopyRequested?.Invoke(this, EventArgs.Empty), "Copy", accent: link is null);
        copy.IsDefault = link is null;
        buttons.Children.Add(copy);
        if (link is not null)
        {
            var open = CaptureUi.TextButton(L.Get("Strings.qrResultOpen"), () => OpenRequested?.Invoke(this, EventArgs.Empty), "Open", accent: true);
            open.IsDefault = true;
            buttons.Children.Add(open);
        }

        var plate = CaptureUi.Plate(new StackPanel { Spacing = 12, Children = { header, box, buttons } }, radius: 16, padding: new Thickness(16));
        plate.Margin = new Thickness(Shadow);
        Content = plate;
        Deactivated += (_, _) => Close();
    }

    public event EventHandler? CopyRequested;

    public event EventHandler? OpenRequested;

    public static double ShadowMargin => Shadow;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }
}
