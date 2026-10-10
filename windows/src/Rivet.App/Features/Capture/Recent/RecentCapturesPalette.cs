// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.Preview;
using Rivet.App.Modules;
using Rivet.App.Shell;
using Rivet.Core.Capture;
using Rivet.Core.Platform;
using Rivet.Imaging.Capture;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Features.Capture.Recent;

/// <summary>Restore and Open for history entries, and the single palette (spec 01 §3.12).</summary>
internal sealed class RecentCapturesActions(IServiceProvider services)
{
    private RecentCapturesPalette? _palette;

    /// <summary>Shows the palette, or brings the open one back to the front.</summary>
    public void ShowPalette()
    {
        if (_palette is { } open)
        {
            open.Activate();
            return;
        }

        var palette = new RecentCapturesPalette(services);
        palette.Closed += (_, _) => _palette = null;
        _palette = palette;
        palette.Show();
    }

    public void ClosePalette() => _palette?.Close();

    public async void Activate(RecentCaptureEntry entry)
    {
        var store = services.GetRequiredService<RecentCapturesStore>();
        var platform = services.GetRequiredService<ICapturePlatform>();
        services.GetService<IAppShell>()?.ClosePanel();
        if (entry.IsScreenshot)
        {
            var path = store.ScreenshotPath(entry);
            var image = path is null ? null : await Task.Run(() => CaptureImaging.DecodeFile(path));
            if (image is null)
            {
                store.Remove(entry.Id);
                platform.Beep();
                return;
            }

            var scale = entry.Scale is > 0 and <= 4 ? entry.Scale.Value : image.Scale;
            var capture = new CapturedImage(CaptureImaging.WithScale(image, scale), new PixelRect(entry.AnchorX, entry.AnchorY, entry.AnchorWidth, entry.AnchorHeight));
            services.GetRequiredService<QuickPreviewController>().Show(new PreviewRequest
            {
                Capture = capture,
                Decision = new PreviewDecision(true, PreviewPolicy.RecoveryTimeout),
                TakesFocus = true,
                Reopened = true,
                RecentId = entry.Id,
            });
            return;
        }

        if (entry.RecordingPath is { } recording && File.Exists(recording))
        {
            services.GetRequiredService<IShellService>().OpenFile(recording);
        }
        else
        {
            store.Remove(entry.Id);
            platform.Beep();
        }
    }
}

/// <summary>
/// The floating recent-captures palette (spec 01 §3.12): 440 DIPs of
/// content, centred on the pointer's display, hidden on Esc, on a click
/// elsewhere and when another app is activated.
/// </summary>
internal sealed class RecentCapturesPalette : Window
{
    private const double ContentWidth = 440;
    private const double Shadow = 12;

    public RecentCapturesPalette(IServiceProvider services)
    {
        CaptureUi.MakeFloating(this);
        Width = ContentWidth + (2 * Shadow);
        SizeToContent = SizeToContent.Height;
        ShowActivated = true;
        var view = new RecentCapturesView(services, showHeader: true, close: Close);
        view.Dismissing += (_, _) => Close();
        var plate = CaptureUi.Plate(view, radius: 18, padding: new Thickness(14));
        plate.Margin = new Thickness(Shadow);
        Content = plate;
        Deactivated += (_, _) =>
        {
            // A confirmation dialog owned by the palette must not close it.
            if (OwnedWindows.Count == 0)
            {
                Close();
            }
        };
        Opened += (_, _) =>
        {
            CaptureWindows.ApplyWorkflowChrome(this);
            var screens = services.GetRequiredService<IScreenService>();
            var display = screens.ScreenFromPoint(screens.CursorPosition);
            var s = display.Scale;
            var w = Bounds.Width * s;
            var h = Math.Max(Bounds.Height, 200) * s;
            var work = display.WorkArea;
            var x = Math.Clamp(work.X + ((work.Width - w) / 2), work.X + (16 * s), Math.Max(work.X + (16 * s), work.Right - w - (16 * s)));
            var y = Math.Clamp(work.Y + ((work.Height - h) / 2), work.Y + (16 * s), Math.Max(work.Y + (16 * s), work.Bottom - h - (16 * s)));
            Position = new Avalonia.PixelPoint((int)Math.Round(x), (int)Math.Round(y));
            Activate();
            services.GetService<IWindowChrome>()?.BringToFront(WindowInterop.Handle(this));
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }
}
