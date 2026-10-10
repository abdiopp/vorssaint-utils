// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Util;

namespace Rivet.Core.Clipboard;

/// <summary>
/// The serial clipboard worker (spec 06 §3.1.1). Three call shapes: fire and
/// forget; work then a result on the UI thread; and work with a deadline,
/// where <c>then</c> gets a timed-out result if the deadline passes first,
/// queued work that only starts after the deadline is skipped, <c>then</c> runs
/// at most once and <c>didFinish</c> always runs when the work really ends.
/// Clipboard contents are never logged.
/// </summary>
public sealed class ClipboardLane(IClipboardPlatform platform)
{
    public IClipboardPlatform Platform { get; } = platform;

    /// <summary>Fire and forget.</summary>
    public void Run(Action<IClipboardPlatform> work) =>
        Platform.Post(() =>
        {
            try
            {
                work(Platform);
            }
            catch (Exception ex)
            {
                Log.Error("clipboard", "Clipboard work failed.", ex);
            }
        });

    /// <summary>Runs <paramref name="work"/> on the lane and delivers the result on the UI thread.</summary>
    public void Run<T>(Func<IClipboardPlatform, T> work, Action<T> then) =>
        Platform.Post(() =>
        {
            T result;
            try
            {
                result = work(Platform);
            }
            catch (Exception ex)
            {
                Log.Error("clipboard", "Clipboard work failed.", ex);
                return;
            }

            UiThread.Post(() => then(result));
        });

    /// <summary>
    /// Work with a deadline. <paramref name="then"/> receives (result, true) when
    /// the work finished in time, or (default, false) on timeout or failure.
    /// <paramref name="didFinish"/> runs on the UI thread when the work really
    /// ended (or was skipped), even after a timeout.
    /// </summary>
    public void Run<T>(TimeSpan timeout, Func<IClipboardPlatform, Func<bool>, T> work, Action<T?, bool> then, Action<T?, bool>? didFinish = null)
    {
        var state = new DeadlineState();
        var timer = new Timer(_ =>
        {
            Volatile.Write(ref state.Expired, 1);
            if (Interlocked.Exchange(ref state.Delivered, 1) == 0)
            {
                UiThread.Post(() => then(default, false));
            }
        }, null, timeout, Timeout.InfiniteTimeSpan);

        Platform.Post(() =>
        {
            if (Volatile.Read(ref state.Expired) != 0)
            {
                timer.Dispose();
                if (didFinish is not null)
                {
                    UiThread.Post(() => didFinish(default, false));
                }

                return;
            }

            T? result = default;
            var ok = false;
            try
            {
                result = work(Platform, () => Volatile.Read(ref state.Expired) != 0);
                ok = true;
            }
            catch (Exception ex)
            {
                Log.Error("clipboard", "Clipboard work failed.", ex);
            }

            timer.Dispose();
            var deliver = Interlocked.Exchange(ref state.Delivered, 1) == 0;
            UiThread.Post(() =>
            {
                if (deliver)
                {
                    then(result, ok);
                }

                didFinish?.Invoke(result, ok);
            });
        });
    }

    /// <summary>Awaitable form of the deadline call; completes with (default, false) on timeout.</summary>
    public Task<(T? Value, bool Completed)> RunAsync<T>(TimeSpan timeout, Func<IClipboardPlatform, T> work)
    {
        var completion = new TaskCompletionSource<(T?, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run<T>(timeout, (p, _) => work(p), (value, ok) => completion.TrySetResult((value, ok)));
        return completion.Task;
    }

    private sealed class DeadlineState
    {
        public int Expired;
        public int Delivered;
    }
}
