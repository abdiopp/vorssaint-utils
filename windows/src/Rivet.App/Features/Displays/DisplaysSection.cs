// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.App.Features.SystemMonitor.Panel;
using Rivet.Core.Displays;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Displays;

/// <summary>
/// The panel's "Displays" section (spec 03 §3.19.7): one row per display,
/// "Show brightness when adjusting", and Options with pointer following and
/// the display brightness shortcuts. Display on/off is not offered on Windows.
/// </summary>
internal sealed class DisplaysSection : UserControl
{
    private readonly BrightnessService _service;
    private readonly ISettingsStore _settings;
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly TextBlock _empty = MonitorUi.Note(null);

    public DisplaysSection(IServiceProvider services)
    {
        _service = services.GetRequiredService<BrightnessService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        var names = services.GetService<IKeyNameProvider>();

        var osd = Toggle(DisplaySettings.OsdEnabled, "BrightnessHigh", "brightness.osdToggle", "brightness.osdCaption");
        var options = new Fold(L.Get("win.displays.options"), () =>
        {
            var chords = string.Join("  ·  ", new[] { DisplaysModule.DecreaseRoleId, DisplaysModule.IncreaseRoleId }
                .Select(shortcuts.Find)
                .Where(r => r is not null)
                .Select(r => shortcuts.GetChord(r!).ToDisplayString(names)));
            return new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 0),
                Children =
                {
                    Toggle(DisplaySettings.FollowPointer, "CursorClick", "win.displays.followPointer", "win.displays.followPointerCaption"),
                    Toggle(DisplaySettings.ShortcutsEnabled, "Keyboard", "brightness.displayBrightnessShortcuts", "brightness.displayBrightnessShortcutCaption", chords),
                },
            };
        }, initiallyOpen: false, fontSize: 12, indent: 4);

        var card = new StackPanel
        {
            Spacing = 10,
            Children = { _rows, _empty, new Border { Classes = { "separator" } }, osd, options },
        };
        var title = new TextBlock { Text = L.Get("brightness.pageTitle").ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, Margin = new Thickness(4, 0) };
        Content = new StackPanel { Spacing = 8, Children = { title, new Border { Classes = { "card" }, Child = card } } };

        AttachedToVisualTree += (_, _) =>
        {
            _service.Changed += OnChanged;

            _service.Refresh();
            Render();
        };
        DetachedFromVisualTree += (_, _) => _service.Changed -= OnChanged;
        Render();
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        var displays = _service.Displays;
        SyncRows(_rows, displays, (status, index) => new DisplayRow(_service, _settings, status, index, wide: false));
        _empty.Text = !_service.IsReady && _service.IsRunning ? L.Get("win.displays.scanning") : L.Get("brightness.noDisplays");
        _empty.IsVisible = displays.Count == 0;
    }

    /// <summary>Keeps one <see cref="DisplayRow"/> per display, updating existing rows in place.</summary>
    internal static void SyncRows(Panel rows, IReadOnlyList<DisplayStatus> displays, Func<DisplayStatus, int, DisplayRow> create)
    {
        var existing = rows.Children.OfType<DisplayRow>().ToList();
        var sameSet = existing.Count == displays.Count && existing.Select(r => r.Id).SequenceEqual(displays.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        if (!sameSet)
        {
            rows.Children.Clear();
            for (var i = 0; i < displays.Count; i++)
            {
                if (i > 0)
                {
                    rows.Children.Add(new Border { Classes = { "separator" } });
                }

                rows.Children.Add(create(displays[i], i));
            }

            return;
        }

        for (var i = 0; i < displays.Count; i++)
        {
            existing[i].Update(displays[i], i);
        }
    }

    private Control Toggle(Setting<bool> setting, string icon, string titleKey, string captionKey, string? trailingHint = null)
    {
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = _settings.Get(setting) };
        toggle.IsCheckedChanged += (_, _) => _settings.Set(setting, toggle.IsChecked == true);
        // Observed only while on screen: the Options disclosure rebuilds its toggles on every open.
        IDisposable? subscription = null;
        toggle.AttachedToVisualTree += (_, _) =>
        {
            toggle.IsChecked = _settings.Get(setting);
            subscription ??= _settings.Observe(() => Dispatcher.UIThread.Post(() => toggle.IsChecked = _settings.Get(setting)), setting);
        };
        toggle.DetachedFromVisualTree += (_, _) =>
        {
            subscription?.Dispose();
            subscription = null;
        };
        AutomationProperties.SetName(toggle, L.Get(titleKey));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = L.Get(titleKey), FontSize = 12, TextWrapping = TextWrapping.Wrap } } };
        if (!string.IsNullOrEmpty(trailingHint))
        {
            text.Children.Add(MonitorUi.Caption(trailingHint));
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);
        ToolTip.SetTip(grid, L.Get(captionKey));
        return grid;
    }
}
