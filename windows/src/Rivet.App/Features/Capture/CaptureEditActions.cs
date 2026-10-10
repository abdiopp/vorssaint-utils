// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.Output;
using Rivet.App.Features.Capture.Preview;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Capture;

/// <summary>
/// "Edit latest screenshot" and "Edit clipboard image" (spec 01 §3.13). They
/// open the screenshot editor when its module is present; otherwise the
/// image appears in the quick preview, where it can still be saved, copied
/// or pinned.
/// </summary>
internal sealed class CaptureEditActions(IServiceProvider services)
{
    /// <summary>Images over 60 megapixels are refused.</summary>
    public const long MaxClipboardPixels = 60_000_000;

    public async Task EditLatestAsync()
    {
        var image = await Task.Run(services.GetRequiredService<LatestCaptureStore>().Load);
        if (image is null)
        {
            services.GetRequiredService<IHud>().Show(L.Get("screenshot.lastCaptureMissing"), HudStyle.Info, "Image");
            return;
        }

        await OpenAsync(new CapturedImage(image, default));
    }

    public async Task EditClipboardAsync()
    {
        var clipboard = services.GetRequiredService<ICaptureClipboard>();
        var read = await Task.Run(() => clipboard.ReadImage(MaxClipboardPixels));
        if (read is null)
        {
            services.GetRequiredService<IHud>().Show(L.Get("screenshot.clipboardImageMissing"), HudStyle.Info, "ClipboardImage");
            return;
        }

        await OpenAsync(new CapturedImage(read.Image, default), read.SourceFile);
    }

    private async Task OpenAsync(CapturedImage capture, string? sourcePath = null)
    {
        services.GetRequiredService<QuickPreviewController>().Close();
        if (services.GetService<IScreenshotEditor>() is { } editor)
        {
            await editor.OpenAsync(new ScreenshotEditRequest { Image = capture.Image, SourcePath = sourcePath });
            return;
        }

        services.GetRequiredService<QuickPreviewController>().Show(new PreviewRequest
        {
            Capture = capture,
            Decision = new PreviewDecision(true, PreviewPolicy.RecoveryTimeout),
            TakesFocus = true,
            Reopened = true,
        });
    }
}
