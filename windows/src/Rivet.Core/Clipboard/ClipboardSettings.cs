// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Clipboard;

/// <summary>
/// Clipboard history, auto clear, paste as plain text and Clean URL settings.
/// Keys match the macOS app (spec 06 §4); shortcuts use the Windows chord format.
/// </summary>
public static class ClipboardSettings
{
    /// <summary>The values the limit picker offers; 0 means unlimited.</summary>
    public static readonly int[] LimitChoices = [20, 50, 100, 250, 500, 1000, 10000, 0];

    public static Setting<bool> Enabled => FeatureKeys.ClipboardHistoryEnabled;

    public static readonly Setting<int> Limit = new("clipboardHistoryLimit", 50, v => LimitChoices.Contains(v) ? v : 50);

    public static readonly Setting<bool> SkipSensitive = new("clipboardHistorySkipSensitive", true);

    public static readonly Setting<bool> IncludeImagesFiles = new("clipboardHistoryIncludeImagesFiles", true);

    /// <summary>App identities (lower-case executable paths or AppUserModelIDs) never recorded.</summary>
    public static readonly Setting<List<string>> IgnoredApps = new("clipboardHistoryIgnoredApps", [], SanitizeApps);

    public static readonly Setting<bool> QuickPreview = new("clipboardHistoryQuickPreview", false);

    public static readonly Setting<bool> ShortcutEnabled = new("clipboardHistoryShortcutEnabled", true);

    public static readonly Setting<string> Shortcut = new("clipboardHistoryShortcut", string.Empty);

    public static readonly KeyChord DefaultShortcut = new(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, 'V');

    // ── Auto clear ─────────────────────────────────────────────────────
    public static readonly Setting<bool> AutoClearOnDelay = new("clipboardAutoClearOnDelay", false);

    /// <summary>Seconds of stillness before clearing; a typed 4 becomes 5, not the default.</summary>
    public static readonly Setting<int> AutoClearDelaySeconds = new("clipboardAutoClearDelaySeconds", 20, Sanitize.Clamp(5, 3600));

    public static readonly Setting<bool> AutoClearOnSleep = new("clipboardAutoClearOnSleep", false);

    public static readonly Setting<bool> AutoClearOnDisplaySleep = new("clipboardAutoClearOnDisplaySleep", false);

    public static readonly Setting<bool> AutoClearOnScreenLock = new("clipboardAutoClearOnScreenLock", false);

    // ── Paste as plain text ────────────────────────────────────────────
    public static Setting<bool> PastePlainEnabled => FeatureKeys.PastePlainEnabled;

    public static readonly Setting<string> PastePlainShortcut = new("pastePlainShortcut", string.Empty);

    /// <summary>Ctrl+Shift+Alt+V (spec 05 §6.1). Never Ctrl+Shift+V: apps use it for their own plain paste.</summary>
    public static readonly KeyChord DefaultPastePlainShortcut = new(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, 'V');

    // ── Clean URL ──────────────────────────────────────────────────────
    public static Setting<bool> UrlCleanerEnabled => FeatureKeys.UrlCleanerEnabled;

    /// <summary>Global names the user added (comma or newline separated, lower case).</summary>
    public static readonly Setting<string> UrlCleanerCustomParameters = new("urlCleanerCustomParameters", string.Empty);

    /// <summary><c>host|name</c> tokens the user added.</summary>
    public static readonly Setting<string> UrlCleanerSiteParameters = new("urlCleanerSiteParameters", string.Empty);

    /// <summary><c>host|name</c> tokens switched off; host "" is all sites, <c>|utm_*</c> the utm prefix.</summary>
    public static readonly Setting<string> UrlCleanerDisabledParameters = new("urlCleanerDisabledParameters", string.Empty);

    private static List<string> SanitizeApps(List<string> apps) =>
        apps.Select(a => a?.Trim() ?? string.Empty)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
