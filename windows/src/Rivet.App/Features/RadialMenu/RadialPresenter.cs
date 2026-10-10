// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.RadialMenu;

/// <summary>
/// Labels, icons and tool resolution for slices (spec 07 §3.2.9), shared by the
/// wheel and the settings canvas. App and file icons and display names are
/// cached per path for the process lifetime, so tracking the pointer never
/// touches the disk.
/// </summary>
public sealed class RadialPresenter(IServiceProvider services)
{
    /// <summary>Tool payloads of features whose actions ids differ from "&lt;feature&gt;.open".</summary>
    private static readonly string[] PreferredVerbs = ["open", "toggle", "start", "show", "capture", "pick", "run"];

    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int), Bitmap?> _icons = [];
    private readonly Dictionary<string, Bitmap?> _custom = new(StringComparer.Ordinal);
    private ActionRegistry Actions => services.GetRequiredService<ActionRegistry>();
    private IRadialPlatform Platform => services.GetRequiredService<IRadialPlatform>();

    // ── Tools ───────────────────────────────────────────────────────────

    /// <summary>
    /// The app action a tool slice runs: the payload itself when it is an action id,
    /// else the feature's "open"/"toggle"/… action, else its first action.
    /// </summary>
    public AppAction? ResolveTool(string payload)
    {
        if (Actions.Get(payload) is { } direct)
        {
            return direct;
        }

        var feature = RadialToolIds.FeatureFor(payload);
        foreach (var verb in PreferredVerbs)
        {
            if (Actions.Get($"{feature}.{verb}") is { } action && action.FeatureId == feature)
            {
                return action;
            }
        }

        return Actions.All.FirstOrDefault(a => a.FeatureId == feature);
    }

    /// <summary>A quick toggle runs the Quick toggles feature's action "quickToggles.&lt;id&gt;".</summary>
    public AppAction? ResolveQuickToggle(string id) => Actions.Get($"{FeatureIds.QuickToggles}.{id}");

    /// <summary>Whether a slice can run now (session-start filter, §3.2.5).</summary>
    public bool IsRunnable(RadialItem item)
    {
        switch (item.Kind)
        {
            case RadialItemKind.Tool:
                if (ResolveTool(item.Payload) is not { } tool || !Actions.IsEnabled(tool))
                {
                    return false;
                }

                // The Shelf slice also needs the shelf switched on.
                return tool.FeatureId != FeatureIds.Shelf || services.GetRequiredService<ISettingsStore>().Get(FeatureKeys.ShelfEnabled);
            case RadialItemKind.QuickToggle:
                return ResolveQuickToggle(item.Payload) is { } toggle && Actions.IsEnabled(toggle);
            default:
                return true;
        }
    }

    /// <summary>Drops slices that cannot run, recursively; empty submenus go too.</summary>
    public IReadOnlyList<RadialItem> Filter(IReadOnlyList<RadialItem> items) =>
        items.Select(i => i.IsSubmenu ? i with { Children = Filter(i.Children) } : i)
            .Where(i => i.IsSubmenu ? i.Children.Count > 0 : IsRunnable(i))
            .ToList();

    // ── Labels and icons ────────────────────────────────────────────────

    public string Label(RadialItem item, NowPlayingState nowPlaying = NowPlayingState.NothingPlaying, NowPlayingSnapshot? snapshot = null) =>
        RadialLabels.Label(item, L.Get, DisplayName, ToolTitle, services.GetService<IKeyNameProvider>(), nowPlaying, snapshot);

    public string Symbol(RadialItem item) => RadialLabels.AutomaticSymbol(item, payload => ResolveTool(payload)?.Icon);

    public string DisplayName(string path)
    {
        if (_names.TryGetValue(path, out var cached))
        {
            return cached;
        }

        string name;
        try
        {
            name = Platform.DisplayName(Platform.ExpandPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            name = Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\'));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(path.TrimEnd('/', '\\'));
        }

        _names[path] = name;
        return name;
    }

    private string? ToolTitle(string payload) => ResolveTool(payload) is { } action ? L.Get(action.TitleKey) : null;

    /// <summary>
    /// The chip image: a decodable custom image (without a custom symbol), else the
    /// real app/file icon for app and file slices without a custom symbol; null = draw the symbol.
    /// </summary>
    public Bitmap? Image(RadialItem item, int sizePx)
    {
        if (item.SymbolName.Length > 0)
        {
            return null;
        }

        if (item.CustomIconData is { Length: > 0 } data)
        {
            if (!_custom.TryGetValue(data, out var custom))
            {
                try
                {
                    using var stream = new MemoryStream(Convert.FromBase64String(data));
                    custom = new Bitmap(stream);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IOException)
                {
                    custom = null;
                }

                _custom[data] = custom;
            }

            if (custom is not null)
            {
                return custom;
            }
        }

        if (item.Kind is not (RadialItemKind.App or RadialItemKind.File) || item.Payload.Length == 0)
        {
            return null;
        }

        var key = (item.Payload, sizePx);
        if (_icons.TryGetValue(key, out var icon))
        {
            return icon;
        }

        try
        {
            var buffer = Platform.Icon(Platform.ExpandPath(item.Payload), sizePx);
            icon = buffer is null ? null : ImageInterop.ToBitmap(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            icon = null;
        }

        _icons[key] = icon;
        return icon;
    }

    /// <summary>The profile colour as 0xAARRGGBB for the current theme ("accent" follows the Windows accent).</summary>
    public uint ColorValue(RadialColor color, bool dark) =>
        RadialLabels.ColorValue(color, dark, services.GetService<Rivet.Core.Platform.IThemeService>()?.AccentColor ?? 0xFF0067C0);
}
