// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.Preview;
using Rivet.App.Features.Capture.Recent;
using Rivet.Core.App;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture.Output;

/// <summary>
/// The after-capture pipeline every screenshot goes through (spec 01 §3.7):
/// latest-capture store, recent captures, automatic copy, the default action
/// (Ask, Save, Save &amp; Copy, Copy, Edit), the automatic shelf copy and the
/// quick preview decision. Direct outputs use the raw pixels (only the 1x
/// option applies); backdrops and watermarks belong to the editor.
/// </summary>
internal sealed class CaptureRouter(IServiceProvider services)
{
    private readonly ISettingsStore _settings = services.GetRequiredService<ISettingsStore>();
    private readonly CaptureOutputService _output = services.GetRequiredService<CaptureOutputService>();
    private readonly ICaptureClipboard _clipboard = services.GetRequiredService<ICaptureClipboard>();
    private readonly IHud _hud = services.GetRequiredService<IHud>();
    private int _generation;

    public async Task RouteAsync(CapturedImage capture)
    {
        var generation = ++_generation;

        // 1. The latest capture (kept only while "Edit latest screenshot" is on).
        if (_settings.Get(CaptureSettings.LastCaptureShortcutEnabled))
        {
            services.GetRequiredService<LatestCaptureStore>().Store(capture.Image);
        }

        // 3. Any open preview closes; 4. the capture joins the history.
        var preview = services.GetRequiredService<QuickPreviewController>();
        preview.Close();
        var recentId = services.GetRequiredService<RecentCapturesStore>().AddScreenshot(capture.Image, capture.Anchor);

        var action = CaptureSettings.ParseDefaultAction(_settings.Get(CaptureSettings.DefaultAction));
        var editor = services.GetService<IScreenshotEditor>();
        if (action == ScreenshotDefaultAction.Edit && editor is null)
        {
            // The editor feature is not part of this build: keep the capture by saving it.
            action = ScreenshotDefaultAction.Save;
        }

        // 5. Automatic copy, in the background; dropped when the clipboard changed meanwhile.
        if (_settings.Get(CaptureSettings.CopyToClipboard) && action is not (ScreenshotDefaultAction.Copy or ScreenshotDefaultAction.SaveAndCopy))
        {
            var before = _clipboard.ChangeCount;
            _ = _output.CopyAsync(capture.Image, applyDownscale: true, stillWanted: () =>
                generation == _generation && _clipboard.ChangeCount == before && _settings.Get(CaptureSettings.CopyToClipboard));
        }

        // 6. The default action.
        var request = new PreviewRequest
        {
            Capture = capture,
            Decision = default,
            RecentId = recentId,
        };
        var succeeded = true;
        switch (action)
        {
            case ScreenshotDefaultAction.Edit:
                await editor!.OpenAsync(new ScreenshotEditRequest
                {
                    Image = capture.Image,
                    Kind = capture.Kind,
                    WindowTitle = capture.WindowTitle,
                    RecentCaptureId = recentId,
                });
                return;

            case ScreenshotDefaultAction.Save:
                request.Saved = await _output.SaveAsync(capture.Image, applyDownscale: true);
                succeeded = request.Saved is not null;
                if (request.Saved is { } saved)
                {
                    _hud.Show(L.Format("screenshot.savedHUDFormat", saved.FolderName), HudStyle.Success, "Save");
                }

                break;

            case ScreenshotDefaultAction.SaveAndCopy:
                request.Saved = await _output.SaveAsync(capture.Image, applyDownscale: true);
                if (request.Saved is { } savedFile)
                {
                    request.Copied = await _output.CopyAsync(capture.Image, applyDownscale: true, existingFile: savedFile.Path);
                    _hud.Show(
                        request.Copied ? L.Format("screenshot.savedAndCopiedHUDFormat", savedFile.FolderName) : L.Format("screenshot.savedHUDFormat", savedFile.FolderName),
                        HudStyle.Success,
                        "Save");
                }

                succeeded = request.Saved is not null && request.Copied;
                break;

            case ScreenshotDefaultAction.Copy:
                request.Copied = await _output.CopyAsync(capture.Image, applyDownscale: true);
                succeeded = request.Copied;
                if (request.Copied)
                {
                    _hud.Show(L.Get("screenshot.copiedHUD"), HudStyle.Success, "Copy");
                }

                break;
        }

        // 7. Automatic shelf copy (the shelf gets its own file, not the saved one).
        if (_settings.Get(CaptureSettings.AddToShelf) && services.GetService<IShelfIntake>() is { IsAvailable: true } shelf
            && services.GetRequiredService<FeatureRuntime>().IsEngaged(FeatureIds.Shelf))
        {
            request.DiscardShelfItem = AddToShelf(shelf, capture, request.Saved);
        }

        // 8. The preview.
        var decision = PreviewPolicy.Decide(
            action,
            succeeded,
            _settings.Get(CaptureSettings.PreviewEnabled),
            _settings.Get(CaptureSettings.PreviewDuration));
        if (!decision.Show)
        {
            return;
        }

        preview.Show(new PreviewRequest
        {
            Capture = capture,
            Decision = decision,
            TakesFocus = PreviewPolicy.TakesFocus(decision, _settings.Get(CaptureSettings.PreviewTakesFocus)),
            ActionRan = action != ScreenshotDefaultAction.Ask,
            RecentId = recentId,
            Saved = request.Saved,
            Copied = request.Copied,
            DiscardShelfItem = request.DiscardShelfItem,
        });
    }

    /// <summary>Writes the shelf's own PNG copy and hands it over; returns how to cancel it.</summary>
    private Action AddToShelf(IShelfIntake shelf, CapturedImage capture, SavedCapture? saved)
    {
        var cancelled = false;
        var paths = services.GetRequiredService<AppPaths>();
        _ = Task.Run(() =>
        {
            try
            {
                var folder = Path.Combine(paths.Cache, "Shelf Screenshots");
                Directory.CreateDirectory(folder);
                var name = saved is not null ? Path.GetFileName(saved.Path) : CaptureOutputService.DefaultName();
                var target = Path.Combine(folder, CaptureNaming.UniqueName(folder, name, File.Exists));
                if (saved is not null && File.Exists(saved.Path))
                {
                    File.Copy(saved.Path, target);
                }
                else
                {
                    var output = CaptureImaging.ForOutput(capture.Image, _settings.Get(CaptureSettings.Downscale));
                    CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(output), target);
                }

                Rivet.Core.Util.UiThread.Post(() =>
                {
                    if (!cancelled)
                    {
                        shelf.AddFiles([target]);
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("capture", "Could not add the screenshot to the shelf.", ex);
                services.GetRequiredService<ICapturePlatform>().Beep();
            }
        });
        return () => cancelled = true;
    }
}
