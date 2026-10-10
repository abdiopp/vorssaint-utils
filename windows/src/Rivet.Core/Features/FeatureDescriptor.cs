// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Features;

/// <summary>Hub sections, in display order.</summary>
public enum FeatureGroup
{
    Capture,
    Monitor,
    Tools,
    AppManagement,
    ClipboardFiles,
    Sound,
    EnergyDisplay,
    MouseKeyboard,
}

/// <summary>What a feature keeps alive while it is on (the hub's energy label).</summary>
public enum EnergyProfile
{
    Idle,
    Mouse,
    Keyboard,
    Inputs,
    Periodic,
}

/// <summary>
/// The Windows gates a feature can depend on. macOS TCC permissions mostly do
/// not exist for desktop apps; these are the ones that do.
/// </summary>
public enum Capability
{
    Notifications,
    Camera,
    Microphone,
    /// <summary>Some parts work only when the app runs as administrator (UIPI, system folders, sensors).</summary>
    Administrator,
}

public sealed record FeatureDescriptor
{
    public required string Id { get; init; }

    public required FeatureGroup Group { get; init; }

    /// <summary>Fluent UI System Icons symbol name (e.g. "Screenshot").</summary>
    public required string Icon { get; init; }

    public required string TitleKey { get; init; }

    public required string DescriptionKey { get; init; }

    /// <summary>The feature's own switches; any one on means engaged. Empty for on-demand tools.</summary>
    public IReadOnlyList<Setting<bool>> EnableKeys { get; init; } = [];

    /// <summary>Switches turned on the first time the feature is installed (default: the first enable key).</summary>
    public IReadOnlyList<Setting<bool>>? InitialEnableKeys { get; init; }

    /// <summary>Availability for installs that never chose (features added in later versions should say false).</summary>
    public bool InstalledByDefault { get; init; } = true;

    public EnergyProfile Energy { get; init; } = EnergyProfile.Idle;

    public IReadOnlyList<Capability> Capabilities { get; init; } = [];

    public bool IsBeta { get; init; }

    /// <summary>Whether the hub may offer to uninstall this feature when it was never switched on.</summary>
    public bool OfferWhenNeverSwitchedOn { get; init; }

    public string AvailabilityKey => $"featureAvailable.{Id}";

    public IReadOnlyList<Setting<bool>> EffectiveInitialEnableKeys =>
        InitialEnableKeys ?? (EnableKeys.Count > 0 ? [EnableKeys[0]] : []);
}
