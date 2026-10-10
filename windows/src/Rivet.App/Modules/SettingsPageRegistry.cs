// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace Rivet.App.Modules;

/// <summary>Sidebar groups of the Settings window, in display order.</summary>
public enum SettingsCategory
{
    Essentials,
    Capture,
    Monitor,
    Tools,
    AppManagement,
    ClipboardFiles,
    Sound,
    EnergyDisplay,
    MouseKeyboard,
    App,
}

/// <summary>A Settings page. Visible when <see cref="FeatureIds"/> is empty or any of them is installed.</summary>
public sealed record SettingsPageDescriptor
{
    public required string Id { get; init; }

    public required string TitleKey { get; init; }

    public required string Icon { get; init; }

    public required SettingsCategory Category { get; init; }

    public int Order { get; init; }

    /// <summary>Features this page configures; the page is visible while any of them is installed.</summary>
    public IReadOnlyList<string> FeatureIds { get; init; } = [];

    /// <summary>
    /// Features whose main page this is: the Features hub and search link them
    /// here. When no page claims a feature, the page with the fewest features wins.
    /// </summary>
    public IReadOnlyList<string> PrimaryFor { get; init; } = [];

    public required Func<IServiceProvider, Control> CreateView { get; init; }

    /// <summary>String keys whose (localized) text makes the page findable in search.</summary>
    public IReadOnlyList<string> KeywordKeys { get; init; } = [];

    /// <summary>Fixed search tokens that match in every language ("PID", "winget").</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];
}

public sealed class SettingsPageRegistry
{
    private readonly List<SettingsPageDescriptor> _pages = [];

    public event EventHandler? Changed;

    public IReadOnlyList<SettingsPageDescriptor> Pages => _pages;

    public void Add(SettingsPageDescriptor page)
    {
        _pages.RemoveAll(p => p.Id == page.Id);
        _pages.Add(page);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public SettingsPageDescriptor? Find(string id) => _pages.FirstOrDefault(p => p.Id == id);

    /// <summary>The page that configures <paramref name="featureId"/>, if any.</summary>
    public SettingsPageDescriptor? ForFeature(string featureId) =>
        _pages.FirstOrDefault(p => p.PrimaryFor.Contains(featureId))
        ?? _pages.Where(p => p.FeatureIds.Contains(featureId)).OrderBy(p => p.FeatureIds.Distinct().Count()).FirstOrDefault();
}
