// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Util;

namespace Rivet.App.Features.Input;

public enum ButtonCaptureKind
{
    /// <summary>"Add a button or side wheel": side buttons and side-wheel directions.</summary>
    Shortcut,

    /// <summary>"Choose a button" for the desktop drag: side buttons only.</summary>
    DesktopGesture,
}

/// <summary>
/// Mouse button shortcuts, side-wheel shortcuts and the desktop drag (spec 07
/// §3.7.3). XBUTTON1/2 ("3"/"4") and horizontal-wheel directions ("-2"/"-1")
/// press one key combination on press; holding the drag button and dragging
/// switches virtual desktops (Ctrl+Win+Left/Right) or opens Task View
/// (Win+Tab). Ups and drags always follow their down's decision, moves are
/// never swallowed, and a press swallowed when the feature stops keeps a
/// small "drain" subscription alive until its up arrives, so an app never
/// receives a lone button-up (which would trigger Back/Forward).
/// </summary>
public sealed class MouseButtonShortcutsService : InputFeatureService
{
    public const int DrainTimeoutMs = 10_000;

    private readonly IAppIdentityResolver _resolver;
    private readonly IWheelDeviceClassifier _classifier;
    private readonly IScreenService? _screens;
    private readonly Func<IMouseButtonClaims?> _claims;
    private volatile Config _config = Config.Empty;
    private volatile CaptureSession? _capture;
    private volatile ScreenInfo[] _screenList = [];

    public MouseButtonShortcutsService(
        ISettingsStore settings,
        IInputHooks hooks,
        IInputClock clock,
        IAppIdentityResolver resolver,
        IWheelDeviceClassifier classifier,
        IScreenService? screens = null,
        Func<IMouseButtonClaims?>? claims = null,
        InputFixesControl? control = null)
        : base(settings, hooks, clock, control, suspendable: true)
    {
        _resolver = resolver;
        _classifier = classifier;
        _screens = screens;
        _claims = claims ?? (static () => null);
        Observe(InputSettings.MouseButtonShortcuts, InputSettings.DesktopGestureEnabled, InputSettings.DesktopGestureButton,
            InputSettings.DesktopGestureFollowsDrag, InputSettings.MouseButtonExceptions);
        if (_screens is not null)
        {
            _screens.ScreensChanged += OnScreensChanged;
            OnScreensChanged(null, EventArgs.Empty);
        }
    }

    /// <summary>True while a capture waits for a button.</summary>
    public bool IsCapturing => _capture is not null;

    /// <summary>
    /// Starts capturing: every extra-button press (and side-wheel direction for
    /// <see cref="ButtonCaptureKind.Shortcut"/>) is reported on the UI thread
    /// and consumed, so it neither navigates nor fires an old mapping. The
    /// middle button is reported (2) and passed. Returns null when the service
    /// cannot watch the mouse (feature not installed).
    /// </summary>
    public IDisposable? BeginCapture(ButtonCaptureKind kind, Action<int> onInput)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var session = new CaptureSession(kind, onInput);
        _capture = session;
        Refresh();
        return new CaptureToken(this, session);
    }

    public override void Dispose()
    {
        if (_screens is not null)
        {
            _screens.ScreensChanged -= OnScreensChanged;
        }

        base.Dispose();
    }

    protected override bool WantsRunning() =>
        _capture is not null
        || Settings.Get(FeatureKeys.MouseButtonShortcutsEnabled)
        || (Settings.Get(InputSettings.DesktopGestureEnabled) && Settings.Get(InputSettings.DesktopGestureButton) != 0);

    protected override void OnConfigure()
    {
        var shortcutsOn = Settings.Get(FeatureKeys.MouseButtonShortcutsEnabled);
        var mappings = new KeyChord[MouseButtonIds.LastExtra + 3];
        foreach (var (id, chord) in MouseButtonMappings.Decode(Settings.Get(InputSettings.MouseButtonShortcuts)))
        {
            mappings[id + 2] = chord;
        }

        var gestureButton = Settings.Get(InputSettings.DesktopGestureEnabled) ? Settings.Get(InputSettings.DesktopGestureButton) : 0;
        if (gestureButton != 0 && shortcutsOn && !mappings[gestureButton + 2].IsEmpty)
        {
            // A button with an active shortcut cannot also be the drag button.
            gestureButton = 0;
        }

        _config = new Config(shortcutsOn, mappings, gestureButton, Settings.Get(InputSettings.DesktopGestureFollowsDrag),
            new AppExclusionList(Settings.Get(InputSettings.MouseButtonExceptions)));
    }

    protected override IDisposable StartSession()
    {
        var session = new Session();
        var subscription = Hooks.SubscribeMouse((ref MouseHookEvent e) => Handle(session, ref e), InputPriorities.MouseButtonShortcuts);
        return new SessionHandle(session, new CompositeDisposable(_resolver.Track(), _classifier.Track(), subscription));
    }

    protected override void StopSession(IDisposable session)
    {
        session.Dispose();
        if (session is SessionHandle { Session: var state })
        {
            var pending = new List<int>();
            for (var id = MouseButtonIds.FirstExtra; id <= MouseButtonIds.LastExtra; id++)
            {
                if (state.Decisions[id] is ButtonDecision.Swallow or ButtonDecision.Drag)
                {
                    pending.Add(id);
                }
            }

            if (pending.Count > 0)
            {
                StartDrain(pending);
            }
        }
    }

    private bool Handle(Session s, ref MouseHookEvent e)
    {
        switch (e.Kind)
        {
            case MouseHookKind.Move:
                if (s.DragButton != 0)
                {
                    FeedDrag(s, e.Position);
                }

                if (!_config.Exceptions.IsEmpty)
                {
                    _resolver.PointerApp(e.Position);
                }

                return false;
            case MouseHookKind.XDown:
                return OnDown(s, MouseButtonIds.FromXButton(e.XButton), e.Position);
            case MouseHookKind.XUp:
                return OnUp(s, MouseButtonIds.FromXButton(e.XButton));
            case MouseHookKind.MiddleDown:
                if (_capture is { } capture)
                {
                    Report(capture, MouseButtonIds.Middle);
                }

                return false;
            case MouseHookKind.HorizontalWheel:
                return OnSideWheel(s, e.WheelDelta, e.Position);
            default:
                return false;
        }
    }

    private bool OnDown(Session s, int id, PixelPoint position)
    {
        if (id is < MouseButtonIds.FirstExtra or > MouseButtonIds.LastExtra)
        {
            return false;
        }

        if (_capture is { } capture)
        {
            Report(capture, id);
            s.Decisions[id] = ButtonDecision.Swallow;
            return true;
        }

        var config = _config;
        if (IsExcluded(config, position) || _claims()?.IsClaimed(id) == true)
        {
            s.Decisions[id] = ButtonDecision.Pass;
            return false;
        }

        if (config.GestureButton == id)
        {
            // Keep the press; it is replayed on release when nothing fired.
            s.Decisions[id] = ButtonDecision.Drag;
            s.DragButton = id;
            s.Tracker = new DesktopDragTracker(ScaleAt(position));
            s.LastPosition = position;
            s.DragFollows = config.FollowsDrag;
            return true;
        }

        var chord = config.ShortcutsOn ? config.Mappings[id + 2] : default;
        if (!chord.IsEmpty)
        {
            Send(chord);
            s.Decisions[id] = ButtonDecision.Swallow;
            return true;
        }

        s.Decisions[id] = ButtonDecision.Pass;
        return false;
    }

    private bool OnUp(Session s, int id)
    {
        if (id is < MouseButtonIds.FirstExtra or > MouseButtonIds.LastExtra)
        {
            return false;
        }

        var decision = s.Decisions[id];
        s.Decisions[id] = ButtonDecision.None;
        switch (decision)
        {
            case ButtonDecision.Swallow:
                return true;
            case ButtonDecision.Drag:
                if (s.DragButton == id)
                {
                    var fired = s.Tracker?.HasFired == true;
                    s.DragButton = 0;
                    s.Tracker = null;
                    var xButton = MouseButtonIds.ToXButton(id);
                    if (!fired && xButton != 0)
                    {
                        // A short click: the app sees one click at the release point.
                        Hooks.SendMouse(MouseHookKind.XDown, xButton);
                        Hooks.SendMouse(MouseHookKind.XUp, xButton);
                    }
                }

                return true;
            default:
                return false;
        }
    }

    private bool OnSideWheel(Session s, int delta, PixelPoint position)
    {
        if (delta == 0)
        {
            return false;
        }

        var now = Clock.NowNs;
        var source = _classifier.Classify(delta, now);
        if (source is WheelSource.Touchpad or WheelSource.Unknown)
        {
            return false;
        }

        // WM_MOUSEHWHEEL: positive is a tilt to the right (the opposite of AppKit).
        var direction = delta > 0 ? MouseButtonIds.SideWheelRight : MouseButtonIds.SideWheelLeft;
        if (_capture is { Kind: ButtonCaptureKind.Shortcut } capture)
        {
            Report(capture, direction);
            return true;
        }

        var config = _config;
        var chord = config.ShortcutsOn ? config.Mappings[direction + 2] : default;
        if (chord.IsEmpty || IsExcluded(config, position))
        {
            return false;
        }

        if (s.Gate.ShouldFire(direction, now))
        {
            Send(chord);
        }

        return true;
    }

    private void FeedDrag(Session s, PixelPoint position)
    {
        var dx = position.X - s.LastPosition.X;
        var dy = position.Y - s.LastPosition.Y;
        s.LastPosition = position;
        if (s.Tracker is null || (dx == 0 && dy == 0))
        {
            return;
        }

        var action = s.Tracker.Feed(dx, dy, Clock.NowNs);
        if (s.DragFollows)
        {
            action = action switch
            {
                DesktopDragAction.PreviousDesktop => DesktopDragAction.NextDesktop,
                DesktopDragAction.NextDesktop => DesktopDragAction.PreviousDesktop,
                _ => action,
            };
        }

        var chord = DesktopChord(action);
        if (!chord.IsEmpty)
        {
            Send(chord);
        }
    }

    /// <summary>The fixed Windows shell shortcuts for the drag actions (App Exposé has no equivalent).</summary>
    public static KeyChord DesktopChord(DesktopDragAction action) => action switch
    {
        DesktopDragAction.PreviousDesktop => new KeyChord(KeyModifiers.Control | KeyModifiers.Win, VirtualKeys.Left),
        DesktopDragAction.NextDesktop => new KeyChord(KeyModifiers.Control | KeyModifiers.Win, VirtualKeys.Right),
        DesktopDragAction.Overview => new KeyChord(KeyModifiers.Win, VirtualKeys.Tab),
        _ => KeyChord.None,
    };

    private bool IsExcluded(Config config, PixelPoint position)
    {
        if (config.Exceptions.IsEmpty)
        {
            return false;
        }

        // Unknown apps are left alone (spec 07 §3.7.8).
        var pointer = _resolver.PointerApp(position);
        if (pointer is null || config.Exceptions.Matches(pointer.Path))
        {
            return true;
        }

        var foreground = _resolver.ForegroundApp;
        return foreground is not null && config.Exceptions.Matches(foreground.Path);
    }

    private void Send(KeyChord chord) => Hooks.SendKeys(ChordStrokes.Exact(chord, Hooks.IsKeyDown));

    private double ScaleAt(PixelPoint point)
    {
        var screens = _screenList;
        foreach (var screen in screens)
        {
            if (screen.Bounds.Contains(point))
            {
                return screen.Scale;
            }
        }

        return screens.Length > 0 ? screens[0].Scale : 1.0;
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        try
        {
            _screenList = _screens?.Screens.ToArray() ?? [];
        }
        catch (Exception)
        {
            _screenList = [];
        }
    }

    private static void Report(CaptureSession capture, int id) => UiThread.Post(() => capture.OnInput(id));

    private void StartDrain(List<int> buttons)
    {
        var remaining = new HashSet<int>(buttons);
        var gate = new object();
        IDisposable? subscription = null;
        Timer? timeout = null;

        void Finish()
        {
            lock (gate)
            {
                subscription?.Dispose();
                subscription = null;
                timeout?.Dispose();
                timeout = null;
            }
        }

        subscription = Hooks.SubscribeMouse((ref MouseHookEvent e) =>
        {
            if (e.Kind != MouseHookKind.XUp)
            {
                return false;
            }

            var id = MouseButtonIds.FromXButton(e.XButton);
            bool swallow;
            bool done;
            lock (gate)
            {
                swallow = remaining.Remove(id);
                done = remaining.Count == 0;
            }

            if (done)
            {
                Finish();
            }

            return swallow;
        }, InputPriorities.MouseButtonShortcuts + 1);
        timeout = new Timer(_ => Finish(), null, DrainTimeoutMs, Timeout.Infinite);
    }

    private void EndCapture(CaptureSession session)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _capture, null, session), session))
        {
            Refresh();
        }
    }

    private enum ButtonDecision : byte
    {
        None,
        Pass,
        Swallow,
        Drag,
    }

    private sealed record Config(bool ShortcutsOn, KeyChord[] Mappings, int GestureButton, bool FollowsDrag, AppExclusionList Exceptions)
    {
        public static Config Empty { get; } = new(false, new KeyChord[MouseButtonIds.LastExtra + 3], 0, false, AppExclusionList.Empty);
    }

    private sealed class Session
    {
        public ButtonDecision[] Decisions { get; } = new ButtonDecision[MouseButtonIds.LastExtra + 1];

        public SideWheelGate Gate { get; } = new();

        public int DragButton { get; set; }

        public DesktopDragTracker? Tracker { get; set; }

        public PixelPoint LastPosition { get; set; }

        public bool DragFollows { get; set; }
    }

    private sealed class SessionHandle(Session session, IDisposable inner) : IDisposable
    {
        public Session Session { get; } = session;

        public void Dispose() => inner.Dispose();
    }

    private sealed record CaptureSession(ButtonCaptureKind Kind, Action<int> OnInput);

    private sealed class CaptureToken(MouseButtonShortcutsService owner, CaptureSession session) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndCapture(session);
            }
        }
    }
}
