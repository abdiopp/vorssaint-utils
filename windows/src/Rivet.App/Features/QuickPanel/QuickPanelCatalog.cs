// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Features.Toggles;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.QuickPanel;
using Rivet.Core.Settings;

namespace Rivet.App.Features.QuickPanel;

public enum QuickTileKind
{
    /// <summary>Runs an action (overlay tools close the panel first).</summary>
    Action,

    /// <summary>Hosts a mini tool inside the panel.</summary>
    Hosted,

    /// <summary>Flips a feature switch; the panel stays.</summary>
    Switch,
}

/// <summary>One tool of the quick panel, resolved from the registries at each opening.</summary>
public sealed record QuickPanelTile
{
    public required string Id { get; init; }

    public required string FeatureId { get; init; }

    public required string TitleKey { get; init; }

    public required string Icon { get; init; }

    public required QuickTileKind Kind { get; init; }

    public string? ActionId { get; init; }

    /// <summary>Action tiles that keep the panel open (toggles such as mic mute).</summary>
    public bool KeepsOpen { get; init; }

    /// <summary>The pause between hiding the panel and running an overlay tool.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(150);

    public Func<IServiceProvider, Control>? CreateView { get; init; }

    public Setting<bool>? Switch { get; init; }

    /// <summary>Action tiles: the action's own on/off state (<see cref="AppAction.IsOn"/>).</summary>
    public Func<bool>? IsOn { get; init; }

    public string? SettingsPageId { get; init; }

    /// <summary>The clipboard tile has an inline options card in edit mode.</summary>
    public bool HasInlineOptions { get; init; }
}

/// <summary>
/// The quick panel's tools (spec 06 §3.9). The macOS tile list is resolved
/// against what modules registered: a Utilities tile of the same feature
/// (hosted or overlay), else a Controls switch, else a "&lt;feature&gt;.toggle"
/// action. Utilities tiles of other features join after the known ones, so
/// new modules appear without a shared list. Order and hidden tiles persist
/// in <c>quickLauncherItemOrder</c> / <c>quickLauncherHiddenItems</c>.
/// </summary>
public sealed class QuickPanelCatalog(IServiceProvider services, ISettingsStore settings, PanelRegistry panel, ActionRegistry actions, FeatureRuntime runtime)
{
    /// <summary>The macOS tile id → the feature that provides it on Windows.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownFeatures = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["keepAwake"] = FeatureIds.KeepAwake,
        ["cleaner"] = FeatureIds.Cleaner,
        ["toggles"] = FeatureIds.QuickToggles,
        ["micMute"] = FeatureIds.MicMute,
        ["screenOCR"] = FeatureIds.ScreenOcr,
        ["colorPicker"] = FeatureIds.ColorPicker,
        ["clipboard"] = FeatureIds.ClipboardHistory,
        ["cleaning"] = FeatureIds.CleaningMode,
        ["homebrew"] = FeatureIds.PackageManager,
        ["media"] = FeatureIds.MediaTools,
        ["urlCleaner"] = FeatureIds.UrlCleaner,
        ["uninstaller"] = FeatureIds.Uninstaller,
        ["screenshot"] = FeatureIds.Screenshot,
        ["screenRecorder"] = FeatureIds.ScreenRecorder,
        ["cameraPreview"] = FeatureIds.CameraPreview,
        ["scratchpad"] = FeatureIds.Scratchpad,
    };

    /// <summary>Panel tiles that never become quick panel tools (the quick panel itself).</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "quickLauncher" };

    /// <summary>Every tool available now, in the default order (known ids first, then other modules' tiles).</summary>
    public List<QuickPanelTile> Available()
    {
        var tiles = new List<QuickPanelTile>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in QuickPanelSettings.DefaultOrder)
        {
            if (KnownFeatures.TryGetValue(id, out var feature) && runtime.IsAvailable(feature) && Resolve(id, feature, used) is { } tile)
            {
                tiles.Add(tile);
            }
        }

        foreach (var extra in panel.Tiles.OrderBy(t => t.Order))
        {
            if (used.Contains(extra.Id) || Excluded.Contains(extra.Id) || !runtime.IsAvailable(extra.FeatureId)
                || tiles.Any(t => t.Id == extra.Id || t.FeatureId == extra.FeatureId))
            {
                continue;
            }

            if (FromPanelTile(extra.Id, extra) is { } tile)
            {
                tiles.Add(tile);
            }
        }

        return tiles;
    }

    private QuickPanelTile? Resolve(string id, string feature, HashSet<string> used)
    {
        switch (id)
        {
            case "toggles":
                return new QuickPanelTile
                {
                    Id = id, FeatureId = feature, TitleKey = "quickToggles.pageTitle", Icon = "ToggleMultiple", Kind = QuickTileKind.Hosted,
                    SettingsPageId = QuickTogglesModule.PageId,
                    CreateView = sp => new QuickTogglesView(sp, editable: false, closeSurface: () => services.GetService<QuickPanelHost>()?.Close(), source: ActionSource.QuickPanel),
                };
            case "clipboard":
                used.Add("clipboard");
                return new QuickPanelTile
                {
                    Id = id, FeatureId = feature, TitleKey = "clipboard.title", Icon = "ClipboardPaste", Kind = QuickTileKind.Action,
                    ActionId = "clipboardHistory.show", Delay = TimeSpan.FromMilliseconds(100), SettingsPageId = ClipboardModule.ClipboardPageId,
                    HasInlineOptions = true,
                };
            case "urlCleaner":
                used.Add("cleanURL");
                return new QuickPanelTile
                {
                    Id = id, FeatureId = feature, TitleKey = "Strings.urlCleanerName", Icon = "Link", Kind = QuickTileKind.Hosted,
                    SettingsPageId = ClipboardModule.UrlCleanerPageId,
                    CreateView = sp => new UrlCleanerView(sp, showAutomaticSwitch: true, showTitle: false),
                };
        }

        if (panel.Tiles.FirstOrDefault(t => t.FeatureId == feature && !Excluded.Contains(t.Id)) is { } tile)
        {
            used.Add(tile.Id);
            return FromPanelTile(id, tile);
        }

        var catalogEntry = FeatureCatalog.Find(feature);
        if (panel.Toggles.FirstOrDefault(t => t.FeatureId == feature) is { } toggle)
        {
            return new QuickPanelTile
            {
                Id = id, FeatureId = feature, TitleKey = toggle.TitleKey, Icon = toggle.Icon, Kind = QuickTileKind.Switch,
                Switch = toggle.Setting, SettingsPageId = toggle.SettingsPageId,
            };
        }

        if (actions.Get(feature + ".toggle") is { } action && actions.IsEnabled(action))
        {
            return new QuickPanelTile
            {
                Id = id, FeatureId = feature, TitleKey = action.TitleKey, Icon = action.Icon, Kind = QuickTileKind.Action,
                ActionId = action.Id, KeepsOpen = true, SettingsPageId = null, IsOn = action.IsOn,
            };
        }

        return catalogEntry is { EnableKeys.Count: 1 }
            ? new QuickPanelTile { Id = id, FeatureId = feature, TitleKey = catalogEntry.TitleKey, Icon = catalogEntry.Icon, Kind = QuickTileKind.Switch, Switch = catalogEntry.EnableKeys[0] }
            : null;
    }

    private static QuickPanelTile? FromPanelTile(string id, PanelTileDescriptor tile)
    {
        if (tile.CreateHostedView is { } create)
        {
            return new QuickPanelTile
            {
                Id = id, FeatureId = tile.FeatureId, TitleKey = tile.TitleKey, Icon = tile.Icon, Kind = QuickTileKind.Hosted,
                CreateView = create, SettingsPageId = tile.SettingsPageId,
            };
        }

        return tile.ActionId is { } actionId
            ? new QuickPanelTile
            {
                Id = id, FeatureId = tile.FeatureId, TitleKey = tile.TitleKey, Icon = tile.Icon, Kind = QuickTileKind.Action,
                ActionId = actionId, SettingsPageId = tile.SettingsPageId,
            }
            : null;
    }

    /// <summary>The tools in the user's order; hidden ones only when asked.</summary>
    public List<QuickPanelTile> Ordered(bool includeHidden)
    {
        var available = Available();
        var order = QuickPanelSettings.SanitizeOrder(settings.Get(QuickPanelSettings.ItemOrder), available.Select(t => t.Id).ToList());
        var hidden = Hidden;
        return order.Select(id => available.First(t => t.Id == id)).Where(t => includeHidden || !hidden.Contains(t.Id)).ToList();
    }

    public HashSet<string> Hidden => QuickPanelSettings.ParseHidden(settings.Get(QuickPanelSettings.HiddenItems));

    public void SetHidden(string id, bool hidden)
    {
        var set = Hidden;
        if (hidden ? set.Add(id) : set.Remove(id))
        {
            settings.Set(QuickPanelSettings.HiddenItems, QuickPanelSettings.FormatHidden(set));
        }
    }

    /// <summary>Moves a tool to the position of another (drag reorder) or by a step (Settings buttons).</summary>
    public void MoveTo(string id, int targetIndex)
    {
        var available = Available().Select(t => t.Id).ToList();
        var order = QuickPanelSettings.SanitizeOrder(settings.Get(QuickPanelSettings.ItemOrder), available);
        var from = order.IndexOf(id);
        if (from < 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, order.Count - 1);
        if (from == targetIndex)
        {
            return;
        }

        order.RemoveAt(from);
        order.Insert(targetIndex, id);
        settings.Set(QuickPanelSettings.ItemOrder, string.Join(',', order));
    }

    /// <summary>The index of a tool in the full stored order (hidden tools included).</summary>
    public int IndexOf(string id) => Ordered(includeHidden: true).FindIndex(t => t.Id == id);

    public void Reset()
    {
        settings.Set(QuickPanelSettings.ItemOrder, string.Empty);
        settings.Set(QuickPanelSettings.HiddenItems, string.Empty);
    }

    /// <summary>Live state: a switch that is on, or a toggle action that reports on.</summary>
    public bool IsLive(QuickPanelTile tile) => tile.Switch is { } setting ? settings.Get(setting) : tile.IsOn?.Invoke() ?? false;

    public string Title(QuickPanelTile tile) => L.Get(tile.TitleKey);
}
