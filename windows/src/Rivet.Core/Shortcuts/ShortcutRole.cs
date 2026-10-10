// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Shortcuts;

/// <summary>
/// One global shortcut of a feature. It registers only while the feature is
/// installed and all <see cref="RequiredEnableKeys"/> are on. Roles always
/// have a value (Reset restores the default); clearing is not offered.
/// </summary>
public sealed record ShortcutRole
{
    public required string Id { get; init; }

    public required string FeatureId { get; init; }

    public required string TitleKey { get; init; }

    /// <summary>Where the chord is stored (<see cref="KeyChord.ToStorageString"/>); empty = default.</summary>
    public required Setting<string> Storage { get; init; }

    public required KeyChord Default { get; init; }

    public IReadOnlyList<Setting<bool>> RequiredEnableKeys { get; init; } = [];

    /// <summary>The action invoked when the shortcut is pressed.</summary>
    public required string ActionId { get; init; }

    public HotkeyOptions Options { get; init; } = HotkeyOptions.None;

    /// <summary>Groups related roles on the Keyboard shortcuts page (e.g. "screenCapture").</summary>
    public string? GroupId { get; init; }
}

public enum ShortcutState
{
    /// <summary>Saved but not running (feature uninstalled or switched off).</summary>
    Inactive,

    Active,

    /// <summary>Should run, but Windows or another app owns the combination.</summary>
    RegistrationFailed,
}
