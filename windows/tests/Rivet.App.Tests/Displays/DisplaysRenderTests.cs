// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Displays;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.App.Modules;
using Rivet.Core.Displays;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Fake.Displays;
using Rivet.Platform.Fake.Shell;
using Xunit;

namespace Rivet.App.Tests.Displays;

public class DisplaysRenderTests
{
    private static ThemeVariant Theme(string theme) => theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static Control Panel(Control content)
    {
        var surface = new Border { Width = 340, Padding = new Thickness(12), Child = content };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        return surface;
    }

    /// <summary>Turns "Control displays" off and on again and waits for the first scan of the fake displays.</summary>
    private static BrightnessService Restart(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsStore>();
        var service = services.GetRequiredService<BrightnessService>();
        settings.Set(DisplaySettings.Enabled, false);
        Pump(() => !service.IsRunning);
        settings.Set(DisplaySettings.Enabled, true);
        Pump(() => service.IsReady && service.Displays.Count == 3);
        Assert.True(service.IsReady, "the brightness service never finished its first scan");
        return service;
    }

    private static void Pump(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void OpenAll(Control root)
    {
        foreach (var fold in root.GetLogicalDescendants().OfType<Fold>().ToList())
        {
            fold.IsOpen = true;
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Section_renders_every_route(string theme)
    {
        var services = TestApp.Host.Services;
        var service = Restart(services);
        Assert.Equal(
            [BrightnessRoute.System, BrightnessRoute.Ddc, BrightnessRoute.Ddc],
            service.Displays.Select(d => d.Route));
        Assert.Equal(DdcState.Unknown, service.Displays[2].Ddc);

        var section = new DisplaysSection(services);
        OpenAll(section);
        TestApp.Snapshot(Panel(section), $"displays-section-{theme}", 340, theme: Theme(theme));

        // The TV rejects the first write: it moves to dimming and the overlay follows the slider.
        service.SetLevel(FakeDisplayCatalog.Tv, 0.4);
        Pump(() => service.Displays[2].Route == BrightnessRoute.Software);
        Assert.Equal(BrightnessRoute.Software, service.Displays[2].Route);
        var overlays = services.GetRequiredService<DimmingOverlays>();
        Pump(() => overlays.Factors.ContainsKey(FakeDisplayCatalog.Tv));
        Assert.Equal(0.4, overlays.Factors[FakeDisplayCatalog.Tv], 3);
        TestApp.Snapshot(Panel(new DisplaysSection(services)), $"displays-section-dimmed-{theme}", 340, theme: Theme(theme));

        // Turning the feature off removes every overlay and leaves the hardware alone.
        var ddc = services.GetRequiredService<FakeDdcChannel>();
        var monitorLevel = ddc.Values[FakeDisplayCatalog.Monitor];
        services.GetRequiredService<ISettingsStore>().Set(DisplaySettings.Enabled, false);
        Pump(() => !service.IsRunning);
        Assert.Empty(overlays.Factors);
        Assert.Equal(monitorLevel, ddc.Values[FakeDisplayCatalog.Monitor]);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Restart(services);
        var page = new DisplaysSettingsPage(services);
        TestApp.Snapshot(new Border { Padding = new Thickness(24), Child = page }, $"settings-displays-page-{theme}", 820, 1500, Theme(theme));
    }

    [AvaloniaFact]
    public void Settings_page_explains_when_display_control_is_off()
    {
        var services = TestApp.Host.Services;
        var settings = services.GetRequiredService<ISettingsStore>();
        settings.Set(DisplaySettings.Enabled, false);
        try
        {
            var page = new DisplaysSettingsPage(services);
            TestApp.Snapshot(new Border { Padding = new Thickness(24), Child = page }, "settings-displays-page-off-light", 820, 900);
            var texts = page.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains(Rivet.Core.Localization.L.Get("win.displays.disabledCaption"), texts);
        }
        finally
        {
            settings.Set(DisplaySettings.Enabled, true);
        }
    }

    [AvaloniaFact]
    public void Osd_renders()
    {
        var view = new BrightnessOsdView { Level = 0.62 };
        var surface = new Border { Width = 260, Height = 210, Background = new SolidColorBrush(Color.FromRgb(0x5B, 0x7F, 0xA6)), Child = view };
        view.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        view.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        TestApp.Snapshot(surface, "displays-osd", 260, 210);
        Assert.Equal(10, BrightnessMath.FilledSegments(0.62));
    }

    [AvaloniaFact]
    public void Registrations_follow_the_spec()
    {
        var services = TestApp.Host.Services;
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        var decrease = shortcuts.Find(DisplaysModule.DecreaseRoleId);
        var increase = shortcuts.Find(DisplaysModule.IncreaseRoleId);
        Assert.NotNull(decrease);
        Assert.NotNull(increase);
        var mods = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win;
        Assert.Equal(KeyChord.Of(mods, VirtualKeys.OemMinus), decrease!.Default);
        Assert.Equal(KeyChord.Of(mods, VirtualKeys.OemPlus), increase!.Default);
        Assert.Equal(["brightnessControlEnabled", "displayBrightnessShortcutsEnabled"], decrease.RequiredEnableKeys.Select(k => k.Key));

        var section = services.GetRequiredService<PanelRegistry>().Sections.Single(s => s.Id == DisplaysModule.SectionId);
        var settings = services.GetRequiredService<ISettingsStore>();
        settings.Set(DisplaySettings.Enabled, false);
        Assert.False(section.IsVisible!());
        settings.Set(DisplaySettings.Enabled, true);
        Assert.True(section.IsVisible!());
    }

    [AvaloniaFact]
    public void Shortcuts_step_the_main_display_once_enabled()
    {
        var services = TestApp.Host.Services;
        var service = Restart(services);
        var settings = services.GetRequiredService<ISettingsStore>();
        var hotkeys = services.GetRequiredService<FakeHotkeyService>();
        var panel = services.GetRequiredService<FakeSystemBrightness>();
        var chord = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.OemPlus);
        Assert.False(hotkeys.Press(chord)); // off by default
        settings.Set(DisplaySettings.ShortcutsEnabled, true);
        try
        {
            Dispatcher.UIThread.RunJobs();
            var before = service.Displays[0].Level;
            Assert.True(hotkeys.Press(chord));
            Pump(() => service.Displays[0].Level > before);
            Assert.Equal(BrightnessMath.Step(before, +1, 1.0 / 16), service.Displays[0].Level, 6);
            Pump(() => panel.Percent == BrightnessMath.ToPercent(service.Displays[0].Level));
            Assert.Equal(BrightnessMath.ToPercent(service.Displays[0].Level), panel.Percent);
        }
        finally
        {
            settings.Set(DisplaySettings.ShortcutsEnabled, false);
        }
    }
}
