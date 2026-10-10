// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Sound;
using Rivet.App.Modules;
using Rivet.Core.Features;
using Rivet.Core.Sound;
using Rivet.Platform.Fake.Sound;
using Xunit;

namespace Rivet.App.Tests.Sound;

/// <summary>Renders the sound UI (panel tab and Settings pages) in both themes with the sample PC.</summary>
public class SoundRenderTests
{
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Mixer_panel_section_renders(string theme)
    {
        using var scope = new SoundTestScope(installPriority: true);
        var surface = new Border { Padding = new Thickness(12), Child = new MixerSectionView(scope.Services) };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        var path = TestApp.Snapshot(surface, $"sound-panel-{theme}", 340, theme: Theme(theme));
        Assert.True(File.Exists(path));
    }

    /// <summary>The panel's Options with every group open (rendered alone so nothing is cut off).</summary>
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Mixer_panel_options_render(string theme)
    {
        using var scope = new SoundTestScope(installPriority: true);
        var options = new MixerOptionsView(scope.Services, inPanel: true);
        foreach (var disclosure in options.GetLogicalDescendants().OfType<Disclosure>())
        {
            disclosure.IsExpanded = true;
        }

        var surface = new Border { Padding = new Thickness(12), Child = new Border { Classes = { "card" }, Child = options } };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        TestApp.Snapshot(surface, $"sound-panel-options-{theme}", 340, theme: Theme(theme));
    }

    [AvaloniaFact]
    public void Priority_only_panel_section_renders()
    {
        using var scope = new SoundTestScope(installPriority: true, installMixer: false);
        var surface = new Border { Padding = new Thickness(12), Child = new MixerSectionView(scope.Services) };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        TestApp.Snapshot(surface, "sound-panel-priority-only-light", 340);
    }

    [AvaloniaTheory]
    [InlineData(SoundModule.MixerPageId, "light")]
    [InlineData(SoundModule.MixerPageId, "dark")]
    [InlineData(SoundModule.OutputSwitcherPageId, "light")]
    [InlineData(SoundModule.OutputSwitcherPageId, "dark")]
    [InlineData(SoundModule.AudioPriorityPageId, "light")]
    [InlineData(SoundModule.AudioPriorityPageId, "dark")]
    [InlineData(SoundModule.MicMutePageId, "light")]
    [InlineData(SoundModule.MicMutePageId, "dark")]
    public void Settings_pages_render(string pageId, string theme)
    {
        using var scope = new SoundTestScope(installPriority: true);
        var page = scope.Services.GetRequiredService<SettingsPageRegistry>().Find(pageId);
        Assert.NotNull(page);
        Assert.Equal(SettingsCategory.Sound, page!.Category);
        var content = new Border { Padding = new Thickness(28, 22, 32, 32), Child = page.CreateView(scope.Services) };
        var height = pageId == SoundModule.MixerPageId ? 1460 : (double?)null;
        TestApp.Snapshot(content, $"sound-settings-{pageId}-{theme}", 900, height, Theme(theme));
    }

    private static ThemeVariant Theme(string name) => name == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
}

/// <summary>
/// Puts the shared test host into a known sound state (sample PC, features
/// installed) and restores the installs afterwards, so other tests see the
/// host as they expect.
/// </summary>
internal sealed class SoundTestScope : IDisposable
{
    private readonly FeatureRuntime _runtime;
    private readonly Dictionary<string, bool> _before = new();

    public SoundTestScope(bool installPriority = false, bool installMixer = true)
    {
        var host = TestApp.Host;
        Services = host.Services;
        _runtime = Services.GetRequiredService<FeatureRuntime>();
        Fake = Services.GetRequiredService<FakeAudioPlatform>();
        Fake.LoadSampleSetup();
        foreach (var (id, wanted) in new[]
                 {
                     (FeatureIds.Mixer, installMixer), (FeatureIds.SoundOutputSwitcher, true),
                     (FeatureIds.AudioPriority, installPriority), (FeatureIds.MicMute, true),
                 })
        {
            _before[id] = _runtime.IsAvailable(id);
            _runtime.SetAvailable(id, wanted);
        }
    }

    public IServiceProvider Services { get; }

    public FakeAudioPlatform Fake { get; }

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        foreach (var (id, available) in _before)
        {
            _runtime.SetAvailable(id, available);
        }

        Fake.LoadSampleSetup();
    }
}
