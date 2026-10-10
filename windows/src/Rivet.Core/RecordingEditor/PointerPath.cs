// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;

namespace Rivet.Core.RecordingEditor;

/// <summary>
/// The pointer as it is drawn, one value per source frame (spec 02 §6.8–6.9):
/// resampled onto the frame grid, zero-phase smoothed, anchored back onto
/// the real position around clicks, raw while the mouse is being shaken, and
/// faded out while it rests.
/// </summary>
public sealed class PointerPath
{
    public const double LightCutoff = 4.0;
    public const double SmoothCutoff = 2.0;
    public const double CinematicCutoff = 1.2;

    /// <summary>1/√(√2 − 1): compensates the squared response of the forward and backward passes.</summary>
    public const double FiltFiltWidening = 1.553773974;

    public const double AnchorWindow = 0.12;
    public const double ShakeTravel = 0.06;
    public const int ShakeReversals = 3;
    public const double IdleAfter = 2.5;
    public const double FadeOut = 0.4;
    public const double FadeIn = 0.25;
    public const double StillTravel = 0.0006;

    private PointerPath(int fps, float[] x, float[] y, ushort[] shape, bool[] visible, float[] opacity, bool hasSamples)
    {
        Fps = fps;
        X = x;
        Y = y;
        Shape = shape;
        Visible = visible;
        Opacity = opacity;
        HasSamples = hasSamples;
    }

    public int Fps { get; }

    public float[] X { get; }

    public float[] Y { get; }

    public ushort[] Shape { get; }

    public bool[] Visible { get; }

    /// <summary>Idle-fade opacity, 0…1.</summary>
    public float[] Opacity { get; }

    public bool HasSamples { get; }

    public int Count => X.Length;

    /// <summary>Source frame index for a source time (clamped).</summary>
    public int IndexOf(double sourceTime) => Math.Clamp(RecorderMath.RoundToInt(sourceTime * Fps), 0, Count - 1);

    public static int FrameCount(double duration, int fps) =>
        Math.Max(1, RecorderMath.RoundToInt(Math.Max(0, duration) * fps));

    /// <summary>
    /// Builds the drawn path. With <paramref name="drawn"/> false (pointer off)
    /// the raw resampled path is kept: it still feeds the camera.
    /// </summary>
    public static PointerPath Build(PointerTrack track, double duration, int fps, PointerSmoothing smoothing, bool drawn)
    {
        var n = FrameCount(duration, fps);
        var (rawX, rawY, shape, visible) = Resample(track.Samples, n, fps);
        var x = (float[])rawX.Clone();
        var y = (float[])rawY.Clone();
        if (drawn && track.Samples.Count > 1 && CutoffOf(smoothing) is { } cutoff)
        {
            var alpha = OnePoleAlpha(cutoff * FiltFiltWidening, 1.0 / fps);
            ZeroPhase(x, alpha);
            ZeroPhase(y, alpha);

            var clicks = new ClickTimeline(track.Clicks);
            var cursor = clicks.CreateCursor();
            for (var i = 0; i < n; i++)
            {
                var w = cursor.AnchorWeight(i / (double)fps);
                if (w > 0)
                {
                    x[i] += (float)((rawX[i] - x[i]) * w);
                    y[i] += (float)((rawY[i] - y[i]) * w);
                }
            }

            var shaking = DetectShake(rawX, rawY, fps);
            for (var i = 0; i < n; i++)
            {
                if (shaking[i])
                {
                    x[i] = rawX[i];
                    y[i] = rawY[i];
                }
            }
        }

        return new PointerPath(fps, x, y, shape, visible, IdleOpacity(x, y, fps), track.Samples.Count > 0);
    }

    public static double? CutoffOf(PointerSmoothing smoothing) => smoothing switch
    {
        PointerSmoothing.Light => LightCutoff,
        PointerSmoothing.Smooth => SmoothCutoff,
        PointerSmoothing.Cinematic => CinematicCutoff,
        _ => null,
    };

    /// <summary>Linear in time between neighbouring samples; shape and visibility step with the previous sample.</summary>
    public static (float[] X, float[] Y, ushort[] Shape, bool[] Visible) Resample(IReadOnlyList<PointerSample> samples, int n, int fps)
    {
        var x = new float[n];
        var y = new float[n];
        var shape = new ushort[n];
        var visible = new bool[n];
        if (samples.Count == 0)
        {
            Array.Fill(visible, true);
            return (x, y, shape, visible);
        }

        var sorted = samples;
        for (var k = 1; k < samples.Count; k++)
        {
            if (samples[k].Time < samples[k - 1].Time)
            {
                sorted = samples.OrderBy(s => s.Time).ToList();
                break;
            }
        }

        var i = 0;
        for (var j = 0; j < n; j++)
        {
            var t = j / (double)fps;
            while (i + 1 < sorted.Count && sorted[i + 1].Time <= t)
            {
                i++;
            }

            var a = sorted[i];
            if (t <= sorted[0].Time)
            {
                x[j] = sorted[0].X;
                y[j] = sorted[0].Y;
                shape[j] = sorted[0].ShapeIndex;
                visible[j] = sorted[0].Visible;
                continue;
            }

            shape[j] = a.ShapeIndex;
            visible[j] = a.Visible;
            if (i + 1 < sorted.Count)
            {
                var b = sorted[i + 1];
                var span = b.Time - a.Time;
                var f = span > 0 ? (t - a.Time) / span : 0;
                x[j] = (float)(a.X + ((b.X - a.X) * f));
                y[j] = (float)(a.Y + ((b.Y - a.Y) * f));
            }
            else
            {
                x[j] = a.X;
                y[j] = a.Y;
            }
        }

        return (x, y, shape, visible);
    }

    /// <summary><c>1 − exp(−2π · cutoff · dt)</c>.</summary>
    public static double OnePoleAlpha(double cutoff, double dt) => 1 - Math.Exp(-2 * Math.PI * cutoff * dt);

    /// <summary>
    /// One-pole low-pass run forward then backward over the series padded with
    /// its end values: no lag, no overshoot, ends untouched.
    /// </summary>
    public static void ZeroPhase(float[] values, double alpha)
    {
        var n = values.Length;
        if (n == 0 || alpha <= 0 || alpha >= 1)
        {
            return;
        }

        var pad = Math.Min(n, Math.Max(1, RecorderMath.RoundToInt(3 / alpha)));
        var buffer = new double[n + (2 * pad)];
        for (var k = 0; k < pad; k++)
        {
            buffer[k] = values[0];
            buffer[pad + n + k] = values[n - 1];
        }

        for (var k = 0; k < n; k++)
        {
            buffer[pad + k] = values[k];
        }

        var v = buffer[0];
        for (var k = 0; k < buffer.Length; k++)
        {
            v += alpha * (buffer[k] - v);
            buffer[k] = v;
        }

        v = buffer[^1];
        for (var k = buffer.Length - 1; k >= 0; k--)
        {
            v += alpha * (buffer[k] - v);
            buffer[k] = v;
        }

        for (var k = 0; k < n; k++)
        {
            values[k] = (float)buffer[pad + k];
        }
    }

    /// <summary>Shaking the mouse to find it is a gesture: such frames use the raw position.</summary>
    public static bool[] DetectShake(float[] x, float[] y, int fps)
    {
        var n = x.Length;
        var result = new bool[n];
        var span = Math.Max(2, RecorderMath.RoundToInt(0.1 * fps));
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - (span / 2));
            var hi = Math.Min(n - 1, i - (span / 2) + span);
            if (hi - lo < 3)
            {
                continue;
            }

            var travel = 0.0;
            var reversals = 0;
            var lastSign = 0;
            for (var k = lo + 1; k <= hi; k++)
            {
                var dx = x[k] - x[k - 1];
                var dy = y[k] - y[k - 1];
                travel += Math.Sqrt((dx * dx) + (dy * dy));
                var sign = Math.Sign(dx);
                if (sign != 0)
                {
                    if (lastSign != 0 && sign != lastSign)
                    {
                        reversals++;
                    }

                    lastSign = sign;
                }
            }

            result[i] = travel > ShakeTravel && reversals >= ShakeReversals;
        }

        return result;
    }

    /// <summary>
    /// §6.9: fade out after 2.5 s still (over 0.4 s) and back in over the
    /// 0.25 s before the next movement, so the pointer is visible when it moves.
    /// </summary>
    public static float[] IdleOpacity(float[] x, float[] y, int fps)
    {
        var n = x.Length;
        var opacity = new float[n];
        if (n == 0)
        {
            return opacity;
        }

        var frame = 1.0 / fps;
        var stillFor = new double[n];
        for (var i = 1; i < n; i++)
        {
            var dx = x[i] - x[i - 1];
            var dy = y[i] - y[i - 1];
            stillFor[i] = Math.Sqrt((dx * dx) + (dy * dy)) > StillTravel ? 0 : stillFor[i - 1] + frame;
        }

        var untilMoves = double.PositiveInfinity;
        for (var i = n - 1; i >= 0; i--)
        {
            if (i < n - 1)
            {
                untilMoves = stillFor[i + 1] == 0 ? 0 : untilMoves + frame;
            }

            var waking = untilMoves <= FadeIn ? 1 - (untilMoves / FadeIn) : 0;
            var sleeping = stillFor[i] <= IdleAfter ? 0 : Math.Min(1, (stillFor[i] - IdleAfter) / FadeOut);
            opacity[i] = (float)(1 - RecorderMath.Smoothstep(Math.Max(0, sleeping - waking)));
        }

        return opacity;
    }
}

/// <summary>
/// Presses and releases for the click effects (§6.8 anchoring, §6.10 punch
/// and ring). Left and right buttons count alike.
/// </summary>
public sealed class ClickTimeline
{
    public const double PunchWindow = 0.13;
    public const double PunchScale = 0.8;
    public const double RingDuration = 0.4;
    public const double RingRadius = 22;

    public ClickTimeline(IEnumerable<PointerClick> clicks)
    {
        var sorted = clicks.Where(c => float.IsFinite(c.Time)).OrderBy(c => c.Time).ToList();
        Events = sorted.Select(c => (double)c.Time).ToArray();
        var presses = new List<(double Down, double Up)>();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!sorted[i].IsDown)
            {
                continue;
            }

            var up = double.PositiveInfinity;
            for (var k = i + 1; k < sorted.Count; k++)
            {
                if (!sorted[k].IsDown)
                {
                    up = sorted[k].Time;
                    break;
                }
            }

            presses.Add((sorted[i].Time, up));
        }

        Presses = presses.ToArray();
    }

    /// <summary>Every press and release time, ascending.</summary>
    public double[] Events { get; }

    /// <summary>Press times with their release (∞ when never released), ascending by press.</summary>
    public (double Down, double Up)[] Presses { get; }

    public IEnumerable<double> PressTimes => Presses.Select(p => p.Down);

    public static double PunchWeight((double Down, double Up) press, double t)
    {
        var (d, r) = press;
        if (t >= d - PunchWindow && t <= d)
        {
            return RecorderMath.Smoothstep((t - (d - PunchWindow)) / PunchWindow);
        }

        if (t >= d && t <= r)
        {
            return 1;
        }

        if (t > r && t <= r + PunchWindow)
        {
            return RecorderMath.Smoothstep(1 - ((t - r) / PunchWindow));
        }

        return 0;
    }

    /// <summary>Reference implementation (full scan): pointer scale from the press "punch".</summary>
    public double PressScale(double t)
    {
        var weight = 0.0;
        foreach (var press in Presses)
        {
            weight = Math.Max(weight, PunchWeight(press, t));
        }

        return 1 - ((1 - PunchScale) * Math.Min(1, weight));
    }

    /// <summary>Reference implementation: the newest ring's progress 0…1, or null.</summary>
    public double? RingProgress(double t)
    {
        double? best = null;
        foreach (var (d, _) in Presses)
        {
            var dt = t - d;
            if (dt >= 0 && dt <= RingDuration)
            {
                var p = dt / RingDuration;
                best = best is null ? p : Math.Min(best.Value, p);
            }
        }

        return best;
    }

    /// <summary>Reference implementation: how much of the raw position to use near clicks (0…1).</summary>
    public double AnchorWeight(double t)
    {
        foreach (var (d, r) in Presses)
        {
            if (t >= d && t <= r)
            {
                return 1;
            }
        }

        var w = 0.0;
        foreach (var e in Events)
        {
            var distance = Math.Abs(t - e);
            if (distance <= PointerPath.AnchorWindow)
            {
                w = Math.Max(w, RecorderMath.Smoothstep(1 - (distance / PointerPath.AnchorWindow)));
            }
        }

        return w;
    }

    public Cursor CreateCursor() => new(this);

    /// <summary>
    /// Walks the clicks with a carried position for ascending times: same
    /// results as the full scans, without rescanning every click per frame.
    /// Times must not decrease between calls.
    /// </summary>
    public sealed class Cursor
    {
        private readonly ClickTimeline _owner;
        private readonly List<int> _punchActive = [];
        private readonly List<int> _anchorActive = [];
        private int _punchNext;
        private int _ringFirst;
        private int _ringNext;
        private int _anchorNext;
        private int _eventFirst;
        private int _eventNext;

        internal Cursor(ClickTimeline owner) => _owner = owner;

        public double PressScale(double t)
        {
            var presses = _owner.Presses;
            while (_punchNext < presses.Length && presses[_punchNext].Down - PunchWindow <= t)
            {
                _punchActive.Add(_punchNext++);
            }

            _punchActive.RemoveAll(i => presses[i].Up + PunchWindow < t);
            var weight = 0.0;
            foreach (var i in _punchActive)
            {
                weight = Math.Max(weight, PunchWeight(presses[i], t));
            }

            return 1 - ((1 - PunchScale) * Math.Min(1, weight));
        }

        public double? RingProgress(double t)
        {
            var presses = _owner.Presses;
            while (_ringNext < presses.Length && presses[_ringNext].Down <= t)
            {
                _ringNext++;
            }

            while (_ringFirst < _ringNext && t - presses[_ringFirst].Down > RingDuration)
            {
                _ringFirst++;
            }

            double? best = null;
            for (var i = _ringFirst; i < _ringNext; i++)
            {
                var dt = t - presses[i].Down;
                if (dt >= 0 && dt <= RingDuration)
                {
                    var p = dt / RingDuration;
                    best = best is null ? p : Math.Min(best.Value, p);
                }
            }

            return best;
        }

        public double AnchorWeight(double t)
        {
            var presses = _owner.Presses;
            while (_anchorNext < presses.Length && presses[_anchorNext].Down <= t)
            {
                _anchorActive.Add(_anchorNext++);
            }

            _anchorActive.RemoveAll(i => presses[i].Up < t);
            foreach (var i in _anchorActive)
            {
                if (t >= presses[i].Down && t <= presses[i].Up)
                {
                    return 1;
                }
            }

            var events = _owner.Events;
            while (_eventNext < events.Length && events[_eventNext] - PointerPath.AnchorWindow <= t)
            {
                _eventNext++;
            }

            while (_eventFirst < _eventNext && t - events[_eventFirst] > PointerPath.AnchorWindow)
            {
                _eventFirst++;
            }

            var w = 0.0;
            for (var i = _eventFirst; i < _eventNext; i++)
            {
                var distance = Math.Abs(t - events[i]);
                if (distance <= PointerPath.AnchorWindow)
                {
                    w = Math.Max(w, RecorderMath.Smoothstep(1 - (distance / PointerPath.AnchorWindow)));
                }
            }

            return w;
        }
    }
}
