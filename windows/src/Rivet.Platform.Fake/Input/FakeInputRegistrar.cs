// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Input;
using Rivet.Core.Modules;

namespace Rivet.Platform.Fake.Input;

/// <summary>Development and test stand-ins for the input fixes' platform services.</summary>
public sealed class FakeInputRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeAppIdentityResolver>();
        services.AddSingleton<IAppIdentityResolver>(sp => sp.GetRequiredService<FakeAppIdentityResolver>());
        services.AddSingleton<FakeRunningApps>();
        services.AddSingleton<IRunningAppsMonitor>(sp => sp.GetRequiredService<FakeRunningApps>());
        services.AddSingleton<FakeWheelClassifier>();
        services.AddSingleton<IWheelDeviceClassifier>(sp => sp.GetRequiredService<FakeWheelClassifier>());
        services.AddSingleton<FakeKeyboardInfo>();
        services.AddSingleton<IKeyboardInfo>(sp => sp.GetRequiredService<FakeKeyboardInfo>());
        services.AddSingleton<FakeGlideFrameTimer>();
        services.AddSingleton<IGlideFrameTimer>(sp => sp.GetRequiredService<FakeGlideFrameTimer>());
        services.AddSingleton<FakeAppCatalog>();
        services.AddSingleton<IAppCatalog>(sp => sp.GetRequiredService<FakeAppCatalog>());
    }
}
