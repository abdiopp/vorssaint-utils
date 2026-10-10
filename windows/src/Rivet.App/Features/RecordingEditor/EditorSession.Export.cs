// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.App.Features.RecordingEditor;

public enum ExportKind
{
    Save,
    SaveAs,
    SaveGif,
    Copy,
    CopyGif,
    CopyAndDelete,
}

/// <summary>Export actions with progress, cancel and the atomic commit (spec 02 §3.34, §3.35).</summary>
public sealed partial class EditorSession
{
    private CancellationTokenSource? _exportCts;
    private Task _exportTask = Task.CompletedTask;

    public bool IsExporting { get; private set; }

    public double ExportProgress { get; private set; }

    /// <summary>The last file a Save, Save as or Save as GIF produced (the finished-file chip).</summary>
    public string? FinishedFile { get; private set; }

    /// <summary>Anything was saved or copied from this editor: closing no longer asks.</summary>
    public bool HasExported { get; private set; }

    public bool CanEncodeVideo => _services.GetService<IVideoEncoderFactory>()?.IsAvailable == true;

    public string SaveFolder => RecordingFolders.SaveFolder(Settings.Get(RecordingEditorSettings.SaveFolder));

    public void SetSaveFolder(string folder) => Settings.Set(RecordingEditorSettings.SaveFolder, folder);

    /// <summary>The default file name for an export now (Save as dialog).</summary>
    public static string DefaultBaseName() => RecordingNames.BaseName(L.Get("recorder.fileNamePrefix"), DateTime.Now);

    public void CancelExport() => _exportCts?.Cancel();

    /// <summary>Waits for a running export to unwind (after cancelling it) so the take can be deleted.</summary>
    public async Task WaitForExportAsync(TimeSpan timeout)
    {
        CancelExport();
        await Task.WhenAny(_exportTask, Task.Delay(timeout)).ConfigureAwait(true);
    }

    public Task ExportAsync(ExportKind kind, string? destination = null)
    {
        if (IsExporting)
        {
            return Task.CompletedTask;
        }

        if (!IsReady)
        {
            // The master cannot be decoded here: Save still keeps the recording by copying the raw file.
            return kind is ExportKind.Save or ExportKind.SaveAs ? SaveRawMasterAsync(destination) : Task.CompletedTask;
        }

        var task = RunExportAsync(kind, destination);
        _exportTask = task;
        return task;
    }

    private async Task RunExportAsync(ExportKind kind, string? destination)
    {
        var gif = kind is ExportKind.SaveGif or ExportKind.CopyGif;
        if (!gif && !CanEncodeVideo)
        {
            Hud?.Show(L.Get("win.recordingEditor.mp4Unavailable"), HudStyle.Warning, duration: TimeSpan.FromSeconds(4));
            return;
        }

        Playback.Pause();
        EndModes();
        var copy = kind is ExportKind.Copy or ExportKind.CopyGif or ExportKind.CopyAndDelete;
        var extension = gif ? ".gif" : ".mp4";
        string path;
        try
        {
            if (destination is not null)
            {
                path = Path.ChangeExtension(destination, extension);
            }
            else
            {
                var folder = copy ? RecordingFolders.CopyCache(Paths) : SaveFolder;
                if (copy)
                {
                    RecordingFolders.Purge(folder, TimeSpan.FromHours(24));
                }

                path = RecordingNames.UniquePath(folder, DefaultBaseName(), extension);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
            return;
        }

        var input = new RecordingExportInput
        {
            Take = Take,
            Document = Document,
            Duration = Duration,
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            FrameRate = Fps,
        };
        var sources = _services.GetRequiredService<IVideoFrameSourceFactory>();
        var encoders = _services.GetService<IVideoEncoderFactory>();
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        IsExporting = true;
        ExportProgress = 0;
        Raise(SessionChange.Export);
        var progress = new Progress<double>(p =>
        {
            ExportProgress = p;
            Raise(SessionChange.Export);
        });

        SKImage? thumbnail = null;
        void KeepThumbnail(PixelBuffer frame)
        {
            using var image = SkiaConvert.ToImage(frame);
            var scale = Math.Min(1, 360.0 / Math.Max(image.Width, image.Height));
            thumbnail = SkiaConvert.Resize(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        }

        byte[]? gifBytes = null;
        try
        {
            await Task.Run(() =>
            {
                using var staged = new StagedFile(path);
                if (gif)
                {
                    gifBytes = RecordingExporter.ExportGif(input, sources, progress, cts.Token, KeepThumbnail);
                    cts.Token.ThrowIfCancellationRequested();
                    File.WriteAllBytes(staged.StagingPath, gifBytes);
                }
                else
                {
                    RecordingExporter.ExportVideo(input, staged.StagingPath, sources, encoders!, progress, cts.Token, KeepThumbnail);
                }

                staged.MarkHidden();
                staged.Commit(cts.Token);
            }, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            thumbnail?.Dispose();
            return;   // cancelled: nothing shown, nothing left behind
        }
        catch (GifTooLongException ex)
        {
            thumbnail?.Dispose();
            Hud?.Show(L.Format("recorder.gifTooLongFormat", ex.MaxSeconds), HudStyle.Warning, duration: TimeSpan.FromSeconds(2.5));
            return;
        }
        catch (MediaUnavailableException)
        {
            thumbnail?.Dispose();
            Hud?.Show(L.Get("win.recordingEditor.mp4Unavailable"), HudStyle.Warning, duration: TimeSpan.FromSeconds(4));
            return;
        }
        catch (Exception ex)
        {
            thumbnail?.Dispose();
            Fail(ex);
            return;
        }
        finally
        {
            IsExporting = false;
            _exportCts = null;
            cts.Dispose();
            Raise(SessionChange.Export);
        }

        var record = AddToRecentCaptures(path, gif, thumbnail);
        thumbnail?.Dispose();
        HasExported = true;
        if (copy)
        {
            if (gif && (gifBytes is null || !GifDecoder.LooksLikeGif(File.ReadAllBytes(path), out _)))
            {
                // Verified before the clipboard is touched: the previous content survives.
                Fail(new InvalidDataException("The GIF could not be verified."));
                return;
            }

            try
            {
                _services.GetRequiredService<IClipboardService>().SetFiles([path]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                Fail(ex);
                return;
            }

            Hud?.Show(L.Get("recorder.copiedHUD"), HudStyle.Success, duration: HudDuration);
            if (kind == ExportKind.CopyAndDelete)
            {
                RequestClose();
            }
        }
        else
        {
            FinishedFile = path;
            var folderName = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
            Hud?.Show(L.Format("recorder.savedHUDFormat", folderName), HudStyle.Success, duration: HudDuration);
        }

        Log.Info("recording-editor", $"Exported {(gif ? "GIF" : "MP4")} ({record?.PixelWidth}×{record?.PixelHeight}).");
        Raise(SessionChange.Export);
    }

    /// <summary>
    /// Copies the untouched master (no pointer, no edits, video only) when this
    /// PC cannot decode it, so closing the editor never loses the only copy.
    /// </summary>
    private async Task SaveRawMasterAsync(string? destination)
    {
        var source = Take.VideoPath;
        if (!File.Exists(source))
        {
            return;
        }

        IsExporting = true;
        Raise(SessionChange.Export);
        try
        {
            var extension = Path.GetExtension(source);
            var path = destination is not null
                ? Path.ChangeExtension(destination, extension)
                : RecordingNames.UniquePath(SaveFolder, DefaultBaseName(), extension);
            await Task.Run(() =>
            {
                using var staged = new StagedFile(path);
                File.Copy(source, staged.StagingPath, overwrite: false);
                staged.Commit(CancellationToken.None);
            }).ConfigureAwait(true);
            FinishedFile = path;
            HasExported = true;
            Hud?.Show(L.Format("recorder.savedHUDFormat", Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty), HudStyle.Success, duration: HudDuration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
        finally
        {
            IsExporting = false;
            Raise(SessionChange.Export);
        }
    }

    private void Fail(Exception ex)
    {
        Log.Error("recording-editor", "Export failed.", ex);
        _services.GetService<IRecordingEditorShell>()?.Beep();
        Hud?.Show(L.Get("recorder.exportFailed"), HudStyle.Error, duration: HudDuration);
    }

    private CaptureRecord? AddToRecentCaptures(string path, bool gif, SKImage? thumbnail)
    {
        var recent = _services.GetService<IRecentCaptures>();
        var id = Guid.NewGuid().ToString("D");
        string? thumbnailPath = null;
        if (thumbnail is not null)
        {
            try
            {
                thumbnailPath = Path.Combine(RecordingFolders.Thumbnails(Paths), id + ".png");
                SkiaConvert.SavePng(thumbnail, thumbnailPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                thumbnailPath = null;
            }
        }

        var doc = Document;
        var size = gif
            ? ExportMath.GifSizeFor(CanvasLayout.Compute(SourceWidth, SourceHeight, doc.BackdropStyle, doc.Aspect, 1).CanvasWidth,
                CanvasLayout.Compute(SourceWidth, SourceHeight, doc.BackdropStyle, doc.Aspect, 1).CanvasHeight, doc.GifSize)
            : ExportSize(doc.Quality);
        var record = new CaptureRecord
        {
            Id = id,
            Kind = gif ? CaptureKind.Gif : CaptureKind.Recording,
            FilePath = path,
            ThumbnailPath = thumbnailPath,
            CreatedAt = DateTimeOffset.Now,
            PixelWidth = size.Width,
            PixelHeight = size.Height,
            Duration = TimeSpan.FromSeconds(ExportSpeed.ExportDuration(Timeline.OutputDuration, doc.ExportSpeed)),
        };
        try
        {
            recent?.Add(record);
        }
        catch (Exception ex)
        {
            Log.Warn("recording-editor", "Recent captures refused the entry.", ex);
        }

        return record;
    }
}
