// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Util;

namespace Rivet.Core.Clipboard;

/// <summary>Something that reacts to clipboard changes inside the shared lane transaction.</summary>
public interface IClipboardChangeHandler
{
    /// <summary>Lower runs first: the URL cleaner rewrites before the history reads (spec 06 §8.2 item 2).</summary>
    int Order { get; }

    /// <summary>Called on the clipboard thread with the counter seen when the transaction started.</summary>
    void OnClipboardChanged(IClipboardPlatform clipboard);
}

/// <summary>
/// Turns WM_CLIPBOARDUPDATE into one lane transaction per burst (debounced:
/// apps write in several steps) that runs every active handler in order, so
/// the history sees the final content of a copy the cleaner rewrote. Also
/// distributes the counters of the app's own writes so nobody records them.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(75);

    private readonly ClipboardLane _lane;
    private readonly List<IClipboardChangeHandler> _handlers = [];
    private readonly Debouncer _debouncer;
    private bool _listening;

    public ClipboardWatcher(ClipboardLane lane)
    {
        _lane = lane;
        _debouncer = new Debouncer(Debounce, () => _lane.Run(RunHandlers));
        _lane.Platform.Changed += OnPlatformChanged;
    }

    /// <summary>Raised on the UI thread with the counter an own write produced.</summary>
    public event Action<uint>? OwnWrite;

    public ClipboardLane Lane => _lane;

    /// <summary>Highest counter produced by this app's own writes so far.</summary>
    public uint LastOwnWrite { get; private set; }

    /// <summary>
    /// Set on the clipboard thread when the URL cleaner replaced a copy: the
    /// counter of the rewrite and the app that made the original copy, so the
    /// history records the cleaned link with the right source.
    /// </summary>
    public (uint Counter, string? OwnerApp)? LastRewrite { get; set; }

    public void Activate(IClipboardChangeHandler handler)
    {
        lock (_handlers)
        {
            if (!_handlers.Contains(handler))
            {
                _handlers.Add(handler);
                _handlers.Sort((a, b) => a.Order.CompareTo(b.Order));
            }
        }

        UpdateListening();
    }

    public void Deactivate(IClipboardChangeHandler handler)
    {
        lock (_handlers)
        {
            _handlers.Remove(handler);
        }

        UpdateListening();
    }

    /// <summary>Records the counter of a write this app made (history and cleaner skip it). Call on any thread.</summary>
    public void NoteOwnWrite(uint counter)
    {
        UiThread.Run(() =>
        {
            LastOwnWrite = Math.Max(LastOwnWrite, counter);
            OwnWrite?.Invoke(counter);
        });
    }

    /// <summary>Runs the handlers now (for example when the history panel opens or closes).</summary>
    public void CheckNow() => _lane.Run(RunHandlers);

    private void OnPlatformChanged(object? sender, EventArgs e) => _debouncer.Trigger();

    private void RunHandlers(IClipboardPlatform clipboard)
    {
        IClipboardChangeHandler[] handlers;
        lock (_handlers)
        {
            handlers = _handlers.ToArray();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler.OnClipboardChanged(clipboard);
            }
            catch (Exception ex)
            {
                Log.Error("clipboard", $"Clipboard handler {handler.GetType().Name} failed.", ex);
            }
        }
    }

    private void UpdateListening()
    {
        bool want;
        lock (_handlers)
        {
            want = _handlers.Count > 0;
        }

        if (want == _listening)
        {
            return;
        }

        _listening = want;
        if (want)
        {
            _lane.Platform.StartListening();
        }
        else
        {
            _lane.Platform.StopListening();
            _debouncer.Cancel();
        }
    }

    public void Dispose()
    {
        _lane.Platform.Changed -= OnPlatformChanged;
        _debouncer.Dispose();
        if (_listening)
        {
            _lane.Platform.StopListening();
        }
    }
}
