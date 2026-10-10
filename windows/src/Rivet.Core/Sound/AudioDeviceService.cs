// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Sound;

public enum AudioSwitchStatus
{
    Success,

    /// <summary>The device is not connected ("Output unavailable").</summary>
    Unavailable,

    /// <summary>This PC cannot change the default device.</summary>
    Unsupported,

    /// <summary>Windows refused the change.</summary>
    Failed,
}

/// <summary>
/// The shared device layer (spec §1.2, §3.10): device lists, defaults,
/// output and input volume, the "all apps" switch, the preferred microphone
/// and the headphone guard. It runs while any sound feature needs it (each
/// owner declares its demand) and publishes on the UI thread.
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    private static readonly TimeSpan OverlayHold = TimeSpan.FromSeconds(1.5);

    private readonly IAudioPlatform _platform;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (bool Devices, bool Sessions)> _demand = new(StringComparer.Ordinal);
    private readonly HeadphoneGuard _guard = new();
    private readonly LatestValueWriter<VolumeWrite> _outputWriter;
    private readonly LatestValueWriter<VolumeWrite> _inputWriter;
    private AudioSnapshot _snapshot = AudioSnapshot.Empty;
    private IReadOnlyList<AudioDevice> _outputs = [];
    private IReadOnlyList<AudioDevice> _inputs = [];
    private VolumeOverlay? _outputOverlay;
    private VolumeOverlay? _inputOverlay;
    private volatile string? _defaultOutput;
    private volatile string? _defaultInput;
    private bool _mixerActive;
    private bool _priorityOwnsInput;
    private bool _guardBusy;
    private bool _preferredBusy;
    private string? _preferredFailureKey;
    private bool _disposed;

    public AudioDeviceService(IAudioPlatform platform, ISettingsStore settings, TimeProvider? time = null)
    {
        _platform = platform;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _outputWriter = new LatestValueWriter<VolumeWrite>(WriteVolumeAsync);
        _inputWriter = new LatestValueWriter<VolumeWrite>(WriteVolumeAsync);
        _platform.SnapshotChanged += OnPlatformSnapshotChanged;
        _settings.Observe(() => UiThread.Run(EvaluatePreferredInput), SoundSettings.PreferredInputDevice);
    }

    /// <summary>Raised on the UI thread after the devices, defaults or volumes changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread after a successful "all apps" switch (per-app routes were cleared).</summary>
    public event EventHandler? UniversalSwitched;

    public IAudioPlatform Platform => _platform;

    public AudioCapabilities Capabilities => _platform.Capabilities;

    /// <summary>The device layer is observing and has published at least once.</summary>
    public bool IsLive => _snapshot.IsLive;

    public AudioSnapshot Snapshot => _snapshot;

    /// <summary>Outputs: default first, then by name, then by id.</summary>
    public IReadOnlyList<AudioDevice> Outputs => _outputs;

    /// <summary>Inputs: default first, then by name, then by id.</summary>
    public IReadOnlyList<AudioDevice> Inputs => _inputs;

    public string? DefaultOutputId => _snapshot.DefaultOutputId;

    public string? DefaultInputId => _snapshot.DefaultInputId;

    public AudioDevice? DefaultOutput => _snapshot.Find(_snapshot.DefaultOutputId);

    public AudioDevice? DefaultInput => _snapshot.Find(_snapshot.DefaultInputId);

    /// <summary>Volume of the default output, with a pending slider value shown immediately.</summary>
    public EndpointVolume? OutputVolume => Overlaid(_snapshot.VolumeOf(DefaultOutputId), _outputOverlay, DefaultOutputId, _outputWriter);

    /// <summary>Gain of the default input, with a pending slider value shown immediately.</summary>
    public EndpointVolume? InputVolume => Overlaid(_snapshot.VolumeOf(DefaultInputId), _inputOverlay, DefaultInputId, _inputWriter);

    /// <summary>
    /// The mixer is installed: the headphone guard and the preferred
    /// microphone apply only then. Turning it off gives back what they changed.
    /// </summary>
    public void SetMixerActive(bool active)
    {
        if (_mixerActive == active)
        {
            return;
        }

        _mixerActive = active;
        if (active)
        {
            _guard.Reset();
            EvaluatePreferredInput();
        }
        else
        {
            _ = RestoreAsync(restoreInput: !_priorityOwnsInput);
        }
    }

    /// <summary>
    /// Audio priority's microphone half is on: it owns the system input, the
    /// preferred microphone stays dormant, and quitting keeps its pick.
    /// </summary>
    public bool PriorityOwnsInput
    {
        get => _priorityOwnsInput;
        set
        {
            if (_priorityOwnsInput == value)
            {
                return;
            }

            _priorityOwnsInput = value;
            if (!value)
            {
                EvaluatePreferredInput();
            }
        }
    }

    /// <summary>Declares what one owner needs; observation runs while anyone needs it.</summary>
    public void SetDemand(string owner, bool devices, bool sessions = false)
    {
        if (devices || sessions)
        {
            _demand[owner] = (devices || sessions, sessions);
        }
        else
        {
            _demand.Remove(owner);
        }

        _platform.SetObservation(_demand.Values.Any(d => d.Devices), _demand.Values.Any(d => d.Sessions));
        if (_demand.Count == 0)
        {
            Publish(AudioSnapshot.Empty);
        }
    }

    public bool HasDemand(string owner) => _demand.ContainsKey(owner);

    // ── Volume ──────────────────────────────────────────────────────────

    /// <summary>Sets the default output's level (coalesced). 0 also mutes; any positive level unmutes.</summary>
    public void SetOutputVolume(double level) => SetVolume(AudioFlow.Render, level);

    /// <summary>Sets the default input's gain (coalesced).</summary>
    public void SetInputVolume(double level) => SetVolume(AudioFlow.Capture, level);

    /// <summary>
    /// Steps the default output by <paramref name="delta"/> after reading its
    /// real level (a published value can be stale after sleep). Returns the new
    /// level, or null when there is no adjustable output.
    /// </summary>
    public async Task<double?> StepOutputVolumeAsync(double delta)
    {
        var id = _defaultOutput;
        if (id is null)
        {
            return null;
        }

        var level = await RunAsync(access =>
        {
            var current = access.ReadVolume(id);
            if (current is null || !current.CanSetVolume)
            {
                return (double?)null;
            }

            var next = VolumeMath.Clamp01(Math.Round((current.Scalar + delta) * 1000) / 1000);
            ApplyVolume(access, id, next, AudioFlow.Render);
            return next;
        }, null).ConfigureAwait(true);

        if (level is { } value)
        {
            UiThread.Run(() =>
            {
                _outputOverlay = new VolumeOverlay(id, value, _time.GetTimestamp());
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        return level;
    }

    public Task SetOutputMuteAsync(bool muted)
    {
        var id = _defaultOutput;
        return id is null ? Task.CompletedTask : RunAsync(access => access.TrySetMute(id, muted), false);
    }

    public Task SetInputMuteAsync(bool muted)
    {
        var id = _defaultInput;
        return id is null ? Task.CompletedTask : RunAsync(access => access.TrySetMute(id, muted), false);
    }

    // ── Default devices ─────────────────────────────────────────────────

    /// <summary>
    /// The "all apps" switch (spec §3.10.2): makes <paramref name="deviceId"/>
    /// the default for every role, then clears every per-app route (volumes
    /// kept), remembers the choice and publishes the new default at once so a
    /// second shortcut press cycles from it.
    /// </summary>
    public async Task<AudioSwitchStatus> SwitchOutputForAllAppsAsync(string deviceId)
    {
        if (!_outputs.Any(d => d.Id == deviceId))
        {
            return AudioSwitchStatus.Unavailable;
        }

        if (!_platform.Capabilities.CanSetDefaultDevice)
        {
            return AudioSwitchStatus.Unsupported;
        }

        var ok = await RunAsync(access => access.TrySetDefaultDevice(deviceId, AudioFlow.Render), false).ConfigureAwait(true);
        if (!ok)
        {
            return AudioSwitchStatus.Failed;
        }

        UiThread.Run(() =>
        {
            _settings.Reset(SoundSettings.AppOutputDevices.Key);
            _settings.Set(SoundSettings.UniversalOutputDevice, deviceId);
            if (_platform.Capabilities.CanRouteApps)
            {
                _ = _platform.ClearAppOutputsAsync();
            }

            if (_snapshot.IsLive && _snapshot.DefaultOutputId != deviceId)
            {
                Publish(_snapshot with { DefaultOutputId = deviceId });
            }

            UniversalSwitched?.Invoke(this, EventArgs.Empty);
        });
        return AudioSwitchStatus.Success;
    }

    /// <summary>Audio priority's lighter path: sets only the default, never touching per-app routes.</summary>
    public async Task<bool> SetDefaultDeviceAsync(string deviceId, AudioFlow flow)
    {
        if (!_platform.Capabilities.CanSetDefaultDevice)
        {
            return false;
        }

        var ok = await RunAsync(access => access.TrySetDefaultDevice(deviceId, flow), false).ConfigureAwait(true);
        if (ok)
        {
            UiThread.Run(() =>
            {
                if (_snapshot.IsLive && _snapshot.DefaultId(flow) != deviceId)
                {
                    Publish(flow == AudioFlow.Render ? _snapshot with { DefaultOutputId = deviceId } : _snapshot with { DefaultInputId = deviceId });
                }
            });
        }

        return ok;
    }

    /// <summary>
    /// The microphone picker (spec §3.13.1). With audio priority owning the
    /// input, it just sets the system input. Otherwise it saves the preference
    /// (null = "Default", no preference) and applies it.
    /// </summary>
    public async Task<AudioSwitchStatus> ChooseInputAsync(string? deviceId)
    {
        if (_priorityOwnsInput)
        {
            return deviceId is null ? AudioSwitchStatus.Success
                : await SetDefaultDeviceAsync(deviceId, AudioFlow.Capture).ConfigureAwait(true) ? AudioSwitchStatus.Success : AudioSwitchStatus.Failed;
        }

        if (deviceId is null)
        {
            var applied = PreferredInputId;
            _settings.Reset(SoundSettings.PreferredInputDevice.Key);
            await RestoreOriginalInputAsync(applied).ConfigureAwait(true);
            return AudioSwitchStatus.Success;
        }

        if (!_inputs.Any(d => d.Id == deviceId))
        {
            return AudioSwitchStatus.Unavailable;
        }

        _preferredFailureKey = null;
        RememberOriginalInput(deviceId);
        _settings.Set(SoundSettings.PreferredInputDevice, deviceId);
        if (_snapshot.DefaultInputId == deviceId)
        {
            return AudioSwitchStatus.Success;
        }

        return await SetDefaultDeviceAsync(deviceId, AudioFlow.Capture).ConfigureAwait(true) ? AudioSwitchStatus.Success : AudioSwitchStatus.Failed;
    }

    /// <summary>The saved preferred microphone, when one is set.</summary>
    public string? PreferredInputId
    {
        get
        {
            var value = _settings.Get(SoundSettings.PreferredInputDevice);
            return value.Length == 0 ? null : value;
        }
    }

    /// <summary>Runs platform work; a broken audio system or a call racing shutdown yields <paramref name="fallback"/>.</summary>
    private async Task<T> RunAsync<T>(Func<IAudioEndpointAccess, T> work, T fallback)
    {
        try
        {
            return await _platform.InvokeAsync(work).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "An audio device operation failed.", ex);
            return fallback;
        }
    }

    // ── Snapshot pipeline ───────────────────────────────────────────────

    private void OnPlatformSnapshotChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            UiThread.Post(() =>
            {
                if (!_disposed && _demand.Count > 0)
                {
                    Publish(_platform.Snapshot);
                }
            });
        }
    }

    private void Publish(AudioSnapshot snapshot)
    {
        var previousDefault = _snapshot.DefaultOutputId;
        _snapshot = snapshot;
        _defaultOutput = snapshot.DefaultOutputId;
        _defaultInput = snapshot.DefaultInputId;
        _outputs = Sort(snapshot.Outputs, snapshot.DefaultOutputId);
        _inputs = Sort(snapshot.Inputs, snapshot.DefaultInputId);
        if (previousDefault != snapshot.DefaultOutputId)
        {
            _outputWriter.DropPending();
        }

        if (_mixerActive)
        {
            RunHeadphoneGuard(snapshot);
            EvaluatePreferredInput();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyList<AudioDevice> Sort(IReadOnlyList<AudioDevice> devices, string? defaultId) =>
        devices
            .OrderBy(d => d.Id == defaultId ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

    // ── Volume writes ───────────────────────────────────────────────────

    private void SetVolume(AudioFlow flow, double level)
    {
        var id = flow == AudioFlow.Render ? DefaultOutputId : DefaultInputId;
        if (id is null)
        {
            return;
        }

        level = VolumeMath.Clamp01(level);
        var overlay = new VolumeOverlay(id, level, _time.GetTimestamp());
        if (flow == AudioFlow.Render)
        {
            _outputOverlay = overlay;
            _outputWriter.Submit(new VolumeWrite(id, level, flow));
        }
        else
        {
            _inputOverlay = overlay;
            _inputWriter.Submit(new VolumeWrite(id, level, flow));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Task WriteVolumeAsync(VolumeWrite write)
    {
        // A write meant for an output that is no longer the default is dropped.
        var current = write.Flow == AudioFlow.Render ? _defaultOutput : _defaultInput;
        if (current != write.DeviceId)
        {
            return Task.CompletedTask;
        }

        return RunAsync(access =>
        {
            ApplyVolume(access, write.DeviceId, write.Level, write.Flow);
            return true;
        }, false);
    }

    /// <summary>Level 0 also mutes; a positive level unmutes (asking for sound means asking for sound).</summary>
    private static void ApplyVolume(IAudioEndpointAccess access, string id, double level, AudioFlow flow)
    {
        access.TrySetVolume(id, (float)level);
        if (flow != AudioFlow.Render)
        {
            return;
        }

        var state = access.ReadVolume(id);
        if (state is null)
        {
            return;
        }

        if (level <= 0 && !state.Muted)
        {
            access.TrySetMute(id, true);
        }
        else if (level > 0 && state.Muted)
        {
            access.TrySetMute(id, false);
        }
    }

    private EndpointVolume? Overlaid(EndpointVolume? actual, VolumeOverlay? overlay, string? id, LatestValueWriter<VolumeWrite> writer)
    {
        if (actual is null || overlay is null || overlay.DeviceId != id)
        {
            return actual;
        }

        var fresh = writer.IsBusy || _time.GetElapsedTime(overlay.Ticks) < OverlayHold;
        return fresh ? actual with { Scalar = (float)overlay.Level, Muted = actual.Muted && overlay.Level <= 0 } : actual;
    }

    // ── Headphone guard ─────────────────────────────────────────────────

    private void RunHeadphoneGuard(AudioSnapshot snapshot)
    {
        if (_guardBusy)
        {
            return;
        }

        var enabled = _settings.Get(SoundSettings.LowerVolumeOnHeadphonesDisconnect);
        var level = _settings.Get(SoundSettings.HeadphonesDisconnectVolumePercent) / 100.0;
        var pending = _settings.Get(SoundSettings.HeadphoneGuardPending);
        var step = _guard.Observe(snapshot, enabled, level, pending);
        switch (step.Kind)
        {
            case HeadphoneGuardStepKind.Lower:
                _guard.Lowered(step.DeviceId!);
                _ = LowerAsync(step.DeviceId!, step.Level);
                break;
            case HeadphoneGuardStepKind.Restore when pending is not null:
                _ = RestoreGuardAsync(pending);
                break;
        }
    }

    private async Task LowerAsync(string deviceId, double level)
    {
        _guardBusy = true;
        try
        {
            var record = await _platform.InvokeAsync(access => HeadphoneGuard.ApplyLower(access, deviceId, level)).ConfigureAwait(true);
            if (record is not null)
            {
                UiThread.Run(() => _settings.Set(SoundSettings.HeadphoneGuardPending, record));
                Log.Info("sound", $"Headphones disconnected: lowered the output to {level:P0}.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Headphone guard could not lower the volume.", ex);
        }
        finally
        {
            _guardBusy = false;
        }
    }

    private async Task RestoreGuardAsync(HeadphoneGuardRestore record)
    {
        _guardBusy = true;
        try
        {
            var settled = await _platform.InvokeAsync(access => HeadphoneGuard.ApplyRestore(access, record)).ConfigureAwait(true);
            if (settled)
            {
                UiThread.Run(() =>
                {
                    if (_settings.Get(SoundSettings.HeadphoneGuardPending) == record)
                    {
                        _settings.Reset(SoundSettings.HeadphoneGuardPending.Key);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Headphone guard could not restore the volume.", ex);
        }
        finally
        {
            _guardBusy = false;
        }
    }

    // ── Preferred microphone ────────────────────────────────────────────

    /// <summary>
    /// Whenever the preferred microphone is connected and not current
    /// (reconnect, Windows switched away), it is applied again. A failed
    /// attempt is not retried until the devices change.
    /// </summary>
    private void EvaluatePreferredInput()
    {
        if (!_mixerActive || _priorityOwnsInput || _preferredBusy || !_snapshot.IsLive)
        {
            return;
        }

        var preferred = PreferredInputId;
        if (preferred is null || _snapshot.DefaultInputId == preferred || !_snapshot.Inputs.Any(d => d.Id == preferred))
        {
            return;
        }

        var key = preferred + "|" + string.Join(',', _snapshot.Inputs.Select(d => d.Id)) + "|" + _snapshot.DefaultInputId;
        if (key == _preferredFailureKey)
        {
            return;
        }

        RememberOriginalInput(preferred);
        _ = ApplyPreferredAsync(preferred, key);
    }

    private async Task ApplyPreferredAsync(string preferred, string key)
    {
        _preferredBusy = true;
        try
        {
            if (!await SetDefaultDeviceAsync(preferred, AudioFlow.Capture).ConfigureAwait(true))
            {
                _preferredFailureKey = key;
            }
        }
        catch (Exception ex)
        {
            _preferredFailureKey = key;
            Log.Warn("sound", "Could not apply the preferred microphone.", ex);
        }
        finally
        {
            _preferredBusy = false;
        }
    }

    /// <summary>The system's original input is remembered the first time the app overrides it.</summary>
    private void RememberOriginalInput(string preferred)
    {
        var current = _snapshot.DefaultInputId;
        if (_settings.Get(SoundSettings.PreferredInputOriginalDevice).Length == 0 && current is not null && current != preferred)
        {
            _settings.Set(SoundSettings.PreferredInputOriginalDevice, current);
        }
    }

    /// <summary>
    /// Gives the original input back only while the app still owns the
    /// override: the current input is the preferred one, the original differs
    /// and is connected.
    /// </summary>
    private async Task RestoreOriginalInputAsync(string? applied)
    {
        var original = _settings.Get(SoundSettings.PreferredInputOriginalDevice);
        if (original.Length == 0)
        {
            return;
        }

        if (applied is not null)
        {
            await _platform.InvokeAsync(access =>
            {
                var current = access.DefaultDeviceId(AudioFlow.Capture);
                if (current == applied && current != original && access.ActiveDevices(AudioFlow.Capture).Any(d => d.Id == original))
                {
                    access.TrySetDefaultDevice(original, AudioFlow.Capture);
                }

                return true;
            }).ConfigureAwait(false);
        }

        _settings.Reset(SoundSettings.PreferredInputOriginalDevice.Key);
    }

    // ── Restore and shutdown ────────────────────────────────────────────

    /// <summary>Gives back the headphone guard's level and (optionally) the original input.</summary>
    private async Task RestoreAsync(bool restoreInput)
    {
        try
        {
            if (_settings.Get(SoundSettings.HeadphoneGuardPending) is { } record)
            {
                var settled = await _platform.InvokeAsync(access =>
                    access.ActiveDevices(AudioFlow.Render).Any(d => d.Id == record.DeviceId) && HeadphoneGuard.ApplyRestore(access, record)).ConfigureAwait(false);
                if (settled)
                {
                    _settings.Reset(SoundSettings.HeadphoneGuardPending.Key);
                }
            }

            if (restoreInput)
            {
                await RestoreOriginalInputAsync(PreferredInputId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Could not restore the audio devices.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _platform.SnapshotChanged -= OnPlatformSnapshotChanged;
        if (_mixerActive)
        {
            // Quitting: give back what the guard and the preferred microphone
            // changed, without the UI thread (its loop has already stopped).
            var restoreInput = !_priorityOwnsInput;
            try
            {
                Task.Run(() => RestoreAsync(restoreInput)).Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException ex)
            {
                Log.Warn("sound", "Restoring audio devices at quit failed.", ex);
            }
        }

        _platform.SetObservation(false, false);
    }

    private sealed record VolumeWrite(string DeviceId, double Level, AudioFlow Flow);

    private sealed record VolumeOverlay(string DeviceId, double Level, long Ticks);
}
