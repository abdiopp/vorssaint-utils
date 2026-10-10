// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Controls;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Toggles;

namespace Rivet.App.Features.Toggles;

/// <summary>
/// What each quick toggle row shows and does (spec 06 §3.10, §7.5): titles
/// that follow the current system state, captions, icons, availability and
/// the run flow (Recycle Bin confirmation, closing the surface first for lock,
/// display off, screen saver and sleep). Shared by the tray section, the
/// quick panel and the Command Bar.
/// </summary>
public sealed class QuickToggleCatalog
{
    /// <summary>The microphone feature's action (another module); the row exists only while it is registered.</summary>
    public const string MicMuteActionId = "micMute.toggle";

    private readonly ActionRegistry _actions;
    private readonly ISettingsStore _settings;
    private readonly IHud? _hud;
    private IReadOnlyList<EjectableVolume>? _ejectable;

    public QuickToggleCatalog(QuickTogglesService service, ActionRegistry actions, ISettingsStore settings, IHud? hud = null)
    {
        Service = service;
        _actions = actions;
        _settings = settings;
        _hud = hud;
    }

    public QuickTogglesService Service { get; }

    private IQuickTogglesPlatform Platform => Service.Platform;

    /// <summary>The stored order (unknown ids dropped, new ones appended).</summary>
    public IReadOnlyList<QuickToggleId> Order => QuickToggleSettings.ParseOrder(_settings.Get(QuickToggleSettings.Order));

    public bool IsAvailable(QuickToggleId id) => id switch
    {
        QuickToggleId.MicMute => _actions.Get(MicMuteActionId) is { } action && _actions.IsEnabled(action),
        _ => true,
    };

    public bool IsVisible(QuickToggleId id) => _settings.Get(QuickToggleSettings.VisibilityOf(id));

    /// <summary>Rows in order: available ones, and hidden ones too while editing.</summary>
    public IReadOnlyList<QuickToggleId> Listed(bool includeHidden) =>
        Order.Where(IsAvailable).Where(id => includeHidden || IsVisible(id)).ToList();

    public void SetVisible(QuickToggleId id, bool visible) => _settings.Set(QuickToggleSettings.VisibilityOf(id), visible);

    /// <summary>Moves a row one step among the available rows.</summary>
    public void Move(QuickToggleId id, int delta)
    {
        var order = Order.ToList();
        var available = order.Where(IsAvailable).ToList();
        var index = available.IndexOf(id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= available.Count)
        {
            return;
        }

        var other = available[target];
        var a = order.IndexOf(id);
        var b = order.IndexOf(other);
        (order[a], order[b]) = (order[b], order[a]);
        _settings.Set(QuickToggleSettings.Order, QuickToggleSettings.FormatOrder(order));
    }

    /// <summary>Default order and every row visible.</summary>
    public void Reset()
    {
        _settings.Reset(QuickToggleSettings.Order.Key);
        foreach (var id in Enum.GetValues<QuickToggleId>())
        {
            _settings.Reset(QuickToggleSettings.VisibilityOf(id).Key);
        }
    }

    public string Title(QuickToggleId id) => id switch
    {
        QuickToggleId.DarkMode => L.Get(Read(() => Platform.IsDarkMode) ? "quickToggles.darkModeToLight" : "quickToggles.darkModeToDark"),
        QuickToggleId.MicMute => _actions.Get(MicMuteActionId) is { } action ? L.Get(action.TitleKey) : L.Get("Strings.micMuteName"),
        QuickToggleId.EmptyTrash => L.Get("quickToggles.emptyTrashTitle"),
        QuickToggleId.EjectDisks => L.Get("quickToggles.ejectTitle"),
        QuickToggleId.HiddenFiles => L.Get(Read(() => Platform.HiddenFilesShown) ? "quickToggles.hiddenFilesHide" : "quickToggles.hiddenFilesShow"),
        QuickToggleId.FileExtensions => L.Get(Read(() => Platform.FileExtensionsShown) ? "win.quickToggles.extensionsHide" : "win.quickToggles.extensionsShow"),
        QuickToggleId.DesktopIcons => L.Get(Read(() => Platform.DesktopIconsHidden) ? "quickToggles.desktopIconsShow" : "quickToggles.desktopIconsHide"),
        QuickToggleId.LockScreen => L.Get("quickToggles.lockScreenTitle"),
        QuickToggleId.DisplayOff => L.Get("quickToggles.displayOffTitle"),
        QuickToggleId.ScreenSaver => L.Get("quickToggles.screenSaverTitle"),
        _ => L.Get("commandBar.powerSleep"),
    };

    /// <summary>The idle caption (the row shows "Could not complete." instead while failed).</summary>
    public string Caption(QuickToggleId id) => id switch
    {
        QuickToggleId.DarkMode => L.Get("quickToggles.darkModeCaption"),
        QuickToggleId.MicMute => L.Get("win.quickToggles.micMuteCaption"),
        QuickToggleId.EmptyTrash => L.Get("quickToggles.emptyTrashCaption"),
        QuickToggleId.EjectDisks => Service.StateOf(id) == QuickToggleState.Running
            ? L.Get("Strings.diskEjecting")
            : _ejectable is { Count: 0 } ? L.Get("Strings.diskNoExternal") : L.Get("quickToggles.ejectCaption"),
        QuickToggleId.HiddenFiles or QuickToggleId.FileExtensions or QuickToggleId.DesktopIcons => L.Get("quickToggles.finderRestartCaption"),
        QuickToggleId.LockScreen => L.Get("quickToggles.lockScreenCaption"),
        QuickToggleId.DisplayOff => L.Get("quickToggles.displayOffCaption"),
        QuickToggleId.ScreenSaver => L.Get("quickToggles.screenSaverCaption"),
        _ => L.Get("win.quickToggles.sleepCaption"),
    };

    public string Icon(QuickToggleId id) => id switch
    {
        QuickToggleId.DarkMode => "DarkTheme",
        QuickToggleId.MicMute => "MicOff",
        QuickToggleId.EmptyTrash => "Delete",
        QuickToggleId.EjectDisks => "ArrowEject",
        QuickToggleId.HiddenFiles => Read(() => Platform.HiddenFilesShown) ? "EyeOff" : "Eye",
        QuickToggleId.FileExtensions => "DocumentText",
        QuickToggleId.DesktopIcons => "Desktop",
        QuickToggleId.LockScreen => "LockClosed",
        QuickToggleId.DisplayOff => "DesktopOff",
        QuickToggleId.ScreenSaver => "SlideMultiple",
        _ => "WeatherMoon",
    };

    /// <summary>Lock, display off, screen saver and sleep close the hosting surface first.</summary>
    public static bool ClosesSurface(QuickToggleId id) => id is QuickToggleId.LockScreen or QuickToggleId.DisplayOff or QuickToggleId.ScreenSaver or QuickToggleId.Sleep;

    /// <summary>Re-reads the external disks off the UI thread (each time a list appears; nothing polls).</summary>
    public async Task RefreshEjectableAsync()
    {
        var excluded = Service.ExcludedVolumes;
        try
        {
            _ejectable = await Task.Run(() => Platform.EjectableVolumes(excluded)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn("quickToggles", $"Listing external disks failed: {ex.GetType().Name}");
            _ejectable = null;
        }
    }

    /// <summary>
    /// Runs a row. <paramref name="closeSurface"/> hides the panel first where the
    /// action takes the screen away; <paramref name="owner"/> hosts the Recycle Bin confirmation.
    /// <paramref name="confirmed"/> skips that dialog (the Command Bar confirms inline).
    /// </summary>
    public async Task<bool> RunAsync(QuickToggleId id, Window? owner, Action? closeSurface, ActionSource source = ActionSource.Panel, bool confirmed = false)
    {
        switch (id)
        {
            case QuickToggleId.DarkMode:
                return await Service.ToggleDarkModeAsync().ConfigureAwait(true);
            case QuickToggleId.MicMute:
                return await _actions.InvokeAsync(MicMuteActionId, source).ConfigureAwait(true);
            case QuickToggleId.HiddenFiles:
                return await Service.ToggleHiddenFilesAsync().ConfigureAwait(true);
            case QuickToggleId.FileExtensions:
                return await Service.ToggleFileExtensionsAsync().ConfigureAwait(true);
            case QuickToggleId.DesktopIcons:
                return await Service.ToggleDesktopIconsAsync().ConfigureAwait(true);
            case QuickToggleId.EmptyTrash:
                return await EmptyRecycleBinAsync(owner, confirmed).ConfigureAwait(true);
            case QuickToggleId.EjectDisks:
                await RefreshEjectableAsync().ConfigureAwait(true);
                if (_ejectable is { Count: 0 })
                {
                    _hud?.Show(L.Get("Strings.diskNoExternal"), HudStyle.Info, "ArrowEject");
                    Service.NotifyChanged();
                    return true;
                }

                var ejected = await Service.EjectAllAsync().ConfigureAwait(true);
                await RefreshEjectableAsync().ConfigureAwait(true);
                Service.NotifyChanged();
                return ejected;
            default:
                closeSurface?.Invoke();
                return await Service.RunAfterSurfaceClosedAsync(id).ConfigureAwait(true);
        }
    }

    private async Task<bool> EmptyRecycleBinAsync(Window? owner, bool confirmed)
    {
        var info = await Task.Run(() => Read<(long Items, long Bytes)?>(Platform.RecycleBinInfo, null)).ConfigureAwait(true);
        if (info is { Items: 0 })
        {
            _hud?.Show(L.Get("win.quickToggles.binEmpty"), HudStyle.Info, "Delete");
            return true;
        }

        if (!confirmed)
        {
            var message = L.Get("quickToggles.emptyTrashConfirmMessage");
            if (info is { } known)
            {
                var size = FormatBytes(known.Bytes);
                message += "\n\n" + (known.Items == 1 ? L.Format("win.quickToggles.binContentsOne", size) : L.Format("win.quickToggles.binContentsFormat", known.Items, size));
            }

            if (!await ConfirmDialog.ShowAsync(owner, L.Get("quickToggles.emptyTrashConfirmTitle"), message, L.Get("quickToggles.emptyTrashConfirmButton")).ConfigureAwait(true))
            {
                return false;
            }
        }

        return await Service.EmptyRecycleBinAsync().ConfigureAwait(true);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn("quickToggles", $"Reading a system setting failed: {ex.GetType().Name}");
            return fallback;
        }
    }

    private static bool Read(Func<bool> read) => Read(read, false);
}
