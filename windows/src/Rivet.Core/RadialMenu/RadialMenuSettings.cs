// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Modules.RadialMenu;

public enum RadialActivationMode
{
    PressOrHold,
    Press,
    Hold,
}

/// <summary>Radial menu preferences (raw keys match the macOS app).</summary>
public static class RadialMenuSettings
{
    public static Setting<bool> Enabled => FeatureKeys.RadialMenuEnabled;

    /// <summary>true = at the pointer, false = at the centre of the pointer's screen.</summary>
    public static readonly Setting<bool> AtPointer = new("radialMenuAtPointer", true);

    public static readonly Setting<string> ActivationMode =
        new("radialMenuActivationMode", "pressOrHold", Sanitize.OneOfStrings("pressOrHold", "pressOrHold", "press", "hold"));

    /// <summary>All wheels, stored as a JSON array (a base64 or JSON string from a macOS backup also decodes).</summary>
    public const string ProfilesKey = "radialMenuProfiles";

    /// <summary>Legacy seed for the first profile's shortcut (also the spec's role storage key; never registered itself).</summary>
    public static readonly Setting<string> LegacyShortcut = new("radialMenuShortcut", string.Empty);

    /// <summary>Legacy seed for the first profile's mouse button.</summary>
    public static readonly Setting<string> LegacyMouseButton = new("radialMenuMouseButton", "off");

    /// <summary>Legacy seed items; absent → the starter wheel; present but empty stays empty.</summary>
    public const string LegacyItemsKey = "radialMenuItems";

    /// <summary>Ctrl+Alt+Win+Space (spec 05 §6.1) for the first wheel.</summary>
    public static KeyChord DefaultShortcut => KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Space);

    public static RadialActivationMode ParseMode(string value) => value switch
    {
        "press" => RadialActivationMode.Press,
        "hold" => RadialActivationMode.Hold,
        _ => RadialActivationMode.PressOrHold,
    };

    public static string ToStorage(RadialActivationMode mode) => mode switch
    {
        RadialActivationMode.Press => "press",
        RadialActivationMode.Hold => "hold",
        _ => "pressOrHold",
    };
}
