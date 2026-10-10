// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Toggles;

/// <summary>A volume "Eject all disks" would eject.</summary>
public sealed record EjectableVolume(string Root, string Label, string? VolumeGuid);

public sealed record EjectResult(int Ejected, int Failed, IReadOnlyList<string> FailedVolumes)
{
    public bool Success => Failed == 0;
}

/// <summary>
/// The Windows side of the quick toggles (spec 06 §7.5): registry plus
/// broadcast for dark mode, Explorer's Advanced keys for hidden files,
/// extensions and desktop icons (applied live, no restart), the Recycle Bin,
/// lock, screen saver, display off, sleep and safe removal of external disks.
/// </summary>
public interface IQuickTogglesPlatform
{
    /// <summary>Apps and Windows both use the dark theme.</summary>
    bool IsDarkMode { get; }

    /// <summary>Sets AppsUseLightTheme and SystemUsesLightTheme, then broadcasts ImmersiveColorSet.</summary>
    bool SetDarkMode(bool dark);

    bool HiddenFilesShown { get; }

    bool SetHiddenFilesShown(bool show);

    bool FileExtensionsShown { get; }

    bool SetFileExtensionsShown(bool show);

    bool DesktopIconsHidden { get; }

    bool SetDesktopIconsHidden(bool hide);

    /// <summary>Items and bytes in the Recycle Bin (all drives), or null when unknown.</summary>
    (long Items, long Bytes)? RecycleBinInfo();

    /// <summary>Empties without Windows' own confirmation (the app asked first), no progress UI or sound.</summary>
    bool EmptyRecycleBin();

    bool LockScreen();

    /// <summary>Whether a screen saver is configured (none means "Start the screen saver" fails).</summary>
    bool IsScreenSaverConfigured { get; }

    bool StartScreenSaver();

    bool TurnOffDisplay();

    bool Sleep();

    /// <summary>Volumes that qualify: removable, optical, or fixed on USB/1394/SD/MMC; never the system drive.</summary>
    IReadOnlyList<EjectableVolume> EjectableVolumes(IReadOnlyCollection<string> excluded);

    Task<EjectResult> EjectAllAsync(IReadOnlyCollection<string> excluded);
}
