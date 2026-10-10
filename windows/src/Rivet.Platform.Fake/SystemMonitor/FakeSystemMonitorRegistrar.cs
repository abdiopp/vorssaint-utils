// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.SystemMonitor;

namespace Rivet.Platform.Fake.SystemMonitor;

/// <summary>Plausible, slowly changing readings so the monitor renders on macOS and in tests.</summary>
public sealed class FakeSystemMonitorRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeMonitorClock>();
        services.AddSingleton<IMonitorClock>(sp => sp.GetRequiredService<FakeMonitorClock>());
        services.AddSingleton<FakeCpuSensor>();
        services.AddSingleton<ICpuSensor>(sp => sp.GetRequiredService<FakeCpuSensor>());
        services.AddSingleton<FakeMemorySensor>();
        services.AddSingleton<IMemorySensor>(sp => sp.GetRequiredService<FakeMemorySensor>());
        services.AddSingleton<FakeGpuSensor>();
        services.AddSingleton<IGpuSensor>(sp => sp.GetRequiredService<FakeGpuSensor>());
        services.AddSingleton<FakeNetworkSensor>();
        services.AddSingleton<INetworkSensor>(sp => sp.GetRequiredService<FakeNetworkSensor>());
        services.AddSingleton<FakeDiskSensor>();
        services.AddSingleton<IDiskSensor>(sp => sp.GetRequiredService<FakeDiskSensor>());
        services.AddSingleton<IDiskActions>(sp => sp.GetRequiredService<FakeDiskSensor>());
        services.AddSingleton<FakePowerSensor>();
        services.AddSingleton<IPowerSensor>(sp => sp.GetRequiredService<FakePowerSensor>());
        services.AddSingleton<FakeTemperatureSensor>();
        services.AddSingleton<ITemperatureSensor>(sp => sp.GetRequiredService<FakeTemperatureSensor>());
        services.AddSingleton<FakeSensorHelper>();
        services.AddSingleton<ISensorHelper>(sp => sp.GetRequiredService<FakeSensorHelper>());
        services.AddSingleton<FakeUsbSensor>();
        services.AddSingleton<IUsbSensor>(sp => sp.GetRequiredService<FakeUsbSensor>());
        services.AddSingleton<FakePeripheralBatterySensor>();
        services.AddSingleton<IPeripheralBatterySensor>(sp => sp.GetRequiredService<FakePeripheralBatterySensor>());
        services.AddSingleton<FakeProcessSampler>();
        services.AddSingleton<IProcessSampler>(sp => sp.GetRequiredService<FakeProcessSampler>());
        services.AddSingleton<IProcessControl>(sp => sp.GetRequiredService<FakeProcessSampler>());
        services.AddSingleton<FakeFullScreenDetector>();
        services.AddSingleton<IFullScreenDetector>(sp => sp.GetRequiredService<FakeFullScreenDetector>());
    }
}
