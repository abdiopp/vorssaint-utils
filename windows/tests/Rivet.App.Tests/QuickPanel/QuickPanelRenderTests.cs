// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Features.QuickPanel;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.App.Tests.Clipboard;
using Rivet.App.Tests.QuickToggles;
using Rivet.Core.Features;
using Rivet.Core.QuickPanel;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.App.Tests.QuickPanel;

public class QuickPanelRenderTests
{
    private static readonly Setting<bool> SampleKeepAwake = new("quickPanelTestsKeepAwake", true);

    internal static IServiceProvider Seed()
    {
        var services = TestApp.Host.Services;
        ClipboardRenderTests.Seed(services);
        QuickToggleRenderTests.Seed(services);
        var runtime = services.GetRequiredService<FeatureRuntime>();
        foreach (var feature in new[]
                 {
                     FeatureIds.QuickLauncher, FeatureIds.KeepAwake, FeatureIds.Screenshot, FeatureIds.ScreenRecorder, FeatureIds.Cleaner,
                     FeatureIds.ScreenOcr, FeatureIds.ColorPicker,
                 })
        {
            runtime.SetAvailable(feature, true);
        }

        var panel = services.GetRequiredService<PanelRegistry>();
        foreach (var (id, feature, title, icon) in new[]
                 {
                     ("screenshot", FeatureIds.Screenshot, "screenshot.pageTitle", "Screenshot"),
                     ("screenRecorder", FeatureIds.ScreenRecorder, "recorder.pageTitle", "Record"),
                     ("cleaner", FeatureIds.Cleaner, "Strings.cleanerName", "Sparkle"),
                     ("screenOCR", FeatureIds.ScreenOcr, "Strings.ocrName", "ScanText"),
                     ("colorPicker", FeatureIds.ColorPicker, "Strings.colorPickerName", "Eyedropper"),
                 })
        {
            // Stand-ins for other modules' Utilities tiles.
            if (!panel.Tiles.Any(t => t.Id == id))
            {
                panel.AddTile(new PanelTileDescriptor { Id = id, FeatureId = feature, TitleKey = title, Icon = icon, ActionId = id });
            }
        }

        if (!panel.Toggles.Any(t => t.FeatureId == FeatureIds.KeepAwake))
        {
            // Stands in for the keep-awake module's Controls switch.
            panel.AddToggle(new PanelToggleDescriptor { Id = "keepAwake", FeatureId = FeatureIds.KeepAwake, TitleKey = "Strings.keepAwakeTitle", Icon = "WeatherMoon", Setting = SampleKeepAwake });
        }

        services.GetRequiredService<QuickPanelCatalog>().Reset();
        return services;
    }

    private static QuickPanelWindow Open(IServiceProvider services, string theme)
    {
        var window = new QuickPanelWindow(services) { RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Open();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Grid_renders(string theme)
    {
        var services = Seed();
        var window = Open(services, theme);
        Assert.Equal("keepAwake", window.Tiles[0].Id);
        Assert.Contains(window.Tiles, t => t.Id == "toggles");
        Assert.Contains(window.Tiles, t => t.Id == "clipboard");
        ClipboardRenderTests.SaveWindow(window, $"quick-panel-{theme}");
        window.Dismiss(FloatingCloseReason.Action);
    }

    [AvaloniaFact]
    public void Edit_mode_renders_with_options_and_add_back()
    {
        var services = Seed();
        var catalog = services.GetRequiredService<QuickPanelCatalog>();
        catalog.SetHidden("screenRecorder", true);
        var window = Open(services, "light");
        window.SetEditing(true);
        var clipboardTile = window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Tag, "clipboard"));
        clipboardTile.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        ClipboardRenderTests.SaveWindow(window, "quick-panel-editing");
        Assert.DoesNotContain(window.Tiles, t => t.Id == "screenRecorder");
        window.Dismiss(FloatingCloseReason.Action);
        catalog.Reset();
    }

    [AvaloniaFact]
    public async Task Hosted_tool_replaces_the_grid_and_escape_returns()
    {
        var services = Seed();
        var window = Open(services, "dark");
        await window.ActivateAsync(window.Tiles.First(t => t.Id == "toggles"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("toggles", window.HostedTile?.Id);
        ClipboardRenderTests.SaveWindow(window, "quick-panel-hosted");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Null(window.HostedTile);
        Assert.True(window.IsVisible);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Arrows_move_clamped_and_switch_tiles_flip()
    {
        var services = Seed();
        var settings = services.GetRequiredService<ISettingsStore>();
        var window = Open(services, "light");
        Assert.Equal(0, window.SelectedIndex);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(0, window.SelectedIndex);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(3, window.SelectedIndex);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(4, window.SelectedIndex);
        var before = settings.Get(SampleKeepAwake);
        window.KeyPress(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
        Assert.NotEqual(before, settings.Get(SampleKeepAwake));
        Assert.True(window.IsVisible);
        settings.Set(SampleKeepAwake, before);
        window.Dismiss(FloatingCloseReason.Action);
    }

    [AvaloniaFact]
    public void Order_and_hidden_tiles_persist()
    {
        var services = Seed();
        var catalog = services.GetRequiredService<QuickPanelCatalog>();
        var settings = services.GetRequiredService<ISettingsStore>();
        catalog.MoveTo("clipboard", 0);
        Assert.Equal("clipboard", catalog.Ordered(false)[0].Id);
        Assert.StartsWith("clipboard,", settings.Get(QuickPanelSettings.ItemOrder), StringComparison.Ordinal);
        catalog.SetHidden("toggles", true);
        catalog.SetHidden("cleaner", true);
        Assert.Equal("cleaner,toggles", settings.Get(QuickPanelSettings.HiddenItems));
        Assert.DoesNotContain(catalog.Ordered(false), t => t.Id == "toggles");
        catalog.Reset();
        Assert.Equal("keepAwake", catalog.Ordered(false)[0].Id);
    }

    [AvaloniaFact]
    public void Toggle_action_tiles_are_lit_while_their_action_reports_on()
    {
        var catalog = Seed().GetRequiredService<QuickPanelCatalog>();
        var on = false;
        var tile = new QuickPanelTile
        {
            Id = "micMute", FeatureId = FeatureIds.MicMute, TitleKey = "Strings.micMuteName", Icon = "MicOff",
            Kind = QuickTileKind.Action, ActionId = "micMute.toggle", IsOn = () => on,
        };
        Assert.False(catalog.IsLive(tile));
        on = true;
        Assert.True(catalog.IsLive(tile));
        Assert.False(catalog.IsLive(tile with { IsOn = null }));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var services = Seed();
        var vm = new SettingsViewModel(services);
        vm.Navigate(QuickPanelModule.PageId);
        Assert.Equal(QuickPanelModule.PageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 1100, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        ClipboardRenderTests.SaveWindow(window, $"settings-quickLauncher-{theme}");
        window.Close();
    }
}
