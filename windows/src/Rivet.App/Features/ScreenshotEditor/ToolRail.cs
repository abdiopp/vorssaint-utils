// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.ScreenshotEditor;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The vertical tool rail (spec 01 §3.10.2): one 33 × 29 button per tool in
/// the configured order, a shortcut badge on each, and a gear that opens the
/// tool order and shortcut editor.
/// </summary>
internal sealed class ToolRail : UserControl
{
    private readonly EditorController _controller;
    private readonly StackPanel _buttons = new() { Spacing = 2 };
    private readonly Dictionary<EditorTool, (Button Button, TextBlock Badge)> _items = [];
    private readonly IKeyNameProvider? _keyNames;

    public ToolRail(EditorController controller)
    {
        _controller = controller;
        _keyNames = controller.Services.GetService<IKeyNameProvider>();
        var gear = EditorChrome.IconButton("Settings", L.Get("screenshot.toolShortcutsTitle"), () => { }, 15, 33, 29);
        gear.Flyout = new Flyout
        {
            Placement = PlacementMode.RightEdgeAlignedTop,
            ShowMode = FlyoutShowMode.Standard,
            Content = new LazyContent(() => new ToolOrderEditor(controller.Services, compact: true) { Width = 340 }),
        };
        var stack = new StackPanel
        {
            Spacing = 2,
            Children = { _buttons, EditorChrome.HorizontalDivider(), gear },
        };
        var scroller = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Content = EditorChrome.Capsule(scroller, 15, new Thickness(5));
        Rebuild();
        controller.Session.ToolChanged += (_, _) => UpdateActive();
        controller.ToolKeysChanged += (_, _) => Rebuild();
    }

    public void Rebuild()
    {
        _buttons.Children.Clear();
        _items.Clear();
        foreach (var tool in _controller.ToolOrder)
        {
            var badge = new TextBlock
            {
                FontSize = 8.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = EditorChrome.Primary,
                Opacity = 0.55,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 2, 0),
                IsHitTestVisible = false,
            };
            var icon = EditorChrome.Icon(EditorTools.Icon(tool), 17, tool == EditorTool.Redact ? FluentIcons.Common.IconVariant.Filled : FluentIcons.Common.IconVariant.Regular);
            var captured = tool;
            var button = new Button
            {
                Width = 33,
                Height = 29,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Focusable = false,
                Classes = { "editorIcon" },
                Content = new Grid { Width = 33, Height = 29, Children = { new Border { Child = icon, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, badge } },
            };
            button.Click += (_, _) => ToolPicked?.Invoke(this, captured);
            button.PointerEntered += (_, _) => badge.Opacity = 0.9;
            button.PointerExited += (_, _) => badge.Opacity = _controller.Session.Tool == captured ? 0.9 : 0.55;
            _buttons.Children.Add(button);
            _items[tool] = (button, badge);
        }

        UpdateBadges();
        UpdateActive();
    }

    /// <summary>A tool button was clicked (the window commits text editing first).</summary>
    public event EventHandler<EditorTool>? ToolPicked;

    private void UpdateBadges()
    {
        var order = _controller.ToolOrder;
        var bindings = _controller.ToolBindings;
        var enabled = _controller.ToolShortcutsEnabled;
        foreach (var (tool, (button, badge)) in _items)
        {
            var key = ToolKeyMap.Badge(order, bindings, tool, enabled, KeyName, _controller.KeyboardLayout.DigitTypedBy);
            badge.Text = key ?? string.Empty;
            var title = L.Get(EditorTools.TitleKey(tool));
            var tip = key is null ? title : $"{title}  ({key})";
            ToolTip.SetTip(button, tip);
            AutomationProperties.SetName(button, title);
        }
    }

    private string KeyName(KeyChord chord) => chord.ToDisplayString(_keyNames);

    private void UpdateActive()
    {
        var active = _controller.Session.Tool;
        foreach (var (tool, (button, badge)) in _items)
        {
            var isActive = tool == active;
            button.Background = isActive ? EditorChrome.AccentTint(0.22) : Brushes.Transparent;
            if (button.Content is Grid { Children: [Border { Child: SymbolIcon icon }, ..] })
            {
                icon.Foreground = isActive ? EditorChrome.AccentBrush : EditorChrome.Primary;
            }

            badge.Opacity = isActive ? 0.9 : 0.55;
        }
    }
}

/// <summary>Builds a flyout's content the first time it opens (keeps the window quick to open).</summary>
internal sealed class LazyContent : ContentControl
{
    private readonly Func<Control> _factory;

    public LazyContent(Func<Control> factory)
    {
        _factory = factory;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Content ??= _factory();
    }
}
