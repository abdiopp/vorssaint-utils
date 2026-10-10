// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Modules.Scratchpad;
using Rivet.Core.Platform;

namespace Rivet.Platform.Fake.Scratchpad;

public sealed class FakeScratchpadPlatform : IScratchpadPlatform
{
    public bool IsOnScreenKeyboardAt(PixelPoint point) => false;
}

public sealed class ScratchpadFakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) => services.AddSingleton<IScratchpadPlatform, FakeScratchpadPlatform>();
}
