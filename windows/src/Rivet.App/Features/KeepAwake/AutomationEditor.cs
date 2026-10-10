// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Awake;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Awake;

/// <summary>
/// The automation editor shared by the panel card and Settings (spec
/// §3.18.4): condition tiles, Any/All (only with more than one condition),
/// the app list for the Applications condition and "Pause while locked".
/// </summary>
internal sealed class AutomationEditor : UserControl
{
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly bool _compact;
    private readonly StackPanel _root = new() { Spacing = 8 };
    private readonly List<IDisposable> _subscriptions = [];

    public AutomationEditor(IServiceProvider services, bool compact)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _compact = compact;
        Content = _root;
        AttachedToVisualTree += (_, _) =>
        {
            _subscriptions.Add(_settings.Observe(() => Dispatcher.UIThread.Post(Build),
                [.. KeepAwakeSettings.AutomationKeys, KeepAwakeSettings.PauseWhenLocked]));
            Build();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        };
        Build();
    }

    /// <summary>Off tiles use the chip colour of the duration chips; on tiles keep the theme's accent.</summary>
    private static void StyleTile(ToggleButton tile)
    {
        if (tile.IsChecked == true)
        {
            tile.ClearValue(TemplatedControl.BackgroundProperty);
        }
        else
        {
            tile.Bind(TemplatedControl.BackgroundProperty, tile.GetResourceObservable("ChipBrush").ToBinding());
        }
    }

    /// <summary>"Off" or the enabled conditions, for disclosure badges.</summary>
    public static string Summary(ISettingsStore settings)
    {
        var parts = new List<string>();
        if (settings.Get(KeepAwakeSettings.AutomationExternalDisplay)) parts.Add(L.Get("keepAwakeAutomation.externalDisplayToggle"));
        if (settings.Get(KeepAwakeSettings.AutomationPower)) parts.Add(L.Get("keepAwakeAutomation.powerToggle"));
        if (settings.Get(KeepAwakeSettings.AutomationApps)) parts.Add(L.Get("keepAwakeAutomation.runningAppsToggle"));
        return parts.Count == 0 ? L.Get("keepAwakeAutomation.automationOff") : string.Join(", ", parts);
    }

    private void Build()
    {
        _root.Children.Clear();
        var conditions = new (Setting<bool> Setting, string Icon, string Title, string Caption)[]
        {
            (KeepAwakeSettings.AutomationExternalDisplay, "DesktopMac", "keepAwakeAutomation.externalDisplayToggle", "keepAwakeAutomation.externalDisplayActive"),
            (KeepAwakeSettings.AutomationPower, "PlugConnected", "keepAwakeAutomation.powerToggle", "keepAwakeAutomation.powerActive"),
            (KeepAwakeSettings.AutomationApps, "Apps", "keepAwakeAutomation.runningAppsToggle", "keepAwakeAutomation.runningAppsActive"),
        };

        if (_compact)
        {
            var tiles = new UniformGrid { Columns = 3, ColumnSpacing = 6 };
            foreach (var (setting, icon, title, caption) in conditions)
            {
                var tile = new ToggleButton
                {
                    IsChecked = _settings.Get(setting),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Padding = new Thickness(4, 6),
                    Content = new StackPanel
                    {
                        Spacing = 3,
                        Children =
                        {
                            new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center },
                            new TextBlock { Text = L.Get(title), FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                };
                ToolTip.SetTip(tile, L.Get(caption));
                AutomationProperties.SetName(tile, L.Get(title));
                var captured = setting;
                StyleTile(tile);
                tile.IsCheckedChanged += (_, _) =>
                {
                    StyleTile(tile);
                    _settings.Set(captured, tile.IsChecked == true);
                };
                tiles.Children.Add(tile);
            }

            _root.Children.Add(tiles);
        }
        else
        {
            foreach (var (setting, icon, title, caption) in conditions)
            {
                _root.Children.Add(ToggleRow(setting, icon, L.Get(title), L.Get(caption)));
            }
        }

        var enabledCount = conditions.Count(c => _settings.Get(c.Setting));
        if (enabledCount > 1)
        {
            var requireAll = _settings.Get(KeepAwakeSettings.AutomationRequireAll);
            var any = new RadioButton { Content = L.Get("keepAwakeAutomation.matchAny"), GroupName = "automationMode" + GetHashCode(), IsChecked = !requireAll };
            var all = new RadioButton { Content = L.Get("keepAwakeAutomation.matchAll"), GroupName = "automationMode" + GetHashCode(), IsChecked = requireAll };
            any.IsCheckedChanged += (_, _) =>
            {
                if (any.IsChecked == true)
                {
                    _settings.Set(KeepAwakeSettings.AutomationRequireAll, false);
                }
            };
            all.IsCheckedChanged += (_, _) =>
            {
                if (all.IsChecked == true)
                {
                    _settings.Set(KeepAwakeSettings.AutomationRequireAll, true);
                }
            };
            _root.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { any, all } },
                    Caption(L.Get(requireAll ? "keepAwakeAutomation.automationCaptionAll" : "keepAwakeAutomation.automationCaption")),
                },
            });
        }

        if (_settings.Get(KeepAwakeSettings.AutomationApps))
        {
            _root.Children.Add(AppList());
        }

        _root.Children.Add(ToggleRow(KeepAwakeSettings.PauseWhenLocked, "LockClosed", L.Get("keepAwakeAutomation.pauseWhenLockedToggle"), L.Get("keepAwakeAutomation.pauseWhenLockedCaption")));
    }

    private Control AppList()
    {
        var apps = _settings.Get(KeepAwakeSettings.AutomationAppList);
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(new TextBlock { Text = L.Get("keepAwakeAutomation.runningAppsListTitle"), FontSize = _compact ? 11.5 : 13, FontWeight = FontWeight.SemiBold });
        foreach (var app in apps)
        {
            var remove = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Subtract, FontSize = 12 } };
            ToolTip.SetTip(remove, L.Get("keepAwakeAutomation.runningAppsRemoveButton"));
            AutomationProperties.SetName(remove, L.Get("keepAwakeAutomation.runningAppsRemoveButton") + " " + app);
            var name = app;
            remove.Click += (_, _) => _settings.Set(KeepAwakeSettings.AutomationAppList,
                _settings.Get(KeepAwakeSettings.AutomationAppList).Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).ToList());
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
            row.Children.Add(remove);
            var text = new TextBlock { Text = app, VerticalAlignment = VerticalAlignment.Center, FontSize = _compact ? 12 : 13, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            list.Children.Add(row);
        }

        var add = new Button
        {
            Padding = new Thickness(8, 3),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Add, FontSize = 13 }, new TextBlock { Text = L.Get("keepAwakeAutomation.runningAppsAddButton"), FontSize = _compact ? 12 : 13 } },
            },
        };
        add.Click += async (_, _) => await AddAppAsync();
        list.Children.Add(add);
        list.Children.Add(Caption(L.Get("keepAwakeAutomation.runningAppsListCaption")));
        return list;
    }

    private async Task AddAppAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        // The file dialog takes focus; keep the panel from closing behind it.
        using var keepOpen = _services.GetService<IAppShell>()?.KeepPanelOpen("keepAwake.addApp");
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.Get("win.keepAwake.appsPickerTitle"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Apps") { Patterns = ["*.exe"] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        var list = _settings.Get(KeepAwakeSettings.AutomationAppList).ToList();
        list.AddRange(files.Select(f => f.Name.ToLowerInvariant()));
        _settings.Set(KeepAwakeSettings.AutomationAppList, list);
    }

    private Control ToggleRow(Setting<bool> setting, string icon, string title, string caption)
    {
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = _settings.Get(setting) };
        toggle.IsCheckedChanged += (_, _) => _settings.Set(setting, toggle.IsChecked == true);
        AutomationProperties.SetName(toggle, title);
        if (_compact)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 6 };
            grid.Children.Add(new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(toggle);
            ToolTip.SetTip(grid, caption);
            return grid;
        }

        return new SettingsRow { Icon = icon, Title = title, Description = caption, Content = toggle };
    }

    private TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, Classes = { "caption" }, FontSize = _compact ? 11 : 12, TextWrapping = TextWrapping.Wrap };
        return block;
    }
}
