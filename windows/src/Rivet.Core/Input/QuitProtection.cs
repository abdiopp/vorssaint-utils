// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Input;

public enum QuitSlot
{
    /// <summary>"Quit / close window": Alt+F4, optionally Ctrl+Q (macOS ⌘Q).</summary>
    Quit,

    /// <summary>"Close tab or document": Ctrl+W, optionally Ctrl+F4 (macOS ⌘W).</summary>
    Close,
}

public static class QuitProtectionModes
{
    public const string Hold = "hold";
    public const string DoublePress = "doublePress";
    public const string ExtraModifier = "extraModifier";

    public const string ShiftModifier = "shift";
    public const string OptionModifier = "option";
    public const string ControlModifier = "control";

    public static IReadOnlyList<string> All { get; } = [Hold, DoublePress, ExtraModifier];

    public static string Sanitize(string value) => All.Contains(value, StringComparer.Ordinal) ? value : Hold;

    public static string SanitizeModifier(string value) =>
        value is ShiftModifier or OptionModifier or ControlModifier ? value : ShiftModifier;

    public static KeyModifiers ModifierOf(string value) => value switch
    {
        OptionModifier => KeyModifiers.Alt,
        ControlModifier => KeyModifiers.Control,
        _ => KeyModifiers.Shift,
    };

    /// <summary>The extra modifier actually required for a chord: never its own base modifier (falls back to Shift).</summary>
    public static KeyModifiers EffectiveExtra(string stored, KeyModifiers chordBase)
    {
        var extra = ModifierOf(stored);
        return (extra & chordBase) != 0 ? KeyModifiers.Shift : extra;
    }
}

public static class QuitProtectionScopes
{
    public const string All = "all";
    public const string SelectedOnly = "selectedOnly";
    public const string AllExceptSelected = "allExceptSelected";

    public static IReadOnlyList<string> Values { get; } = [All, SelectedOnly, AllExceptSelected];

    public static string Sanitize(string value) => Values.Contains(value, StringComparer.Ordinal) ? value : All;

    /// <summary>
    /// The scope truth table. An app that cannot be identified is outside a
    /// "selected only" list and inside an "all except" list.
    /// </summary>
    public static bool Applies(string scope, AppExclusionList list, string? appPath) => scope switch
    {
        SelectedOnly => appPath is not null && list.Matches(appPath),
        AllExceptSelected => appPath is null || !list.Matches(appPath),
        _ => true,
    };
}

/// <summary>One protected chord: base modifier(s) plus a key, matched on virtual-key codes (which already follow the layout).</summary>
public sealed record QuitProtectionChord(QuitSlot Slot, KeyModifiers Base, int VirtualKey)
{
    public KeyChord Chord => new(Base, VirtualKey);

    public static QuitProtectionChord AltF4 { get; } = new(QuitSlot.Quit, KeyModifiers.Alt, VirtualKeys.Function(4));

    public static QuitProtectionChord CtrlQ { get; } = new(QuitSlot.Quit, KeyModifiers.Control, VirtualKeys.Letter('Q'));

    public static QuitProtectionChord CtrlW { get; } = new(QuitSlot.Close, KeyModifiers.Control, VirtualKeys.Letter('W'));

    public static QuitProtectionChord CtrlF4 { get; } = new(QuitSlot.Close, KeyModifiers.Control, VirtualKeys.Function(4));
}

/// <summary>A snapshot of one slot's configuration, rebuilt whenever its settings change.</summary>
public sealed record QuitProtectionSlotConfig
{
    public required QuitSlot Slot { get; init; }

    public bool Enabled { get; init; }

    public string Mode { get; init; } = QuitProtectionModes.Hold;

    public double HoldMs { get; init; } = 800;

    public double DoubleIntervalMs { get; init; } = 600;

    public string ExtraModifier { get; init; } = QuitProtectionModes.ShiftModifier;

    public string Scope { get; init; } = QuitProtectionScopes.All;

    public AppExclusionList Apps { get; init; } = AppExclusionList.Empty;

    public bool ShowFeedback { get; init; } = true;

    public IReadOnlyList<QuitProtectionChord> Chords { get; init; } = [];
}

/// <summary>What the HUD should show.</summary>
public sealed record QuitHudRequest(QuitSlot Slot, string Mode, KeyChord Shown, long? ProgressStartNs, long? ProgressEndNs);

/// <summary>Side effects of <see cref="QuitProtectionMachine"/>; implemented by the service (and fakes in tests).</summary>
public interface IQuitProtectionHost
{
    void ShowHud(QuitHudRequest request);

    void HideHud();

    /// <summary>Injects the chord exactly (other held modifiers released first), down then up.</summary>
    void SendChord(KeyChord chord);

    /// <summary>Injects the mask key so a swallowed Alt chord does not activate a menu bar on Alt's release.</summary>
    void SendMask();

    /// <summary>Wakes the machine at a monotonic deadline (null cancels).</summary>
    void ScheduleWake(long? deadlineNs);

    /// <summary>The window in front now (cheap; checked before confirming).</summary>
    nint ForegroundWindow();

    /// <summary>The identity of the foreground app, resolved now (null when unknown).</summary>
    string? ForegroundAppPath();
}

/// <summary>
/// Quit and close protection (spec 07 §3.7.7, §6.7 state table), adapted to
/// Windows chords. Callers serialize access (the hook thread and the timer
/// thread take the same lock). Returns true from the key handlers to swallow.
/// </summary>
public sealed class QuitProtectionMachine
{
    public const double DoublePendingSlackMs = 100;
    public const double ExtraPendingMs = 1500;

    private readonly IQuitProtectionHost _host;
    private readonly bool[] _down = new bool[256];
    private Pending? _pending;
    private int _swallowVk = -1;
    private bool _passSwallowedUp;

    public QuitProtectionMachine(IQuitProtectionHost host)
    {
        _host = host;
    }

    public QuitProtectionSlotConfig Quit { get; set; } = new() { Slot = QuitSlot.Quit };

    public QuitProtectionSlotConfig Close { get; set; } = new() { Slot = QuitSlot.Close };

    public bool IsPending => _pending is not null;

    public QuitProtectionChord? PendingChord => _pending?.Chord;

    public bool OnKeyDown(int vk, KeyModifiers modifiers, long t)
    {
        vk &= 0xFF;
        var isRepeat = _down[vk];
        _down[vk] = true;

        if (vk == VirtualKeys.Escape && _pending is not null)
        {
            Cancel();
            return true;
        }

        if (vk == _swallowVk)
        {
            return true;
        }

        var held = modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Win);
        if (_pending is null && (held & (KeyModifiers.Control | KeyModifiers.Alt)) == 0)
        {
            return false;
        }

        if (IsModifierKey(vk))
        {
            return false;
        }

        var match = Match(vk);
        if (_pending is not null && (match is null || match.Value.Chord != _pending.Chord))
        {
            // Any other key, or a different protected shortcut, cancels what is pending.
            Cancel();
        }

        if (match is null)
        {
            return false;
        }

        var (chord, slot) = match.Value;
        if (!QuitProtectionScopes.Applies(slot.Scope, slot.Apps, _host.ForegroundAppPath()))
        {
            return false;
        }

        if (isRepeat && (held & chord.Base) != 0)
        {
            return true;
        }

        return slot.Mode switch
        {
            QuitProtectionModes.DoublePress => DoublePress(chord, slot, held, t),
            QuitProtectionModes.ExtraModifier => ExtraModifier(chord, slot, held, t),
            _ => Hold(chord, slot, held, t),
        };
    }

    public bool OnKeyUp(int vk, long t)
    {
        vk &= 0xFF;
        _down[vk] = false;
        if (vk == _swallowVk)
        {
            _swallowVk = -1;
            return !_passSwallowedUp;
        }

        if (_pending is { } pending)
        {
            if (vk == pending.Chord.VirtualKey)
            {
                if (pending.Mode != QuitProtectionModes.DoublePress)
                {
                    // Hold released too early, or the extra-modifier hint: cancel.
                    Cancel();
                }

                return true;
            }

            if (IsBaseModifierKey(vk, pending.Chord.Base) && !BaseStillHeld(pending.Chord.Base))
            {
                Cancel();
            }
        }

        return false;
    }

    /// <summary>The scheduled deadline arrived.</summary>
    public void OnWake(long t)
    {
        if (_pending is not { } pending || t < pending.Deadline)
        {
            if (_pending is { } still)
            {
                _host.ScheduleWake(still.Deadline);
            }

            return;
        }

        if (pending.Mode == QuitProtectionModes.Hold)
        {
            _pending = null;
            _host.HideHud();
            _host.ScheduleWake(null);
            if (_host.ForegroundWindow() != pending.Target)
            {
                // The target lost focus: never send the chord to another window.
                return;
            }

            // The physical key is still down: its auto-repeats and release are eaten.
            _swallowVk = pending.Chord.VirtualKey;
            _passSwallowedUp = false;
            _host.SendChord(pending.Chord.Chord);
            return;
        }

        Cancel();
    }

    /// <summary>Forget everything (feature stopped or reconfigured).</summary>
    public void Reset()
    {
        if (_pending is not null)
        {
            Cancel();
        }

        _swallowVk = -1;
        Array.Clear(_down);
    }

    public void Cancel()
    {
        _pending = null;
        _host.HideHud();
        _host.ScheduleWake(null);
    }

    private bool Hold(QuitProtectionChord chord, QuitProtectionSlotConfig slot, KeyModifiers held, long t)
    {
        if (held != chord.Base)
        {
            return false;
        }

        var deadline = t + InputTime.FromMs(slot.HoldMs);
        Start(chord, slot, t, deadline);
        if (slot.ShowFeedback)
        {
            _host.ShowHud(new QuitHudRequest(chord.Slot, slot.Mode, chord.Chord, t, deadline));
        }

        return true;
    }

    private bool DoublePress(QuitProtectionChord chord, QuitProtectionSlotConfig slot, KeyModifiers held, long t)
    {
        if (held != chord.Base)
        {
            return false;
        }

        if (_pending is { } pending && pending.Chord == chord && pending.Mode == QuitProtectionModes.DoublePress
            && t - pending.Start <= InputTime.FromMs(slot.DoubleIntervalMs) && _host.ForegroundWindow() == pending.Target)
        {
            // Second press inside the interval: let this press through and eat its repeats.
            _pending = null;
            _host.HideHud();
            _host.ScheduleWake(null);
            _swallowVk = chord.VirtualKey;
            _passSwallowedUp = true;
            return false;
        }

        Start(chord, slot, t, t + InputTime.FromMs(slot.DoubleIntervalMs + DoublePendingSlackMs));
        if (slot.ShowFeedback)
        {
            _host.ShowHud(new QuitHudRequest(chord.Slot, slot.Mode, chord.Chord, null, null));
        }

        return true;
    }

    private bool ExtraModifier(QuitProtectionChord chord, QuitProtectionSlotConfig slot, KeyModifiers held, long t)
    {
        var extra = QuitProtectionModes.EffectiveExtra(slot.ExtraModifier, chord.Base);
        if (held == (chord.Base | extra))
        {
            _pending = null;
            _host.HideHud();
            _host.ScheduleWake(null);
            _swallowVk = chord.VirtualKey;
            _passSwallowedUp = false;
            _host.SendChord(chord.Chord);
            return true;
        }

        if (held != chord.Base)
        {
            return false;
        }

        Start(chord, slot, t, t + InputTime.FromMs(ExtraPendingMs));
        if (slot.ShowFeedback)
        {
            _host.ShowHud(new QuitHudRequest(chord.Slot, slot.Mode, new KeyChord(chord.Base | extra, chord.VirtualKey), null, null));
        }

        return true;
    }

    private void Start(QuitProtectionChord chord, QuitProtectionSlotConfig slot, long t, long deadline)
    {
        _pending = new Pending(chord, slot.Mode, t, deadline, _host.ForegroundWindow());
        if (chord.Base.HasFlag(KeyModifiers.Alt))
        {
            _host.SendMask();
        }

        _host.ScheduleWake(deadline);
    }

    private (QuitProtectionChord Chord, QuitProtectionSlotConfig Slot)? Match(int vk) => Match(Quit, vk) ?? Match(Close, vk);

    private static (QuitProtectionChord Chord, QuitProtectionSlotConfig Slot)? Match(QuitProtectionSlotConfig slot, int vk)
    {
        if (!slot.Enabled)
        {
            return null;
        }

        for (var i = 0; i < slot.Chords.Count; i++)
        {
            if (slot.Chords[i].VirtualKey == vk)
            {
                return (slot.Chords[i], slot);
            }
        }

        return null;
    }

    private bool BaseStillHeld(KeyModifiers chordBase) =>
        chordBase.HasFlag(KeyModifiers.Alt)
            ? _down[VirtualKeys.LMenu] || _down[VirtualKeys.RMenu] || _down[VirtualKeys.Menu]
            : _down[VirtualKeys.LControl] || _down[VirtualKeys.RControl] || _down[VirtualKeys.Control];

    private static bool IsBaseModifierKey(int vk, KeyModifiers chordBase) => chordBase.HasFlag(KeyModifiers.Alt)
        ? vk is VirtualKeys.LMenu or VirtualKeys.RMenu or VirtualKeys.Menu
        : vk is VirtualKeys.LControl or VirtualKeys.RControl or VirtualKeys.Control;

    private static bool IsModifierKey(int vk) => VirtualKeys.IsModifier(vk) || vk == VirtualKeys.Capital || vk == ChordStrokes.MaskKey;

    private sealed record Pending(QuitProtectionChord Chord, string Mode, long Start, long Deadline, nint Target);
}
