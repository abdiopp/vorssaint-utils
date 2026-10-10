// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.Core.Modules;

/// <summary>
/// Registers platform implementations (Windows, or the fakes used for
/// development and tests). The app finds every non-abstract implementation in
/// the platform assembly by reflection, so each module adds its own registrar
/// file instead of editing a shared list.
/// </summary>
public interface IPlatformRegistrar
{
    /// <summary>Lower runs first; modules normally leave this at 0.</summary>
    int Order => 0;

    void Register(IServiceCollection services);
}
