// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Sound;

namespace Rivet.Platform.Fake.Sound;

/// <summary>Registers the in-memory audio system for development builds and UI tests.</summary>
public sealed class FakeSoundRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeAudioPlatform>();
        services.AddSingleton<IAudioPlatform>(sp => sp.GetRequiredService<FakeAudioPlatform>());
    }
}
