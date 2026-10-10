// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Features;

/// <summary>
/// Stable feature ids. They are persisted (availability keys, presets, the
/// "kept" list), so they can be added but never renamed. Ids shared with the
/// macOS app keep the same spelling.
/// </summary>
public static class FeatureIds
{
    // Capture and create
    public const string Screenshot = "screenshot";
    public const string ScreenRecorder = "screenRecorder";
    public const string ScreenOcr = "screenOCR";
    public const string ColorPicker = "colorPicker";
    public const string CameraPreview = "cameraPreview";
    public const string MediaTools = "mediaTools";

    // System monitor
    public const string MonitorCpu = "monitorCPU";
    public const string MonitorGpu = "monitorGPU";
    public const string MonitorMemory = "monitorMemory";
    public const string MonitorNetwork = "monitorNetwork";
    public const string MonitorDisk = "monitorDisk";
    public const string MonitorPower = "monitorPower";
    public const string ConnectedDevices = "connectedDevices";

    // Tools
    public const string CommandBar = "commandBar";
    public const string QuickLauncher = "quickLauncher";
    public const string QuickToggles = "quickToggles";
    public const string RadialMenu = "radialMenu";
    public const string Scratchpad = "scratchpad";
    public const string CleaningMode = "cleaningMode";
    public const string AgentUsage = "agentUsage";

    // App management
    public const string Cleaner = "cleaner";
    public const string Uninstaller = "uninstaller";
    public const string AppUpdates = "appUpdates";
    public const string PackageManager = "packageManager";
    public const string KillProcess = "killProcess";
    public const string PortManager = "portManager";

    // Clipboard and files
    public const string ClipboardHistory = "clipboardHistory";
    public const string PastePlain = "pastePlain";
    public const string Shelf = "shelf";
    public const string UrlCleaner = "urlCleaner";

    // Sound
    public const string Mixer = "mixer";
    public const string SoundOutputSwitcher = "soundOutputSwitcher";
    public const string AudioPriority = "audioPriority";
    public const string MicMute = "micMute";

    // Energy and display
    public const string KeepAwake = "keepAwake";
    public const string Brightness = "brightness";

    // Mouse and keyboard
    public const string ScrollInverter = "scrollInverter";
    public const string SmoothScroll = "smoothScroll";
    public const string MouseButtonShortcuts = "mouseButtonShortcuts";
    public const string MouseClickDebounce = "mouseClickDebounce";
    public const string KeyboardDebounce = "keyboardDebounce";
    public const string TextSnippets = "textSnippets";
    public const string SuperKey = "superKey";
    public const string QuitWindowProtection = "quitWindowProtection";
}
