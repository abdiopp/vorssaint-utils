// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Awake;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Awake;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Fake.Awake;
using Rivet.Platform.Fake.Shell;
using Xunit;

namespace Rivet.App.Tests.Awake;

public class KeepAwakeRenderTests
{
    private static Control Panel(Control content)
    {
        var surface = new Border { Width = 340, Padding = new Thickness(12), Child = content };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        return surface;
    }

    private static ThemeVariant Theme(string theme) => theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static readonly KeyChord Chord = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Letter('K'));

    /// <summary>Opens every fold, including the ones that only exist once their parent opened.</summary>
    private static void OpenAll(Control root)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var closed = root.GetLogicalDescendants().OfType<Fold>().Where(f => !f.IsOpen).ToList();
            if (closed.Count == 0)
            {
                return;
            }

            foreach (var fold in closed)
            {
                fold.IsOpen = true;
            }
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Card_renders_idle_and_active(string theme)
    {
        var services = TestApp.Host.Services;
        var manager = services.GetRequiredService<KeepAwakeManager>();
        manager.Stop();
        TestApp.Snapshot(Panel(new KeepAwakeCard(services)), $"keepawake-card-idle-{theme}", 340, theme: Theme(theme));
        try
        {
            manager.Activate(60);
            var card = new KeepAwakeCard(services);
            OpenAll(card);
            TestApp.Snapshot(Panel(card), $"keepawake-card-active-{theme}", 340, theme: Theme(theme));
        }
        finally
        {
            manager.Stop();
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Card_renders_the_battery_note_and_automation(string theme)
    {
        var services = TestApp.Host.Services;
        var manager = services.GetRequiredService<KeepAwakeManager>();
        var power = services.GetRequiredService<FakePowerSource>();
        var settings = services.GetRequiredService<ISettingsStore>();
        manager.Stop();
        try
        {
            power.BatteryPercent = 7;
            manager.Activate(30);
            Assert.False(manager.IsActive);
            settings.Set(KeepAwakeSettings.AutomationApps, true);
            settings.Set(KeepAwakeSettings.AutomationPower, true);
            settings.Set(KeepAwakeSettings.AutomationAppList, ["zoom.exe", "obs64.exe"]);
            var card = new KeepAwakeCard(services);
            OpenAll(card);
            TestApp.Snapshot(Panel(card), $"keepawake-card-battery-{theme}", 340, theme: Theme(theme));
        }
        finally
        {
            power.BatteryPercent = 78;
            power.Raise();
            settings.Reset(KeepAwakeSettings.AutomationApps.Key);
            settings.Reset(KeepAwakeSettings.AutomationPower.Key);
            settings.Reset(KeepAwakeSettings.AutomationAppList.Key);
            manager.Stop();
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var services = TestApp.Host.Services;
        var page = new KeepAwakeSettingsPage(services);
        TestApp.Snapshot(new Border { Padding = new Thickness(24), Child = page }, $"settings-keepawake-page-{theme}", 820, 2600, Theme(theme));
    }

    [AvaloniaFact]
    public void Registrations_follow_the_spec()
    {
        var services = TestApp.Host.Services;
        Assert.NotNull(services.GetRequiredService<ActionRegistry>().Get(KeepAwakeModule.ToggleActionId));
        var role = services.GetRequiredService<ShortcutManager>().Find(KeepAwakeModule.ShortcutRoleId);
        Assert.NotNull(role);
        Assert.Equal(Chord, role!.Default);
        Assert.Contains(role.RequiredEnableKeys, k => k.Key == "hotkeyEnabled");
        var menu = services.GetRequiredService<TrayMenuRegistry>().Items;
        Assert.Contains(menu, i => i.Id == "keepAwake.toggle" && i.Order < 0);
        Assert.Equal(7, menu.Count(i => i.Id.StartsWith("keepAwake.activate.", StringComparison.Ordinal)));
        Assert.Contains(services.GetRequiredService<PanelRegistry>().Sections, s => s.Id == KeepAwakeModule.SectionId);
    }

    [AvaloniaFact]
    public void Shortcut_toggles_and_the_tray_shows_the_session()
    {
        var services = TestApp.Host.Services;
        var manager = services.GetRequiredService<KeepAwakeManager>();
        var hotkeys = services.GetRequiredService<FakeHotkeyService>();
        var requests = services.GetRequiredService<FakePowerRequests>();
        manager.Stop();
        try
        {
            Assert.True(hotkeys.Press(Chord));
            Assert.True(manager.IsActive);
            Assert.True(requests.System);
            Assert.Contains("Awake", KeepAwakePresenter.TooltipLine(manager, showCountdown: false), StringComparison.Ordinal);
            Assert.True(hotkeys.Press(Chord));
            Assert.False(manager.IsActive);
            Assert.False(requests.System);
        }
        finally
        {
            manager.Stop();
        }
    }
}
