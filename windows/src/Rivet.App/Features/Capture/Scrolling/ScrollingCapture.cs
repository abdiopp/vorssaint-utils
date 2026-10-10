// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;
using PixelPoint = Rivet.Core.Platform.PixelPoint;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Features.Capture.Scrolling;

/// <summary>
/// The control bar shown while a scrolling capture runs (spec 01 §3.15.2):
/// top centre of the region's display, the message, the stitched height,
/// automatic scrolling, Cancel and Done. Kept out of the captured frames.
/// </summary>
internal sealed class ScrollingControlBar : Window
{
    private readonly TextBlock _height = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Right };
    private readonly SymbolIcon _icon = new() { Symbol = FluentIcons.Common.Symbol.ArrowSortDown, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly Avalonia.Controls.Primitives.ToggleButton _auto;
    private readonly Button _done;
    private readonly Button _cancel;

    public ScrollingControlBar()
    {
        CaptureUi.MakeFloating(this);
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        _icon.Bind(SymbolIcon.ForegroundProperty, _icon.GetResourceObservable("AccentBrush").ToBinding());
        _height.Bind(TextBlock.ForegroundProperty, _height.GetResourceObservable("TextSecondaryBrush").ToBinding());

        var message = new TextBlock
        {
            Text = L.Get("screenshot.scrollingCaptureProgressHUD"),
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 340,
            TextWrapping = TextWrapping.Wrap,
        };
        _auto = new Avalonia.Controls.Primitives.ToggleButton
        {
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new SymbolIcon { Symbol = FluentIcons.Common.Symbol.ArrowAutofitDown, FontSize = 14 },
                    new TextBlock { Text = L.Get("win.capture.autoScroll"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(_auto, L.Get("win.capture.autoScrollTooltip"));
        _auto.IsCheckedChanged += (_, _) => AutoScrollChanged?.Invoke(this, _auto.IsChecked == true);
        _cancel = CaptureUi.TextButton(L.Get("screenshot.cancel"), () => CancelRequested?.Invoke(this, EventArgs.Empty), tooltip: L.Get("win.capture.keyEsc"));
        _done = CaptureUi.TextButton(L.Get("screenshot.done"), () => DoneRequested?.Invoke(this, EventArgs.Empty), "Checkmark", accent: true, tooltip: L.Get("win.capture.keyEnter"));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _icon, message, _height, _auto, _cancel, _done },
        };
        var plate = CaptureUi.Plate(row, radius: 10, padding: new Thickness(14, 8));
        plate.Margin = new Thickness(10);
        Content = plate;
    }

    public event EventHandler? DoneRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler<bool>? AutoScrollChanged;

    public void SetHeight(int pixels) => _height.Text = L.Format("win.capture.pixelsFormat", pixels);

    public void SetFinishing()
    {
        _icon.Symbol = FluentIcons.Common.Symbol.ArrowSync;
        _done.IsEnabled = false;
        _auto.IsEnabled = false;
    }

    public void StopAutoScroll() => _auto.IsChecked = false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DoneRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnKeyDown(e);
    }
}

/// <summary>
/// Runs a scrolling capture (spec 01 §3.15, §6.16): polls the region every
/// 90 ms while the user scrolls by any means (or while automatic scrolling
/// sends wheel notches over the region), stitches the newly revealed strips
/// and routes the result like any screenshot. Enter finishes and Esc cancels
/// through the shared keyboard hook, so neither reaches the page.
/// </summary>
internal sealed class ScrollingCaptureController(IServiceProvider services)
{
    private ScrollCaptureEngine? _engine;
    private CancellationTokenSource? _cancel;

    public bool IsRunning => _engine is not null;

    /// <summary>Asks the running capture to finish (Done, Enter, or the capture shortcuts pressed again).</summary>
    public void Finish() => _engine?.RequestFinish();

    public void Cancel() => _cancel?.Cancel();

    public async Task<CapturedImage?> RunAsync(ScreenInfo display, PixelRect region)
    {
        if (_engine is not null)
        {
            return null;
        }

        var settings = services.GetRequiredService<ISettingsStore>();
        var capturer = services.GetRequiredService<IScreenCapturer>();
        var hooks = services.GetRequiredService<IInputHooks>();
        var platform = services.GetRequiredService<ICapturePlatform>();
        var hud = services.GetRequiredService<IHud>();
        using var source = capturer.CreateRegionCapture(display, region, new DisplayCaptureOptions
        {
            IncludeCursor = false,
            HideOwnWindows = settings.Get(CaptureSettings.HideOwnWindows),
        });
        if (source is null)
        {
            hud.Show(L.Get("screenshot.captureFailed"), HudStyle.Error, "ErrorCircle");
            platform.Beep();
            return null;
        }

        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        var engine = new ScrollCaptureEngine(source.CaptureFrame);
        _engine = engine;
        var bar = new ScrollingControlBar();
        bar.SetHeight(region.Height);
        bar.DoneRequested += (_, _) =>
        {
            bar.SetFinishing();
            engine.RequestFinish();
        };
        bar.CancelRequested += (_, _) => cancel.Cancel();

        // Automatic scrolling: one wheel notch over the region after each processed frame,
        // stopping once the page no longer moves (the end of the page).
        var autoScroll = false;
        var stableFrames = 0;
        bar.AutoScrollChanged += (_, on) =>
        {
            autoScroll = on;
            stableFrames = 0;
            if (on)
            {
                platform.SetCursorPosition(new PixelPoint(region.X + (region.Width / 2), region.Y + (region.Height / 2)));
            }
        };
        engine.Progress += height => Dispatcher.UIThread.Post(() => bar.SetHeight(height));
        engine.FrameProcessed += stable => Dispatcher.UIThread.Post(() =>
        {
            if (!autoScroll || cancel.IsCancellationRequested)
            {
                return;
            }

            stableFrames = stable ? stableFrames + 1 : 0;
            if (stableFrames >= 12)
            {
                autoScroll = false;
                bar.StopAutoScroll();
                bar.SetFinishing();
                engine.RequestFinish();
                return;
            }

            if (stableFrames % 3 == 0)
            {
                hooks.SendWheel(-120, horizontal: false);
            }
        });

        var keyboard = hooks.SubscribeKeyboard((ref KeyboardHookEvent e) =>
        {
            if (e.VirtualKey == Core.Shortcuts.VirtualKeys.Return && e.Modifiers == Core.Shortcuts.KeyModifiers.None)
            {
                if (e.Action == KeyAction.Down)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        bar.SetFinishing();
                        engine.RequestFinish();
                    });
                }

                return true;
            }

            if (e.VirtualKey == Core.Shortcuts.VirtualKeys.Escape)
            {
                if (e.Action == KeyAction.Down)
                {
                    Dispatcher.UIThread.Post(cancel.Cancel);
                }

                return true;
            }

            return false;
        }, priority: 250);

        bar.Opened += (_, _) =>
        {
            CaptureWindows.ApplyWorkflowChrome(bar, noActivate: true);
            var s = display.Scale;
            var w = (int)Math.Ceiling(bar.Bounds.Width * s);
            var x = display.WorkArea.X + ((display.WorkArea.Width - w) / 2);
            var y = display.WorkArea.Y + (int)Math.Round(14 * s);
            bar.Position = new Avalonia.PixelPoint(x, y);
        };
        bar.Show();

        ScrollCaptureResult result;
        try
        {
            result = await Task.Run(() => engine.RunAsync(cancel.Token));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("capture", "Scrolling capture failed.", ex);
            result = new ScrollCaptureResult(ScrollCaptureOutcome.Failed, null);
        }
        finally
        {
            keyboard.Dispose();
            bar.Close();
            _engine = null;
            _cancel = null;
        }

        switch (result.Outcome)
        {
            case ScrollCaptureOutcome.Cancelled:
                hud.Show(L.Get("Strings.mediaCancelled"), HudStyle.Info, "Dismiss");
                return null;
            case ScrollCaptureOutcome.Failed:
                hud.Show(L.Get("screenshot.captureFailed"), HudStyle.Error, "ErrorCircle");
                platform.Beep();
                return null;
            case ScrollCaptureOutcome.Partial:
                hud.Show(L.Get("screenshot.scrollingCapturePartialHUD"), HudStyle.Warning, "Warning");
                break;
            case ScrollCaptureOutcome.Limited:
                hud.Show(L.Get("screenshot.scrollingCaptureTooLongHUD"), HudStyle.Warning, "Warning");
                break;
        }

        return result.Image is { } image ? new CapturedImage(CaptureImaging.WithScale(image, display.Scale), region) { Kind = CaptureTargetKind.Area } : null;
    }
}
