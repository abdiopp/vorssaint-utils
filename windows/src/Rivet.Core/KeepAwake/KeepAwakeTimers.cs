// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Util;

namespace Rivet.Core.Awake;

/// <summary>Wall clock and thread-pool timers whose callbacks run on the UI thread.</summary>
public sealed class SystemKeepAwakeTimers : IKeepAwakeTimers
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public IDisposable Schedule(TimeSpan dueIn, Action callback)
    {
        var handle = new Handle();
        handle.Timer = new Timer(_ =>
        {
            if (!handle.Cancelled)
            {
                UiThread.Post(() =>
                {
                    if (!handle.Cancelled)
                    {
                        callback();
                    }
                });
            }
        }, null, dueIn < TimeSpan.Zero ? TimeSpan.Zero : dueIn, Timeout.InfiniteTimeSpan);
        return handle;
    }

    private sealed class Handle : IDisposable
    {
        public Timer? Timer { get; set; }

        public volatile bool Cancelled;

        public void Dispose()
        {
            Cancelled = true;
            Timer?.Dispose();
        }
    }
}
