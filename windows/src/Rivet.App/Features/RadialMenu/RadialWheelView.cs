// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Imaging.RadialMenu;

namespace Rivet.App.Features.RadialMenu;

/// <summary>
/// A drawn wheel (spec 07 §3.2.10): the disc, wedge, guides and chips come from
/// <see cref="RadialWheelRenderer"/> (the same Skia code as the snapshots),
/// with the chip icons and the hub face laid over it as Avalonia controls.
/// Motion: open fade/scale with the chip bloom, springs for the wedge and the
/// highlighted chip, and a short fold on close; all instant with Reduce Motion.
/// Used at full size by the wheel window and at canvas size in Settings.
/// </summary>
public sealed class RadialWheelView : Grid
{
    private readonly SkiaView _skia = new();
    private readonly Canvas _icons = new() { IsHitTestVisible = false };
    private readonly Border _hub;
    private readonly ContentControl _hubContent = new() { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RadialPresenter _presenter;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
    private readonly List<Control> _iconControls = [];
    private IReadOnlyList<RadialItem> _items = [];
    private RadialWheelSize _size = RadialWheelSize.Wheel;
    private double _side;
    private uint _color = 0xFF0067C0;
    private bool _dark;
    private int? _highlight;
    private double _open = 1;
    private double _openTarget = 1;
    private double _wedgeAngle;
    private double _wedgeVelocity;
    private double _wedgeTarget;
    private double _wedgeOpacity;
    private double[] _scales = [];
    private double[] _scaleVelocity = [];
    private double[] _bloom = [];
    private DateTime _bloomStart = DateTime.MinValue;
    private DateTime _closeStart = DateTime.MinValue;
    private Action? _closed;
    private DateTime _lastTick = DateTime.UtcNow;

    public RadialWheelView(RadialPresenter presenter, RadialWheelSize size, double side)
    {
        _presenter = presenter;
        _size = size;
        _side = side;
        Width = side;
        Height = side;
        _skia.Draw += (_, e) => RadialWheelRenderer.Draw(e.Canvas, (float)(e.Size.Width / 2), (float)(e.Size.Height / 2), Frame());
        _hub = new Border
        {
            Width = size.Hub - 6,
            Height = size.Hub - 6,
            CornerRadius = new CornerRadius(size.Hub / 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _hubContent,
            IsHitTestVisible = false,
        };
        Children.Add(_skia);
        Children.Add(_icons);
        Children.Add(_hub);
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Disables motion (Reduce Motion, snapshots).</summary>
    public bool ReduceMotion { get; set; }

    public IReadOnlyList<RadialItem> Items => _items;

    public double Side => _side;

    public RadialWheelSize WheelSize => _size;

    public void SetContent(IReadOnlyList<RadialItem> items, uint color, bool dark, NowPlayingState nowPlaying = NowPlayingState.NothingPlaying, NowPlayingSnapshot? snapshot = null)
    {
        _items = items;
        _color = color;
        _dark = dark;
        if (_scales.Length != items.Count)
        {
            _scales = Enumerable.Repeat(1.0, items.Count).ToArray();
            _scaleVelocity = new double[items.Count];
            _bloom = Enumerable.Repeat(1.0, items.Count).ToArray();
        }

        BuildIcons(nowPlaying, snapshot);
        _skia.InvalidateVisual();
    }

    /// <summary>Highlight and hub: the highlighted name, a back chevron over the submenu name, or the brand mark.</summary>
    public void SetHighlight(int? index, string? hubLabel, string? submenuName)
    {
        var changed = index != _highlight;
        _highlight = index;
        if (index is { } i && _items.Count > 0)
        {
            var target = RadialGeometry.SliceAngle(i, _items.Count);
            _wedgeTarget = _wedgeOpacity <= 0.01 ? target : RadialGeometry.WedgeTarget(_wedgeTarget, target);
            if (_wedgeOpacity <= 0.01)
            {
                _wedgeAngle = _wedgeTarget;
                _wedgeVelocity = 0;
            }
        }

        _hubContent.Content = HubFace(index is not null ? hubLabel : null, submenuName);
        UpdateIconColors();
        if (changed)
        {
            Animate();
        }
    }

    /// <summary>Starts the open animation (fade 0.18 s, scale 0.9 → 1, chips bloom clockwise within 0.17 s).</summary>
    public void PlayOpen()
    {
        _closed = null;
        _openTarget = 1;
        if (ReduceMotion)
        {
            _open = 1;
            Array.Fill(_bloom, 1.0);
            _skia.InvalidateVisual();
            return;
        }

        _open = 0;
        Array.Fill(_bloom, 0.0);
        _bloomStart = DateTime.UtcNow;
        Animate();
    }

    /// <summary>Fades and folds (0.13 s), then calls <paramref name="done"/>.</summary>
    public void PlayClose(Action done)
    {
        if (ReduceMotion)
        {
            _open = 0;
            done();
            return;
        }

        _openTarget = 0;
        _closeStart = DateTime.UtcNow;
        _closed = done;
        Animate();
    }

    /// <summary>Centre of chip i inside this view (DIPs).</summary>
    public Point ChipCenter(int index)
    {
        var (x, y) = RadialWheelRenderer.ChipCenter(index, _items.Count, _size.Ring, _side / 2, _side / 2);
        return new Point(x, y);
    }

    private RadialWheelFrame Frame() => new()
    {
        Count = _items.Count,
        Highlight = _highlight,
        WedgeAngle = _wedgeAngle,
        WedgeOpacity = _wedgeOpacity,
        Open = _open,
        Bloom = _bloom,
        ChipScale = _scales,
        Color = _color,
        Dark = _dark,
        Size = _size,
    };

    private void Animate()
    {
        if (!_timer.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            _timer.Start();
        }
    }

    /// <summary>One animation step; stops when everything settled.</summary>
    internal void Tick()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _lastTick).TotalSeconds, 0, 0.05);
        _lastTick = now;
        var busy = false;

        if (ReduceMotion)
        {
            _wedgeAngle = _wedgeTarget;
            _wedgeOpacity = _highlight is null ? 0 : 1;
            for (var i = 0; i < _scales.Length; i++)
            {
                _scales[i] = _highlight == i ? RadialGeometry.HighlightScale : 1;
            }
        }
        else
        {
            // Open / close.
            if (_openTarget > _open)
            {
                _open = Math.Min(1, _open + (dt / 0.18));
                busy |= _open < 1;
                var elapsed = (now - _bloomStart).TotalSeconds;
                for (var i = 0; i < _bloom.Length; i++)
                {
                    var local = Math.Clamp((elapsed - RadialGeometry.BloomDelay(i, _bloom.Length)) / 0.22, 0, 1);
                    _bloom[i] = 1 - Math.Pow(1 - local, 3);
                    busy |= local < 1;
                }
            }
            else if (_openTarget < _open)
            {
                _open = Math.Max(0, _open - (dt / 0.13));
                for (var i = 0; i < _bloom.Length; i++)
                {
                    _bloom[i] = Math.Max(0.3, _open);
                }

                busy |= _open > 0;
            }
            else
            {
                for (var i = 0; i < _bloom.Length; i++)
                {
                    if (_bloom[i] < 1 && _openTarget >= 1)
                    {
                        _bloom[i] = Math.Min(1, _bloom[i] + (dt / 0.22));
                        busy = true;
                    }
                }
            }

            // Wedge spring and opacity.
            (_wedgeAngle, _wedgeVelocity) = RadialSpring.Wedge.Step(_wedgeAngle, _wedgeVelocity, _wedgeTarget, dt);
            busy |= Math.Abs(_wedgeAngle - _wedgeTarget) > 0.001 || Math.Abs(_wedgeVelocity) > 0.01;
            var opacityTarget = _highlight is null ? 0 : 1;
            _wedgeOpacity += Math.Sign(opacityTarget - _wedgeOpacity) * Math.Min(Math.Abs(opacityTarget - _wedgeOpacity), dt / 0.12);
            busy |= Math.Abs(_wedgeOpacity - opacityTarget) > 0.001;

            // Highlighted chip spring.
            for (var i = 0; i < _scales.Length; i++)
            {
                var target = _highlight == i ? RadialGeometry.HighlightScale : 1;
                (_scales[i], _scaleVelocity[i]) = RadialSpring.Highlight.Step(_scales[i], _scaleVelocity[i], target, dt);
                busy |= Math.Abs(_scales[i] - target) > 0.001 || Math.Abs(_scaleVelocity[i]) > 0.01;
            }
        }

        LayoutIcons();
        _skia.InvalidateVisual();
        Opacity = 1;
        if (!busy)
        {
            _timer.Stop();
            if (_openTarget <= 0 && _closed is { } done)
            {
                _closed = null;
                done();
            }
        }
    }

    /// <summary>Settles every animation at once (tests and snapshots).</summary>
    public void Settle()
    {
        _open = _openTarget;
        Array.Fill(_bloom, 1.0);
        _wedgeAngle = _wedgeTarget;
        _wedgeVelocity = 0;
        _wedgeOpacity = _highlight is null ? 0 : 1;
        for (var i = 0; i < _scales.Length; i++)
        {
            _scales[i] = _highlight == i ? RadialGeometry.HighlightScale : 1;
            _scaleVelocity[i] = 0;
        }

        _timer.Stop();
        LayoutIcons();
        _skia.InvalidateVisual();
        if (_openTarget <= 0 && _closed is { } done)
        {
            _closed = null;
            done();
        }
    }

    // ── Icons and hub ───────────────────────────────────────────────────

    private void BuildIcons(NowPlayingState nowPlaying, NowPlayingSnapshot? snapshot)
    {
        _icons.Children.Clear();
        _iconControls.Clear();
        var imageSize = _size == RadialWheelSize.Wheel ? 34.0 : 28.0;
        var symbolSize = _size == RadialWheelSize.Wheel ? 20.0 : 17.0;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.5;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            Control icon;
            if (item.Kind == RadialItemKind.Media && item.Payload == RadialMediaIds.NowPlaying)
            {
                icon = Symbol("MusicNote2", symbolSize);
            }
            else if (_presenter.Image(item, (int)Math.Round(imageSize * scaling)) is { } bitmap)
            {
                icon = new Image { Source = bitmap, Width = imageSize, Height = imageSize, Stretch = Stretch.Uniform };
            }
            else
            {
                icon = Symbol(_presenter.Symbol(item), symbolSize);
            }

            _iconControls.Add(icon);
            _icons.Children.Add(icon);
        }

        UpdateIconColors();
        LayoutIcons();
    }

    private static SymbolIcon Symbol(string name, double size) => new()
    {
        Symbol = IconConverter.Parse(name),
        FontSize = size,
        Width = size + 4,
        Height = size + 4,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void UpdateIconColors()
    {
        for (var i = 0; i < _iconControls.Count; i++)
        {
            if (_iconControls[i] is SymbolIcon symbol)
            {
                symbol.Foreground = _highlight == i
                    ? Brushes.White
                    : new SolidColorBrush(_dark ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(220, 28, 28, 30));
            }
        }
    }

    private void LayoutIcons()
    {
        var count = _iconControls.Count;
        var scale = 0.9 + (0.1 * _open);
        for (var i = 0; i < count; i++)
        {
            var control = _iconControls[i];
            var bloom = i < _bloom.Length ? Math.Min(1, _bloom[i]) : 1;
            var (x, y) = RadialWheelRenderer.ChipCenter(i, count, (_size.Ring * bloom) + (8 * (1 - bloom)), 0, 0);
            var w = control.Width;
            var h = control.Height;
            Canvas.SetLeft(control, (_side / 2) + (x * scale) - (w / 2));
            Canvas.SetTop(control, (_side / 2) + (y * scale) - (h / 2));
            var chip = i < _scales.Length ? _scales[i] : 1;
            control.RenderTransform = new ScaleTransform(chip * scale, chip * scale);
            control.Opacity = bloom * _open;
        }

        _hub.Opacity = _open;
    }

    private Control HubFace(string? label, string? submenuName)
    {
        var foreground = new SolidColorBrush(_dark ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(230, 20, 20, 22));
        var fontSize = _size == RadialWheelSize.Wheel ? 11.0 : 10.0;
        if (label is not null)
        {
            return new TextBlock
            {
                Text = label,
                FontSize = fontSize,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 3,
                MaxWidth = _hub.Width - 8,
                Foreground = foreground,
            };
        }

        if (submenuName is not null)
        {
            return new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new SymbolIcon { Symbol = FluentIcons.Common.Symbol.ChevronLeft, FontSize = 13, Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock
                    {
                        Text = submenuName.Length == 0 ? L.Get("radialMenu.kindSubmenu") : submenuName,
                        FontSize = fontSize,
                        FontWeight = FontWeight.SemiBold,
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 2,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = _hub.Width - 8,
                        Foreground = foreground,
                    },
                },
            };
        }

        return new SymbolIcon { Symbol = IconConverter.Parse("CircleMultipleSubtractCheckmark"), FontSize = _size == RadialWheelSize.Wheel ? 24 : 20, Foreground = new SolidColorBrush(_color.ToColor()) };
    }
}

internal static class RadialColorExtensions
{
    public static Color ToColor(this uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
