// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.App.Shell;
using Rivet.Core.Localization;
using Rivet.Core.Modules.CleaningMode;
using Rivet.Core.Platform;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Features.CleaningMode;

/// <summary>The five unlock-progress dots.</summary>
public sealed class CleaningProgressDots : StackPanel
{
    private readonly List<Ellipse> _dots = [];
    private readonly IBrush _done;
    private readonly IBrush _pending;

    public CleaningProgressDots(double size, double spacing, IBrush done, IBrush pending)
    {
        Orientation = Orientation.Horizontal;
        Spacing = spacing;
        HorizontalAlignment = HorizontalAlignment.Center;
        _done = done;
        _pending = pending;
        for (var i = 0; i < CleaningModeConstants.UnlockThreshold; i++)
        {
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = pending,
                Transitions = [new Avalonia.Animation.BrushTransition { Property = Shape.FillProperty, Duration = TimeSpan.FromSeconds(0.15) }],
            };
            _dots.Add(dot);
            Children.Add(dot);
        }

        AutomationProperties.SetName(this, L.Get("Strings.cleaningOverlaySubtitle"));
    }

    public void SetProgress(int progress)
    {
        for (var i = 0; i < _dots.Count; i++)
        {
            _dots[i].Fill = i < progress ? _done : _pending;
        }
    }
}

/// <summary>
/// The black screen of one monitor (spec 07 §3.4.6): opaque, above everything,
/// absorbing every click and touch, with the unlock instructions, five dots,
/// the Unlock button and the note on what Windows never lets an app block.
/// </summary>
public sealed class CleaningOverlayWindow : Window
{
    private readonly CleaningProgressDots _dots;
    private bool _chromeApplied;

    public CleaningOverlayWindow(Action unlock)
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Black;
        Title = L.Get("Strings.cleaningMenuItem");
        _dots = new CleaningProgressDots(12, 11, Brushes.White, new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)));
        Content = BuildContent(unlock, _dots);
    }

    public PixelRect ScreenBounds { get; private set; }

    public void SetProgress(int progress) => _dots.SetProgress(progress);

    /// <summary>Covers <paramref name="bounds"/> (physical pixels) on a monitor with <paramref name="scale"/>.</summary>
    public void Cover(PixelRect bounds, double scale)
    {
        ScreenBounds = bounds;
        Position = new Avalonia.PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width / scale;
        Height = bounds.Height / scale;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost);
        }
    }

    internal static Control BuildContent(Action unlock, CleaningProgressDots dots)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = L.Get("Strings.cleaningOverlayUnlock"), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center },
            Background = Brushes.White,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(24, 9),
            MinWidth = 130,
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        button.Click += (_, _) => unlock();
        AutomationProperties.SetName(button, L.Get("Strings.cleaningOverlayUnlock"));

        var column = new StackPanel
        {
            Spacing = 18,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new SymbolIcon { Symbol = Symbol.Keyboard, FontSize = 48, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = L.Get("Strings.cleaningOverlayTitle"), FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = L.Get("win.cleaningMode.overlaySubtitle"), FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(191, 255, 255, 255)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
                dots,
                button,
                new TextBlock { Text = L.Get("win.cleaningMode.mouseHint"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
            },
        };

        var limits = new TextBlock
        {
            Text = L.Get("win.cleaningMode.limits"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24, 0, 24, 28),
            MaxWidth = 560,
        };

        return new Grid { Children = { new Border { Padding = new Thickness(44), Child = column }, limits } };
    }
}

/// <summary>
/// "Keep screen visible": a small card at the top-right of each monitor
/// (40 DIP from the top, 24 from the right) instead of a full-screen window,
/// so clicks reach apps normally while keys and the wheel stay blocked.
/// </summary>
public sealed class CleaningIndicatorWindow : Window
{
    private readonly CleaningProgressDots _dots;
    private bool _chromeApplied;

    public CleaningIndicatorWindow(Action unlock)
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = L.Get("Strings.cleaningMenuItem");
        var accent = Application.Current?.FindResource("AccentBrush") as IBrush ?? Brushes.DodgerBlue;
        var pending = Application.Current?.FindResource("TextTertiaryBrush") as IBrush ?? Brushes.Gray;
        _dots = new CleaningProgressDots(6, 4, accent, pending);
        Content = BuildContent(unlock, _dots, this);
    }

    public PixelRect ScreenBounds { get; private set; }

    public void SetProgress(int progress) => _dots.SetProgress(progress);

    /// <summary>Top-right of the work area: 40 DIP down, 24 DIP in.</summary>
    public void PlaceOn(PixelRect work, PixelRect bounds, double scale)
    {
        ScreenBounds = bounds;
        var width = (int)Math.Ceiling((Bounds.Width > 0 ? Bounds.Width : 300) * scale);
        Position = new Avalonia.PixelPoint(work.Right - width - (int)Math.Round(24 * scale), work.Y + (int)Math.Round(40 * scale));
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate | WindowChromeOptions.Topmost);
        }
    }

    internal static Control BuildContent(Action unlock, CleaningProgressDots dots, Control owner)
    {
        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Child = new SymbolIcon { Symbol = Symbol.Keyboard, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(Border.BackgroundProperty, owner.GetResourceObservable("AccentSoftBrush").ToBinding());

        var unlockButton = new Button
        {
            Content = L.Get("Strings.cleaningOverlayUnlock"),
            FontSize = 12,
            Padding = new Thickness(12, 4),
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
        };
        unlockButton.Click += (_, _) => unlock();

        var subtitle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = L.Get("win.cleaningMode.overlaySubtitle"), Classes = { "caption" }, FontSize = 11, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center },
                dots,
            },
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(12),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 4, Color = Color.FromArgb(70, 0, 0, 0) }),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 10,
                Children =
                {
                    icon,
                    WithColumn(new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = L.Get("Strings.cleaningOverlayTitle"), FontSize = 13, FontWeight = FontWeight.SemiBold },
                            subtitle,
                        },
                    }, 1),
                    WithColumn(unlockButton, 2),
                },
            },
        };
        card.Bind(Border.BackgroundProperty, owner.GetResourceObservable("HudBackgroundBrush").ToBinding());
        card.Bind(Border.BorderBrushProperty, owner.GetResourceObservable("PanelBorderBrush").ToBinding());
        return card;
    }

    private static Control WithColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
