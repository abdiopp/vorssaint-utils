// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Capture;
using Rivet.Core.Modules;

namespace Rivet.Platform.Fake.Capture;

public sealed class FakeCaptureRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeDesktop>();
        services.AddSingleton<FakeScreenCapturer>();
        services.AddSingleton<IScreenCapturer>(sp => sp.GetRequiredService<FakeScreenCapturer>());
        services.AddSingleton<IWindowEnumerator, FakeWindowEnumerator>();
        services.AddSingleton<FakeCaptureClipboard>();
        services.AddSingleton<ICaptureClipboard>(sp => sp.GetRequiredService<FakeCaptureClipboard>());
        services.AddSingleton<FakeOcrEngine>();
        services.AddSingleton<IOcrEngine>(sp => sp.GetRequiredService<FakeOcrEngine>());
        services.AddSingleton<FakeCapturePlatform>();
        services.AddSingleton<ICapturePlatform>(sp => sp.GetRequiredService<FakeCapturePlatform>());
    }
}
