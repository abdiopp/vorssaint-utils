// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Sound;

namespace Rivet.Platform.Windows.Sound;

/// <summary>Registers the Windows audio system (Core Audio through NAudio plus the policy interfaces).</summary>
public sealed class WindowsSoundRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<WindowsAudioPlatform>();
        services.AddSingleton<IAudioPlatform>(sp => sp.GetRequiredService<WindowsAudioPlatform>());
    }
}
