// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Shell;
using Rivet.Core.Localization;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Platform;
using Rivet.Imaging.RadialMenu;
using PixelPoint = Rivet.Core.Platform.PixelPoint;

namespace Rivet.App.Features.RadialMenu;

/// <summary>
/// The 400×400 wheel window (spec 07 §3.2.10): transparent, borderless,
/// topmost, never activated and click-through (the session's mouse hook
/// takes clicks inside the square, so the app underneath never gets half a
/// click). Pre-built once and reused; a token guards against a stale fade
/// hiding a window a new session reclaimed.
/// </summary>
public sealed class RadialWheelWindow : Window
{
    private readonly RadialMenuService _service;
    private bool _chromeApplied;
    private int _token;

    public RadialWheelWindow(RadialMenuService service)
    {
        _service = service;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;
        Width = RadialGeometry.WindowSize;
        Height = RadialGeometry.WindowSize;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = L.Get("radialMenu.pageTitle");
        Wheel = new RadialWheelView(service.Presenter, RadialWheelSize.Wheel, RadialGeometry.WindowSize);
        Content = Wheel;
    }

    public RadialWheelView Wheel { get; }

    public bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>Shows the wheel centred on <paramref name="center"/> (physical pixels) and plays the open animation.</summary>
    public void ShowSession(PixelPoint center, double scale)
    {
        _token++;
        var half = (int)Math.Round(RadialGeometry.WindowSize / 2 * scale);
        Position = new Avalonia.PixelPoint(center.X - half, center.Y - half);
        Wheel.ReduceMotion = AppHostReduceMotion();
        Refresh();
        if (!IsVisible)
        {
            Show();
        }

        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate | WindowChromeOptions.ClickThrough | WindowChromeOptions.Topmost);
        }

        Wheel.PlayOpen();
    }

    /// <summary>Redraws the level, highlight and hub from the session.</summary>
    public void Refresh()
    {
        if (_service.Session is not { } session || _service.SessionProfile is not { } profile)
        {
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        Wheel.SetContent(session.CurrentItems, _service.Presenter.ColorValue(profile.Color, dark), dark, _service.NowPlaying, _service.NowPlayingSnapshot);
        var label = session.HighlightedItem is { } item ? _service.Presenter.Label(item, _service.NowPlaying, _service.NowPlayingSnapshot) : null;
        Wheel.SetHighlight(session.Highlight, label, session.SubmenuName);
    }

    /// <summary>Fades out, then hides (unless a new session took the window meanwhile).</summary>
    public void CloseSession()
    {
        var token = _token;
        Wheel.PlayClose(() =>
        {
            if (token == _token)
            {
                Hide();
            }
        });
    }

    private bool AppHostReduceMotion() =>
        Hosting.AppHost.Current?.Services.GetService<IThemeService>()?.ReduceMotion == true;
}

/// <summary>
/// The Now Playing card (spec 07 §3.2.11): 320 DIP wide, rounded 16, centred on
/// the wheel centre and clamped to the work area with 12 DIP margins; artwork
/// (or a music tile), title, album and artist, and "Open %@". A click brings
/// the player forward; it closes on any click outside or the next session.
/// </summary>
public sealed class NowPlayingCard : Window
{
    private readonly IServiceProvider _services;
    private readonly Image _art = new() { Width = 76, Height = 76, Stretch = Stretch.UniformToFill };
    private readonly Border _artTile;
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "caption" } };
    private readonly TextBlock _open = new() { FontSize = 11, Classes = { "caption" } };
    private NowPlayingSnapshot? _snapshot;
    private IDisposable? _outside;
    private bool _chromeApplied;

    public NowPlayingCard(IServiceProvider services)
    {
        _services = services;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 320 + 24;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        _artTile = new Border
        {
            Width = 76,
            Height = 76,
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)),
            Child = new Panel { Children = { new SymbolIcon { Symbol = Symbol.MusicNote2, FontSize = 30, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, _art } },
        };
        var texts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail } };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        top.Children.Add(_artTile);
        Grid.SetColumn(texts, 1);
        top.Children.Add(texts);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new SymbolIcon { Symbol = Symbol.Open, FontSize = 12 }, _open } };
        var card = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 6 20 0 #44000000"),
            Child = new StackPanel { Spacing = 10, Children = { top, footer } },
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("PanelBackgroundBrush"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("PanelBorderBrush"));
        card.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (_snapshot is { } snapshot && !_services.GetRequiredService<INowPlayingService>().Activate(snapshot))
            {
                _services.GetRequiredService<IRadialPlatform>().Beep();
            }

            Hide();
        };
        Content = card;
    }

    public void ShowAt(NowPlayingSnapshot snapshot, PixelPoint center, ScreenInfo screen)
    {
        _snapshot = snapshot;
        _title.Text = snapshot.DisplayTitle;
        _detail.Text = string.Join(" — ", new[] { snapshot.Artist, snapshot.Album }.Where(s => s.Length > 0));
        _detail.IsVisible = _detail.Text.Length > 0;
        _open.Text = L.Format("radialMenu.mediaOpenAppFormat", snapshot.AppName ?? L.Get("radialMenu.mediaNowPlaying"));
        (_art.Source as Bitmap)?.Dispose();
        _art.Source = null;
        if (snapshot.Artwork is { } art)
        {
            try
            {
                using var stream = new MemoryStream(art);
                _art.Source = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                _art.Source = null;
            }
        }

        var scale = screen.Scale <= 0 ? 1 : screen.Scale;
        Measure(Size.Infinity);
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Math.Max(DesiredSize.Height, 140) * scale);
        var margin = (int)Math.Round(12 * scale);
        var work = screen.WorkArea;
        var x = Math.Clamp(center.X - (width / 2), work.X + margin, Math.Max(work.X + margin, work.Right - width - margin));
        var y = Math.Clamp(center.Y - (height / 2), work.Y + margin, Math.Max(work.Y + margin, work.Bottom - height - margin));
        Position = new Avalonia.PixelPoint(x, y);
        Show();
        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate | WindowChromeOptions.Topmost);
        }

        // Any click outside closes it (and still reaches its target).
        _outside?.Dispose();
        _outside = _services.GetRequiredService<IInputHooks>().SubscribeMouse(OnMouse);
    }

    public new void Hide()
    {
        _outside?.Dispose();
        _outside = null;
        if (IsVisible)
        {
            base.Hide();
        }
    }

    private bool OnMouse(ref MouseHookEvent e)
    {
        if (e.Kind is not (MouseHookKind.LeftDown or MouseHookKind.RightDown or MouseHookKind.MiddleDown))
        {
            return false;
        }

        var p = e.Position;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var scale = RenderScaling;
            var inside = p.X >= Position.X && p.X <= Position.X + (Bounds.Width * scale) && p.Y >= Position.Y && p.Y <= Position.Y + (Bounds.Height * scale);
            if (!inside)
            {
                Hide();
            }
        });
        return false;
    }
}
