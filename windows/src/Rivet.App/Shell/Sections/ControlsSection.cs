// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Shell.Sections;

/// <summary>The Controls tab: switches for the input and file features, grouped by category with an on/total counter.</summary>
public sealed class ControlsSection : UserControl
{
    private readonly List<IDisposable> _bindings = [];

    public ControlsSection(IServiceProvider services)
    {
        var registry = services.GetRequiredService<PanelRegistry>();
        var runtime = services.GetRequiredService<FeatureRuntime>();
        var settings = services.GetRequiredService<ISettingsStore>();
        var hidden = PanelLayout.ParseHidden(settings.Get(Core.App.ShellSettings.PanelHiddenItems));
        var toggles = registry.Toggles
            .Where(t => runtime.IsAvailable(t.FeatureId) && !hidden.Contains("toggle:" + t.Id))
            .OrderBy(t => t.Order)
            .ToList();

        var root = new StackPanel { Spacing = 10 };
        foreach (var group in toggles.GroupBy(t => t.Category).OrderBy(g => g.Key))
        {
            var titleKey = group.Key switch
            {
                PanelToggleCategory.Input => "hub.groupMouseKeyboard",
                PanelToggleCategory.Files => "hub.groupClipboardFiles",
                _ => "hub.groupTools",
            };
            var count = new TextBlock { Classes = { "sectionTitle" }, HorizontalAlignment = HorizontalAlignment.Right };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 0) };
            header.Children.Add(new TextBlock { Text = L.Get(titleKey).ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" } });
            Grid.SetColumn(count, 1);
            header.Children.Add(count);

            var list = new StackPanel();
            var items = group.ToList();
            void UpdateCount() => count.Text = $"{items.Count(t => settings.Get(t.Setting))}/{items.Count}";
            foreach (var toggle in items)
            {
                list.Children.Add(BuildRow(settings, toggle, UpdateCount));
            }

            UpdateCount();
            root.Children.Add(new StackPanel { Spacing = 6, Children = { header, new Border { Classes = { "card" }, Padding = new Thickness(8, 4), Child = list } } });
        }

        if (toggles.Count == 0)
        {
            root.Children.Add(new TextBlock { Text = L.Get("win.shell.noResults"), Classes = { "caption" } });
        }

        Content = root;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var binding in _bindings)
        {
            binding.Dispose();
        }

        _bindings.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private Control BuildRow(ISettingsStore settings, PanelToggleDescriptor toggle, Action changed)
    {
        var property = settings.Bind(toggle.Setting);
        _bindings.Add(property);
        var icon = new SymbolIcon { Symbol = IconConverter.Parse(toggle.Icon), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = L.Get(toggle.TitleKey), Classes = { "rowTitle" }, VerticalAlignment = VerticalAlignment.Center };
        var toggleSwitch = new ToggleSwitch { Classes = { "compact" }, IsChecked = property.Value };
        toggleSwitch.IsCheckedChanged += (_, _) =>
        {
            property.Value = toggleSwitch.IsChecked == true;
            changed();
        };
        property.PropertyChanged += (_, _) =>
        {
            toggleSwitch.IsChecked = property.Value;
            changed();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(4, 4) };
        grid.Children.Add(icon);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        Grid.SetColumn(toggleSwitch, 2);
        grid.Children.Add(toggleSwitch);
        if (toggle.CaptionKey is { } caption)
        {
            ToolTip.SetTip(grid, L.Get(caption));
        }

        AutomationProperties.SetName(toggleSwitch, title.Text);
        return grid;
    }
}
