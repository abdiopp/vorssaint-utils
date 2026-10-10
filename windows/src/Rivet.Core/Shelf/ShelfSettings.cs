// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Modules.Shelf;

public enum ShelfDockPlacement
{
    /// <summary>Near the tray icon (macOS "below the menu bar icon").</summary>
    Tray,

    TopCenter,
}

/// <summary>Shelf preferences (raw keys match the macOS app, spec 07 §4.1). The items live in their own file.</summary>
public static class ShelfSettings
{
    public static Setting<bool> Enabled => FeatureKeys.ShelfEnabled;

    public static readonly Setting<bool> ShortcutEnabled = new("shelfShortcutEnabled", true);

    /// <summary>Toggle hotkey (VK storage, empty = the default Ctrl+Alt+Win+D).</summary>
    public static readonly Setting<string> Shortcut = new("shelfShortcut", string.Empty);

    /// <summary>With File Explorer in front, the shortcut adds its selection.</summary>
    public static readonly Setting<bool> ShortcutAddsExplorerSelection = new("shelfShortcutAddsFinderSelection", false);

    public static readonly Setting<bool> ShakeToOpen = new("shelfShakeToOpen", true);

    /// <summary>The docked pill near the tray icon appears during drags and keeps dropped items.</summary>
    public static readonly Setting<bool> DropZoneEnabled = new("shelfDropZoneEnabled", true);

    public static readonly Setting<string> DockPlacement = new("shelfDockPlacement", "menuBar", Sanitize.OneOfStrings("menuBar", "menuBar", "topCenter"));

    public static readonly Setting<bool> EdgeDragEnabled = new("shelfEdgeDragEnabled", false);

    public static readonly Setting<bool> CloseAfterDrop = new("shelfCloseAfterDrop", false);

    public static readonly Setting<bool> RemoveAfterDrop = new("shelfRemoveAfterDrop", true);

    public static readonly Setting<bool> ClearOnClose = new("shelfClearOnClose", false);

    /// <summary>Apps (full .exe paths or file names) whose drags never auto-open the shelf; trimmed, de-duplicated, order kept.</summary>
    public static readonly Setting<List<string>> AutomaticExclusions = new("shelfAutomaticExclusions", [], CleanExclusions);

    public static KeyChord DefaultShortcut => KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Letter('D'));

    public static ShelfDockPlacement ParsePlacement(string value) => value == "topCenter" ? ShelfDockPlacement.TopCenter : ShelfDockPlacement.Tray;

    public static string ToStorage(ShelfDockPlacement placement) => placement == ShelfDockPlacement.TopCenter ? "topCenter" : "menuBar";

    public static List<string> CleanExclusions(List<string>? list)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list ?? [])
        {
            var trimmed = entry?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}

/// <summary>Constants of spec 07 §6.1.</summary>
public static class ShelfConstants
{
    public const int MaxLeaves = 200;
    public const int MaxTextLength = 200_000;
    public const int RestoreDepth = 4;
    public const int TextTitleLength = 48;
    public const int TooltipTextCap = 500;

    public static readonly TimeSpan ShakeWindow = TimeSpan.FromSeconds(0.5);
    public const int ShakeMinimumSamples = 5;
    public const double ShakeDirectionThreshold = 6;
    public const int ShakeReversals = 3;
    public const double ShakeTravel = 220;
    public static readonly TimeSpan ShakeCooldown = TimeSpan.FromSeconds(1.0);

    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(0.15);
    public static readonly TimeSpan DockDwell = TimeSpan.FromSeconds(0.15);
    public const double DockTriggerMargin = 16;
    public const double DockRetreatMargin = 32;
    public const double FallbackPillWidth = 72;
    public const double FallbackPillHeight = 32;
    public static readonly TimeSpan DockDragEndGrace = TimeSpan.FromSeconds(0.15);
    public static readonly TimeSpan DockCatchTick = TimeSpan.FromSeconds(0.9);
    public const double DockInset = 4;
    public const double DockSideClamp = 8;
    public const double DockNoAnchorInset = 12;

    public const double EdgeTrigger = 200;
    public const double EdgeRetreat = 330;
    public static readonly TimeSpan EdgeDwell = TimeSpan.FromSeconds(0.15);
    public const double PeekMargin = 8;
    public static readonly TimeSpan EdgeEndGrace = TimeSpan.FromSeconds(0.15);

    public static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(0.22);
    public const double SummonOffset = 16;
    public const double SummonClamp = 8;
    public static readonly TimeSpan OwnedPayloadRetirement = TimeSpan.FromSeconds(600);

    public const double CardWidth = 304;
    public const double TileAreaHeight = 188;
    public const double CardPadding = 14;
    public const double CardSpacing = 11;
    public const double CardRadius = 18;
    public const double TileWidth = 78;
    public const double TileHeight = 88;
    public const double TileSpacing = 10;
    public const double TileInset = 4;
    public const double MinContentWidth = 276;
    public const double DragStartThreshold = 4;
    public const int OpenWithCap = 40;
    public static readonly TimeSpan TooltipDelay = TimeSpan.FromSeconds(1.0);
    public const double TooltipMaxWidth = 280;
    public const double ThumbnailSize = 64;
    public const double GenericIconSize = 20;
}
