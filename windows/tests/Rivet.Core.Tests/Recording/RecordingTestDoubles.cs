// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Tests.Recording;

/// <summary>A host clock the test moves by hand.</summary>
internal sealed class ManualClock(double start = 1000) : IHostClock
{
    public double Now { get; set; } = start;

    public void Advance(double seconds) => Now += seconds;
}

internal sealed class TestHooks : IInputHooks
{
    private readonly List<KeyboardHookHandler> _keyboard = [];
    private readonly List<MouseHookHandler> _mouse = [];

    public int MouseSubscribers => _mouse.Count;

    public int KeyboardSubscribers => _keyboard.Count;

    public IDisposable SubscribeKeyboard(KeyboardHookHandler handler, int priority = 0)
    {
        lock (_keyboard)
        {
            _keyboard.Add(handler);
        }

        return new Release(() =>
        {
            lock (_keyboard)
            {
                _keyboard.Remove(handler);
            }
        });
    }

    public IDisposable SubscribeMouse(MouseHookHandler handler, int priority = 0)
    {
        lock (_mouse)
        {
            _mouse.Add(handler);
        }

        return new Release(() =>
        {
            lock (_mouse)
            {
                _mouse.Remove(handler);
            }
        });
    }

    public void Key(int vk, KeyAction action)
    {
        var e = new KeyboardHookEvent { VirtualKey = vk, Action = action };
        KeyboardHookHandler[] handlers;
        lock (_keyboard)
        {
            handlers = [.. _keyboard];
        }

        foreach (var handler in handlers)
        {
            handler(ref e);
        }
    }

    public void Mouse(MouseHookKind kind)
    {
        var e = new MouseHookEvent { Kind = kind };
        MouseHookHandler[] handlers;
        lock (_mouse)
        {
            handlers = [.. _mouse];
        }

        foreach (var handler in handlers)
        {
            handler(ref e);
        }
    }

    public void SendKeys(IReadOnlyList<(int VirtualKey, KeyAction Action)> strokes)
    {
    }

    public void SendText(string text)
    {
    }

    public void SendWheel(int delta, bool horizontal)
    {
    }

    public void SendMouse(MouseHookKind kind, int xButton = 0)
    {
    }

    public bool IsKeyDown(int virtualKey) => false;

    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

internal sealed class TestCursor : ICursorProbe
{
    public CursorReading Reading { get; set; } = new(new PixelPoint(0, 0), true, 1);

    public int Captures { get; private set; }

    /// <summary>Handle → picture identity (the same picture can come under several handles).</summary>
    public Dictionary<nint, ulong> Pictures { get; } = new() { [1] = 0xA, [2] = 0xB, [3] = 0xA };

    public CursorReading Read() => Reading;

    public CursorShapeSnapshot? CaptureShape(nint handle, double monitorScale)
    {
        Captures++;
        return Pictures.TryGetValue(handle, out var id)
            ? new CursorShapeSnapshot(id, 32, 32, 1, 1, [1, 2, 3, (byte)id])
            : null;
    }
}

internal sealed class TestSystem : IRecorderSystem
{
    public WindowRects? Window { get; set; }

    public int Awake { get; private set; }

    public IDisposable PreventSleep(string reason)
    {
        Awake++;
        return new Released(() => Awake--);
    }

    public WindowRects? GetWindowRects(nint window) => Window;

    public (string? Title, string? Process) DescribeWindow(nint window) => ("Notes", "notepad");

    public IPrecisionTimer CreateTimer() => new Timer();

    public void Beep()
    {
    }

    private sealed class Timer : IPrecisionTimer
    {
        public void Wait(TimeSpan duration) => Thread.Sleep(duration > TimeSpan.Zero ? duration : TimeSpan.Zero);

        public void Dispose()
        {
        }
    }

    private sealed class Released(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

internal sealed class TestVideoBackend : IVideoCaptureBackend
{
    public TaskCompletionSource? Gate { get; set; }

    public Exception? CreateFailure { get; set; }

    public TestVideoSession? Session { get; private set; }

    public Action<string>? OnFinish { get; set; }

    public RecorderAvailability CheckAvailability() => RecorderAvailability.Available;

    public async Task<IVideoCaptureSession> CreateAsync(VideoCaptureRequest request, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (CreateFailure is { } failure)
        {
            throw failure;
        }

        File.WriteAllBytes(request.OutputPath, [0]);
        Session = new TestVideoSession(request, OnFinish);
        return Session;
    }
}

internal sealed class TestVideoSession(VideoCaptureRequest request, Action<string>? onFinish) : IVideoCaptureSession
{
    public int Width => request.Target.Region.Width;

    public int Height => request.Target.Region.Height;

    public (int Width, int Height) ContentSize { get; set; } = (request.Target.Region.Width, request.Target.Region.Height);

    public bool Started { get; private set; }

    public bool Cancelled { get; private set; }

    public bool Disposed { get; private set; }

    public double? FinishedAt { get; private set; }

    public event EventHandler<CaptureEndedEventArgs>? Ended;

    public void Start(PauseClock clock)
    {
        Assert(clock.HasOrigin, "the origin is set before capture starts");
        Started = true;
    }

    public void RaiseEnded(CaptureEndReason reason) => Ended?.Invoke(this, new CaptureEndedEventArgs(reason));

    public Task<VideoCaptureResult> FinishAsync(double endTime)
    {
        onFinish?.Invoke(Path.GetDirectoryName(request.OutputPath)!);
        FinishedAt = endTime;
        return Task.FromResult(new VideoCaptureResult { Written = Started, FramesWritten = 2, Width = Width, Height = Height });
    }

    public Task CancelAsync()
    {
        Cancelled = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private static void Assert(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected: " + what);
        }
    }
}

internal sealed class TestAudioBackend : IAudioCaptureBackend
{
    public bool MicrophoneFails { get; set; }

    public bool SystemFails { get; set; }

    public TestAudioSession? System { get; private set; }

    public TestAudioSession? Microphone { get; private set; }

    public Task<IAudioCaptureSession> OpenSystemAudioAsync(CancellationToken cancellationToken)
    {
        if (SystemFails)
        {
            throw new InvalidOperationException("no loopback");
        }

        System = new TestAudioSession("system");
        return Task.FromResult<IAudioCaptureSession>(System);
    }

    public Task<IAudioCaptureSession> OpenMicrophoneAsync(string? deviceId, CancellationToken cancellationToken)
    {
        if (MicrophoneFails)
        {
            throw new UnauthorizedAccessException("blocked");
        }

        Microphone = new TestAudioSession("mic");
        return Task.FromResult<IAudioCaptureSession>(Microphone);
    }

    public MicrophoneStatus ProbeMicrophone(string? deviceId) => MicrophoneFails ? MicrophoneStatus.Denied : MicrophoneStatus.Available;

    public IReadOnlyList<AudioDeviceInfo> ListMicrophones() => [];
}

internal sealed class TestAudioSession(string description) : IAudioCaptureSession
{
    public string Description { get; } = description;

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public event AudioPacketHandler? PacketAvailable;

    public event EventHandler<string>? Failed;

    public void Start() => Started = true;

    public void Stop() => Stopped = true;

    public void Dispose() => Disposed = true;

    public void Deliver(byte[] data, double hostTime) => PacketAvailable?.Invoke(data, PcmFormat.Take, hostTime, false);

    public void Fail() => Failed?.Invoke(this, "gone");
}
