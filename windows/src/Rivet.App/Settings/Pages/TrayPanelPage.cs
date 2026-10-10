// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.App.Shell;
using Rivet.App.Shell.Sections;
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Settings.Pages;

/// <summary>Tray icon behaviour, and the order and visibility of panel tabs and Utilities rows.</summary>
public sealed class TrayPanelPage : SettingsPage
{
    private readonly PanelRegistry _panel;
    private readonly FeatureRuntime _runtime;
    private readonly StackPanel _sections = new() { Spacing = 2 };
    private readonly StackPanel _tiles = new() { Spacing = 2 };

    public TrayPanelPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _panel = services.GetRequiredService<PanelRegistry>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        var shell = services.GetRequiredService<IShellService>();
        Track(Settings.Observe(() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild), ShellSettings.PanelSectionOrder, ShellSettings.PanelUtilityOrder, ShellSettings.PanelHiddenItems));

        Content = Stack(
            Header("win.shell.trayPanelTitle", "win.shell.panelLayoutIntro"),
            CardText(L.Get("win.shell.panelLayoutCaption"), _sections),
            Card("Strings.utilitiesSection", _tiles),
            Card("win.shell.trayIconTitle",
                _runtime.IsAvailable(FeatureIds.KeepAwake)
                    ? Toggle(ShellSettings.KeepAwakeRightClickToggle, "CursorClick", "Strings.keepAwakeRightClickToggle", "Strings.keepAwakeRightClickToggleCaption")
                    : null,
                Row("PanelBottom", L.Get("win.shell.trayPinGuideTitle"), L.Get("win.shell.trayPinGuideBody"),
                    ActionButton(L.Get("win.shell.trayPinGuideOpenSettings"), () => shell.OpenSystemSettings("ms-settings:taskbar")))));
        Rebuild();
    }

    private void Rebuild()
    {
        var hidden = PanelLayout.ParseHidden(Settings.Get(ShellSettings.PanelHiddenItems));

        var sections = PanelLayout.Order(
            _panel.Sections.Where(s => s.FeatureIds.Count == 0 || s.FeatureIds.Any(_runtime.IsAvailable)),
            s => s.Id, s => s.Order, Settings.Get(ShellSettings.PanelSectionOrder));
        _sections.Children.Clear();
        var visibleCount = sections.Count(s => !hidden.Contains(s.Id));
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var isHidden = hidden.Contains(section.Id);
            _sections.Children.Add(ItemRow(section.Icon, L.Get(section.TitleKey), i, sections.Count, !isHidden,
                canHide: isHidden || visibleCount > 1,
                move: delta => Move(ShellSettings.PanelSectionOrder, sections.Select(s => s.Id).ToList(), i, delta),
                setVisible: visible => SetHidden(section.Id, !visible)));
        }

        var tiles = PanelLayout.Order(_panel.Tiles.Where(t => _runtime.IsAvailable(t.FeatureId)), t => t.Id, t => t.Order, Settings.Get(ShellSettings.PanelUtilityOrder));
        _tiles.Children.Clear();
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            _tiles.Children.Add(ItemRow(tile.Icon, L.Get(tile.TitleKey), i, tiles.Count, !hidden.Contains("tile:" + tile.Id), canHide: true,
                move: delta => Move(ShellSettings.PanelUtilityOrder, tiles.Select(t => t.Id).ToList(), i, delta),
                setVisible: visible => SetHidden("tile:" + tile.Id, !visible)));
        }
    }

    private Control ItemRow(string icon, string title, int index, int count, bool visible, bool canHide, Action<int> move, Action<bool> setVisible)
    {
        var up = new Button { Classes = { "icon" }, IsEnabled = index > 0, Content = new SymbolIcon { Symbol = Symbol.ChevronUp, FontSize = 14 } };
        var down = new Button { Classes = { "icon" }, IsEnabled = index < count - 1, Content = new SymbolIcon { Symbol = Symbol.ChevronDown, FontSize = 14 } };
        up.Click += (_, _) => move(-1);
        down.Click += (_, _) => move(1);
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = visible, IsEnabled = canHide };
        toggle.IsCheckedChanged += (_, _) => setVisible(toggle.IsChecked == true);
        AutomationProperties.SetName(toggle, title);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 2) };
        grid.Children.Add(up);
        Grid.SetColumn(down, 1);
        grid.Children.Add(down);
        var symbol = new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        Grid.SetColumn(symbol, 2);
        grid.Children.Add(symbol);
        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Opacity = visible ? 1 : 0.55 };
        Grid.SetColumn(text, 3);
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 4);
        grid.Children.Add(toggle);
        return grid;
    }

    private void Move(Setting<string> orderSetting, List<string> ids, int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= ids.Count)
        {
            return;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);
        Settings.Set(orderSetting, PanelLayout.ToCsv(ids));
    }

    private void SetHidden(string id, bool hidden)
    {
        var set = PanelLayout.ParseHidden(Settings.Get(ShellSettings.PanelHiddenItems));
        if (hidden ? set.Add(id) : set.Remove(id))
        {
            Settings.Set(ShellSettings.PanelHiddenItems, PanelLayout.ToCsv(set.OrderBy(s => s, StringComparer.Ordinal)));
        }
    }
}
