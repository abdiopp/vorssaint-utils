// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Sound;

namespace Rivet.App.Features.Sound;

/// <summary>
/// The mixer's audio-devices block (spec §3.10.1, §3.13): the output picker
/// ("all apps") with the output level, and the microphone picker with the
/// input level. Used in the panel and on the Volume mixer settings page.
/// </summary>
internal sealed class DevicesBlock : UserControl
{
    private readonly AudioDeviceService _devices;
    private readonly MicMuteService _micMute;
    private readonly FeatureRuntime _runtime;
    private readonly ComboBox _outputBox = Picker();
    private readonly ComboBox _inputBox = Picker();
    private readonly Button _outputMute;
    private readonly VolumeSlider _outputSlider = new();
    private readonly PercentLabel _outputPercent = new();
    private readonly Grid _outputVolumeRow;
    private readonly TextBlock _outputMessage = SoundUi.Caption(string.Empty, "WarningBrush");
    private readonly Avalonia.Controls.Panel _inputIcon = new() { Width = 28 };
    private readonly VolumeSlider _inputSlider = new();
    private readonly PercentLabel _inputPercent = new();
    private readonly Grid _inputVolumeRow;
    private readonly TextBlock _inputMessage = SoundUi.Caption(string.Empty);
    private List<string?> _outputIds = [];
    private List<string?> _inputIds = [];
    private string? _inputError;
    private bool _updating;
    private bool _refreshPending;

    public DevicesBlock(IServiceProvider services)
    {
        _devices = services.GetRequiredService<AudioDeviceService>();
        _micMute = services.GetRequiredService<MicMuteService>();
        _runtime = services.GetRequiredService<FeatureRuntime>();

        ToolTip.SetTip(_outputBox, L.Get("Strings.mixerSystemOutputTooltip"));
        AutomationProperties.SetName(_outputBox, L.Get("Strings.mixerSystemOutputTitle"));
        ToolTip.SetTip(_inputBox, L.Get("Strings.mixerInputTooltip"));
        AutomationProperties.SetName(_inputBox, L.Get("Strings.mixerInputTitle"));
        _outputBox.SelectionChanged += (_, _) => OnOutputPicked();
        _inputBox.SelectionChanged += (_, _) => OnInputPicked();
        _outputBox.DropDownClosed += (_, _) => FlushPendingRefresh();
        _inputBox.DropDownClosed += (_, _) => FlushPendingRefresh();

        _outputMute = SoundUi.IconButton("Speaker2", L.Get("Strings.actionMute"), ToggleOutputMute);
        _outputSlider.UserChanged += level => _devices.SetOutputVolume(level);
        _outputPercent.Committed += level => _devices.SetOutputVolume(level);
        _inputSlider.UserChanged += level => _devices.SetInputVolume(level);
        _inputPercent.Committed += level => _devices.SetInputVolume(level);
        AutomationProperties.SetName(_outputSlider, L.Get("notch.volume"));
        AutomationProperties.SetName(_inputSlider, L.Get("Strings.mixerInputTitle"));

        _outputVolumeRow = VolumeRow(_outputMute, _outputSlider, _outputPercent);
        _inputVolumeRow = VolumeRow(_inputIcon, _inputSlider, _inputPercent);
        _outputMessage.IsVisible = false;
        _inputMessage.IsVisible = false;

        Content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                PickerRow("Speaker2", _outputBox),
                _outputVolumeRow,
                _outputMessage,
                PickerRow("Mic", _inputBox),
                _inputVolumeRow,
                _inputMessage,
            },
        };

        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _devices.Changed += OnChanged;
        _micMute.Changed += OnChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _devices.Changed -= OnChanged;
        _micMute.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private static ComboBox Picker() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        MinWidth = 0,
        FontSize = 12.5,
    };

    private static Grid PickerRow(string icon, ComboBox box)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*"), ColumnSpacing = 6 };
        var symbol = SoundUi.Icon(icon, 16, "AccentBrush");
        symbol.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(symbol);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private static Grid VolumeRow(Control leading, VolumeSlider slider, PercentLabel percent)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, -4, 0, 0) };
        leading.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(leading);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(percent, 2);
        grid.Children.Add(percent);
        return grid;
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void FlushPendingRefresh()
    {
        if (_refreshPending)
        {
            _refreshPending = false;
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    private void Refresh()
    {
        if (_outputBox.IsDropDownOpen || _inputBox.IsDropDownOpen)
        {
            // Rebuilding the items would close the open list; catch up when it closes.
            _refreshPending = true;
            RefreshLevels();
            return;
        }

        _updating = true;
        try
        {
            RefreshOutputPicker();
            RefreshInputPicker();
        }
        finally
        {
            _updating = false;
        }

        RefreshLevels();
    }

    private void RefreshOutputPicker()
    {
        var outputs = _devices.Outputs;
        var labels = outputs.Select(d => d.Name).ToList();
        _outputIds = outputs.Select(d => (string?)d.Id).ToList();
        var current = _devices.DefaultOutputId;
        var index = _outputIds.IndexOf(current);
        if (outputs.Count == 0)
        {
            labels = [L.Get("Strings.mixerSystemOutputNoDevices")];
            _outputIds = [null];
            index = 0;
        }
        else if (index < 0)
        {
            labels.Insert(0, L.Get("Strings.mixerOutputUnavailable"));
            _outputIds.Insert(0, null);
            index = 0;
        }

        _outputBox.ItemsSource = labels;
        _outputBox.SelectedIndex = index;
        _outputBox.IsEnabled = outputs.Count > 0 && _devices.Capabilities.CanSetDefaultDevice;
    }

    private void RefreshInputPicker()
    {
        var inputs = _devices.Inputs;
        var current = _devices.DefaultInputId;
        var labels = new List<string>();
        _inputIds = [];
        int index;
        string? fallback = null;
        if (inputs.Count == 0)
        {
            labels.Add(L.Get("Strings.mixerInputNoDevices"));
            _inputIds.Add(null);
            index = 0;
        }
        else if (_devices.PriorityOwnsInput)
        {
            // Audio priority owns the input: the picker shows and sets the current system input.
            labels.AddRange(inputs.Select(d => d.Name));
            _inputIds.AddRange(inputs.Select(d => (string?)d.Id));
            index = Math.Max(0, _inputIds.IndexOf(current));
        }
        else
        {
            labels.Add(L.Get("Strings.mixerOutputDefault"));
            _inputIds.Add(null);
            foreach (var input in inputs)
            {
                labels.Add(input.Id == current ? $"{input.Name} ({L.Get("Strings.mixerOutputCurrent")})" : input.Name);
                _inputIds.Add(input.Id);
            }

            var preferred = _devices.PreferredInputId;
            index = preferred is null ? 0 : _inputIds.IndexOf(preferred);
            if (preferred is not null && index < 0)
            {
                labels.Add(L.Get("Strings.mixerInputUnavailable"));
                _inputIds.Add(preferred);
                index = labels.Count - 1;
                fallback = L.Get("Strings.mixerInputFallback");
            }
        }

        if (_inputError is not null)
        {
            ShowInputMessage(_inputError, isError: true);
        }
        else if (fallback is not null)
        {
            ShowInputMessage(fallback, isError: false);
        }
        else
        {
            _inputMessage.IsVisible = false;
        }

        _inputBox.ItemsSource = labels;
        _inputBox.SelectedIndex = index;
        _inputBox.IsEnabled = inputs.Count > 0 && _devices.Capabilities.CanSetDefaultDevice;
    }

    private void RefreshLevels()
    {
        var output = _devices.OutputVolume;
        _outputVolumeRow.IsVisible = _devices.DefaultOutput is not null && output is { CanSetVolume: true };
        if (output is not null)
        {
            _outputSlider.Show(output.Scalar);
            _outputPercent.Show(output.Scalar);
            var silent = output.Muted || output.Scalar <= 0;
            SoundUi.SetIcon(_outputMute, silent ? "SpeakerMute" : "Speaker2");
            var tip = L.Get(output.Muted ? "Strings.actionUnmute" : "Strings.actionMute");
            ToolTip.SetTip(_outputMute, tip);
            AutomationProperties.SetName(_outputMute, tip);
        }

        var input = _devices.InputVolume;
        var micMuted = _runtime.IsAvailable(FeatureIds.MicMute) && _micMute.IsMuted;
        _inputVolumeRow.IsVisible = _devices.DefaultInput is not null && input is { CanSetVolume: true };
        _inputVolumeRow.IsEnabled = !micMuted;
        _inputIcon.Children.Clear();
        _inputIcon.Children.Add(SoundUi.Icon(micMuted || input?.Muted == true ? "MicOff" : "Mic", 15, micMuted ? "WarningBrush" : "TextSecondaryBrush"));
        ToolTip.SetTip(_inputVolumeRow, micMuted ? L.Get("win.sound.inputLevelMuted") : null);
        if (input is not null)
        {
            _inputSlider.Show(input.Scalar);
            _inputPercent.Show(input.Scalar);
        }
    }

    private async void OnOutputPicked()
    {
        if (_updating || _outputBox.SelectedIndex < 0 || _outputBox.SelectedIndex >= _outputIds.Count)
        {
            return;
        }

        var id = _outputIds[_outputBox.SelectedIndex];
        if (id is null || id == _devices.DefaultOutputId)
        {
            return;
        }

        var status = await SafeSwitch(() => _devices.SwitchOutputForAllAppsAsync(id));
        _outputMessage.Text = status == AudioSwitchStatus.Success ? string.Empty : OutputSwitcherService.SwitchErrorText(status);
        _outputMessage.IsVisible = status != AudioSwitchStatus.Success;
        Refresh();
    }

    private async void OnInputPicked()
    {
        if (_updating || _inputBox.SelectedIndex < 0 || _inputBox.SelectedIndex >= _inputIds.Count)
        {
            return;
        }

        var id = _inputIds[_inputBox.SelectedIndex];
        if (id is not null && !_devices.Inputs.Any(d => d.Id == id))
        {
            return;
        }

        var status = await SafeSwitch(() => _devices.ChooseInputAsync(id));
        _inputError = status switch
        {
            AudioSwitchStatus.Success => null,
            AudioSwitchStatus.Unavailable => L.Get("Strings.mixerInputUnavailable"),
            _ => L.Format("Strings.mixerInputErrorFormat", L.Get("win.sound.switchRefused")),
        };
        Refresh();
    }

    /// <summary>Event handlers are async void: nothing may escape them.</summary>
    private static async Task<AudioSwitchStatus> SafeSwitch(Func<Task<AudioSwitchStatus>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            Rivet.Core.Diagnostics.Log.Warn("sound", "Switching the device failed.", ex);
            return AudioSwitchStatus.Failed;
        }
    }

    private void ShowInputMessage(string text, bool isError)
    {
        _inputMessage.Text = text;
        _inputMessage.IsVisible = true;
        _inputMessage.Bind(TextBlock.ForegroundProperty, _inputMessage.GetResourceObservable(isError ? "WarningBrush" : "TextSecondaryBrush").ToBinding());
    }

    private void ToggleOutputMute()
    {
        if (_devices.OutputVolume is { } volume)
        {
            _ = _devices.SetOutputMuteAsync(!volume.Muted);
        }
    }
}
