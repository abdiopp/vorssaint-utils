// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Fake.Shell;

namespace Rivet.Platform.Fake;

public sealed class FakeShellRegistrar : IPlatformRegistrar
{
    public int Order => -100;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IPlatformInfo, FakePlatformInfo>();
        services.AddSingleton<FakeInputHooks>();
        services.AddSingleton<IInputHooks>(sp => sp.GetRequiredService<FakeInputHooks>());
        services.AddSingleton<FakeHotkeyService>();
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<FakeHotkeyService>());
        services.AddSingleton<IKeyNameProvider, FakeKeyNames>();
        services.AddSingleton<FakeTrayIcon>();
        services.AddSingleton<ITrayIcon>(sp => sp.GetRequiredService<FakeTrayIcon>());
        services.AddSingleton<FakeTheme>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<FakeTheme>());
        services.AddSingleton<IAutostartService, FakeAutostart>();
        services.AddSingleton<FakeNotifications>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<FakeNotifications>());
        services.AddSingleton<FakeShell>();
        services.AddSingleton<IShellService>(sp => sp.GetRequiredService<FakeShell>());
        services.AddSingleton<ISingleInstanceService, FakeSingleInstance>();
        services.AddSingleton<FakeScreens>();
        services.AddSingleton<IScreenService>(sp => sp.GetRequiredService<FakeScreens>());
        services.AddSingleton<IWindowChrome, FakeWindowChrome>();
        services.AddSingleton<IFocusHandoff, FakeFocusHandoff>();
        services.AddSingleton<IRelauncher, FakeRelauncher>();
        services.AddSingleton<IClipboardService, FakeClipboard>();
    }
}
