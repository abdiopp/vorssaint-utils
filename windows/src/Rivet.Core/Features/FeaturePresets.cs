// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;
using static Rivet.Core.Features.FeatureIds;

namespace Rivet.Core.Features;

public sealed record FeaturePreset(
    string Id,
    string Icon,
    string NameKey,
    string DescriptionKey,
    IReadOnlyList<string> Features,
    IReadOnlyList<Setting<bool>> EnableKeys);

/// <summary>The hub's one-click bundles. A clean install starts from Essentials.</summary>
public static class FeaturePresets
{
    public static FeaturePreset Essentials { get; } = new(
        "essentials", "Star", "hub.presetEssentialName", "win.shell.presetEssentialsDescription",
        [Screenshot, ScreenRecorder, Cleaner, Mixer, KeepAwake,
         MonitorCpu, MonitorGpu, MonitorMemory, MonitorNetwork, MonitorDisk, MonitorPower],
        []);

    public static FeaturePreset Creator { get; } = new(
        "creator", "VideoClip", "win.shell.presetCreatorName", "win.shell.presetCreatorDescription",
        [Screenshot, ScreenRecorder, ScreenOcr, ColorPicker, CameraPreview, MediaTools,
         ClipboardHistory, Shelf, Scratchpad],
        [FeatureKeys.ClipboardHistoryEnabled]);

    public static FeaturePreset BatteryAndQuiet { get; } = new(
        "battery", "Battery7", "hub.presetBatteryName", "hub.presetBatteryDesc",
        [MonitorCpu, MonitorMemory, MonitorPower],
        []);

    public static IReadOnlyList<FeaturePreset> All { get; } = [Essentials, Creator, BatteryAndQuiet];
}
