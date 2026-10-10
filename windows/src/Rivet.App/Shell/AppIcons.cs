// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Rivet.App.Shell;

/// <summary>The app icon for windows and image views (Assets/app-icon.png).</summary>
public static class AppIcons
{
    private static readonly Uri IconUri = new($"avares://{typeof(AppIcons).Assembly.GetName().Name}/Assets/app-icon.png");

    public static WindowIcon? Window
    {
        get
        {
            try
            {
                using var stream = AssetLoader.Open(IconUri);
                return new WindowIcon(stream);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    public static Bitmap? Image
    {
        get
        {
            try
            {
                using var stream = AssetLoader.Open(IconUri);
                return new Bitmap(stream);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
