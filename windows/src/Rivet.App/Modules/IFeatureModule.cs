// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.App.Modules;

/// <summary>
/// A feature module: one folder under <c>Features/</c>. The app finds every
/// non-abstract implementation by reflection, so adding a module never means
/// editing a shared list. Platform implementations are registered separately
/// by an <see cref="Rivet.Core.Modules.IPlatformRegistrar"/> in the platform
/// assemblies (Windows and Fake).
/// </summary>
public interface IFeatureModule
{
    /// <summary>Short id for logs, e.g. "screenshot".</summary>
    string Id { get; }

    /// <summary>Registers the module's own services (stores, view models, controllers).</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Contributes actions, shortcuts, feature controllers, panel content,
    /// settings pages, tray menu items and search providers. Runs once on
    /// the UI thread after the container is built and before features sync.
    /// </summary>
    void Initialize(ModuleContext context);
}
