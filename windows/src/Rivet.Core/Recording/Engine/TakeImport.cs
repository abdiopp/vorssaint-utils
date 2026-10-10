// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Recording.Engine;

public enum TakeImportFailure
{
    /// <summary>Not a regular file (missing, a folder, a link) or empty.</summary>
    NotAFile,

    /// <summary>The file has no video track.</summary>
    NoVideo,

    /// <summary>Windows cannot read the file.</summary>
    Unsupported,

    /// <summary>Less than the file's size plus 500 MB free.</summary>
    NotEnoughSpace,
}

public sealed class TakeImportException(TakeImportFailure reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public TakeImportFailure Reason { get; } = reason;
}

/// <summary>
/// Media tools → "open in the recording editor" (spec 02 §3.19): copies an
/// existing movie into a new take (independent of the original) so the editor
/// treats it like a recording. The take has no pointer or typing track; its
/// sound, if any, is decoded into the take's system-audio WAV. Returns the take
/// folder for <c>IRecordingEditor.OpenAsync</c>. Failure messages belong to
/// the caller ("no video", "unsupported").
/// </summary>
public interface ITakeImporter
{
    Task<string> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);
}

/// <summary>The checks every importer runs before copying.</summary>
public static class TakeImportRules
{
    public const long SpaceMargin = 500_000_000;

    /// <summary>Throws <see cref="TakeImportException"/> unless the file can be imported.</summary>
    public static void Validate(string sourcePath, long? freeBytes)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            throw new TakeImportException(TakeImportFailure.NotAFile, "The path is not usable.", ex);
        }

        if (!info.Exists || info.Length <= 0 || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new TakeImportException(TakeImportFailure.NotAFile, "Only a regular, non-empty file can be imported.");
        }

        if (freeBytes is { } free && free < info.Length + SpaceMargin)
        {
            throw new TakeImportException(TakeImportFailure.NotEnoughSpace, "Not enough free space for a copy of the file.");
        }
    }

    /// <summary>The manifest of an imported movie (no capture region, no side tracks).</summary>
    public static TakeManifest Manifest(string videoFile, string codec, int width, int height, double fps, double durationSeconds, bool hasSound, string appVersion) => new()
    {
        AppVersion = appVersion,
        CreatedAt = DateTimeOffset.UtcNow,
        Capture = new TakeCapture
        {
            Kind = TakeCaptureKind.Display,
            Fps = double.IsFinite(fps) && fps > 0 ? (int)Math.Clamp(Math.Round(fps), 1, 240) : 30,
            Monitor = new TakeMonitor { Device = string.Empty, DpiScale = 1, RectPx = [0, 0, width, height] },
            RegionPx = [0, 0, width, height],
        },
        Video = new TakeVideo
        {
            Codec = codec,
            File = videoFile,
            Width = width,
            Height = height,
            Vfr = false,
            DurationSeconds = Math.Max(0, durationSeconds),
        },
        Audio = hasSound ? [new TakeAudio { Source = TakeAudioSource.System, File = RecordingSession.SystemAudioFile }] : [],
    };
}
