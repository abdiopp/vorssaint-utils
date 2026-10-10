// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Rivet.Imaging.Recording;
using SkiaSharp;

namespace Rivet.Platform.Fake.Recording;

/// <summary>
/// Development stand-in for the picture: it "captures" frames at the frame
/// rate without encoding anything. <c>take.mp4</c> is created as an EMPTY
/// placeholder so the take folder has its real layout; it is not playable.
/// Everything else in the take (WAVs, pointer and typing tracks, manifest) is
/// produced by the real Core engine.
/// </summary>
public sealed class FakeVideoCaptureBackend : IVideoCaptureBackend
{
    public RecorderAvailability Availability { get; set; } = RecorderAvailability.Available;

    /// <summary>Tests: the next <see cref="CreateAsync"/> throws.</summary>
    public bool FailNextCreate { get; set; }

    public FakeVideoCaptureSession? LastSession { get; private set; }

    public RecorderAvailability CheckAvailability() => Availability;

    public Task<IVideoCaptureSession> CreateAsync(VideoCaptureRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextCreate)
        {
            FailNextCreate = false;
            throw new InvalidOperationException("Fake capture failure.");
        }

        File.WriteAllBytes(request.OutputPath, []);
        var session = new FakeVideoCaptureSession(request);
        LastSession = session;
        return Task.FromResult<IVideoCaptureSession>(session);
    }
}

public sealed class FakeVideoCaptureSession : IVideoCaptureSession
{
    private readonly VideoCaptureRequest _request;
    private readonly object _gate = new();
    private Timer? _timer;
    private int _frames;
    private bool _finished;

    public FakeVideoCaptureSession(VideoCaptureRequest request)
    {
        _request = request;
        (Width, Height) = VideoSizeLimits.Fit(request.Target.Region.Width, request.Target.Region.Height);
        ContentSize = (request.Target.Region.Width, request.Target.Region.Height);
    }

    public int Width { get; }

    public int Height { get; }

    public (int Width, int Height) ContentSize { get; set; }

    public bool Started { get; private set; }

    public bool Cancelled { get; private set; }

    public int FramesWritten => Volatile.Read(ref _frames);

    public double? FinishedAt { get; private set; }

    public event EventHandler<CaptureEndedEventArgs>? Ended;

    public void Start(PauseClock clock)
    {
        Started = true;
        Interlocked.Increment(ref _frames); // the first frame, forced to t = 0
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _request.FrameRate));
        _timer = new Timer(_ =>
        {
            if (!clock.IsPaused)
            {
                Interlocked.Increment(ref _frames);
            }
        }, null, interval, interval);
    }

    /// <summary>Tests: simulate the window closing or the display changing.</summary>
    public void RaiseEnded(CaptureEndReason reason) => Ended?.Invoke(this, new CaptureEndedEventArgs(reason, "fake"));

    public Task<VideoCaptureResult> FinishAsync(double endTime)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_finished)
            {
                return Task.FromResult(new VideoCaptureResult { Written = false, Error = "Already finished." });
            }

            _finished = true;
            FinishedAt = endTime;
            var frames = FramesWritten + 1; // the last frame, re-appended at the stop time
            Log.Info("recorder", $"[fake] master would hold {frames} frames ({Width}×{Height}, {endTime:0.00} s).");
            return Task.FromResult(new VideoCaptureResult
            {
                Written = Started,
                FramesWritten = frames,
                Width = Width,
                Height = Height,
                Error = Started ? null : "Capture never started.",
            });
        }
    }

    public Task CancelAsync()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _finished = true;
            Cancelled = true;
        }

        try
        {
            File.Delete(_request.OutputPath);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        _timer = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Synthetic audio: a quiet 440 Hz tone for system sound, a 220 Hz tone for the microphone, in 10 ms packets on the host clock.</summary>
public sealed class FakeAudioCaptureBackend : IAudioCaptureBackend
{
    public MicrophoneStatus MicrophoneStatus { get; set; } = MicrophoneStatus.Available;

    public bool SystemAudioFails { get; set; }

    public FakeAudioSession? LastSystem { get; private set; }

    public FakeAudioSession? LastMicrophone { get; private set; }

    public Task<IAudioCaptureSession> OpenSystemAudioAsync(CancellationToken cancellationToken)
    {
        if (SystemAudioFails)
        {
            throw new InvalidOperationException("Fake loopback failure.");
        }

        LastSystem = new FakeAudioSession("fake loopback", 440);
        return Task.FromResult<IAudioCaptureSession>(LastSystem);
    }

    public Task<IAudioCaptureSession> OpenMicrophoneAsync(string? deviceId, CancellationToken cancellationToken)
    {
        if (MicrophoneStatus != MicrophoneStatus.Available)
        {
            throw new UnauthorizedAccessException("Fake microphone is blocked.");
        }

        LastMicrophone = new FakeAudioSession($"fake microphone {deviceId}".Trim(), 220);
        return Task.FromResult<IAudioCaptureSession>(LastMicrophone);
    }

    public MicrophoneStatus ProbeMicrophone(string? deviceId) => MicrophoneStatus;

    public IReadOnlyList<AudioDeviceInfo> ListMicrophones() =>
    [
        new AudioDeviceInfo("fake-mic-usb", "Microphone (Fake USB Audio)", true),
        new AudioDeviceInfo("fake-mic-headset", "Headset Microphone (Fake)", false),
    ];
}

public sealed class FakeAudioSession(string description, double frequency) : IAudioCaptureSession
{
    private const int FramesPerPacket = 480;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private long _emitted;

    public string Description { get; } = description;

    public int PacketsDelivered { get; private set; }

    public event AudioPacketHandler? PacketAvailable;

    public event EventHandler<string>? Failed;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "FakeAudio" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(1));
        }

        lock (_gate)
        {
            PacketAvailable = null;
        }
    }

    /// <summary>Tests: simulate the device disappearing.</summary>
    public void RaiseFailed() => Failed?.Invoke(this, "fake device removed");

    public void Dispose() => Stop();

    private void Run()
    {
        var format = PcmFormat.Take;
        var start = QpcClock.Instance.Now;
        var packet = new byte[FramesPerPacket * format.BlockAlign];
        while (!_stop)
        {
            for (var i = 0; i < FramesPerPacket; i++)
            {
                var t = (_emitted + i) / (double)format.SampleRate;
                var sample = (short)(Math.Sin(2 * Math.PI * frequency * t) * 3000);
                BitConverter.TryWriteBytes(packet.AsSpan(i * 4), sample);
                BitConverter.TryWriteBytes(packet.AsSpan((i * 4) + 2), sample);
            }

            var hostTime = start + (_emitted / (double)format.SampleRate);
            lock (_gate)
            {
                PacketAvailable?.Invoke(packet, format, hostTime, false);
            }

            PacketsDelivered++;
            _emitted += FramesPerPacket;
            var due = start + (_emitted / (double)format.SampleRate) - QpcClock.Instance.Now;
            if (due > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(due));
            }
        }
    }
}

/// <summary>A pointer that wanders over the fake 1920×1080 display, switching between an arrow and a hand every two seconds.</summary>
public sealed class FakeCursorProbe : ICursorProbe
{
    private readonly double _start = QpcClock.Instance.Now;

    /// <summary>Tests: force a reading instead of the synthetic path.</summary>
    public CursorReading? Override { get; set; }

    public CursorReading Read()
    {
        if (Override is { } forced)
        {
            return forced;
        }

        var t = QpcClock.Instance.Now - _start;
        var x = 960 + (600 * Math.Sin(t * 0.9));
        var y = 540 + (300 * Math.Sin(t * 1.7));
        var handle = (int)(t / 2) % 2 == 0 ? 1 : 2;
        return new CursorReading(new PixelPoint((int)x, (int)y), true, handle);
    }

    public CursorShapeSnapshot? CaptureShape(nint handle, double monitorScale)
    {
        var size = 32;
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var path = new SKPath();
        if (handle == 2)
        {
            path.AddRoundRect(new SKRect(9, 6, 21, 28), 5, 5);
        }
        else
        {
            path.MoveTo(2, 2);
            path.LineTo(2, 24);
            path.LineTo(8, 18);
            path.LineTo(12, 27);
            path.LineTo(16, 25);
            path.LineTo(12, 17);
            path.LineTo(20, 17);
            path.Close();
        }

        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);
        using var image = surface.Snapshot();
        var buffer = Imaging.Skia.SkiaConvert.ToPixelBuffer(image);
        return CursorShapeFactory.Create(buffer, handle == 2 ? 14 : 2, handle == 2 ? 6 : 2, 1);
    }
}

public sealed class FakeRecorderSystem : IRecorderSystem
{
    public int SleepPreventions { get; private set; }

    public WindowRects? Window { get; set; } = new WindowRects(new PixelRect(193, 113, 1214, 814), new PixelRect(200, 120, 1200, 800));

    public IDisposable PreventSleep(string reason)
    {
        SleepPreventions++;
        Log.Info("recorder", $"[fake] keeping the PC awake: {reason}");
        return new Release(() => SleepPreventions--);
    }

    public WindowRects? GetWindowRects(nint window) => Window;

    public (string? Title, string? Process) DescribeWindow(nint window) => ("Fake window", "fake.exe");

    public IPrecisionTimer CreateTimer() => new SleepTimer();

    public void Beep() => Log.Info("recorder", "[fake] beep");

    private sealed class SleepTimer : IPrecisionTimer
    {
        public void Wait(TimeSpan duration)
        {
            if (duration > TimeSpan.Zero)
            {
                Thread.Sleep(duration);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>Copies the placeholder master (there is no encoder on the development host).</summary>
public sealed class FakeRawTakeExporter : IRawTakeExporter
{
    public Task<bool> ExportAsync(string takeFolder, TakeManifest manifest, string outputPath, CancellationToken cancellationToken)
    {
        var master = Path.Combine(takeFolder, manifest.Video.File);
        if (!File.Exists(master))
        {
            return Task.FromResult(false);
        }

        File.Copy(master, outputPath, overwrite: false);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Development importer: validates and copies the movie like the real one,
/// but cannot probe it, so the manifest carries a placeholder 1280×720 size,
/// no duration and no sound.
/// </summary>
public sealed class FakeTakeImporter(Rivet.Core.App.AppPaths paths) : ITakeImporter
{
    public Task<string> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        TakeImportRules.Validate(sourcePath, DiskSpace.FreeBytes(TakeFolders.Root(paths)));
        var folder = TakeFolders.Create(paths);
        var extension = Path.GetExtension(sourcePath);
        var videoFile = "take" + (string.IsNullOrEmpty(extension) ? ".mp4" : extension.ToLowerInvariant());
        File.Copy(sourcePath, Path.Combine(folder, videoFile));
        TakeImportRules.Manifest(videoFile, "unknown", 1280, 720, 30, 0, hasSound: false, "dev").Write(folder);
        return Task.FromResult(folder);
    }
}
