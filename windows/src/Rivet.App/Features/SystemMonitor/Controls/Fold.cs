// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;

namespace Rivet.App.Features.SystemMonitor.Controls;

/// <summary>
/// A disclosure: a chevron header that shows its content below when open.
/// The content is created only while open, so a folded per-app list costs
/// nothing ("while folded no per-app sampling happens", spec §3.2).
/// </summary>
public sealed class Fold : UserControl
{
    private readonly Func<Control> _create;
    private readonly ContentControl _body = new();
    private readonly SymbolIcon _chevron = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly double _indent;
    private bool _open;

    public Fold(string title, Func<Control> create, bool initiallyOpen, double fontSize = 11.5, double indent = 14, bool secondary = true)
    {
        _create = create;
        _indent = indent;
        _chevron.Bind(SymbolIcon.ForegroundProperty, _chevron.GetResourceObservable("TextSecondaryBrush").ToBinding());
        var label = new TextBlock { Text = title, FontSize = fontSize, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (secondary)
        {
            label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("TextSecondaryBrush").ToBinding());
        }

        var header = new Button
        {
            Classes = { "row" },
            Padding = new Thickness(2, 3),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _chevron, label } },
        };
        AutomationProperties.SetName(header, title);
        header.Click += (_, _) => IsOpen = !_open;
        Content = new StackPanel { Spacing = 2, Children = { header, _body } };
        IsOpen = initiallyOpen;
    }

    public bool IsOpen
    {
        get => _open;
        set
        {
            _open = value;
            _chevron.Symbol = IconConverter.Parse(value ? "ChevronDown" : "ChevronRight");
            _body.Content = value ? _create() : null;
            if (_body.Content is Control content)
            {
                content.Margin = new Thickness(_indent, 0, 0, 0);
            }
        }
    }
}
