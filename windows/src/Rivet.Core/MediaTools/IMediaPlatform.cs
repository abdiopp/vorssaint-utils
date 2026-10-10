// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Modules.MediaTools;

/// <summary>What a movie file contains.</summary>
public sealed record VideoInfo
{
    public double Duration { get; init; }

    /// <summary>Display size (rotation already applied).</summary>
    public MediaSize Size { get; init; }

    public double FrameRate { get; init; }

    public bool HasVideo { get; init; }

    public bool HasAudio { get; init; }

    /// <summary>Clockwise rotation stored in the file (0, 90, 180, 270).</summary>
    public int Rotation { get; init; }
}

public interface IVideoProbe
{
    /// <summary>Duration, size, frame rate and tracks, or null when the file cannot be read.</summary>
    Task<VideoInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record VideoEncodeRequest
{
    public required string Input { get; init; }

    /// <summary>The temporary file to write (MP4).</summary>
    public required string Output { get; init; }

    public double Start { get; init; }

    public double End { get; init; }

    public required MediaSize Size { get; init; }

    public int VideoBitRate { get; init; }

    public int AudioBitRate { get; init; }

    public int FrameRate { get; init; } = 30;

    public bool KeepAudio { get; init; } = true;
}

/// <summary>H.264/AAC MP4 encoding (Windows.Media.Transcoding on Windows).</summary>
public interface IVideoTranscoder
{
    bool IsAvailable { get; }

    /// <summary>Encodes; reports 0–1 progress; throws <see cref="MediaJobException"/> with a user message on failure.</summary>
    Task EncodeAsync(VideoEncodeRequest request, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>Frames for GIF creation (Media Foundation source reader on Windows).</summary>
public interface IVideoFrameReader
{
    bool IsAvailable { get; }

    /// <summary>
    /// Decodes the frames at <paramref name="times"/> (seconds, ascending) scaled
    /// to <paramref name="size"/> with rotation applied, and hands each one to
    /// <paramref name="onFrame"/> in order. The buffer may be reused afterwards.
    /// </summary>
    Task ReadFramesAsync(string input, IReadOnlyList<double> times, MediaSize size, Func<int, PixelBuffer, Task> onFrame, CancellationToken cancellationToken);
}

/// <summary>Text recognition (Windows.Media.Ocr on Windows).</summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }

    /// <summary>BCP-47 tags of the installed recognizer languages.</summary>
    IReadOnlyList<string> AvailableLanguages { get; }

    /// <summary>Lines joined with "\n" (empty when nothing was found). Uses the first available preferred language.</summary>
    Task<string> RecognizeAsync(PixelBuffer image, IReadOnlyList<string> preferredLanguages, bool accurate, CancellationToken cancellationToken);
}

/// <summary>System image codecs beyond what SkiaSharp handles (HEIC, TIFF and other WIC codecs).</summary>
public interface IImageCodecs
{
    /// <summary>A HEIF/HEVC encoder is installed (Windows: the HEIF and HEVC Video Extensions).</summary>
    bool CanEncodeHeic { get; }

    bool CanDecodeHeic { get; }

    /// <summary>
    /// Decodes with the system codecs, EXIF orientation applied, downsampled so
    /// the longest side is at most <paramref name="maxPixel"/> (0 = full size).
    /// </summary>
    PixelBuffer? Decode(string path, int maxPixel);

    /// <summary>Encodes HEIC at <paramref name="quality"/> (0.1–1), or null.</summary>
    byte[]? EncodeHeic(PixelBuffer image, double quality);
}

/// <summary>File identity and attributes for safe output commits.</summary>
public interface IFileIdentity
{
    /// <summary>Same volume and file id (hard links and symlinks count as the same file).</summary>
    bool AreSameFile(string first, string second);

    /// <summary>Clears the hidden attribute.</summary>
    void ClearHidden(string path);

    /// <summary>Moves into place, replacing (or not) an existing file; retries briefly on sharing violations.</summary>
    void Move(string source, string destination, bool replace);
}

/// <summary>Portable defaults used when no platform implementation is registered.</summary>
public sealed class PortableFileIdentity : IFileIdentity
{
    public bool AreSameFile(string first, string second)
    {
        try
        {
            var a = Path.GetFullPath(first);
            var b = Path.GetFullPath(second);
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(a, b, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public void ClearHidden(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Hidden))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.Hidden);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Move(string source, string destination, bool replace)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, replace);
                return;
            }
            catch (IOException) when (attempt < 4 && File.Exists(source) && (replace || !File.Exists(destination)))
            {
                // Antivirus and indexers briefly lock new files.
                Thread.Sleep(120);
            }
        }
    }
}

/// <summary>
/// A temporary file in a fresh hidden folder on the destination volume; the
/// finished file is renamed over the destination, and the folder is always
/// removed afterwards, so failures and cancellations never leave partial
/// files or damage an existing one (spec 07 §3.6.8).
/// </summary>
public sealed class MediaOutputCommit : IDisposable
{
    private readonly IFileIdentity _identity;

    public MediaOutputCommit(string destination, IFileIdentity identity)
    {
        _identity = identity;
        Destination = destination;
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        TempFolder = Path.Combine(folder, $".rivet-{Guid.NewGuid():N}");
        var info = Directory.CreateDirectory(TempFolder);
        try
        {
            info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        var extension = Path.GetExtension(destination);
        TempFile = Path.Combine(TempFolder, "Output" + extension);
    }

    public string Destination { get; }

    public string TempFolder { get; }

    public string TempFile { get; }

    /// <summary>Renames the finished temp file over the destination and clears the hidden flag unless the name starts with ".".</summary>
    public void Commit(bool replace = true)
    {
        _identity.Move(TempFile, Destination, replace);
        if (!Path.GetFileName(Destination).StartsWith('.'))
        {
            _identity.ClearHidden(Destination);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempFolder))
            {
                Directory.Delete(TempFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
