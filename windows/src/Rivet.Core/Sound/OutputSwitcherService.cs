// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Sound;

/// <summary>
/// The output switcher (spec §3.11): a shortcut that moves to the next
/// selected output that is connected, through the "all apps" switch, and a
/// HUD naming the new device.
/// </summary>
public sealed class OutputSwitcherService : IDisposable
{
    private readonly AudioDeviceService _devices;
    private readonly ISettingsStore _settings;
    private readonly IHud? _hud;
    private readonly IDisposable _enabledSubscription;
    private bool _active;
    private bool _wasEnabled;
    private bool _seedPending;

    public OutputSwitcherService(AudioDeviceService devices, ISettingsStore settings, IHud? hud = null)
    {
        _devices = devices;
        _settings = settings;
        _hud = hud;
        _wasEnabled = settings.Get(FeatureKeys.SoundOutputSwitcherEnabled);
        _enabledSubscription = settings.Observe(() => UiThread.Run(OnEnabledChanged), FeatureKeys.SoundOutputSwitcherEnabled);
        _devices.Changed += OnDevicesChanged;
    }

    /// <summary>Raised on the UI thread when the selection changed.</summary>
    public event EventHandler? Changed;

    public bool IsEnabled => _settings.Get(FeatureKeys.SoundOutputSwitcherEnabled);

    /// <summary>Outputs in the cycle, in order (disconnected ones included).</summary>
    public IReadOnlyList<string> Selection => _settings.Get(SoundSettings.OutputSwitcherDeviceIds) ?? [];

    public bool IsSelected(string deviceId) => Selection.Contains(deviceId, StringComparer.Ordinal);

    public void SetActive(bool active)
    {
        _active = active;
        if (active && IsEnabled && Selection.Count == 0)
        {
            Seed();
        }
    }

    /// <summary>A checkbox of "Outputs in cycle" changed.</summary>
    public void SetSelected(string deviceId, bool selected)
    {
        var visible = _devices.Outputs.Select(d => d.Id).ToList();
        _settings.Set(SoundSettings.OutputSwitcherDeviceIds, OutputCycle.UpdateSelection(_settings.Get(SoundSettings.OutputSwitcherDeviceIds), visible, deviceId, selected));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The shortcut: switch to the next output and say which one.</summary>
    public async Task<OutputCycleResult> NextAsync()
    {
        var result = OutputCycle.Next(Selection, _devices.Outputs.Select(d => d.Id).ToList(), _devices.DefaultOutputId);
        switch (result.Outcome)
        {
            case OutputCycleOutcome.NoCandidates:
                _hud?.Show(L.Get("Strings.soundOutputSwitcherNoAvailableSelection"), HudStyle.Warning, "Speaker2");
                break;
            case OutputCycleOutcome.Unchanged:
                ShowDevice(result.Target);
                break;
            case OutputCycleOutcome.Switch:
                var status = await _devices.SwitchOutputForAllAppsAsync(result.Target!).ConfigureAwait(true);
                if (status == AudioSwitchStatus.Success)
                {
                    ShowDevice(result.Target);
                }
                else
                {
                    _hud?.Show(SwitchErrorText(status), HudStyle.Error, "Speaker2");
                    return new OutputCycleResult(OutputCycleOutcome.Failed, result.Target);
                }

                break;
        }

        return result;
    }

    /// <summary>"Output unavailable" or "Could not switch: …" for a failed switch.</summary>
    public static string SwitchErrorText(AudioSwitchStatus status) => status switch
    {
        AudioSwitchStatus.Unavailable => L.Get("Strings.mixerOutputUnavailable"),
        AudioSwitchStatus.Unsupported => L.Format("Strings.mixerSystemOutputErrorFormat", L.Get("win.sound.switchUnsupported")),
        _ => L.Format("Strings.mixerSystemOutputErrorFormat", L.Get("win.sound.switchRefused")),
    };

    private void ShowDevice(string? deviceId)
    {
        if (_devices.Outputs.FirstOrDefault(d => d.Id == deviceId) is { } device)
        {
            _hud?.Show(device.Name, HudStyle.Info, device.IsHeadphones ? "Headphones" : "Speaker2");
        }
    }

    /// <summary>Turning the switcher on with an empty selection seeds it with the current output.</summary>
    private void OnEnabledChanged()
    {
        var enabled = IsEnabled;
        if (enabled && !_wasEnabled && _active && Selection.Count == 0)
        {
            Seed();
        }

        _wasEnabled = enabled;
    }

    private void Seed()
    {
        if (_devices.DefaultOutputId is { } current)
        {
            _seedPending = false;
            _settings.Set(SoundSettings.OutputSwitcherDeviceIds, [current]);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _seedPending = true;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (_seedPending && _active && IsEnabled && Selection.Count == 0)
        {
            Seed();
        }
    }

    public void Dispose()
    {
        _enabledSubscription.Dispose();
        _devices.Changed -= OnDevicesChanged;
    }
}
