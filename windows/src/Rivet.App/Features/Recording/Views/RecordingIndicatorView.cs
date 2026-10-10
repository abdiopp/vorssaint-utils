// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Recording.Views;

/// <summary>
/// The recording pill (spec 02 §3.14): a dark capsule with a pulsing red dot,
/// the elapsed time, and pause/resume, stop and discard buttons. It is always
/// dark (it floats over arbitrary content, like the macOS pill). Discard asks
/// for a second click instead of a dialog, so nothing steals focus or appears
/// over the recorded app.
/// </summary>
public sealed class RecordingIndicatorView : UserControl
{
    public const double PillHeight = 32;
    public const double PillWidth = 184;

    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(240, 31, 31, 31));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
    private static readonly IBrush Divider = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
    private static readonly IBrush Hover = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
    private static readonly IBrush Pressed = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
    private static readonly IBrush RecordRed = new SolidColorBrush(Color.FromRgb(255, 69, 58));
    private static readonly IBrush ConfirmFill = new SolidColorBrush(Color.FromArgb(220, 196, 43, 28));

    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Fill = RecordRed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _elapsed = new()
    {
        Text = "0:00",
        Width = 50,
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontFeatures = FontFeatureCollection.Parse("+tnum"),
    };

    private readonly Button _pause;
    private readonly SymbolIcon _pauseIcon = new() { Symbol = Symbol.Pause, FontSize = 13, Foreground = Brushes.White };
    private readonly Button _stop;
    private readonly Button _discard;
    private readonly SymbolIcon _discardIcon = new() { Symbol = Symbol.Delete, FontSize = 13, Foreground = Brushes.White };
    private CancellationTokenSource? _pulse;
    private DispatcherTimer? _confirmTimer;
    private bool _paused;
    private bool _confirmingDiscard;

    public RecordingIndicatorView()
    {
        _pause = MakeButton(_pauseIcon, L.Get("recorder.pauseButton"), () => PauseClicked?.Invoke(this, EventArgs.Empty));
        _stop = MakeButton(new SymbolIcon { Symbol = Symbol.Stop, FontSize = 13, Foreground = RecordRed, IconVariant = IconVariant.Filled }, L.Get("recorder.stopButton"), () => StopClicked?.Invoke(this, EventArgs.Empty));
        _discard = MakeButton(_discardIcon, L.Get("win.recording.discard"), OnDiscardClicked);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = 12 },
                _dot,
                new Border { Width = 5 },
                _elapsed,
                new Border { Width = 3 },
                new Border { Width = 1, Height = PillHeight - 16, Background = Divider, VerticalAlignment = VerticalAlignment.Center },
                new Border { Width = 3 },
                _pause,
                new Border { Width = 4 },
                _stop,
                new Border { Width = 4 },
                _discard,
            },
        };

        var capsule = new Border
        {
            Width = PillWidth,
            Height = PillHeight,
            CornerRadius = new CornerRadius(PillHeight / 2),
            Background = Fill,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, OffsetY = 3, Color = Color.FromArgb(90, 0, 0, 0) }),
            Child = row,
        };
        ToolTip.SetTip(capsule, L.Get("recorder.indicatorTooltip"));
        AutomationProperties.SetName(capsule, L.Get("recorder.indicatorTooltip"));

        // Room for the shadow.
        Content = new Border { Padding = new Thickness(8, 4, 8, 10), Background = Brushes.Transparent, Child = capsule };
    }

    public event EventHandler? PauseClicked;

    public event EventHandler? StopClicked;

    public event EventHandler? DiscardConfirmed;

    public string ElapsedText => _elapsed.Text ?? string.Empty;

    public bool IsPaused => _paused;

    public bool IsConfirmingDiscard => _confirmingDiscard;

    public void Update(string elapsed, bool paused)
    {
        _elapsed.Text = elapsed;
        if (paused == _paused)
        {
            return;
        }

        _paused = paused;
        _pauseIcon.Symbol = paused ? Symbol.Play : Symbol.Pause;
        var tip = L.Get(paused ? "recorder.resumeButton" : "recorder.pauseButton");
        ToolTip.SetTip(_pause, tip);
        AutomationProperties.SetName(_pause, tip);
        UpdatePulse();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdatePulse();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pulse?.Cancel();
        _pulse = null;
        _confirmTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The dot pulses (1 → 0.28, 0.85 s, back and forth) while recording; paused, it rests at 40 %.</summary>
    private void UpdatePulse()
    {
        _pulse?.Cancel();
        _pulse = null;
        if (_paused || VisualRoot is null)
        {
            _dot.Opacity = _paused ? 0.4 : 1;
            return;
        }

        _pulse = new CancellationTokenSource();
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(0.85),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.28) } },
            },
        };
        _ = animation.RunAsync(_dot, _pulse.Token);
    }

    private void OnDiscardClicked()
    {
        if (_confirmingDiscard)
        {
            ResetDiscard();
            DiscardConfirmed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _confirmingDiscard = true;
        _discard.Background = ConfirmFill;
        _discardIcon.Symbol = Symbol.Checkmark;
        var tip = L.Get("win.recording.discardConfirm");
        ToolTip.SetTip(_discard, tip);
        AutomationProperties.SetName(_discard, tip);
        ToolTip.SetIsOpen(_discard, true);
        _confirmTimer?.Stop();
        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _confirmTimer.Tick += (_, _) => ResetDiscard();
        _confirmTimer.Start();
    }

    private void ResetDiscard()
    {
        _confirmTimer?.Stop();
        _confirmingDiscard = false;
        _discard.Background = Brushes.Transparent;
        _discardIcon.Symbol = Symbol.Delete;
        var tip = L.Get("win.recording.discard");
        ToolTip.SetIsOpen(_discard, false);
        ToolTip.SetTip(_discard, tip);
        AutomationProperties.SetName(_discard, tip);
    }

    private static Button MakeButton(Control icon, string tip, Action click)
    {
        var button = new Button
        {
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            Content = icon,
        };
        button.Styles.Add(new Style(x => x.OfType<Button>().Class(":pointerover").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Hover) },
        });
        button.Styles.Add(new Style(x => x.OfType<Button>().Class(":pressed").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Pressed) },
        });
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }
}
