// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using Rivet.App.Controls;

namespace Rivet.App.Features.Sound;

/// <summary>
/// A collapsible group ("Options", "Apps in the list") drawn with the app's
/// own row and card styles, so it matches the panel in both themes.
/// </summary>
internal sealed class Disclosure : UserControl
{
    private readonly Button _header;
    private readonly ContentControl _body;
    private readonly SymbolIcon _chevron = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private bool _expanded;

    public Disclosure(object header, Control content, bool card = false, bool expanded = false)
    {
        _chevron.Bind(SymbolIcon.ForegroundProperty, _chevron.GetResourceObservable("TextSecondaryBrush").ToBinding());
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var headerContent = header is Control control ? control : new TextBlock { Text = header.ToString(), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        headerGrid.Children.Add(headerContent);
        Grid.SetColumn(_chevron, 1);
        headerGrid.Children.Add(_chevron);
        _header = new Button { Classes = { "row" }, Content = headerGrid, Padding = new Thickness(card ? 10 : 4, 6) };
        _header.Click += (_, _) => IsExpanded = !IsExpanded;
        if (header is string title)
        {
            AutomationProperties.SetName(_header, title);
        }

        _body = new ContentControl { Content = content, Margin = card ? new Thickness(10, 0, 10, 8) : new Thickness(4, 0, 0, 4) };
        var stack = new StackPanel { Spacing = 2, Children = { _header, _body } };
        Content = card ? new Border { Classes = { "card" }, Padding = new Thickness(0), Child = stack } : stack;
        IsExpanded = expanded;
    }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            _body.IsVisible = value;
            _chevron.Symbol = IconConverter.Parse(value ? "ChevronUp" : "ChevronDown");
        }
    }
}
