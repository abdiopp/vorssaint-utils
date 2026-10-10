// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Input;

/// <summary>
/// Settings of the input fixes (spec 07 §4.7). Raw keys match the macOS app
/// wherever the setting exists there, so backups can move between platforms;
/// the enable switches themselves live in <see cref="Features.FeatureKeys"/>.
/// Values whose format is macOS-specific (key codes, bundle ids) are converted
/// or ignored on read, never trusted.
/// </summary>
public static class InputSettings
{
    // ── Extra click filter ─────────────────────────────────────────────
    public const int DefaultClickWindowMs = 25;

    /// <summary>Filter window, 5–100 ms; anything else falls back to 25 (not clamped).</summary>
    public static readonly Setting<int> ClickDebounceWindowMs =
        new("mouseClickDebounceWindowMs", DefaultClickWindowMs, v => v is >= 5 and <= 100 ? v : DefaultClickWindowMs);

    // ── Key debounce ───────────────────────────────────────────────────
    public const int DefaultKeyWindowMs = 5;

    /// <summary>Global window, 0–500 ms (0 accepts everything); out of range → 5, not clamped.</summary>
    public static readonly Setting<int> KeyDebounceWindowMs =
        new("keyboardDebounceWindowMs", DefaultKeyWindowMs, v => v is >= 0 and <= 500 ? v : DefaultKeyWindowMs);

    /// <summary>Per-key overrides, see <see cref="KeyDebounceOverrides"/> for the format.</summary>
    public static readonly Setting<string> KeyDebounceKeyWindows = new("keyboardDebounceKeyWindows", string.Empty);

    // ── Mouse button shortcuts and the desktop drag ────────────────────
    /// <summary>Button id ("3", "4", "-2", "-1", …) → shortcut (<see cref="Shortcuts.KeyChord"/> storage string).</summary>
    public static readonly Setting<IReadOnlyDictionary<string, string>> MouseButtonShortcuts =
        new("mouseButtonShortcuts", new Dictionary<string, string>(), MouseButtonMappings.Sanitize);

    public static readonly Setting<bool> DesktopGestureEnabled = new("mouseSpacesGestureEnabled", false);

    /// <summary>The button held for the desktop drag: 3–31, 0 = none.</summary>
    public static readonly Setting<int> DesktopGestureButton =
        new("mouseSpacesGestureButton", 0, v => v is >= MouseButtonIds.FirstExtra and <= MouseButtonIds.LastExtra ? v : 0);

    public static readonly Setting<bool> DesktopGestureFollowsDrag = new("mouseSpacesGestureFollowsDrag", false);

    public static readonly Setting<string[]> MouseButtonExceptions =
        new("mouseButtonExceptions", [], AppExclusionList.Sanitize);

    // ── Smooth scrolling ───────────────────────────────────────────────
    public const int DefaultSmoothStep = 40;

    /// <summary>Pixels per notch, 20–100 (0 → 40).</summary>
    public static readonly Setting<int> SmoothScrollStep =
        new("smoothScrollStep", DefaultSmoothStep, v => v == 0 ? DefaultSmoothStep : Math.Clamp(v, 20, 100));

    /// <summary>Response 0–100 % (response time 160 → 40 ms).</summary>
    public static readonly Setting<int> SmoothScrollResponse = new("smoothScrollResponse", 65, v => Math.Clamp(v, 0, 100));

    /// <summary>Coast 0–100 % (landing stretch up to 3×).</summary>
    public static readonly Setting<int> SmoothScrollCoast = new("smoothScrollCoast", 0, v => Math.Clamp(v, 0, 100));

    public static readonly Setting<string[]> SmoothScrollExceptions =
        new("smoothScrollExceptions", [], AppExclusionList.Sanitize);

    // ── Scroll direction ───────────────────────────────────────────────
    /// <summary>Invert horizontal wheels. When never saved it follows the vertical switch (macOS migration).</summary>
    public static readonly Setting<bool> ScrollInverterHorizontal = new("scrollInverterHorizontalEnabled", false);

    public static readonly Setting<string[]> ScrollInverterExceptions =
        new("scrollInverterExceptions", [], AppExclusionList.Sanitize);

    // ── Super key ──────────────────────────────────────────────────────
    public static readonly Setting<string> SuperKeySource =
        new("superKeySource", SuperKeySources.CapsLock, SuperKeySources.Sanitize);

    /// <summary>
    /// Modifier tokens joined by "+" (control, option, shift, command = Ctrl, Alt,
    /// Shift, Win). The Windows default is Ctrl+Alt+Shift: all four together is
    /// the Office key (spec 07 §3.7.6).
    /// </summary>
    public static readonly Setting<string> SuperKeyModifiers =
        new("superKeyModifiers", SuperKeyModifierSet.DefaultStorage, SuperKeyModifierSet.SanitizeStorage);

    public static readonly Setting<string> SuperKeySoloAction =
        new("superKeySoloAction", SuperKeySoloActions.None, SuperKeySoloActions.Sanitize);

    public static readonly Setting<string[]> SuperKeyExceptions =
        new("superKeyExceptions", [], AppExclusionList.Sanitize);
}

/// <summary>Settings of one quit-protection slot ("Quit" = Alt+F4/Ctrl+Q, "Close" = Ctrl+W/Ctrl+F4).</summary>
public sealed class QuitProtectionSlotSettings
{
    private QuitProtectionSlotSettings(string name, Setting<bool> enabled, bool secondChordDefault)
    {
        Enabled = enabled;
        Mode = new($"quitProtection{name}Mode", QuitProtectionModes.Hold, QuitProtectionModes.Sanitize);
        HoldDurationMs = new($"quitProtection{name}HoldDurationMs", 800, v => double.IsFinite(v) ? Math.Clamp(v, 250, 2000) : 800);
        DoubleIntervalMs = new($"quitProtection{name}DoubleIntervalMs", 600, v => double.IsFinite(v) ? Math.Clamp(v, 200, 1500) : 600);
        ExtraModifier = new($"quitProtection{name}ExtraModifier", QuitProtectionModes.ShiftModifier, QuitProtectionModes.SanitizeModifier);
        Scope = new($"quitProtection{name}Scope", QuitProtectionScopes.All, QuitProtectionScopes.Sanitize);
        Exceptions = new($"quitProtection{name}Exceptions", [], AppExclusionList.SanitizeSorted);
        ShowFeedback = new($"quitProtection{name}ShowFeedback", true);
        SecondChord = new($"quitProtection{name}{(name == "Quit" ? "CtrlQ" : "CtrlF4")}", secondChordDefault);
    }

    public static QuitProtectionSlotSettings Quit { get; } = new("Quit", Features.FeatureKeys.QuitProtectionQuitEnabled, secondChordDefault: true);

    public static QuitProtectionSlotSettings Close { get; } = new("Close", Features.FeatureKeys.QuitProtectionCloseEnabled, secondChordDefault: false);

    public Setting<bool> Enabled { get; }

    public Setting<string> Mode { get; }

    public Setting<double> HoldDurationMs { get; }

    public Setting<double> DoubleIntervalMs { get; }

    public Setting<string> ExtraModifier { get; }

    public Setting<string> Scope { get; }

    public Setting<string[]> Exceptions { get; }

    public Setting<bool> ShowFeedback { get; }

    /// <summary>
    /// Windows-only: also protect the slot's second chord (Ctrl+Q for the quit
    /// slot, on by default; Ctrl+F4 for the close slot, off by default).
    /// </summary>
    public Setting<bool> SecondChord { get; }

    public IEnumerable<SettingDefinition> All =>
        [Enabled, Mode, HoldDurationMs, DoubleIntervalMs, ExtraModifier, Scope, Exceptions, ShowFeedback, SecondChord];
}
