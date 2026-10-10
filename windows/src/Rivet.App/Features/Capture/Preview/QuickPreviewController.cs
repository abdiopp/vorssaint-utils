// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.Output;
using Rivet.App.Features.Capture.Pins;
using Rivet.App.Features.Capture.Text;
using Rivet.App.Shell;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture.Preview;

/// <summary>One preview presentation.</summary>
internal sealed class PreviewRequest
{
    public required CapturedImage Capture { get; init; }

    public required PreviewDecision Decision { get; init; }

    public bool TakesFocus { get; init; }

    /// <summary>The default action already ran (Automatic placement goes to the corner).</summary>
    public bool ActionRan { get; init; }

    /// <summary>Reopened from Recent captures: Discard touches nothing.</summary>
    public bool Reopened { get; init; }

    public SavedCapture? Saved { get; set; }

    public bool Copied { get; set; }

    public string? RecentId { get; init; }

    /// <summary>Removes this capture's automatic shelf copy (or cancels its pending encode).</summary>
    public Action? DiscardShelfItem { get; set; }
}

/// <summary>
/// Shows and drives the quick preview (spec 01 §3.9): placement, the
/// auto-dismiss timer (paused while hovered or sharing), keyboard, Edit,
/// Save, Copy, Pin, Share, QR and Discard (which recycles a saved file and
/// gives its %# number back). Esc only dismisses, never discards.
/// </summary>
internal sealed class QuickPreviewController
{
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly CaptureOutputService _output;
    private readonly IScreenService _screens;
    private readonly ICapturePlatform _platform;
    private readonly IHud _hud;
    private QuickPreviewWindow? _window;
    private PreviewRequest? _request;
    private DispatcherTimer? _timer;
    private IReadOnlyList<DetectedCode> _codes = [];
    private int _generation;
    private bool _sharing;
    private DateTime _shareStarted;

    public QuickPreviewController(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _output = services.GetRequiredService<CaptureOutputService>();
        _screens = services.GetRequiredService<IScreenService>();
        _platform = services.GetRequiredService<ICapturePlatform>();
        _hud = services.GetRequiredService<IHud>();
    }

    public bool IsOpen => _window is not null;

    internal QuickPreviewWindow? Window => _window;

    public void Show(PreviewRequest request)
    {
        Close();
        var generation = ++_generation;
        _request = request;
        _codes = [];
        // Edit is always offered: without the editor module it opens the image in the default app.
        var window = new QuickPreviewWindow(request.Capture, request.Decision.IsPersistent, editAvailable: true);
        window.SetCompleted(request.Saved is not null, request.Copied);
        window.Command += (_, command) => OnCommand(command);
        window.DragOut = DragOutAsync;
        window.PointerEntered += (_, _) => PauseTimer();
        window.PointerExited += (_, _) => RestartTimer();

        // The share sheet reports a chosen app but not a cancel: focus coming back means it closed.
        window.Activated += (_, _) =>
        {
            if (_sharing && DateTime.UtcNow - _shareStarted > TimeSpan.FromSeconds(1))
            {
                _sharing = false;
                RestartTimer();
            }
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
                _timer?.Stop();
            }
        };
        _window = window;

        var display = CaptureWindows.DisplayFor(_screens, request.Capture.Anchor);
        var position = CaptureSettings.ParsePreviewPosition(_settings.Get(CaptureSettings.PreviewPositionValue));
        var margin = QuickPreviewWindow.ShadowMargin * display.Scale;
        var origin = PreviewPolicy.Place(position, request.ActionRan, request.Capture.Anchor, _screens.CursorPosition, display.WorkArea, display.Scale);
        var bounds = new PixelRect(
            (int)Math.Round(origin.X - margin),
            (int)Math.Round(origin.Y - margin),
            (int)Math.Round(window.Width * display.Scale),
            (int)Math.Round(window.Height * display.Scale));
        window.ShowActivated = request.TakesFocus;
        window.Opened += (_, _) =>
        {
            CaptureWindows.ApplyWorkflowChrome(window);
            CaptureWindows.PlaceExactly(window, bounds, display.Scale);
            if (request.TakesFocus)
            {
                window.Activate();
                _services.GetService<IWindowChrome>()?.BringToFront(WindowInterop.Handle(window));
            }
        };
        window.Position = new Avalonia.PixelPoint(bounds.X, bounds.Y);
        window.Show();
        RestartTimer();

        // One background QR scan of the full-resolution capture; silent when nothing is found.
        var image = request.Capture.Image;
        _ = Task.Run(() => QrDetector.Detect(image)).ContinueWith(task =>
        {
            if (generation == _generation && _window is not null && task.Status == TaskStatus.RanToCompletion && task.Result.Count > 0)
            {
                _codes = task.Result;
                _window.ShowQrButton();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Close()
    {
        _timer?.Stop();
        _timer = null;
        var window = _window;
        _window = null;
        _sharing = false;
        window?.Close();
    }

    private void OnCommand(PreviewCommand command)
    {
        if (_request is not { } request)
        {
            return;
        }

        switch (command)
        {
            case PreviewCommand.Dismiss:
                Close();
                break;
            case PreviewCommand.Edit:
                Close();
                Dispatcher.UIThread.Post(() => _ = OpenEditorAsync(request));
                break;
            case PreviewCommand.Save:
                _ = SaveAsync(request);
                break;
            case PreviewCommand.Copy:
                _ = CopyAsync(request);
                break;
            case PreviewCommand.Pin:
                _services.GetRequiredService<PinManager>().Pin(request.Capture.Image);
                Close();
                break;
            case PreviewCommand.Discard:
                Discard(request);
                break;
            case PreviewCommand.Qr:
                var codes = _codes;
                Close();
                _services.GetRequiredService<ScreenTextService>().ShowQrResult(codes, request.Capture.Image.Height);
                break;
            case PreviewCommand.Share:
                Share(request);
                break;
        }
    }

    private async Task OpenEditorAsync(PreviewRequest request)
    {
        if (_services.GetService<IScreenshotEditor>() is { } editor)
        {
            await editor.OpenAsync(new ScreenshotEditRequest
            {
                Image = request.Capture.Image,
                SourcePath = request.Saved?.Path,
                Kind = request.Capture.Kind,
                WindowTitle = request.Capture.WindowTitle,
                RecentCaptureId = request.RecentId,
            });
            return;
        }

        // No editor installed: open the capture in the default image app (Photos can edit it).
        var path = request.Saved?.Path ?? await Task.Run(() => CaptureTempFiles.WriteExport(request.Capture.Image, CaptureOutputService.DefaultName()));
        _services.GetRequiredService<IShellService>().OpenFile(path);
    }

    private async Task SaveAsync(PreviewRequest request)
    {
        if (request.Saved is not null)
        {
            return;
        }

        PauseTimer();
        var saved = await _output.SaveAsync(request.Capture.Image, applyDownscale: true);
        if (saved is null)
        {
            RestartTimer();
            return;
        }

        request.Saved = saved;
        _hud.Show(L.Format("screenshot.savedHUDFormat", saved.FolderName), HudStyle.Success, "Save");
        Close();
    }

    private async Task CopyAsync(PreviewRequest request)
    {
        PauseTimer();
        if (await _output.CopyAsync(request.Capture.Image, applyDownscale: true))
        {
            request.Copied = true;
            _hud.Show(L.Get("screenshot.copiedHUD"), HudStyle.Success, "Copy");
            Close();
        }
        else
        {
            RestartTimer();
        }
    }

    private void Discard(PreviewRequest request)
    {
        if (!request.Reopened)
        {
            if (request.Saved is { } saved)
            {
                // Recycle Bin, never a permanent delete; give the %# number back when possible.
                _services.GetRequiredService<IShellService>().MoveToRecycleBin([saved.Path]);
                if (saved.ConsumedNumber is { } n)
                {
                    FileNumberSequence.Rewind(_settings, n);
                }
            }

            request.DiscardShelfItem?.Invoke();
        }

        Close();
    }

    private void Share(PreviewRequest request)
    {
        if (_window is not { } window)
        {
            return;
        }

        try
        {
            var output = CaptureImaging.ForOutput(request.Capture.Image, _settings.Get(CaptureSettings.Downscale));
            var path = CaptureTempFiles.WriteExport(output, CaptureOutputService.DefaultName());
            _sharing = true;
            _shareStarted = DateTime.UtcNow;
            PauseTimer();
            var started = _platform.ShareFile(WindowInterop.Handle(window), path, L.Get("screenshot.editorTitle"), chosen => Dispatcher.UIThread.Post(() =>
            {
                _sharing = false;
                if (chosen)
                {
                    Close();
                }
                else
                {
                    RestartTimer();
                }
            }));
            if (!started)
            {
                _sharing = false;
                _platform.Beep();
                RestartTimer();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("capture", "Could not write the shared file.", ex);
            _sharing = false;
            _platform.Beep();
        }
    }

    private async Task DragOutAsync(PointerPressedEventArgs press)
    {
        if (_request is not { } request || _window is not { } window)
        {
            return;
        }

        PauseTimer();
        try
        {
            // The preview drags the raw capture at full resolution (no 1x downscale).
            var path = await Task.Run(() => CaptureTempFiles.WriteExport(request.Capture.Image, CaptureOutputService.DefaultName()));
            var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            if (file is null)
            {
                return;
            }

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(file));
            await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("capture", "Dragging the capture out failed.", ex);
            _platform.Beep();
        }
        finally
        {
            RestartTimer();
        }
    }

    private void PauseTimer() => _timer?.Stop();

    private void RestartTimer()
    {
        _timer?.Stop();
        if (_window is null || _sharing || _request?.Decision.Timeout is not { } timeout)
        {
            return;
        }

        if (_window.IsPointerOver)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = timeout };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            Close();
        };
        _timer.Start();
    }
}
