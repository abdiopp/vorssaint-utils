// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Input;

/// <summary>What the quit-protection HUD shows: a title, an optional detail line and an optional hold progress.</summary>
public sealed record QuitHudContent(string Title, string? Detail, TimeSpan? ProgressRemaining);

/// <summary>Shows and hides the quit-protection HUD (implemented by an overlay window; tests record the calls).</summary>
public interface IQuitProtectionHud
{
    void Show(QuitHudContent content);

    void Hide();
}

/// <summary>
/// Quit and close protection on the shared keyboard hook (spec 07 §3.7.7).
/// The two slots protect Alt+F4 (and Ctrl+Q) and Ctrl+W (and Ctrl+F4) per
/// app scope; the protected press is swallowed and the chord is sent again
/// (tagged) only after the configured confirmation. The foreground window is
/// re-checked before anything is sent, so a confirmation never lands in a
/// different window. Windows of apps running as administrator are out of the
/// hook's reach and stay unprotected (UIPI).
/// </summary>
public sealed class QuitProtectionService : InputFeatureService, IQuitProtectionHost
{
    private readonly object _gate = new();
    private readonly IAppIdentityResolver _resolver;
    private readonly IQuitProtectionHud _hud;
    private readonly QuitProtectionMachine _machine;
    private readonly Timer _wake;

    public QuitProtectionService(ISettingsStore settings, IInputHooks hooks, IInputClock clock, IAppIdentityResolver resolver, IQuitProtectionHud hud)
        : base(settings, hooks, clock)
    {
        _resolver = resolver;
        _hud = hud;
        _machine = new QuitProtectionMachine(this);
        _wake = new Timer(_ => OnWake(), null, Timeout.Infinite, Timeout.Infinite);
        Observe([.. QuitProtectionSlotSettings.Quit.All, .. QuitProtectionSlotSettings.Close.All]);
    }

    /// <summary>The machine, for tests.</summary>
    internal QuitProtectionMachine Machine => _machine;

    public static QuitProtectionSlotConfig ReadSlot(ISettingsStore settings, QuitSlot slot)
    {
        var s = slot == QuitSlot.Quit ? QuitProtectionSlotSettings.Quit : QuitProtectionSlotSettings.Close;
        var chords = new List<QuitProtectionChord>(2);
        if (slot == QuitSlot.Quit)
        {
            chords.Add(QuitProtectionChord.AltF4);
            if (settings.Get(s.SecondChord)) chords.Add(QuitProtectionChord.CtrlQ);
        }
        else
        {
            chords.Add(QuitProtectionChord.CtrlW);
            if (settings.Get(s.SecondChord)) chords.Add(QuitProtectionChord.CtrlF4);
        }

        return new QuitProtectionSlotConfig
        {
            Slot = slot,
            Enabled = settings.Get(s.Enabled),
            Mode = settings.Get(s.Mode),
            HoldMs = settings.Get(s.HoldDurationMs),
            DoubleIntervalMs = settings.Get(s.DoubleIntervalMs),
            ExtraModifier = settings.Get(s.ExtraModifier),
            Scope = settings.Get(s.Scope),
            Apps = new AppExclusionList(settings.Get(s.Exceptions)),
            ShowFeedback = settings.Get(s.ShowFeedback),
            Chords = chords,
        };
    }

    /// <summary>The HUD title for a request, e.g. "Hold Alt+F4 to quit".</summary>
    public static QuitHudContent HudContent(QuitHudRequest request, IKeyNameProvider? names, long nowNs)
    {
        var chord = request.Shown.ToDisplayString(names);
        var quit = request.Slot == QuitSlot.Quit;
        var (key, detail) = request.Mode switch
        {
            QuitProtectionModes.DoublePress => (quit ? "quitProtection.doubleQuitHUDFormat" : "quitProtection.doubleCloseHUDFormat", L.Get("quitProtection.cancelHint")),
            QuitProtectionModes.ExtraModifier => (quit ? "quitProtection.extraQuitHUDFormat" : "quitProtection.extraCloseHUDFormat", (string?)null),
            _ => (quit ? "quitProtection.holdQuitHUDFormat" : "quitProtection.holdCloseHUDFormat", L.Get("quitProtection.cancelHint")),
        };
        TimeSpan? remaining = request.ProgressEndNs is { } end
            ? TimeSpan.FromMilliseconds(Math.Max(0, (end - nowNs) / (double)InputTime.NsPerMs))
            : null;
        return new QuitHudContent(L.Format(key, chord), detail, remaining);
    }

    public override void Dispose()
    {
        base.Dispose();
        _wake.Dispose();
    }

    protected override bool WantsRunning() =>
        Settings.Get(FeatureKeys.QuitProtectionQuitEnabled) || Settings.Get(FeatureKeys.QuitProtectionCloseEnabled);

    protected override void OnConfigure()
    {
        lock (_gate)
        {
            _machine.Quit = ReadSlot(Settings, QuitSlot.Quit);
            _machine.Close = ReadSlot(Settings, QuitSlot.Close);
        }
    }

    protected override IDisposable StartSession()
    {
        lock (_gate)
        {
            _machine.Reset();
        }

        var subscription = Hooks.SubscribeKeyboard(OnKey, InputPriorities.QuitProtection);
        return new CompositeDisposable(_resolver.Track(), subscription);
    }

    protected override void StopSession(IDisposable session)
    {
        session.Dispose();
        lock (_gate)
        {
            _machine.Reset();
        }
    }

    private bool OnKey(ref KeyboardHookEvent e)
    {
        var now = Clock.NowNs;
        lock (_gate)
        {
            return e.Action == KeyAction.Down
                ? _machine.OnKeyDown(e.VirtualKey, e.Modifiers, now)
                : _machine.OnKeyUp(e.VirtualKey, now);
        }
    }

    private void OnWake()
    {
        lock (_gate)
        {
            _machine.OnWake(Clock.NowNs);
        }
    }

    // ── IQuitProtectionHost (called under _gate) ─────────────────────────
    void IQuitProtectionHost.ShowHud(QuitHudRequest request) =>
        _hud.Show(HudContent(request, null, Clock.NowNs));

    void IQuitProtectionHost.HideHud() => _hud.Hide();

    void IQuitProtectionHost.SendChord(KeyChord chord) => Hooks.SendKeys(ChordStrokes.Exact(chord, Hooks.IsKeyDown));

    void IQuitProtectionHost.SendMask() => Hooks.SendKeys(ChordStrokes.Mask);

    void IQuitProtectionHost.ScheduleWake(long? deadlineNs)
    {
        if (deadlineNs is not { } deadline)
        {
            _wake.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var dueMs = Math.Max(0, (deadline - Clock.NowNs) / InputTime.NsPerMs) + 1;
        _wake.Change(dueMs, Timeout.Infinite);
    }

    nint IQuitProtectionHost.ForegroundWindow() => _resolver.ForegroundWindow;

    string? IQuitProtectionHost.ForegroundAppPath() => _resolver.ResolveForegroundNow().App?.Path;
}
