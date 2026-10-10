// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Platform;

/// <summary>Facts about the OS and process.</summary>
public interface IPlatformInfo
{
    bool IsWindows { get; }

    /// <summary>e.g. "Windows 11 23H2 (10.0.22631)".</summary>
    string OsDescription { get; }

    Version OsVersion { get; }

    /// <summary>Running as administrator (elevated token).</summary>
    bool IsElevated { get; }

    /// <summary>"x64" or "arm64".</summary>
    string Architecture { get; }

    /// <summary>Windows 11 (build 22000 or later).</summary>
    bool IsWindows11 => OsVersion.Build >= 22000;
}

/// <summary>Monitors and the pointer.</summary>
public interface IScreenService
{
    IReadOnlyList<ScreenInfo> Screens { get; }

    ScreenInfo Primary { get; }

    /// <summary>Union of all monitors, physical pixels.</summary>
    PixelRect VirtualScreen { get; }

    PixelPoint CursorPosition { get; }

    ScreenInfo ScreenFromPoint(PixelPoint point);

    /// <summary>Raised when monitors are added, removed, rearranged or rescaled.</summary>
    event EventHandler? ScreensChanged;
}

public enum TrayMouseButton
{
    Left,
    Right,
    Middle,
}

public sealed class TrayClickEventArgs(TrayMouseButton button, PixelPoint position) : EventArgs
{
    public TrayMouseButton Button { get; } = button;

    /// <summary>Pointer position at the click, physical pixels.</summary>
    public PixelPoint Position { get; } = position;
}

/// <summary>The notification-area icon.</summary>
public interface ITrayIcon : IDisposable
{
    /// <summary>Adds the icon (or re-adds it after Explorer restarts).</summary>
    void Show();

    void Hide();

    /// <summary>Icon images for 100/125/150/200 % (16/20/24/32 px); the platform picks the best match.</summary>
    void SetIcon(IReadOnlyList<PixelBuffer> sizes);

    /// <summary>Tooltip text (Windows truncates at 127 characters).</summary>
    void SetTooltip(string text);

    /// <summary>Icon rectangle in physical pixels, when Windows reports it (not while in the overflow area).</summary>
    PixelRect? Bounds { get; }

    event EventHandler<TrayClickEventArgs>? Clicked;

    /// <summary>Taskbar theme changed: re-render template icons.</summary>
    event EventHandler? TaskbarThemeChanged;
}

/// <summary>Theme facts for following Windows' light/dark mode and accent.</summary>
public interface IThemeService
{
    /// <summary>Apps (windows) use the light theme.</summary>
    bool AppsUseLightTheme { get; }

    /// <summary>The taskbar uses the light theme (tray icons follow this, not the app theme).</summary>
    bool SystemUsesLightTheme { get; }

    /// <summary>Accent colour as 0xAARRGGBB.</summary>
    uint AccentColor { get; }

    /// <summary>"Transparency effects" on in Personalization.</summary>
    bool TransparencyEnabled { get; }

    bool HighContrast { get; }

    /// <summary>"Animation effects" off.</summary>
    bool ReduceMotion { get; }

    event EventHandler? Changed;
}

public enum AutostartState
{
    Disabled,
    Enabled,

    /// <summary>Registered, but switched off in Settings → Apps → Startup.</summary>
    DisabledByUser,
}

public interface IAutostartService
{
    AutostartState State { get; }

    /// <summary>Registers or removes the startup entry. Returns the resulting state.</summary>
    AutostartState SetEnabled(bool enabled);
}

public sealed record NotificationRequest
{
    public required string Title { get; init; }

    public required string Body { get; init; }

    /// <summary>Optional image (e.g. a capture thumbnail), absolute file path.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Buttons: (label, action id). The action runs when the button is clicked.</summary>
    public IReadOnlyList<(string Label, string ActionId)> Buttons { get; init; } = [];

    /// <summary>Action run when the toast body is clicked.</summary>
    public string? ClickActionId { get; init; }

    /// <summary>Replaces an earlier toast with the same tag.</summary>
    public string? Tag { get; init; }
}

public interface INotificationService
{
    bool IsEnabled { get; }

    void Show(NotificationRequest request);

    /// <summary>Raised (on the UI thread) when the user clicks a toast or one of its buttons.</summary>
    event EventHandler<string>? Activated;
}

/// <summary>Explorer, URLs, files and the Recycle Bin.</summary>
public interface IShellService
{
    void OpenUrl(string url);

    void OpenFile(string path);

    /// <summary>Opens Explorer with the item selected.</summary>
    void RevealInExplorer(string path);

    /// <summary>Opens a Windows Settings page, e.g. <c>ms-settings:privacy-webcam</c>.</summary>
    void OpenSystemSettings(string uri);

    /// <summary>Moves items to the Recycle Bin. Returns false when anything could not be recycled.</summary>
    bool MoveToRecycleBin(IEnumerable<string> paths);

    /// <summary>Shows the "Open with" dialog.</summary>
    void OpenWith(string path);
}

/// <summary>One running copy per user session; later launches forward their arguments.</summary>
public interface ISingleInstanceService : IDisposable
{
    /// <summary>True when this process is the primary instance.</summary>
    bool TryAcquire();

    /// <summary>From a secondary instance: hand the arguments to the primary one.</summary>
    bool SendToPrimary(IReadOnlyList<string> args);

    /// <summary>Raised on the primary instance when another launch forwards its arguments.</summary>
    event EventHandler<IReadOnlyList<string>>? ActivationRequested;
}

[Flags]
public enum WindowChromeOptions
{
    None = 0,

    /// <summary>No taskbar button and no Alt+Tab entry.</summary>
    ToolWindow = 1,

    /// <summary>Clicking does not activate the window (HUDs, previews, overlays that keep focus elsewhere).</summary>
    NoActivate = 2,

    /// <summary>Mouse input passes through to what is below.</summary>
    ClickThrough = 4,

    /// <summary>Kept out of screenshots and recordings (WDA_EXCLUDEFROMCAPTURE).</summary>
    ExcludeFromCapture = 8,

    /// <summary>Above other top-most windows, including the taskbar.</summary>
    Topmost = 16,
}

public enum WindowBackdrop
{
    None,

    /// <summary>Settings-style window material (Windows 11).</summary>
    Mica,

    /// <summary>Flyout material (Windows 11).</summary>
    Acrylic,
}

/// <summary>Native window tweaks the UI framework does not expose.</summary>
public interface IWindowChrome
{
    void Apply(nint hwnd, WindowChromeOptions options);

    void Remove(nint hwnd, WindowChromeOptions options);

    /// <summary>Returns false when the backdrop is not supported (Windows 10, transparency off).</summary>
    bool SetBackdrop(nint hwnd, WindowBackdrop backdrop, bool dark);

    void SetDarkTitleBar(nint hwnd, bool dark);

    void SetRoundedCorners(nint hwnd, bool rounded);

    /// <summary>The window currently in front (to hand focus back later).</summary>
    nint GetForegroundWindow();

    /// <summary>Brings <paramref name="hwnd"/> to the front, working around focus-steal rules where allowed.</summary>
    bool BringToFront(nint hwnd);
}

public interface IRelauncher
{
    /// <summary>Starts a fresh copy after this process exits, then shuts down the app.</summary>
    void RelaunchAndExit(IReadOnlyList<string>? args = null);
}

/// <summary>Basic clipboard access. Clipboard history has its own richer service.</summary>
public interface IClipboardService
{
    string? GetText();

    void SetText(string text);

    PixelBuffer? GetImage();

    /// <summary>Puts an image on the clipboard (as a bitmap and PNG).</summary>
    void SetImage(PixelBuffer image);

    void SetFiles(IReadOnlyList<string> paths);

    IReadOnlyList<string> GetFiles();
}

/// <summary>Restores the window that was in front before the app took focus.</summary>
public interface IFocusHandoff
{
    void Remember();

    void Restore();
}
