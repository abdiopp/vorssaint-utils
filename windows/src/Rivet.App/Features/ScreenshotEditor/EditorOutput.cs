// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Shell;
using Rivet.Core.App;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.ScreenshotEditor;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The editor's outputs (spec 01 §3.8, §3.10.16). Save and Copy go through
/// the capture module's <see cref="ICaptureOutput"/> when it is installed
/// (folder, naming and clipboard rules live there); otherwise Save writes a
/// PNG to Pictures\Screenshots and Copy puts the image on the clipboard.
/// </summary>
internal sealed class EditorOutput(EditorController controller, Window owner)
{
    private readonly IServiceProvider _services = controller.Services;

    private IHud? Hud => _services.GetService<IHud>();

    private string DefaultName =>
        _services.GetService<ICaptureOutput>()?.SuggestFileName(controller.Request.WindowTitle) is { Length: > 0 } suggested
            ? EnsurePng(suggested)
            : EditorFiles.DefaultName(DateTime.Now, L.Get("screenshot.fileNamePrefix"));

    private static string EnsurePng(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png";

    public async Task<bool> CopyAsync()
    {
        try
        {
            using var export = await controller.ExportAsync(includeBackdrop: true);
            var buffer = EditorController.ToBuffer(export);
            if (_services.GetService<ICaptureOutput>() is { } output)
            {
                output.Copy(buffer);
            }
            else
            {
                _services.GetRequiredService<IClipboardService>().SetImage(buffer);
            }

            Hud?.Show(L.Get("screenshot.copiedHUD"), HudStyle.Success, "Copy");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("screenshotEditor", "Copy failed.", ex);
            Hud?.Show(L.Get("win.screenshotEditor.copyFailed"), HudStyle.Error);
            return false;
        }
    }

    public async Task<bool> SaveAsync()
    {
        try
        {
            using var export = await controller.ExportAsync(includeBackdrop: true);
            var buffer = EditorController.ToBuffer(export);
            string? path;
            if (_services.GetService<ICaptureOutput>() is { } output)
            {
                var result = await output.SaveAsync(buffer, controller.Request.WindowTitle, controller.Request.RecentCaptureId);
                path = result?.Path;
            }
            else
            {
                path = await Task.Run(() => SaveToPictures(export));
            }

            if (path is null)
            {
                Hud?.Show(L.Get("win.screenshotEditor.saveFailed"), HudStyle.Error);
                return false;
            }

            var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
            Hud?.Show(L.Format("screenshot.savedHUDFormat", folder), HudStyle.Success, "Save");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("screenshotEditor", "Save failed.", ex);
            Hud?.Show(L.Get("win.screenshotEditor.saveFailed"), HudStyle.Error);
            return false;
        }
    }

    /// <summary>Fallback without the capture module: Pictures\Screenshots, dated name, unique, PNG with density.</summary>
    private string SaveToPictures(EditorExport export)
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pictures))
        {
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var folder = AppPaths.EnsureDirectory(Path.Combine(pictures, "Screenshots"));
        var path = EditorFiles.UniquePath(folder, EditorFiles.DefaultName(DateTime.Now, L.Get("screenshot.fileNamePrefix")));
        PngWriter.Save(export.Image, export.Scale, path);
        return path;
    }

    /// <summary>Native save dialog, PNG only, the dated name suggested. True when a file was written.</summary>
    public async Task<bool> SaveAsAsync()
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = DefaultName,
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
        });
        if (file is null)
        {
            return false;
        }

        try
        {
            using var export = await controller.ExportAsync(includeBackdrop: true);
            var bytes = await Task.Run(() => PngWriter.Encode(export.Image, export.Scale));
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(bytes);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("screenshotEditor", "Save As failed.", ex);
            Hud?.Show(L.Get("win.screenshotEditor.saveFailed"), HudStyle.Error);
            return false;
        }
    }

    public bool CanAddToShelf => _services.GetService<IShelfIntake>()?.IsAvailable == true;

    /// <summary>
    /// Hands the PNG to the shelf under the default dated name. The shelf
    /// references files, so the PNG is kept in the app's data folder.
    /// </summary>
    public async Task<bool> AddToShelfAsync()
    {
        if (_services.GetService<IShelfIntake>() is not { IsAvailable: true } shelf)
        {
            return false;
        }

        try
        {
            using var export = await controller.ExportAsync(includeBackdrop: true);
            var paths = _services.GetRequiredService<AppPaths>();
            var name = DefaultName;
            var path = await Task.Run(() =>
            {
                var folder = AppPaths.EnsureDirectory(Path.Combine(paths.LocalFolder("ScreenshotEditor"), "Shelf", Guid.NewGuid().ToString("D")));
                var target = Path.Combine(folder, name);
                PngWriter.Save(export.Image, export.Scale, target);
                return target;
            });
            shelf.AddFiles([path]);
            Hud?.Show(L.Get("Strings.shelfName"), HudStyle.Success, "TrayItemAdd");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("screenshotEditor", "Add to Shelf failed.", ex);
            Hud?.Show(L.Get("win.screenshotEditor.shelfFailed"), HudStyle.Error);
            return false;
        }
    }

    /// <summary>Pins the export without the backdrop (annotations, watermark and rounded corners kept).</summary>
    public async Task<bool> PinAsync()
    {
        try
        {
            using var export = await controller.ExportAsync(includeBackdrop: false);
            var buffer = EditorController.ToBuffer(export);
            PinWindow.Show(_services, buffer);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("screenshotEditor", "Pin failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes the full export to <c>%TEMP%\ScreenshotDrag-&lt;GUID&gt;\&lt;name&gt;.png</c>
    /// (deleted an hour later) for drag-out and sharing.
    /// </summary>
    public async Task<string?> WriteTempFileAsync()
    {
        try
        {
            using var export = await controller.ExportAsync(includeBackdrop: true);
            var name = DefaultName;
            return await Task.Run(() =>
            {
                var folder = Path.Combine(Path.GetTempPath(), EditorFiles.DragFolderPrefix + Guid.NewGuid().ToString("D"));
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, name);
                PngWriter.Save(export.Image, export.Scale, path);
                ScheduleCleanup(folder);
                return path;
            });
        }
        catch (Exception ex)
        {
            Log.Error("screenshotEditor", "Could not write the drag file.", ex);
            return null;
        }
    }

    public async Task<bool> ShareAsync()
    {
        var sheet = _services.GetService<IShareSheet>();
        if (sheet is not { IsAvailable: true })
        {
            return false;
        }

        var path = await WriteTempFileAsync();
        if (path is null)
        {
            return false;
        }

        return await sheet.ShareFileAsync(WindowInterop.Handle(owner), path, L.Get("screenshot.editorTitle"), () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(controller.MarkClean));
    }

    public void CopyText(string text)
    {
        _services.GetRequiredService<IClipboardService>().SetText(text);
        Hud?.Show(L.Get("Strings.ocrCopied"), HudStyle.Success, "Copy");
    }

    private static void ScheduleCleanup(string folder) =>
        _ = Task.Delay(TimeSpan.FromHours(1)).ContinueWith(_ => DeleteDragFolder(folder), TaskScheduler.Default);

    public static void DeleteDragFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (info.Exists && EditorFiles.IsDragFolderName(info.Name) && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("screenshotEditor", $"Could not delete {folder}.", ex);
        }
    }
}
