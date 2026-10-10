// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Windows.Input;
using Rivet.Platform.Windows.Interop;
using Rivet.Platform.Windows.Shell;

namespace Rivet.Platform.Windows;

/// <summary>The shell services. Feature modules add their own registrars next to their code.</summary>
public sealed class WindowsShellRegistrar : IPlatformRegistrar
{
    public int Order => -100;

    public void Register(IServiceCollection services)
    {
        // Created lazily on the UI thread, after the UI framework's message loop exists.
        services.AddSingleton<NativeWindowHost>();
        services.AddSingleton<IPlatformInfo, WindowsPlatformInfo>();
        services.AddSingleton<IInputHooks, WindowsInputHooks>();
        services.AddSingleton<IHotkeyService, WindowsHotkeyService>();
        services.AddSingleton<IKeyNameProvider, WindowsKeyNames>();
        services.AddSingleton<ITrayIcon, WindowsTrayIcon>();
        services.AddSingleton<IThemeService, WindowsTheme>();
        services.AddSingleton<IAutostartService, WindowsAutostart>();
        services.AddSingleton<WindowsNotifications>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<WindowsNotifications>());
        services.AddSingleton<IShellService, WindowsShell>();
        services.AddSingleton<ISingleInstanceService, WindowsSingleInstance>();
        services.AddSingleton<IScreenService, WindowsScreens>();
        services.AddSingleton<IWindowChrome, WindowsWindowChrome>();
        services.AddSingleton<IFocusHandoff, WindowsFocusHandoff>();
        services.AddSingleton<IRelauncher, WindowsRelauncher>();
        services.AddSingleton<IClipboardService, WindowsClipboard>();
    }
}
