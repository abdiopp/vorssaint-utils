// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Rivet.App.Features.Capture.Output;
using Rivet.App.Shell;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Rivet.Imaging.Capture;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Features.Capture.Pins;

/// <summary>
/// Pinned captures (spec 01 §3.11). Pins are content windows: they appear
/// in captures only while "Hide app windows" is off. While any pin ignores
/// clicks, a shared mouse hook watches for Alt+click inside it to make it
/// clickable again; the hook exists only while needed. Pins are not persisted.
/// </summary>
internal sealed class PinManager(IScreenService screens, IInputHooks hooks, CaptureOutputService output, IHud hud) : IDisposable
{
    private readonly List<PinWindow> _pins = [];
    private int _created;
    private IDisposable? _recoveryHook;
    private volatile PixelRect[] _clickThroughBounds = [];

    public IReadOnlyList<PinWindow> Pins => _pins;

    public IReadOnlyCollection<nint> Handles => _pins.Select(p => WindowInterop.Handle(p)).Where(h => h != 0).ToList();

    public PinWindow Pin(PixelBuffer image)
    {
        var display = screens.ScreenFromPoint(screens.CursorPosition);
        var work = display.WorkArea;
        var scale = display.Scale;
        var natural = new Size(image.Width / image.Scale, image.Height / image.Scale);

        // Natural size, at most 55 % of the work area's smaller side, at least 90 × 60.
        var limit = 0.55 * Math.Min(work.Width, work.Height) / scale;
        var fit = Math.Min(1, Math.Min(limit / natural.Width, limit / natural.Height));
        var width = natural.Width * fit;
        var height = natural.Height * fit;
        if (width < 90 || height < 60)
        {
            var grow = Math.Max(90 / width, 60 / height);
            width *= grow;
            height *= grow;
        }

        _created++;
        var cascade = ((_created - 1) % 5) * 26 * scale;
        var x = work.X + ((work.Width - (width * scale)) / 2) + cascade;
        var y = work.Y + ((work.Height - (height * scale)) / 2) + cascade;

        var pin = new PinWindow(image, natural) { Width = width, Height = height };
        pin.ContextMenu = BuildMenu(pin);
        pin.MenuCommand += (_, command) => _ = Run(pin, command);
        pin.Closed += (_, _) =>
        {
            _pins.Remove(pin);
            UpdateRecoveryHook();
        };
        pin.PositionChanged += (_, _) => RefreshBounds();
        pin.Resized += (_, _) => RefreshBounds();
        pin.Position = new Avalonia.PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        pin.Opened += (_, _) =>
        {
            // Content window: no taskbar button, above normal windows; not permanently excluded from capture.
            WindowInterop.ApplyChrome(pin, WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost);
            pin.Position = new Avalonia.PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        };
        _pins.Add(pin);
        pin.Show();
        return pin;
    }

    public void CloseAll()
    {
        foreach (var pin in _pins.ToList())
        {
            pin.Close();
        }
    }

    private ContextMenu BuildMenu(PinWindow pin)
    {
        var opacity = new MenuItem { Header = L.Get("screenshot.pinOpacity") };
        var opacityItems = new List<MenuItem>();
        foreach (var value in new[] { 1.0, 0.85, 0.7, 0.5 })
        {
            var item = new MenuItem
            {
                Header = $"{value * 100:0} %",
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "pinOpacity",
                IsChecked = Math.Abs(pin.PinOpacity - value) < 0.01,
            };
            item.Click += (_, _) =>
            {
                pin.PinOpacity = value;
                foreach (var other in opacityItems)
                {
                    other.IsChecked = ReferenceEquals(other, item);
                }
            };
            opacityItems.Add(item);
        }

        opacity.ItemsSource = opacityItems;

        var ignore = new MenuItem { Header = L.Get("screenshot.pinClickThrough"), ToggleType = MenuItemToggleType.CheckBox };
        ignore.Click += (_, _) =>
        {
            var enable = !pin.IgnoresClicks;
            pin.SetIgnoreClicks(enable);
            ignore.IsChecked = enable;
            UpdateRecoveryHook();
            if (enable)
            {
                hud.Show(L.Get("win.capture.pinClickThroughHint"), HudStyle.Info, "CursorClick");
            }
        };

        MenuItem Item(string key, string command, string? gesture = null)
        {
            var item = new MenuItem { Header = L.Get(key), InputGesture = gesture is null ? null : Avalonia.Input.KeyGesture.Parse(gesture) };
            item.Click += (_, _) => _ = Run(pin, command);
            return item;
        }

        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("screenshot.copyButton", "copy", "Ctrl+C"),
                Item("screenshot.saveAsButton", "saveAs", "Ctrl+S"),
                new Separator(),
                Item("win.capture.pinActualSize", "actualSize", "Ctrl+D0"),
                opacity,
                ignore,
                new Separator(),
                Item("Strings.menuClose", "close", "Escape"),
                Item("screenshot.pinCloseAll", "closeAll"),
            },
        };
    }

    private async Task Run(PinWindow pin, string command)
    {
        switch (command)
        {
            case "copy":
                if (await output.CopyAsync(pin.Image, applyDownscale: true))
                {
                    hud.Show(L.Get("screenshot.copiedHUD"), HudStyle.Success, "Copy");
                }

                break;
            case "saveAs":
                await SaveAsAsync(pin);
                break;
            case "actualSize":
                pin.ActualSize();
                break;
            case "close":
                pin.Close();
                break;
            case "closeAll":
                CloseAll();
                break;
        }
    }

    private static async Task SaveAsAsync(PinWindow pin)
    {
        try
        {
            var file = await pin.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = CaptureOutputService.DefaultName(),
                DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
                ShowOverwritePrompt = true,
            });
            if (file is null)
            {
                return;
            }

            var png = await Task.Run(() => CaptureImaging.EncodePng(pin.Image));
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("capture", "Saving the pinned capture failed.", ex);
        }
    }

    /// <summary>Alt+click inside a click-through pin turns click-through off again.</summary>
    private void UpdateRecoveryHook()
    {
        RefreshBounds();
        var needed = _pins.Any(p => p.IgnoresClicks);
        if (needed && _recoveryHook is null)
        {
            _recoveryHook = hooks.SubscribeMouse(OnMouse, priority: 50);
        }
        else if (!needed && _recoveryHook is not null)
        {
            _recoveryHook.Dispose();
            _recoveryHook = null;
        }
    }

    /// <summary>Snapshot of click-through pin bounds for the hook thread (Avalonia objects are UI-thread only).</summary>
    private void RefreshBounds() =>
        _clickThroughBounds = _pins.Where(p => p.IgnoresClicks).Select(BoundsOf).ToArray();

    private static PixelRect BoundsOf(PinWindow pin) =>
        new(pin.Position.X, pin.Position.Y, (int)Math.Round(pin.Bounds.Width * pin.RenderScaling), (int)Math.Round(pin.Bounds.Height * pin.RenderScaling));

    /// <summary>Runs on the hook thread: decide from the snapshot, recover on the UI thread.</summary>
    private bool OnMouse(ref MouseHookEvent e)
    {
        if (e.Kind != MouseHookKind.LeftDown || !e.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            return false;
        }

        var point = e.Position;
        if (!_clickThroughBounds.Any(b => b.Contains(point)))
        {
            return false;
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var pin in _pins.Where(p => p.IgnoresClicks))
            {
                if (BoundsOf(pin).Contains(point))
                {
                    pin.SetIgnoreClicks(false);
                    if (pin.ContextMenu?.ItemsSource is object[] items)
                    {
                        foreach (var item in items.OfType<MenuItem>().Where(i => i.ToggleType == MenuItemToggleType.CheckBox))
                        {
                            item.IsChecked = false;
                        }
                    }

                    break;
                }
            }

            UpdateRecoveryHook();
        });
        return true;
    }

    public void Dispose()
    {
        _recoveryHook?.Dispose();
        _recoveryHook = null;
    }
}
