// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Rivet.Core.Settings;

namespace Rivet.App.Modules;

/// <summary>
/// A tab of the tray panel. Shown while any of <see cref="FeatureIds"/> is
/// installed (and <see cref="IsVisible"/> allows it) and the user did not hide it.
/// </summary>
public sealed record PanelSectionDescriptor
{
    public required string Id { get; init; }

    public required string TitleKey { get; init; }

    /// <summary>Fluent icon name for the tab.</summary>
    public required string Icon { get; init; }

    public required IReadOnlyList<string> FeatureIds { get; init; }

    /// <summary>Default position (lower first) until the user reorders.</summary>
    public int Order { get; init; }

    /// <summary>Builds the section content. Called each time the tab is shown.</summary>
    public required Func<IServiceProvider, Control> CreateView { get; init; }

    public Func<bool>? IsVisible { get; init; }

    /// <summary>Clicks in other apps do not close the panel while this tab is shown.</summary>
    public bool KeepsPanelOpen { get; init; }

    /// <summary>The settings page the footer's Settings button opens while this tab is shown.</summary>
    public string? SettingsPageId { get; init; }
}

/// <summary>A row of the Utilities tab: launches a tool, or hosts a mini tool in place.</summary>
public sealed record PanelTileDescriptor
{
    public required string Id { get; init; }

    public required string FeatureId { get; init; }

    public required string TitleKey { get; init; }

    /// <summary>Tooltip/caption text.</summary>
    public string? CaptionKey { get; init; }

    public required string Icon { get; init; }

    public int Order { get; init; }

    /// <summary>Overlay tools: the panel closes and this action runs shortly after.</summary>
    public string? ActionId { get; init; }

    /// <summary>Hosted tools: replaces the tile list inside the panel until the user goes back.</summary>
    public Func<IServiceProvider, Control>? CreateHostedView { get; init; }

    /// <summary>Shows the shortcut of this role as a hint on the row.</summary>
    public string? ShortcutRoleId { get; init; }

    public string? SettingsPageId { get; init; }

    public bool HostedKeepsPanelOpen { get; init; } = true;

    /// <summary>A live caption that stays visible (e.g. "Recording 0:42"); null hides it.</summary>
    public Func<string?>? LiveCaption { get; init; }

    /// <summary>A title that changes with state ("Stop recording"); null falls back to <see cref="TitleKey"/>.</summary>
    public Func<string?>? LiveTitle { get; init; }

    /// <summary>An icon that changes with state; null falls back to <see cref="Icon"/>.</summary>
    public Func<string?>? LiveIcon { get; init; }

    /// <summary>Optional small button under the row (e.g. "Recent captures" under Screenshot).</summary>
    public PanelTileAccessory? Accessory { get; init; }
}

/// <summary>A secondary button shown under a Utilities row.</summary>
public sealed record PanelTileAccessory
{
    public required string TitleKey { get; init; }

    public required string Icon { get; init; }

    /// <summary>Runs an action (the panel closes first).</summary>
    public string? ActionId { get; init; }

    /// <summary>Or hosts a mini tool in place, like a hosted tile.</summary>
    public Func<IServiceProvider, Control>? CreateHostedView { get; init; }

    /// <summary>Hidden while this returns false.</summary>
    public Func<bool>? IsVisible { get; init; }
}

public enum PanelToggleCategory
{
    Input,
    Files,
    Other,
}

/// <summary>A switch row of the Controls tab, bound to a feature's enable key.</summary>
public sealed record PanelToggleDescriptor
{
    public required string Id { get; init; }

    public required string FeatureId { get; init; }

    public required string TitleKey { get; init; }

    public string? CaptionKey { get; init; }

    public required string Icon { get; init; }

    public required Setting<bool> Setting { get; init; }

    public PanelToggleCategory Category { get; init; } = PanelToggleCategory.Input;

    public int Order { get; init; }

    public string? SettingsPageId { get; init; }
}

public sealed class PanelRegistry
{
    private readonly List<PanelSectionDescriptor> _sections = [];
    private readonly List<PanelTileDescriptor> _tiles = [];
    private readonly List<PanelToggleDescriptor> _toggles = [];

    public event EventHandler? Changed;

    public IReadOnlyList<PanelSectionDescriptor> Sections => _sections;

    public IReadOnlyList<PanelTileDescriptor> Tiles => _tiles;

    public IReadOnlyList<PanelToggleDescriptor> Toggles => _toggles;

    public void AddSection(PanelSectionDescriptor section)
    {
        _sections.RemoveAll(s => s.Id == section.Id);
        _sections.Add(section);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddTile(PanelTileDescriptor tile)
    {
        _tiles.RemoveAll(t => t.Id == tile.Id);
        _tiles.Add(tile);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddToggle(PanelToggleDescriptor toggle)
    {
        _toggles.RemoveAll(t => t.Id == toggle.Id);
        _toggles.Add(toggle);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks open panels to rebuild (a tile's live title, icon or visibility changed).</summary>
    public void Invalidate() => Rivet.Core.Util.UiThread.Run(() => Changed?.Invoke(this, EventArgs.Empty));
}
