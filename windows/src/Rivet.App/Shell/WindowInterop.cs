// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.Core.Platform;

namespace Rivet.App.Shell;

/// <summary>Native handle access and screen geometry helpers for app windows.</summary>
public static class WindowInterop
{
    public static nint Handle(TopLevel window) => window.TryGetPlatformHandle()?.Handle ?? 0;

    /// <summary>Applies native window tweaks (no-op on the development platform).</summary>
    public static void ApplyChrome(TopLevel window, WindowChromeOptions options)
    {
        var handle = Handle(window);
        if (handle != 0)
        {
            AppHost.Current?.Services.GetService<IWindowChrome>()?.Apply(handle, options);
        }
    }

    public static void SetBackdrop(TopLevel window, WindowBackdrop backdrop, bool dark)
    {
        var handle = Handle(window);
        if (handle != 0)
        {
            AppHost.Current?.Services.GetService<IWindowChrome>()?.SetBackdrop(handle, backdrop, dark);
        }
    }

    public static void SetRoundedCorners(TopLevel window)
    {
        var handle = Handle(window);
        if (handle != 0)
        {
            AppHost.Current?.Services.GetService<IWindowChrome>()?.SetRoundedCorners(handle, true);
        }
    }

    /// <summary>The Avalonia screen containing <paramref name="point"/> (physical pixels), or the primary one.</summary>
    public static Screen? ScreenAt(WindowBase window, Avalonia.PixelPoint point) =>
        window.Screens.ScreenFromPoint(point) ?? window.Screens.Primary ?? window.Screens.All.FirstOrDefault();

    /// <summary>The screen under the mouse pointer.</summary>
    public static Screen? ScreenAtCursor(WindowBase window)
    {
        var cursor = AppHost.Current?.Services.GetService<IScreenService>()?.CursorPosition ?? default;
        return ScreenAt(window, new Avalonia.PixelPoint(cursor.X, cursor.Y));
    }
}
