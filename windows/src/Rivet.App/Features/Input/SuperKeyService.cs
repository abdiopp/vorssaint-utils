// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Input;

public enum SuperKeyStatus
{
    Off,
    Working,

    /// <summary>An app from the list is running; the key works normally meanwhile.</summary>
    Paused,
}

/// <summary>
/// The Super key (spec 07 §3.7.6, Windows redesign). Windows key messages
/// carry no modifier flags, so the held source key presses real modifiers:
/// they go down (left-hand variants, tagged) when the source goes down and up
/// when it goes up ("eager" injection, which also makes Ctrl/Alt+click chords
/// work). A lone press runs the tap action after the modifiers are released;
/// a mask key keeps that lone release from opening Start, activating a menu
/// bar, switching the input language or toggling an IME. Source repeats are
/// swallowed and ignored. Modifiers are never left down: they are released on
/// the source's release, when the feature stops or pauses, and by a watchdog
/// that checks the physical key state (Raw Input) every few seconds.
/// </summary>
public sealed class SuperKeyService : InputFeatureService
{
    /// <summary>The fake Left Ctrl Windows sends before Right Alt on AltGr layouts.</summary>
    public const int AltGrFakeControlScan = 0x21D;

    private readonly IKeyboardInfo _keyboard;
    private readonly IRunningAppsMonitor _running;
    private volatile Config _config = Config.Default;
    private IDisposable? _runningWatch;
    private volatile bool _paused;
    private volatile Session? _current;

    public SuperKeyService(ISettingsStore settings, IInputHooks hooks, IInputClock clock, IKeyboardInfo keyboard, IRunningAppsMonitor running)
        : base(settings, hooks, clock)
    {
        _keyboard = keyboard;
        _running = running;
        Observe(InputSettings.SuperKeySource, InputSettings.SuperKeyModifiers, InputSettings.SuperKeySoloAction, InputSettings.SuperKeyExceptions);
    }

    public SuperKeyStatus Status =>
        IsRunning ? SuperKeyStatus.Working : _paused && IsAvailable && Settings.Get(FeatureKeys.SuperKeyEnabled) ? SuperKeyStatus.Paused : SuperKeyStatus.Off;

    public override void Dispose()
    {
        base.Dispose();
        Interlocked.Exchange(ref _runningWatch, null)?.Dispose();
    }

    protected override bool WantsRunning() => Settings.Get(FeatureKeys.SuperKeyEnabled) && !_paused;

    protected override void OnConfigure()
    {
        var source = Settings.Get(InputSettings.SuperKeySource);
        var config = new Config(
            source,
            SuperKeySources.VirtualKey(source),
            SuperKeyModifierSet.Parse(Settings.Get(InputSettings.SuperKeyModifiers)),
            Settings.Get(InputSettings.SuperKeySoloAction),
            new AppExclusionList(Settings.Get(InputSettings.SuperKeyExceptions)));
        _config = config;

        var watch = IsAvailable && Settings.Get(FeatureKeys.SuperKeyEnabled) && !config.Exceptions.IsEmpty;
        if (watch && _runningWatch is null)
        {
            _runningWatch = _running.Watch(Refresh);
        }
        else if (!watch && _runningWatch is not null)
        {
            _runningWatch.Dispose();
            _runningWatch = null;
        }

        var paused = watch && _running.IsAnyRunning(config.Exceptions);
        if (paused != _paused)
        {
            _paused = paused;
            RaiseStatusChanged();
        }
    }

    protected override IDisposable StartSession()
    {
        var config = _config;
        var session = new Session(config);
        if (config.SourceVk == VirtualKeys.Capital && _keyboard.IsCapsLockOn)
        {
            // Starting on Caps Lock forces capitals off (a tagged toggle).
            Hooks.SendKeys(ChordStrokes.Tap(VirtualKeys.Capital));
        }

        _current = session;
        var keyboard = Hooks.SubscribeKeyboard((ref KeyboardHookEvent e) => OnKey(session, ref e), InputPriorities.SuperKey);
        var mouse = Hooks.SubscribeMouse((ref MouseHookEvent e) => OnMouse(session, ref e), InputPriorities.SuperKey);
        return new SessionHandle(session, new CompositeDisposable(_keyboard.TrackPhysicalKeys(), keyboard, mouse));
    }

    protected override void StopSession(IDisposable session)
    {
        session.Dispose();
        if (session is SessionHandle { Session: var state })
        {
            if (ReferenceEquals(_current, state))
            {
                _current = null;
            }

            lock (state)
            {
                state.Watchdog?.Dispose();
                state.Watchdog = null;
                if (state.State.IsHeld || state.Injected.Count > 0)
                {
                    ReleaseModifiers(state, mask: true);
                }

                state.State.Reset();
            }
        }
    }

    /// <summary>Runs the held-key watchdog now (tests; the timer runs it every few seconds).</summary>
    internal void RunWatchdogNow()
    {
        if (_current is { } session)
        {
            Watch(session);
        }
    }

    private bool OnKey(Session s, ref KeyboardHookEvent e)
    {
        var vk = e.VirtualKey;
        var down = e.Action == KeyAction.Down;
        lock (s)
        {
            if (!s.State.IsHeld)
            {
                // Settings changes apply from the next press; a hold keeps the configuration it started with.
                s.Config = _config;
            }

            if (vk == s.Config.SourceVk)
            {
                if (down)
                {
                    var otherModifiers = (e.Modifiers & ~ChordStrokes.ModifierOf(vk)) != KeyModifiers.None;
                    if (s.State.OnTriggerDown(otherModifiers, Clock.NowNs))
                    {
                        PressModifiers(s);
                        ArmWatchdog(s);
                    }

                    return true;
                }

                if (!s.State.IsHeld)
                {
                    // The watchdog already let go of this press: keep its release quiet too.
                    return true;
                }

                var release = s.State.OnTriggerUp(Clock.NowNs);
                s.Watchdog?.Dispose();
                s.Watchdog = null;
                ReleaseModifiers(s, mask: release != SuperKeyRelease.Chord);
                Run(SuperKeyState.Effect(s.Config.SoloAction, release, didRepeat: false));
                return true;
            }

            if (s.Config.SourceVk == VirtualKeys.RMenu && e.ScanCode == AltGrFakeControlScan)
            {
                return true;
            }

            if (ChordStrokes.ModifierOf(vk) != KeyModifiers.None)
            {
                s.Physical[vk & 0xFF] = down;
                if (s.State.IsHeld)
                {
                    s.State.OnOtherInput();
                    if (!down && s.Injected.Contains(vk))
                    {
                        // Keep the modifier the Super key holds down until the Super key itself goes up.
                        return true;
                    }
                }

                return false;
            }

            s.State.OnOtherInput();
            return false;
        }
    }

    private static bool OnMouse(Session s, ref MouseHookEvent e)
    {
        if (e.Kind is MouseHookKind.LeftDown or MouseHookKind.RightDown or MouseHookKind.MiddleDown or MouseHookKind.XDown)
        {
            lock (s)
            {
                s.State.OnOtherInput();
            }
        }

        return false;
    }

    private void PressModifiers(Session s)
    {
        var strokes = new List<(int, KeyAction)>(4);
        foreach (var vk in ChordStrokes.LeftKeys(s.Config.Modifiers))
        {
            var modifier = ChordStrokes.ModifierOf(vk);
            if (IsHeldEitherSide(modifier))
            {
                continue;
            }

            strokes.Add((vk, KeyAction.Down));
            s.Injected.Add(vk);
        }

        if (strokes.Count > 0)
        {
            Hooks.SendKeys(strokes);
        }
    }

    private void ReleaseModifiers(Session s, bool mask)
    {
        if (s.Injected.Count == 0)
        {
            return;
        }

        var strokes = new List<(int, KeyAction)>(8);
        var needsMask = mask || s.Injected.Any(vk => ChordStrokes.ModifierOf(vk) is KeyModifiers.Alt or KeyModifiers.Win);
        if (needsMask)
        {
            strokes.AddRange(ChordStrokes.Mask);
        }

        for (var i = s.Injected.Count - 1; i >= 0; i--)
        {
            var vk = s.Injected[i];
            if (!s.Physical[vk & 0xFF])
            {
                strokes.Add((vk, KeyAction.Up));
            }
        }

        s.Injected.Clear();
        Hooks.SendKeys(strokes);
    }

    private void Run(SuperKeyEffect effect)
    {
        switch (effect)
        {
            case SuperKeyEffect.Escape:
                Hooks.SendKeys(ChordStrokes.Tap(VirtualKeys.Escape));
                break;
            case SuperKeyEffect.ToggleCapsLock:
                Hooks.SendKeys(ChordStrokes.Tap(VirtualKeys.Capital));
                break;
            case SuperKeyEffect.NextInputSource:
                _keyboard.SelectNextInputSource();
                break;
        }
    }

    private void ArmWatchdog(Session s)
    {
        s.Watchdog?.Dispose();
        var delay = SuperKeyState.WatchdogDelay(_keyboard.RepeatDelayMs);
        s.Watchdog = new Timer(_ => Watch(s), null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Watchdog tick: a hold whose source key is physically up lost its
    /// release (for example to a window of an elevated app, which the hook
    /// cannot see), so the modifiers are let go. Unknown state re-arms.
    /// </summary>
    private void Watch(Session s)
    {
        lock (s)
        {
            if (!s.State.IsHeld)
            {
                return;
            }

            if (_keyboard.IsPhysicallyDown(s.Config.SourceVk) == false)
            {
                ReleaseModifiers(s, mask: true);
                s.State.Reset();
                s.Watchdog?.Dispose();
                s.Watchdog = null;
                return;
            }

            s.Watchdog?.Change(SuperKeyState.WatchdogDelay(_keyboard.RepeatDelayMs), Timeout.InfiniteTimeSpan);
        }
    }

    private bool IsHeldEitherSide(KeyModifiers modifier) => modifier switch
    {
        KeyModifiers.Control => Hooks.IsKeyDown(VirtualKeys.LControl) || Hooks.IsKeyDown(VirtualKeys.RControl),
        KeyModifiers.Alt => Hooks.IsKeyDown(VirtualKeys.LMenu) || Hooks.IsKeyDown(VirtualKeys.RMenu),
        KeyModifiers.Shift => Hooks.IsKeyDown(VirtualKeys.LShift) || Hooks.IsKeyDown(VirtualKeys.RShift),
        KeyModifiers.Win => Hooks.IsKeyDown(VirtualKeys.LWin) || Hooks.IsKeyDown(VirtualKeys.RWin),
        _ => false,
    };

    private sealed record Config(string Source, int SourceVk, KeyModifiers Modifiers, string SoloAction, AppExclusionList Exceptions)
    {
        public static Config Default { get; } = new(SuperKeySources.CapsLock, VirtualKeys.Capital, SuperKeyModifierSet.Default, SuperKeySoloActions.None, AppExclusionList.Empty);
    }

    private sealed class Session(Config config)
    {
        public Config Config { get; set; } = config;

        public SuperKeyState State { get; } = new();

        public bool[] Physical { get; } = new bool[256];

        public List<int> Injected { get; } = new(4);

        public Timer? Watchdog { get; set; }
    }

    private sealed class SessionHandle(Session session, IDisposable inner) : IDisposable
    {
        public Session Session { get; } = session;

        public void Dispose() => inner.Dispose();
    }
}
