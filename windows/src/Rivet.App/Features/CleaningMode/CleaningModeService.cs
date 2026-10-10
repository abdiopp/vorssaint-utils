// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.App.Shell;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Modules.CleaningMode;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.CleaningMode;

/// <summary>
/// Runs Cleaning Mode: installs the keyboard filter first (through the
/// shared hooks, highest priority), then shows a black screen per monitor (or
/// the corner indicators), follows display changes and the live mode
/// setting, keeps the black overlay in front, ends on a user switch or lock,
/// and always removes the filter first on every exit path — including app
/// shutdown (Dispose). The filter itself also releases the keyboard if the UI
/// stops answering, and Ctrl+Alt+Del always works.
/// </summary>
public sealed class CleaningModeService : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly IScreenService _screens;
    private readonly ICleaningPlatform? _platform;
    private readonly IHud _hud;
    private readonly List<CleaningOverlayWindow> _overlays = [];
    private readonly List<CleaningIndicatorWindow> _indicators = [];
    private DispatcherTimer? _timer;
    private IDisposable? _session;
    private IDisposable? _foreground;
    private IDisposable? _modeObserver;
    private bool _keepVisible;

    public CleaningModeService(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _screens = services.GetRequiredService<IScreenService>();
        _platform = services.GetService<ICleaningPlatform>();
        _hud = services.GetRequiredService<IHud>();
        Controller = new CleaningModeController(services.GetRequiredService<IInputHooks>(), _platform);
        Controller.ProgressChanged += (_, progress) => SetProgress(progress);
        Controller.Ended += (_, reason) => OnEnded(reason);
        Controller.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on the UI thread when the lock starts, waits or ends (other features pause meanwhile).</summary>
    public event EventHandler? StateChanged;

    public CleaningModeController Controller { get; }

    public bool IsActive => Controller.IsActive;

    public IReadOnlyList<Window> Windows => [.. _overlays, .. _indicators];

    /// <summary>Lock the keyboard now (no-op when already locked).</summary>
    public void Activate()
    {
        if (Controller.IsActive)
        {
            return;
        }

        if (!Controller.TryActivate())
        {
            _hud.Show(L.Get("win.cleaningMode.couldNotLock"), HudStyle.Error, "Keyboard");
            return;
        }

        Log.Info("cleaning", "Cleaning Mode on.");
        _services.GetService<IAppShell>()?.ClosePanel();
        _keepVisible = _settings.Get(CleaningModeSettings.KeepScreenVisible);
        _modeObserver = _settings.Observe(CleaningModeSettings.KeepScreenVisible.Key, () => Dispatcher.UIThread.Post(OnModeChanged));
        _screens.ScreensChanged += OnScreensChanged;
        _session = _platform?.WatchSession(OnSessionChanged);
        _foreground = _platform?.WatchForeground(OnForegroundChanged);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Controller.Tick();
        _timer.Start();
        _services.GetService<ITrayPresence>()?.SetIndicator("cleaningMode", new TrayIndicator
        {
            Tint = 0xFF0067C0,
            TooltipLine = L.Get("Strings.cleaningMenuItem"),
            Priority = 50,
        });
        ShowOverlays();
    }

    public void Unlock() => Controller.RequestUnlock();

    public void Dispose()
    {
        // App quit: input first, then windows.
        Controller.Dispose();
        TearDownUi();
    }

    private void OnEnded(CleaningEndReason reason)
    {
        Log.Info("cleaning", $"Cleaning Mode off ({reason}).");
        TearDownUi();
        switch (reason)
        {
            case CleaningEndReason.FailedOpen:
                _hud.Show(L.Get("win.cleaningMode.endedNotResponding"), HudStyle.Warning, "Keyboard");
                break;
            case CleaningEndReason.HooksLost:
                _hud.Show(L.Get("win.cleaningMode.endedHooksLost"), HudStyle.Warning, "Keyboard");
                break;
        }
    }

    private void TearDownUi()
    {
        _timer?.Stop();
        _timer = null;
        _session?.Dispose();
        _session = null;
        _foreground?.Dispose();
        _foreground = null;
        _modeObserver?.Dispose();
        _modeObserver = null;
        _screens.ScreensChanged -= OnScreensChanged;
        foreach (var window in Windows)
        {
            window.Close();
        }

        _overlays.Clear();
        _indicators.Clear();
        _services.GetService<ITrayPresence>()?.SetIndicator("cleaningMode", null);
    }

    private void OnModeChanged()
    {
        if (!Controller.IsActive)
        {
            return;
        }

        var keepVisible = _settings.Get(CleaningModeSettings.KeepScreenVisible);
        if (keepVisible == _keepVisible)
        {
            return;
        }

        _keepVisible = keepVisible;
        foreach (var window in Windows)
        {
            window.Close();
        }

        _overlays.Clear();
        _indicators.Clear();
        ShowOverlays();
    }

    private void OnScreensChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (Controller.IsActive)
        {
            ShowOverlays();
        }
    });

    private void OnSessionChanged(SessionChange change)
    {
        if (change is SessionChange.Disconnected or SessionChange.Locked)
        {
            Controller.ForceEnd(CleaningEndReason.SessionChanged);
        }
    }

    /// <summary>
    /// Keys aimed at an elevated window bypass a non-elevated hook (UIPI), so
    /// the black screen takes the foreground back — except from Task Manager,
    /// which is how a user escapes with Ctrl+Alt+Del.
    /// </summary>
    private void OnForegroundChanged(nint hwnd)
    {
        if (!Controller.IsActive || _keepVisible || _overlays.Count == 0)
        {
            return;
        }

        if (_overlays.Any(o => WindowInterop.Handle(o) == hwnd))
        {
            return;
        }

        var path = _platform?.ProcessPathOf(hwnd);
        if (path is not null && Path.GetFileName(path).Equals("Taskmgr.exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var chrome = _services.GetService<IWindowChrome>();
        var target = OverlayUnderPointer();
        if (target is not null && chrome is not null)
        {
            Dispatcher.UIThread.Post(() => chrome.BringToFront(WindowInterop.Handle(target)), DispatcherPriority.Background);
        }
    }

    /// <summary>One overlay per monitor; exact matches are reused (no flash), new monitors added, extra ones removed.</summary>
    private void ShowOverlays()
    {
        var screens = _screens.Screens;
        if (screens.Count == 0)
        {
            screens = [new ScreenInfo { Id = "fallback", FriendlyName = "fallback", Bounds = new PixelRect(0, 0, 800, 600), WorkArea = new PixelRect(0, 0, 800, 600), Scale = 1, IsPrimary = true }];
        }

        if (_keepVisible)
        {
            Sync(_indicators, screens, s => new CleaningIndicatorWindow(Unlock), (w, s) =>
            {
                if (!w.IsVisible)
                {
                    w.Show();
                }

                w.PlaceOn(s.WorkArea, s.Bounds, s.Scale);
            });
        }
        else
        {
            Sync(_overlays, screens, s => new CleaningOverlayWindow(Unlock), (w, s) =>
            {
                w.Cover(s.Bounds, s.Scale);
                if (!w.IsVisible)
                {
                    w.Show();
                }

                w.Cover(s.Bounds, s.Scale);
            });

            // Take the keyboard focus so keys target this process (see OnForegroundChanged).
            var target = OverlayUnderPointer();
            if (target is not null)
            {
                target.Activate();
                _services.GetService<IWindowChrome>()?.BringToFront(WindowInterop.Handle(target));
            }
        }

        SetProgress(Controller.Progress);
    }

    private static void Sync<T>(List<T> windows, IReadOnlyList<ScreenInfo> screens, Func<ScreenInfo, T> create, Action<T, ScreenInfo> place)
        where T : Window
    {
        var bounds = (Func<T, PixelRect>)(w => w switch
        {
            CleaningOverlayWindow o => o.ScreenBounds,
            CleaningIndicatorWindow i => i.ScreenBounds,
            _ => default,
        });
        foreach (var stale in windows.Where(w => screens.All(s => s.Bounds != bounds(w))).ToList())
        {
            stale.Close();
            windows.Remove(stale);
        }

        foreach (var screen in screens)
        {
            var window = windows.FirstOrDefault(w => bounds(w) == screen.Bounds);
            if (window is null)
            {
                window = create(screen);
                windows.Add(window);
            }

            place(window, screen);
        }
    }

    private CleaningOverlayWindow? OverlayUnderPointer()
    {
        var cursor = _screens.CursorPosition;
        return _overlays.FirstOrDefault(o => o.ScreenBounds.Contains(cursor)) ?? _overlays.FirstOrDefault();
    }

    private void SetProgress(int progress)
    {
        foreach (var overlay in _overlays)
        {
            overlay.SetProgress(progress);
        }

        foreach (var indicator in _indicators)
        {
            indicator.SetProgress(progress);
        }
    }
}
