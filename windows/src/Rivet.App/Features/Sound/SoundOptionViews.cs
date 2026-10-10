// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Sound;

namespace Rivet.App.Features.Sound;

/// <summary>Two-way switch bindings that release their settings subscriptions with the view.</summary>
internal sealed class SettingBindings : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly List<IDisposable> _subscriptions = [];

    public SettingBindings(ISettingsStore settings)
    {
        _settings = settings;
    }

    public ToggleSwitch Switch(Setting<bool> setting, Action<bool>? changed = null)
    {
        var property = _settings.Bind(setting);
        _subscriptions.Add(property);
        var toggle = new ToggleSwitch { IsChecked = property.Value };
        toggle.IsCheckedChanged += (_, _) =>
        {
            var value = toggle.IsChecked == true;
            if (property.Value != value)
            {
                property.Value = value;
                changed?.Invoke(value);
            }
        };
        property.PropertyChanged += (_, _) => toggle.IsChecked = property.Value;
        return toggle;
    }

    public void Watch(Action changed, params SettingDefinition[] settings) =>
        _subscriptions.Add(_settings.Observe(() => Dispatcher.UIThread.Post(changed), settings));

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}

/// <summary>
/// The mixer's "Options" (spec §3.9.7): hide inactive apps, the headphone
/// guard, finer volume steps, (panel only) the output switcher and audio
/// priority, and the "Apps in the list" chooser.
/// </summary>
internal sealed class MixerOptionsView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly MixerService _mixer;
    private readonly FeatureRuntime _runtime;
    private readonly bool _inPanel;
    private readonly SettingBindings _bindings;
    private readonly StackPanel _appsList = new() { Spacing = 0 };
    private readonly TextBlock _appsSummary = new() { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };

    public MixerOptionsView(IServiceProvider services, bool inPanel)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _mixer = services.GetRequiredService<MixerService>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _inPanel = inPanel;
        _bindings = new SettingBindings(_settings);
        Build();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _mixer.Changed += OnMixerChanged;
        RefreshApps();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _mixer.Changed -= OnMixerChanged;
        _bindings.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private void Build()
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(SoundUi.SwitchRow(L.Get("mixer.hideInactiveApps"), null, _bindings.Switch(SoundSettings.HideInactiveApps)));

        var levelRow = HeadphoneLevelRow();
        var guard = _bindings.Switch(SoundSettings.LowerVolumeOnHeadphonesDisconnect, on => levelRow.IsVisible = on);
        levelRow.IsVisible = guard.IsChecked == true;
        stack.Children.Add(SoundUi.SwitchRow(L.Get("Strings.mixerLowerOnHeadphonesDisconnect"), L.Get("Strings.mixerLowerOnHeadphonesDisconnectCaption"), guard));
        stack.Children.Add(levelRow);
        if (!_inPanel)
        {
            var note = SoundUi.Caption(L.Get("win.sound.headphoneGuardJackNote"), "TextTertiaryBrush");
            note.Margin = new Thickness(0, -2, 48, 2);
            stack.Children.Add(note);
        }

        var stepRow = _inPanel ? null : FineStepRow();
        var fine = _bindings.Switch(SoundSettings.PreciseVolumeRollerEnabled, on =>
        {
            if (stepRow is not null)
            {
                stepRow.IsVisible = on;
            }
        });
        stack.Children.Add(SoundUi.SwitchRow(L.Get("Strings.preciseVolumeRollerEnable"), L.Get("Strings.preciseVolumeRollerCaption"), fine));
        if (stepRow is not null)
        {
            stepRow.IsVisible = fine.IsChecked == true;
            stack.Children.Add(stepRow);
        }

        if (_inPanel && _runtime.IsAvailable(FeatureIds.SoundOutputSwitcher))
        {
            stack.Children.Add(new Border { Classes = { "separator" }, Margin = new Thickness(0, 2) });
            stack.Children.Add(new OutputCycleView(_services, showShortcut: false));
        }

        if (_inPanel && _runtime.IsAvailable(FeatureIds.AudioPriority))
        {
            stack.Children.Add(new Disclosure(L.Get("Strings.audioPrioritySection"), new PriorityListsView(_services, compact: true)));
        }

        var appsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        appsHeader.Children.Add(new TextBlock { Text = L.Get("Strings.mixerVisibleApps"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_appsSummary, 1);
        appsHeader.Children.Add(_appsSummary);
        stack.Children.Add(new Disclosure(appsHeader, _appsList));

        Content = stack;
        RefreshApps();
    }

    /// <summary>"Volume after disconnect", 10–100 % in steps of 5.</summary>
    private Grid HeadphoneLevelRow()
    {
        var values = Enumerable.Range(2, 19).Select(i => i * 5).ToList();
        var box = new ComboBox
        {
            ItemsSource = values.Select(v => VolumeMath.FormatPercent(v / 100.0, Localizer.Current.Culture)).ToList(),
            MinWidth = 86,
            FontSize = 12,
        };
        void Select() => box.SelectedIndex = Math.Max(0, values.IndexOf((int)Math.Round(_settings.Get(SoundSettings.HeadphonesDisconnectVolumePercent) / 5.0) * 5));
        Select();
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                _settings.Set(SoundSettings.HeadphonesDisconnectVolumePercent, values[box.SelectedIndex]);
            }
        };
        _bindings.Watch(Select, SoundSettings.HeadphonesDisconnectVolumePercent);
        AutomationProperties.SetName(box, L.Get("Strings.mixerHeadphonesDisconnectVolume"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(16, 0, 0, 2) };
        grid.Children.Add(new TextBlock { Text = L.Get("Strings.mixerHeadphonesDisconnectVolume"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private Grid FineStepRow()
    {
        var steps = new[] { 0.5, 1.0 };
        var box = new ComboBox
        {
            ItemsSource = steps.Select(s => (s / 100).ToString("#0.#%", Localizer.Current.Culture)).ToList(),
            MinWidth = 86,
            FontSize = 12,
            SelectedIndex = Math.Max(0, Array.IndexOf(steps, _settings.Get(SoundSettings.PreciseVolumeRollerStepPercent))),
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                _settings.Set(SoundSettings.PreciseVolumeRollerStepPercent, steps[box.SelectedIndex]);
            }
        };
        AutomationProperties.SetName(box, L.Get("win.sound.fineStepTitle"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(16, 0, 0, 2) };
        grid.Children.Add(new TextBlock { Text = L.Get("win.sound.fineStepTitle"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private void OnMixerChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RefreshApps);

    private void RefreshApps()
    {
        var hidden = _mixer.HiddenCount;
        _appsSummary.Text = hidden == 0 ? L.Get("Strings.mixerAllShown") : $"{L.Get("Strings.mixerHiddenCountLabel")}: {hidden}";
        _appsList.Children.Clear();
        foreach (var entry in _mixer.ListEntries)
        {
            var box = new CheckBox { Content = entry.DisplayName, IsChecked = entry.IsShown, IsEnabled = entry.CanToggle, FontSize = 12.5, Margin = new Thickness(0, -2) };
            var id = entry.Id;
            box.IsCheckedChanged += (_, _) => _mixer.SetShown(id, box.IsChecked == true);
            _appsList.Children.Add(box);
        }
    }
}

/// <summary>"Switch outputs with shortcut" and "Outputs in cycle" (spec §3.11).</summary>
internal sealed class OutputCycleView : UserControl
{
    private readonly OutputSwitcherService _switcher;
    private readonly AudioDeviceService _devices;
    private readonly SettingBindings _bindings;
    private readonly StackPanel _list = new() { Spacing = 0 };
    private readonly TextBlock _empty = SoundUi.Caption(L.Get("Strings.mixerSystemOutputNoDevices"));

    public OutputCycleView(IServiceProvider services, bool showShortcut, bool showToggle = true)
    {
        _switcher = services.GetRequiredService<OutputSwitcherService>();
        _devices = services.GetRequiredService<AudioDeviceService>();
        _bindings = new SettingBindings(services.GetRequiredService<ISettingsStore>());
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        var names = services.GetService<IKeyNameProvider>();

        var caption = L.Get("Strings.soundOutputSwitcherCaption");
        if (!showShortcut && shortcuts.Find(SoundModule.OutputSwitcherRoleId) is { } role)
        {
            caption += " " + shortcuts.GetChord(role).ToDisplayString(names);
        }

        var stack = new StackPanel { Spacing = 4 };
        if (showToggle)
        {
            stack.Children.Add(SoundUi.SwitchRow(L.Get("Strings.soundOutputSwitcherEnable"), caption, _bindings.Switch(FeatureKeys.SoundOutputSwitcherEnabled)));
            stack.Children.Add(new TextBlock { Text = L.Get("Strings.soundOutputSwitcherDevices"), FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        }

        stack.Children.Add(_list);
        stack.Children.Add(_empty);
        Content = stack;
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _devices.Changed += OnChanged;
        _switcher.Changed += OnChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _devices.Changed -= OnChanged;
        _switcher.Changed -= OnChanged;
        _bindings.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        _list.Children.Clear();
        foreach (var device in _devices.Outputs)
        {
            var id = device.Id;
            var box = new CheckBox { Content = device.Name, IsChecked = _switcher.IsSelected(id), FontSize = 12.5, Margin = new Thickness(0, -2) };
            box.IsCheckedChanged += (_, _) => _switcher.SetSelected(id, box.IsChecked == true);
            _list.Children.Add(box);
        }

        _empty.IsVisible = _devices.Outputs.Count == 0;
    }
}

/// <summary>Both priority lists, outputs then microphones, with the caption (spec §3.14).</summary>
internal sealed class PriorityListsView : UserControl
{
    public PriorityListsView(IServiceProvider services, bool compact)
    {
        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SoundUi.Caption(L.Get("Strings.audioPriorityCaption")),
                new PriorityListView(services, AudioFlow.Render, compact),
                new PriorityListView(services, AudioFlow.Capture, compact),
            },
        };
    }
}

/// <summary>
/// One priority list: the "automatically switch" switch and the ranked
/// devices (rank, name or last-known name, "Current" / "Unavailable"),
/// reordered with the handle (drag) or Move up/down.
/// </summary>
internal sealed class PriorityListView : UserControl
{
    private readonly AudioPriorityService _priority;
    private readonly AudioDeviceService _devices;
    private readonly AudioFlow _flow;
    private readonly bool _compact;
    private readonly SettingBindings _bindings;
    private readonly StackPanel _rows = new() { Spacing = 3 };
    private readonly TextBlock _empty;
    private int? _dragFrom;
    private int? _dragTo;

    /// <param name="compact">The narrow panel: no move buttons (drag the handle, or Move up/down in the context menu).</param>
    public PriorityListView(IServiceProvider services, AudioFlow flow, bool compact = false)
    {
        _priority = services.GetRequiredService<AudioPriorityService>();
        _devices = services.GetRequiredService<AudioDeviceService>();
        _flow = flow;
        _compact = compact;
        _bindings = new SettingBindings(services.GetRequiredService<ISettingsStore>());
        var isOutput = flow == AudioFlow.Render;
        _empty = SoundUi.Caption(L.Get(isOutput ? "Strings.mixerSystemOutputNoDevices" : "Strings.mixerInputNoDevices"));
        var toggle = _bindings.Switch(isOutput ? FeatureKeys.AudioPriorityOutputEnabled : FeatureKeys.AudioPriorityInputEnabled);
        Content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = L.Get(isOutput ? "Strings.audioPriorityOutputList" : "Strings.audioPriorityInputList"), FontWeight = FontWeight.SemiBold, FontSize = 12.5 },
                SoundUi.SwitchRow(L.Get(isOutput ? "Strings.audioPriorityOutputEnable" : "Strings.audioPriorityInputEnable"), null, toggle),
                _rows,
                _empty,
            },
        };

        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => EndDrag(commit: false);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _priority.Changed += OnChanged;
        _devices.Changed += OnChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _priority.Changed -= OnChanged;
        _devices.Changed -= OnChanged;
        _bindings.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_dragFrom is null)
        {
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    private void Refresh()
    {
        var entries = _priority.Entries(_flow);
        _rows.Children.Clear();
        for (var i = 0; i < entries.Count; i++)
        {
            _rows.Children.Add(BuildRow(entries[i], i, entries.Count));
        }

        _empty.IsVisible = entries.Count == 0;
    }

    private Border BuildRow(PriorityEntry entry, int index, int count)
    {
        var handle = SoundUi.Icon("ReOrderDotsVertical", 14, "TextTertiaryBrush");
        var handleHost = new Border { Background = Brushes.Transparent, Padding = new Thickness(2, 4), Child = handle, Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
        ToolTip.SetTip(handleHost, L.Get("win.sound.dragToReorder"));
        handleHost.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragFrom = index;
                _dragTo = index;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
        };

        var rank = new TextBlock { Text = entry.Rank.ToString(Localizer.Current.Culture), Width = 16, FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Classes = { "tertiary" } };
        var name = new TextBlock { Text = entry.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = entry.IsAvailable ? 1 : 0.6 };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (entry.IsCurrent)
        {
            badges.Children.Add(new Border { Classes = { "pill", "accent" }, Child = new TextBlock { Text = L.Get("Strings.audioPriorityCurrent"), Foreground = Brushes.White } });
        }

        if (!entry.IsAvailable)
        {
            var unavailable = new TextBlock { Text = L.Get("Strings.audioPriorityUnavailable"), FontSize = 11 };
            unavailable.Bind(TextBlock.ForegroundProperty, unavailable.GetResourceObservable("WarningBrush").ToBinding());
            badges.Children.Add(unavailable);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"), ColumnSpacing = 6, MinHeight = 28 };
        grid.Children.Add(handleHost);
        Place(grid, rank, 1);
        Place(grid, name, 2);
        Place(grid, badges, 3);
        if (!_compact)
        {
            var up = SoundUi.IconButton("ChevronUp", L.Get("Strings.audioPriorityMoveUp"), () => _priority.Move(_flow, index, index - 1), 12);
            up.IsEnabled = index > 0;
            var down = SoundUi.IconButton("ChevronDown", L.Get("Strings.audioPriorityMoveDown"), () => _priority.Move(_flow, index, index + 1), 12);
            down.IsEnabled = index < count - 1;
            Place(grid, up, 4);
            Place(grid, down, 5);
        }

        var moveUp = new MenuItem { Header = L.Get("Strings.audioPriorityMoveUp"), Icon = SoundUi.Icon("ArrowUp", 14), IsEnabled = index > 0 };
        moveUp.Click += (_, _) => _priority.Move(_flow, index, index - 1);
        var moveDown = new MenuItem { Header = L.Get("Strings.audioPriorityMoveDown"), Icon = SoundUi.Icon("ArrowDown", 14), IsEnabled = index < count - 1 };
        moveDown.Click += (_, _) => _priority.Move(_flow, index, index + 1);

        var border = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 1, 2, 1),
        };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable(entry.IsCurrent ? "AccentBrush" : "PanelCardBorderBrush").ToBinding());
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable(entry.IsCurrent ? "AccentFaintBrush" : "PanelControlBrush").ToBinding());
        border.ContextFlyout = new MenuFlyout { ItemsSource = new object[] { moveUp, moveDown } };
        AutomationProperties.SetName(border, $"{entry.Rank}. {entry.Name}");
        ToolTip.SetTip(name, entry.Name);
        return border;
    }

    private static void Place(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom is not { } from)
        {
            return;
        }

        var y = e.GetPosition(_rows).Y;
        var rows = _rows.Children.ToList();
        var target = rows.FindIndex(r => r.Bounds.Top <= y && y < r.Bounds.Bottom);
        if (target < 0)
        {
            target = y < 0 ? 0 : rows.Count - 1;
        }

        _dragTo = target;
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Opacity = i == from ? 0.45 : 1;
            rows[i].RenderTransform = i == target && i != from ? new TranslateTransform(0, target > from ? -3 : 3) : null;
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragFrom is null)
        {
            return;
        }

        e.Handled = true;
        EndDrag(commit: true);
        e.Pointer.Capture(null);
    }

    private void EndDrag(bool commit)
    {
        if (_dragFrom is not { } from)
        {
            return;
        }

        var to = _dragTo ?? from;
        _dragFrom = null;
        _dragTo = null;
        if (commit && to != from)
        {
            _priority.Move(_flow, from, to);
        }

        Refresh();
    }
}
