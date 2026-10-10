// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.App;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Recording;

/// <summary>
/// The screen recorder's state machine (spec 02 §3.1–§3.18): one toggle for
/// every entry point; pre-flight; choosing what to record (the shared
/// capture selector, or the monitor under the pointer when it is absent); the
/// microphone check; countdown; start; pause/resume; live monitoring (elapsed
/// time, free disk space, display changes); stop and delivery; the take
/// sweep. A generation counter makes cancelling at any await safe: every
/// asynchronous step checks it is still current. Runs on the UI thread; all
/// capture work happens off it.
/// </summary>
public sealed class ScreenRecorderController : IScreenRecorder, IFeatureController, IDisposable
{
    public const string TrayIndicatorId = "screenRecorder";
    private const uint RecordingTint = 0xFFE81123;

    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly FeatureRuntime _runtime;
    private readonly IScreenService _screens;
    private readonly AppPaths _paths;
    private readonly RecordingDelivery _delivery;
    private readonly RecordingChrome _chrome;
    private ScreenRecorderState _state = ScreenRecorderState.Idle;
    private bool _selecting;
    private int _generation;
    private RecordingSession? _session;
    private RecordingTarget? _target;
    private IDisposable? _sleep;
    private DispatcherTimer? _tick;
    private bool _diskCheckRunning;
    private bool _microphoneWarned;
    private bool _systemAudioWarned;
    private bool _screensHooked;
    private bool _disposed;

    public ScreenRecorderController(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _screens = services.GetRequiredService<IScreenService>();
        _paths = services.GetRequiredService<AppPaths>();
        _delivery = services.GetRequiredService<RecordingDelivery>();
        _chrome = services.GetRequiredService<RecordingChrome>();
        _chrome.PauseRequested += (_, _) => TogglePause();
        _chrome.StopRequested += (_, _) => Stop(StopReason.User);
        _chrome.DiscardRequested += (_, _) => Stop(new StopReason(StopKind.Discard));
    }

    public event EventHandler? StateChanged;

    /// <summary>Raised every second while recording (elapsed label refresh), on the UI thread.</summary>
    public event EventHandler? Tick;

    public ScreenRecorderState State => _state;

    public bool IsBusy => _state != ScreenRecorderState.Idle;

    /// <summary>Recording or paused (or opening the take): stopping makes sense.</summary>
    public bool IsRecording => _state is ScreenRecorderState.Recording or ScreenRecorderState.Paused or ScreenRecorderState.Starting;

    public double ElapsedSeconds => _session?.Elapsed ?? 0;

    /// <summary>"Recording 0:42" / "Paused 0:42" for the panel tile and Settings.</summary>
    public string? LiveCaption => IsRecording
        ? $"{L.Get(_state == ScreenRecorderState.Paused ? "win.recording.pausedLabel" : "win.recording.recordingLabel")} {ElapsedLabel.Format(ElapsedSeconds)}"
        : null;

    public RecordingTarget? Target => _target;

    public RecordingChrome Chrome => _chrome;

    /// <summary>The take of the running recording (for tests and diagnostics).</summary>
    public string? ActiveTakeFolder => _session?.TakeFolder;

    /// <summary>The last stop's delivery, for tests.</summary>
    internal Task? Finishing { get; private set; }

    /// <summary>Waits used by the countdown and the start delay (tests shorten them).</summary>
    internal Func<TimeSpan, Task> Delay { get; set; } = static d => Task.Delay(d);

    public Task ToggleAsync(bool fromShortcut = false)
    {
        switch (_state)
        {
            case ScreenRecorderState.Recording or ScreenRecorderState.Paused or ScreenRecorderState.Starting:
                Stop(StopReason.User);
                return Task.CompletedTask;
            case ScreenRecorderState.Preparing when !_selecting:
                CancelPending();
                return Task.CompletedTask;
            case ScreenRecorderState.Preparing or ScreenRecorderState.Finishing:
                // The chooser is on screen (it has its own Esc), or a stop is still writing the file.
                return Task.CompletedTask;
            default:
                return BeginAsync(fromShortcut, null);
        }
    }

    public Task RecordSelectionAsync(CaptureSelection selection) =>
        IsBusy ? Task.CompletedTask : BeginAsync(fromShortcut: false, selection);

    public void Stop() => Stop(StopReason.User);

    public bool TogglePause()
    {
        if (_session is not { } session)
        {
            return false;
        }

        if (_state == ScreenRecorderState.Recording && session.Pause())
        {
            SetState(ScreenRecorderState.Paused);
        }
        else if (_state == ScreenRecorderState.Paused && session.Resume())
        {
            SetState(ScreenRecorderState.Recording);
        }
        else
        {
            return false;
        }

        _chrome.UpdateIndicator(ElapsedLabel.Format(session.Elapsed), session.IsPaused);
        return true;
    }

    /// <summary>Feature controller: uninstalling stops a recording (it is still saved) and cancels a pending start.</summary>
    public void Sync(bool available)
    {
        if (!available)
        {
            if (_session is not null)
            {
                Stop(new StopReason(StopKind.FeatureRemoved));
            }
            else if (_state == ScreenRecorderState.Preparing)
            {
                CancelPending();
            }
        }

        SweepInBackground();
    }

    /// <summary>Stops the recording; ignored without one or while finishing. Without a session it cancels a pending start.</summary>
    public void Stop(StopReason reason)
    {
        var session = _session;
        if (session is null)
        {
            if (_state == ScreenRecorderState.Preparing && !_selecting)
            {
                CancelPending();
            }

            return;
        }

        if (_state == ScreenRecorderState.Finishing)
        {
            return;
        }

        _generation++;
        _session = null;
        SetState(ScreenRecorderState.Finishing);
        _chrome.HideAll();
        StopLiveMonitoring();
        Finishing = FinishAsync(session, reason);
    }

    /// <summary>Quitting: a running recording is finished and saved straight away (blocking, bounded).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        var session = _session;
        _session = null;
        try
        {
            _tick?.Stop();
        }
        catch (Exception ex)
        {
            Log.Debug("recorder", $"Stopping the timer at shutdown: {ex.Message}");
        }

        ReleaseSleep();
        try
        {
            if (session is not null)
            {
                Log.Info("recorder", "Quitting while recording: finishing and saving the take.");
                Task.Run(async () =>
                {
                    var outcome = await session.StopAsync().ConfigureAwait(false);
                    if (outcome.Written && outcome.Manifest is { } manifest)
                    {
                        var saved = await _delivery.SaveAsync(session.TakeFolder, manifest).ConfigureAwait(false);
                        if (saved is not null)
                        {
                            RecordingDelivery.TryDeleteTake(session.TakeFolder);
                        }

                        Log.Info("recorder", saved is null ? "Saving on quit failed; the take is kept." : $"Saved on quit: {saved}");
                    }
                    else
                    {
                        RecordingDelivery.TryDeleteTake(session.TakeFolder);
                    }
                }).Wait(TimeSpan.FromSeconds(30));
            }
            else
            {
                Finishing?.Wait(TimeSpan.FromSeconds(15));
            }
        }
        catch (Exception ex)
        {
            Log.Error("recorder", "Finishing the recording at shutdown failed.", ex);
        }
    }

    /// <summary>What the user picked, resolved against the monitors once (spec 02 §3.3).</summary>
    public static RecordingTarget Resolve(CaptureSelection selection, IScreenService screens)
    {
        var bounds = selection.Bounds;
        var monitor = screens.Screens.FirstOrDefault(s => string.Equals(s.Id, selection.ScreenId, StringComparison.OrdinalIgnoreCase))
                      ?? screens.ScreenFromPoint(new PixelPoint(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)));
        switch (selection.Kind)
        {
            case CaptureTargetKind.Window when selection.WindowHandle != 0:
            {
                var clamped = bounds.Intersect(monitor.Bounds);
                return new RecordingTarget
                {
                    Kind = RecordingTargetKind.Window,
                    Monitor = monitor,
                    Region = clamped.IsEmpty ? monitor.Bounds : clamped,
                    Window = selection.WindowHandle,
                };
            }

            case CaptureTargetKind.Display:
                return new RecordingTarget { Kind = RecordingTargetKind.Display, Monitor = monitor, Region = RegionSnapping.Snap(monitor.Bounds, monitor.Bounds) };
            default:
                return new RecordingTarget { Kind = RecordingTargetKind.Area, Monitor = monitor, Region = RegionSnapping.Snap(bounds, monitor.Bounds) };
        }
    }

    /// <summary>Without the shared selector: the whole monitor under the pointer.</summary>
    public static RecordingTarget MonitorUnderPointer(IScreenService screens)
    {
        var monitor = screens.ScreenFromPoint(screens.CursorPosition);
        return new RecordingTarget { Kind = RecordingTargetKind.Display, Monitor = monitor, Region = RegionSnapping.Snap(monitor.Bounds, monitor.Bounds) };
    }

    private async Task BeginAsync(bool fromShortcut, CaptureSelection? selection)
    {
        if (_disposed || !_runtime.IsAvailable(FeatureIds.ScreenRecorder))
        {
            return;
        }

        var generation = ++_generation;
        SetState(ScreenRecorderState.Preparing);
        try
        {
            if (!await PreflightAsync() || generation != _generation)
            {
                if (generation == _generation)
                {
                    ResetToIdle();
                }

                return;
            }

            RecordingTarget? target;
            if (selection is not null)
            {
                target = Resolve(selection, _screens);
            }
            else
            {
                _selecting = true;
                try
                {
                    target = await ChooseAsync(fromShortcut);
                }
                finally
                {
                    _selecting = false;
                }
            }

            if (generation != _generation)
            {
                return;
            }

            // Pre-flight again now that the area is confirmed (spec §3.2).
            if (target is null || !await PreflightAsync() || generation != _generation)
            {
                if (generation == _generation)
                {
                    ResetToIdle();
                }

                return;
            }

            await PrepareAndStartAsync(target, generation);
        }
        catch (Exception ex)
        {
            Log.Error("recorder", "Starting a recording failed.", ex);
            if (generation == _generation)
            {
                _chrome.HideAll();
                ResetToIdle();
                Hud(L.Get("recorder.recordFailed"), HudStyle.Error);
            }
        }
    }

    private async Task<bool> PreflightAsync()
    {
        var video = _services.GetRequiredService<IVideoCaptureBackend>();
        var availability = await Task.Run(video.CheckAvailability);
        if (availability != RecorderAvailability.Available)
        {
            Hud(RecordingChrome.MessageFor(availability), HudStyle.Error);
            return false;
        }

        var root = TakeFolders.Root(_paths);
        var free = await Task.Run(() => DiskSpace.FreeBytes(root));
        if (free is { } bytes && bytes < DiskSpace.MinimumToStart)
        {
            await RecordingChrome.ShowAlertAsync(L.Get("recorder.noSpaceTitle"), L.Get("recorder.noSpaceMessage"));
            return false;
        }

        return true;
    }

    private async Task<RecordingTarget?> ChooseAsync(bool fromShortcut)
    {
        var selector = _services.GetService<ICaptureSelector>();
        if (selector is null)
        {
            return MonitorUnderPointer(_screens);
        }

        if (selector.IsActive)
        {
            return null;
        }

        var showMenu = !fromShortcut || _settings.Get(RecorderSettings.ShowCaptureMenuOnShortcut);
        var selection = await selector.SelectAsync(new CaptureSelectorRequest { Tool = CaptureTool.Recording, AllowToolSwitching = showMenu });
        if (selection is null)
        {
            return null;
        }

        if (selection.Tool != CaptureTool.Recording)
        {
            Log.Info("recorder", $"The selector returned a {selection.Tool} selection; nothing to record.");
            return null;
        }

        return Resolve(selection, _screens);
    }

    private async Task PrepareAndStartAsync(RecordingTarget target, int generation)
    {
        _target = target;
        _microphoneWarned = false;
        _systemAudioWarned = false;
        if (target.Kind != RecordingTargetKind.Window)
        {
            _chrome.ShowGuide(target);
        }

        var useMicrophone = _settings.Get(RecorderSettings.Microphone);
        if (useMicrophone)
        {
            var device = _settings.Get(RecorderSettings.MicrophoneDevice);
            var audio = _services.GetRequiredService<IAudioCaptureBackend>();
            var status = await Task.Run(() => audio.ProbeMicrophone(device));
            if (generation != _generation)
            {
                return;
            }

            if (status != MicrophoneStatus.Available)
            {
                Log.Info("recorder", $"Microphone {status}; recording without it.");
                WarnMicrophone();
                useMicrophone = false;
            }
        }

        for (var n = _settings.Get(RecorderSettings.Countdown); n > 0; n--)
        {
            _chrome.ShowCountdown(n);
            await Delay(TimeSpan.FromSeconds(1));
            if (generation != _generation)
            {
                return;
            }
        }

        _chrome.HideCountdown();
        await StartRecordingAsync(target, useMicrophone, generation);
    }

    private async Task StartRecordingAsync(RecordingTarget target, bool useMicrophone, int generation)
    {
        if (generation != _generation || !_runtime.IsAvailable(FeatureIds.ScreenRecorder))
        {
            return;
        }

        string folder;
        try
        {
            folder = TakeFolders.Create(_paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("recorder", "Creating the take folder failed.", ex);
            _chrome.HideAll();
            ResetToIdle();
            Hud(L.Get("recorder.recordFailed"), HudStyle.Error);
            return;
        }

        var session = new RecordingSession(
            new RecordingSessionOptions
            {
                Target = target,
                TakeFolder = folder,
                FrameRate = _settings.Get(RecorderSettings.FrameRate),
                SystemAudio = _settings.Get(RecorderSettings.SystemAudio),
                Microphone = useMicrophone,
                MicrophoneDevice = _settings.Get(RecorderSettings.MicrophoneDevice),
                AppVersion = AppIdentity.VersionString,
            },
            new RecordingServices(
                _services.GetRequiredService<IVideoCaptureBackend>(),
                _services.GetRequiredService<IAudioCaptureBackend>(),
                _services.GetRequiredService<ICursorProbe>(),
                _services.GetRequiredService<IRecorderSystem>(),
                _services.GetRequiredService<IInputHooks>(),
                QpcClock.Instance));
        session.CaptureEnded += (_, e) => Dispatcher.UIThread.Post(() => OnCaptureEnded(session, e));
        session.MicrophoneUnavailable += (_, _) => Dispatcher.UIThread.Post(WarnMicrophone);
        session.SystemAudioUnavailable += (_, _) => Dispatcher.UIThread.Post(WarnSystemAudio);
        _session = session;
        SetState(ScreenRecorderState.Starting);

        // The pill exists before capture starts; then let the chooser overlays leave the screen.
        if (_settings.Get(RecorderSettings.ShowIndicator))
        {
            _chrome.ShowIndicator(target);
        }

        await Delay(TimeSpan.FromMilliseconds(120));
        if (_session != session)
        {
            return;
        }

        try
        {
            await Task.Run(() => session.StartAsync());
        }
        catch (Exception ex)
        {
            if (_session != session)
            {
                return; // a stop arrived meanwhile and owns the take
            }

            Log.Error("recorder", "The recording could not start.", ex);
            _session = null;
            _chrome.HideAll();
            _ = Task.Run(() => RecordingDelivery.TryDeleteTake(folder));
            ResetToIdle();
            Hud(L.Get("recorder.recordFailed"), HudStyle.Error);
            return;
        }

        if (_session != session)
        {
            return;
        }

        SetState(ScreenRecorderState.Recording);
        StartLiveMonitoring();
    }

    private async Task FinishAsync(RecordingSession session, StopReason reason)
    {
        var folder = session.TakeFolder;
        try
        {
            var outcome = await Task.Run(() => reason.Kind == StopKind.Discard ? session.DiscardAsync() : session.StopAsync());
            if (reason.Kind == StopKind.Discard)
            {
                await Task.Run(() => RecordingDelivery.TryDeleteTake(folder));
                Log.Info("recorder", "Recording discarded.");
            }
            else if (outcome.Written && outcome.Manifest is { } manifest)
            {
                await _delivery.DeliverAsync(folder, manifest, reason);
            }
            else
            {
                await Task.Run(() => RecordingDelivery.TryDeleteTake(folder));

                // Stopped before capture even began (within the start-up wait): a cancel, not a failure.
                if (session.Clock.HasOrigin)
                {
                    Hud(L.Get("recorder.recordFailed"), HudStyle.Error);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("recorder", "Finishing the recording failed.", ex);
            Hud(L.Get("recorder.recordFailed"), HudStyle.Error);
        }
        finally
        {
            ResetToIdle();
            SweepInBackground();
        }
    }

    private void CancelPending()
    {
        _generation++;
        _chrome.HideAll();
        ResetToIdle();
    }

    private void StartLiveMonitoring()
    {
        try
        {
            _sleep = _services.GetRequiredService<IRecorderSystem>().PreventSleep("Recording the screen");
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Could not keep the PC awake.", ex);
        }

        _tick?.Stop();
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        if (!_screensHooked)
        {
            _screensHooked = true;
            _screens.ScreensChanged += OnScreensChanged;
        }

        OnTick();
    }

    private void StopLiveMonitoring()
    {
        _tick?.Stop();
        _tick = null;
        ReleaseSleep();
        if (_screensHooked)
        {
            _screensHooked = false;
            _screens.ScreensChanged -= OnScreensChanged;
        }
    }

    private void ReleaseSleep()
    {
        try
        {
            Interlocked.Exchange(ref _sleep, null)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Releasing the power request failed.", ex);
        }
    }

    private void OnTick()
    {
        if (_session is not { } session)
        {
            return;
        }

        var elapsed = ElapsedLabel.Format(session.Elapsed);
        _chrome.UpdateIndicator(elapsed, session.IsPaused);
        UpdateTray();
        Tick?.Invoke(this, EventArgs.Empty);
        CheckDisk(session);
    }

    /// <summary>Spec §3.15: one check at a time, off the UI thread; an answer for an older recording is ignored.</summary>
    private void CheckDisk(RecordingSession session)
    {
        if (_diskCheckRunning)
        {
            return;
        }

        _diskCheckRunning = true;
        var root = TakeFolders.Root(_paths);
        _ = Task.Run(() => DiskSpace.FreeBytes(root)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            _diskCheckRunning = false;
            if (_session == session && task.IsCompletedSuccessfully && task.Result is { } free && free < DiskSpace.MinimumWhileRecording)
            {
                Log.Warn("recorder", $"Only {free / 1_000_000} MB free; stopping.");
                Stop(new StopReason(StopKind.DiskAlmostFull, L.Get("recorder.stoppedNoSpaceHUD")));
            }
        }), TaskScheduler.Default);
    }

    private void OnCaptureEnded(RecordingSession session, CaptureEndedEventArgs e)
    {
        if (_session == session)
        {
            Stop(new StopReason(StopKind.CaptureEnded, RecordingChrome.MessageFor(e.Reason)));
        }
    }

    /// <summary>The recorded monitor disappeared or changed size: stop cleanly (window recordings follow their window).</summary>
    private void OnScreensChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_session is null || _target is not { Kind: not RecordingTargetKind.Window } target)
        {
            return;
        }

        var monitor = _screens.Screens.FirstOrDefault(s => s.Id == target.Monitor.Id);
        if (monitor is null || monitor.Bounds != target.Monitor.Bounds)
        {
            Stop(new StopReason(StopKind.CaptureEnded, L.Get("win.recording.displayChanged")));
        }
    });

    private void WarnMicrophone()
    {
        if (!_microphoneWarned)
        {
            _microphoneWarned = true;
            Hud(L.Get("recorder.microphoneUnavailableHUD"), HudStyle.Warning, "MicOff");
        }
    }

    private void WarnSystemAudio()
    {
        if (!_systemAudioWarned && IsBusy)
        {
            _systemAudioWarned = true;
            Hud(L.Get("win.recording.systemAudioUnavailable"), HudStyle.Warning, "SpeakerOff");
        }
    }

    private void SetState(ScreenRecorderState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        UpdateTray();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetToIdle()
    {
        _target = null;
        _selecting = false;
        SetState(ScreenRecorderState.Idle);
    }

    private void UpdateTray()
    {
        var tray = _services.GetService<ITrayPresence>();
        if (tray is null)
        {
            return;
        }

        tray.SetIndicator(TrayIndicatorId, IsRecording
            ? new TrayIndicator { Tint = RecordingTint, TooltipLine = LiveCaption, Priority = 100 }
            : null);
    }

    private void SweepInBackground()
    {
        var root = TakeFolders.Root(_paths);
        var owned = _session?.TakeFolder is { } active ? new[] { active } : [];
        _ = Task.Run(() =>
        {
            try
            {
                TakeSweeper.Sweep(root, owned, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Log.Warn("recorder", "Sweeping old takes failed.", ex);
            }
        });
    }

    private void Hud(string message, HudStyle style, string? icon = null) =>
        _services.GetService<IHud>()?.Show(message, style, icon);
}
