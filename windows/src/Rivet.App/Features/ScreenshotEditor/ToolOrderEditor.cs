// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.ScreenshotEditor;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using CoreModifiers = Rivet.Core.Shortcuts.KeyModifiers;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// "Editor tools" (spec 01 §3.10.15): tool order and per-tool keys, shown in
/// the rail's gear popover and on the Settings page. A digit moves the tool
/// into that slot, Delete clears its key (the tool moves just below slot 9),
/// any other key becomes the tool's key after the conflict checks.
/// </summary>
internal sealed class ToolOrderEditor : UserControl
{
    private readonly ISettingsStore _settings;
    private readonly ShortcutManager? _shortcuts;
    private readonly IKeyNameProvider? _keyNames;
    private readonly IKeyboardLayoutInfo _layout;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ToggleSwitch _enabled = new() { Classes = { "compact" } };
    private IDisposable? _subscription;
    private KeyField? _recording;

    public ToolOrderEditor(IServiceProvider services, bool compact)
    {
        _settings = services.GetRequiredService<ISettingsStore>();
        _shortcuts = services.GetService<ShortcutManager>();
        _keyNames = services.GetService<IKeyNameProvider>();
        _layout = services.GetService<IKeyboardLayoutInfo>() ?? new UsKeyboardLayoutInfo();
        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("WarningBrush").ToBinding());

        _enabled.IsChecked = _settings.Get(EditorSettings.ToolShortcutsEnabled);
        _enabled.IsCheckedChanged += (_, _) => _settings.Set(EditorSettings.ToolShortcutsEnabled, _enabled.IsChecked == true);
        AutomationProperties.SetName(_enabled, L.Get("screenshot.toolShortcutsToggle"));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock { Text = L.Get("screenshot.toolShortcutsToggle"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_enabled, 1);
        header.Children.Add(_enabled);

        var caption = new TextBlock { Text = L.Get("screenshot.toolShortcutsCaption"), Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        var reset = new Button { Content = L.Get("win.shell.reset"), HorizontalAlignment = HorizontalAlignment.Right };
        reset.Click += (_, _) =>
        {
            _settings.Reset(EditorSettings.ToolOrderCsv.Key);
            _settings.Reset(EditorSettings.ToolShortcutsCsv.Key);
            ShowError(null);
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { header, caption, _rows, _error, reset },
        };
        if (compact)
        {
            stack.Children.Insert(0, new TextBlock { Text = L.Get("screenshot.toolShortcutsTitle"), FontSize = 14, FontWeight = FontWeight.SemiBold });
        }

        Content = stack;
        Padding = compact ? new Thickness(4) : new Thickness(0);
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _subscription ??= _settings.Observe(
            () => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild),
            EditorSettings.ToolOrderCsv, EditorSettings.ToolShortcutsCsv, EditorSettings.ToolShortcutsEnabled);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopRecording();
        _subscription?.Dispose();
        _subscription = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Whether a key field is listening (the editor's own shortcuts are suspended meanwhile).</summary>
    public bool IsRecording => _recording is not null;

    private IReadOnlyList<EditorTool> Order => ToolOrder.Parse(_settings.Get(EditorSettings.ToolOrderCsv));

    private ToolBindings Bindings => ToolBindings.Parse(_settings.Get(EditorSettings.ToolShortcutsCsv));

    private void Rebuild()
    {
        _enabled.IsChecked = _settings.Get(EditorSettings.ToolShortcutsEnabled);
        var order = Order;
        var bindings = Bindings;
        _rows.Children.Clear();
        for (var i = 0; i < order.Count; i++)
        {
            _rows.Children.Add(BuildRow(order, bindings, order[i], i));
        }
    }

    private Control BuildRow(IReadOnlyList<EditorTool> order, ToolBindings bindings, EditorTool tool, int index)
    {
        var title = L.Get(EditorTools.TitleKey(tool));
        var icon = new SymbolIcon
        {
            Symbol = IconConverter.Parse(EditorTools.Icon(tool)),
            FontSize = 16,
            IconVariant = tool == EditorTool.Redact ? FluentIcons.Common.IconVariant.Filled : FluentIcons.Common.IconVariant.Regular,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var key = bindings.KeyFor(tool);
        var suspended = key is { } k && ToolKeyMap.IsSuspended(k, _layout.DigitTypedBy);
        var digit = ToolOrder.DigitOf(order, tool);
        var field = new KeyField(this, tool)
        {
            Width = 86,
            Placeholder = digit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? L.Get("win.shell.shortcutNone"),
            Chord = suspended ? null : key,
            KeyNames = _keyNames,
        };
        AutomationProperties.SetName(field, $"{title} {L.Get("screenshot.toolShortcutsTitle")}");

        var resetKey = SmallButton("ArrowCounterclockwise", L.Get("win.shell.reset"), () => Apply(tool, KeyChord.None, typedDigit: null, clearOnly: true));
        resetKey.IsVisible = key is not null;
        var up = SmallButton("ChevronUp", L.Get("win.screenshotEditor.moveUp"), () => _settings.Set(EditorSettings.ToolOrderCsv, ToolOrder.Format(ToolOrder.Swap(Order, tool, -1))));
        var down = SmallButton("ChevronDown", L.Get("win.screenshotEditor.moveDown"), () => _settings.Set(EditorSettings.ToolOrderCsv, ToolOrder.Format(ToolOrder.Swap(Order, tool, +1))));
        up.IsEnabled = index > 0;
        down.IsEnabled = index < order.Count - 1;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 1) };
        grid.Children.Add(icon);
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Grid.SetColumn(resetKey, 2);
        grid.Children.Add(resetKey);
        Grid.SetColumn(field, 3);
        grid.Children.Add(field);
        Grid.SetColumn(up, 4);
        grid.Children.Add(up);
        Grid.SetColumn(down, 5);
        grid.Children.Add(down);
        return grid;
    }

    private static Button SmallButton(string icon, string tip, Action onClick)
    {
        var button = new Button
        {
            Content = new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 13 },
            Padding = new Thickness(5),
            MinWidth = 0,
            MinHeight = 0,
            Classes = { "icon" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    internal void StartRecording(KeyField field)
    {
        if (_recording is not null && _recording != field)
        {
            _recording.SetRecording(false);
        }

        if (_recording is null)
        {
            _shortcuts?.Suspend();
        }

        _recording = field;
        field.SetRecording(true);
        ShowError(null);
    }

    internal void StopRecording()
    {
        if (_recording is null)
        {
            return;
        }

        _recording.SetRecording(false);
        _recording = null;
        _shortcuts?.Resume();
    }

    internal void Recorded(EditorTool tool, KeyChord chord, int? typedDigit)
    {
        StopRecording();
        Apply(tool, chord, typedDigit, clearOnly: false);
    }

    private void Apply(EditorTool tool, KeyChord chord, int? typedDigit, bool clearOnly)
    {
        if (clearOnly)
        {
            _settings.Set(EditorSettings.ToolShortcutsCsv, Bindings.With(tool, null).Format());
            ShowError(null);
            return;
        }

        var outcome = ToolKeyRecorder.Record(
            Order, Bindings, tool, chord, typedDigit,
            globalShortcutOwner: c => _shortcuts?.FindConflict(c, null, includeInactive: false) is { } role ? L.Get(role.TitleKey) : null,
            isSystemShortcut: ReservedShortcuts.IsReserved,
            toolName: t => L.Get(EditorTools.TitleKey(t)));
        if (!outcome.Accepted)
        {
            var reason = outcome.Rejection switch
            {
                ToolKeyRejection.ReservedByEditor => L.Get("screenshot.toolShortcutReserved"),
                ToolKeyRejection.UsedBySystem => L.Get("win.shell.shortcutReserved"),
                _ => L.Format("win.shell.shortcutUsedByFormat", outcome.ConflictName ?? string.Empty),
            };
            ShowError($"{L.Get(EditorTools.TitleKey(tool))} · {chord.ToDisplayString(_keyNames)}  {reason}");
            return;
        }

        _settings.Set(EditorSettings.ToolOrderCsv, ToolOrder.Format(outcome.Order));
        _settings.Set(EditorSettings.ToolShortcutsCsv, outcome.Bindings.Format());
        ShowError(null);
    }

    private void ShowError(string? text)
    {
        _error.Text = text;
        _error.IsVisible = !string.IsNullOrEmpty(text);
    }
}

/// <summary>A tool's shortcut field: click, then press a key (Esc cancels).</summary>
internal sealed class KeyField : Button
{
    private readonly ToolOrderEditor _owner;
    private readonly EditorTool _tool;
    private readonly TextBlock _label = new() { HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private bool _recording;

    public KeyField(ToolOrderEditor owner, EditorTool tool)
    {
        _owner = owner;
        _tool = tool;
        Content = _label;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Padding = new Thickness(4, 3);
        MinHeight = 0;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        this.Bind(BackgroundProperty, this.GetResourceObservable("PanelControlBrush").ToBinding());
        this.Bind(BorderBrushProperty, this.GetResourceObservable("PanelCardBorderBrush").ToBinding());
        Click += (_, _) =>
        {
            if (_recording)
            {
                _owner.StopRecording();
            }
            else
            {
                _owner.StartRecording(this);
                Focus();
            }
        };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    public string Placeholder { get; init; } = string.Empty;

    public KeyChord? Chord { get; init; }

    public IKeyNameProvider? KeyNames { get; init; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateLabel();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_recording)
        {
            _owner.StopRecording();
        }
    }

    public void SetRecording(bool recording)
    {
        _recording = recording;
        UpdateLabel();
    }

    private void UpdateLabel()
    {
        if (_recording)
        {
            _label.Text = L.Get("win.shell.shortcutPressKeys");
            _label.Opacity = 1;
        }
        else if (Chord is { } chord)
        {
            _label.Text = chord.ToDisplayString(KeyNames);
            _label.Opacity = 1;
        }
        else
        {
            _label.Text = Placeholder;
            _label.Opacity = 0.55;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        e.Handled = true;
        var vk = AvaloniaKeyMap.ToVirtualKey(e.Key);
        if (vk == 0 || VirtualKeys.IsModifier(vk))
        {
            return;
        }

        var modifiers = AvaloniaKeyMap.ToModifiers(e.KeyModifiers);
        if (vk == VirtualKeys.Escape && modifiers == CoreModifiers.None)
        {
            _owner.StopRecording();
            return;
        }

        if (vk is VirtualKeys.Back or VirtualKeys.Delete && modifiers == CoreModifiers.None)
        {
            _owner.Recorded(_tool, KeyChord.None, null);
            return;
        }

        // Digits are judged by what the key types on the current layout (AZERTY's Shift+& is 1).
        int? digit = e.KeySymbol is { Length: 1 } symbol && symbol[0] is >= '1' and <= '9'
                     && (modifiers & (CoreModifiers.Control | CoreModifiers.Alt | CoreModifiers.Win)) == 0
            ? symbol[0] - '0'
            : null;
        _owner.Recorded(_tool, new KeyChord(modifiers, vk), digit);
    }
}
