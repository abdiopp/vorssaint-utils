// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Agents;
using Rivet.Core.Modules;

namespace Rivet.Platform.Windows.Agents;

public sealed class AgentUsageRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) =>
        services.AddSingleton<IAgentUsagePlatform, WindowsAgentUsagePlatform>();
}
