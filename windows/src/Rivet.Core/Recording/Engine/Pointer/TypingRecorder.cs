// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Recording.Engine.Pointer;

/// <summary>
/// Keystroke times for the editor's "keep zoomed in while typing" (spec 02
/// §3.13). Only the time of each key-down is kept, never the key. The
/// low-level hook reports auto-repeat as more key-downs, so a key counts once
/// until its key-up. Modifier keys alone are not typing (macOS reports them
/// as flag changes, not key-downs).
/// </summary>
public sealed class TypingRecorder : IDisposable
{
    private readonly IInputHooks _hooks;
    private readonly PauseClock _clock;
    private readonly IHostClock _host;
    private readonly object _gate = new();
    private readonly HashSet<int> _down = [];
    private readonly List<double> _times = [];
    private IDisposable? _subscription;
    private bool _stopped;

    public TypingRecorder(IInputHooks hooks, PauseClock clock, IHostClock host)
    {
        _hooks = hooks;
        _clock = clock;
        _host = host;
    }

    public void Start() => _subscription = _hooks.SubscribeKeyboard(OnKey);

    /// <summary>Stops listening; returns the times in recording seconds, ascending.</summary>
    public IReadOnlyList<double> Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
        lock (_gate)
        {
            _stopped = true;
            _times.Sort();
            return _times.ToList();
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    public static bool IsModifier(int virtualKey) => VirtualKeys.IsModifier(virtualKey) || virtualKey == VirtualKeys.Capital;

    private bool OnKey(ref KeyboardHookEvent e)
    {
        var now = _host.Now;
        lock (_gate)
        {
            if (_stopped)
            {
                return false;
            }

            if (e.Action == KeyAction.Up)
            {
                _down.Remove(e.VirtualKey);
                return false;
            }

            if (!_down.Add(e.VirtualKey) || IsModifier(e.VirtualKey))
            {
                return false;
            }

            if (_clock.EventTime(now) is { } time)
            {
                _times.Add(time);
            }
        }

        return false;
    }
}
