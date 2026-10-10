// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;

namespace Rivet.Core.Recording.Engine.Pointer;

/// <summary>
/// The pointer track (spec 02 §3.12): a dedicated thread samples position,
/// visibility and cursor shape at 125 Hz; button presses and releases of the
/// left and right buttons come from the shared low-level mouse hook with
/// exact timestamps. Everything is timed through the <see cref="PauseClock"/>
/// (events inside a pause are dropped). Shapes are read only when the cursor
/// handle changes, then deduplicated by content in the <see cref="CursorCatalog"/>.
/// </summary>
public sealed class PointerRecorder : IDisposable
{
    public const double SampleIntervalSeconds = 0.008;

    private readonly ICursorProbe _probe;
    private readonly IPointerRegion _region;
    private readonly PauseClock _clock;
    private readonly IHostClock _host;
    private readonly IRecorderSystem _system;
    private readonly IInputHooks _hooks;
    private readonly double _monitorScale;
    private readonly object _gate = new();
    private readonly List<PointerSample> _samples = new(125 * 60);
    private readonly List<PointerClick> _clicks = [];
    private readonly CursorCatalog _catalog = new();
    private Thread? _thread;
    private IDisposable? _mouse;
    private volatile bool _stop;
    private nint _lastHandle;
    private ushort _shapeIndex;
    private bool _loggedProbeFailure;
    private PointerTrack? _result;

    public PointerRecorder(ICursorProbe probe, IPointerRegion region, PauseClock clock, IHostClock host, IRecorderSystem system, IInputHooks hooks, double monitorScale)
    {
        _probe = probe;
        _region = region;
        _clock = clock;
        _host = host;
        _system = system;
        _hooks = hooks;
        _monitorScale = double.IsFinite(monitorScale) && monitorScale > 0 ? monitorScale : 1;
    }

    /// <summary>Starts sampling. <paramref name="startThread"/> = false lets tests drive <see cref="SampleOnce"/>.</summary>
    public void Start(bool startThread = true)
    {
        _mouse = _hooks.SubscribeMouse(OnMouse);
        if (startThread)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "RecorderPointer", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    /// <summary>Stops sampling and returns the track (idempotent).</summary>
    public PointerTrack Stop()
    {
        _mouse?.Dispose();
        _mouse = null;
        _stop = true;
        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        lock (_gate)
        {
            return _result ??= new PointerTrack
            {
                SystemScale = 1,
                DisplayScale = (float)_monitorScale,
                Samples = _samples.ToList(),
                Clicks = _clicks.OrderBy(c => c.Time).ToList(),
                Shapes = _catalog.ToPointerShapes(),
            };
        }
    }

    public void Dispose()
    {
        _mouse?.Dispose();
        _mouse = null;
        _stop = true;
    }

    /// <summary>Takes one sample now (the thread calls this every 8 ms).</summary>
    public void SampleOnce()
    {
        CursorReading reading;
        try
        {
            reading = _probe.Read();
        }
        catch (Exception ex)
        {
            LogProbeFailure(ex);
            return;
        }

        var now = _host.Now;
        if (reading.Handle != 0 && reading.Handle != _lastHandle)
        {
            _lastHandle = reading.Handle;
            UpdateShape(reading.Handle);
        }

        if (_clock.EventTime(now) is not { } time)
        {
            return;
        }

        (double X, double Y) point;
        try
        {
            point = _region.Normalize(reading.Position);
        }
        catch (Exception ex)
        {
            LogProbeFailure(ex);
            return;
        }

        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            return;
        }

        lock (_gate)
        {
            if (_result is null)
            {
                _samples.Add(new PointerSample((float)time, (float)point.X, (float)point.Y, _shapeIndex, reading.Visible));
            }
        }
    }

    private void UpdateShape(nint handle)
    {
        CursorShapeSnapshot? shape;
        try
        {
            shape = _probe.CaptureShape(handle, _monitorScale);
        }
        catch (Exception ex)
        {
            LogProbeFailure(ex);
            return;
        }

        if (shape is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_catalog.Add(shape) is { } index)
            {
                _shapeIndex = index;
            }
        }
    }

    private bool OnMouse(ref MouseHookEvent e)
    {
        bool? down = e.Kind switch
        {
            MouseHookKind.LeftDown or MouseHookKind.RightDown => true,
            MouseHookKind.LeftUp or MouseHookKind.RightUp => false,
            _ => null,
        };
        if (down is { } isDown && _clock.EventTime(_host.Now) is { } time)
        {
            lock (_gate)
            {
                if (_result is null)
                {
                    _clicks.Add(new PointerClick((float)time, isDown));
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Paced on the real performance counter (sleeping the rest of each 8 ms
    /// slot); the samples themselves carry the injected host clock's time.
    /// </summary>
    private void Run()
    {
        try
        {
            using var timer = _system.CreateTimer();
            var pace = System.Diagnostics.Stopwatch.StartNew();
            var next = 0.0;
            while (!_stop)
            {
                SampleOnce();
                next += SampleIntervalSeconds;
                var wait = next - pace.Elapsed.TotalSeconds;
                if (wait < -0.25)
                {
                    // Fell far behind (suspend, debugger): resynchronize instead of bursting.
                    next = pace.Elapsed.TotalSeconds;
                }
                else if (wait > 0)
                {
                    timer.Wait(TimeSpan.FromSeconds(wait));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("recorder", "Pointer sampler stopped.", ex);
        }
    }

    private void LogProbeFailure(Exception ex)
    {
        if (!_loggedProbeFailure)
        {
            _loggedProbeFailure = true;
            Log.Warn("recorder", "Reading the pointer failed.", ex);
        }
    }
}
