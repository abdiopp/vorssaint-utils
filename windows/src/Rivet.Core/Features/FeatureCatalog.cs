// SPDX-License-Identifier: GPL-3.0-or-later
using static Rivet.Core.Features.FeatureIds;

namespace Rivet.Core.Features;

/// <summary>
/// Every feature of the Windows app. macOS-only features (Dock, Finder,
/// Spaces, window snapping, the app switcher and other things Windows does
/// natively) are not here. Title and description keys reuse the macOS string
/// catalog, so the hub is translated in all 15 languages; descriptions that
/// mention Mac-only concepts get a Windows rewording in <c>i18n-win/shell.*.json</c>.
/// </summary>
public static class FeatureCatalog
{
    public static IReadOnlyList<FeatureDescriptor> All { get; } =
    [
        // ── Capture and create ──────────────────────────────────────────
        new() { Id = Screenshot, Group = FeatureGroup.Capture, Icon = "Screenshot",
            TitleKey = "screenshot.pageTitle", DescriptionKey = "screenshot.hubDescription" },
        new() { Id = ScreenRecorder, Group = FeatureGroup.Capture, Icon = "Record",
            TitleKey = "recorder.pageTitle", DescriptionKey = "recorder.hubDescription",
            Capabilities = [Capability.Microphone] },
        new() { Id = ScreenOcr, Group = FeatureGroup.Capture, Icon = "ScanText",
            TitleKey = "Strings.ocrName", DescriptionKey = "hub.descScreenOCR" },
        new() { Id = ColorPicker, Group = FeatureGroup.Capture, Icon = "Eyedropper",
            TitleKey = "Strings.colorPickerName", DescriptionKey = "hub.descColorPicker" },
        new() { Id = CameraPreview, Group = FeatureGroup.Capture, Icon = "Camera",
            TitleKey = "cameraPreview.pageTitle", DescriptionKey = "cameraPreview.hubDescription",
            Capabilities = [Capability.Camera] },
        new() { Id = MediaTools, Group = FeatureGroup.Capture, Icon = "ImageMultiple",
            TitleKey = "Strings.mediaName", DescriptionKey = "hub.descMediaTools" },

        // ── System monitor ──────────────────────────────────────────────
        new() { Id = MonitorCpu, Group = FeatureGroup.Monitor, Icon = "DeveloperBoard",
            TitleKey = "Strings.cpuLabel", DescriptionKey = "hub.descMonitorCPU",
            Energy = EnergyProfile.Periodic, Capabilities = [Capability.Notifications, Capability.Administrator] },
        new() { Id = MonitorGpu, Group = FeatureGroup.Monitor, Icon = "DeveloperBoardSearch",
            TitleKey = "Strings.gpuLabel", DescriptionKey = "hub.descMonitorGPU",
            Energy = EnergyProfile.Periodic },
        new() { Id = MonitorMemory, Group = FeatureGroup.Monitor, Icon = "Ram",
            TitleKey = "Strings.memorySection", DescriptionKey = "hub.descMonitorMemory",
            Energy = EnergyProfile.Periodic, Capabilities = [Capability.Notifications] },
        new() { Id = MonitorNetwork, Group = FeatureGroup.Monitor, Icon = "Globe",
            TitleKey = "Strings.networkSection", DescriptionKey = "hub.descMonitorNetwork",
            Energy = EnergyProfile.Periodic },
        new() { Id = MonitorDisk, Group = FeatureGroup.Monitor, Icon = "Storage",
            TitleKey = "Strings.diskSection", DescriptionKey = "hub.descMonitorDisk",
            Energy = EnergyProfile.Periodic, Capabilities = [Capability.Notifications] },
        new() { Id = MonitorPower, Group = FeatureGroup.Monitor, Icon = "Flash",
            TitleKey = "Strings.powerSection", DescriptionKey = "hub.descMonitorPower",
            Energy = EnergyProfile.Periodic, Capabilities = [Capability.Notifications] },
        new() { Id = ConnectedDevices, Group = FeatureGroup.Monitor, Icon = "UsbPlug",
            TitleKey = "connectedDevices.title", DescriptionKey = "connectedDevices.hubDescription",
            Energy = EnergyProfile.Periodic },

        // ── Tools ───────────────────────────────────────────────────────
        new() { Id = CommandBar, Group = FeatureGroup.Tools, Icon = "Search",
            TitleKey = "commandBar.pageTitle", DescriptionKey = "commandBar.hubDescription" },
        new() { Id = QuickLauncher, Group = FeatureGroup.Tools, Icon = "Grid",
            TitleKey = "Strings.launcherName", DescriptionKey = "hub.descQuickLauncher" },
        new() { Id = QuickToggles, Group = FeatureGroup.Tools, Icon = "ToggleMultiple",
            TitleKey = "quickToggles.pageTitle", DescriptionKey = "quickToggles.hubDescription" },
        new() { Id = RadialMenu, Group = FeatureGroup.Tools, Icon = "CircleMultipleSubtractCheckmark",
            TitleKey = "radialMenu.pageTitle", DescriptionKey = "radialMenu.hubDescription",
            EnableKeys = [FeatureKeys.RadialMenuEnabled], Energy = EnergyProfile.Inputs },
        new() { Id = Scratchpad, Group = FeatureGroup.Tools, Icon = "Note",
            TitleKey = "scratchpad.pageTitle", DescriptionKey = "scratchpad.hubDescription" },
        new() { Id = CleaningMode, Group = FeatureGroup.Tools, Icon = "Broom",
            TitleKey = "Strings.cleaningMenuItem", DescriptionKey = "hub.descCleaningMode" },
        new() { Id = AgentUsage, Group = FeatureGroup.Tools, Icon = "Bot",
            TitleKey = "notchAgents.title", DescriptionKey = "win.shell.agentUsageDescription",
            Energy = EnergyProfile.Periodic, Capabilities = [Capability.Notifications] },

        // ── App management ──────────────────────────────────────────────
        new() { Id = Cleaner, Group = FeatureGroup.AppManagement, Icon = "Sparkle",
            TitleKey = "Strings.cleanerName", DescriptionKey = "win.shell.cleanerDescription",
            Capabilities = [Capability.Notifications, Capability.Administrator] },
        new() { Id = Uninstaller, Group = FeatureGroup.AppManagement, Icon = "Delete",
            TitleKey = "Strings.uninstallerName", DescriptionKey = "hub.descUninstaller",
            Capabilities = [Capability.Administrator] },
        new() { Id = AppUpdates, Group = FeatureGroup.AppManagement, Icon = "ArrowDownload",
            TitleKey = "appUpdates.pageTitle", DescriptionKey = "appUpdates.hubDescription",
            Energy = EnergyProfile.Periodic, Capabilities = [Capability.Notifications] },
        new() { Id = PackageManager, Group = FeatureGroup.AppManagement, Icon = "Box",
            TitleKey = "win.shell.packageManagerTitle", DescriptionKey = "win.shell.packageManagerDescription" },
        new() { Id = KillProcess, Group = FeatureGroup.AppManagement, Icon = "ErrorCircle",
            TitleKey = "killProcess.pageTitle", DescriptionKey = "killProcess.hubDescription",
            InstalledByDefault = false, IsBeta = true },
        new() { Id = PortManager, Group = FeatureGroup.AppManagement, Icon = "PlugConnected",
            TitleKey = "portManager.title", DescriptionKey = "portManager.hubDescription",
            InstalledByDefault = false },

        // ── Clipboard and files ─────────────────────────────────────────
        new() { Id = ClipboardHistory, Group = FeatureGroup.ClipboardFiles, Icon = "ClipboardPaste",
            TitleKey = "clipboard.title", DescriptionKey = "hub.descClipboardHistory",
            EnableKeys = [FeatureKeys.ClipboardHistoryEnabled], Energy = EnergyProfile.Periodic },
        new() { Id = PastePlain, Group = FeatureGroup.ClipboardFiles, Icon = "DocumentText",
            TitleKey = "Strings.pastePlainName", DescriptionKey = "hub.descPastePlain",
            EnableKeys = [FeatureKeys.PastePlainEnabled] },
        new() { Id = Shelf, Group = FeatureGroup.ClipboardFiles, Icon = "Archive",
            TitleKey = "Strings.shelfName", DescriptionKey = "win.shell.shelfDescription",
            EnableKeys = [FeatureKeys.ShelfEnabled], Energy = EnergyProfile.Mouse },
        new() { Id = UrlCleaner, Group = FeatureGroup.ClipboardFiles, Icon = "Link",
            TitleKey = "Strings.urlCleanerName", DescriptionKey = "hub.descURLCleaner",
            EnableKeys = [FeatureKeys.UrlCleanerEnabled], Energy = EnergyProfile.Periodic },

        // ── Sound ───────────────────────────────────────────────────────
        new() { Id = Mixer, Group = FeatureGroup.Sound, Icon = "Speaker2",
            TitleKey = "Strings.mixerSection", DescriptionKey = "hub.descMixer" },
        new() { Id = SoundOutputSwitcher, Group = FeatureGroup.Sound, Icon = "SpeakerBluetooth",
            TitleKey = "Strings.soundOutputSwitcherTitle", DescriptionKey = "hub.descSoundOutputSwitcher",
            EnableKeys = [FeatureKeys.SoundOutputSwitcherEnabled] },
        new() { Id = AudioPriority, Group = FeatureGroup.Sound, Icon = "TextNumberList",
            TitleKey = "hub.titleAudioPriority", DescriptionKey = "hub.descAudioPriority",
            EnableKeys = [FeatureKeys.AudioPriorityOutputEnabled, FeatureKeys.AudioPriorityInputEnabled],
            InitialEnableKeys = [FeatureKeys.AudioPriorityOutputEnabled, FeatureKeys.AudioPriorityInputEnabled],
            InstalledByDefault = false },
        new() { Id = MicMute, Group = FeatureGroup.Sound, Icon = "MicOff",
            TitleKey = "Strings.micMuteName", DescriptionKey = "hub.descMicMute" },

        // ── Energy and display ──────────────────────────────────────────
        new() { Id = KeepAwake, Group = FeatureGroup.EnergyDisplay, Icon = "WeatherMoon",
            TitleKey = "Strings.keepAwakeTitle", DescriptionKey = "win.shell.keepAwakeDescription" },
        new() { Id = Brightness, Group = FeatureGroup.EnergyDisplay, Icon = "Desktop",
            TitleKey = "brightness.pageTitle", DescriptionKey = "brightness.hubDescription",
            EnableKeys = [FeatureKeys.BrightnessControlEnabled] },

        // ── Mouse and keyboard ──────────────────────────────────────────
        new() { Id = ScrollInverter, Group = FeatureGroup.MouseKeyboard, Icon = "ArrowSort",
            TitleKey = "Strings.invertMouseScroll", DescriptionKey = "hub.descScrollInverter",
            EnableKeys = [FeatureKeys.ScrollInverterEnabled], Energy = EnergyProfile.Mouse,
            OfferWhenNeverSwitchedOn = true },
        new() { Id = SmoothScroll, Group = FeatureGroup.MouseKeyboard, Icon = "CursorHover",
            TitleKey = "Strings.smoothScrollName", DescriptionKey = "hub.descSmoothScroll",
            EnableKeys = [FeatureKeys.SmoothScrollEnabled], Energy = EnergyProfile.Mouse },
        new() { Id = MouseButtonShortcuts, Group = FeatureGroup.MouseKeyboard, Icon = "ControlButton",
            TitleKey = "mouseButtons.pageTitle", DescriptionKey = "mouseButtons.hubDescription",
            EnableKeys = [FeatureKeys.MouseButtonShortcutsEnabled], Energy = EnergyProfile.Mouse,
            OfferWhenNeverSwitchedOn = true },
        new() { Id = MouseClickDebounce, Group = FeatureGroup.MouseKeyboard, Icon = "CursorClick",
            TitleKey = "mouseClickDebounce.title", DescriptionKey = "mouseClickDebounce.caption",
            EnableKeys = [FeatureKeys.MouseClickDebounceEnabled], Energy = EnergyProfile.Mouse,
            OfferWhenNeverSwitchedOn = true },
        new() { Id = KeyboardDebounce, Group = FeatureGroup.MouseKeyboard, Icon = "Keyboard",
            TitleKey = "Strings.keyDebounceName", DescriptionKey = "hub.descKeyboardDebounce",
            EnableKeys = [FeatureKeys.KeyboardDebounceEnabled], Energy = EnergyProfile.Keyboard,
            OfferWhenNeverSwitchedOn = true },
        new() { Id = TextSnippets, Group = FeatureGroup.MouseKeyboard, Icon = "TextExpand",
            TitleKey = "snippets.pageTitle", DescriptionKey = "snippets.hubDescription",
            EnableKeys = [FeatureKeys.TextSnippetsEnabled, FeatureKeys.SnippetLibraryEnabled],
            Energy = EnergyProfile.Inputs },
        new() { Id = SuperKey, Group = FeatureGroup.MouseKeyboard, Icon = "KeyboardShift",
            TitleKey = "superKey.pageTitle", DescriptionKey = "superKey.hubDescription",
            EnableKeys = [FeatureKeys.SuperKeyEnabled], Energy = EnergyProfile.Inputs,
            OfferWhenNeverSwitchedOn = true },
        new() { Id = QuitWindowProtection, Group = FeatureGroup.MouseKeyboard, Icon = "Shield",
            TitleKey = "quitProtection.name", DescriptionKey = "win.shell.quitProtectionDescription",
            EnableKeys = [FeatureKeys.QuitProtectionQuitEnabled, FeatureKeys.QuitProtectionCloseEnabled],
            Energy = EnergyProfile.Keyboard },
    ];

    private static readonly Dictionary<string, FeatureDescriptor> ById =
        All.ToDictionary(f => f.Id, StringComparer.Ordinal);

    public static FeatureDescriptor Get(string id) =>
        ById.TryGetValue(id, out var feature) ? feature : throw new KeyNotFoundException($"Unknown feature '{id}'.");

    public static FeatureDescriptor? Find(string id) => ById.GetValueOrDefault(id);

    public static IEnumerable<FeatureDescriptor> InGroup(FeatureGroup group) => All.Where(f => f.Group == group);

    public static string GroupTitleKey(FeatureGroup group) => group switch
    {
        FeatureGroup.Capture => "win.shell.groupCapture",
        FeatureGroup.Monitor => "hub.groupMonitor",
        FeatureGroup.Tools => "hub.groupTools",
        FeatureGroup.AppManagement => "settingsCategories.appManagement",
        FeatureGroup.ClipboardFiles => "hub.groupClipboardFiles",
        FeatureGroup.Sound => "hub.groupSound",
        FeatureGroup.EnergyDisplay => "hub.groupEnergyDisplay",
        FeatureGroup.MouseKeyboard => "hub.groupMouseKeyboard",
        _ => "hub.groupTools",
    };

    public static string GroupIcon(FeatureGroup group) => group switch
    {
        FeatureGroup.Capture => "Screenshot",
        FeatureGroup.Monitor => "DataTrending",
        FeatureGroup.Tools => "WrenchScrewdriver",
        FeatureGroup.AppManagement => "AppsList",
        FeatureGroup.ClipboardFiles => "ClipboardPaste",
        FeatureGroup.Sound => "Speaker2",
        FeatureGroup.EnergyDisplay => "Flash",
        FeatureGroup.MouseKeyboard => "Cursor",
        _ => "Apps",
    };

    public static string EnergyLabelKey(EnergyProfile energy) => energy switch
    {
        EnergyProfile.Mouse => "hub.energyMouse",
        EnergyProfile.Keyboard => "hub.energyKeyboard",
        EnergyProfile.Inputs => "hub.energyInputs",
        EnergyProfile.Periodic => "hub.energyPeriodic",
        _ => "hub.energyIdle",
    };
}
