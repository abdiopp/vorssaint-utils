// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Styling;
using Rivet.Core.App;
using Rivet.Core.Settings;

namespace Rivet.App.Shell;

/// <summary>Applies the System/Light/Dark preference to every window, live.</summary>
public sealed class ThemeController
{
    private readonly ISettingsStore _settings;

    public ThemeController(ISettingsStore settings)
    {
        _settings = settings;
        settings.Observe(ShellSettings.Appearance.Key, Apply);
    }

    public void Apply()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = _settings.Get(ShellSettings.Appearance) switch
        {
            AppAppearance.Light => ThemeVariant.Light,
            AppAppearance.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
