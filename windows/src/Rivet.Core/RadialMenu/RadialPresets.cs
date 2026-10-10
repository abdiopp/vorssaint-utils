// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.RadialMenu;

/// <summary>The six profile presets (spec 07 §3.2.12); items in slot order from 12 o'clock.</summary>
public static class RadialPresets
{
    public static IReadOnlyList<RadialPreset> All { get; } =
        [RadialPreset.General, RadialPreset.Media, RadialPreset.Tools, RadialPreset.WindowLayout, RadialPreset.QuickToggles, RadialPreset.Blank];

    public static string Id(RadialPreset preset) => preset switch
    {
        RadialPreset.General => "general",
        RadialPreset.Media => "media",
        RadialPreset.Tools => "tools",
        RadialPreset.WindowLayout => "windowLayout",
        RadialPreset.QuickToggles => "quickToggles",
        _ => "blank",
    };

    public static RadialPreset? Parse(string? id) => id switch
    {
        "general" => RadialPreset.General,
        "media" => RadialPreset.Media,
        "tools" => RadialPreset.Tools,
        "windowLayout" => RadialPreset.WindowLayout,
        "quickToggles" => RadialPreset.QuickToggles,
        "blank" => RadialPreset.Blank,
        _ => null,
    };

    public static string TitleKey(RadialPreset preset) => preset switch
    {
        RadialPreset.General => "radialMenu.presetGeneral",
        RadialPreset.Media => "radialMenu.presetMedia",
        RadialPreset.Tools => "radialMenu.presetTools",
        RadialPreset.WindowLayout => "radialMenu.presetWindowLayout",
        RadialPreset.QuickToggles => "radialMenu.presetQuickToggles",
        _ => "radialMenu.presetBlank",
    };

    public static RadialColor Color(RadialPreset preset) => preset switch
    {
        RadialPreset.General => RadialColor.Accent,
        RadialPreset.Media => RadialColor.Purple,
        RadialPreset.Tools => RadialColor.Cyan,
        RadialPreset.WindowLayout => RadialColor.Orange,
        RadialPreset.QuickToggles => RadialColor.Mint,
        _ => RadialColor.Graphite,
    };

    /// <summary>Fresh items (new ids) for a preset.</summary>
    public static IReadOnlyList<RadialItem> Items(RadialPreset preset) => preset switch
    {
        RadialPreset.General =>
        [
            Media(RadialMediaIds.PlayPause), Media(RadialMediaIds.NextTrack), Tool("screenshot"),
            new RadialItem { Kind = RadialItemKind.File, Payload = "~/Downloads" }, Tool("colorPicker"), Media(RadialMediaIds.PreviousTrack),
        ],
        RadialPreset.Media =>
        [
            Media(RadialMediaIds.PlayPause), Media(RadialMediaIds.NextTrack), Media(RadialMediaIds.NowPlaying), Media(RadialMediaIds.PreviousTrack),
        ],
        RadialPreset.Tools =>
        [
            Tool("screenshot"), Tool("colorPicker"), Tool("screenOCR"), Tool("screenRecorder"), Tool("micMute"), Tool("scratchpad"),
        ],
        RadialPreset.WindowLayout =>
        [
            Layout("maximize"), Layout("rightHalf"), Layout("bottomHalf"), Layout("leftHalf"), Layout("topHalf"),
        ],
        RadialPreset.QuickToggles =>
        [
            Toggle(RadialQuickToggleIds.DarkMode), Toggle(RadialQuickToggleIds.DesktopIcons), Toggle(RadialQuickToggleIds.HiddenFiles),
            Toggle(RadialQuickToggleIds.LockScreen), Toggle(RadialQuickToggleIds.EmptyTrash),
        ],
        _ => [],
    };

    /// <summary>The default wheel of a fresh install: the General preset.</summary>
    public static IReadOnlyList<RadialItem> StarterItems() => Items(RadialPreset.General);

    /// <summary>
    /// A new profile from a preset. General profiles are named "General N"
    /// (N = profile count + 1); the others take the preset title. New profiles have no triggers.
    /// </summary>
    public static RadialProfile NewProfile(RadialPreset preset, int existingCount, Func<string, string> localize)
    {
        var title = localize(TitleKey(preset));
        var name = preset == RadialPreset.General ? $"{title} {existingCount + 1}" : title;
        return new RadialProfile
        {
            Name = name,
            Color = Color(preset),
            Items = Items(preset),
            Preset = preset,
        };
    }

    /// <summary>A copy with new ids, "&lt;name&gt; 2", and no triggers.</summary>
    public static RadialProfile Duplicate(RadialProfile profile, string generalTitle)
    {
        var name = profile.Name.Length == 0 ? generalTitle : profile.Name;
        return profile with
        {
            Id = Guid.NewGuid(),
            Name = Truncate($"{name} 2", RadialProfile.MaxNameLength),
            Shortcut = string.Empty,
            MouseButton = RadialMouseTrigger.Off,
            TrackpadTap = false,
            Items = profile.Items.Select(Renew).ToList(),
        };
    }

    /// <summary>The preset whose items "Reset" restores: the stored one, else matched by the profile name.</summary>
    public static RadialPreset? PresetFor(RadialProfile profile, Func<string, string> localize)
    {
        if (profile.Preset is { } stored)
        {
            return stored;
        }

        var name = profile.Name.Trim();
        if (name.Length == 0)
        {
            return RadialPreset.General;
        }

        foreach (var preset in All)
        {
            var title = localize(TitleKey(preset));
            if (name.Equals(title, StringComparison.CurrentCultureIgnoreCase)
                || (preset == RadialPreset.General && name.StartsWith(title + " ", StringComparison.CurrentCultureIgnoreCase)))
            {
                return preset;
            }
        }

        return null;
    }

    private static RadialItem Renew(RadialItem item) => item with { Id = Guid.NewGuid(), Children = item.Children.Select(Renew).ToList() };

    private static RadialItem Media(string id) => new() { Kind = RadialItemKind.Media, Payload = id };

    private static RadialItem Tool(string id) => new() { Kind = RadialItemKind.Tool, Payload = id };

    private static RadialItem Layout(string id) => new() { Kind = RadialItemKind.WindowLayout, Payload = id };

    private static RadialItem Toggle(string id) => new() { Kind = RadialItemKind.QuickToggle, Payload = id };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
