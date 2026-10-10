// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.App.Settings;

/// <summary>Ids of the built-in pages (persisted in deep links; never rename).</summary>
public static class SettingsPageIds
{
    public const string General = "general";
    public const string TrayPanel = "trayPanel";
    public const string Features = "features";
    public const string Shortcuts = "shortcuts";
    public const string Advanced = "advanced";
    public const string About = "about";
}

/// <summary>Hand-off between navigation requests and pages (e.g. which hub row to reveal).</summary>
public sealed class SettingsNavigationState
{
    /// <summary>A feature the Features page should scroll to and highlight once, then clear.</summary>
    public string? PendingRevealFeature { get; set; }

    public string? TakeReveal()
    {
        var value = PendingRevealFeature;
        PendingRevealFeature = null;
        return value;
    }
}
