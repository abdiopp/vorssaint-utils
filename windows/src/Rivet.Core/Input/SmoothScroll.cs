// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Input;

/// <summary>
/// The smooth-scroll curve for one axis (spec 07 §6.7). Each frame emits an
/// exponential-decay share of what remains, with a minimum speed and an
/// optional "coast" that stretches the landing. Same-direction input adds to
/// the glide; a reversal replaces the tail so the first opposite notch answers
/// at once. Distances are pixels (Windows: 1 px = 3 wheel units, so the default
/// 40 px step equals one 120-unit notch).
/// </summary>
public sealed class SmoothScrollAxis
{
    /// <summary>Largest frame step, so a stalled frame never dumps the whole glide.</summary>
    public const double MaxFrameSeconds = 1.0 / 20;

    public double Remaining { get; private set; }

    /// <summary>Total distance of the current glide (for the coast stretch).</summary>
    public double Glide { get; private set; }

    public bool IsIdle => Remaining == 0;

    /// <summary>Adds a notch's distance; returns true when this reversed the axis (callers drop their carry).</summary>
    public bool Add(double distance)
    {
        if (distance == 0 || !double.IsFinite(distance))
        {
            return false;
        }

        if (Remaining != 0 && Math.Sign(distance) != Math.Sign(Remaining))
        {
            Remaining = distance;
            Glide = Math.Abs(distance);
            return true;
        }

        Remaining += distance;
        Glide += Math.Abs(distance);
        return false;
    }

    public void Reset()
    {
        Remaining = 0;
        Glide = 0;
    }

    /// <summary>Response time τ₀ = 0.160 − 0.120·R/100 s.</summary>
    public static double ResponseTime(int response) => 0.160 - (0.120 * Math.Clamp(response, 0, 100) / 100.0);

    /// <summary>Advances by <paramref name="elapsedSeconds"/> and returns the signed distance to emit.</summary>
    public double Step(double elapsedSeconds, int response, int coast)
    {
        var r = Math.Abs(Remaining);
        if (r == 0)
        {
            return 0;
        }

        var dt = Math.Clamp(elapsedSeconds, 0, MaxFrameSeconds);
        var stretch = 1.0;
        if (Glide > r)
        {
            var progress = 1 - (r / Glide);
            stretch = 1 + (Math.Clamp(coast, 0, 100) / 100.0 * 2 * progress * progress);
        }

        double emit;
        if (r <= 1 / stretch)
        {
            emit = r;
        }
        else
        {
            var tau = ResponseTime(response) * stretch;
            var share = r * (1 - Math.Exp(-dt / tau));
            var minimum = 60 / stretch * dt;
            emit = Math.Min(r, Math.Max(share, minimum));
        }

        var signed = Math.Sign(Remaining) * emit;
        Remaining -= signed;
        if (Math.Abs(Remaining) < 1e-9)
        {
            Remaining = 0;
            Glide = 0;
        }

        return signed;
    }
}

/// <summary>
/// Splits fractional wheel units into whole ones: truncation with a carry,
/// rounding to nearest on the landing frame. Reset on reversal and landing.
/// </summary>
public sealed class WheelUnitCarry
{
    /// <summary>Synthetic deltas are clamped to this magnitude.</summary>
    public const int MaxUnits = 1_000_000;

    private double _carry;

    public int Take(double units, bool landing)
    {
        var total = units + _carry;
        int whole;
        if (landing)
        {
            whole = (int)Math.Round(total, MidpointRounding.AwayFromZero);
            _carry = 0;
        }
        else
        {
            whole = (int)Math.Truncate(total);
            _carry = total - whole;
        }

        return Math.Clamp(whole, -MaxUnits, MaxUnits);
    }

    public void Reset() => _carry = 0;
}

/// <summary>Both axes of a glide plus their carries, shared by the hook and frame threads under a lock.</summary>
public sealed class SmoothScrollEngine
{
    /// <summary>Wheel units per pixel: a 40 px step is one 120-unit notch.</summary>
    public const double UnitsPerPixel = 3.0;

    private readonly object _gate = new();
    private readonly SmoothScrollAxis _vertical = new();
    private readonly SmoothScrollAxis _horizontal = new();
    private readonly WheelUnitCarry _verticalCarry = new();
    private readonly WheelUnitCarry _horizontalCarry = new();
    private bool _shift;
    private bool _hasShift;

    public int Response { get; set; } = 65;

    public int Coast { get; set; }

    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return _vertical.IsIdle && _horizontal.IsIdle;
            }
        }
    }

    /// <summary>
    /// Adds one notch (pixels, signed in wheel direction). A change of the
    /// Shift state since the last notch restarts the glide (spec rule 10).
    /// </summary>
    public void Add(double pixels, bool horizontal, bool shift)
    {
        lock (_gate)
        {
            if (_hasShift && shift != _shift)
            {
                ResetLocked();
            }

            _shift = shift;
            _hasShift = true;
            var axis = horizontal ? _horizontal : _vertical;
            if (axis.Add(pixels))
            {
                (horizontal ? _horizontalCarry : _verticalCarry).Reset();
            }
        }
    }

    /// <summary>Runs one frame. Returns the whole wheel units to send per axis and whether the glide continues.</summary>
    public (int Vertical, int Horizontal, bool Active) Frame(double elapsedSeconds)
    {
        lock (_gate)
        {
            var v = StepAxis(_vertical, _verticalCarry, elapsedSeconds);
            var h = StepAxis(_horizontal, _horizontalCarry, elapsedSeconds);
            return (v, h, !_vertical.IsIdle || !_horizontal.IsIdle);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ResetLocked();
            _hasShift = false;
        }
    }

    private void ResetLocked()
    {
        _vertical.Reset();
        _horizontal.Reset();
        _verticalCarry.Reset();
        _horizontalCarry.Reset();
    }

    private int StepAxis(SmoothScrollAxis axis, WheelUnitCarry carry, double elapsed)
    {
        if (axis.IsIdle)
        {
            return 0;
        }

        var pixels = axis.Step(elapsed, Response, Coast);
        return carry.Take(pixels * UnitsPerPixel, landing: axis.IsIdle);
    }
}

/// <summary>
/// Which wheel axis a direction setting applies to (spec 07 §3.7.5). A Shift
/// + vertical notch is scrolled sideways by most Windows apps, so it follows
/// the horizontal setting, matching what the user sees.
/// </summary>
public static class ScrollDirection
{
    public static bool ShouldInvert(bool horizontalEvent, bool shiftHeld, bool invertVertical, bool invertHorizontal) =>
        horizontalEvent || shiftHeld ? invertHorizontal : invertVertical;
}
