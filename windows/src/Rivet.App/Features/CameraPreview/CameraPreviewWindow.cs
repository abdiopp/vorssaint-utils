// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.App.Shell;
using Rivet.Core.Localization;
using Rivet.Core.Modules.CameraPreview;
using Rivet.Core.Platform;

namespace Rivet.App.Features.CameraPreview;

/// <summary>
/// The floating mirror (spec 07 §3.5.4–3.5.6): a fixed 320×240 rounded
/// frame (radius 14, 1 DIP white border at 14 %, black fill, a soft shadow),
/// always dark, no close button, draggable anywhere. Shows a spinner while
/// starting, the mirrored aspect-filled video while running, or a status
/// with an icon, a caption and (denied/unavailable) one button. With two or
/// more cameras a picker capsule fades in at the bottom while hovered.
/// </summary>
public sealed class CameraPreviewWindow : Window
{
    /// <summary>Room around the frame for the shadow; the window is this much larger on every side.</summary>
    public const double ShadowMargin = 14;

    private const double PickerLabelMaxWidth = 210;

    private readonly CameraPreviewService _service;
    private readonly Border _frame;
    private readonly CameraFrameView _video = new() { IsVisible = false };
    private readonly CameraSpinner _spinner = new() { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _status;
    private readonly SymbolIcon _statusIcon = new() { FontSize = 26, Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _statusText = new() { FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)) };
    private readonly Button _statusButton = new() { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, Padding = new Thickness(12, 4) };
    private readonly Border _picker;
    private readonly TextBlock _pickerLabel = new() { FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.None };
    private CameraPreviewState _state = CameraPreviewState.Idle;
    private MenuFlyout? _menu;
    private bool _pointerInside;
    private bool _chromeApplied;

    public CameraPreviewWindow(CameraPreviewService service)
    {
        _service = service;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        Width = CameraPreviewLayout.Width + (2 * ShadowMargin);
        Height = CameraPreviewLayout.Height + (2 * ShadowMargin);
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        RequestedThemeVariant = ThemeVariant.Dark;
        Title = L.Get("cameraPreview.pageTitle");

        _statusButton.Click += (_, _) => OnStatusButton();
        _status = new StackPanel
        {
            IsVisible = false,
            Spacing = 10,
            Margin = new Thickness(28),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _statusIcon, _statusText, _statusButton },
        };

        var pickerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new SymbolIcon { Symbol = Symbol.Camera, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                _pickerLabel,
                new SymbolIcon { Symbol = Symbol.ChevronDown, FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _picker = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 5),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.Hand),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = pickerContent,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = CameraPreviewLayout.HoverAnimation }],
        };
        ToolTip.SetTip(_picker, L.Get("win.cameraPreview.chooseCamera"));
        _picker.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_picker).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                OpenCameraMenu();
            }
        };

        var inner = new Border
        {
            CornerRadius = new CornerRadius(CameraPreviewLayout.CornerRadius),
            Background = Brushes.Black,
            ClipToBounds = true,
            Child = new Grid { Children = { _video, _spinner, _status, _picker } },
        };
        _frame = new Border
        {
            Width = CameraPreviewLayout.Width,
            Height = CameraPreviewLayout.Height,
            Margin = new Thickness(ShadowMargin),
            CornerRadius = new CornerRadius(CameraPreviewLayout.CornerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            Background = Brushes.Black,
            BoxShadow = BoxShadows.Parse("0 4 14 0 #66000000"),
            Child = inner,
        };
        _frame.PointerPressed += OnFramePointerPressed;
        _frame.PointerEntered += (_, _) => SetPointerInside(true);
        _frame.PointerExited += (_, _) => SetPointerInside(false);
        Content = _frame;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _service.Hide(CameraHideReason.Escape);
            }
        };
        Opened += (_, _) => ApplyChromeOnce();
    }

    public CameraPreviewState ShownState => _state;

    public bool PickerVisible => _picker.IsHitTestVisible;

    /// <summary>"Animation effects" off in Windows: the picker appears and disappears without the fade.</summary>
    public bool ReduceMotion
    {
        get => _picker.Transitions is null;
        set => _picker.Transitions = value ? null : [new DoubleTransition { Property = OpacityProperty, Duration = CameraPreviewLayout.HoverAnimation }];
    }

    public bool HasFrame => _video.HasFrame;

    /// <summary>The camera menu is open (its popup may reach outside the frame; clicks there must not dismiss).</summary>
    public bool IsMenuOpen() => _menu?.IsOpen == true;

    /// <summary>The frame's rectangle in physical pixels (the window minus the shadow margin).</summary>
    public Core.Platform.PixelRect FrameRect()
    {
        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var margin = (int)Math.Round(ShadowMargin * scale);
        return new Core.Platform.PixelRect(
            Position.X + margin,
            Position.Y + margin,
            (int)Math.Round(CameraPreviewLayout.Width * scale),
            (int)Math.Round(CameraPreviewLayout.Height * scale));
    }

    /// <summary>Places the frame per spec (top centre of the pointer's monitor) by moving the window.</summary>
    public void Place(ScreenInfo screen)
    {
        var frame = CameraPreviewLayout.Position(screen);
        var scale = screen.Scale <= 0 ? 1 : screen.Scale;
        var margin = (int)Math.Round(ShadowMargin * scale);
        Position = new Avalonia.PixelPoint(frame.X - margin, frame.Y - margin);
    }

    public void ShowState(CameraPreviewState state)
    {
        _state = state;
        var running = state == CameraPreviewState.Running;
        _spinner.IsVisible = state is CameraPreviewState.Idle or CameraPreviewState.WaitingPermission or CameraPreviewState.Starting;
        _video.IsVisible = running;
        if (!running)
        {
            _video.Clear();
        }

        _status.IsVisible = state is CameraPreviewState.Denied or CameraPreviewState.Unavailable or CameraPreviewState.NoCamera;
        switch (state)
        {
            case CameraPreviewState.Denied:
                _statusIcon.Symbol = Symbol.CameraOff;
                _statusText.Text = L.Get("win.cameraPreview.deniedMessage");
                _statusButton.Content = L.Get("win.cameraPreview.openPrivacySettings");
                _statusButton.IsVisible = true;
                break;
            case CameraPreviewState.Unavailable:
                _statusIcon.Symbol = Symbol.CameraOff;
                _statusText.Text = L.Get("win.cameraPreview.unavailableMessage");
                _statusButton.Content = L.Get("win.cameraPreview.retry");
                _statusButton.IsVisible = true;
                break;
            case CameraPreviewState.NoCamera:
                _statusIcon.Symbol = Symbol.Camera;
                _statusText.Text = L.Get("cameraPreview.noCameraMessage");
                _statusButton.IsVisible = false;
                break;
        }

        UpdatePicker();
    }

    public void ShowFrame(PixelBuffer frame)
    {
        if (_state == CameraPreviewState.Running)
        {
            _video.SetFrame(frame);
        }
    }

    /// <summary>Refreshes the picker after the camera list or the current camera changed.</summary>
    public void UpdatePicker()
    {
        var cameras = _service.Controller.Cameras;
        var visible = _state == CameraPreviewState.Running && _pointerInside && cameras.Count >= 2;
        var name = _service.Controller.CurrentCamera?.Name ?? string.Empty;
        _pickerLabel.Text = name;
        _pickerLabel.Measure(Size.Infinity);

        // A name that does not fit drops the label; the capsule keeps the icon and chevron.
        _pickerLabel.IsVisible = name.Length > 0 && _pickerLabel.DesiredSize.Width <= PickerLabelMaxWidth;
        _picker.Opacity = visible ? 1 : 0;
        _picker.IsHitTestVisible = visible;
    }

    public void SetPointerInside(bool inside)
    {
        _pointerInside = inside;
        UpdatePicker();
    }

    private void OpenCameraMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.Top };
        foreach (var camera in _service.Controller.Cameras)
        {
            var item = new MenuItem
            {
                Header = camera.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = camera.Id == _service.Controller.CurrentDeviceId,
            };
            var id = camera.Id;
            item.Click += (_, _) => _service.Controller.SelectCamera(id);
            flyout.Items.Add(item);
        }

        _menu = flyout;
        flyout.ShowAt(_picker);
    }

    private void OnStatusButton()
    {
        switch (_state)
        {
            case CameraPreviewState.Denied:
                _service.OpenPrivacySettings();
                break;
            case CameraPreviewState.Unavailable:
                _service.Controller.Retry();
                break;
        }
    }

    private void OnFramePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual source && (source is Button || source.FindAncestorOfType<Button>() is not null))
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void ApplyChromeOnce()
    {
        if (_chromeApplied)
        {
            return;
        }

        _chromeApplied = true;

        // Hidden from the taskbar and Alt+Tab, but focusable so Esc reaches it.
        WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow);
    }
}

public enum CameraHideReason
{
    Escape,
    Hotkey,
    OutsideClick,
    AppSwitch,
    Session,
    Uninstall,
}
