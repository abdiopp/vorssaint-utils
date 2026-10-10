// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string) is fine for snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.RadialMenu;
using Rivet.App.Settings;
using Rivet.Core.Features;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Fake.RadialMenu;
using Rivet.Platform.Fake.Shell;
using Xunit;
using PixelPoint = Rivet.Core.Platform.PixelPoint;

namespace Rivet.App.Tests.RadialMenu;

public sealed class RadialMenuAppTests : IDisposable
{
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;

    private static RadialMenuService Service()
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.RadialMenu, true);
        host.Settings.Set(FeatureKeys.RadialMenuEnabled, true);
        host.Settings.Set(RadialMenuSettings.ActivationMode, "pressOrHold");
        host.Settings.Set(RadialMenuSettings.AtPointer, true);
        var service = host.Services.GetRequiredService<RadialMenuService>();
        service.Sync(true);
        service.Close();

        // A known wheel: the General preset.
        var profile = RadialProfilesCodec.DefaultProfile();
        service.SaveProfiles([profile]);
        var hooks = host.Services.GetRequiredService<FakeInputHooks>();
        foreach (var vk in new[] { VkControl, VkMenu, VkLWin })
        {
            hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = vk, Action = KeyAction.Up });
        }

        host.Services.GetRequiredService<FakeScreens>().CursorPosition = new PixelPoint(960, 540);
        return service;
    }

    /// <summary>
    /// An open wheel swallows Enter, Esc, arrows and digits from the shared hooks, which would break
    /// later tests in the same host (e.g. the recorder's chooser), so a failed test must not leave one open.
    /// </summary>
    public void Dispose()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            TestApp.Host.Services.GetService<RadialMenuService>()?.Close();
        }
    }

    private static FakeInputHooks Hooks => TestApp.Host.Services.GetRequiredService<FakeInputHooks>();

    private static void Pump(Func<bool> until, double seconds = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!until() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_default_wheel_opens_with_its_runnable_slices()
    {
        var service = Service();
        var profile = service.Profiles[0];
        Assert.Equal(KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Space).ToStorageString(), profile.Shortcut);
        Assert.Equal(profile.Shortcut, TestApp.Host.Settings.Get(RadialMenuService.SlotShortcut[0]));
        Assert.True(TestApp.Host.Settings.Get(RadialMenuService.SlotActive[0]));

        service.TryIt(profile);
        var session = Assert.IsType<RadialSession>(service.Session);
        Assert.Equal(RadialPhase.Sticky, session.Phase);
        // Every General slice runs here: the capture module provides the Screenshot and Color picker tools.
        Assert.Equal(6, session.CurrentItems.Count);
        Assert.Equal("screenshot.capture", service.Presenter.ResolveTool("screenshot")?.Id);
        Assert.Equal("colorPicker.pick", service.Presenter.ResolveTool("colorPicker")?.Id);
        service.Close();
        Assert.Null(service.Session);
    }

    [AvaloniaFact]
    public void Quick_toggle_slices_resolve_to_the_quick_toggles_actions()
    {
        var service = Service();
        Assert.Equal("quickToggles.darkMode", service.Presenter.ResolveQuickToggle("darkMode")?.Id);
        Assert.Equal("quickToggles.lockScreen", service.Presenter.ResolveQuickToggle("lockScreen")?.Id);
    }

    [AvaloniaFact]
    public void A_wheel_on_the_back_button_claims_it_from_mouse_button_shortcuts()
    {
        var service = Service();
        var claims = TestApp.Host.Services.GetRequiredService<Rivet.Core.Input.IMouseButtonClaims>();
        Assert.Same(service, claims);
        Assert.False(claims.IsClaimed(RadialMouseTrigger.Back));

        service.SaveProfiles([service.Profiles[0] with { MouseButton = new RadialMouseTrigger(RadialMouseTrigger.Back) }]);
        Assert.True(claims.IsClaimed(RadialMouseTrigger.Back));
        Assert.False(claims.IsClaimed(RadialMouseTrigger.Forward));

        service.SaveProfiles([service.Profiles[0] with { MouseButton = RadialMouseTrigger.Off }]);
        Assert.False(claims.IsClaimed(RadialMouseTrigger.Back));
    }

    [AvaloniaFact]
    public void Holding_the_shortcut_pointing_and_releasing_runs_the_slice()
    {
        var service = Service();
        foreach (var vk in new[] { VkControl, VkMenu, VkLWin })
        {
            Hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = vk, Action = KeyAction.Down });
        }

        service.OnShortcut(0);
        Assert.Equal(RadialPhase.Held, service.Session!.Phase);

        // Point straight up (slice 0, Play/Pause), past the 8 DIP arming travel and the 40 DIP dead zone.
        Assert.False(Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.Move, Position = new PixelPoint(960, 440) }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, service.Session!.Highlight);

        Hooks.Sent.Clear();
        Hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = VkLWin, Action = KeyAction.Up });
        Dispatcher.UIThread.RunJobs();
        Assert.Null(service.Session);
        Hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = VkControl, Action = KeyAction.Up });
        Hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = VkMenu, Action = KeyAction.Up });
        Pump(() => Hooks.Sent.Count >= 2);
        Assert.Contains("keys:B3↓", Hooks.Sent);
        Assert.Contains("keys:B3↑", Hooks.Sent);
    }

    [AvaloniaFact]
    public void A_quick_press_keeps_the_wheel_open_and_digits_run_slices()
    {
        var service = Service();
        service.OnShortcut(0);

        // No modifiers down: the release already happened, nothing highlighted → sticky.
        Assert.Equal(RadialPhase.Sticky, service.Session!.Phase);
        var platform = TestApp.Host.Services.GetRequiredService<FakeRadialPlatform>();
        platform.Log.Clear();
        // Digit 4 is the fourth slice of the General wheel: the Downloads folder.
        Assert.True(Hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x34, Action = KeyAction.Down }));
        Dispatcher.UIThread.RunJobs();
        Assert.False(Hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x41, Action = KeyAction.Down }));
        Assert.Null(service.Session);
        Pump(() => platform.Log.Count > 0);
        Assert.Contains(platform.Log, l => l.Contains("Downloads", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Escape_closes_and_an_outside_click_dismisses_without_being_swallowed()
    {
        var service = Service();
        service.TryIt(service.Profiles[0]);
        Assert.True(Hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x1B, Action = KeyAction.Down }));
        Assert.True(Hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x1B, Action = KeyAction.Up }));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(service.Session);

        service.TryIt(service.Profiles[0]);
        // A click on the ring with nothing highlighted is taken and does nothing.
        Assert.True(Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftDown, Position = new PixelPoint(960, 440) }));
        Assert.True(Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftUp, Position = new PixelPoint(960, 440) }));
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(service.Session);
        Assert.False(Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftDown, Position = new PixelPoint(100, 100) }));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(service.Session);
    }

    [AvaloniaFact]
    public void A_side_button_opens_its_wheel_and_both_halves_of_the_click_are_swallowed()
    {
        var service = Service();
        service.AssignMouseButton(service.Profiles[0].Id, new RadialMouseTrigger(RadialMouseTrigger.Back));
        Assert.True(Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.XDown, XButton = 1, Position = new PixelPoint(960, 540) }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RadialTriggerKind.MouseButton, service.Session!.Trigger);
        Assert.True(Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.XUp, XButton = 1, Position = new PixelPoint(960, 540) }));
        Dispatcher.UIThread.RunJobs();

        // Released with nothing highlighted in press-or-hold: the wheel stays open.
        Assert.Equal(RadialPhase.Sticky, service.Session!.Phase);

        // The Forward button is not claimed and passes through.
        Assert.False(Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.XDown, XButton = 2 }));
        service.Close();
        service.AssignMouseButton(service.Profiles[0].Id, RadialMouseTrigger.Off);
        Assert.False(Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.XDown, XButton = 1 }));
    }

    [AvaloniaFact]
    public void Editing_a_slot_on_the_shortcuts_page_writes_back_into_the_wheel()
    {
        var service = Service();
        var chord = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt, VirtualKeys.Letter('R'));
        TestApp.Host.Settings.Set(RadialMenuService.SlotShortcut[0], chord.ToStorageString());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(chord.ToStorageString(), service.Profiles[0].Shortcut);
    }

    [AvaloniaFact]
    public void Profiles_from_presets_get_names_colours_and_no_triggers()
    {
        var service = Service();
        var media = RadialPresets.NewProfile(RadialPreset.Media, service.Profiles.Count, Rivet.Core.Localization.L.Get);
        service.SaveProfiles([.. service.Profiles, media]);
        var saved = service.Profiles[1];
        Assert.Equal("Media", saved.Name);
        Assert.Equal(RadialColor.Purple, saved.Color);
        Assert.False(saved.HasTrigger);
        Assert.False(TestApp.Host.Settings.Get(RadialMenuService.SlotActive[1]));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_wheel_renders_with_a_highlighted_slice(string theme)
    {
        var service = Service();
        var profile = service.Profiles[0] with { Color = RadialColor.Purple, Items = [.. service.Profiles[0].Items, new RadialItem { Kind = RadialItemKind.Submenu, Name = "More", Children = [new RadialItem { Kind = RadialItemKind.Url, Payload = "https://example.org" }] }] };
        service.SaveProfiles([profile]);
        service.TryIt(service.Profiles[0]);
        var window = service.Window!;
        window.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.Move, Position = new PixelPoint(1060, 540) });
        Dispatcher.UIThread.RunJobs();
        window.Refresh();
        window.Wheel.Settle();

        // A backdrop behind the transparent wheel, like a desktop.
        var host = new Border { Background = theme == "dark" ? Avalonia.Media.Brushes.DimGray : Avalonia.Media.Brushes.LightSteelBlue, Width = 400, Height = 400 };
        window.Content = null;
        host.Child = window.Wheel;
        TestApp.Snapshot(host, $"radial-wheel-window-{theme}", 400, 400, window.RequestedThemeVariant);
        host.Child = null;
        window.Content = window.Wheel;
        service.Close();
    }

    [AvaloniaFact]
    public void The_settings_page_and_item_editor_render()
    {
        var host = TestApp.Host;
        Service();
        foreach (var theme in new[] { "light", "dark" })
        {
            var vm = new SettingsViewModel(host.Services);
            vm.Navigate(RadialMenuModule.PageId);
            Assert.Equal(RadialMenuModule.PageId, vm.CurrentPageId);
            var window = new SettingsWindow(vm) { Width = 1080, Height = 1400, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
            window.Show();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"settings-radialMenu-{theme}.png"));
            window.Close();

            var service = host.Services.GetRequiredService<RadialMenuService>();
            var editor = new RadialItemEditor(host.Services, service.Presenter, new RadialItem { Kind = RadialItemKind.Url, Payload = "example.org" }, allowSubmenu: true, isNew: false)
            {
                RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            editor.Show();
            using var editorFrame = editor.CaptureRenderedFrame();
            Assert.NotNull(editorFrame);
            editorFrame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"radial-item-editor-{theme}.png"));
            Assert.Equal("https://example.org", editor.Item.Payload);
            editor.Close();
        }
    }
}
