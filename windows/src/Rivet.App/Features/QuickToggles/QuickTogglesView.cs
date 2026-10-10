// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Toggles;

namespace Rivet.App.Features.Toggles;

/// <summary>
/// The quick toggles list (spec 06 §3.10): one-click rows with the running /
/// failed state machine. In the tray section it can be edited (order,
/// visibility, reset); hosted in the quick panel it cannot.
/// </summary>
public sealed class QuickTogglesView : UserControl
{
    private readonly QuickToggleCatalog _catalog;
    private readonly ISettingsStore _settings;
    private readonly IAppShell? _shell;
    private readonly bool _editable;
    private readonly bool _editOnly;
    private readonly Action? _closeSurface;
    private readonly ActionSource _source;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly Button _edit = new() { Classes = { "link" } };
    private readonly Button _reset = new() { Classes = { "link" } };
    private IDisposable? _subscription;
    private bool _editing;

    /// <param name="closeSurface">Hides the panel that hosts the list (lock, display off, screen saver, sleep).</param>
    /// <param name="editOnly">Always in edit mode, without a title (the Settings page).</param>
    public QuickTogglesView(IServiceProvider services, bool editable, Action? closeSurface = null, ActionSource source = ActionSource.Panel, bool editOnly = false)
    {
        _editOnly = editOnly;
        _editing = editOnly;
        _catalog = services.GetRequiredService<QuickToggleCatalog>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _shell = services.GetService<IAppShell>();
        _editable = editable;
        _closeSurface = closeSurface ?? (() => _shell?.ClosePanel());
        _source = source;

        _edit.Content = L.Get("win.quickToggles.edit");
        _edit.Click += (_, _) =>
        {
            _editing = !_editing;
            Rebuild();
        };
        _reset.Content = L.Get("win.quickToggles.reset");
        _reset.Click += (_, _) =>
        {
            _catalog.Reset();
            Rebuild();
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(4, 0, 4, 4), IsVisible = editable };
        header.Children.Add(new TextBlock { Text = L.Get("quickToggles.pageTitle").ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, VerticalAlignment = VerticalAlignment.Center, IsVisible = !editOnly });
        _edit.IsVisible = !editOnly;
        Grid.SetColumn(_reset, 1);
        header.Children.Add(_reset);
        Grid.SetColumn(_edit, 2);
        header.Children.Add(_edit);
        Content = new StackPanel { Spacing = 4, Children = { header, new Border { Classes = { "card" }, Padding = new Thickness(4), Child = _rows } } };
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _catalog.Service.Changed += OnChanged;
        _subscription = _settings.Observe(() => Dispatcher.UIThread.Post(Rebuild), [QuickToggleSettings.Order, .. QuickToggleSettings.AllVisibilityKeys]);
        _ = RefreshAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _catalog.Service.Changed -= OnChanged;
        _subscription?.Dispose();
        _subscription = null;
        _editing = _editOnly;
        base.OnDetachedFromVisualTree(e);
    }

    private async Task RefreshAsync()
    {
        await _catalog.RefreshEjectableAsync().ConfigureAwait(true);
        Rebuild();
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        _edit.Content = L.Get(_editing ? "win.quickToggles.done" : "win.quickToggles.edit");
        _reset.IsVisible = _editing;
        _rows.Children.Clear();
        var ids = _catalog.Listed(includeHidden: _editing);
        foreach (var id in ids)
        {
            _rows.Children.Add(_editing ? EditRow(id, ids) : ActionRow(id));
        }

        if (ids.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = L.Get("win.quickToggles.allHidden"), Classes = { "caption" }, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
        }
    }

    private Control Plate(QuickToggleId id, bool failed) => new Border
    {
        Width = 30,
        Height = 30,
        CornerRadius = new CornerRadius(8),
        Classes = { "iconTile" },
        VerticalAlignment = VerticalAlignment.Center,
        Child = ClipboardUi.Icon(_catalog.Icon(id), 16, failed ? "DangerBrush" : "AccentBrush"),
    };

    private Control ActionRow(QuickToggleId id)
    {
        var state = _catalog.Service.StateOf(id);
        var failed = state == QuickToggleState.Failed;
        var caption = new TextBlock
        {
            Text = failed ? L.Get("quickToggles.actionFailed") : _catalog.Caption(id),
            Classes = { "caption" },
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        };
        if (failed && this.TryFindResource("DangerBrush", ActualThemeVariant, out var danger) && danger is IBrush brush)
        {
            caption.Foreground = brush;
        }

        var title = _catalog.Title(id);
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = title, Classes = { "rowTitle" } }, caption } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        grid.Children.Add(Plate(id, failed));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var row = new Button
        {
            Classes = { "row" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 5),
            Content = grid,
            IsEnabled = state != QuickToggleState.Running,
            Opacity = state == QuickToggleState.Running ? 0.55 : 1,
        };
        AutomationProperties.SetName(row, title);
        AutomationProperties.SetHelpText(row, caption.Text);
        row.Click += async (_, _) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            await _catalog.RunAsync(id, owner, QuickToggleCatalog.ClosesSurface(id) ? _closeSurface : null, _source).ConfigureAwait(true);
            Rebuild();
        };
        return row;
    }

    private Control EditRow(QuickToggleId id, IReadOnlyList<QuickToggleId> ids)
    {
        var index = ids.ToList().IndexOf(id);
        var up = ClipboardUi.IconButton("ArrowUp", L.Get("clipboard.moveUp"), () => _catalog.Move(id, -1), 12);
        up.IsEnabled = index > 0;
        var down = ClipboardUi.IconButton("ArrowDown", L.Get("clipboard.moveDown"), () => _catalog.Move(id, 1), 12);
        down.IsEnabled = index < ids.Count - 1;
        var visible = new ToggleSwitch { Classes = { "compact" }, IsChecked = _catalog.IsVisible(id), VerticalAlignment = VerticalAlignment.Center };
        var title = _catalog.Title(id);
        AutomationProperties.SetName(visible, title);
        visible.IsCheckedChanged += (_, _) => _catalog.SetVisible(id, visible.IsChecked == true);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), ColumnSpacing = 6, Margin = new Thickness(4, 3) };
        grid.Children.Add(up);
        Grid.SetColumn(down, 1);
        grid.Children.Add(down);
        var plate = Plate(id, false);
        Grid.SetColumn(plate, 2);
        grid.Children.Add(plate);
        var label = new TextBlock { Text = title, Classes = { "rowTitle" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 3);
        grid.Children.Add(label);
        Grid.SetColumn(visible, 4);
        grid.Children.Add(visible);
        return grid;
    }
}
