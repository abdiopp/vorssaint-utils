// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Toggles;
using Rivet.App.Settings;
using Rivet.App.Tests.Clipboard;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Toggles;
using Xunit;

namespace Rivet.App.Tests.QuickToggles;

public class QuickToggleRenderTests
{
    internal static void Seed(IServiceProvider services)
    {
        var runtime = services.GetRequiredService<FeatureRuntime>();
        runtime.SetAvailable(FeatureIds.QuickToggles, true);
        runtime.SetAvailable(FeatureIds.MicMute, true);
        var actions = services.GetRequiredService<ActionRegistry>();
        if (actions.Get(QuickToggleCatalog.MicMuteActionId) is null)
        {
            // Stands in for the microphone module's action.
            actions.Register(new AppAction
            {
                Id = QuickToggleCatalog.MicMuteActionId,
                FeatureId = FeatureIds.MicMute,
                TitleKey = "Strings.micMuteName",
                Icon = "MicOff",
                Run = _ => Task.CompletedTask,
            });
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Section_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Seed(services);
        TestApp.Snapshot(new Border { Padding = new Avalonia.Thickness(12), Child = new QuickTogglesView(services, editable: true) }, $"quick-toggles-{theme}", 360,
            theme: theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    [AvaloniaFact]
    public void Section_edit_mode_renders()
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var view = new QuickTogglesView(services, editable: true);
        var edit = view.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, L.Get("win.quickToggles.edit")));
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        TestApp.Snapshot(new Border { Padding = new Avalonia.Thickness(12), Child = view }, "quick-toggles-editing", 360);
        Assert.Contains(view.GetLogicalDescendants().OfType<ToggleSwitch>(), t => t.IsChecked == true);
    }

    [AvaloniaFact]
    public void Catalog_moves_and_resets_rows()
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var catalog = services.GetRequiredService<QuickToggleCatalog>();
        catalog.Reset();
        Assert.Equal(QuickToggleId.DarkMode, catalog.Listed(false)[0]);
        catalog.Move(QuickToggleId.DarkMode, 1);
        Assert.Equal(QuickToggleId.MicMute, catalog.Listed(false)[0]);
        catalog.SetVisible(QuickToggleId.MicMute, false);
        Assert.DoesNotContain(QuickToggleId.MicMute, catalog.Listed(false));
        Assert.Contains(QuickToggleId.MicMute, catalog.Listed(true));
        catalog.Reset();
        Assert.Equal(QuickToggleId.DarkMode, catalog.Listed(false)[0]);
        Assert.Contains(QuickToggleId.MicMute, catalog.Listed(false));
    }

    [AvaloniaFact]
    public async Task Toggle_flips_the_title()
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var catalog = services.GetRequiredService<QuickToggleCatalog>();
        var before = catalog.Title(QuickToggleId.HiddenFiles);
        Assert.True(await catalog.RunAsync(QuickToggleId.HiddenFiles, null, null));
        Assert.NotEqual(before, catalog.Title(QuickToggleId.HiddenFiles));
        Assert.True(await catalog.RunAsync(QuickToggleId.HiddenFiles, null, null));
        Assert.Equal(before, catalog.Title(QuickToggleId.HiddenFiles));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var vm = new SettingsViewModel(services);
        vm.Navigate(QuickTogglesModule.PageId);
        Assert.Equal(QuickTogglesModule.PageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 1200, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        ClipboardRenderTests.SaveWindow(window, $"settings-quickToggles-{theme}");
        window.Close();
    }
}
