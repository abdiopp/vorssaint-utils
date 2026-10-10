// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Shell.Sections;

/// <summary>
/// The Utilities tab: one row per installed tool. Overlay tools close the
/// panel and launch; hosted tools replace the list in place (with a back
/// button) until the user returns. The hosted tool survives the panel closing.
/// </summary>
public sealed class UtilitiesSection : UserControl
{
    /// <summary>The tool hosted in place, kept across panel openings (as on macOS).</summary>
    private static string? _hostedTileId;

    private readonly IServiceProvider _services;
    private readonly PanelRegistry _registry;
    private readonly FeatureRuntime _runtime;
    private readonly ISettingsStore _settings;
    private readonly ActionRegistry _actions;
    private readonly ShortcutManager _shortcuts;
    private readonly IAppShell _shell;
    private IDisposable? _keepOpen;
    private DispatcherTimer? _liveTimer;

    public UtilitiesSection(IServiceProvider services)
    {
        _services = services;
        _registry = services.GetRequiredService<PanelRegistry>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _actions = services.GetRequiredService<ActionRegistry>();
        _shortcuts = services.GetRequiredService<ShortcutManager>();
        _shell = services.GetRequiredService<IAppShell>();
        Build();
    }

    /// <summary>Tiles in panel order, minus uninstalled and hidden ones.</summary>
    public static IReadOnlyList<PanelTileDescriptor> VisibleTiles(PanelRegistry registry, FeatureRuntime runtime, ISettingsStore settings)
    {
        var hidden = PanelLayout.ParseHidden(settings.Get(ShellSettings.PanelHiddenItems));
        var installed = registry.Tiles.Where(t => runtime.IsAvailable(t.FeatureId));
        return PanelLayout.Order(installed, t => t.Id, t => t.Order, settings.Get(ShellSettings.PanelUtilityOrder))
            .Where(t => !hidden.Contains("tile:" + t.Id))
            .ToList();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keepOpen?.Dispose();
        _keepOpen = null;
        _liveTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Build()
    {
        _keepOpen?.Dispose();
        _keepOpen = null;
        _liveTimer?.Stop();
        PanelTileDescriptor? hosted = null;
        if (_hostedTileId is { } id)
        {
            var accessory = id.EndsWith("#accessory", StringComparison.Ordinal);
            var tile = _registry.Tiles.FirstOrDefault(t => t.Id == (accessory ? id[..^"#accessory".Length] : id) && _runtime.IsAvailable(t.FeatureId));
            hosted = accessory
                ? tile?.Accessory?.CreateHostedView is { } view ? tile with { TitleKey = tile.Accessory.TitleKey, Icon = tile.Accessory.Icon, CreateHostedView = view } : null
                : tile?.CreateHostedView is not null ? tile : null;
        }

        Content = hosted is null ? BuildList() : BuildHosted(hosted);
    }

    private Control BuildList()
    {
        var tiles = VisibleTiles(_registry, _runtime, _settings);
        var stack = new StackPanel { Spacing = 0 };
        var live = new List<LiveRow>();
        foreach (var tile in tiles)
        {
            var row = BuildRow(tile, out var liveRow);
            if (tile.LiveCaption is not null || tile.LiveTitle is not null || tile.LiveIcon is not null)
            {
                live.Add(liveRow);
            }

            stack.Children.Add(row);
            if (tile.Accessory is { } accessory && (accessory.IsVisible?.Invoke() ?? true))
            {
                stack.Children.Add(BuildAccessory(tile, accessory));
            }
        }

        if (live.Count > 0)
        {
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveTimer.Tick += (_, _) => UpdateLive(live);
            _liveTimer.Start();
            UpdateLive(live);
        }

        if (tiles.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = L.Get("win.shell.noResults"), Classes = { "caption" }, Margin = new Thickness(8) });
        }

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L.Get("Strings.utilitiesSection").ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, Margin = new Thickness(4, 0) },
                new Border { Classes = { "card" }, Padding = new Thickness(4), Child = stack },
            },
        };
    }

    private sealed record LiveRow(PanelTileDescriptor Tile, TextBlock Title, TextBlock Caption, SymbolIcon Icon);

    private static void UpdateLive(List<LiveRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Tile.LiveCaption is { } readCaption)
            {
                var text = readCaption();
                row.Caption.Text = text;
                row.Caption.IsVisible = !string.IsNullOrEmpty(text);
            }

            if (row.Tile.LiveTitle is { } readTitle)
            {
                row.Title.Text = readTitle() ?? L.Get(row.Tile.TitleKey);
            }

            if (row.Tile.LiveIcon is { } readIcon)
            {
                row.Icon.Symbol = IconConverter.Parse(readIcon() ?? row.Tile.Icon);
            }
        }
    }

    private Button BuildRow(PanelTileDescriptor tile, out LiveRow live)
    {
        var liveCaption = new TextBlock { Classes = { "caption" }, IsVisible = false, FontSize = 11 };
        var title = new TextBlock { Text = tile.LiveTitle?.Invoke() ?? L.Get(tile.TitleKey), Classes = { "rowTitle" }, VerticalAlignment = VerticalAlignment.Center };
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title, liveCaption } };

        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (tile.ShortcutRoleId is { } roleId && _shortcuts.Find(roleId) is { } role && _shortcuts.IsWanted(role))
        {
            var names = _services.GetService<IKeyNameProvider>();
            trailing.Children.Add(new Border
            {
                Classes = { "pill" },
                Child = new TextBlock { Text = _shortcuts.GetChord(role).ToDisplayString(names), Classes = { "tertiary" }, FontSize = 10 },
            });
        }

        trailing.Children.Add(new SymbolIcon { Symbol = Symbol.ChevronRight, FontSize = 12, Opacity = 0.55 });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,Auto"), ColumnSpacing = 8 };
        var icon = new SymbolIcon { Symbol = IconConverter.Parse(tile.LiveIcon?.Invoke() ?? tile.Icon), FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
        live = new LiveRow(tile, title, liveCaption, icon);
        icon.Bind(SymbolIcon.ForegroundProperty, this.GetResourceObservable("AccentBrush").ToBinding());
        grid.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        var button = new Button { Classes = { "row" }, Content = grid };
        AutomationProperties.SetName(button, title.Text);
        if (tile.CaptionKey is { } caption)
        {
            ToolTip.SetTip(button, L.Get(caption));
        }

        button.Click += (_, _) => Activate(tile);
        return button;
    }

    private Control BuildAccessory(PanelTileDescriptor tile, PanelTileAccessory accessory)
    {
        var button = new Button
        {
            Classes = { "row" },
            Padding = new Thickness(8, 4),
            Margin = new Thickness(34, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new SymbolIcon { Symbol = IconConverter.Parse(accessory.Icon), FontSize = 13 },
                    new TextBlock { Text = L.Get(accessory.TitleKey), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) =>
        {
            if (accessory.CreateHostedView is not null)
            {
                _hostedTileId = tile.Id + "#accessory";
                Content = BuildHosted(tile with { TitleKey = accessory.TitleKey, Icon = accessory.Icon, CreateHostedView = accessory.CreateHostedView });
            }
            else if (accessory.ActionId is { } actionId)
            {
                _shell.ClosePanel();
                DispatcherTimer.RunOnce(() => _ = _actions.InvokeAsync(actionId, ActionSource.Panel), TimeSpan.FromMilliseconds(200));
            }
        };
        return button;
    }

    private void Activate(PanelTileDescriptor tile)
    {
        if (tile.CreateHostedView is not null)
        {
            _hostedTileId = tile.Id;
            Build();
            return;
        }

        if (tile.ActionId is { } actionId)
        {
            _shell.ClosePanel();
            // Let the panel finish hiding so it never appears in the capture or steals focus.
            DispatcherTimer.RunOnce(() => _ = _actions.InvokeAsync(actionId, ActionSource.Panel), TimeSpan.FromMilliseconds(200));
        }
    }

    private Control BuildHosted(PanelTileDescriptor tile)
    {
        if (tile.HostedKeepsPanelOpen)
        {
            _keepOpen = _shell.KeepPanelOpen("hosted:" + tile.Id);
        }

        var back = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = Symbol.ChevronLeft, FontSize = 14 } };
        ToolTip.SetTip(back, L.Get("Strings.panelSettings"));
        back.Click += (_, _) =>
        {
            _hostedTileId = null;
            Build();
        };

        Control body;
        try
        {
            body = tile.CreateHostedView!(_services);
        }
        catch (Exception ex)
        {
            body = new TextBlock { Text = ex.Message, Classes = { "caption" } };
        }

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                back,
                new SymbolIcon { Symbol = IconConverter.Parse(tile.Icon), FontSize = 15, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = L.Get(tile.TitleKey), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        return new StackPanel { Spacing = 8, Children = { header, body } };
    }
}
