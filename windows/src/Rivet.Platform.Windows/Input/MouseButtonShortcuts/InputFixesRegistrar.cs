// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Input;
using Rivet.Core.Modules;
using Rivet.Platform.Windows.Input.SmoothScroll;
using Rivet.Platform.Windows.Input.SuperKey;

namespace Rivet.Platform.Windows.Input.MouseButtonShortcuts;

/// <summary>Windows services of the input fixes (app identity, Raw Input, frame pacing, keyboard facts).</summary>
public sealed class InputFixesRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<WindowsRawInput>();
        services.AddSingleton<IWheelDeviceClassifier, WindowsWheelClassifier>();
        services.AddSingleton<IKeyboardInfo, WindowsKeyboardInfo>();
        services.AddSingleton<IRunningAppsMonitor, WindowsRunningApps>();
        services.AddSingleton<IGlideFrameTimer, WindowsGlideFrameTimer>();
        services.AddSingleton<IAppIdentityResolver, WindowsAppIdentityResolver>();
        services.AddSingleton<IAppCatalog, WindowsAppCatalog>();
    }
}
