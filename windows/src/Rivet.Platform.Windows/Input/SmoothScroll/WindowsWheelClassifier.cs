// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Input;

namespace Rivet.Platform.Windows.Input.SmoothScroll;

/// <summary>
/// Mouse wheel vs Precision Touchpad for the wheel features. WH_MOUSE_LL
/// carries no device, so:
/// <list type="number">
/// <item>a touchpad report seen in the last <see cref="TouchpadGraceMs"/> ms
/// makes the event touchpad scrolling (it is left alone);</item>
/// <item>otherwise whole notches (multiples of 120) are a mouse wheel;</item>
/// <item>fractional deltas are a high-resolution mouse wheel only when a
/// Precision Touchpad exists and is being tracked (it would have reported
/// contacts); without one they stay "unknown", because legacy touchpad drivers
/// also send fractional wheel messages.</item>
/// </list>
/// Raw Input cannot be matched to a hook event one by one (the hook runs
/// before WM_INPUT is read), hence the activity window.
/// </summary>
public sealed class WindowsWheelClassifier : IWheelDeviceClassifier
{
    public const double TouchpadGraceMs = 350;

    private readonly WindowsRawInput _rawInput;
    private readonly object _gate = new();
    private int _users;
    private IDisposable? _tracking;
    private bool? _hasTouchpad;

    public WindowsWheelClassifier(WindowsRawInput rawInput)
    {
        _rawInput = rawInput;
    }

    public WheelSource Classify(int delta, long timestampNs)
    {
        var last = _rawInput.LastTouchpadReport;
        if (last != 0 && _rawInput.TouchpadTracking)
        {
            var sinceMs = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
            if (sinceMs <= TouchpadGraceMs)
            {
                return WheelSource.Touchpad;
            }
        }

        var baseline = NotchWheelClassifier.ClassifyDelta(delta);
        if (baseline == WheelSource.Unknown && delta != 0 && _rawInput.TouchpadTracking)
        {
            return WheelSource.MouseHighResolution;
        }

        return baseline;
    }

    public IDisposable Track()
    {
        lock (_gate)
        {
            if (_users++ == 0)
            {
                _hasTouchpad ??= SafeHasTouchpad();
                if (_hasTouchpad == true)
                {
                    _tracking = _rawInput.TrackTouchpad();
                }
            }
        }

        return new Releaser(() =>
        {
            lock (_gate)
            {
                if (--_users == 0)
                {
                    _tracking?.Dispose();
                    _tracking = null;
                    // Re-check for a touchpad next time (it may be plugged in later).
                    _hasTouchpad = null;
                }
            }
        });
    }

    private static bool SafeHasTouchpad()
    {
        try
        {
            return WindowsRawInput.HasPrecisionTouchpad();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
