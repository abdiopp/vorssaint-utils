// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): the replacement overload is not needed for test snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.App.Shell;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Xunit;

namespace Rivet.App.Tests;

/// <summary>The language is process-wide, so these tests must not run beside others.</summary>
[CollectionDefinition(nameof(LanguageSwitching), DisableParallelization = true)]
public sealed class LanguageSwitching;

/// <summary>
/// Renders the panel tabs and a spread of Settings pages in languages whose text runs long
/// (German, Russian) or uses another script (Japanese), for a human look at clipping and wrapping.
/// </summary>
[Collection(nameof(LanguageSwitching))]
public class LocalizedRenderTests
{
    private static readonly string[] Tabs = ["keepAwake", "system", "utilities"];

    private static readonly string[] PageFeatures =
    [
        FeatureIds.KeepAwake, FeatureIds.Cleaner, FeatureIds.Screenshot, FeatureIds.CommandBar,
        FeatureIds.Shelf, FeatureIds.MonitorCpu, FeatureIds.ClipboardHistory, FeatureIds.RadialMenu,
    ];

    [AvaloniaTheory]
    [InlineData(AppLanguage.De)]
    [InlineData(AppLanguage.Ru)]
    [InlineData(AppLanguage.Ja)]
    public void Panel_and_settings_render_in_other_languages(AppLanguage language)
    {
        var host = TestApp.Host;
        var before = Localizer.Current.Language;
        Localizer.Current.Language = language;
        try
        {
            var code = language.Code();
            var vm = new PanelViewModel(host.Services);
            vm.Rebuild();
            foreach (var tab in vm.Tabs.Where(t => Tabs.Contains(t.Id)))
            {
                vm.Select(tab);
                var surface = new Border { Child = new PanelView { DataContext = vm } };
                surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
                TestApp.Snapshot(surface, $"l10n-{code}-panel-{tab.Id}", 342, theme: ThemeVariant.Light);
            }

            var pages = host.Services.GetRequiredService<SettingsPageRegistry>();
            foreach (var feature in PageFeatures)
            {
                if (pages.ForFeature(feature) is not { } page)
                {
                    continue;
                }

                var settings = new SettingsViewModel(host.Services);
                settings.Navigate(page.Id);
                var window = new SettingsWindow(settings) { Width = 1080, Height = 900 };
                window.Show();
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                Assert.NotNull(frame);
                frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"l10n-{code}-settings-{page.Id}.png"));
                window.Close();
            }
        }
        finally
        {
            Localizer.Current.Language = before;
        }
    }
}
