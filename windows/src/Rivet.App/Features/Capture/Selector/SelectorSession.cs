// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.ColorPicking;
using Rivet.App.Features.Capture.Pins;
using Rivet.App.Shell;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;
using KeyModifiers = Rivet.Core.Shortcuts.KeyModifiers;

namespace Rivet.App.Features.Capture.Selector;

/// <summary>
/// One capture-selector session (spec 01 §3.4): photographs the displays
/// (frozen mode) or stays live, shows one overlay per display, routes pointer
/// and keyboard input (the shared low-level hook makes Esc and the other keys
/// work even without focus) into <see cref="SelectorModel"/>, and turns the
/// confirmed gesture into pixels, a region or a colour.
/// </summary>
internal sealed class SelectorSession
{
    private static readonly TimeSpan LiveHideDelay = TimeSpan.FromMilliseconds(120);

    private readonly IServiceProvider _services;
    private readonly SelectorRequest _request;
    private readonly ISettingsStore _settings;
    private readonly IScreenService _screens;
    private readonly IScreenCapturer _capturer;
    private readonly IWindowEnumerator _windowEnumerator;
    private readonly IInputHooks _hooks;
    private readonly ICapturePlatform _platform;
    private readonly ColorPickService _colors;
    private readonly TaskCompletionSource<SelectorOutcome?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, SelectorOverlayWindow> _overlays = new(StringComparer.Ordinal);
    private Dictionary<string, DisplaySource> _sources = new(StringComparer.Ordinal);
    private Dictionary<string, DisplaySource> _liveSnapshot = new(StringComparer.Ordinal);
    private IReadOnlyList<CaptureWindowInfo> _allWindows = [];
    private IReadOnlyList<ScreenInfo> _displays = [];
    private SelectorModel? _model;
    private IDisposable? _keyboard;
    private DispatcherTimer? _copiedTimer;
    private int _refreshGeneration;
    private int _snapshotGeneration;
    private bool _finished;

    public SelectorSession(IServiceProvider services, SelectorRequest request)
    {
        _services = services;
        _request = request;
        _settings = services.GetRequiredService<ISettingsStore>();
        _screens = services.GetRequiredService<IScreenService>();
        _capturer = services.GetRequiredService<IScreenCapturer>();
        _windowEnumerator = services.GetRequiredService<IWindowEnumerator>();
        _hooks = services.GetRequiredService<IInputHooks>();
        _platform = services.GetRequiredService<ICapturePlatform>();
        _colors = services.GetRequiredService<ColorPickService>();
    }

    /// <summary>The model, once the session is on screen (tests and diagnostics).</summary>
    internal SelectorModel? Model => _model;

    internal IReadOnlyCollection<SelectorOverlayWindow> Overlays => _overlays.Values;

    /// <summary>Ends the session without a result (feature uninstalled, second press).</summary>
    public void Cancel() => Finish(null);

    public async Task<SelectorOutcome?> RunAsync()
    {
        _displays = _screens.Screens.ToList();
        var config = BuildConfig();
        var cursor = _screens.CursorPosition;
        _model = new SelectorModel(config, _displays, new PointD(cursor.X + 0.5, cursor.Y + 0.5));
        try
        {
            if (!await AcquireSourcesAsync(_model.Policy))
            {
                Fail();
                return null;
            }

            await RebuildWindowsAsync(_model.Policy);
            if (_finished)
            {
                return await _done.Task;
            }

            _services.GetService<IFocusHandoff>()?.Remember();
            foreach (var display in _displays)
            {
                if (_model.Policy.Freeze && !_sources.ContainsKey(display.Id))
                {
                    continue; // a display whose photograph failed gets no overlay
                }

                var overlay = new SelectorOverlayWindow(this, display, _settings);
                _overlays[display.Id] = overlay;
                overlay.Show();
            }

            _keyboard = _hooks.SubscribeKeyboard(OnHookKey, priority: 300);
            FocusOverlayUnderPointer();
            RefreshAll();
            return await _done.Task;
        }
        finally
        {
            Cleanup();
        }
    }

    // ── Rendering state ────────────────────────────────────────────────
    public HintState? HintStateFor(ScreenInfo display)
    {
        if (_model is not { } model || !model.HintBarVisible(display.Id))
        {
            return null;
        }

        return new HintState(
            model.Config.Tools, model.Tool, model.Mode, _request.ShowPalette, _request.Standalone, _request.RegionOnly, _request.Purpose,
            model.Scrolling, model.LoupeOn, model.RepeatAvailable, model.AllowsWindowClicks);
    }

    public bool PillVisible(ScreenInfo display) => _model?.FullScreenPillVisible(display.Id) == true;

    public OverlayScene? SceneFor(ScreenInfo display)
    {
        if (_model is not { } model)
        {
            return null;
        }

        var origin = display.Bounds;
        RectD Local(RectD r) => r.Offset(-origin.X, -origin.Y);
        _sources.TryGetValue(display.Id, out var source);
        var frozen = model.Policy.Freeze;
        var selection = model.SelectionDisplayId == display.Id ? model.Selection : null;
        RectD? highlight = model.HighlightedWindow is { } window && model.PointerDisplayId == display.Id ? Local(RectD.From(window.Bounds)) : null;
        string? badge = null;
        if (selection is { IsEmpty: false } sel)
        {
            var size = model.Mode == SelectorMode.Geometry
                ? CaptureGeometry.SnapRegion(sel.RoundOutward(), display.Bounds)
                : new PixelRect(0, 0, (int)Math.Round(sel.Width, MidpointRounding.AwayFromZero), (int)Math.Round(sel.Height, MidpointRounding.AwayFromZero));
            badge = $"{size.Width} × {size.Height}";
        }

        return new OverlayScene
        {
            Width = display.Bounds.Width,
            Height = display.Bounds.Height,
            Scale = display.Scale,
            Background = frozen ? source?.Image : null,
            Dim = frozen ? 0.22 : 0.18,
            Selection = selection is { } s ? Local(s) : null,
            Highlight = highlight,
            Ghost = model.GhostRect(display.Id) is { } g ? Local(g) : null,
            Badge = badge,
            Loupe = model.LoupeVisible(display.Id) ? Loupe(model, display) : null,
        };
    }

    private LoupeScene? Loupe(SelectorModel model, ScreenInfo display)
    {
        var source = model.Policy.Freeze ? _sources.GetValueOrDefault(display.Id) : _liveSnapshot.GetValueOrDefault(display.Id);
        if (source is null)
        {
            return null;
        }

        var local = new PointD(model.Pointer.X - display.Bounds.X, model.Pointer.Y - display.Bounds.Y);
        var image = source.Pixels;
        var imagePoint = new PointD(local.X * image.Width / display.Bounds.Width, local.Y * image.Height / display.Bounds.Height);
        var target = LoupeMath.TargetPixel(imagePoint, image.Width, image.Height);
        var color = CaptureImaging.ReadPixel(image, target.X, target.Y);
        var copied = model.IsCopiedFeedbackActive(DateTime.UtcNow);
        return new LoupeScene
        {
            Pointer = local,
            Source = source.Image,
            Sample = LoupeMath.SampleRect(imagePoint, image.Width, image.Height, LoupeMath.SampleSide(model.Zoom)),
            Target = target,
            Color = color,
            Value = copied ? model.CopiedValue! : _colors.Formatted(color),
            Copied = copied,
        };
    }

    public KeyModifiers HeldModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (_hooks.IsKeyDown(0x10)) modifiers |= KeyModifiers.Shift;
        if (_hooks.IsKeyDown(0x12)) modifiers |= KeyModifiers.Alt;
        if (_hooks.IsKeyDown(0x11)) modifiers |= KeyModifiers.Control;
        return modifiers;
    }

    // ── Input ──────────────────────────────────────────────────────────
    public void OnPointerMoved(PointD point, KeyModifiers modifiers)
    {
        if (_model is not { } model || _finished)
        {
            return;
        }

        var previousDisplay = model.PointerDisplayId;
        model.PointerMoved(point, modifiers);
        if (previousDisplay != model.PointerDisplayId && _overlays.TryGetValue(model.PointerDisplayId, out var overlay) && !model.IsDragging)
        {
            overlay.Activate();
        }

        RefreshAll();
    }

    public void OnPointerPressed(PointD point, KeyModifiers modifiers)
    {
        if (_model is not { } model || _finished)
        {
            return;
        }

        model.PointerPressed(point, modifiers);
        RefreshAll();
    }

    public void OnPointerReleased(PointD point, KeyModifiers modifiers)
    {
        if (_model is not { } model || _finished)
        {
            return;
        }

        Handle(model.PointerReleased(point, modifiers));
        RefreshAll();
    }

    public void OnPointerCaptureLost()
    {
        // Pointer capture can be lost to the system (Alt+Tab); a pending release would never arrive.
        if (_model is { IsDragging: true } model && !_finished)
        {
            Handle(model.PointerReleased(model.Pointer, KeyModifiers.None));
            RefreshAll();
        }
    }

    public void OnWheel(double delta, bool continuous, KeyModifiers modifiers)
    {
        if (_model is not { } model || _finished)
        {
            return;
        }

        if (model.Wheel(delta, continuous, modifiers.HasFlag(KeyModifiers.Alt)))
        {
            _settings.Set(CaptureSettings.LoupeLastZoom, model.Zoom);
            RefreshAll();
        }
    }

    public void OnKey(SelectorKey key, KeyAction action, KeyModifiers modifiers)
    {
        if (_model is not { } model || _finished)
        {
            return;
        }

        if (action == KeyAction.Up)
        {
            model.KeyUp(key);
            RefreshAll();
            return;
        }

        var loupeWasOn = model.LoupeOn;
        Handle(model.KeyDown(key, modifiers | HeldModifiers()));
        if (key == SelectorKey.Z && model.LoupeOn && !loupeWasOn && !model.Policy.Freeze)
        {
            _ = TakeLiveSnapshotAsync();
        }

        RefreshAll();
    }

    public void RequestTool(CaptureTool tool)
    {
        if (_model is not { } model || _finished || model.IsInert || tool == model.Tool || !model.Config.Tools.Contains(tool))
        {
            return;
        }

        Handle(model.BeginSwitch(tool));
        RefreshAll();
    }

    public void OnFullScreenPill(ScreenInfo display)
    {
        if (_model is not { } model || _finished || model.IsInert || model.RefreshPending)
        {
            return;
        }

        model.MarkPending();
        Handle(new SelectorAction { Kind = SelectorActionKind.ConfirmDisplay, DisplayId = display.Id, Region = RectD.From(display.Bounds) });
    }

    public void SetPointerOverPill(bool over)
    {
        if (_model is { } model)
        {
            model.PointerOverPill = over;
            RefreshAll();
        }
    }

    /// <summary>Runs on the hook thread: decide quickly, act on the UI thread.</summary>
    private bool OnHookKey(ref KeyboardHookEvent e)
    {
        if (_finished || SelectorKeys.Map(e.VirtualKey) is not { } key || !SelectorKeys.ShouldSwallow(key, e.Modifiers))
        {
            return false;
        }

        var action = e.Action;
        var modifiers = e.Modifiers;
        Dispatcher.UIThread.Post(() => OnKey(key, action, modifiers));
        return true;
    }

    // ── Actions ────────────────────────────────────────────────────────
    private void Handle(SelectorAction? action)
    {
        if (action is null || _model is not { } model)
        {
            return;
        }

        switch (action.Kind)
        {
            case SelectorActionKind.Cancel:
                Finish(null);
                break;
            case SelectorActionKind.SwitchTool:
                _ = SwitchToolAsync(action);
                break;
            case SelectorActionKind.MovePointer:
                _platform.SetCursorPosition(action.Point.Floor());
                break;
            case SelectorActionKind.CopyColor:
                CopyColor(model, action);
                break;
            default:
                _ = ProduceAsync(action);
                break;
        }
    }

    private void CopyColor(SelectorModel model, SelectorAction action)
    {
        if (ColorAt(model, action.Point) is not { } color)
        {
            return;
        }

        if (_colors.Copy(color, toast: false))
        {
            model.ShowCopied(_colors.Formatted(color), DateTime.UtcNow);
            _copiedTimer?.Stop();
            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(LoupeMath.CopiedFeedbackSeconds + 0.05) };
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer?.Stop();
                RefreshAll();
            };
            _copiedTimer.Start();
        }
    }

    private uint? ColorAt(SelectorModel model, PointD point)
    {
        var display = _displays.FirstOrDefault(d => d.Bounds.Contains(point.Floor())) ?? model.PointerDisplay;
        var source = model.Policy.Freeze ? _sources.GetValueOrDefault(display.Id) : _liveSnapshot.GetValueOrDefault(display.Id);
        if (source is null)
        {
            return null;
        }

        var image = source.Pixels;
        var imagePoint = new PointD((point.X - display.Bounds.X) * image.Width / display.Bounds.Width, (point.Y - display.Bounds.Y) * image.Height / display.Bounds.Height);
        var target = LoupeMath.TargetPixel(imagePoint, image.Width, image.Height);
        return CaptureImaging.ReadPixel(image, target.X, target.Y);
    }

    /// <summary>Turns a confirmation into the session's outcome (pixels, region or colour).</summary>
    private async Task ProduceAsync(SelectorAction action)
    {
        if (_model is not { } model || _finished)
        {
            return;
        }

        var display = _displays.FirstOrDefault(d => d.Id == action.DisplayId) ?? model.PointerDisplay;
        RefreshAll();
        try
        {
            var outcome = model.Mode switch
            {
                SelectorMode.Color => ColorOutcome(model, action, display),
                SelectorMode.Geometry => GeometryOutcome(model, action, display),
                _ => await ImageOutcomeAsync(model, action, display),
            };
            if (outcome is null)
            {
                Fail();
                return;
            }

            Finish(outcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("capture", "Producing the capture failed.", ex);
            Fail();
        }
    }

    private SelectorOutcome? ColorOutcome(SelectorModel model, SelectorAction action, ScreenInfo display) =>
        ColorAt(model, action.Point) is { } color
            ? new SelectorOutcome { Tool = model.Tool, Kind = action.Kind, Display = display, Color = color, Region = new PixelRect(action.Point.Floor().X, action.Point.Floor().Y, 1, 1) }
            : null;

    private SelectorOutcome GeometryOutcome(SelectorModel model, SelectorAction action, ScreenInfo display)
    {
        var rect = action.Kind switch
        {
            SelectorActionKind.ConfirmWindow when action.Window is { } window => window.Bounds,
            SelectorActionKind.ConfirmDisplay => display.Bounds,
            _ => action.Region.RoundOutward(),
        };
        return new SelectorOutcome
        {
            Tool = model.Tool,
            Kind = action.Kind,
            Display = display,
            Region = CaptureGeometry.SnapRegion(rect, display.Bounds),
            Window = action.Window,
            Scrolling = action.Scrolling || _request.ForceGeometry,
        };
    }

    private async Task<SelectorOutcome?> ImageOutcomeAsync(SelectorModel model, SelectorAction action, ScreenInfo display)
    {
        var policy = model.Policy;
        var options = Options(policy);
        CapturedImage? image = null;
        if (action.Kind == SelectorActionKind.ConfirmWindow && action.Window is { } window)
        {
            // A clicked window is captured live (its own pixels), even in frozen mode.
            HideOverlays();
            var attached = WindowPicking.AttachedWindows(_allWindows, window);
            var pixels = await _capturer.CaptureWindowAsync(window, attached, options);
            if (pixels is not null)
            {
                image = new CapturedImage(pixels, window.Bounds) { Kind = CaptureTargetKind.Window, WindowTitle = window.Title };
            }
        }
        else
        {
            var region = action.Kind == SelectorActionKind.ConfirmDisplay ? RectD.From(display.Bounds) : action.Region;
            PixelBuffer? still;
            if (policy.Freeze)
            {
                still = _sources.GetValueOrDefault(display.Id)?.Pixels;
            }
            else
            {
                // Live: hide the overlays first so they can never appear in the picture.
                HideOverlays();
                await Task.Delay(LiveHideDelay);
                var captured = await _capturer.CaptureDisplaysAsync([display], options);
                still = captured.GetValueOrDefault(display.Id);
            }

            if (still is not null)
            {
                var kind = action.Kind == SelectorActionKind.ConfirmDisplay ? CaptureTargetKind.Display : CaptureTargetKind.Area;
                var pixels = kind == CaptureTargetKind.Display ? still : CaptureImaging.Crop(still, CaptureGeometry.ToImageRect(region, display.Bounds, still.Width, still.Height));
                if (pixels is not null)
                {
                    var anchor = region.RoundOutward().Intersect(display.Bounds);
                    image = new CapturedImage(CaptureImaging.WithScale(pixels, display.Scale), anchor) { Kind = kind };
                }
            }
        }

        if (image is null)
        {
            return null;
        }

        return new SelectorOutcome
        {
            Tool = model.Tool,
            Kind = action.Kind,
            Display = display,
            Region = image.Anchor,
            Window = action.Window,
            Image = image,
            Scrolling = action.Scrolling,
        };
    }

    private async Task SwitchToolAsync(SelectorAction action)
    {
        if (_model is not { } model)
        {
            return;
        }

        if (!action.NeedsRefresh)
        {
            await RebuildWindowsAsync(model.Policy);
            RefreshAll();
            return;
        }

        // Different source policy: re-photograph (old pixels stay until the new ones arrive).
        var generation = ++_refreshGeneration;
        var policy = model.Policy;
        var fresh = new Dictionary<string, DisplaySource>(StringComparer.Ordinal);
        if (policy.Freeze)
        {
            var targets = _displays.Where(d => _overlays.ContainsKey(d.Id)).ToList();
            IReadOnlyDictionary<string, PixelBuffer> captured;
            try
            {
                captured = await _capturer.CaptureDisplaysAsync(targets, Options(policy));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn("capture", "Refreshing the photographs failed.", ex);
                captured = new Dictionary<string, PixelBuffer>();
            }

            if (generation != _refreshGeneration || _finished)
            {
                return; // a newer refresh (or the end of the session) supersedes this one
            }

            if (targets.Any(d => !captured.ContainsKey(d.Id)))
            {
                // Never resume on stale pixels.
                Fail();
                return;
            }

            fresh = await Task.Run(() => captured.ToDictionary(p => p.Key, p => new DisplaySource(p.Value), StringComparer.Ordinal));
        }

        var old = _sources;
        _sources = fresh;
        DisposeLater(old.Values.ToList());

        await RebuildWindowsAsync(policy);
        model.CompleteRefresh();
        RefreshAll();
    }

    private async Task<bool> AcquireSourcesAsync(SelectorSourcePolicy policy)
    {
        if (!policy.Freeze)
        {
            return true;
        }

        try
        {
            var captured = await _capturer.CaptureDisplaysAsync(_displays, Options(policy));

            // Converting large stills for drawing takes a moment: keep it off the UI thread.
            _sources = await Task.Run(() => captured.ToDictionary(p => p.Key, p => new DisplaySource(p.Value), StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("capture", "Photographing the displays failed.", ex);
        }

        return _sources.Count > 0;
    }

    private async Task TakeLiveSnapshotAsync()
    {
        var generation = ++_snapshotGeneration;
        var policy = _model?.Policy;
        if (policy is null)
        {
            return;
        }

        var captured = await _capturer.CaptureDisplaysAsync(_displays, Options(policy) with { IncludeCursor = false });
        if (generation != _snapshotGeneration || _finished)
        {
            return;
        }

        DisposeLater(_liveSnapshot.Values.ToList());
        _liveSnapshot = await Task.Run(() => captured.ToDictionary(p => p.Key, p => new DisplaySource(p.Value), StringComparer.Ordinal));
        RefreshAll();
    }

    private async Task RebuildWindowsAsync(SelectorSourcePolicy policy)
    {
        var protectedHandles = policy.KeepContentOut ? _services.GetService<PinManager>()?.Handles.ToHashSet() : null;
        try
        {
            _allWindows = await Task.Run(_windowEnumerator.EnumerateWindows);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("capture", "Listing windows failed.", ex);
            _allWindows = [];
        }

        if (_model is { } model)
        {
            model.Windows = WindowPicking.Pickable(_allWindows, policy.HideOwnWindows, protectedHandles);
        }
    }

    private DisplayCaptureOptions Options(SelectorSourcePolicy policy) => new()
    {
        IncludeCursor = policy.IncludePointer,
        HideOwnWindows = policy.HideOwnWindows,
        ExcludedWindows = policy.KeepContentOut ? _services.GetService<PinManager>()?.Handles ?? [] : [],
    };

    private SelectorConfig BuildConfig()
    {
        var freeze = !_request.ForceLive && _settings.Get(CaptureSettings.Freeze);
        var hideOwn = _settings.Get(CaptureSettings.HideOwnWindows);
        var remember = _settings.Get(CaptureSettings.LoupeRememberZoom);
        return new SelectorConfig
        {
            Tools = _request.Tools,
            InitialTool = _request.Tool,
            ShowPalette = _request.ShowPalette,
            Standalone = _request.Standalone,
            RegionOnly = _request.RegionOnly,
            ForceGeometry = _request.ForceGeometry,
            Purpose = _request.Purpose,
            LoupeStartsOn = _settings.Get(CaptureSettings.LoupeStartsOn),
            InitialZoom = LoupeMath.InitialZoom(remember, _settings.Get(CaptureSettings.LoupeLastZoom), _settings.Get(CaptureSettings.LoupeDefaultZoom)),
            SteppedZoomByDefault = _settings.Get(CaptureSettings.LoupeSteppedZoomByDefault),
            ShowLastRegion = _settings.Get(CaptureSettings.ShowLastRegion),
            ScreenshotPolicy = new SelectorSourcePolicy(freeze, _settings.Get(CaptureSettings.IncludePointer), hideOwn, hideOwn),
            HideOwnWindowsSetting = hideOwn,
        };
    }

    // ── Lifetime ───────────────────────────────────────────────────────
    private void RefreshAll()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.Refresh();
        }
    }

    private void FocusOverlayUnderPointer()
    {
        if (_model is not { } model || !_overlays.TryGetValue(model.PointerDisplayId, out var overlay))
        {
            overlay = _overlays.Values.FirstOrDefault();
        }

        if (overlay is null)
        {
            return;
        }

        overlay.Activate();
        var handle = WindowInterop.Handle(overlay);
        if (handle != 0)
        {
            _services.GetService<IWindowChrome>()?.BringToFront(handle);
        }
    }

    private void HideOverlays()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.Hide();
        }
    }

    private void Fail()
    {
        if (_finished)
        {
            return;
        }

        _services.GetService<IHud>()?.Show(L.Get("screenshot.captureFailed"), HudStyle.Error, "ErrorCircle");
        _platform.Beep();
        Finish(null);
    }

    private void Finish(SelectorOutcome? outcome)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _model?.End();
        _keyboard?.Dispose();
        _keyboard = null;
        HideOverlays();
        if (outcome is null)
        {
            _services.GetService<IFocusHandoff>()?.Restore();
        }

        _done.TrySetResult(outcome);
    }

    private void Cleanup()
    {
        _keyboard?.Dispose();
        _keyboard = null;
        _copiedTimer?.Stop();
        foreach (var overlay in _overlays.Values)
        {
            overlay.Close();
        }

        _overlays.Clear();
        DisposeLater(_sources.Values.Concat(_liveSnapshot.Values).ToList());
        _sources = new Dictionary<string, DisplaySource>(StringComparer.Ordinal);
        _liveSnapshot = new Dictionary<string, DisplaySource>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The render thread may still be drawing a frame that uses these images:
    /// release them a moment later instead of under its feet.
    /// </summary>
    private static void DisposeLater(IReadOnlyList<DisplaySource> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        DispatcherTimer.RunOnce(() =>
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
        }, TimeSpan.FromSeconds(1));
    }
}
