// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Recording.Engine;

public enum RecordingTargetKind
{
    Area,
    Window,
    Display,
}

/// <summary>What to record, resolved once when the user confirms (spec 02 §3.3) and never recomputed.</summary>
public sealed record RecordingTarget
{
    public required RecordingTargetKind Kind { get; init; }

    /// <summary>The monitor the selection was made on.</summary>
    public required ScreenInfo Monitor { get; init; }

    /// <summary>
    /// Physical virtual-screen pixels, snapped (§6.1). For a window: its frame
    /// when it was picked, clamped to the monitor (informative; the capture
    /// follows the window).
    /// </summary>
    public required PixelRect Region { get; init; }

    /// <summary>The picked window (HWND) for <see cref="RecordingTargetKind.Window"/>.</summary>
    public nint Window { get; init; }

    public TakeCaptureKind ManifestKind => Kind switch
    {
        RecordingTargetKind.Window => TakeCaptureKind.Window,
        RecordingTargetKind.Display => TakeCaptureKind.Display,
        _ => TakeCaptureKind.Area,
    };
}

/// <summary>Why the platform could refuse to record before anything starts.</summary>
public enum RecorderAvailability
{
    Available,

    /// <summary>Windows Graphics Capture is missing (Windows older than 10 2004, or blocked by policy).</summary>
    CaptureUnsupported,

    /// <summary>Media Foundation or its H.264 encoder is missing (Windows "N" editions without the Media Feature Pack).</summary>
    EncoderUnavailable,
}

public enum CaptureEndReason
{
    /// <summary>The recorded window was closed.</summary>
    WindowClosed,

    /// <summary>The recorded monitor was disconnected or changed resolution.</summary>
    DisplayChanged,

    /// <summary>The graphics device was reset or removed, or capture stopped on its own.</summary>
    CaptureLost,

    /// <summary>The encoder rejected a frame; what was written is kept.</summary>
    EncoderFailed,
}

public sealed class CaptureEndedEventArgs(CaptureEndReason reason, string? detail = null) : EventArgs
{
    public CaptureEndReason Reason { get; } = reason;

    /// <summary>Technical detail for the log.</summary>
    public string? Detail { get; } = detail;
}

public sealed record VideoCaptureRequest
{
    public required RecordingTarget Target { get; init; }

    /// <summary>Maximum frames per second (30 or 60).</summary>
    public required int FrameRate { get; init; }

    /// <summary>The master file to create (<c>take.mp4</c>).</summary>
    public required string OutputPath { get; init; }

    public IHostClock Clock { get; init; } = QpcClock.Instance;
}

public sealed record VideoCaptureResult
{
    /// <summary>The file is complete and playable (at least one frame, finalized).</summary>
    public required bool Written { get; init; }

    public int FramesWritten { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string Codec { get; init; } = "h264";

    /// <summary>Frames were written only when the picture changed.</summary>
    public bool VariableFrameRate { get; init; } = true;

    /// <summary>Why the file is not usable, for the log.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// The picture: Windows Graphics Capture → crop/fit on the GPU → H.264 in an
/// MP4 through a Media Foundation sink writer. One instance per recording.
/// </summary>
public interface IVideoCaptureSession : IAsyncDisposable
{
    /// <summary>Encoded frame width (even). The region size unless it exceeded the encoder limits.</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>The captured item's current content size (the window's size for window recordings).</summary>
    (int Width, int Height) ContentSize { get; }

    /// <summary>Capture ended on its own (window closed, display changed, device lost). Raised once, on any thread.</summary>
    event EventHandler<CaptureEndedEventArgs>? Ended;

    /// <summary>Starts delivering frames, timed through <paramref name="clock"/>; the first frame is forced to t = 0.</summary>
    void Start(PauseClock clock);

    /// <summary>
    /// Stops capturing, appends the last frame once more at <paramref name="endTime"/>
    /// (recording seconds) and finalizes the file. Safe to call once.
    /// </summary>
    Task<VideoCaptureResult> FinishAsync(double endTime);

    /// <summary>Abandons the file (start failed or the take is discarded).</summary>
    Task CancelAsync();
}

public interface IVideoCaptureBackend
{
    /// <summary>Cheap check run before the chooser opens.</summary>
    RecorderAvailability CheckAvailability();

    /// <summary>Opens the file and prepares the capture without starting it (spec §3.6 "writer starts").</summary>
    Task<IVideoCaptureSession> CreateAsync(VideoCaptureRequest request, CancellationToken cancellationToken);
}

/// <summary>Interleaved PCM layout of captured audio.</summary>
public readonly record struct PcmFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    /// <summary>What every take stores: 48 kHz, stereo, 16-bit PCM.</summary>
    public static PcmFormat Take { get; } = new(48_000, 2, 16, false);

    public int BytesPerSample => BitsPerSample / 8;

    public int BlockAlign => Channels * BytesPerSample;

    public bool IsValid => SampleRate is > 0 and <= 768_000 && Channels is > 0 and <= 32 &&
                           (IsFloat ? BitsPerSample is 32 or 64 : BitsPerSample is 8 or 16 or 24 or 32);
}

/// <summary>
/// A captured audio packet. <paramref name="data"/> is only valid during the
/// call. <paramref name="hostTime"/> is the capture time of its first frame on
/// the host clock (<see cref="QpcClock"/>); <paramref name="silent"/> means
/// the device flagged the packet as silence (its bytes may be garbage).
/// </summary>
public delegate void AudioPacketHandler(ReadOnlySpan<byte> data, PcmFormat format, double hostTime, bool silent);

/// <summary>One capture stream (system audio loopback or a microphone).</summary>
public interface IAudioCaptureSession : IDisposable
{
    /// <summary>For the log: "process loopback", the device name, ...</summary>
    string Description { get; }

    /// <summary>Raised on the capture thread; handlers must be quick.</summary>
    event AudioPacketHandler? PacketAvailable;

    /// <summary>The stream died for good (device removed, access revoked). Raised once.</summary>
    event EventHandler<string>? Failed;

    void Start();

    /// <summary>Stops and returns once no more packets will be delivered.</summary>
    void Stop();
}

public enum MicrophoneStatus
{
    Available,

    /// <summary>Windows privacy settings block desktop apps from the microphone.</summary>
    Denied,

    /// <summary>No active capture device.</summary>
    NoDevice,
}

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public interface IAudioCaptureBackend
{
    /// <summary>What the PC plays, excluding this app's own sounds where Windows allows it.</summary>
    Task<IAudioCaptureSession> OpenSystemAudioAsync(CancellationToken cancellationToken);

    /// <summary>A microphone; null or empty follows the Windows default input.</summary>
    Task<IAudioCaptureSession> OpenMicrophoneAsync(string? deviceId, CancellationToken cancellationToken);

    /// <summary>Quick check (privacy switch, device present) without opening a stream.</summary>
    MicrophoneStatus ProbeMicrophone(string? deviceId);

    IReadOnlyList<AudioDeviceInfo> ListMicrophones();
}

/// <summary>The pointer at one instant: physical virtual-screen pixels, shown or hidden, and the current cursor handle.</summary>
public readonly record struct CursorReading(PixelPoint Position, bool Visible, nint Handle);

/// <summary>
/// A cursor picture as it appears on the recorded monitor: size and hot spot in
/// video pixels (the PNG may be a sharper, larger rendition with the same
/// aspect), plus its content identity (§6.24).
/// </summary>
public sealed record CursorShapeSnapshot(ulong Identity, float Width, float Height, float HotX, float HotY, byte[] Png);

public interface ICursorProbe
{
    /// <summary>Called 125 times a second from the sampler thread; must be cheap.</summary>
    CursorReading Read();

    /// <summary>Reads the picture of <paramref name="handle"/> as drawn on a monitor at <paramref name="monitorScale"/>; null when unreadable.</summary>
    CursorShapeSnapshot? CaptureShape(nint handle, double monitorScale);
}

/// <summary>A window's rectangles in physical pixels: the full window and its visible frame (DWM extended frame bounds).</summary>
public readonly record struct WindowRects(PixelRect Window, PixelRect Frame);

/// <summary>A sleep with millisecond accuracy (the default timer tick is 15.6 ms).</summary>
public interface IPrecisionTimer : IDisposable
{
    void Wait(TimeSpan duration);
}

/// <summary>Small OS services the recorder needs.</summary>
public interface IRecorderSystem
{
    /// <summary>Keeps the PC and the display awake until disposed (a sleeping display delivers no frames).</summary>
    IDisposable PreventSleep(string reason);

    /// <summary>Null when the window no longer exists.</summary>
    WindowRects? GetWindowRects(nint window);

    /// <summary>Title and process name of a window, for the take manifest.</summary>
    (string? Title, string? Process) DescribeWindow(nint window);

    IPrecisionTimer CreateTimer();

    /// <summary>The system "error" sound.</summary>
    void Beep();
}

/// <summary>
/// Writes a self-contained MP4 from a take (its video plus its audio tracks
/// mixed into one AAC track, no pointer), used when the recording is saved
/// straight away instead of opening the editor.
/// </summary>
public interface IRawTakeExporter
{
    Task<bool> ExportAsync(string takeFolder, TakeManifest manifest, string outputPath, CancellationToken cancellationToken);
}
