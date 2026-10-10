// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Displays;

/// <summary>
/// The serial worker brightness work runs on: display enumeration, DDC/CI
/// and WMI calls (tens of milliseconds each) never touch the UI thread, and
/// commands to a monitor never overlap.
/// </summary>
public interface IBrightnessWorker : IDisposable
{
    /// <summary>Monotonic seconds.</summary>
    double Now { get; }

    void Post(Action work);

    /// <summary>Posts <paramref name="work"/> after <paramref name="delay"/>; dispose the result to cancel.</summary>
    IDisposable Schedule(TimeSpan delay, Action work);

    /// <summary>Waits on the worker (DDC pacing). Only call it from work items.</summary>
    void Pause(TimeSpan duration);
}

/// <summary>A dedicated background thread consuming a queue.</summary>
public sealed class SerialBrightnessWorker : IBrightnessWorker
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private readonly List<Timer> _timers = [];
    private int _disposed;

    public SerialBrightnessWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Brightness" };
        _thread.Start();
    }

    public double Now => _clock.Elapsed.TotalSeconds;

    public void Post(Action work)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _queue.Add(work);
        }
        catch (InvalidOperationException)
        {
            // Completed while shutting down.
        }
    }

    public IDisposable Schedule(TimeSpan delay, Action work)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            lock (_timers)
            {
                _timers.Remove(timer!);
            }

            timer!.Dispose();
            Post(work);
        });
        lock (_timers)
        {
            _timers.Add(timer);
        }

        timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        return new Cancel(() =>
        {
            lock (_timers)
            {
                _timers.Remove(timer);
            }

            timer.Dispose();
        });
    }

    public void Pause(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
        {
            Thread.Sleep(duration);
        }
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Error("brightness", "Brightness work failed.", ex);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_timers)
        {
            foreach (var timer in _timers)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }

        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class Cancel(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
