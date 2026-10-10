// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Input;

/// <summary>
/// Button numbering shared with the macOS app: 0 left, 1 right, 2 middle,
/// 3 Back (XBUTTON1), 4 Forward (XBUTTON2), 5–31 further buttons (not
/// delivered by the standard Windows mouse stack), and the side-wheel
/// directions as pseudo-buttons −2 (left) and −1 (right).
/// </summary>
public static class MouseButtonIds
{
    public const int Middle = 2;
    public const int Back = 3;
    public const int Forward = 4;
    public const int FirstExtra = 3;
    public const int LastExtra = 31;
    public const int SideWheelLeft = -2;
    public const int SideWheelRight = -1;

    /// <summary>XBUTTON1/XBUTTON2 (1 or 2) → 3 or 4.</summary>
    public static int FromXButton(int xButton) => xButton == 2 ? Forward : Back;

    /// <summary>The XBUTTON number (1/2) for a button id, or 0 when Windows cannot synthesize it.</summary>
    public static int ToXButton(int id) => id switch
    {
        Back => 1,
        Forward => 2,
        _ => 0,
    };

    public static bool IsMappable(int id) => id is SideWheelLeft or SideWheelRight or (>= FirstExtra and <= LastExtra);

    public static bool IsSideWheel(int id) => id is SideWheelLeft or SideWheelRight;

    /// <summary>The string-table key and format argument for a button's name.</summary>
    public static (string Key, int? Number) NameKey(int id) => id switch
    {
        Back => ("mouseButtons.backButtonName", null),
        Forward => ("mouseButtons.forwardButtonName", null),
        SideWheelLeft => ("mouseButtons.sideWheelLeftName", null),
        SideWheelRight => ("mouseButtons.sideWheelRightName", null),
        // Matches mouse software: button 5 is "Button 6".
        _ => ("mouseButtons.otherButtonFormat", id + 1),
    };

    /// <summary>Rows sort numerically: −2, −1, 3, 4, 5…</summary>
    public static IEnumerable<int> Sorted(IEnumerable<int> ids) => ids.OrderBy(i => i);
}

/// <summary>Validation of the stored button → shortcut map. Invalid entries are dropped on read.</summary>
public static class MouseButtonMappings
{
    public static IReadOnlyDictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? stored)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stored is null)
        {
            return result;
        }

        foreach (var (key, value) in stored)
        {
            if (TryParseId(key, out var id) && KeyChord.TryParse(value, out var chord) && IsBindable(chord))
            {
                result[id.ToString(CultureInfo.InvariantCulture)] = chord.ToStorageString();
            }
        }

        return result;
    }

    public static bool TryParseId(string? key, out int id) =>
        int.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id) && MouseButtonIds.IsMappable(id);

    /// <summary>The recorder's rule: Ctrl, Alt or Win with a key, or a function key.</summary>
    public static bool IsBindable(KeyChord chord) => chord.IsValidGlobalShortcut;

    public static IReadOnlyDictionary<int, KeyChord> Decode(IReadOnlyDictionary<string, string> stored)
    {
        var result = new Dictionary<int, KeyChord>();
        foreach (var (key, value) in stored)
        {
            if (TryParseId(key, out var id) && KeyChord.TryParse(value, out var chord) && IsBindable(chord))
            {
                result[id] = chord;
            }
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> stored, int id, KeyChord? chord)
    {
        var copy = new Dictionary<string, string>(stored, StringComparer.Ordinal);
        var key = id.ToString(CultureInfo.InvariantCulture);
        if (chord is { IsEmpty: false } c)
        {
            copy[key] = c.ToStorageString();
        }
        else
        {
            copy.Remove(key);
        }

        return copy;
    }
}

/// <summary>
/// Side-wheel burst gate (spec 07 §3.7.3). Events at most 250 ms apart form
/// one burst and every event extends it; inside a burst each direction fires
/// at most once, so a held, auto-repeating tilt fires once until a quiet gap.
/// </summary>
public sealed class SideWheelGate
{
    public const long QuietNs = 250 * InputTime.NsPerMs;

    private long _last;
    private bool _has;
    private bool _firedLeft;
    private bool _firedRight;

    /// <summary>Returns true when the event should fire its direction's shortcut; false = swallow quietly.</summary>
    public bool ShouldFire(int direction, long t)
    {
        if (!_has || t < _last || t - _last > QuietNs)
        {
            _firedLeft = false;
            _firedRight = false;
        }

        _has = true;
        _last = t;
        if (direction == MouseButtonIds.SideWheelLeft)
        {
            if (_firedLeft)
            {
                return false;
            }

            _firedLeft = true;
            return true;
        }

        if (_firedRight)
        {
            return false;
        }

        _firedRight = true;
        return true;
    }

    public void Reset()
    {
        _has = false;
        _firedLeft = false;
        _firedRight = false;
    }
}

public enum DesktopDragAction
{
    None,

    /// <summary>The desktop to the left (Ctrl+Win+Left).</summary>
    PreviousDesktop,

    /// <summary>The desktop to the right (Ctrl+Win+Right).</summary>
    NextDesktop,

    /// <summary>Dragged up: Task View (Win+Tab).</summary>
    Overview,

    /// <summary>Dragged down: macOS App Exposé, which has no Windows equivalent.</summary>
    AppWindows,
}

/// <summary>
/// The button-drag tracker (spec 07 §3.7.3 "Spaces drag"), a pure function of
/// pointer travel. The axis commits at the first firing (horizontal on a tie);
/// vertical fires once per press; horizontal repeats one step at a time, never
/// within 0.35 s of the previous step, banking at most one extra step.
/// Steps are in physical pixels (220/150 DIP scaled by the monitor).
/// </summary>
public sealed class DesktopDragTracker
{
    public const double SpaceStepDip = 220;
    public const double OverviewStepDip = 150;
    public const long RepeatCooldownNs = 350 * InputTime.NsPerMs;

    private readonly double _spaceStep;
    private readonly double _overviewStep;
    private double _sumX;
    private double _sumY;
    private Axis _axis;
    private bool _hasFired;
    private long _lastFire;

    public DesktopDragTracker(double scale = 1.0)
    {
        var s = scale > 0 && double.IsFinite(scale) ? scale : 1.0;
        _spaceStep = SpaceStepDip * s;
        _overviewStep = OverviewStepDip * s;
    }

    private enum Axis
    {
        None,
        Horizontal,
        Vertical,
    }

    public bool HasFired => _hasFired;

    /// <summary>Adds pointer travel (smaller y = up) and returns what to perform now.</summary>
    public DesktopDragAction Feed(double dx, double dy, long t)
    {
        _sumX += dx;
        _sumY += dy;
        switch (_axis)
        {
            case Axis.None:
            {
                var h = Math.Abs(_sumX) / _spaceStep;
                var v = Math.Abs(_sumY) / _overviewStep;
                if (h >= 1 && h >= v)
                {
                    _axis = Axis.Horizontal;
                    return Horizontal(t);
                }

                if (v >= 1)
                {
                    _axis = Axis.Vertical;
                    _hasFired = true;
                    _lastFire = t;
                    return _sumY < 0 ? DesktopDragAction.Overview : DesktopDragAction.AppWindows;
                }

                return DesktopDragAction.None;
            }

            case Axis.Horizontal:
                return Horizontal(t);

            default:
                return DesktopDragAction.None;
        }
    }

    private DesktopDragAction Horizontal(long t)
    {
        if (_hasFired && t - _lastFire < RepeatCooldownNs)
        {
            _sumX = Math.Clamp(_sumX, -_spaceStep, _spaceStep);
            return DesktopDragAction.None;
        }

        if (Math.Abs(_sumX) < _spaceStep)
        {
            return DesktopDragAction.None;
        }

        var left = _sumX < 0;
        _sumX -= left ? -_spaceStep : _spaceStep;
        _hasFired = true;
        _lastFire = t;
        return left ? DesktopDragAction.PreviousDesktop : DesktopDragAction.NextDesktop;
    }
}
