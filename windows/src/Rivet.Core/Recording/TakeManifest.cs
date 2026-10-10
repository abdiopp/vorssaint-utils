// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.Core.Recording;

/// <summary>
/// <c>take.json</c>: what a recording folder contains. Shared contract between
/// the recording engine (writes it at stop) and the recording editor (reads it).
/// All tracks share one time origin: t = 0 is the start of the recording, with
/// pauses removed.
/// <code>
/// Recordings/Take-&lt;UUID&gt;/
///   take.json      this manifest
///   take.mp4       H.264 video, no cursor, no audio
///   system.wav     system audio (optional), PCM, starts at t = 0
///   mic.wav        microphone (optional), PCM, starts at t = 0
///   pointer.bin    pointer track v4 (optional), see <see cref="PointerTrack"/>
///   typing.json    {"times":[…]} key-down times (optional)
///   edit.json      the editor's document (owned by the recording editor)
/// </code>
/// </summary>
public sealed record TakeManifest
{
    public const int CurrentVersion = 1;

    public const string FileName = "take.json";

    public int Version { get; init; } = CurrentVersion;

    public string AppVersion { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public required TakeCapture Capture { get; init; }

    public required TakeVideo Video { get; init; }

    public IReadOnlyList<TakeAudio> Audio { get; init; } = [];

    public TakePointerTrack? PointerTrack { get; init; }

    public string? TypingTrack { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a manifest; null when the file is missing or unreadable.</summary>
    public static TakeManifest? Read(string takeFolder)
    {
        var path = Path.Combine(takeFolder, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<TakeManifest>(File.ReadAllText(path), Options);
            return manifest is { Version: >= 1 and <= CurrentVersion } ? manifest : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Atomic write (temp file, then replace).</summary>
    public void Write(string takeFolder)
    {
        var path = Path.Combine(takeFolder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
    }

    public string PathOf(string takeFolder, string relative) => Path.Combine(takeFolder, relative);
}

public enum TakeCaptureKind
{
    Area,
    Window,
    Display,
}

public sealed record TakeCapture
{
    public required TakeCaptureKind Kind { get; init; }

    /// <summary>Nominal frame rate the engine targeted (30 or 60).</summary>
    public int Fps { get; init; } = 60;

    public required TakeMonitor Monitor { get; init; }

    /// <summary>Captured rectangle in physical virtual-screen pixels: [x, y, width, height].</summary>
    public required int[] RegionPx { get; init; }

    public TakeWindow? Window { get; init; }
}

public sealed record TakeMonitor
{
    /// <summary>Device name, e.g. <c>\\.\DISPLAY1</c>.</summary>
    public required string Device { get; init; }

    /// <summary>DPI scale of the recorded monitor (1.5 at 150 %).</summary>
    public double DpiScale { get; init; } = 1;

    /// <summary>Monitor rectangle in physical pixels: [x, y, width, height].</summary>
    public required int[] RectPx { get; init; }
}

public sealed record TakeWindow
{
    public string? Title { get; init; }

    public string? Process { get; init; }
}

public sealed record TakeVideo
{
    public string Codec { get; init; } = "h264";

    public string File { get; init; } = "take.mp4";

    /// <summary>Pixel size of the video frames (even numbers).</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>True when frames were written only on change (variable frame rate).</summary>
    public bool Vfr { get; init; }

    public double DurationSeconds { get; init; }
}

public enum TakeAudioSource
{
    System,
    Microphone,
}

public sealed record TakeAudio
{
    public required TakeAudioSource Source { get; init; }

    /// <summary>PCM WAV file in the take folder; it starts at t = 0 (silence padded).</summary>
    public required string File { get; init; }

    public int SampleRate { get; init; } = 48000;

    public int Channels { get; init; } = 2;
}

public sealed record TakePointerTrack
{
    public string File { get; init; } = "pointer.bin";

    public int Version { get; init; } = PointerTrack.CurrentVersion;
}
