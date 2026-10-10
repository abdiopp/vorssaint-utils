// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Toggles;

public enum QuickToggleId
{
    DarkMode,
    MicMute,
    EmptyTrash,
    EjectDisks,
    HiddenFiles,
    FileExtensions,
    DesktopIcons,
    LockScreen,
    DisplayOff,
    ScreenSaver,
    Sleep,
}

public enum QuickToggleState
{
    Idle,
    Running,

    /// <summary>"Could not complete." for 2.4 s.</summary>
    Failed,
}

/// <summary>Quick toggles settings: order and per-row visibility (keys match the macOS app).</summary>
public static class QuickToggleSettings
{
    public static readonly Setting<string> Order = new("panelToggleOrder", string.Empty);

    public static readonly Setting<List<string>> ExcludedVolumes = new("diskEjectExcludedVolumes", [],
        list => list.Select(v => v?.Trim() ?? string.Empty).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    private static readonly Dictionary<QuickToggleId, Setting<bool>> Visibility = new()
    {
        [QuickToggleId.DarkMode] = new("panelToggleDarkMode", true),
        [QuickToggleId.MicMute] = new("panelUtilityMicMute", true),
        [QuickToggleId.EmptyTrash] = new("panelToggleEmptyTrash", true),
        [QuickToggleId.EjectDisks] = new("panelToggleEjectDisks", true),
        [QuickToggleId.HiddenFiles] = new("panelToggleHiddenFiles", true),
        [QuickToggleId.FileExtensions] = new("panelToggleFileExtensions", true),
        [QuickToggleId.DesktopIcons] = new("panelToggleDesktopIcons", true),
        [QuickToggleId.LockScreen] = new("panelToggleLockScreen", true),
        [QuickToggleId.DisplayOff] = new("panelToggleDisplayOff", true),
        [QuickToggleId.ScreenSaver] = new("panelToggleScreenSaver", true),
        [QuickToggleId.Sleep] = new("panelToggleSleep", true),
    };

    public static Setting<bool> VisibilityOf(QuickToggleId id) => Visibility[id];

    public static IEnumerable<SettingDefinition> AllVisibilityKeys => Visibility.Values;

    /// <summary>The persisted id ("darkMode", "emptyTrash"…).</summary>
    public static string StorageId(QuickToggleId id)
    {
        var name = id.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Stored order: known ids once in stored order, then every missing id (new ones join at the end).</summary>
    public static IReadOnlyList<QuickToggleId> ParseOrder(string stored)
    {
        var all = Enum.GetValues<QuickToggleId>();
        var result = new List<QuickToggleId>();
        foreach (var token in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = all.Cast<QuickToggleId?>().FirstOrDefault(id => StorageId(id!.Value) == token);
            if (match is { } id && !result.Contains(id))
            {
                result.Add(id);
            }
        }

        result.AddRange(all.Where(id => !result.Contains(id)));
        return result;
    }

    public static string FormatOrder(IEnumerable<QuickToggleId> order) => string.Join(',', order.Select(StorageId));
}

/// <summary>
/// Runs quick toggles with the row state machine (spec 06 §3.10): a running
/// row ignores repeated clicks, success clears at once (the new title is the
/// feedback), failure shows "Could not complete." for 2.4 s. Nothing polls.
/// </summary>
public sealed class QuickTogglesService
{
    public static readonly TimeSpan FailureDuration = TimeSpan.FromSeconds(2.4);

    private readonly IQuickTogglesPlatform _platform;
    private readonly ISettingsStore _settings;
    private readonly Dictionary<QuickToggleId, QuickToggleState> _states = [];

    public QuickTogglesService(IQuickTogglesPlatform platform, ISettingsStore settings)
    {
        _platform = platform;
        _settings = settings;
    }

    /// <summary>Raised on the UI thread when a row's state or the system state it shows changed.</summary>
    public event EventHandler? Changed;

    public IQuickTogglesPlatform Platform => _platform;

    public QuickToggleState StateOf(QuickToggleId id) => _states.GetValueOrDefault(id);

    public IReadOnlyCollection<string> ExcludedVolumes => _settings.Get(QuickToggleSettings.ExcludedVolumes);

    /// <summary>Runs one toggle's work off the UI thread and tracks its state.</summary>
    public async Task<bool> RunAsync(QuickToggleId id, Func<bool> work)
    {
        if (StateOf(id) == QuickToggleState.Running)
        {
            return false;
        }

        SetState(id, QuickToggleState.Running);
        bool ok;
        try
        {
            ok = await Task.Run(work).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("quickToggles", $"Quick toggle {id} failed.", ex);
            ok = false;
        }

        if (ok)
        {
            SetState(id, QuickToggleState.Idle);
        }
        else
        {
            SetState(id, QuickToggleState.Failed);
            _ = Task.Delay(FailureDuration).ContinueWith(_ => UiThread.Post(() =>
            {
                if (StateOf(id) == QuickToggleState.Failed)
                {
                    SetState(id, QuickToggleState.Idle);
                }
            }), TaskScheduler.Default);
        }

        return ok;
    }

    public Task<bool> ToggleDarkModeAsync() => RunAsync(QuickToggleId.DarkMode, () => _platform.SetDarkMode(!_platform.IsDarkMode));

    public Task<bool> ToggleHiddenFilesAsync() => RunAsync(QuickToggleId.HiddenFiles, () => _platform.SetHiddenFilesShown(!_platform.HiddenFilesShown));

    public Task<bool> ToggleFileExtensionsAsync() => RunAsync(QuickToggleId.FileExtensions, () => _platform.SetFileExtensionsShown(!_platform.FileExtensionsShown));

    public Task<bool> ToggleDesktopIconsAsync() => RunAsync(QuickToggleId.DesktopIcons, () => _platform.SetDesktopIconsHidden(!_platform.DesktopIconsHidden));

    /// <summary>Empties the bin; call only after the user confirmed.</summary>
    public Task<bool> EmptyRecycleBinAsync() => RunAsync(QuickToggleId.EmptyTrash, _platform.EmptyRecycleBin);

    public async Task<bool> EjectAllAsync()
    {
        if (StateOf(QuickToggleId.EjectDisks) == QuickToggleState.Running)
        {
            return false;
        }

        SetState(QuickToggleId.EjectDisks, QuickToggleState.Running);
        EjectResult result;
        try
        {
            result = await _platform.EjectAllAsync(ExcludedVolumes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("quickToggles", "Eject all disks failed.", ex);
            result = new EjectResult(0, 1, []);
        }

        SetState(QuickToggleId.EjectDisks, result.Success ? QuickToggleState.Idle : QuickToggleState.Failed);
        if (!result.Success)
        {
            _ = Task.Delay(FailureDuration).ContinueWith(_ => UiThread.Post(() => SetState(QuickToggleId.EjectDisks, QuickToggleState.Idle)), TaskScheduler.Default);
        }

        return result.Success;
    }

    /// <summary>Lock, display off, screen saver and sleep run 0.15 s after the hosting surface closed.</summary>
    public async Task<bool> RunAfterSurfaceClosedAsync(QuickToggleId id)
    {
        await Task.Delay(150).ConfigureAwait(true);
        Func<bool> work = id switch
        {
            QuickToggleId.LockScreen => () => _platform.LockScreen() || _platform.StartScreenSaver(),
            QuickToggleId.DisplayOff => _platform.TurnOffDisplay,
            QuickToggleId.ScreenSaver => _platform.StartScreenSaver,
            QuickToggleId.Sleep => _platform.Sleep,
            _ => () => false,
        };
        return await RunAsync(id, work).ConfigureAwait(true);
    }

    public void NotifyChanged() => UiThread.Run(() => Changed?.Invoke(this, EventArgs.Empty));

    private void SetState(QuickToggleId id, QuickToggleState state)
    {
        _states[id] = state;
        NotifyChanged();
    }
}
