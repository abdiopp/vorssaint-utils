// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rivet.Core.Platform;
using Rivet.Core.Util;
using Rivet.Platform.Windows.Interop;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Shell;

/// <summary>Light/dark mode, accent colour and accessibility flags, refreshed on WM_SETTINGCHANGE.</summary>
public sealed unsafe class WindowsTheme : IThemeService, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;
    private readonly UISettings? _uiSettings;

    public WindowsTheme(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
        _host.AddHandler(_handler);
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        catch (COMException)
        {
            _uiSettings = null;
        }
    }

    public event EventHandler? Changed;

    public bool AppsUseLightTheme => ReadDword("AppsUseLightTheme", 1) != 0;

    public bool SystemUsesLightTheme => ReadDword("SystemUsesLightTheme", 0) != 0;

    public bool TransparencyEnabled => ReadDword("EnableTransparency", 1) != 0;

    public uint AccentColor
    {
        get
        {
            try
            {
                if (_uiSettings is not null)
                {
                    var c = _uiSettings.GetColorValue(UIColorType.Accent);
                    return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                }
            }
            catch (COMException)
            {
            }

            return 0xFF0078D4;
        }
    }

    public bool HighContrast
    {
        get
        {
            var info = new HIGHCONTRASTW { cbSize = (uint)sizeof(HIGHCONTRASTW) };
            return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETHIGHCONTRAST, info.cbSize, &info, 0)
                   && (info.dwFlags & HIGHCONTRASTW_FLAGS.HCF_HIGHCONTRASTON) != 0;
        }
    }

    public bool ReduceMotion
    {
        get
        {
            int enabled = 1;
            return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0) && enabled == 0;
        }
    }

    private static int ReadDword(string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is int value ? value : fallback;
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == PInvoke.WM_SETTINGCHANGE || message == PInvoke.WM_THEMECHANGED || message == PInvoke.WM_SYSCOLORCHANGE)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }

    public void Dispose() => _host.RemoveHandler(_handler);
}
