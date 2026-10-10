// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.App;

public enum AppAppearance
{
    System,
    Light,
    Dark,
}

/// <summary>Preferences owned by the app shell (keys match the macOS app where shared).</summary>
public static class ShellSettings
{
    /// <summary>UI language code; empty means "follow Windows" (never written until the user picks).</summary>
    public static readonly Setting<string> AppLanguage = new("appLanguage", string.Empty);

    public static readonly Setting<AppAppearance> Appearance = new("appAppearance", AppAppearance.System);

    /// <summary>Use Mica/Acrylic backdrops where Windows supports them.</summary>
    public static readonly Setting<bool> TranslucencyEnabled = new("translucencyEnabled", true);

    public static readonly Setting<bool> HasOnboarded = new("hasOnboarded", false);

    public static readonly Setting<int> OnboardingStep = new("onboardingStep", 0, Sanitize.Clamp(0, 10));

    public static readonly Setting<string> FeatureHubKeptFeatures = new("featureHubKeptFeatures", string.Empty);

    public static readonly Setting<bool> LaunchAtLoginWanted = new("launchAtLoginWanted", true);

    public static readonly Setting<bool> AutoCheckUpdates = new("autoCheckUpdates", true);

    public static readonly Setting<bool> IncludeBetaUpdates = new("includeBetaUpdates", false);

    public static readonly Setting<string> UpdateLastInstallFailure = new("updateLastInstallFailure", string.Empty, machineState: true);

    public static readonly Setting<long> UpdateLastCheckUnix = new("updateLastCheckUnix", 0L, machineState: true);

    public static readonly Setting<double> SettingsWindowWidth = new("settingsWindowWidth", 0d, machineState: true);

    public static readonly Setting<double> SettingsWindowHeight = new("settingsWindowHeight", 0d, machineState: true);

    /// <summary>Set while starting up; still set at the next launch means the last start crashed.</summary>
    public static readonly Setting<bool> StartupDidNotFinish = new("startupDidNotFinish", false, machineState: true);

    public static readonly Setting<string> LastUpdateIntroVersion = new("lastUpdateIntroVersion", string.Empty);

    /// <summary>Comma-separated panel section ids, in order (unknown ids ignored, missing ones inserted).</summary>
    public static readonly Setting<string> PanelSectionOrder = new("panelSectionOrder", string.Empty);

    /// <summary>Comma-separated utility tile ids, in order.</summary>
    public static readonly Setting<string> PanelUtilityOrder = new("panelUtilityOrder", string.Empty);

    /// <summary>Comma-separated panel ids the user hid (sections and tiles share the list).</summary>
    public static readonly Setting<string> PanelHiddenItems = new("panelHiddenItems", string.Empty);

    /// <summary>Tray glyph: empty for the product mark, else a Fluent icon name.</summary>
    public static readonly Setting<string> TrayIconSymbol = new("trayIconSymbol", string.Empty);

    /// <summary>Right-clicking the tray icon toggles Keep Awake instead of opening the menu.</summary>
    public static readonly Setting<bool> KeepAwakeRightClickToggle = new("keepAwakeRightClickToggle", false);

    /// <summary>Whether the first-run "pin the tray icon" guidance was shown.</summary>
    public static readonly Setting<bool> TrayPinGuideShown = new("trayPinGuideShown", false);
}
