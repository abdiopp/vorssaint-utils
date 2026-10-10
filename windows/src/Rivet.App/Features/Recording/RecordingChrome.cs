// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Rivet.App.Features.Recording.Views;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;

namespace Rivet.App.Features.Recording;

/// <summary>Why a recording stopped; decides the delivery and the message.</summary>
public enum StopKind
{
    /// <summary>Stop button, shortcut, tile or tray: the editor opens if the setting allows.</summary>
    User,

    /// <summary>The window closed or the display changed: delivered like a normal stop, with a message.</summary>
    CaptureEnded,

    /// <summary>Less than 500 MB free: saved straight away.</summary>
    DiskAlmostFull,

    /// <summary>The feature was uninstalled: saved straight away.</summary>
    FeatureRemoved,

    /// <summary>The app is quitting: saved straight away, no UI.</summary>
    AppQuit,

    /// <summary>The pill's discard button: the take is deleted.</summary>
    Discard,
}

public sealed record StopReason(StopKind Kind, string? Message = null)
{
    public static StopReason User { get; } = new(StopKind.User);

    /// <summary>Only a normal stop (or a capture that ended on its own) opens the editor.</summary>
    public bool OpensEditor => Kind is StopKind.User or StopKind.CaptureEnded;
}

/// <summary>
/// The recorder's floating UI: region guide, countdown, indicator pill and
/// the "not enough space" alert. All windows are created lazily on the UI
/// thread and closed when not needed.
/// </summary>
public sealed class RecordingChrome
{
    private readonly IScreenService _screens;
    private RecordingIndicatorWindow? _indicator;
    private RegionGuideWindow? _guide;
    private CountdownWindow? _countdown;
    private DispatcherTimer? _countdownFade;

    public RecordingChrome(IScreenService screens)
    {
        _screens = screens;
    }

    public event EventHandler? PauseRequested;

    public event EventHandler? StopRequested;

    public event EventHandler? DiscardRequested;

    public bool IsIndicatorVisible => _indicator is { IsVisible: true };

    public bool IsGuideVisible => _guide is { IsVisible: true };

    public bool IsCountdownVisible => _countdown is { IsVisible: true };

    public RecordingIndicatorView? IndicatorView => _indicator?.View;

    public void ShowGuide(RecordingTarget target)
    {
        _guide ??= new RegionGuideWindow();
        _guide.ShowFor(target.Monitor, target.Region);
    }

    public void HideGuide()
    {
        _guide?.Close();
        _guide = null;
    }

    /// <summary>One countdown step: fades in, drains for 0.92 s, then fades out (re-presented for the next number).</summary>
    public void ShowCountdown(int number)
    {
        var screen = _screens.ScreenFromPoint(_screens.CursorPosition);
        _countdown ??= new CountdownWindow();
        _countdown.ShowNumber(screen, number);
        _countdownFade?.Stop();
        _countdownFade = new DispatcherTimer { Interval = TimeSpan.FromSeconds(CountdownView.DrainSeconds) };
        _countdownFade.Tick += (_, _) =>
        {
            _countdownFade?.Stop();
            if (_countdown is { } window)
            {
                window.Opacity = 0;
            }
        };
        _countdownFade.Start();
    }

    public void HideCountdown()
    {
        _countdownFade?.Stop();
        _countdown?.Close();
        _countdown = null;
    }

    public void ShowIndicator(RecordingTarget target)
    {
        if (_indicator is null)
        {
            _indicator = new RecordingIndicatorWindow();
            _indicator.View.PauseClicked += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
            _indicator.View.StopClicked += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
            _indicator.View.DiscardConfirmed += (_, _) => DiscardRequested?.Invoke(this, EventArgs.Empty);
        }

        _indicator.View.Update("0:00", paused: false);
        _indicator.ShowOn(target.Monitor);
    }

    public void UpdateIndicator(string elapsed, bool paused) => _indicator?.View.Update(elapsed, paused);

    public void HideIndicator()
    {
        _indicator?.Close();
        _indicator = null;
    }

    public void HideAll()
    {
        HideIndicator();
        HideGuide();
        HideCountdown();
    }

    /// <summary>A small modal alert with one button (spec: "Not enough space to record").</summary>
    public static async Task ShowAlertAsync(string title, string message)
    {
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = true,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            MaxWidth = 440,
        };
        var ok = new Button
        {
            Content = L.Get("ShelfPromiseDeliveryStrings.okButton"),
            IsDefault = true,
            IsCancel = true,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "accent" },
        };
        ok.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 392 },
                ok,
            },
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("WindowBackgroundBrush").ToBinding());
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        window.Activate();
        await closed.Task;
    }

    /// <summary>The message shown when a recording stopped on its own.</summary>
    public static string MessageFor(CaptureEndReason reason) => reason switch
    {
        CaptureEndReason.WindowClosed => L.Get("win.recording.windowClosed"),
        CaptureEndReason.DisplayChanged => L.Get("win.recording.displayChanged"),
        _ => L.Get("win.recording.captureLost"),
    };

    /// <summary>The message for an availability problem found before recording.</summary>
    public static string MessageFor(RecorderAvailability availability) => availability switch
    {
        RecorderAvailability.CaptureUnsupported => L.Get("win.recording.captureUnsupported"),
        RecorderAvailability.EncoderUnavailable => L.Get("win.recording.encoderMissing"),
        _ => L.Get("recorder.recordFailed"),
    };

    internal static HudStyle StyleFor(StopKind kind) => kind is StopKind.DiskAlmostFull or StopKind.CaptureEnded ? HudStyle.Warning : HudStyle.Success;
}
