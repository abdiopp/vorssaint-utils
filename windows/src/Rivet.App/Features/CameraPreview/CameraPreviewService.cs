// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Shell;
using Rivet.Core.Features;
using Rivet.Core.Modules.CameraPreview;
using Rivet.Core.Modules.CleaningMode;
using Rivet.Core.Modules.Scratchpad;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.CameraPreview;

/// <summary>
/// Show, hide and dismissal for the camera mirror (spec 07 §3.5.2–3.5.8).
/// The camera runs only while the window is shown. Dismissal: Esc, a mouse
/// press outside the frame (enlarged by 2 DIP, except on the on-screen
/// keyboard) seen by the shared mouse hook, another window taking the
/// foreground after a 1 s grace (the launching surface hands focus back right
/// after the show), a session lock or user switch, the hotkey, and uninstall.
/// </summary>
public sealed class CameraPreviewService : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IInputHooks _hooks;
    private readonly IScreenService _screens;
    private readonly ICameraService _cameras;
    private readonly IScratchpadPlatform? _windowFacts;
    private readonly ICleaningPlatform? _session;
    private CameraPreviewWindow? _window;
    private IDisposable? _outsideClick;
    private IDisposable? _foregroundWatch;
    private IDisposable? _sessionWatch;
    private DateTime _graceUntil;
    private bool _lastDenied;

    public CameraPreviewService(IServiceProvider services)
    {
        _services = services;
        _hooks = services.GetRequiredService<IInputHooks>();
        _screens = services.GetRequiredService<IScreenService>();
        _cameras = services.GetRequiredService<ICameraService>();
        _windowFacts = services.GetService<IScratchpadPlatform>();
        _session = services.GetService<ICleaningPlatform>();
        Controller = new CameraPreviewController(_cameras, services.GetRequiredService<ISettingsStore>());
        // The controller raises these on the UI thread through UiThread; marshal anyway so the
        // window is never touched from a capture thread (UiThread runs inline until configured).
        Controller.StateChanged += (_, _) => OnUi(OnStateChanged);
        Controller.FrameReady += (_, _) => OnUi(OnFrameReady);
    }

    public CameraPreviewController Controller { get; }

    public CameraPreviewWindow? Window => _window;

    public bool IsVisible => _window?.IsVisible == true;

    /// <summary>Camera access is off (from the last start, or the privacy switches): the tile and Settings say so.</summary>
    public bool AccessDenied => _lastDenied || _cameras.Access == CameraAccess.Denied;

    public event EventHandler? AccessChanged;

    /// <summary>The hotkey: hide if shown, else show.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide(CameraHideReason.Hotkey);
        }
        else
        {
            Show();
        }
    }

    /// <summary>Not a toggle: does nothing while shown.</summary>
    public void Show()
    {
        if (IsVisible || !_services.GetRequiredService<FeatureRuntime>().IsAvailable(FeatureIds.CameraPreview))
        {
            return;
        }

        _window ??= CreateWindow();
        var reduceMotion = _services.GetService<IThemeService>()?.ReduceMotion == true;
        _window.ReduceMotion = reduceMotion;
        var screen = _screens.ScreenFromPoint(_screens.CursorPosition);
        _window.ShowState(CameraPreviewState.Starting);
        _window.Place(screen);
        _services.GetService<IFocusHandoff>()?.Remember();
        _window.Opacity = reduceMotion ? 1 : 0;
        _window.Show();

        // The real scaling is known once shown (mixed-DPI monitors).
        _window.Place(screen);
        _window.Activate();
        _services.GetService<IWindowChrome>()?.BringToFront(WindowInterop.Handle(_window));
        _window.Focus();
        if (!reduceMotion)
        {
            FadeIn(_window);
        }

        _graceUntil = DateTime.UtcNow + CameraPreviewLayout.PermissionGrace;
        _outsideClick = _hooks.SubscribeMouse(OnGlobalMouse);
        _foregroundWatch = _session?.WatchForeground(OnForegroundChanged);
        _sessionWatch = _session?.WatchSession(change =>
        {
            if (change is SessionChange.Locked or SessionChange.Disconnected)
            {
                Hide(CameraHideReason.Session);
            }
        });
        Controller.Start();
    }

    /// <summary>Stops the camera at once (no fade-out) and removes every monitor.</summary>
    public void Hide(CameraHideReason reason)
    {
        _outsideClick?.Dispose();
        _outsideClick = null;
        _foregroundWatch?.Dispose();
        _foregroundWatch = null;
        _sessionWatch?.Dispose();
        _sessionWatch = null;
        Controller.Stop();
        if (_window is not { IsVisible: true } window)
        {
            return;
        }

        window.SetPointerInside(false);
        window.Hide();

        // Only a deliberate close hands focus back; a click or app switch already chose the next window.
        if (reason is CameraHideReason.Escape or CameraHideReason.Hotkey)
        {
            _services.GetService<IFocusHandoff>()?.Restore();
        }
    }

    public void OpenPrivacySettings()
    {
        var uri = Controller.PrivacySettingsUri;
        Hide(CameraHideReason.AppSwitch);
        _services.GetRequiredService<IShellService>().OpenSystemSettings(uri);
    }

    public void Dispose()
    {
        Hide(CameraHideReason.Uninstall);
        Controller.Dispose();
    }

    private CameraPreviewWindow CreateWindow()
    {
        var window = new CameraPreviewWindow(this);
        window.Closing += (_, e) =>
        {
            if (!e.IsProgrammatic)
            {
                e.Cancel = true;
                Hide(CameraHideReason.Escape);
            }
        };

        // A window closed from outside (shutdown, a test harness) cannot be shown again: build a new one next time.
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                Hide(CameraHideReason.Uninstall);
                _window = null;
            }
        };
        return window;
    }

    private void OnStateChanged()
    {
        var denied = Controller.State == CameraPreviewState.Denied;
        if (denied != _lastDenied && Controller.State is CameraPreviewState.Denied or CameraPreviewState.Running)
        {
            _lastDenied = denied;
            AccessChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_window is { IsVisible: true } window && Controller.IsShown)
        {
            window.ShowState(Controller.State);
        }
    }

    private void OnFrameReady()
    {
        if (Controller.TakeFrame() is { } frame && _window is { IsVisible: true } window)
        {
            window.ShowFrame(frame);
        }
    }

    private bool OnGlobalMouse(ref MouseHookEvent e)
    {
        if (e.Kind is not (MouseHookKind.LeftDown or MouseHookKind.RightDown or MouseHookKind.MiddleDown or MouseHookKind.XDown))
        {
            return false;
        }

        var point = e.Position;
        Dispatcher.UIThread.Post(() => OnMouseDownAnywhere(point));
        return false;
    }

    private void OnMouseDownAnywhere(PixelPoint point)
    {
        if (_window is not { IsVisible: true } window || Controller.State == CameraPreviewState.WaitingPermission)
        {
            return;
        }

        var frame = window.FrameRect();
        var tolerance = (int)Math.Ceiling(CameraPreviewLayout.OutsideClickTolerance * (window.RenderScaling <= 0 ? 1 : window.RenderScaling));
        var inside = point.X >= frame.X - tolerance && point.X <= frame.Right + tolerance
                     && point.Y >= frame.Y - tolerance && point.Y <= frame.Bottom + tolerance;
        if (inside || window.IsMenuOpen() || _windowFacts?.IsOnScreenKeyboardAt(point) == true)
        {
            return;
        }

        Hide(CameraHideReason.OutsideClick);
    }

    private void OnForegroundChanged(nint hwnd)
    {
        if (_window is not { IsVisible: true } window || hwnd == 0 || DateTime.UtcNow < _graceUntil
            || Controller.State == CameraPreviewState.WaitingPermission)
        {
            return;
        }

        if (hwnd == WindowInterop.Handle(window) || window.IsMenuOpen())
        {
            return;
        }

        Hide(CameraHideReason.AppSwitch);
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private static void FadeIn(CameraPreviewWindow window)
    {
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - started).TotalSeconds / CameraPreviewLayout.FadeIn.TotalSeconds;
            window.Opacity = Math.Min(1, t);
            if (t >= 1 || !window.IsVisible)
            {
                window.Opacity = 1;
                timer.Stop();
            }
        };
        timer.Start();
    }
}
