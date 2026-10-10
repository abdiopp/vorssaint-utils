// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Sound;

/// <summary>
/// One write in flight, only the newest pending value kept (spec §3.10.3):
/// a slider drag produces dozens of values, the device sees only the latest.
/// Thread-safe.
/// </summary>
public sealed class LatestValueWriter<T>
{
    private readonly Func<T, Task> _write;
    private readonly object _gate = new();
    private bool _inFlight;
    private bool _hasPending;
    private T _pending = default!;

    public LatestValueWriter(Func<T, Task> write)
    {
        _write = write;
    }

    /// <summary>A write is running or waiting.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    public void Submit(T value)
    {
        lock (_gate)
        {
            if (_inFlight)
            {
                _pending = value;
                _hasPending = true;
                return;
            }

            _inFlight = true;
        }

        _ = RunAsync(value);
    }

    /// <summary>Forgets the queued value (the target changed); the write in flight still completes.</summary>
    public void DropPending()
    {
        lock (_gate)
        {
            _hasPending = false;
            _pending = default!;
        }
    }

    private async Task RunAsync(T value)
    {
        while (true)
        {
            try
            {
                await _write(value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("sound", "A coalesced volume write failed.", ex);
            }

            lock (_gate)
            {
                if (!_hasPending)
                {
                    _inFlight = false;
                    return;
                }

                value = _pending;
                _hasPending = false;
                _pending = default!;
            }
        }
    }
}
