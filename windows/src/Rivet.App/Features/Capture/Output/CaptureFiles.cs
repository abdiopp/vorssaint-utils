// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture.Output;

/// <summary>
/// The most recent screenshot on disk for "Edit latest screenshot" (spec
/// 01 §3.13), kept only while that shortcut is on. Written in the
/// background; a read while the write is pending returns the pending capture.
/// </summary>
internal sealed class LatestCaptureStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private PixelBuffer? _pending;
    private int _generation;

    public LatestCaptureStore(AppPaths paths)
        : this(Path.Combine(paths.Cache, "LatestScreenshot.png"))
    {
    }

    internal LatestCaptureStore(string path) => _path = path;

    public void Store(PixelBuffer image)
    {
        int generation;
        lock (_gate)
        {
            _pending = image;
            generation = ++_generation;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var png = CaptureImaging.EncodePng(image);
                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        return; // superseded by a newer capture
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllBytes(temp, png);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("capture", "Could not store the latest screenshot.", ex);
            }
            finally
            {
                lock (_gate)
                {
                    if (generation == _generation)
                    {
                        _pending = null;
                    }
                }
            }
        });
    }

    /// <summary>The latest capture; the scale comes from the stored DPI (a file without a valid one counts as none).</summary>
    public PixelBuffer? Load()
    {
        lock (_gate)
        {
            if (_pending is { } pending)
            {
                return pending;
            }
        }

        return File.Exists(_path) ? CaptureImaging.DecodeFile(_path, requireDpi: true) : null;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _pending = null;
        }

        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("capture", "Could not clear the latest screenshot.", ex);
        }
    }
}

/// <summary>
/// Temporary export files for drag-out and sharing (spec 01 §3.8.4): one
/// <c>ScreenshotDrag-&lt;GUID&gt;</c> folder per export, deleted an hour later;
/// leftovers are removed at launch (only folders with that exact name, never links).
/// </summary>
internal static partial class CaptureTempFiles
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static string Root => Path.GetTempPath();

    /// <summary>Writes a PNG into a fresh folder and schedules its deletion.</summary>
    public static string WriteExport(PixelBuffer image, string fileName)
    {
        var folder = Path.Combine(Root, $"ScreenshotDrag-{Guid.NewGuid():D}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(image), path);
        _ = Task.Delay(Lifetime).ContinueWith(_ => DeleteFolder(folder), TaskScheduler.Default);
        return path;
    }

    /// <summary>Deletes leftover export folders from earlier runs.</summary>
    public static void CleanLeftovers()
    {
        try
        {
            foreach (var directory in new DirectoryInfo(Root).EnumerateDirectories("ScreenshotDrag-*"))
            {
                if (FolderName().IsMatch(directory.Name) && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    DeleteFolder(directory.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("capture", "Could not clean old drag folders.", ex);
        }
    }

    internal static bool IsExportFolderName(string name) => FolderName().IsMatch(name);

    private static void DeleteFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            foreach (var file in info.EnumerateFiles())
            {
                file.Delete();
            }

            info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("capture", $"Could not delete {folder}.", ex);
        }
    }

    [GeneratedRegex("^ScreenshotDrag-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex FolderName();
}
