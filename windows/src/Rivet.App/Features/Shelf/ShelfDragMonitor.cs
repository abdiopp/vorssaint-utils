// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia.Threading;
using Rivet.Core.Modules.Shelf;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Shelf;

/// <summary>
/// Global drag detection for the shelf (spec 07 §3.1.5–§3.1.9). Windows has no
/// "drag started" notification, so this watches the primary button through the
/// shared low-level mouse hook: a press followed by movement past the system
/// drag threshold is a <em>potential</em> drag; window moves and resizes are
/// excluded through the move/size WinEvents, and presses on the app's own
/// windows never count. The hook handler only copies the event and posts it to
/// the UI thread (it returns in microseconds and never swallows anything).
///
/// The shake opens the card speculatively (it is a deliberate gesture). The
/// dock pill and the edge peek additionally wait for a hint that content is
/// being dragged: the shell drag image that File Explorer, browsers and most
/// OLE drag sources show. Without it (a text selection, a slider, a drawing
/// stroke) nothing appears.
/// </summary>
public sealed class ShelfDragMonitor : IDisposable
{
    private readonly IInputHooks _hooks;
    private readonly IShelfPlatform _platform;
    private readonly IScreenService _screens;
    private readonly ShelfService _owner;
    private readonly IDisposable _subscription;
    private readonly IDisposable _moveSize;
    private readonly DragGestureTracker _tracker = new();
    private readonly ShakeDetector _shake = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _watchdog;
    private PixelPoint _pressPoint;
    private PixelPoint _lastPoint;
    private string? _sourceApp;
    private bool _contentHint;
    private bool _moveSizeActive;
    private volatile bool _buttonDown;
    private bool _disposed;

    public ShelfDragMonitor(IInputHooks hooks, IShelfPlatform platform, IScreenService screens, ShelfService owner)
    {
        _hooks = hooks;
        _platform = platform;
        _screens = screens;
        _owner = owner;
        var (x, y) = platform.DragThreshold;
        _tracker.ThresholdX = Math.Max(1, x);
        _tracker.ThresholdY = Math.Max(1, y);
        _watchdog = new DispatcherTimer { Interval = ShelfConstants.WatchdogInterval };
        _watchdog.Tick += (_, _) => Watchdog();
        _moveSize = platform.WatchMoveSize(active =>
        {
            _moveSizeActive = active;
            if (active)
            {
                _tracker.Cancel();
                _watchdog.Stop();
            }
        });
        _subscription = hooks.SubscribeMouse(OnMouse, priority: -10);
    }

    /// <summary>Pill → card dwell (150 ms inside the trigger frame).</summary>
    public DwellTracker<int> DockDwell { get; } = new(ShelfConstants.DockDwell);

    /// <summary>Edge peek dwell (150 ms on the same edge of the same screen).</summary>
    public DwellTracker<ShelfEdgeMatch> EdgeDwell { get; } = new(ShelfConstants.EdgeDwell);

    public DragGesturePhase Phase => _tracker.Phase;

    /// <summary>Hook thread: copy and post; never swallow.</summary>
    private bool OnMouse(ref MouseHookEvent e)
    {
        var swapped = _platform.MouseButtonsSwapped;
        var down = swapped ? MouseHookKind.RightDown : MouseHookKind.LeftDown;
        var up = swapped ? MouseHookKind.RightUp : MouseHookKind.LeftUp;
        var kind = e.Kind;
        if (kind == down)
        {
            _buttonDown = true;
            var point = e.Position;
            var own = _platform.IsOwnWindowAt(point);
            Dispatcher.UIThread.Post(() => Press(point, own));
        }
        else if (kind == up)
        {
            _buttonDown = false;
            Dispatcher.UIThread.Post(Release);
        }
        else if (kind == MouseHookKind.Move && _buttonDown)
        {
            var point = e.Position;
            Dispatcher.UIThread.Post(() => Move(point));
        }

        return false;
    }

    /// <summary>Feeds an event directly (tests, and the UI-thread side of the hook).</summary>
    public void Press(PixelPoint point, bool onOwnWindow)
    {
        if (_disposed)
        {
            return;
        }

        _pressPoint = point;
        _lastPoint = point;
        _sourceApp = null;
        _contentHint = false;
        _shake.Reset();
        DockDwell.Reset();
        EdgeDwell.Reset();
        _tracker.Press(point.X, point.Y, null, onOwnWindow || _owner.InternalDragActive);
    }

    public void Move(PixelPoint point)
    {
        if (_disposed || _moveSizeActive)
        {
            return;
        }

        _lastPoint = point;
        if (_tracker.Move(point.X, point.Y))
        {
            // Became a potential drag: resolve the source app once (cheap Win32 calls, off the hook thread).
            _sourceApp = SafeSourceApp(_pressPoint);
            _watchdog.Start();
        }

        if (!_tracker.IsAutomaticDrag)
        {
            return;
        }

        var now = _clock.Elapsed;
        if (!_contentHint && _platform.IsDragImageVisible())
        {
            _contentHint = true;
            _owner.OnDragStarted(_sourceApp);
        }

        // Shake thresholds are in DIPs; the hook reports physical pixels.
        var scale = _screens.ScreenFromPoint(point).Scale;
        if (_shake.Add(now, point.X / (scale <= 0 ? 1 : scale)))
        {
            _owner.OnShake(point, _sourceApp);
        }

        if (_contentHint)
        {
            _owner.OnDragMoved(point, _sourceApp, now);
        }
    }

    public void Release()
    {
        if (_disposed)
        {
            return;
        }

        _watchdog.Stop();
        var wasDragging = _tracker.Release();
        _shake.Reset();
        DockDwell.Reset();
        EdgeDwell.Reset();
        if (wasDragging)
        {
            _owner.OnDragEnded();
        }
    }

    /// <summary>
    /// Every 0.15 s during a drag: a swallowed button-up ends the gesture;
    /// otherwise the dwell timers advance even while the pointer rests.
    /// </summary>
    private void Watchdog()
    {
        if (_tracker.Phase != DragGesturePhase.Dragging)
        {
            _watchdog.Stop();
            return;
        }

        if (!_platform.IsPrimaryButtonDown())
        {
            _buttonDown = false;
            Release();
            return;
        }

        if (_tracker.IsAutomaticDrag)
        {
            if (!_contentHint && _platform.IsDragImageVisible())
            {
                _contentHint = true;
                _owner.OnDragStarted(_sourceApp);
            }

            if (_contentHint)
            {
                _owner.OnDragMoved(_lastPoint, _sourceApp, _clock.Elapsed);
            }
        }
    }

    private string? SafeSourceApp(PixelPoint point)
    {
        try
        {
            return _platform.ProcessPathAt(point);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog.Stop();
        _subscription.Dispose();
        _moveSize.Dispose();
    }
}
