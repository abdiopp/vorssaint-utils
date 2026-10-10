// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): the replacement overload is not needed for test snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.App.Shell;
using Rivet.Core.Features;
using Xunit;

namespace Rivet.App.Tests;

public class ShellRenderTests
{
    [AvaloniaFact]
    public void Every_catalog_icon_name_exists()
    {
        var names = FeatureCatalog.All.Select(f => f.Icon)
            .Concat(Enum.GetValues<FeatureGroup>().Select(FeatureCatalog.GroupIcon))
            .Concat(FeaturePresets.All.Select(p => p.Icon));
        var host = TestApp.Host;
        names = names
            .Concat(host.Services.GetRequiredService<PanelRegistry>().Sections.Select(s => s.Icon))
            .Concat(host.Services.GetRequiredService<SettingsPageRegistry>().Pages.Select(p => p.Icon));
        var unknown = names.Distinct().Where(n => !IconConverter.IsKnown(n)).ToList();
        Assert.True(unknown.Count == 0, "Unknown icons: " + string.Join(", ", unknown));
    }

    [AvaloniaFact]
    public void Tray_icon_renders_every_size_for_both_taskbar_themes()
    {
        foreach (var light in new[] { true, false })
        {
            var sizes = TrayIconRenderer.Render(light, tint: 0xFFFF9500, badge: true);
            Assert.Equal(TrayIconRenderer.Sizes, sizes.Select(s => s.Width));
            Assert.Contains(sizes[0].Pixels, b => b != 0);
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Panel_renders(string theme)
    {
        var host = TestApp.Host;
        var panel = host.Services.GetRequiredService<PanelRegistry>();
        TestApp.AddSampleContent(panel);
        var runtime = host.Services.GetRequiredService<FeatureRuntime>();
        runtime.SetAvailable(FeatureIds.ScreenOcr, true);
        runtime.SetAvailable(FeatureIds.ColorPicker, true);
        runtime.SetAvailable(FeatureIds.SmoothScroll, true);

        var vm = new PanelViewModel(host.Services);
        vm.Rebuild();
        Assert.NotEmpty(vm.Tabs);
        var surface = new Border { CornerRadius = new Avalonia.CornerRadius(8), BorderThickness = new Avalonia.Thickness(1), Child = new PanelView { DataContext = vm } };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        TestApp.Snapshot(surface, $"panel-{theme}", 342, theme: theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    [AvaloniaTheory]
    [InlineData(SettingsPageIds.General)]
    [InlineData(SettingsPageIds.TrayPanel)]
    [InlineData(SettingsPageIds.Features)]
    [InlineData(SettingsPageIds.Shortcuts)]
    [InlineData(SettingsPageIds.Advanced)]
    [InlineData(SettingsPageIds.About)]
    public void Settings_pages_render(string pageId)
    {
        var host = TestApp.Host;
        var vm = new SettingsViewModel(host.Services);
        vm.Navigate(pageId);
        Assert.Equal(pageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 900 };
        window.Show();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"settings-{pageId}.png"));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Onboarding_steps_render(int step)
    {
        var host = TestApp.Host;
        var window = new OnboardingWindow(host.Services);
        window.Show(step);
        window.Show();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"onboarding-{step}.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_search_finds_pages_and_routes_uninstalled_features_to_the_hub()
    {
        var host = TestApp.Host;
        var runtime = host.Services.GetRequiredService<FeatureRuntime>();
        runtime.SetAvailable(FeatureIds.PortManager, false);
        var vm = new SettingsViewModel(host.Services) { SearchText = "Port" };
        Assert.Contains(vm.SearchResults, h => h.PageId == SettingsPageIds.Features && h.RevealFeatureId == FeatureIds.PortManager);
        vm.SearchText = "backup";
        Assert.Contains(vm.SearchResults, h => h.PageId == SettingsPageIds.Advanced);
    }

    [AvaloniaFact]
    public void Panel_layout_inserts_new_items_near_their_default_neighbours()
    {
        var items = new[] { ("a", 0), ("b", 10), ("c", 20), ("d", 30) };
        var ordered = PanelLayout.Order(items, i => i.Item1, i => i.Item2, "d,a,zzz,a");
        Assert.Equal(["d", "a", "b", "c"], ordered.Select(i => i.Item1));
        var withNew = PanelLayout.Order(items, i => i.Item1, i => i.Item2, "c,a");
        Assert.Equal(4, withNew.Count);
        Assert.Equal("c", withNew[0].Item1);
    }
}
