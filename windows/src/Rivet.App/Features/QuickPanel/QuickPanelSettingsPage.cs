// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Clipboard;
using Rivet.Core.Localization;
using Rivet.Core.QuickPanel;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.QuickPanel;

/// <summary>
/// Settings → Quick panel: open it now, its shortcut, and the tools (show or
/// hide each one, move it up or down, reset). The panel's own edit mode does
/// the same with drag and drop.
/// </summary>
public sealed class QuickPanelSettingsPage : SettingsPage
{
    private readonly QuickPanelCatalog _catalog;
    private readonly StackPanel _tools = new() { Spacing = 2 };

    public QuickPanelSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _catalog = services.GetRequiredService<QuickPanelCatalog>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        var host = services.GetRequiredService<QuickPanelHost>();
        Content = Stack(
            Header("Strings.launcherName", "Strings.launcherCaption"),
            Card(null,
                Row("Grid", L.Get("Strings.launcherOpenNow"), L.Get("Strings.launcherKeysHint"), ActionButton(L.Get("Strings.launcherOpenNow"), host.Toggle, "Open", accent: true)),
                Toggle(QuickPanelSettings.ShortcutEnabled, "Keyboard", "win.quickPanel.shortcutToggle"),
                shortcuts.Find(QuickPanelModule.RoleId) is { } role ? new ShortcutRoleRow(role) : null),
            Card("win.quickPanel.toolsTitle",
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Caption(L.Get("Strings.launcherEditHint")),
                        _tools,
                        ActionButton(L.Get("win.quickToggles.reset"), () => _catalog.Reset(), "ArrowReset"),
                    },
                }));
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RebuildTools), QuickPanelSettings.ItemOrder, QuickPanelSettings.HiddenItems));
        RebuildTools();
    }

    private void RebuildTools()
    {
        _tools.Children.Clear();
        var tiles = _catalog.Ordered(includeHidden: true);
        var hidden = _catalog.Hidden;
        if (tiles.Count == 0)
        {
            _tools.Children.Add(Caption(L.Get("win.quickPanel.noTools")));
            return;
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var index = i;
            var title = _catalog.Title(tile);
            var up = ClipboardUi.IconButton("ArrowUp", L.Get("clipboard.moveUp"), () => _catalog.MoveTo(tile.Id, index - 1), 12);
            up.IsEnabled = i > 0;
            AutomationProperties.SetName(up, $"{L.Get("clipboard.moveUp")} · {title}");
            var down = ClipboardUi.IconButton("ArrowDown", L.Get("clipboard.moveDown"), () => _catalog.MoveTo(tile.Id, index + 1), 12);
            down.IsEnabled = i < tiles.Count - 1;
            AutomationProperties.SetName(down, $"{L.Get("clipboard.moveDown")} · {title}");
            var visible = new ToggleSwitch { Classes = { "compact" }, IsChecked = !hidden.Contains(tile.Id) };
            AutomationProperties.SetName(visible, $"{L.Get("Strings.panelShowItem")} · {title}");
            visible.IsCheckedChanged += (_, _) => _catalog.SetHidden(tile.Id, visible.IsChecked != true);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 1) };
            grid.Children.Add(new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), Classes = { "caption", "tertiary" }, Width = 18, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right });
            var icon = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Classes = { "iconTile" }, Child = ClipboardUi.Icon(tile.Icon, 14, "AccentBrush") };
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);
            var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = hidden.Contains(tile.Id) ? 0.5 : 1 };
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);
            Grid.SetColumn(up, 3);
            grid.Children.Add(up);
            Grid.SetColumn(down, 4);
            grid.Children.Add(down);
            Grid.SetColumn(visible, 5);
            grid.Children.Add(visible);
            _tools.Children.Add(grid);
        }
    }
}
