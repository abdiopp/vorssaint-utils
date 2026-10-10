// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.Output;
using Rivet.App.Shell;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture.Text;

/// <summary>
/// "Copy text from screen" (spec 01 §3.16): a QR code in the area wins (shown
/// in the result panel); otherwise the text is recognized on device with
/// Windows OCR, joined in reading order and copied. The image is never kept.
/// When Windows has no OCR language, the user is told how to add one.
/// </summary>
internal sealed class ScreenTextService(IServiceProvider services)
{
    public const string OpenLanguageSettingsActionId = "screenOCR.openLanguageSettings";

    private readonly ISettingsStore _settings = services.GetRequiredService<ISettingsStore>();
    private readonly IOcrEngine _ocr = services.GetRequiredService<IOcrEngine>();
    private readonly IHud _hud = services.GetRequiredService<IHud>();
    private readonly CaptureOutputService _output = services.GetRequiredService<CaptureOutputService>();
    private QrResultWindow? _qrWindow;
    private int _generation;

    public async Task ProcessAsync(PixelBuffer image)
    {
        var generation = ++_generation;
        if (_settings.Get(CaptureSettings.OcrDetectQrCodes))
        {
            var codes = await Task.Run(() => QrDetector.Detect(image));
            if (generation != _generation)
            {
                return;
            }

            if (codes.Count > 0)
            {
                ShowQrResult(codes, image.Height);
                return;
            }
        }

        if (!_ocr.GetStatus().IsAvailable)
        {
            ExplainMissingLanguage();
            return;
        }

        OcrPage? page;
        try
        {
            var languages = OcrTextJoiner.PreferredLanguages(Localizer.Current.Language.Code());
            page = await Task.Run(() => _ocr.RecognizeAsync(image, languages));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("capture", "Text recognition failed.", ex);
            page = null;
        }

        if (generation != _generation)
        {
            return;
        }

        if (page is null)
        {
            ExplainMissingLanguage();
            return;
        }

        var text = OcrTextJoiner.Join(page, _settings.Get(CaptureSettings.OcrRemoveLineBreaks));
        if (string.IsNullOrWhiteSpace(text))
        {
            _hud.Show(L.Get("Strings.ocrNoText"), HudStyle.Info, "TextGrammarError");
            return;
        }

        if (_output.CopyText(text))
        {
            _hud.Show(L.Get("Strings.ocrCopied"), HudStyle.Success, "Copy");
        }
    }

    /// <summary>Shows the QR panel under the pointer (replacing an open one).</summary>
    public void ShowQrResult(IReadOnlyList<DetectedCode> codes, int imageHeight)
    {
        if (codes.Count == 0)
        {
            return;
        }

        _qrWindow?.Close();
        var payload = QrPayloads.Join(codes, imageHeight);
        var link = QrPayloads.OpenableUrl(codes);
        var window = new QrResultWindow(payload, link);
        window.CopyRequested += (_, _) =>
        {
            window.Close();
            if (_output.CopyText(payload))
            {
                _hud.Show(L.Get("Strings.ocrQRCopied"), HudStyle.Success, "Copy");
            }
        };
        window.OpenRequested += (_, _) =>
        {
            window.Close();
            if (link is not null)
            {
                services.GetRequiredService<IShellService>().OpenUrl(link.AbsoluteUri);
            }
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_qrWindow, window))
            {
                _qrWindow = null;
            }
        };
        _qrWindow = window;

        // Centred under the pointer, 18 DIPs below it, kept 12 DIPs inside the work area.
        var screens = services.GetRequiredService<IScreenService>();
        var pointer = screens.CursorPosition;
        var display = screens.ScreenFromPoint(pointer);
        var s = display.Scale;
        var width = window.Width * s;
        var estimatedHeight = 220 * s;
        var x = pointer.X - (width / 2);
        var y = pointer.Y + (18 * s) - (QrResultWindow.ShadowMargin * s);
        var work = display.WorkArea;
        x = Math.Clamp(x, work.X + (12 * s), Math.Max(work.X + (12 * s), work.Right - width - (12 * s)));
        y = Math.Clamp(y, work.Y + (12 * s), Math.Max(work.Y + (12 * s), work.Bottom - estimatedHeight - (12 * s)));
        window.Position = new Avalonia.PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        window.Opened += (_, _) =>
        {
            CaptureWindows.ApplyWorkflowChrome(window);
            window.Activate();
            services.GetService<IWindowChrome>()?.BringToFront(WindowInterop.Handle(window));
        };
        window.Show();
    }

    private void ExplainMissingLanguage()
    {
        _hud.Show(L.Get("win.capture.ocrUnavailableTitle"), HudStyle.Warning, "LocalLanguage", TimeSpan.FromSeconds(4));
        services.GetService<INotificationService>()?.Show(new NotificationRequest
        {
            Title = L.Get("win.capture.ocrUnavailableTitle"),
            Body = L.Get("win.capture.ocrUnavailableBody"),
            Buttons = [(L.Get("win.capture.openLanguageSettings"), OpenLanguageSettingsActionId)],
            ClickActionId = OpenLanguageSettingsActionId,
            Tag = "ocr-language",
        });
    }

    public void OpenLanguageSettings() =>
        Dispatcher.UIThread.Post(() => services.GetRequiredService<IShellService>().OpenSystemSettings("ms-settings:regionlanguage"));
}
