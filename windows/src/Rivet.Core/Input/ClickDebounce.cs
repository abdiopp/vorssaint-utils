// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Input;

/// <summary>The buttons the extra click filter covers. Side buttons are never filtered.</summary>
public enum ClickButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

/// <summary>
/// Extra click filter (spec 07 §3.7.1). A down that arrives less than the
/// window after the last *accepted* up of the same button is a bounce: it and
/// its paired up are swallowed. There are no timers and nothing is ever
/// delayed, so healthy clicks pass untouched. Pure state; the caller supplies
/// monotonic timestamps. Not thread-safe: one instance per hook subscription.
/// </summary>
public sealed class ClickDebounceFilter
{
    /// <summary>
    /// Windows deviation: a pending press older than this is considered lost
    /// (its up went to an elevated window the hook cannot see), so the next
    /// down is judged afresh instead of being swallowed. HID mice report button
    /// state, so a real duplicate down without an up never arrives this late.
    /// </summary>
    public const long StalePressNs = 1_000_000_000;

    private readonly ButtonState[] _buttons = new ButtonState[3];

    public ClickDebounceFilter(long windowNs)
    {
        WindowNs = windowNs;
    }

    public long WindowNs { get; set; }

    /// <summary>Returns true when the down must be swallowed.</summary>
    public bool OnDown(ClickButton button, long t)
    {
        ref var s = ref Prepare(button, t);
        if (s.AcceptedDown || s.SuppressedDown)
        {
            if (t - s.PressStart < StalePressNs)
            {
                // A duplicate down while the press is held, or extra downs before
                // the bounce's up: they share that up.
                return true;
            }

            s.AcceptedDown = false;
            s.SuppressedDown = false;
        }

        if (s.HasAcceptedUp && t - s.LastAcceptedUp >= 0 && t - s.LastAcceptedUp < WindowNs)
        {
            s.SuppressedDown = true;
            s.PressStart = t;
            return true;
        }

        s.AcceptedDown = true;
        s.PressStart = t;
        return false;
    }

    /// <summary>Returns true when the up must be swallowed (it belongs to a swallowed down).</summary>
    public bool OnUp(ClickButton button, long t)
    {
        ref var s = ref Prepare(button, t);
        if (s.SuppressedDown)
        {
            s.SuppressedDown = false;
            return true;
        }

        if (!s.AcceptedDown)
        {
            // Unmatched up (e.g. after a restart of the filter): fail open.
            return false;
        }

        s.AcceptedDown = false;
        s.LastAcceptedUp = t;
        s.HasAcceptedUp = true;
        return false;
    }

    /// <summary>True while a swallowed down still waits for its up.</summary>
    public bool IsSuppressing(ClickButton button) => _buttons[(int)button].SuppressedDown;

    public void Reset() => Array.Clear(_buttons);

    private ref ButtonState Prepare(ClickButton button, long t)
    {
        ref var s = ref _buttons[(int)button];
        if (s.HasEvent && t < s.LastEvent)
        {
            // Time went backwards: start this button over.
            s = default;
        }

        s.HasEvent = true;
        s.LastEvent = t;
        return ref s;
    }

    private struct ButtonState
    {
        public bool AcceptedDown;
        public bool SuppressedDown;
        public bool HasAcceptedUp;
        public bool HasEvent;
        public long LastAcceptedUp;
        public long LastEvent;
        public long PressStart;
    }
}
