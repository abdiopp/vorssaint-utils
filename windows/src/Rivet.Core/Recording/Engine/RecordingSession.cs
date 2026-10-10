// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine.Audio;
using Rivet.Core.Recording.Engine.Pointer;

namespace Rivet.Core.Recording.Engine;

/// <summary>The platform pieces one recording needs.</summary>
public sealed record RecordingServices(
    IVideoCaptureBackend Video,
    IAudioCaptureBackend Audio,
    ICursorProbe Cursor,
    IRecorderSystem System,
    IInputHooks Hooks,
    IHostClock Clock);

public sealed record RecordingSessionOptions
{
    public required RecordingTarget Target { get; init; }

    /// <summary>The take folder (created by the caller, <see cref="TakeFolders.Create"/>).</summary>
    public required string TakeFolder { get; init; }

    public int FrameRate { get; init; } = 60;

    public bool SystemAudio { get; init; }

    public bool Microphone { get; init; }

    /// <summary>Capture endpoint id; null or empty follows the Windows default input.</summary>
    public string? MicrophoneDevice { get; init; }

    public string AppVersion { get; init; } = string.Empty;
}

public sealed record RecordingOutcome
{
    /// <summary>The take holds a finished master and its manifest.</summary>
    public required bool Written { get; init; }

    public TakeManifest? Manifest { get; init; }

    /// <summary>Recording seconds (pauses removed) at the stop.</summary>
    public double Duration { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// One recording, from "writer opened" to "take.json written" (spec 02 §3.6,
/// §3.16). Start order: open the master; create the system-audio stream; set
/// the clock origin immediately before video capture starts; start system
/// audio, then the microphone (its failure is reported and recording goes
/// on); then the pointer and typing samplers.
/// <para>
/// Cancellation contract: a stop may arrive while start is suspended at any
/// await. Exactly one of "start failed" and "stop" owns the file: a failing
/// start cancels it, a stop finalizes it. Stop waits until start has fully
/// unwound before it touches anything, and once stopping begins late
/// callbacks are ignored. The manifest is written last, after the master,
/// the WAVs and the side tracks are complete.
/// </para>
/// </summary>
public sealed class RecordingSession : IAsyncDisposable
{
    public const string SystemAudioFile = "system.wav";
    public const string MicrophoneFile = "mic.wav";
    public const string VideoFile = "take.mp4";

    private readonly RecordingSessionOptions _options;
    private readonly RecordingServices _services;
    private readonly object _stopGate = new();
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private readonly TaskCompletionSource _startCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<RecordingOutcome>? _stopTask;
    private Exception? _startFailure;
    private volatile bool _stopping;
    private bool _startCalled;
    private IVideoCaptureSession? _video;
    private IAudioCaptureSession? _systemAudio;
    private IAudioCaptureSession? _microphone;
    private AudioTrackWriter? _systemWriter;
    private AudioTrackWriter? _microphoneWriter;
    private PointerRecorder? _pointer;
    private TypingRecorder? _typing;
    private int _endedRaised;

    public RecordingSession(RecordingSessionOptions options, RecordingServices services)
    {
        _options = options;
        _services = services;
    }

    public PauseClock Clock { get; } = new();

    public RecordingSessionOptions Options => _options;

    /// <summary>Capture ended on its own (window closed, display changed, device lost). Raised once, on any thread.</summary>
    public event EventHandler<CaptureEndedEventArgs>? CaptureEnded;

    /// <summary>The microphone could not be opened or failed during the recording. Raised on any thread.</summary>
    public event EventHandler? MicrophoneUnavailable;

    /// <summary>System audio could not be opened or failed during the recording. Raised on any thread.</summary>
    public event EventHandler? SystemAudioUnavailable;

    /// <summary>Capture is running (the origin is set).</summary>
    public bool IsCapturing => Clock.HasOrigin && !_stopping;

    public bool IsPaused => Clock.IsPaused;

    public bool IsStopping => _stopping;

    /// <summary>Recording seconds now (frozen while paused).</summary>
    public double Elapsed => Clock.Elapsed(_services.Clock.Now);

    public bool HasMicrophone => _microphoneWriter is not null;

    public string TakeFolder => _options.TakeFolder;

    /// <summary>Opens everything and starts capturing. Throws when the recording cannot start (the take is then left for the caller to delete).</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            if (_startCalled)
            {
                throw new InvalidOperationException("A recording session starts once.");
            }

            _startCalled = true;
        }

        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _startFailure = ex;
            await CleanUpFailedStartAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _startCompleted.TrySetResult();
        }
    }

    public bool Pause() => IsCapturing && Clock.Pause(_services.Clock.Now);

    public bool Resume() => !_stopping && Clock.Resume(_services.Clock.Now);

    /// <summary>Stops and finalizes the take. Idempotent; safe at any point of start.</summary>
    public Task<RecordingOutcome> StopAsync() => StopOnce(discard: false);

    /// <summary>Stops without finalizing anything; the caller deletes the take.</summary>
    public Task<RecordingOutcome> DiscardAsync() => StopOnce(discard: true);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("recorder", "Stopping the recording on dispose failed.", ex);
        }
    }

    private Task<RecordingOutcome> StopOnce(bool discard)
    {
        lock (_stopGate)
        {
            return _stopTask ??= StopCoreAsync(discard);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.TakeFolder);

        // 1. The writer: open the master and prepare capture (not started yet).
        _video = await _services.Video.CreateAsync(new VideoCaptureRequest
        {
            Target = _options.Target,
            FrameRate = _options.FrameRate,
            OutputPath = Path.Combine(_options.TakeFolder, VideoFile),
            Clock = _services.Clock,
        }, cancellationToken).ConfigureAwait(false);
        _video.Ended += OnVideoEnded;
        if (_stopping)
        {
            return;
        }

        // 2. The system-audio stream, created before capture so its head is short.
        if (_options.SystemAudio)
        {
            await OpenSystemAudioAsync(cancellationToken).ConfigureAwait(false);
            if (_stopping)
            {
                return;
            }
        }

        // 3. Origin immediately before capture starts; the first frame is forced to t = 0.
        Clock.Begin(_services.Clock.Now);
        _video.Start(Clock);

        // 4. System audio before the microphone, to keep the silent head short.
        if (_systemAudio is { } system)
        {
            try
            {
                system.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("recorder", "System audio did not start.", ex);
                DropSystemAudio();
                SystemAudioUnavailable?.Invoke(this, EventArgs.Empty);
            }
        }

        // 5. Microphone; failure is reported, the recording goes on.
        if (_options.Microphone && !_stopping)
        {
            await OpenMicrophoneAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_stopping)
        {
            return;
        }

        // 6. Samplers.
        var target = _options.Target;
        IPointerRegion region = target.Kind == RecordingTargetKind.Window
            ? new WindowPointerRegion(_services.System, target.Window, () => _video.ContentSize, _video.Width, _video.Height, target.Region)
            : new FixedPointerRegion(target.Region);
        _pointer = new PointerRecorder(_services.Cursor, region, Clock, _services.Clock, _services.System, _services.Hooks, target.Monitor.Scale);
        _pointer.Start();
        _typing = new TypingRecorder(_services.Hooks, Clock, _services.Clock);
        _typing.Start();
        Log.Info("recorder", $"Recording {target.Kind} {target.Region.Width}×{target.Region.Height} → {_video.Width}×{_video.Height} @ {_options.FrameRate} fps"
                             + $" (system audio: {_systemAudio?.Description ?? "off"}, microphone: {_microphone?.Description ?? "off"}).");
    }

    private async Task OpenSystemAudioAsync(CancellationToken cancellationToken)
    {
        IAudioCaptureSession? system = null;
        try
        {
            system = await _services.Audio.OpenSystemAudioAsync(cancellationToken).ConfigureAwait(false);
            var writer = new AudioTrackWriter(Path.Combine(_options.TakeFolder, SystemAudioFile), Clock);
            system.PacketAvailable += writer.Append;
            system.Failed += (_, reason) =>
            {
                Log.Warn("recorder", $"System audio stopped: {reason}");
                if (!_stopping)
                {
                    SystemAudioUnavailable?.Invoke(this, EventArgs.Empty);
                }
            };
            _systemAudio = system;
            _systemWriter = writer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("recorder", "System audio is unavailable.", ex);
            if (_systemAudio is null)
            {
                SafeDispose(system);
            }

            DropSystemAudio();
            SystemAudioUnavailable?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task OpenMicrophoneAsync(CancellationToken cancellationToken)
    {
        IAudioCaptureSession? microphone = null;
        AudioTrackWriter? writer = null;
        try
        {
            microphone = await _services.Audio.OpenMicrophoneAsync(_options.MicrophoneDevice, cancellationToken).ConfigureAwait(false);
            writer = new AudioTrackWriter(Path.Combine(_options.TakeFolder, MicrophoneFile), Clock);
            microphone.PacketAvailable += writer.Append;
            microphone.Failed += (_, reason) =>
            {
                Log.Warn("recorder", $"Microphone stopped: {reason}");
                if (!_stopping)
                {
                    MicrophoneUnavailable?.Invoke(this, EventArgs.Empty);
                }
            };
            _microphone = microphone;
            _microphoneWriter = writer;
            if (!_stopping)
            {
                microphone.Start();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("recorder", "The microphone is unavailable.", ex);
            _microphone = null;
            _microphoneWriter = null;
            SafeDispose(microphone);
            writer?.Dispose();
            TryDelete(Path.Combine(_options.TakeFolder, MicrophoneFile));
            MicrophoneUnavailable?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DropSystemAudio()
    {
        SafeDispose(_systemAudio);
        _systemAudio = null;
        _systemWriter?.Dispose();
        _systemWriter = null;
        TryDelete(Path.Combine(_options.TakeFolder, SystemAudioFile));
    }

    private async Task CleanUpFailedStartAsync()
    {
        _stopping = true;
        _pointer?.Dispose();
        _typing?.Dispose();
        SafeStop(_systemAudio);
        SafeStop(_microphone);
        SafeDispose(_systemAudio);
        SafeDispose(_microphone);
        _systemWriter?.Dispose();
        _microphoneWriter?.Dispose();
        if (_video is { } video)
        {
            try
            {
                await video.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("recorder", "Cancelling the master failed.", ex);
            }

            await SafeDisposeAsync(video).ConfigureAwait(false);
        }
    }

    private async Task<RecordingOutcome> StopCoreAsync(bool discard)
    {
        var stopTime = _services.Clock.Now;
        _stopping = true;
        bool started;
        lock (_stopGate)
        {
            started = _startCalled;
        }

        if (started)
        {
            await _startCompleted.Task.ConfigureAwait(false);
        }

        if (_startFailure is { } failure)
        {
            return new RecordingOutcome { Written = false, Error = failure.Message };
        }

        var end = Clock.Elapsed(stopTime);

        // From the stop instant on the timeline is closed: samples, clicks and
        // keys that arrive while the sources wind down are rejected, so nothing
        // lands after the end.
        Clock.Pause(stopTime);
        try
        {
            // Stop the streams concurrently and wait for all of them.
            var audioStopped = Task.Run(() =>
            {
                SafeStop(_systemAudio);
                SafeStop(_microphone);
            });
            VideoCaptureResult video;
            if (_video is null)
            {
                video = new VideoCaptureResult { Written = false, Error = "The recording stopped before the master was opened." };
            }
            else if (discard || !Clock.HasOrigin)
            {
                await _video.CancelAsync().ConfigureAwait(false);
                video = new VideoCaptureResult { Written = false, Error = discard ? "Discarded." : "The recording stopped before capture started." };
            }
            else
            {
                video = await _video.FinishAsync(end).ConfigureAwait(false);
            }

            await audioStopped.ConfigureAwait(false);
            var pointer = _pointer?.Stop();
            var typing = _typing?.Stop() ?? [];

            if (discard || !video.Written)
            {
                _systemWriter?.Dispose();
                _microphoneWriter?.Dispose();
                if (!discard)
                {
                    Log.Warn("recorder", $"Nothing was recorded: {video.Error}");
                }

                return new RecordingOutcome { Written = false, Duration = end, Error = video.Error };
            }

            var audio = new List<TakeAudio>();
            if (FinishTrack(_systemWriter, end))
            {
                audio.Add(new TakeAudio { Source = TakeAudioSource.System, File = SystemAudioFile });
            }

            if (FinishTrack(_microphoneWriter, end))
            {
                audio.Add(new TakeAudio { Source = TakeAudioSource.Microphone, File = MicrophoneFile });
            }

            var hasPointer = pointer is { IsEmpty: false } && WriteAtomically(Path.Combine(_options.TakeFolder, PointerTrack.FileName), pointer.Write);
            var hasTyping = typing.Count > 0 && WriteAtomically(Path.Combine(_options.TakeFolder, TypingTrack.FileName), new TypingTrack(typing).Write);

            var manifest = BuildManifest(video, end, audio, hasPointer, hasTyping);
            manifest.Write(_options.TakeFolder);
            Log.Info("recorder", $"Recorded {end:0.00} s, {video.FramesWritten} frames, {pointer?.Samples.Count ?? 0} pointer samples, {pointer?.Clicks.Count ?? 0} clicks, {typing.Count} keys.");
            return new RecordingOutcome { Written = true, Manifest = manifest, Duration = end };
        }
        catch (Exception ex)
        {
            Log.Error("recorder", "Finishing the recording failed.", ex);
            return new RecordingOutcome { Written = false, Duration = end, Error = ex.Message };
        }
        finally
        {
            _pointer?.Dispose();
            _typing?.Dispose();
            SafeDispose(_systemAudio);
            SafeDispose(_microphone);
            _systemWriter?.Dispose();
            _microphoneWriter?.Dispose();
            if (_video is { } video)
            {
                await SafeDisposeAsync(video).ConfigureAwait(false);
            }
        }
    }

    private TakeManifest BuildManifest(VideoCaptureResult video, double end, List<TakeAudio> audio, bool hasPointer, bool hasTyping)
    {
        var target = _options.Target;
        var monitor = target.Monitor;
        TakeWindow? window = null;
        if (target.Kind == RecordingTargetKind.Window && target.Window != 0)
        {
            var (title, process) = _services.System.DescribeWindow(target.Window);
            window = new TakeWindow { Title = title, Process = process };
        }

        return new TakeManifest
        {
            AppVersion = _options.AppVersion,
            CreatedAt = _createdAt,
            Capture = new TakeCapture
            {
                Kind = target.ManifestKind,
                Fps = _options.FrameRate,
                Monitor = new TakeMonitor
                {
                    Device = monitor.Id,
                    DpiScale = monitor.Scale,
                    RectPx = [monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height],
                },
                RegionPx = [target.Region.X, target.Region.Y, target.Region.Width, target.Region.Height],
                Window = window,
            },
            Video = new TakeVideo
            {
                Codec = video.Codec,
                File = VideoFile,
                Width = video.Width,
                Height = video.Height,
                Vfr = video.VariableFrameRate,
                DurationSeconds = Math.Round(end, 6),
            },
            Audio = audio,
            PointerTrack = hasPointer ? new TakePointerTrack() : null,
            TypingTrack = hasTyping ? TypingTrack.FileName : null,
        };
    }

    private static bool FinishTrack(AudioTrackWriter? writer, double end)
    {
        if (writer is null)
        {
            return false;
        }

        try
        {
            writer.Finish(end);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("recorder", $"Finishing {Path.GetFileName(writer.Path)} failed.", ex);
            TryDelete(writer.Path);
            return false;
        }
    }

    /// <summary>Writes through the shared track writers into a temporary file, then moves it into place.</summary>
    private static bool WriteAtomically(string path, Action<string> write)
    {
        var temp = path + ".tmp";
        try
        {
            write(temp);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("recorder", $"Writing {Path.GetFileName(path)} failed.", ex);
            TryDelete(temp);
            return false;
        }
    }

    private void OnVideoEnded(object? sender, CaptureEndedEventArgs e)
    {
        if (_stopping || Interlocked.Exchange(ref _endedRaised, 1) == 1)
        {
            return;
        }

        Log.Warn("recorder", $"Capture ended on its own: {e.Reason} {e.Detail}");
        CaptureEnded?.Invoke(this, e);
    }

    private static void SafeStop(IAudioCaptureSession? session)
    {
        try
        {
            session?.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Stopping an audio stream failed.", ex);
        }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Releasing a capture resource failed.", ex);
        }
    }

    private static async Task SafeDisposeAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Releasing the video capture failed.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
