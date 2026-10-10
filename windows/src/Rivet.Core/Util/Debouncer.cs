// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Util;

/// <summary>Runs an action once after calls stop arriving for <c>delay</c>.</summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly Action _action;
    private readonly Timer _timer;
    private int _disposed;

    public Debouncer(TimeSpan delay, Action action)
    {
        _delay = delay;
        _action = action;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Trigger()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Cancel() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void Fire()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _action();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
        }
    }
}
