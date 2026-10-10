// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture.Output;

/// <summary>The result of a direct save.</summary>
internal sealed record SavedCapture(string Path, string FolderName, int? ConsumedNumber);

/// <summary>
/// Saving and copying captures (spec 01 §3.8): the configured folder with a
/// dated subfolder and file-name pattern, unique names, PNG with DPI, the
/// optional 1x downscale, and the clipboard trio backed by a pruned cache of
/// copied files. Implements <see cref="ICaptureOutput"/> for the editor.
/// </summary>
internal sealed class CaptureOutputService : ICaptureOutput
{
    private readonly ISettingsStore _settings;
    private readonly ICaptureClipboard _clipboard;
    private readonly ICapturePlatform _platform;
    private readonly string _copiedFolder;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public CaptureOutputService(ISettingsStore settings, ICaptureClipboard clipboard, ICapturePlatform platform, AppPaths paths)
    {
        _settings = settings;
        _clipboard = clipboard;
        _platform = platform;
        _copiedFolder = Path.Combine(paths.Cache, "Copied Screenshots");
    }

    public string SaveFolder => ResolveFolder();

    /// <summary>The localized default-name prefix ("Screenshot").</summary>
    public static string Prefix => L.Get("screenshot.fileNamePrefix");

    public string SuggestFileName(string? windowTitle = null) =>
        CaptureNaming.FileName(_settings.Get(CaptureSettings.FileNamePattern), DateTime.Now, FileNumberSequence.Peek(_settings), Prefix);

    /// <summary>The default dated name, used for copies, drags and shares.</summary>
    public static string DefaultName() => CaptureNaming.DefaultName(DateTime.Now, CaptureNaming.SanitizeComponent(Prefix, "Screenshot"));

    async Task<CaptureSaveResult?> ICaptureOutput.SaveAsync(PixelBuffer image, string? windowTitle, string? existingRecordId)
    {
        var saved = await SaveAsync(image, applyDownscale: true).ConfigureAwait(true);
        if (saved is null)
        {
            return null;
        }

        var record = new CaptureRecord
        {
            Id = existingRecordId ?? Guid.NewGuid().ToString("D"),
            Kind = CaptureKind.Screenshot,
            FilePath = saved.Path,
            CreatedAt = DateTimeOffset.Now,
            PixelWidth = image.Width,
            PixelHeight = image.Height,
        };
        return new CaptureSaveResult(saved.Path, record);
    }

    void ICaptureOutput.Copy(PixelBuffer image) => _ = CopyAsync(image, applyDownscale: true);

    /// <summary>
    /// Saves to the configured folder (§3.8.1). A failure beeps and gives the
    /// consumed %# number back when nothing else advanced the sequence.
    /// </summary>
    public async Task<SavedCapture?> SaveAsync(PixelBuffer image, bool applyDownscale)
    {
        var output = applyDownscale ? CaptureImaging.ForOutput(image, _settings.Get(CaptureSettings.Downscale)) : image;
        var pattern = _settings.Get(CaptureSettings.FileNamePattern);
        var subfolderPattern = _settings.Get(CaptureSettings.SaveSubfolder);
        int? consumed = null;
        await _saveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var now = DateTime.Now;
            if (CaptureNaming.UsesNumber(pattern.Trim()))
            {
                consumed = FileNumberSequence.Consume(_settings);
            }

            var baseFolder = ResolveFolder();
            var number = consumed ?? FileNumberSequence.Peek(_settings);
            var fileName = CaptureNaming.FileName(pattern, now, number, Prefix);
            var path = await Task.Run(() =>
            {
                var folder = baseFolder;
                var sub = CaptureNaming.Subfolder(subfolderPattern, now);
                if (sub.Length > 0)
                {
                    try
                    {
                        var candidate = Path.Combine(baseFolder, sub);
                        Directory.CreateDirectory(candidate);
                        folder = candidate;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        // Save in the base folder rather than lose the capture.
                        Log.Warn("capture", "The dated subfolder could not be created; saving in the base folder.", ex);
                    }
                }

                Directory.CreateDirectory(folder);
                var unique = CaptureNaming.UniqueName(folder, fileName, p => File.Exists(p) || Directory.Exists(p));
                var target = Path.Combine(folder, unique);
                CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(output), target);
                return target;
            }).ConfigureAwait(true);

            var folderName = Path.GetFileName(Path.GetDirectoryName(path)) ?? path;
            return new SavedCapture(path, folderName, consumed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn("capture", "Saving the screenshot failed.", ex);
            if (consumed is { } n)
            {
                FileNumberSequence.Rewind(_settings, n);
            }

            _platform.Beep();
            return null;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Copies an image (§3.8.3): a PNG in the private copied-files cache, then
    /// one clipboard item with PNG, bitmap and file. Returns false (and beeps) on failure.
    /// </summary>
    public async Task<bool> CopyAsync(PixelBuffer image, bool applyDownscale, string? existingFile = null, Func<bool>? stillWanted = null)
    {
        var output = applyDownscale ? CaptureImaging.ForOutput(image, _settings.Get(CaptureSettings.Downscale)) : image;
        string? file = existingFile;
        byte[] png;
        try
        {
            (png, file) = await Task.Run(() =>
            {
                var bytes = CaptureImaging.EncodePng(output);
                if (existingFile is not null)
                {
                    return (bytes, existingFile);
                }

                Directory.CreateDirectory(_copiedFolder);
                if (new DirectoryInfo(_copiedFolder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("The copied-files cache is a link.");
                }

                if (bytes.LongLength > CopiedFilesPolicy.MaxBytes)
                {
                    return (bytes, (string?)null);
                }

                var name = CaptureNaming.UniqueName(_copiedFolder, DefaultName(), File.Exists);
                var path = Path.Combine(_copiedFolder, name);
                CaptureImaging.WritePngAtomically(bytes, path);
                return (bytes, path);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("capture", "Rendering the copy failed.", ex);
            _platform.Beep();
            return false;
        }

        if (stillWanted is not null && !stillWanted())
        {
            DeleteQuietly(existingFile is null ? file : null);
            return false;
        }

        // The bitmap conversion inside is heavy for large captures; the clipboard is opened and closed on that same thread.
        var ok = await Task.Run(() => _clipboard.SetImage(output, png, file));
        if (!ok)
        {
            Log.Warn("capture", "The clipboard refused the screenshot.");
            DeleteQuietly(existingFile is null ? file : null);
            _platform.Beep();
            return false;
        }

        if (existingFile is null)
        {
            _ = Task.Run(() => PruneCopied(file));
        }

        return true;
    }

    /// <summary>Copies text (OCR results, colours, QR payloads) marked as coming from this app.</summary>
    public bool CopyText(string text)
    {
        var ok = _clipboard.SetText(text);
        if (!ok)
        {
            _platform.Beep();
        }

        return ok;
    }

    /// <summary>The configured folder if it exists, else the Screenshots folder, else Desktop, else the profile.</summary>
    public string ResolveFolder()
    {
        var configured = ExpandHome(_settings.Get(CaptureSettings.SaveFolder).Trim());
        if (configured.Length > 0 && Directory.Exists(configured))
        {
            return configured;
        }

        try
        {
            var standard = _platform.DefaultScreenshotFolder;
            Directory.CreateDirectory(standard);
            return standard;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("capture", "The Screenshots folder is unavailable.", ex);
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Directory.Exists(desktop) ? desktop : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string ExpandHome(string path)
    {
        if (path.StartsWith('~'))
        {
            var rest = path[1..].TrimStart('/', '\\');
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rest);
        }

        return path;
    }

    private void PruneCopied(string? current)
    {
        try
        {
            var directory = new DirectoryInfo(_copiedFolder);
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            var files = directory.EnumerateFiles("*.png")
                .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(f => new CachedFile(f.FullName, f.LastWriteTimeUtc, f.Length))
                .ToList();
            foreach (var path in CopiedFilesPolicy.FilesToDelete(files, current, DateTime.UtcNow))
            {
                DeleteQuietly(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("capture", "Pruning copied screenshots failed.", ex);
        }
    }

    private static void DeleteQuietly(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("capture", $"Could not delete {path}.", ex);
        }
    }
}
