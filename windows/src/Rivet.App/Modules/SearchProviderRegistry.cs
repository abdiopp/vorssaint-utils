// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Contracts;
using Rivet.Core.Features;

namespace Rivet.App.Modules;

/// <summary>Command Bar result sources contributed by modules.</summary>
public sealed class SearchProviderRegistry(FeatureRuntime runtime)
{
    private readonly List<ISearchProvider> _providers = [];

    public void Add(ISearchProvider provider)
    {
        _providers.RemoveAll(p => p.Id == provider.Id);
        _providers.Add(provider);
    }

    /// <summary>Providers whose feature is installed.</summary>
    public IReadOnlyList<ISearchProvider> Active => _providers.Where(p => runtime.IsAvailable(p.FeatureId)).ToList();
}
