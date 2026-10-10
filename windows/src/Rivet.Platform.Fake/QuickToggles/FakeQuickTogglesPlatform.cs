// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Toggles;

namespace Rivet.Platform.Fake.Toggles;

/// <summary>Remembers toggles in memory; system actions only log.</summary>
public sealed class FakeQuickTogglesPlatform : IQuickTogglesPlatform
{
    public List<string> Actions { get; } = [];

    public bool IsDarkMode { get; private set; }

    public bool HiddenFilesShown { get; private set; }

    public bool FileExtensionsShown { get; private set; } = true;

    public bool DesktopIconsHidden { get; private set; }

    public bool IsScreenSaverConfigured => true;

    public long BinItems { get; set; } = 12;

    public bool SetDarkMode(bool dark) => Record($"darkMode={dark}", () => IsDarkMode = dark);

    public bool SetHiddenFilesShown(bool show) => Record($"hiddenFiles={show}", () => HiddenFilesShown = show);

    public bool SetFileExtensionsShown(bool show) => Record($"extensions={show}", () => FileExtensionsShown = show);

    public bool SetDesktopIconsHidden(bool hide) => Record($"desktopIcons={hide}", () => DesktopIconsHidden = hide);

    public (long Items, long Bytes)? RecycleBinInfo() => (BinItems, BinItems * 1_500_000);

    public bool EmptyRecycleBin() => Record("emptyBin", () => BinItems = 0);

    public bool LockScreen() => Record("lock", () => { });

    public bool StartScreenSaver() => Record("screenSaver", () => { });

    public bool TurnOffDisplay() => Record("displayOff", () => { });

    public bool Sleep() => Record("sleep", () => { });

    public IReadOnlyList<EjectableVolume> EjectableVolumes(IReadOnlyCollection<string> excluded) =>
        new[] { new EjectableVolume("E:\\", "USB DRIVE", null) }
            .Where(v => !excluded.Contains(v.Label, StringComparer.OrdinalIgnoreCase) && !excluded.Contains(v.Root, StringComparer.OrdinalIgnoreCase))
            .ToList();

    public Task<EjectResult> EjectAllAsync(IReadOnlyCollection<string> excluded)
    {
        var volumes = EjectableVolumes(excluded);
        Actions.Add("eject:" + volumes.Count);
        return Task.FromResult(new EjectResult(volumes.Count, 0, []));
    }

    private bool Record(string action, Action apply)
    {
        apply();
        Actions.Add(action);
        Rivet.Core.Diagnostics.Log.Info("quickToggles", "[fake] " + action);
        return true;
    }
}

public sealed class FakeQuickTogglesRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeQuickTogglesPlatform>();
        services.AddSingleton<IQuickTogglesPlatform>(sp => sp.GetRequiredService<FakeQuickTogglesPlatform>());
    }
}
