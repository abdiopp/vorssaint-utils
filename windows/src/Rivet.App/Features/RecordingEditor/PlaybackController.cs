// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.RecordingEditor;
using Rivet.Core.RecordingEditor.Audio;
using Avalonia.Threading;
using Rivet.Imaging.RecordingEditor;
using SkiaSharp;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The preview player (spec 02 §3.20.6, §6.17). A background thread decodes
/// the master and composes each frame with the same compositor as the export,
/// at the editor frame rate on the edited 1× clock (cut stretches are absent,
/// not skipped). The audio device is the master clock while playing; frames
/// are dropped rather than letting the picture fall behind. The stage draws
/// the latest frame from a double buffer.
/// </summary>
public sealed class PlaybackController : IDisposable
{
    private readonly EditorSession _session;
    private readonly IVideoFrameSourceFactory _sources;
    private readonly IAudioPlaybackFactory? _audioFactory;
    private readonly object _gate = new();
    private readonly object _frameGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = new();
    private Thread? _thread;

    // Requested state (UI thread → worker), guarded by _gate.
    private EditDocument? _pendingDocument;
    private bool _pendingRaw;
    private bool _documentDirty;
    private double _outputScale = 0.5;
    private bool _scaleDirty = true;
    private double _target;
    private bool _renderRequested = true;
    private bool _playing;
    private double _playStart;
    private bool _disposed;

    // Display (worker → UI), guarded by _frameGate.
    private SKBitmap? _front;
    private SKBitmap? _back;
    private bool _frontIsRaw;

    private IAudioPlayback? _audio;
    private EditedAudioMix? _mix;
    private volatile float _outputTime;

    public PlaybackController(EditorSession session, IServiceProvider services)
    {
        _session = session;
        _sources = services.GetRequiredService<IVideoFrameSourceFactory>();
        _audioFactory = services.GetService<IAudioPlaybackFactory>();
    }

    /// <summary>Current output (edited, 1×) time.</summary>
    public double OutputTime => _outputTime;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _playing;
            }
        }
    }

    /// <summary>Raised on the UI thread when a new frame is ready to draw.</summary>
    public event Action? FrameReady;

    /// <summary>Raised on the UI thread when playback starts or stops.</summary>
    public event Action? StateChanged;

    /// <summary>Frames composed so far (tests wait for the first one).</summary>
    public int FramesRendered { get; private set; }

    public void Start()
    {
        if (_thread is not null || !_session.IsReady)
        {
            return;
        }

        lock (_gate)
        {
            _pendingDocument ??= _session.Document;
            _documentDirty = true;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "RecordingPreview", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    // ── Requests from the UI thread ───────────────────────────────────

    public void UpdateDocument(EditDocument document, bool raw)
    {
        lock (_gate)
        {
            var timingChanged = _pendingDocument is { } previous && EditDocument.AffectsTiming(previous, document);
            _pendingDocument = document;
            _pendingRaw = raw;
            _documentDirty = true;
            if (timingChanged && _playing)
            {
                StopPlayingLocked();
            }

            _target = Math.Min(_target, EditTimeline.For(document, _session.Duration).OutputDuration);
        }

        _wake.Set();
    }

    /// <summary>The stage's size in device pixels: the preview renders no larger than it shows.</summary>
    public void SetViewport(double pixelWidth, double pixelHeight)
    {
        if (pixelWidth < 8 || pixelHeight < 8)
        {
            return;
        }

        var doc = _session.Document;
        var canvas = CanvasLayout.Compute(_session.SourceWidth, _session.SourceHeight, doc.BackdropStyle, doc.Aspect, 1);
        var fit = Math.Min(pixelWidth / canvas.CanvasWidth, pixelHeight / canvas.CanvasHeight);
        var scale = Math.Clamp(Math.Ceiling(fit * 8) / 8, 0.125, 1);
        lock (_gate)
        {
            if (Math.Abs(scale - _outputScale) < 0.001)
            {
                return;
            }

            _outputScale = scale;
            _scaleDirty = true;
        }

        _wake.Set();
    }

    public void Seek(double outputTime)
    {
        var duration = _session.Timeline.OutputDuration;
        var t = Math.Clamp(outputTime, 0, Math.Max(0, duration));
        bool restart;
        lock (_gate)
        {
            restart = _playing;
            if (restart)
            {
                StopPlayingLocked();
            }

            _target = t;
            _renderRequested = true;
            _outputTime = (float)t;
        }

        if (restart)
        {
            Play();
        }

        _wake.Set();
    }

    public void Play()
    {
        if (!_session.IsReady)
        {
            return;
        }

        var duration = _session.Timeline.OutputDuration;
        lock (_gate)
        {
            if (_playing)
            {
                return;
            }

            if (_target >= duration - 0.05)
            {
                _target = 0;
            }

            _playStart = _target;
            _playing = true;
            _clock.Restart();
            StartAudioLocked();
        }

        _wake.Set();
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_playing)
            {
                return;
            }

            StopPlayingLocked();
        }

        _wake.Set();
        StateChanged?.Invoke();
    }

    public void TogglePlay()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Kept tracks or gains changed: the mix reads them live, nothing to rebuild.</summary>
    public void AudioChanged()
    {
    }

    /// <summary>Draws the latest frame under the frame lock (called by the stage on the render thread).</summary>
    public void WithFrame(Action<SKBitmap?, bool> draw)
    {
        lock (_frameGate)
        {
            draw(_front, _frontIsRaw);
        }
    }

    // ── Worker ────────────────────────────────────────────────────────

    private void Run()
    {
        FramePlan? plan = null;
        FrameCompositor? compositor = null;
        IVideoFrameSource? source = null;
        var decodeScale = 0.0;
        var raw = false;
        var lastIndex = -1;
        using var assets = new CompositorAssets(_session.Take.Pointer);
        try
        {
            while (true)
            {
                EditDocument? rebuild = null;
                double scale;
                bool playing;
                double target;
                bool render;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        break;
                    }

                    if (_documentDirty || _scaleDirty)
                    {
                        rebuild = _pendingDocument ?? _session.Document;
                        raw = _pendingRaw;
                        _documentDirty = false;
                        _scaleDirty = false;
                        _renderRequested = true;
                    }

                    scale = _outputScale;
                    playing = _playing;
                    target = _target;
                    render = _renderRequested;
                    _renderRequested = false;
                }

                if (rebuild is not null)
                {
                    compositor?.Dispose();
                    plan = FramePlan.Build(rebuild, _session.Duration, _session.SourceWidth, _session.SourceHeight, _session.Fps,
                        _session.Take.Pointer, _session.Take.PointerVersion, scale);
                    compositor = new FrameCompositor(plan, assets);
                    var wanted = DecodeScale(plan, scale);
                    if (source is null || Math.Abs(wanted - decodeScale) > 0.01)
                    {
                        source?.Dispose();
                        source = OpenSource(wanted);
                        decodeScale = wanted;
                    }

                    lastIndex = -1;
                }

                if (plan is null || compositor is null || source is null)
                {
                    _wake.WaitOne(100);
                    continue;
                }

                if (playing)
                {
                    var elapsed = PlayedSeconds();
                    var t = _playStart + elapsed;
                    if (t >= plan.Timeline.OutputDuration - 0.02)
                    {
                        // Reached the end: pause and go back to the start.
                        lock (_gate)
                        {
                            StopPlayingLocked();
                            _target = 0;
                            _renderRequested = true;
                        }

                        _outputTime = 0;
                        Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
                        continue;
                    }

                    _outputTime = (float)t;
                    var index = (int)Math.Floor(t * plan.Fps);
                    if (index != lastIndex)
                    {
                        lastIndex = index;
                        RenderFrame(plan, compositor, source, index / (double)plan.Fps, raw);
                    }

                    var next = ((index + 1) / (double)plan.Fps) - t;
                    _wake.WaitOne(TimeSpan.FromMilliseconds(Math.Clamp(next * 1000, 1, 16)));
                    continue;
                }

                if (render)
                {
                    lastIndex = plan.IndexFor(target);
                    RenderFrame(plan, compositor, source, target, raw);
                }

                _wake.WaitOne(250);
            }
        }
        catch (Exception ex)
        {
            Log.Error("recording-editor", "The preview stopped.", ex);
        }
        finally
        {
            compositor?.Dispose();
            source?.Dispose();
        }
    }

    private double PlayedSeconds()
    {
        double? device = null;
        try
        {
            // The UI thread may stop the device at any moment; then the stopwatch takes over.
            device = _audio?.PlayedSeconds;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            device = null;
        }

        return device ?? _clock.Elapsed.TotalSeconds;
    }

    private IVideoFrameSource? OpenSource(double scale)
    {
        try
        {
            var options = scale >= 0.99
                ? new VideoOpenOptions { CacheSize = 12 }
                : new VideoOpenOptions
                {
                    Width = RecorderMath.EvenSide(_session.SourceWidth * scale),
                    Height = RecorderMath.EvenSide(_session.SourceHeight * scale),
                    CacheSize = 12,
                };
            return _sources.Open(_session.Take.VideoPath, options);
        }
        catch (Exception ex) when (ex is IOException or MediaUnavailableException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("recording-editor", "The preview cannot decode the recording.", ex);
            return null;
        }
    }

    /// <summary>Decode no larger than the card shows at the plan's largest zoom (sharp when zoomed, cheap otherwise).</summary>
    private double DecodeScale(FramePlan plan, double outputScale)
    {
        var card = plan.Layout.Card;
        var needed = Math.Max(card.Width / (double)_session.SourceWidth, card.Height / (double)_session.SourceHeight) * Math.Max(1, plan.MaxZoom) * 1.1;
        return Math.Clamp(Math.Ceiling(needed * 8) / 8, 0.25, 1);
    }

    private void RenderFrame(FramePlan plan, FrameCompositor compositor, IVideoFrameSource source, double editedTime, bool raw)
    {
        VideoFrame? frame;
        try
        {
            frame = source.GetFrame(plan.Timeline.SourceTime(editedTime));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Log.Warn("recording-editor", "A preview frame could not be decoded.", ex);
            return;
        }

        if (frame is null)
        {
            return;
        }

        var width = raw ? frame.Pixels.Width : compositor.Width;
        var height = raw ? frame.Pixels.Height : compositor.Height;
        if (_back is null || _back.Width != width || _back.Height != height)
        {
            _back?.Dispose();
            _back = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        }

        using (var pinned = new PinnedFrame(frame.Pixels))
        using (var canvas = new SKCanvas(_back))
        {
            if (raw)
            {
                canvas.DrawImage(pinned.Image, 0, 0);
            }
            else
            {
                compositor.Render(canvas, pinned.Image, editedTime);
            }

            canvas.Flush();
        }

        lock (_frameGate)
        {
            (_front, _back) = (_back, _front);
            _frontIsRaw = raw;
        }

        FramesRendered++;
        Dispatcher.UIThread.Post(() => FrameReady?.Invoke());
    }

    // ── Audio ─────────────────────────────────────────────────────────

    private void StartAudioLocked()
    {
        if (_audioFactory is null)
        {
            return;
        }

        var tracks = new List<AudioTrackInput>();
        if (_session.Take.SystemAudioPath is { } system)
        {
            tracks.Add(new AudioTrackInput(system, () => _session.Document.KeepsSystemAudio ? _session.Document.SystemAudioGain : 0));
        }

        if (_session.Take.MicrophoneAudioPath is { } mic)
        {
            tracks.Add(new AudioTrackInput(mic, () => _session.Document.KeepsMicrophone ? _session.Document.MicrophoneGain : 0));
        }

        if (tracks.Count == 0)
        {
            return;
        }

        try
        {
            _mix = new EditedAudioMix(tracks, _session.Timeline.KeptRanges);
            _mix.Seek(_playStart);
            _audio = _audioFactory.Create();
            _audio?.Start(_mix);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn("recording-editor", "Preview audio could not start.", ex);
            StopAudioLocked();
        }
    }

    private void StopAudioLocked()
    {
        _audio?.Stop();
        _audio?.Dispose();
        _audio = null;
        _mix?.Dispose();
        _mix = null;
    }

    private void StopPlayingLocked()
    {
        var t = _playStart + PlayedSeconds();
        _playing = false;
        _clock.Stop();
        StopAudioLocked();
        _target = Math.Clamp(t, 0, Math.Max(0, _session.Timeline.OutputDuration));
        _outputTime = (float)_target;
        _renderRequested = true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _playing = false;
            StopAudioLocked();
        }

        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        lock (_frameGate)
        {
            _front?.Dispose();
            _back?.Dispose();
            _front = null;
            _back = null;
        }

        _wake.Dispose();
    }
}
