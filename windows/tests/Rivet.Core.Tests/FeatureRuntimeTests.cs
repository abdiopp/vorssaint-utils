// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests;

public class FeatureRuntimeTests
{
    [Fact]
    public void Catalog_ids_are_unique_and_keys_are_consistent()
    {
        Assert.Equal(FeatureCatalog.All.Count, FeatureCatalog.All.Select(f => f.Id).Distinct().Count());
        foreach (var preset in FeaturePresets.All)
        {
            Assert.All(preset.Features, id => Assert.NotNull(FeatureCatalog.Find(id)));
        }
    }

    [Fact]
    public void Clean_install_starts_from_essentials()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        runtime.PrepareFirstRunAvailability();
        foreach (var feature in FeatureCatalog.All)
        {
            Assert.Equal(FeaturePresets.Essentials.Features.Contains(feature.Id), runtime.IsAvailable(feature));
            Assert.True(store.IsSaved(feature.AvailabilityKey));
        }
    }

    [Fact]
    public void First_run_preparation_stops_after_onboarding()
    {
        var store = SettingsStore.InMemory();
        store.Set(ShellSettings.HasOnboarded, true);
        var runtime = new FeatureRuntime(store);
        runtime.PrepareFirstRunAvailability();
        Assert.False(store.IsSaved(FeatureCatalog.Get(FeatureIds.Screenshot).AvailabilityKey));
    }

    [Fact]
    public void First_install_switches_on_the_primary_key_once()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        runtime.SetAvailable(FeatureIds.ClipboardHistory, false);
        runtime.SetAvailable(FeatureIds.ClipboardHistory, true);
        Assert.True(store.Get(FeatureKeys.ClipboardHistoryEnabled));

        store.Set(FeatureKeys.ClipboardHistoryEnabled, false);
        runtime.SetAvailable(FeatureIds.ClipboardHistory, false);
        runtime.SetAvailable(FeatureIds.ClipboardHistory, true);
        Assert.False(store.Get(FeatureKeys.ClipboardHistoryEnabled));
    }

    [Fact]
    public void Audio_priority_enables_both_lists()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        runtime.SetAvailable(FeatureIds.AudioPriority, true);
        Assert.True(store.Get(FeatureKeys.AudioPriorityOutputEnabled));
        Assert.True(store.Get(FeatureKeys.AudioPriorityInputEnabled));
    }

    [Fact]
    public void Controllers_follow_availability_and_enable_keys()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        var calls = new List<bool>();
        runtime.RegisterController(FeatureIds.SmoothScroll, new DelegateFeatureController(calls.Add));
        runtime.SyncAtLaunch();
        var before = calls.Count;
        store.Set(FeatureKeys.SmoothScrollEnabled, true);
        Assert.True(calls.Count > before);
        runtime.SetAvailable(FeatureIds.SmoothScroll, false);
        Assert.False(calls[^1]);
    }

    [Fact]
    public void Uninstalling_a_loaded_feature_asks_for_a_restart()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        runtime.SetAvailable(FeatureIds.Screenshot, true);
        runtime.SyncAtLaunch();
        Assert.False(runtime.NeedsRestartToUnload);
        runtime.SetAvailable(FeatureIds.Screenshot, false);
        Assert.True(runtime.NeedsRestartToUnload);
    }

    [Fact]
    public void Never_switched_on_lists_untouched_installed_features()
    {
        var store = SettingsStore.InMemory();
        foreach (var feature in FeatureCatalog.All)
        {
            store.Set(new Setting<bool>(feature.AvailabilityKey, feature.InstalledByDefault), true);
        }

        var runtime = new FeatureRuntime(store);
        runtime.SyncAtLaunch();
        var offered = runtime.NeverSwitchedOn().Select(f => f.Id).ToList();
        Assert.Contains(FeatureIds.ScrollInverter, offered);
        Assert.Contains(FeatureIds.KeyboardDebounce, offered);

        store.Set(FeatureKeys.KeyboardDebounceEnabled, false);
        Assert.DoesNotContain(FeatureIds.KeyboardDebounce, runtime.NeverSwitchedOn().Select(f => f.Id));

        runtime.KeepFeatures([FeatureIds.ScrollInverter]);
        Assert.DoesNotContain(FeatureIds.ScrollInverter, runtime.NeverSwitchedOn().Select(f => f.Id));
    }

    [Fact]
    public void Presets_replace_the_installed_set()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        runtime.ReplaceAvailable(FeaturePresets.Creator.Features, FeaturePresets.Creator.EnableKeys);
        Assert.True(runtime.IsAvailable(FeatureIds.ScreenRecorder));
        Assert.False(runtime.IsAvailable(FeatureIds.Cleaner));
        Assert.True(store.Get(FeatureKeys.ClipboardHistoryEnabled));
        Assert.Equal(FeaturePresets.Creator.Features.Count, runtime.AvailableCount);
    }

    [Fact]
    public void Hardware_gate_refuses_installs_but_never_revokes()
    {
        var store = SettingsStore.InMemory();
        var allow = true;
        var runtime = new FeatureRuntime(store, f => f.Id != FeatureIds.Brightness || allow);
        Assert.True(runtime.SetAvailable(FeatureIds.Brightness, true));
        allow = false;
        Assert.True(runtime.IsAvailable(FeatureIds.Brightness));
        Assert.True(runtime.SetAvailable(FeatureIds.Brightness, false));
        Assert.False(runtime.SetAvailable(FeatureIds.Brightness, true));
    }
}
