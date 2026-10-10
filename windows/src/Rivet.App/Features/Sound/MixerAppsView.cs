// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Localization;
using Rivet.Core.Sound;

namespace Rivet.App.Features.Sound;

/// <summary>
/// One app of the mixer (spec §3.9.3): icon with the playing dot, name, pin
/// glyph, per-app output menu, actions menu, mute, slider, percent and reset.
/// </summary>
internal sealed class MixerRowView : UserControl
{
    private readonly MixerService _mixer;
    private readonly AudioDeviceService _devices;
    private readonly Image _image = new() { Width = 28, Height = 28 };
    private readonly Avalonia.Controls.Panel _iconHost = new() { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Ellipse _playingDot = new() { Width = 9, Height = 9, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, StrokeThickness = 1.5 };
    private readonly TextBlock _name = new() { Classes = { "rowTitle" }, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Control _pin = SoundUi.Icon("Pin", 11, "TextTertiaryBrush");
    private readonly TextBlock _caption = SoundUi.Caption(string.Empty, "TextTertiaryBrush");
    private readonly Button _outputButton;
    private readonly Button _moreButton;
    private readonly Button _muteButton;
    private readonly VolumeSlider _slider = new();
    private readonly PercentLabel _percent = new();
    private readonly Button _resetButton;

    public MixerRowView(MixerService mixer, AudioDeviceService devices, MixerRow row)
    {
        _mixer = mixer;
        _devices = devices;
        Row = row;

        _caption.FontSize = 11;
        _caption.TextWrapping = TextWrapping.NoWrap;
        _caption.TextTrimming = TextTrimming.CharacterEllipsis;
        _iconHost.Children.Add(_image);
        _iconHost.Children.Add(_playingDot);
        _playingDot.Bind(Shape.FillProperty, _playingDot.GetResourceObservable("MetricGreenBrush").ToBinding());
        _playingDot.Bind(Shape.StrokeProperty, _playingDot.GetResourceObservable("PanelBackgroundBrush").ToBinding());

        _outputButton = SoundUi.IconButton("ArrowRouting", L.Get("Strings.mixerOutputTooltip"), () => { }, 14);
        _outputButton.Flyout = new MenuFlyout();
        ((MenuFlyout)_outputButton.Flyout).Opening += (_, _) => FillOutputMenu((MenuFlyout)_outputButton.Flyout!);
        _moreButton = SoundUi.IconButton("MoreHorizontal", L.Get("mixer.actions"), () => { }, 14);
        var moreMenu = new MenuFlyout();
        moreMenu.Opening += (_, _) => FillActionsMenu(moreMenu);
        _moreButton.Flyout = moreMenu;
        var contextMenu = new MenuFlyout();
        contextMenu.Opening += (_, _) => FillActionsMenu(contextMenu);
        ContextFlyout = contextMenu;

        _muteButton = SoundUi.IconButton("Speaker2", L.Get("Strings.actionMute"), () => _mixer.ToggleMute(Row.RowId), 14);
        _resetButton = SoundUi.IconButton("ArrowCounterclockwise", L.Get("Strings.mixerResetTooltip"), () => _mixer.ResetVolume(Row.RowId), 13);
        _slider.UserChanged += value => _mixer.SetVolume(Row.RowId, value);
        _percent.Committed += value => _mixer.SetVolume(Row.RowId, value);

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _name, _pin } };
        var texts = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { nameRow, _caption } };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 2 };
        top.Children.Add(texts);
        Grid.SetColumn(_outputButton, 1);
        top.Children.Add(_outputButton);
        Grid.SetColumn(_moreButton, 2);
        top.Children.Add(_moreButton);

        var volume = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,28"), ColumnSpacing = 4, Margin = new Thickness(-6, -6, 0, -4) };
        volume.Children.Add(_muteButton);
        Grid.SetColumn(_slider, 1);
        volume.Children.Add(_slider);
        Grid.SetColumn(_percent, 2);
        volume.Children.Add(_percent);
        Grid.SetColumn(_resetButton, 3);
        volume.Children.Add(_resetButton);

        var body = new StackPanel { Spacing = 0, Children = { top, volume } };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*"), Margin = new Thickness(4, 4, 2, 2) };
        root.Children.Add(_iconHost);
        Grid.SetColumn(body, 1);
        root.Children.Add(body);
        Content = root;
        Background = Brushes.Transparent;
        ToolTip.SetTip(_iconHost, L.Get("mixer.arrange"));
        Update(row);
    }

    public MixerRow Row { get; private set; }

    public bool IsAdjusting => _slider.IsUserDragging;

    public void Update(MixerRow row)
    {
        Row = row;
        _name.Text = row.DisplayName;
        AutomationProperties.SetName(this, row.DisplayName);
        AutomationProperties.SetName(_slider, row.DisplayName);
        _pin.IsVisible = row.IsPinned;
        _playingDot.IsVisible = row.IsPlaying;
        _image.Source = SoundUi.Bitmap(row.Icon);
        if (row.Icon is null && _iconHost.Children.Count == 2)
        {
            _iconHost.Children.Insert(0, SoundUi.Icon(row.IsSystemSounds ? "Alert" : "Apps", 22, "TextSecondaryBrush"));
        }
        else if (row.Icon is not null && _iconHost.Children.Count == 3)
        {
            _iconHost.Children.RemoveAt(0);
        }

        _slider.Show(row.Volume);
        _percent.Show(row.Volume);
        _resetButton.IsVisible = !VolumeMath.IsUnity(row.Volume) || row.Muted;
        var silent = row.IsSilent;
        SoundUi.SetIcon(_muteButton, silent ? "SpeakerMute" : "Speaker2", 14);
        if (_muteButton.Content is Control glyph)
        {
            glyph.Bind(ForegroundProperty, glyph.GetResourceObservable(silent ? "MetricRedBrush" : "TextPrimaryBrush").ToBinding());
        }

        var muteTip = L.Get(row.Muted ? "Strings.actionUnmute" : "Strings.actionMute");
        ToolTip.SetTip(_muteButton, muteTip);
        AutomationProperties.SetName(_muteButton, $"{muteTip} {row.DisplayName}");
        _muteButton.IsEnabled = row.SessionKeys.Count > 0 || VolumeMath.IsSilent(row.Volume);

        _outputButton.IsVisible = _mixer.CanRouteApps && !row.IsSystemSounds;
        var route = row.OutputDeviceId;
        var routed = route is not null;
        if (_outputButton.Content is Control outputGlyph)
        {
            outputGlyph.Bind(ForegroundProperty, outputGlyph.GetResourceObservable(routed ? "AccentBrush" : "TextSecondaryBrush").ToBinding());
        }

        if (routed)
        {
            var device = _devices.Outputs.FirstOrDefault(d => d.Id == route);
            _caption.Text = device is null ? L.Get("Strings.mixerOutputFallback") : device.Name;
            _caption.IsVisible = true;
        }
        else
        {
            _caption.IsVisible = false;
        }
    }

    private void FillOutputMenu(MenuFlyout menu)
    {
        var items = new List<object>();
        var route = Row.OutputDeviceId;
        items.Add(OutputItem(L.Get("Strings.mixerOutputDefault"), null, route is null));
        foreach (var device in _devices.Outputs)
        {
            var label = device.Id == _devices.DefaultOutputId ? $"{device.Name} ({L.Get("Strings.mixerOutputCurrent")})" : device.Name;
            items.Add(OutputItem(label, device.Id, route == device.Id));
        }

        if (route is not null && _devices.Outputs.All(d => d.Id != route))
        {
            items.Add(new MenuItem { Header = L.Get("Strings.mixerOutputUnavailable"), ToggleType = MenuItemToggleType.Radio, IsChecked = true, IsEnabled = false });
        }

        items.Add(new Separator());
        items.Add(new MenuItem { Header = L.Get("win.sound.routingRestartHint"), IsEnabled = false, FontSize = 11 });
        menu.ItemsSource = items;
    }

    private MenuItem OutputItem(string label, string? deviceId, bool isChecked)
    {
        var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = isChecked };
        item.Click += async (_, _) => await _mixer.SetOutputAsync(Row.RowId, deviceId);
        return item;
    }

    private void FillActionsMenu(MenuFlyout menu)
    {
        var items = new List<object>();
        var row = Row;
        if (row.CanArrange)
        {
            var pin = new MenuItem { Header = L.Get(row.IsPinned ? "mixer.unpin" : "mixer.pin"), Icon = SoundUi.Icon(row.IsPinned ? "PinOff" : "Pin", 14) };
            pin.Click += (_, _) =>
            {
                if (row.IsPinned)
                {
                    _mixer.Unpin(row.RowId);
                }
                else
                {
                    _mixer.Pin(row.RowId);
                }
            };
            items.Add(pin);

            var up = new MenuItem { Header = L.Get("mixer.moveUp"), Icon = SoundUi.Icon("ArrowUp", 14), IsEnabled = _mixer.CanMoveBy(row.RowId, -1) };
            up.Click += (_, _) => _mixer.MoveBy(row.RowId, -1);
            var down = new MenuItem { Header = L.Get("mixer.moveDown"), Icon = SoundUi.Icon("ArrowDown", 14), IsEnabled = _mixer.CanMoveBy(row.RowId, 1) };
            down.Click += (_, _) => _mixer.MoveBy(row.RowId, 1);
            items.Add(up);
            items.Add(down);
        }

        if (row.CanHide || row.IsSystemSounds)
        {
            if (items.Count > 0)
            {
                items.Add(new Separator());
            }

            var hide = new MenuItem { Header = L.Get("Strings.mixerHideFromList"), Icon = SoundUi.Icon("EyeOff", 14) };
            hide.Click += (_, _) => _mixer.Hide(row.RowId);
            items.Add(hide);
        }

        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = L.Get("win.sound.noActions"), IsEnabled = false });
        }

        menu.ItemsSource = items;
    }
}

/// <summary>
/// The mixer's app list: rows updated in place (a slider being dragged is
/// never rebuilt), the empty message, and reordering by holding Ctrl and
/// dragging a row onto another row of the same pin group (spec §3.9.6).
/// </summary>
internal sealed class MixerAppsView : UserControl
{
    private readonly MixerService _mixer;
    private readonly AudioDeviceService _devices;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _insertion = new() { Height = 2, IsVisible = false, CornerRadius = new CornerRadius(1) };
    private readonly TextBlock _empty = SoundUi.Caption(string.Empty);
    private readonly Dictionary<string, MixerRowView> _views = new(StringComparer.Ordinal);
    private MixerRowView? _dragRow;
    private (string Target, bool After)? _drop;
    private bool _refreshQueued;

    public MixerAppsView(IServiceProvider services)
    {
        _mixer = services.GetRequiredService<MixerService>();
        _devices = services.GetRequiredService<AudioDeviceService>();
        _empty.Text = L.Get("Strings.mixerEmpty");
        _empty.Margin = new Thickness(6, 8);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _insertion.Bind(Border.BackgroundProperty, _insertion.GetResourceObservable("AccentBrush").ToBinding());
        _overlay.Children.Add(_insertion);
        Content = new Grid { Children = { new StackPanel { Children = { _list, _empty } }, _overlay } };

        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => EndDrag(commit: false);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _mixer.Changed += OnChanged;
        _devices.Changed += OnChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _mixer.Changed -= OnChanged;
        _devices.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        var rows = _mixer.Rows;
        var current = _list.Children.OfType<MixerRowView>().Select(v => v.Row.RowId).ToList();
        var wanted = rows.Select(r => r.RowId).ToList();
        var frozen = _dragRow is not null || _views.Values.Any(v => v.IsAdjusting);
        if (current.SequenceEqual(wanted) || frozen)
        {
            // Same rows (or a drag in progress): update in place; structure catches up afterwards.
            foreach (var row in rows)
            {
                if (_views.TryGetValue(row.RowId, out var view))
                {
                    view.Update(row);
                }
            }

            _empty.IsVisible = current.Count == 0 && rows.Count == 0;
            return;
        }

        _list.Children.Clear();
        foreach (var row in rows)
        {
            if (!_views.TryGetValue(row.RowId, out var view))
            {
                view = new MixerRowView(_mixer, _devices, row);
                _views[row.RowId] = view;
            }
            else
            {
                view.Update(row);
            }

            _list.Children.Add(view);
        }

        foreach (var stale in _views.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            _views.Remove(stale);
        }

        _empty.IsVisible = rows.Count == 0;
    }

    // ── Ctrl+drag reorder ───────────────────────────────────────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var row = RowAt(e.GetPosition(_list));
        if (row is null || !row.Row.CanArrange)
        {
            return;
        }

        _dragRow = row;
        row.Opacity = 0.45;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is null)
        {
            return;
        }

        e.Handled = true;
        var position = e.GetPosition(_list);
        var target = RowAt(position);
        if (target is null || ReferenceEquals(target, _dragRow) || !target.Row.CanArrange || target.Row.IsPinned != _dragRow.Row.IsPinned)
        {
            _drop = null;
            _insertion.IsVisible = false;
            return;
        }

        var bounds = target.Bounds;
        var after = position.Y > bounds.Top + (bounds.Height / 2);
        _drop = (target.Row.RowId, after);
        var y = after ? bounds.Bottom : bounds.Top;
        var listOffset = _list.TranslatePoint(new Point(0, 0), _overlay) ?? new Point(0, 0);
        Canvas.SetLeft(_insertion, listOffset.X);
        Canvas.SetTop(_insertion, listOffset.Y + y - 1);
        _insertion.Width = _list.Bounds.Width;
        _insertion.IsVisible = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is null)
        {
            return;
        }

        e.Handled = true;
        EndDrag(commit: true);
        e.Pointer.Capture(null);
    }

    private void EndDrag(bool commit)
    {
        if (_dragRow is not { } row)
        {
            return;
        }

        _dragRow = null;
        row.Opacity = 1;
        _insertion.IsVisible = false;
        if (commit && _drop is { } drop)
        {
            _mixer.Move(row.Row.RowId, drop.Target, drop.After);
        }

        _drop = null;
        Refresh();
    }

    private MixerRowView? RowAt(Point position) =>
        _list.Children.OfType<MixerRowView>().FirstOrDefault(v => v.Bounds.Top <= position.Y && position.Y < v.Bounds.Bottom);
}
