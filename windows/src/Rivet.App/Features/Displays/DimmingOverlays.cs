// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Rivet.App.Shell;
using Rivet.Core.Diagnostics;
using Rivet.Core.Displays;
using Rivet.Core.Platform;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Features.Displays;

/// <summary>
/// Software dimming: one black, click-through, capture-excluded, top-most
/// overlay per display whose alpha darkens the picture. Gamma ramps are not
/// used because Windows clamps them near identity and ignores them in HDR.
/// Overlays live only in this process: quitting (or a crash) removes them.
/// </summary>
internal sealed class DimmingOverlays : ISoftwareDimmer, IDisposable
{
    private readonly IScreenService? _screens;
    private readonly Dictionary<string, OverlayWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _factors = new(StringComparer.OrdinalIgnoreCase);

    public DimmingOverlays(IScreenService? screens) => _screens = screens;

    /// <summary>
    /// Real overlays need click-through windows, which only the Windows build
    /// has; development builds record the factor and draw nothing.
    /// </summary>
    public static bool DrawsWindows => OperatingSystem.IsWindows();

    /// <summary>The picture factor per display (1 = undimmed displays are absent).</summary>
    public IReadOnlyDictionary<string, double> Factors => _factors;

    public void Apply(DisplayDevice display, double factor)
    {
        _factors[display.Id] = factor;
        if (!DrawsWindows)
        {
            Log.Debug("brightness", $"[dev] dim {display.Id} to {factor:0.00}");
            return;
        }

        var screen = _screens?.Screens.FirstOrDefault(s => string.Equals(s.Id, display.Id, StringComparison.OrdinalIgnoreCase));
        var bounds = screen?.Bounds ?? display.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (!_windows.TryGetValue(display.Id, out var window))
        {
            window = new OverlayWindow();
            _windows[display.Id] = window;
        }

        window.Cover(bounds, screen?.Scale ?? 1, BrightnessMath.OverlayAlpha(factor));
    }

    public void Remove(string displayId)
    {
        _factors.Remove(displayId);
        if (_windows.Remove(displayId, out var window))
        {
            Close(window);
        }
    }

    public void RemoveAll()
    {
        _factors.Clear();
        foreach (var window in _windows.Values)
        {
            Close(window);
        }

        _windows.Clear();
    }

    private static void Close(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", "Could not close a dimming overlay.", ex);
        }
    }

    public void Dispose() => RemoveAll();

    private sealed class OverlayWindow : Window
    {
        private readonly Border _shade = new() { Background = Brushes.Black };
        private PixelRect _bounds;

        public OverlayWindow()
        {
            Title = "Dimming";
            WindowDecorations = WindowDecorations.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            CanResize = false;
            Focusable = false;
            IsHitTestVisible = false;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Content = _shade;
            Opened += (_, _) =>
            {
                WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate
                    | WindowChromeOptions.ClickThrough | WindowChromeOptions.ExcludeFromCapture | WindowChromeOptions.Topmost);
                Fit();
            };
            ScalingChanged += (_, _) => Fit();
        }

        public void Cover(PixelRect bounds, double scale, double alpha)
        {
            _bounds = bounds;
            _shade.Opacity = alpha;
            if (IsVisible)
            {
                Fit();
                return;
            }

            Position = new Avalonia.PixelPoint(bounds.X, bounds.Y);
            Width = bounds.Width / Math.Max(scale, 0.5);
            Height = bounds.Height / Math.Max(scale, 0.5);
            Show();
        }

        /// <summary>Covers the monitor exactly, in the scaling of the monitor the window is on.</summary>
        private void Fit()
        {
            var scaling = DesktopScaling > 0 ? DesktopScaling : 1;
            Position = new Avalonia.PixelPoint(_bounds.X, _bounds.Y);
            Width = _bounds.Width / scaling;
            Height = _bounds.Height / scaling;
        }
    }
}
