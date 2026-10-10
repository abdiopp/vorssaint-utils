// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Util;

namespace Rivet.Core.Sound;

/// <summary>
/// "Use finer volume steps" (spec §3.15, Windows mapping): the shared
/// low-level keyboard hook takes the volume keys (and the volume knobs that
/// arrive as those keys), swallows them and steps the default output by a
/// small amount instead of Windows' 2 %. A press with Ctrl, Alt or Win held
/// goes to Windows unchanged. Swallowing the keys also hides Windows' own
/// volume indicator, so a HUD shows the new level.
/// </summary>
public sealed class PreciseVolumeService : IDisposable
{
    public const int VolumeMuteKey = 0xAD;
    public const int VolumeDownKey = 0xAE;
    public const int VolumeUpKey = 0xAF;

    private readonly IInputHooks _hooks;
    private readonly AudioDeviceService _devices;
    private readonly ISettingsStore _settings;
    private readonly IHud? _hud;
    private readonly IDisposable _subscription;
    private readonly PreciseVolumeGate _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IDisposable? _hook;
    private int _passThroughKey;
    private bool _mixerInstalled;

    public PreciseVolumeService(IInputHooks hooks, AudioDeviceService devices, ISettingsStore settings, IHud? hud = null)
    {
        _hooks = hooks;
        _devices = devices;
        _settings = settings;
        _hud = hud;
        _subscription = settings.Observe(() => UiThread.Run(Sync), SoundSettings.PreciseVolumeRollerEnabled);
    }

    /// <summary>The hook is installed (enabled and the mixer is installed).</summary>
    public bool IsRunning => _hook is not null;

    /// <summary>The mixer feature's controller.</summary>
    public void SetMixerInstalled(bool installed)
    {
        _mixerInstalled = installed;
        Sync();
    }

    private void Sync()
    {
        var wanted = _mixerInstalled && _settings.Get(SoundSettings.PreciseVolumeRollerEnabled);
        if (wanted && _hook is null)
        {
            _passThroughKey = 0;
            _hook = _hooks.SubscribeKeyboard(OnKey, priority: 50);
        }
        else if (!wanted && _hook is not null)
        {
            _hook.Dispose();
            _hook = null;
        }
    }

    /// <summary>Runs on the hook thread: decide in microseconds, post the work.</summary>
    private bool OnKey(ref KeyboardHookEvent e)
    {
        if (e.VirtualKey is not (VolumeUpKey or VolumeDownKey))
        {
            return false;
        }

        if (e.Action == KeyAction.Up)
        {
            if (_passThroughKey == e.VirtualKey)
            {
                _passThroughKey = 0;
                return false;
            }

            return true;
        }

        if ((e.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0)
        {
            // Left to Windows, with its repeats.
            _passThroughKey = e.VirtualKey;
            return false;
        }

        if (_passThroughKey == e.VirtualKey)
        {
            return false;
        }

        var direction = e.VirtualKey == VolumeUpKey ? 1 : -1;
        if (_gate.Accept(direction, _clock.ElapsedMilliseconds))
        {
            UiThread.Post(() => _ = StepAsync(direction));
        }

        return true;
    }

    private async Task StepAsync(int direction)
    {
        var step = _settings.Get(SoundSettings.PreciseVolumeRollerStepPercent) / 100.0;
        var level = await _devices.StepOutputVolumeAsync(direction * step).ConfigureAwait(true);
        if (level is { } value && _hud is not null)
        {
            _hud.Show(L.Format("win.sound.volumeHudFormat", VolumeMath.FormatPercent(value, Localizer.Current.Culture)),
                HudStyle.Info, value <= 0 ? "SpeakerMute" : "Speaker2", TimeSpan.FromSeconds(1.2));
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _hook?.Dispose();
        _hook = null;
    }
}
