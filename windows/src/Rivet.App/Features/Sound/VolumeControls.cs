// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.Core.Localization;
using Rivet.Core.Sound;

namespace Rivet.App.Features.Sound;

/// <summary>
/// A 0–100 % slider that reports only the user's changes, ignores model
/// updates while it is being dragged, and steps 1 % per mouse-wheel notch
/// (Windows' own flyout steps 2 %): the "precise roller" for the mouse.
/// </summary>
internal sealed class VolumeSlider : Slider
{
    private bool _quiet;
    private bool _dragging;

    public VolumeSlider()
    {
        Minimum = 0;
        Maximum = 100;
        SmallChange = 1;
        LargeChange = 5;
        VerticalAlignment = VerticalAlignment.Center;
        AddHandler(Thumb.DragStartedEvent, (_, _) => _dragging = true, handledEventsToo: true);
        AddHandler(Thumb.DragCompletedEvent, (_, _) => _dragging = false, handledEventsToo: true);
        PointerWheelChanged += OnWheel;
        ValueChanged += (_, e) =>
        {
            if (!_quiet)
            {
                UserChanged?.Invoke(e.NewValue / 100.0);
            }
        };
    }

    /// <summary>The user moved the slider (0…1).</summary>
    public event Action<double>? UserChanged;

    /// <summary>The thumb is held (by our tracking or the slider's own).</summary>
    public bool IsUserDragging => _dragging || IsDragging;

    protected override Type StyleKeyOverride => typeof(Slider);

    /// <summary>Shows a model value (0…1) without echoing it back; ignored mid-drag.</summary>
    public void Show(double scalar)
    {
        if (IsUserDragging)
        {
            return;
        }

        var value = Math.Round(VolumeMath.Clamp01(scalar) * 100, 1);
        if (Math.Abs(Value - value) < 0.05)
        {
            return;
        }

        _quiet = true;
        try
        {
            Value = value;
        }
        finally
        {
            _quiet = false;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!IsEnabled || e.Delta.Y == 0)
        {
            return;
        }

        Value = Math.Clamp(Math.Round(Value) + Math.Sign(e.Delta.Y), Minimum, Maximum);
        e.Handled = true;
    }
}

/// <summary>
/// The percentage next to a slider. Clicking it turns it into a selected text
/// field (spec §3.9.3): a number with an optional "%" and the UI language's
/// decimal separator, clamped; Enter commits, Esc cancels, leaving the field
/// commits a valid entry. An invalid entry keeps the field open, outlined.
/// </summary>
internal sealed class PercentLabel : ContentControl
{
    private readonly Button _button = new()
    {
        Classes = { "icon" },
        Padding = new Thickness(4, 2),
        MinWidth = 42,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBox _box = new()
    {
        Width = 50,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(4, 2),
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Right,
    };

    private readonly TextBlock _text = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private double _value;
    private bool _editing;

    public PercentLabel()
    {
        _button.Content = _text;
        _button.Click += (_, _) => BeginEdit();
        ToolTip.SetTip(_button, L.Get("win.sound.percentEditTooltip"));
        _box.KeyDown += OnKey;
        _box.LostFocus += (_, _) => Commit(onFocusLost: true);
        Content = _button;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>The value was typed and accepted (0…1).</summary>
    public event Action<double>? Committed;

    public double MaxPercent { get; init; } = 100;

    public void Show(double scalar)
    {
        _value = scalar;
        _text.Text = VolumeMath.FormatPercent(scalar, Localizer.Current.Culture);
        AutomationProperties.SetName(_button, _text.Text);
    }

    private void BeginEdit()
    {
        if (!IsEnabled)
        {
            return;
        }

        _editing = true;
        _box.Text = VolumeMath.ToPercent(_value).ToString(Localizer.Current.Culture);
        _box.BorderBrush = null;
        Content = _box;
        _box.Focus();
        _box.SelectAll();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit(onFocusLost: false);
                e.Handled = true;
                break;
            case Key.Escape:
                EndEdit();
                e.Handled = true;
                break;
        }
    }

    private void Commit(bool onFocusLost)
    {
        if (!_editing)
        {
            return;
        }

        if (VolumeMath.TryParsePercent(_box.Text, MaxPercent, Localizer.Current.Culture, out var scalar))
        {
            EndEdit();
            Show(scalar);
            Committed?.Invoke(scalar);
        }
        else if (onFocusLost)
        {
            EndEdit();
        }
        else
        {
            _box.BorderBrush = SoundUi.Brush(this, "WarningBrush") ?? Brushes.OrangeRed;
            _box.SelectAll();
        }
    }

    private void EndEdit()
    {
        _editing = false;
        Content = _button;
    }
}
