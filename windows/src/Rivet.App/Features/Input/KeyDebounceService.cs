// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Input;

/// <summary>
/// Key debounce (spec 07 §3.7.2) on the shared keyboard hook. Modifiers and
/// lock keys are excluded explicitly (Windows reports them as ordinary keys),
/// key-ups always pass, and auto-repeat is inferred from the keyboard repeat
/// delay because low-level hooks carry no repeat flag.
/// </summary>
public sealed class KeyDebounceService : InputFeatureService
{
    private readonly IKeyboardInfo _keyboard;
    private volatile Config _config = new(InputSettings.DefaultKeyWindowMs, new Dictionary<int, int>(), 200 * InputTime.NsPerMs);

    public KeyDebounceService(ISettingsStore settings, IInputHooks hooks, IInputClock clock, IKeyboardInfo keyboard, InputFixesControl? control = null)
        : base(settings, hooks, clock, control, suspendable: true)
    {
        _keyboard = keyboard;
        Observe(InputSettings.KeyDebounceWindowMs, InputSettings.KeyDebounceKeyWindows);
    }

    /// <summary>
    /// Down-while-down at or after 80 % of the repeat delay (at least 150 ms) is
    /// auto-repeat. Bounces arrive within a few milliseconds, far below that.
    /// </summary>
    public static long RepeatThresholdNs(int repeatDelayMs) =>
        Math.Max(150, (long)(Math.Max(repeatDelayMs, 0) * 0.8)) * InputTime.NsPerMs;

    protected override bool WantsRunning() => Settings.Get(FeatureKeys.KeyboardDebounceEnabled);

    protected override void OnConfigure() => _config = new Config(
        Settings.Get(InputSettings.KeyDebounceWindowMs),
        KeyDebounceOverrides.Decode(Settings.Get(InputSettings.KeyDebounceKeyWindows)),
        RepeatThresholdNs(_keyboard.RepeatDelayMs));

    protected override IDisposable StartSession()
    {
        var filter = new KeyDebounceFilter();
        Config? applied = null;
        return Hooks.SubscribeKeyboard((ref KeyboardHookEvent e) =>
        {
            var config = _config;
            if (!ReferenceEquals(config, applied))
            {
                filter.GlobalWindowMs = config.GlobalWindowMs;
                filter.RepeatThresholdNs = config.RepeatThresholdNs;
                filter.SetOverrides(config.Overrides);
                applied = config;
            }

            var id = KeyDebounceFilter.KeyIdFor(e.VirtualKey, e.ScanCode, e.IsExtended);
            return id >= 0 && filter.OnKey(id, e.Action == KeyAction.Down, Clock.NowNs);
        }, InputPriorities.KeyDebounce);
    }

    private sealed record Config(int GlobalWindowMs, IReadOnlyDictionary<int, int> Overrides, long RepeatThresholdNs);
}
