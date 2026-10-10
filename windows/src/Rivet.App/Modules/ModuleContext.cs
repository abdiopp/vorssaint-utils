// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Modules;

/// <summary>Everything a module can contribute to, handed to <see cref="IFeatureModule.Initialize"/>.</summary>
public sealed class ModuleContext(IServiceProvider services)
{
    public IServiceProvider Services { get; } = services;

    public ISettingsStore Settings => Services.GetRequiredService<ISettingsStore>();

    public FeatureRuntime Features => Services.GetRequiredService<FeatureRuntime>();

    public ActionRegistry Actions => Services.GetRequiredService<ActionRegistry>();

    public ShortcutManager Shortcuts => Services.GetRequiredService<ShortcutManager>();

    public PanelRegistry Panel => Services.GetRequiredService<PanelRegistry>();

    public SettingsPageRegistry SettingsPages => Services.GetRequiredService<SettingsPageRegistry>();

    public TrayMenuRegistry TrayMenu => Services.GetRequiredService<TrayMenuRegistry>();

    public SearchProviderRegistry Search => Services.GetRequiredService<SearchProviderRegistry>();

    public IHud Hud => Services.GetRequiredService<IHud>();

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();
}
