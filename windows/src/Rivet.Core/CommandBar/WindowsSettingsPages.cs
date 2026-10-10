// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Launcher;

/// <summary>One Windows Settings page or classic applet the Command Bar can open.</summary>
public sealed record WindowsSettingsPage(string Key, string Uri, string Keywords)
{
    /// <summary>String key of the page title (win.commandBar.ms.*).</summary>
    public string TitleKey => "win.commandBar.ms." + Key;

    /// <summary>Row id: the persisted source id stays <c>macsettings</c>.</summary>
    public string RowId => "macsettings." + Uri;
}

/// <summary>
/// The static replacement for the macOS pane scan (spec 06 §7.3): the 51
/// reference panes mapped to <c>ms-settings:</c> URIs (panes without a
/// Windows twin dropped) plus Windows-only pages and classic applets.
/// Keywords are fixed English tokens that match in every language.
/// </summary>
public static class WindowsSettingsPages
{
    public static readonly IReadOnlyList<WindowsSettingsPage> All =
    [
        new("about", "ms-settings:about", "about pc system info specs device name rename"),
        new("accessibility", "ms-settings:easeofaccess", "accessibility ease of access narrator magnifier"),
        new("nearbySharing", "ms-settings:crossdevice", "nearby sharing shared experiences airdrop handoff"),
        new("colors", "ms-settings:colors", "colors appearance dark mode light mode accent theme"),
        new("yourInfo", "ms-settings:yourinfo", "your info account microsoft account profile"),
        new("windowsSecurity", "windowsdefender:", "windows security defender antivirus virus protection"),
        new("bluetooth", "ms-settings:bluetooth", "bluetooth devices pair"),
        new("autoPlay", "ms-settings:autoplay", "autoplay cd dvd removable"),
        new("taskbar", "ms-settings:taskbar", "taskbar tray system tray control center"),
        new("dateTime", "ms-settings:dateandtime", "date time clock time zone"),
        new("multitasking", "ms-settings:multitasking", "multitasking snap desktops alt tab dock"),
        new("workplace", "ms-settings:workplace", "work school device management profiles mdm"),
        new("display", "ms-settings:display", "display screen resolution scale monitor brightness hdr"),
        new("family", "ms-settings:family-group", "family screen time parental"),
        new("focus", "ms-settings:quiethours", "focus do not disturb quiet hours"),
        new("gaming", "ms-settings:gaming-gamebar", "gaming game bar xbox"),
        new("gameControllers", "joy.cpl", "game controllers joystick gamepad"),
        new("settingsHome", "ms-settings:", "settings home general"),
        new("sound", "ms-settings:sound", "sound audio output input volume headphones speakers"),
        new("emailAccounts", "ms-settings:emailandaccounts", "email accounts internet accounts"),
        new("typing", "ms-settings:typing", "typing keyboard autocorrect"),
        new("regionLanguage", "ms-settings:regionlanguage", "language region locale format"),
        new("lockScreen", "ms-settings:lockscreen", "lock screen"),
        new("startupApps", "ms-settings:startupapps", "startup apps login items autostart"),
        new("mouse", "ms-settings:mousetouchpad", "mouse pointer scrolling"),
        new("network", "ms-settings:network-status", "network internet status"),
        new("notifications", "ms-settings:notifications", "notifications alerts"),
        new("power", "ms-settings:powersleep", "power battery sleep energy"),
        new("batterySaver", "ms-settings:batterysaver", "battery saver energy saver"),
        new("printers", "ms-settings:printers", "printers scanners print"),
        new("privacy", "ms-settings:privacy", "privacy security permissions"),
        new("remoteDesktop", "ms-settings:remotedesktop", "remote desktop sharing"),
        new("windowsUpdate", "ms-settings:windowsupdate", "windows update software update upgrade"),
        new("search", "ms-settings:cortana-windowssearch", "search indexing windows search spotlight"),
        new("storage", "ms-settings:storagesense", "storage disk space storage sense"),
        new("backup", "ms-settings:backup", "backup time machine file history"),
        new("signIn", "ms-settings:signinoptions", "sign in password pin windows hello fingerprint face"),
        new("touchpad", "ms-settings:devices-touchpad", "touchpad trackpad gestures"),
        new("recovery", "ms-settings:recovery", "recovery reset this pc transfer"),
        new("otherUsers", "ms-settings:otherusers", "other users accounts family users groups"),
        new("vpn", "ms-settings:network-vpn", "vpn"),
        new("background", "ms-settings:personalization-background", "background wallpaper desktop picture"),
        new("wifi", "ms-settings:network-wifi", "wifi wi-fi wireless"),
        new("apps", "ms-settings:appsfeatures", "apps installed apps uninstall programs"),
        new("defaultApps", "ms-settings:defaultapps", "default apps browser"),
        new("personalization", "ms-settings:personalization", "personalization"),
        new("themes", "ms-settings:themes", "themes"),
        new("start", "ms-settings:personalization-start", "start menu"),
        new("clipboard", "ms-settings:clipboard", "clipboard history sync"),
        new("nightLight", "ms-settings:nightlight", "night light blue light"),
        new("airplaneMode", "ms-settings:network-airplanemode", "airplane mode flight"),
        new("mobileHotspot", "ms-settings:network-mobilehotspot", "mobile hotspot tethering"),
        new("ethernet", "ms-settings:network-ethernet", "ethernet wired"),
        new("proxy", "ms-settings:network-proxy", "proxy"),
        new("camera", "ms-settings:privacy-webcam", "camera webcam privacy"),
        new("microphone", "ms-settings:privacy-microphone", "microphone mic privacy"),
        new("location", "ms-settings:privacy-location", "location gps privacy"),
        new("optionalFeatures", "ms-settings:optionalfeatures", "optional features"),
        new("developers", "ms-settings:developers", "for developers developer mode"),
        new("activation", "ms-settings:activation", "activation product key license"),
        new("speech", "ms-settings:speech", "speech voice"),
        new("fonts", "ms-settings:fonts", "fonts"),
        new("pen", "ms-settings:pen", "pen ink"),
        new("usb", "ms-settings:usb", "usb"),
        new("graphics", "ms-settings:display-advancedgraphics", "graphics gpu"),
        new("troubleshoot", "ms-settings:troubleshoot", "troubleshoot fix problems"),
        new("soundDevices", "mmsys.cpl", "sound devices playback recording control panel"),
        new("networkConnections", "ncpa.cpl", "network connections adapters control panel"),
        new("programsFeatures", "appwiz.cpl", "programs and features uninstall control panel"),
        new("deviceManager", "devmgmt.msc", "device manager drivers hardware"),
        new("powerOptions", "powercfg.cpl", "power options plan control panel"),
    ];
}
