// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.RecordingEditor;

/// <summary>A decoded video frame: BGRA pixels and its presentation time in source seconds.</summary>
public sealed class VideoFrame(PixelBuffer pixels, double time)
{
    public PixelBuffer Pixels { get; } = pixels;

    public double Time { get; } = time;
}

/// <summary>How to open a video for decoding.</summary>
public sealed record VideoOpenOptions
{
    /// <summary>Decode at this size (aspect kept by the caller); null = the video's own size.</summary>
    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Frames kept for back-and-forth scrubbing.</summary>
    public int CacheSize { get; init; } = 8;
}

/// <summary>
/// Decodes the master. <see cref="GetFrame"/> returns the frame on screen at a
/// source time: the latest frame whose timestamp is at or before it (the master
/// is variable frame rate). Sequential reads decode forward; going back or far
/// ahead seeks to the previous key frame and decodes forward to the exact
/// frame. Not thread-safe: one reader per thread.
/// </summary>
public interface IVideoFrameSource : IDisposable
{
    /// <summary>Size of the video itself (even), regardless of the decode size.</summary>
    int VideoWidth { get; }

    int VideoHeight { get; }

    /// <summary>Size of the frames this reader returns.</summary>
    int FrameWidth { get; }

    int FrameHeight { get; }

    double Duration { get; }

    /// <summary>Nominal frame rate reported by the container (0 when unknown).</summary>
    double NominalFrameRate { get; }

    VideoFrame? GetFrame(double time, CancellationToken cancellationToken = default);
}

/// <summary>Raised when the platform cannot decode or encode video at all (e.g. Windows N without the Media Feature Pack).</summary>
public sealed class MediaUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Opens video files for decoding (Media Foundation on Windows, synthetic frames in development).</summary>
public interface IVideoFrameSourceFactory
{
    /// <summary>Throws <see cref="MediaUnavailableException"/> or <see cref="IOException"/> when the file cannot be opened.</summary>
    IVideoFrameSource Open(string path, VideoOpenOptions? options = null);
}

/// <summary>Encoder settings for an MP4 export.</summary>
public sealed record VideoEncoderSettings
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int FrameRate { get; init; }

    public required int BitRate { get; init; }

    public required int KeyFrameIntervalFrames { get; init; }

    public bool IncludeAudio { get; init; }

    public int AudioSampleRate { get; init; } = ExportMath.AudioSampleRate;

    public int AudioChannels { get; init; } = 2;

    public int AudioBitRate { get; init; } = ExportMath.AudioBitRate;
}

/// <summary>
/// Writes an MP4 (H.264 + AAC). Frames are BGRA, top-down; timestamps in
/// seconds. Audio is interleaved 32-bit float stereo at the configured rate.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    void WriteVideoFrame(PixelBuffer frame, double timestamp, double duration);

    void WriteAudio(ReadOnlySpan<float> interleaved, double timestamp);

    /// <summary>Finalizes the file. Without it (dispose only) the output is incomplete and must be deleted.</summary>
    void Finish();
}

public interface IVideoEncoderFactory
{
    /// <summary>False when this PC cannot encode MP4 (the UI explains it instead of failing silently).</summary>
    bool IsAvailable { get; }

    IVideoEncoder Create(string path, VideoEncoderSettings settings);
}

/// <summary>Supplies the edited timeline's mixed audio for playback, from an output time.</summary>
public interface IAudioFeed
{
    int SampleRate { get; }

    /// <summary>Fills interleaved stereo float samples; returns how many frames were written (0 = end).</summary>
    int Read(Span<float> interleaved);

    /// <summary>Repositions the feed (output seconds).</summary>
    void Seek(double outputTime);
}

/// <summary>Plays the preview's audio (WASAPI on Windows; silent in development).</summary>
public interface IAudioPlayback : IDisposable
{
    /// <summary>Starts playing <paramref name="feed"/> from its current position.</summary>
    void Start(IAudioFeed feed);

    void Stop();

    /// <summary>Seconds played since <see cref="Start"/>, as the device reports them; null when there is no device clock.</summary>
    double? PlayedSeconds { get; }
}

public interface IAudioPlaybackFactory
{
    /// <summary>Null when no output device is available.</summary>
    IAudioPlayback? Create();
}

/// <summary>Small OS services the editor needs beyond the shared shell services.</summary>
public interface IRecordingEditorShell
{
    /// <summary>Current desktop wallpapers for the background picker (spec 02 §3.29): existing local files, de-duplicated.</summary>
    IReadOnlyList<string> CurrentWallpapers();

    /// <summary>The system's default alert sound (macOS "beep" on export failures).</summary>
    void Beep();
}
