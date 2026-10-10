// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;
using Rivet.Core.Toggles;
using Xunit;

namespace Rivet.Core.Tests.QuickToggles;

/// <summary>Spec 06 §3.10: row order, visibility keys and the row state machine.</summary>
public class QuickToggleTests
{
    [Fact]
    public void Storage_ids_match_the_macos_keys()
    {
        Assert.Equal("darkMode", QuickToggleSettings.StorageId(QuickToggleId.DarkMode));
        Assert.Equal("emptyTrash", QuickToggleSettings.StorageId(QuickToggleId.EmptyTrash));
        Assert.Equal("panelToggleOrder", QuickToggleSettings.Order.Key);
        Assert.Equal("panelToggleDarkMode", QuickToggleSettings.VisibilityOf(QuickToggleId.DarkMode).Key);
        Assert.Equal("panelUtilityMicMute", QuickToggleSettings.VisibilityOf(QuickToggleId.MicMute).Key);
        Assert.All(Enum.GetValues<QuickToggleId>(), id => Assert.True(QuickToggleSettings.VisibilityOf(id).Default));
    }

    [Fact]
    public void Order_keeps_stored_ids_and_appends_new_ones()
    {
        var order = QuickToggleSettings.ParseOrder("lockScreen, darkMode,bogus,darkMode");
        Assert.Equal(QuickToggleId.LockScreen, order[0]);
        Assert.Equal(QuickToggleId.DarkMode, order[1]);
        Assert.Equal(Enum.GetValues<QuickToggleId>().Length, order.Count);
        Assert.Equal(order, QuickToggleSettings.ParseOrder(QuickToggleSettings.FormatOrder(order)));
    }

    [Fact]
    public async Task Failure_sets_failed_and_success_clears()
    {
        var service = new QuickTogglesService(new NullPlatform(), SettingsStore.InMemory());
        Assert.False(await service.RunAsync(QuickToggleId.DarkMode, () => false));
        Assert.Equal(QuickToggleState.Failed, service.StateOf(QuickToggleId.DarkMode));
        Assert.True(await service.RunAsync(QuickToggleId.DarkMode, () => true));
        Assert.Equal(QuickToggleState.Idle, service.StateOf(QuickToggleId.DarkMode));
        Assert.False(await service.RunAsync(QuickToggleId.HiddenFiles, () => throw new InvalidOperationException()));
        Assert.Equal(QuickToggleState.Failed, service.StateOf(QuickToggleId.HiddenFiles));
    }

    [Fact]
    public async Task A_running_row_ignores_repeated_clicks()
    {
        var service = new QuickTogglesService(new NullPlatform(), SettingsStore.InMemory());
        var gate = new ManualResetEventSlim();
        var first = service.RunAsync(QuickToggleId.EmptyTrash, () => gate.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(QuickToggleState.Running, service.StateOf(QuickToggleId.EmptyTrash));
        var calls = 0;
        Assert.False(await service.RunAsync(QuickToggleId.EmptyTrash, () => ++calls > 0));
        Assert.Equal(0, calls);
        gate.Set();
        Assert.True(await first);
    }

    private sealed class NullPlatform : IQuickTogglesPlatform
    {
        public bool IsDarkMode => false;

        public bool HiddenFilesShown => false;

        public bool FileExtensionsShown => false;

        public bool DesktopIconsHidden => false;

        public bool IsScreenSaverConfigured => false;

        public bool SetDarkMode(bool dark) => true;

        public bool SetHiddenFilesShown(bool show) => true;

        public bool SetFileExtensionsShown(bool show) => true;

        public bool SetDesktopIconsHidden(bool hide) => true;

        public (long Items, long Bytes)? RecycleBinInfo() => null;

        public bool EmptyRecycleBin() => true;

        public bool LockScreen() => true;

        public bool StartScreenSaver() => true;

        public bool TurnOffDisplay() => true;

        public bool Sleep() => true;

        public IReadOnlyList<EjectableVolume> EjectableVolumes(IReadOnlyCollection<string> excluded) => [];

        public Task<EjectResult> EjectAllAsync(IReadOnlyCollection<string> excluded) => Task.FromResult(new EjectResult(0, 0, []));
    }
}
