// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Sound;

/// <summary>
/// "Mute microphone" (spec §3.13.3): mutes every microphone, keeps new ones
/// muted while on, and unmutes only what it muted. The state survives quitting
/// and relaunching (an update restart must never silently reopen the
/// microphone); uninstalling the feature unmutes first.
/// </summary>
public sealed class MicMuteService : IDisposable
{
    private const string DemandOwner = "micMute";

    private readonly IAudioPlatform _platform;
    private readonly AudioDeviceService _devices;
    private readonly ISettingsStore _settings;
    private readonly IHud? _hud;
    private string? _lastSweptDevices;
    private bool? _intent;
    private bool _active;
    private bool _disposed;

    public MicMuteService(IAudioPlatform platform, AudioDeviceService devices, ISettingsStore settings, IHud? hud = null)
    {
        _platform = platform;
        _devices = devices;
        _settings = settings;
        _hud = hud;
        _devices.Changed += OnDevicesChanged;
    }

    /// <summary>Raised on the UI thread when muted/unmuted.</summary>
    public event EventHandler? Changed;

    public bool IsMuted => _settings.Get(SoundSettings.MicMuteActive);

    public bool IsActive => _active;

    /// <summary>The feature's controller: on install it re-asserts a persisted mute; on uninstall it unmutes first.</summary>
    public void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            _lastSweptDevices = null;
            UpdateDemand();
            if (IsMuted)
            {
                _ = SweepAsync(mute: true, feedback: false);
            }
        }
        else if (IsMuted || MicMuteRecord.Load(_settings).HasOutstandingClaims)
        {
            _intent = false;
            _ = UnmuteThenStopAsync();
        }
        else
        {
            _devices.SetDemand(DemandOwner, false);
        }
    }

    /// <summary>
    /// Mutes or unmutes. Two quick presses alternate even before the first
    /// sweep finished: the decision follows the last request, not the device state.
    /// </summary>
    public Task ToggleAsync() => (_intent ?? IsMuted) ? UnmuteAsync() : MuteAsync();

    public Task MuteAsync(bool feedback = true)
    {
        _intent = true;
        return SweepAsync(mute: true, feedback);
    }

    public Task UnmuteAsync(bool feedback = true)
    {
        _intent = false;
        return SweepAsync(mute: false, feedback);
    }

    private async Task UnmuteThenStopAsync()
    {
        await SweepAsync(mute: false, feedback: false).ConfigureAwait(true);
        UiThread.Run(() =>
        {
            if (!_active)
            {
                _devices.SetDemand(DemandOwner, false);
            }
        });
    }

    /// <summary>
    /// One sweep on the audio thread. The claim record is loaded, changed and
    /// saved inside that one work item, and so is the muted state, so sweeps
    /// apply strictly in order and two quick ones never start from the same record.
    /// </summary>
    private async Task SweepAsync(bool mute, bool feedback)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var (result, wasMuted) = await _platform.InvokeAsync(access =>
            {
                var record = MicMuteRecord.Load(_settings);
                var sweep = mute ? MicMuteEngine.Mute(access, record) : MicMuteEngine.Unmute(access, record);
                if (!sweep.Record.SameAs(record))
                {
                    sweep.Record.Save(_settings);
                }

                var before = _settings.Get(SoundSettings.MicMuteActive);
                _settings.Set(SoundSettings.MicMuteActive, mute);
                return (sweep, before);
            }).ConfigureAwait(false);

            UiThread.Run(() =>
            {
                UpdateDemand();
                if (feedback)
                {
                    ShowFeedback(mute, result.Partial);
                }
                else if (result.Partial && mute)
                {
                    Log.Warn("sound", "Mute microphone: a newly connected microphone could not be muted.");
                }

                if (wasMuted != mute || feedback)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            });
        }
        catch (Exception ex)
        {
            _intent = null;
            Log.Error("sound", "Mute microphone failed.", ex);
        }
    }

    private void ShowFeedback(bool muted, bool partial)
    {
        if (_hud is null)
        {
            return;
        }

        if (partial)
        {
            _hud.Show(L.Get(muted ? "Strings.micMutePartialHUD" : "Strings.micUnmutePartialHUD"), HudStyle.Warning, "MicOff");
        }
        else
        {
            _hud.Show(L.Get(muted ? "Strings.micMutedHUD" : "Strings.micUnmutedHUD"), HudStyle.Info, muted ? "MicOff" : "Mic");
        }
    }

    /// <summary>Listen to the devices while muted, or while claims are outstanding (to release returning devices).</summary>
    private void UpdateDemand()
    {
        if (!_active)
        {
            return;
        }

        var needed = IsMuted || MicMuteRecord.Load(_settings).HasOutstandingClaims;
        _devices.SetDemand(DemandOwner, needed);
    }

    /// <summary>
    /// A microphone connected, changed state or became the default: while
    /// muted it is muted on arrival; with claims outstanding, returning
    /// devices are given back.
    /// </summary>
    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (!_active || !_devices.IsLive || !_devices.HasDemand(DemandOwner))
        {
            return;
        }

        var key = string.Join(',', _devices.Snapshot.Inputs.Select(d => d.Id)) + "|" + _devices.DefaultInputId;
        if (key == _lastSweptDevices)
        {
            return;
        }

        _lastSweptDevices = key;
        // Follow the latest request: a press that is still being applied must win.
        if (_intent ?? IsMuted)
        {
            _ = SweepAsync(mute: true, feedback: false);
        }
        else if (MicMuteRecord.Load(_settings).HasOutstandingClaims)
        {
            _ = SweepAsync(mute: false, feedback: false);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _devices.Changed -= OnDevicesChanged;
    }
}
