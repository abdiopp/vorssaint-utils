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
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Settings.Pages;

/// <summary>
/// The Features hub: install or uninstall every feature, apply a preset,
/// offer to remove features that were never switched on, and show what each
/// feature depends on. Uninstalling never deletes settings.
/// </summary>
public sealed class FeaturesPage : SettingsPage
{
    private readonly IServiceProvider _services;
    private readonly FeatureRuntime _runtime;
    private readonly SettingsPageRegistry _pages;
    private readonly Dictionary<string, ToggleSwitch> _switches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<FeatureGroup, TextBlock> _groupCounts = [];
    private readonly TextBlock _summary = new() { FontSize = 14, FontWeight = FontWeight.SemiBold };
    private readonly ProgressBar _progress = new() { Minimum = 0, Height = 4 };
    private readonly Border _restartCard;
    private readonly ContentControl _neverUsedHost = new();
    private bool _updating;

    public FeaturesPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _services = services;
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _pages = services.GetRequiredService<SettingsPageRegistry>();
        var relauncher = services.GetRequiredService<IRelauncher>();

        _restartCard = new Border
        {
            Classes = { "settingsCard" },
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 12,
                Children =
                {
                    new SymbolIcon { Symbol = Symbol.ArrowClockwise, FontSize = 22, VerticalAlignment = VerticalAlignment.Center },
                    Grid(Note(L.Get("hub.restartNote")), 1),
                    Grid(ActionButton(L.Get("hub.restartButton"), () => relauncher.RelaunchAndExit(), accent: true), 2),
                },
            },
        };
        _restartCard.Bind(Border.BackgroundProperty, _restartCard.GetResourceObservable("AccentSoftBrush").ToBinding());

        var install = ActionButton(L.Get("hub.installAllButton"), _runtime.InstallAll);
        var uninstall = ActionButton(L.Get("hub.uninstallAllButton"), _runtime.UninstallAll);
        var summaryCard = CardText(null, new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { _summary, Grid(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { install, uninstall } }, 1) },
                },
                _progress,
            },
        });

        var groups = new StackPanel { Spacing = 16 };
        foreach (var group in Enum.GetValues<FeatureGroup>())
        {
            groups.Children.Add(BuildGroup(group));
        }

        Content = Stack(
            Header("hub.pageTitle", "hub.intro"),
            _restartCard,
            summaryCard,
            _neverUsedHost,
            BuildPresets(),
            groups,
            Caption(L.Get("hub.footerNote")),
            Caption(L.Get("hub.energyHelp")));

        _runtime.Changed += OnRuntimeChanged;
        Refresh();
        if (services.GetRequiredService<SettingsNavigationState>().TakeReveal() is { } reveal)
        {
            Dispatcher.UIThread.Post(() => Reveal(reveal), DispatcherPriority.Loaded);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _runtime.Changed -= OnRuntimeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private static T Grid<T>(T control, int column)
        where T : Control
    {
        Avalonia.Controls.Grid.SetColumn(control, column);
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    private void OnRuntimeChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        _updating = true;
        foreach (var (id, toggle) in _switches)
        {
            toggle.IsChecked = _runtime.IsAvailable(id);
        }

        _updating = false;
        var installed = _runtime.AvailableCount;
        var total = FeatureCatalog.All.Count;
        _summary.Text = L.Format("hub.activeCountFormat", installed, total);
        _progress.Maximum = total;
        _progress.Value = installed;
        foreach (var (group, label) in _groupCounts)
        {
            var features = FeatureCatalog.InGroup(group).ToList();
            label.Text = $"{features.Count(_runtime.IsAvailable)}/{features.Count}";
        }

        _restartCard.IsVisible = _runtime.NeedsRestartToUnload;
        _neverUsedHost.Content = BuildNeverUsed();
    }

    private Control BuildGroup(FeatureGroup group)
    {
        var count = new TextBlock { Classes = { "caption", "mono" }, VerticalAlignment = VerticalAlignment.Center };
        _groupCounts[group] = count;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new Border
        {
            Classes = { "iconTile" },
            Child = new SymbolIcon { Symbol = IconConverter.Parse(FeatureCatalog.GroupIcon(group)), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        header.Children.Add(Grid(new TextBlock { Text = L.Get(FeatureCatalog.GroupTitleKey(group)), Classes = { "cardTitle" } }, 1));
        header.Children.Add(Grid(count, 2));

        var rows = new StackPanel { Spacing = 2 };
        var first = true;
        foreach (var feature in FeatureCatalog.InGroup(group))
        {
            if (!first)
            {
                rows.Children.Add(new Border { Classes = { "separator" }, Margin = new Thickness(8, 2) });
            }

            rows.Children.Add(BuildFeatureRow(feature));
            first = false;
        }

        return new Border { Classes = { "settingsCard" }, Child = new StackPanel { Children = { header, rows } } };
    }

    private Control BuildFeatureRow(FeatureDescriptor feature)
    {
        var title = new TextBlock { Text = L.Get(feature.TitleKey), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { title } };
        if (feature.IsBeta)
        {
            titleLine.Children.Add(new Border { Classes = { "pill", "beta" }, Child = new TextBlock { Text = L.Get("Strings.betaBadge"), Foreground = Brushes.DarkOrange } });
        }

        var meta = feature.Capabilities.Count > 0
            ? string.Join(" · ", feature.Capabilities.Select(c => L.Get($"win.shell.capability{c}")))
            : L.Get(FeatureCatalog.EnergyLabelKey(feature.Energy));
        var texts = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                titleLine,
                new TextBlock { Text = L.Get(feature.DescriptionKey), Classes = { "caption" } },
                new TextBlock { Text = meta, Classes = { "caption", "tertiary" }, FontSize = 11 },
            },
        };

        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = _runtime.IsAvailable(feature), IsEnabled = _runtime.CanInstall(feature) };
        AutomationProperties.SetName(toggle, title.Text);
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!_updating && !_runtime.SetAvailable(feature.Id, toggle.IsChecked == true))
            {
                toggle.IsChecked = _runtime.IsAvailable(feature);
            }
        };
        _switches[feature.Id] = toggle;

        var open = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = Symbol.ChevronRight, FontSize = 14 } };
        ToolTip.SetTip(open, L.Get("Strings.menuSettings"));
        open.Click += (_, _) =>
        {
            if (_pages.ForFeature(feature.Id) is { } page)
            {
                _services.GetRequiredService<IAppShell>().OpenSettings(page.Id);
            }
        };
        open.IsVisible = _pages.ForFeature(feature.Id) is not null;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(8, 6) };
        grid.Children.Add(texts);
        grid.Children.Add(Grid(open, 1));
        grid.Children.Add(Grid(toggle, 2));
        var border = new Border { CornerRadius = new CornerRadius(6), Child = grid };
        _rows[feature.Id] = border;
        return border;
    }

    private Control BuildPresets()
    {
        var tiles = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var preset in FeaturePresets.All)
        {
            var apply = ActionButton(L.Get("hub.presetApplyButton"), async () =>
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                var name = L.Get(preset.NameKey);
                if (await ConfirmDialog.ShowAsync(owner, name, L.Format("hub.presetConfirmFormat", name), L.Get("hub.presetConfirmApply"), L.Get("hub.presetConfirmCancel")))
                {
                    _runtime.ReplaceAvailable(preset.Features, preset.EnableKeys);
                }
            });
            apply.HorizontalAlignment = HorizontalAlignment.Left;
            tiles.Children.Add(new Border
            {
                Classes = { "card" },
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new Border { Classes = { "iconTile" }, HorizontalAlignment = HorizontalAlignment.Left, Child = new SymbolIcon { Symbol = IconConverter.Parse(preset.Icon), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                        new TextBlock { Text = L.Get(preset.NameKey), FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = L.Get(preset.DescriptionKey), Classes = { "caption" }, MinHeight = 48 },
                        apply,
                    },
                },
            });
        }

        return CardText(L.Get("hub.presetsTitle"), tiles, Caption(L.Get("hub.presetsCaption")));
    }

    private Control? BuildNeverUsed()
    {
        var candidates = _runtime.NeverSwitchedOn();
        if (candidates.Count < 3)
        {
            return null;
        }

        var names = string.Join(", ", candidates.Select(f => L.Get(f.TitleKey)));
        var keep = ActionButton(L.Get("hub.neverUsedKeep"), () => _runtime.KeepFeatures(candidates.Select(f => f.Id)));
        var remove = ActionButton(L.Get("hub.neverUsedUninstall"), () =>
        {
            foreach (var feature in _runtime.NeverSwitchedOn())
            {
                _runtime.SetAvailable(feature.Id, false);
            }
        }, accent: true);
        return CardText(L.Get("hub.neverUsedTitle"),
            Note(L.Format("hub.neverUsedMessageFormat", names)),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { keep, remove } });
    }

    /// <summary>Scrolls a feature row into view and highlights it briefly.</summary>
    private void Reveal(string featureId)
    {
        if (!_rows.TryGetValue(featureId, out var row) || row is not Border border)
        {
            return;
        }

        border.BringIntoView();
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("AccentFaintBrush").ToBinding());
        DispatcherTimer.RunOnce(() => border.Background = Brushes.Transparent, TimeSpan.FromSeconds(1.6));
    }
}
