// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Modules.RadialMenu;

/// <summary>State of the Now Playing lookup for the open wheel.</summary>
public enum NowPlayingState
{
    Loading,
    NothingPlaying,
    Playing,
}

/// <summary>Automatic labels and icons (spec 07 §3.2.9). A non-empty custom name always wins.</summary>
public static class RadialLabels
{
    public static string Label(
        RadialItem item,
        Func<string, string> localize,
        Func<string, string> displayName,
        Func<string, string?> toolTitle,
        IKeyNameProvider? keyNames = null,
        NowPlayingState nowPlaying = NowPlayingState.NothingPlaying,
        NowPlayingSnapshot? snapshot = null)
    {
        if (item.Name.Length > 0)
        {
            return item.Name;
        }

        return item.Kind switch
        {
            RadialItemKind.App or RadialItemKind.File => displayName(item.Payload),
            RadialItemKind.Url => RadialLinks.HostLabel(item.Payload),
            RadialItemKind.Shortcut => KeyChord.TryParse(item.Payload, out var chord) && !chord.IsEmpty
                ? chord.ToDisplayString(keyNames)
                : localize("radialMenu.kindShortcut"),
            RadialItemKind.Tool => toolTitle(item.Payload) ?? item.Payload,
            RadialItemKind.QuickToggle => localize(QuickToggleTitleKey(item.Payload)),
            RadialItemKind.WindowLayout => localize(WindowLayoutActions.TitleKey(item.Payload)),
            RadialItemKind.Media => item.Payload switch
            {
                RadialMediaIds.PlayPause => localize("radialMenu.mediaPlayPause"),
                RadialMediaIds.PreviousTrack => localize("radialMenu.mediaPrevious"),
                RadialMediaIds.NextTrack => localize("radialMenu.mediaNext"),
                _ => nowPlaying switch
                {
                    NowPlayingState.Playing when snapshot is not null => snapshot.Label,
                    NowPlayingState.Loading => localize("radialMenu.mediaNowPlaying"),
                    _ => localize("radialMenu.mediaNothingPlaying"),
                },
            },
            RadialItemKind.Submenu => localize("radialMenu.kindSubmenu"),
            _ => item.Payload,
        };
    }

    /// <summary>The Fluent icon used when no real icon or custom symbol applies.</summary>
    public static string AutomaticSymbol(RadialItem item, Func<string, string?>? toolIcon = null)
    {
        if (item.SymbolName.Length > 0)
        {
            return item.SymbolName;
        }

        return item.Kind switch
        {
            RadialItemKind.App => "Apps",
            RadialItemKind.File => item.Payload.EndsWith('/') || item.Payload.EndsWith('\\') || !Path.HasExtension(item.Payload) ? "Folder" : "Document",
            RadialItemKind.Url => "Globe",
            RadialItemKind.Shortcut => "Keyboard",
            RadialItemKind.Tool => toolIcon?.Invoke(item.Payload) ?? "Apps",
            RadialItemKind.QuickToggle => item.Payload switch
            {
                RadialQuickToggleIds.DarkMode => "DarkTheme",
                RadialQuickToggleIds.EmptyTrash => "Delete",
                RadialQuickToggleIds.EjectDisks => "ArrowEject",
                RadialQuickToggleIds.HiddenFiles => "EyeOff",
                RadialQuickToggleIds.DesktopIcons => "Desktop",
                RadialQuickToggleIds.LockScreen => "LockClosed",
                RadialQuickToggleIds.DisplayOff => "DesktopOff",
                _ => "Image",
            },
            RadialItemKind.WindowLayout => item.Payload switch
            {
                "maximize" or "fullScreen" or "marginMaximize" => "Maximize",
                "restore" => "ArrowUndo",
                "center" => "AlignCenterHorizontal",
                "nextDisplay" or "previousDisplay" => "DesktopArrowRight",
                "leftHalf" => "LayoutColumnTwoFocusLeft",
                "rightHalf" => "LayoutColumnTwoFocusRight",
                _ => "LayoutColumnTwo",
            },
            RadialItemKind.Media => item.Payload switch
            {
                RadialMediaIds.PlayPause => "Play",
                RadialMediaIds.PreviousTrack => "Previous",
                RadialMediaIds.NextTrack => "Next",
                _ => "MusicNote2",
            },
            RadialItemKind.Submenu => "Grid",
            _ => "Apps",
        };
    }

    public static string QuickToggleTitleKey(string id) => id switch
    {
        RadialQuickToggleIds.DarkMode => "quickToggles.darkModeToDark",
        RadialQuickToggleIds.EmptyTrash => "quickToggles.emptyTrashTitle",
        RadialQuickToggleIds.EjectDisks => "quickToggles.ejectTitle",
        RadialQuickToggleIds.HiddenFiles => "quickToggles.hiddenFilesShow",
        RadialQuickToggleIds.DesktopIcons => "quickToggles.desktopIconsHide",
        RadialQuickToggleIds.LockScreen => "quickToggles.lockScreenTitle",
        RadialQuickToggleIds.DisplayOff => "quickToggles.displayOffTitle",
        _ => "quickToggles.screenSaverTitle",
    };

    /// <summary>Colour name string keys, for the swatch tooltips.</summary>
    public static string ColorTitleKey(RadialColor color) => color switch
    {
        RadialColor.Accent => "radialMenu.colorAccent",
        RadialColor.Blue => "radialMenu.colorBlue",
        RadialColor.Purple => "radialMenu.colorPurple",
        RadialColor.Pink => "radialMenu.colorPink",
        RadialColor.Red => "radialMenu.colorRed",
        RadialColor.Orange => "radialMenu.colorOrange",
        RadialColor.Yellow => "radialMenu.colorYellow",
        RadialColor.Green => "radialMenu.colorGreen",
        RadialColor.Mint => "radialMenu.colorMint",
        RadialColor.Cyan => "radialMenu.colorCyan",
        RadialColor.Indigo => "radialMenu.colorIndigo",
        _ => "radialMenu.colorGraphite",
    };

    /// <summary>
    /// Profile colours as 0xAARRGGBB: the macOS light-mode values from the spec,
    /// the macOS dark system colours in dark mode, and the Windows accent for "accent".
    /// </summary>
    public static uint ColorValue(RadialColor color, bool dark, uint accent) => color switch
    {
        RadialColor.Accent => accent | 0xFF000000,
        RadialColor.Blue => dark ? 0xFF0A84FF : Rgb(0, 0.48, 1),
        RadialColor.Purple => dark ? 0xFFBF5AF2 : Rgb(0.58, 0.20, 0.85),
        RadialColor.Pink => dark ? 0xFFFF375F : Rgb(0.88, 0.16, 0.45),
        RadialColor.Red => dark ? 0xFFFF453A : Rgb(0.85, 0.18, 0.18),
        RadialColor.Orange => dark ? 0xFFFF9F0A : Rgb(0.95, 0.45, 0),
        RadialColor.Yellow => dark ? 0xFFFFD60A : Rgb(0.85, 0.65, 0),
        RadialColor.Green => dark ? 0xFF30D158 : Rgb(0.18, 0.65, 0.25),
        RadialColor.Mint => dark ? 0xFF66D4CF : Rgb(0, 0.68, 0.60),
        RadialColor.Cyan => dark ? 0xFF5AC8F5 : Rgb(0.15, 0.65, 0.85),
        RadialColor.Indigo => dark ? 0xFF5E5CE6 : Rgb(0.35, 0.35, 0.85),
        _ => dark ? Rgb(0.65, 0.65, 0.65) : Rgb(0.40, 0.40, 0.40),
    };

    private static uint Rgb(double r, double g, double b) =>
        0xFF000000 | ((uint)Math.Round(r * 255) << 16) | ((uint)Math.Round(g * 255) << 8) | (uint)Math.Round(b * 255);
}
