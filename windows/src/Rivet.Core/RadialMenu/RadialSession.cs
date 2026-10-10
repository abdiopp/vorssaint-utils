// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.RadialMenu;

public enum RadialTriggerKind
{
    /// <summary>A profile's global shortcut.</summary>
    Shortcut,

    /// <summary>A profile's side mouse button.</summary>
    MouseButton,

    /// <summary>Settings "Try it" (and anything else that cannot be held).</summary>
    TryIt,
}

public enum RadialPhase
{
    Closed,

    /// <summary>The trigger is still held: releasing it runs the highlighted slice (mode-dependent).</summary>
    Held,

    /// <summary>The wheel stays open until a click, Enter, a digit, Esc or a dismissal.</summary>
    Sticky,
}

public enum RadialKey
{
    Escape,
    Enter,
    Left,
    Up,
    Right,
    Down,
}

public enum RadialOutcomeKind
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>The highlight, level or phase changed: redraw.</summary>
    Changed,

    /// <summary>Close the wheel, then run <see cref="RadialOutcome.Item"/>.</summary>
    Run,

    /// <summary>Close the wheel.</summary>
    Close,
}

public readonly record struct RadialOutcome(RadialOutcomeKind Kind, RadialItem? Item = null)
{
    public static RadialOutcome None => new(RadialOutcomeKind.None);

    public static RadialOutcome Changed => new(RadialOutcomeKind.Changed);

    public static RadialOutcome Close => new(RadialOutcomeKind.Close);

    public static RadialOutcome Run(RadialItem item) => new(RadialOutcomeKind.Run, item);
}

/// <summary>
/// One wheel session as a pure state machine (spec 07 §3.2.4–3.2.8, §6.2):
/// hold versus press is decided only by whether a slice is highlighted at
/// release (there is no time threshold); pointer moves change nothing until
/// the pointer travelled 8 DIP from where the wheel (or a submenu) opened;
/// arrows rotate and, except in hold mode, switch to the sticky phase; digits
/// 1–9 run that slice; Esc steps back; one submenu level. Positions are DIP
/// offsets from the wheel centre with y pointing down (screen convention).
/// </summary>
public sealed class RadialSession
{
    private readonly List<(IReadOnlyList<RadialItem> Items, string? Name)> _stack = [];
    private (double X, double Y) _armOrigin;
    private (double X, double Y) _pointer;
    private bool _armed;

    public RadialSession(IReadOnlyList<RadialItem> rootItems, RadialTriggerKind trigger, bool shortcutHasModifiers, RadialActivationMode mode, (double X, double Y) pointer)
    {
        Mode = mode;
        Trigger = trigger;
        _stack.Add((rootItems, null));
        _pointer = pointer;
        _armOrigin = pointer;
        Phase = StartsHeld(trigger, shortcutHasModifiers, mode) ? RadialPhase.Held : RadialPhase.Sticky;
    }

    public RadialActivationMode Mode { get; }

    public RadialTriggerKind Trigger { get; }

    public RadialPhase Phase { get; private set; }

    /// <summary>The highlighted slice of the current level, if any.</summary>
    public int? Highlight { get; private set; }

    /// <summary>The highlight came from the keyboard (arrows) rather than the pointer.</summary>
    public bool HighlightFromKeyboard { get; private set; }

    public IReadOnlyList<RadialItem> CurrentItems => _stack[^1].Items;

    public bool InSubmenu => _stack.Count > 1;

    /// <summary>Name of the open submenu (null at the root; empty when unnamed).</summary>
    public string? SubmenuName => InSubmenu ? _stack[^1].Name ?? string.Empty : null;

    public RadialItem? HighlightedItem => Highlight is { } i && i >= 0 && i < CurrentItems.Count ? CurrentItems[i] : null;

    /// <summary>
    /// Whether a session starts in the hold phase: shortcuts with modifiers and
    /// mouse buttons do, unless the mode is "press"; bare function keys, the
    /// trackpad tap and "Try it" never do (their release cannot be seen).
    /// </summary>
    public static bool StartsHeld(RadialTriggerKind trigger, bool shortcutHasModifiers, RadialActivationMode mode) => trigger switch
    {
        RadialTriggerKind.Shortcut => shortcutHasModifiers && mode != RadialActivationMode.Press,
        RadialTriggerKind.MouseButton => mode != RadialActivationMode.Press,
        _ => false,
    };

    /// <summary>The pointer moved (DIP offset from the centre, y down).</summary>
    public RadialOutcome PointerMoved(double x, double y)
    {
        if (Phase == RadialPhase.Closed)
        {
            return RadialOutcome.None;
        }

        _pointer = (x, y);
        if (!_armed)
        {
            var dx = x - _armOrigin.X;
            var dy = y - _armOrigin.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) < RadialGeometry.ArmingTravel)
            {
                return RadialOutcome.None;
            }

            _armed = true;
        }

        var highlight = RadialGeometry.Highlight(x, -y, CurrentItems.Count);
        if (highlight == Highlight && !HighlightFromKeyboard)
        {
            return RadialOutcome.None;
        }

        Highlight = highlight;
        HighlightFromKeyboard = false;
        return RadialOutcome.Changed;
    }

    /// <summary>
    /// The held trigger was released (modifier up, or the mouse button up).
    /// Press-or-hold: run the highlight, or stay open (sticky). Hold: run the
    /// highlight, or close. Ignored outside the hold phase.
    /// </summary>
    public RadialOutcome Release()
    {
        if (Phase != RadialPhase.Held)
        {
            return RadialOutcome.None;
        }

        if (Highlight is { } index)
        {
            return Activate(index);
        }

        if (Mode == RadialActivationMode.Hold)
        {
            return Finish(RadialOutcome.Close);
        }

        Phase = RadialPhase.Sticky;
        return RadialOutcome.Changed;
    }

    public RadialOutcome Key(RadialKey key)
    {
        if (Phase == RadialPhase.Closed)
        {
            return RadialOutcome.None;
        }

        switch (key)
        {
            case RadialKey.Escape:
                return StepBack();
            case RadialKey.Enter:
                return Highlight is { } index ? Activate(index) : RadialOutcome.None;
            default:
                var delta = key is RadialKey.Right or RadialKey.Down ? 1 : -1;
                if (CurrentItems.Count == 0)
                {
                    return RadialOutcome.None;
                }

                Highlight = RadialGeometry.Rotate(Highlight, delta, CurrentItems.Count);
                HighlightFromKeyboard = true;
                if (Mode != RadialActivationMode.Hold)
                {
                    Phase = RadialPhase.Sticky;
                }

                return RadialOutcome.Changed;
        }
    }

    /// <summary>Digits 1–9 run that slice if it exists (slices 10–12 have no digit).</summary>
    public RadialOutcome Digit(int digit)
    {
        if (Phase == RadialPhase.Closed || digit is < 1 or > 9 || digit > CurrentItems.Count)
        {
            return RadialOutcome.None;
        }

        return Activate(digit - 1);
    }

    /// <summary>A click on the wheel window at <paramref name="distance"/> DIP from the centre.</summary>
    public RadialOutcome Click(double distance)
    {
        if (Phase == RadialPhase.Closed)
        {
            return RadialOutcome.None;
        }

        return RadialGeometry.ClickAction(distance) switch
        {
            RadialClickAction.Close => Finish(RadialOutcome.Close),
            RadialClickAction.StepBack => StepBack(),
            _ => Highlight is { } index ? Activate(index) : RadialOutcome.None,
        };
    }

    /// <summary>Any dismissal (outside click, app switch, same trigger again).</summary>
    public RadialOutcome Dismiss() => Phase == RadialPhase.Closed ? RadialOutcome.None : Finish(RadialOutcome.Close);

    private RadialOutcome Activate(int index)
    {
        if (index < 0 || index >= CurrentItems.Count)
        {
            return RadialOutcome.None;
        }

        var item = CurrentItems[index];
        if (item.IsSubmenu && !InSubmenu)
        {
            // Push the children, re-arm from where the pointer is now so a
            // double click never fires the child under the parent's direction.
            _stack.Add((item.Children, item.Name));
            Highlight = null;
            HighlightFromKeyboard = false;
            Phase = RadialPhase.Sticky;
            _armOrigin = _pointer;
            _armed = false;
            return RadialOutcome.Changed;
        }

        return Finish(RadialOutcome.Run(item));
    }

    private RadialOutcome StepBack()
    {
        if (!InSubmenu)
        {
            return Finish(RadialOutcome.Close);
        }

        _stack.RemoveAt(_stack.Count - 1);
        Highlight = null;
        HighlightFromKeyboard = false;
        _armOrigin = _pointer;
        _armed = false;
        return RadialOutcome.Changed;
    }

    private RadialOutcome Finish(RadialOutcome outcome)
    {
        Phase = RadialPhase.Closed;
        return outcome;
    }
}
