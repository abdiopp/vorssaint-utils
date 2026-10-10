// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.RadialMenu;

/// <summary>
/// Settings › Tools › Radial menu (spec 07 §3.2.13): the master switch, opening
/// behaviour and placement, "Try it"; the profiles (picker, add from a preset,
/// duplicate, delete, name, colour, shortcut, side button with a live button
/// test); and the actions (visual canvas, list, add, reset).
/// </summary>
public sealed class RadialMenuSettingsPage : SettingsPage
{
    private readonly IServiceProvider _services;
    private readonly RadialMenuService _service;
    private readonly ShortcutManager _shortcuts;
    private readonly StackPanel _profileCard = new() { Spacing = 0 };
    private readonly StackPanel _actionsCard = new() { Spacing = 10 };
    private readonly TextBlock _failed;
    private readonly RadialCanvasEditor _canvas;
    private readonly TextBlock _buttonTest = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private Guid _selected;
    private Guid? _submenu;
    private bool _showList;
    private bool _editingName;

    public RadialMenuSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _services = services;
        _service = services.GetRequiredService<RadialMenuService>();
        _shortcuts = services.GetRequiredService<ShortcutManager>();
        _selected = _service.Profiles.FirstOrDefault()?.Id ?? Guid.Empty;
        _canvas = new RadialCanvasEditor(_service.Presenter);
        _canvas.EditRequested += (_, index) => _ = EditAsync(index);
        _canvas.OpenSubmenuRequested += (_, index) => OpenSubmenu(index);
        _canvas.RemoveRequested += (_, index) => RemoveAt(index);
        _canvas.SwapRequested += (_, swap) => Swap(swap.From, swap.To);
        _canvas.HubClicked += (_, _) =>
        {
            if (_submenu is not null)
            {
                _submenu = null;
                RebuildActions();
            }
            else if (CurrentItems().Count == 0)
            {
                _ = AddAsync();
            }
        };

        var modes = new (string Value, string Label)[]
        {
            ("pressOrHold", L.Get("radialMenu.activationModePressOrHold")),
            ("press", L.Get("radialMenu.activationModePress")),
            ("hold", L.Get("radialMenu.activationModeHold")),
        };
        var positions = new (bool Value, string Label)[]
        {
            (true, L.Get("radialMenu.positionPointer")),
            (false, L.Get("radialMenu.positionCenter")),
        };
        _failed = new TextBlock { Text = L.Get("win.radialMenu.registrationFailed"), TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
        _failed.Bind(TextBlock.ForegroundProperty, _failed.GetResourceObservable("WarningBrush"));

        Content = Stack(
            Header("radialMenu.pageTitle", "radialMenu.hubDescription"),
            Card(null,
                Toggle(RadialMenuSettings.Enabled, "CircleMultipleSubtractCheckmark", "radialMenu.enableLabel", "radialMenu.enableCaption"),
                Choice(RadialMenuSettings.ActivationMode, "CursorClick", "radialMenu.activationModeLabel", "radialMenu.activationModeCaption", modes),
                Choice(RadialMenuSettings.AtPointer, "Cursor", "radialMenu.positionLabel", null, positions),
                Row("Play", L.Get("radialMenu.tryButton"), L.Get("win.radialMenu.tryCaption"), ActionButton(L.Get("radialMenu.tryButton"), TryIt, "Play", accent: true)),
                _failed),
            CardText(L.Get("radialMenu.profilesHeader"), _profileCard),
            CardText(L.Get("radialMenu.actionsHeader"), _actionsCard));

        _service.ProfilesChanged += OnProfilesChanged;
        _shortcuts.StatesChanged += OnShortcutStates;
        _service.ButtonTested += OnButtonTested;
        RebuildProfile();
        RebuildActions();
        UpdateFailed();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _service.ProfilesChanged -= OnProfilesChanged;
        _shortcuts.StatesChanged -= OnShortcutStates;
        _service.ButtonTested -= OnButtonTested;
        _service.SetButtonTest(false);
        base.OnDetachedFromVisualTree(e);
    }

    private RadialProfile? Profile => _service.Profiles.FirstOrDefault(p => p.Id == _selected) ?? _service.Profiles.FirstOrDefault();

    private int SlotOf(RadialProfile profile) => _service.Profiles.ToList().FindIndex(p => p.Id == profile.Id);

    private void OnProfilesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_service.Profiles.All(p => p.Id != _selected))
        {
            _selected = _service.Profiles.FirstOrDefault()?.Id ?? Guid.Empty;
            _submenu = null;
        }

        if (!_editingName)
        {
            RebuildProfile();
        }

        RebuildActions();
    });

    private void OnShortcutStates(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateFailed);

    private void UpdateFailed() =>
        _failed.IsVisible = Enumerable.Range(0, RadialMenuService.ShortcutSlots)
            .Any(i => _shortcuts.Find(RadialMenuService.SlotRole(i).Id) is { } role && _shortcuts.GetState(role) == ShortcutState.RegistrationFailed);

    private void TryIt()
    {
        if (Profile is { } profile)
        {
            _service.TryIt(profile);
        }
    }

    private void Save(RadialProfile profile) => _service.UpdateProfile(profile);

    // ── Profiles card ───────────────────────────────────────────────────

    private void RebuildProfile()
    {
        _profileCard.Children.Clear();
        if (Profile is not { } profile)
        {
            return;
        }

        _selected = profile.Id;
        var profiles = _service.Profiles;
        var general = L.Get("radialMenu.presetGeneral");

        // Picker, add, duplicate, delete.
        var picker = new ComboBox { MinWidth = 200, ItemsSource = profiles.Select(p => p.Name.Length == 0 ? general : p.Name).ToList(), SelectedIndex = SlotOf(profile) };
        AutomationProperties.SetName(picker, L.Get("radialMenu.profilePickerLabel"));
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex >= 0 && picker.SelectedIndex < profiles.Count && profiles[picker.SelectedIndex].Id != _selected)
            {
                _selected = profiles[picker.SelectedIndex].Id;
                _submenu = null;
                RebuildProfile();
                RebuildActions();
            }
        };
        var add = IconButton(Symbol.Add, L.Get("radialMenu.addProfileButton"));
        var presets = new MenuFlyout();
        foreach (var preset in RadialPresets.All)
        {
            var item = new MenuItem { Header = L.Get(RadialPresets.TitleKey(preset)) };
            item.Click += (_, _) =>
            {
                var created = RadialPresets.NewProfile(preset, _service.Profiles.Count, L.Get);
                _selected = created.Id;
                _submenu = null;
                _service.SaveProfiles([.. _service.Profiles, created]);
            };
            presets.Items.Add(item);
        }

        add.Flyout = presets;
        var duplicate = IconButton(Symbol.Copy, L.Get("radialMenu.duplicateProfileButton"));
        duplicate.Click += (_, _) =>
        {
            var copy = RadialPresets.Duplicate(profile, general);
            _selected = copy.Id;
            _service.SaveProfiles([.. _service.Profiles, copy]);
        };
        var delete = IconButton(Symbol.Delete, L.Get("radialMenu.deleteProfileButton"));
        delete.IsEnabled = profiles.Count > 1;
        delete.Click += async (_, _) =>
        {
            var name = profile.Name.Length == 0 ? general : profile.Name;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (await ConfirmDialog.ShowAsync(owner, L.Format("radialMenu.deleteProfileConfirmFormat", name), L.Get("win.radialMenu.deleteProfileMessage"), L.Get("radialMenu.deleteProfileButton"), L.Get("Strings.mediaCancel"), destructive: true).ConfigureAwait(true))
            {
                _service.SaveProfiles(_service.Profiles.Where(p => p.Id != profile.Id).ToList());
            }
        };
        _profileCard.Children.Add(Row("Album", L.Get("radialMenu.profilePickerLabel"), null,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { picker, add, duplicate, delete } }));

        // Name (saved on every keystroke without rebuilding the card).
        var name = new TextBox { Text = profile.Name, PlaceholderText = general, MaxLength = RadialProfile.MaxNameLength, Width = 220 };
        AutomationProperties.SetName(name, L.Get("radialMenu.profileNameLabel"));
        name.TextChanged += (_, _) =>
        {
            if (Profile is not { } current || (name.Text ?? string.Empty) == current.Name)
            {
                return;
            }

            _editingName = true;
            try
            {
                Save(current with { Name = name.Text ?? string.Empty });
            }
            finally
            {
                _editingName = false;
            }

            if (picker.ItemsSource is List<string> labels && SlotOf(current) is var index and >= 0 && index < labels.Count)
            {
                var updated = _service.Profiles.Select(p => p.Name.Length == 0 ? general : p.Name).ToList();
                picker.ItemsSource = updated;
                picker.SelectedIndex = index;
            }
        };
        _profileCard.Children.Add(Row("Rename", L.Get("radialMenu.profileNameLabel"), null, name));

        // Colour swatches.
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var swatches = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, MaxWidth = 330 };
        foreach (var color in Enum.GetValues<RadialColor>())
        {
            var selected = profile.Color == color;
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(selected ? 2 : 0),
                Background = new SolidColorBrush(_service.Presenter.ColorValue(color, dark).ToColor()),
            };
            if (selected)
            {
                swatch.Bind(BorderBrushProperty, swatch.GetResourceObservable("TextPrimaryBrush"));
            }

            ToolTip.SetTip(swatch, L.Get(RadialLabels.ColorTitleKey(color)));
            AutomationProperties.SetName(swatch, L.Get(RadialLabels.ColorTitleKey(color)));
            swatch.Click += (_, _) =>
            {
                if (Profile is { } current)
                {
                    Save(current with { Color = color });
                }
            };
            swatches.Children.Add(swatch);
        }

        _profileCard.Children.Add(Row("Color", L.Get("radialMenu.profileColorLabel"), null, swatches));

        // Shortcut (first six wheels).
        var slot = SlotOf(profile);
        if (slot < RadialMenuService.ShortcutSlots)
        {
            var recorder = new ShortcutRecorder { AllowClear = true };
            if (KeyChord.TryParse(profile.Shortcut, out var chord))
            {
                recorder.Chord = chord;
            }

            AutomationProperties.SetName(recorder, L.Get("radialMenu.profileShortcutLabel"));
            var message = new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            message.Bind(TextBlock.ForegroundProperty, message.GetResourceObservable("WarningBrush"));
            recorder.RecordingChanged += (_, recording) =>
            {
                if (recording)
                {
                    _shortcuts.Suspend();
                }
                else
                {
                    _shortcuts.Resume();
                }
            };
            recorder.ChordRecorded += (_, e) =>
            {
                var value = e.Chord;
                if (!value.IsEmpty)
                {
                    if (!value.IsValidGlobalShortcut)
                    {
                        Show(message, L.Get("win.radialMenu.shortcutInvalid"));
                        return;
                    }

                    if (_service.ShortcutOwner(value, profile.Id) is { } other)
                    {
                        Show(message, L.Format("win.radialMenu.shortcutUsedFormat", other.Name.Length == 0 ? general : other.Name));
                        return;
                    }

                    if (_shortcuts.FindConflict(value, RadialMenuService.SlotRole(slot), includeInactive: false) is { } role && !role.Id.StartsWith("radialMenu.", StringComparison.Ordinal))
                    {
                        Show(message, L.Format("win.radialMenu.shortcutUsedFormat", L.Get(role.TitleKey)));
                        return;
                    }
                }

                message.IsVisible = false;
                recorder.Chord = value;
                if (Profile is { } current)
                {
                    Save(current with { Shortcut = value.IsEmpty ? string.Empty : value.ToStorageString() });
                }
            };
            var shortcut = new StackPanel { Spacing = 4, Children = { recorder, message } };
            _profileCard.Children.Add(Row("Keyboard", L.Get("radialMenu.profileShortcutLabel"), L.Get("win.radialMenu.shortcutCaption"), shortcut));
        }
        else
        {
            _profileCard.Children.Add(Row("Keyboard", L.Get("radialMenu.profileShortcutLabel"), L.Format("win.radialMenu.noShortcutSlotFormat", RadialMenuService.ShortcutSlots)));
        }

        // Side mouse button.
        var buttons = new (RadialMouseTrigger Value, string Label)[]
        {
            (RadialMouseTrigger.Off, L.Get("radialMenu.mouseTriggerOff")),
            (new RadialMouseTrigger(RadialMouseTrigger.Back), L.Get("radialMenu.mouseTriggerBack")),
            (new RadialMouseTrigger(RadialMouseTrigger.Forward), L.Get("radialMenu.mouseTriggerForward")),
        };
        var mouse = new ComboBox { MinWidth = 200, ItemsSource = buttons.Select(b => b.Label).ToList() };
        mouse.SelectedIndex = Math.Max(0, Array.FindIndex(buttons, b => b.Value == profile.MouseButton));
        AutomationProperties.SetName(mouse, L.Get("radialMenu.mouseTriggerLabel"));
        mouse.SelectionChanged += (_, _) =>
        {
            if (mouse.SelectedIndex >= 0 && buttons[mouse.SelectedIndex].Value != profile.MouseButton)
            {
                _service.AssignMouseButton(profile.Id, buttons[mouse.SelectedIndex].Value);
            }
        };
        var mouseCaption = profile.MouseButton.IsOff ? L.Get("win.radialMenu.mouseRequirement") : L.Get("win.radialMenu.mouseWarning");
        _profileCard.Children.Add(Row("CursorClick", L.Get("radialMenu.mouseTriggerLabel"), mouseCaption, mouse));
        if (!profile.MouseButton.IsOff && !profile.MouseButton.IsSupportedOnWindows)
        {
            _profileCard.Children.Add(Row("Warning", L.Format("win.radialMenu.buttonUnsupportedFormat", profile.MouseButton.Button + 1), null));
        }

        // Button test.
        var test = new ToggleSwitch { Classes = { "compact" }, IsChecked = _service.ButtonTestActive };
        AutomationProperties.SetName(test, L.Get("radialMenu.buttonTestLabel"));
        test.IsCheckedChanged += (_, _) =>
        {
            _service.SetButtonTest(test.IsChecked == true);
            _buttonTest.Text = test.IsChecked == true ? L.Get("radialMenu.buttonTestWaiting") : L.Get("win.radialMenu.buttonTestHint");
        };
        _buttonTest.Text = _service.ButtonTestActive ? L.Get("radialMenu.buttonTestWaiting") : L.Get("win.radialMenu.buttonTestHint");
        Detach(_buttonTest);
        _profileCard.Children.Add(Row("Pulse", L.Get("radialMenu.buttonTestLabel"), null, test));
        _profileCard.Children.Add(new Border { Padding = new Thickness(16, 0, 16, 12), Child = _buttonTest });

        if (!profile.HasTrigger)
        {
            var note = Note(L.Get("win.radialMenu.noTrigger"));
            _profileCard.Children.Add(new Border { Padding = new Thickness(16, 0, 16, 12), Child = note });
        }

        // Separators between rows like the shared cards.
        for (var i = _profileCard.Children.Count - 1; i > 0; i--)
        {
            if (_profileCard.Children[i] is SettingsRow && _profileCard.Children[i - 1] is SettingsRow)
            {
                _profileCard.Children.Insert(i, new Border { Classes = { "separator" } });
            }
        }
    }

    private void OnButtonTested(object? sender, int xButton)
    {
        if (Profile is not { } profile)
        {
            return;
        }

        _buttonTest.Text = profile.MouseButton.XButton == xButton ? L.Get("win.radialMenu.buttonTestSeen") : L.Get("radialMenu.buttonTestOther");
    }

    private static void Show(TextBlock message, string text)
    {
        message.Text = text;
        message.IsVisible = true;
    }

    private static Button IconButton(Symbol symbol, string tip)
    {
        var button = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = symbol, FontSize = 15 }, Width = 32, Height = 32, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
    }

    // ── Actions card ────────────────────────────────────────────────────

    private IReadOnlyList<RadialItem> CurrentItems()
    {
        if (Profile is not { } profile)
        {
            return [];
        }

        return _submenu is { } id && profile.Items.FirstOrDefault(i => i.Id == id) is { IsSubmenu: true } sub ? sub.Children : profile.Items;
    }

    private void SetCurrentItems(IReadOnlyList<RadialItem> items)
    {
        if (Profile is not { } profile)
        {
            return;
        }

        if (_submenu is { } id)
        {
            Save(profile with { Items = profile.Items.Select(i => i.Id == id ? i with { Children = items } : i).ToList() });
        }
        else
        {
            Save(profile with { Items = items });
        }
    }

    private void RebuildActions()
    {
        _actionsCard.Children.Clear();
        if (Profile is not { } profile)
        {
            return;
        }

        if (_submenu is { } id && profile.Items.All(i => i.Id != id))
        {
            _submenu = null;
        }

        var items = CurrentItems();
        var submenu = _submenu is { } sid ? profile.Items.First(i => i.Id == sid) : null;
        Detach(_canvas);
        _canvas.SetLevel(items, _service.Presenter.ColorValue(profile.Color, dark: true), submenu is not null, submenu?.Name);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(16, 4, 16, 0) };
        header.Children.Add(new TextBlock { Text = L.Get("radialMenu.canvasHint"), Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        if (submenu is not null)
        {
            var back = new Button { Content = "‹ " + L.Get("radialMenu.backButton") };
            back.Click += (_, _) =>
            {
                _submenu = null;
                RebuildActions();
            };
            Grid.SetColumn(back, 1);
            header.Children.Add(back);
        }
        else if (RadialPresets.PresetFor(profile, L.Get) is { } preset)
        {
            var reset = new Button { Content = L.Get("radialMenu.resetActionsButton") };
            reset.Click += async (_, _) =>
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                if (await ConfirmDialog.ShowAsync(owner, L.Get("radialMenu.resetActionsConfirm"), L.Get("radialMenu.resetActionsConfirmMessage"), L.Get("radialMenu.resetActionsButton"), L.Get("Strings.mediaCancel")).ConfigureAwait(true)
                    && Profile is { } current)
                {
                    Save(current with { Items = RadialPresets.Items(preset) });
                }
            };
            Grid.SetColumn(reset, 1);
            header.Children.Add(reset);
        }

        var addButton = new Button { Content = L.Get("radialMenu.addButton"), IsEnabled = items.Count < RadialGeometry.MaxItems };
        addButton.Click += (_, _) => _ = AddAsync();
        Grid.SetColumn(addButton, 2);
        header.Children.Add(addButton);
        var list = new Button { Content = L.Get(_showList ? "radialMenu.hideListButton" : "radialMenu.showListButton") };
        list.Click += (_, _) =>
        {
            _showList = !_showList;
            RebuildActions();
        };
        Grid.SetColumn(list, 3);
        header.Children.Add(list);
        _actionsCard.Children.Add(header);
        _actionsCard.Children.Add(new Border { Padding = new Thickness(16, 0, 16, items.Count >= RadialGeometry.MaxItems || _showList ? 0 : 16), Child = _canvas });

        if (items.Count == 0)
        {
            _actionsCard.Children.Add(new TextBlock { Text = L.Get("radialMenu.emptyCaption"), Classes = { "caption" }, Margin = new Thickness(16, 0, 16, 12) });
        }

        if (items.Count >= RadialGeometry.MaxItems)
        {
            _actionsCard.Children.Add(new TextBlock { Text = L.Get("radialMenu.limitCaption"), Classes = { "caption" }, Margin = new Thickness(16, 0, 16, 12) });
        }

        if (_showList)
        {
            _actionsCard.Children.Add(BuildList(items));
        }
    }

    /// <summary>The list editor: rows move (not swap) with the arrows; Remove from the context menu or the button.</summary>
    private Control BuildList(IReadOnlyList<RadialItem> items)
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 16, 16) };
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var item = items[i];
            var icon = new SymbolIcon { Symbol = IconConverter.Parse(_service.Presenter.Symbol(item)), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
            var texts = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = _service.Presenter.Label(item, NowPlayingState.Loading), TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = RadialItemEditor.KindTitle(item.Kind), Classes = { "caption" } },
                },
            };
            var up = IconButton(Symbol.ArrowUp, L.Get("win.radialMenu.moveUp"));
            up.IsEnabled = index > 0;
            up.Click += (_, _) => Move(index, index - 1);
            var down = IconButton(Symbol.ArrowDown, L.Get("win.radialMenu.moveDown"));
            down.IsEnabled = index < items.Count - 1;
            down.Click += (_, _) => Move(index, index + 1);
            var edit = IconButton(Symbol.Edit, L.Get("Strings.menuEdit"));
            edit.Click += (_, _) => _ = EditAsync(index);
            var remove = IconButton(Symbol.Delete, L.Get("radialMenu.deleteButton"));
            remove.Click += (_, _) => RemoveAt(index);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 8, Background = Brushes.Transparent };
            row.Children.Add(icon);
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            Grid.SetColumn(up, 2);
            row.Children.Add(up);
            Grid.SetColumn(down, 3);
            row.Children.Add(down);
            Grid.SetColumn(edit, 4);
            row.Children.Add(edit);
            Grid.SetColumn(remove, 5);
            row.Children.Add(remove);
            if (item.IsSubmenu && _submenu is null)
            {
                row.DoubleTapped += (_, _) => OpenSubmenu(index);
            }

            stack.Children.Add(row);
        }

        return stack;
    }

    private void OpenSubmenu(int index)
    {
        var items = CurrentItems();
        if (_submenu is null && index < items.Count && items[index].IsSubmenu)
        {
            _submenu = items[index].Id;
            RebuildActions();
        }
    }

    private void Move(int from, int to)
    {
        var items = CurrentItems().ToList();
        if (from < 0 || from >= items.Count || to < 0 || to >= items.Count)
        {
            return;
        }

        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        SetCurrentItems(items);
    }

    private void Swap(int a, int b)
    {
        var items = CurrentItems().ToList();
        if (a < 0 || b < 0 || a >= items.Count || b >= items.Count)
        {
            return;
        }

        (items[a], items[b]) = (items[b], items[a]);
        SetCurrentItems(items);
    }

    private void RemoveAt(int index)
    {
        var items = CurrentItems().ToList();
        if (index >= 0 && index < items.Count)
        {
            items.RemoveAt(index);
            SetCurrentItems(items);
        }
    }

    private async Task AddAsync()
    {
        if (CurrentItems().Count >= RadialGeometry.MaxItems || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var editor = new RadialItemEditor(_services, _service.Presenter, new RadialItem { Kind = RadialItemKind.Tool }, allowSubmenu: _submenu is null, isNew: true);
        if (await editor.ShowAsync(owner).ConfigureAwait(true) == RadialEditorResult.Saved)
        {
            SetCurrentItems([.. CurrentItems(), editor.Item]);
        }
    }

    private async Task EditAsync(int index)
    {
        var items = CurrentItems();
        if (index < 0 || index >= items.Count || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var editor = new RadialItemEditor(_services, _service.Presenter, items[index], allowSubmenu: _submenu is null, isNew: false);
        switch (await editor.ShowAsync(owner).ConfigureAwait(true))
        {
            case RadialEditorResult.Saved:
                var edited = editor.Item;
                SetCurrentItems(CurrentItems().Select((item, i) => i == index ? edited with { Children = edited.IsSubmenu ? item.Children : [] } : item).ToList());
                break;
            case RadialEditorResult.Removed:
                RemoveAt(index);
                break;
        }
    }
}
