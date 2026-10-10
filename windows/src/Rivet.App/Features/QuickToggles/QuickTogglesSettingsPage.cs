// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Toggles;

namespace Rivet.App.Features.Toggles;

/// <summary>Settings → Quick toggles: the dark mode button, the rows (order and visibility) and the drives "Eject all disks" never touches.</summary>
public sealed class QuickTogglesSettingsPage : SettingsPage
{
    private readonly QuickToggleCatalog _catalog;
    private readonly StackPanel _excluded = new() { Spacing = 4 };
    private readonly Button _darkMode = new();

    public QuickTogglesSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _catalog = services.GetRequiredService<QuickToggleCatalog>();
        _darkMode.Click += async (_, _) =>
        {
            await _catalog.Service.ToggleDarkModeAsync().ConfigureAwait(true);
            UpdateDarkMode();
        };
        UpdateDarkMode();
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RebuildExcluded), QuickToggleSettings.ExcludedVolumes));

        var add = new Button { Content = L.Get("diskExclusions.addButton") };
        add.Click += async (_, _) => await ShowAddMenuAsync(add).ConfigureAwait(true);
        Content = Stack(
            Header("quickToggles.pageTitle", "quickToggles.panelCaption"),
            Card(null,
                Row("DarkTheme", L.Get("quickToggles.darkModeCaption"), null, _darkMode)),
            Card("quickToggles.pageTitle",
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Caption(L.Get("win.quickToggles.settingsRowsCaption")),
                        new QuickTogglesView(services, editable: true, editOnly: true),
                    },
                }),
            Card("diskExclusions.listTitle",
                new StackPanel
                {
                    Spacing = 8,
                    Children = { Caption(L.Get("diskExclusions.caption")), _excluded, add },
                }));
        RebuildExcluded();
    }

    private void UpdateDarkMode() => _darkMode.Content = _catalog.Title(QuickToggleId.DarkMode);

    private void RebuildExcluded()
    {
        _excluded.Children.Clear();
        foreach (var volume in Settings.Get(QuickToggleSettings.ExcludedVolumes))
        {
            var name = volume;
            var remove = new Button { Content = L.Get("diskExclusions.removeButton") };
            remove.Click += (_, _) => Settings.Set(QuickToggleSettings.ExcludedVolumes,
                Settings.Get(QuickToggleSettings.ExcludedVolumes).Where(v => !string.Equals(v, name, StringComparison.OrdinalIgnoreCase)).ToList());
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _excluded.Children.Add(row);
        }
    }

    private void Exclude(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var list = Settings.Get(QuickToggleSettings.ExcludedVolumes).ToList();
        if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(trimmed);
            Settings.Set(QuickToggleSettings.ExcludedVolumes, list);
        }
    }

    /// <summary>"Add drive…": the drives connected now (by label, else root), plus "Other drive name…".</summary>
    private async Task ShowAddMenuAsync(Control anchor)
    {
        IReadOnlyList<EjectableVolume> volumes;
        try
        {
            volumes = await Task.Run(() => _catalog.Service.Platform.EjectableVolumes([])).ConfigureAwait(true);
        }
        catch (Exception)
        {
            volumes = [];
        }

        var menu = new MenuFlyout();
        foreach (var volume in volumes)
        {
            var name = volume.Label.Length > 0 ? volume.Label : volume.Root;
            var item = new MenuItem { Header = volume.Label.Length > 0 ? $"{volume.Label} ({volume.Root.TrimEnd('\\')})" : volume.Root };
            item.Click += (_, _) => Exclude(name);
            menu.Items.Add(item);
        }

        var other = new MenuItem { Header = L.Get("diskExclusions.otherDrive") };
        other.Click += (_, _) => AskForName(anchor);
        menu.Items.Add(other);
        menu.ShowAt(anchor);
    }

    private void AskForName(Control anchor)
    {
        var box = new TextBox { PlaceholderText = L.Get("diskExclusions.customPlaceholder"), Width = 220 };
        AutomationProperties.SetName(box, L.Get("diskExclusions.customPlaceholder"));
        var add = new Button { Content = L.Get("diskExclusions.addButton").TrimEnd('…', '.'), IsDefault = true };
        var flyout = new Flyout { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4), Children = { box, add } } };
        add.Click += (_, _) =>
        {
            Exclude(box.Text ?? string.Empty);
            flyout.Hide();
        };
        flyout.ShowAt(anchor);
        box.Focus();
    }
}
