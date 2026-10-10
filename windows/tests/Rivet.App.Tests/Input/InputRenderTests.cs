// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Input;
using Rivet.App.Modules;
using Rivet.App.Shell.Sections;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.Input;
using Xunit;

namespace Rivet.App.Tests.Input;

/// <summary>A service provider that overrides a few services of the shared test host.</summary>
internal sealed class OverlayServices(IServiceProvider inner) : IServiceProvider
{
    private readonly Dictionary<Type, object> _overrides = [];

    public OverlayServices With<T>(T instance)
        where T : notnull
    {
        _overrides[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType) =>
        _overrides.TryGetValue(serviceType, out var instance) ? instance : inner.GetService(serviceType);
}

public class InputRenderTests
{
    private static ThemeVariant Theme(string theme) => theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>Fresh settings plus running services built on them, so statuses show.</summary>
    private static (OverlayServices Services, InputRig Rig) Services(Action<ISettingsStore>? configure = null)
    {
        var rig = new InputRig();
        configure?.Invoke(rig.Settings);
        var runtime = new FeatureRuntime(rig.Settings);
        var keyDebounce = rig.KeyDebounce();
        keyDebounce.Sync(true);
        var inverter = rig.Inverter();
        inverter.Sync(true);
        var superKey = rig.SuperKey();
        superKey.Sync(true);
        var buttons = rig.MouseButtons();
        buttons.Sync(true);
        var services = new OverlayServices(TestApp.Host.Services)
            .With<ISettingsStore>(rig.Settings)
            .With(runtime)
            .With(keyDebounce)
            .With(inverter)
            .With(superKey)
            .With(buttons)
            .With<IAppCatalog>(new FakeAppCatalog())
            .With<IKeyboardInfo>(rig.Keyboard);
        return (services, rig);
    }

    private static Control Page(Control page) => new Border { Padding = new Thickness(28, 22, 32, 32), Child = page };

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Mouse_page_renders(string theme)
    {
        _ = TestApp.Host;
        var (services, _) = Services(s =>
        {
            s.Set(FeatureKeys.MouseClickDebounceEnabled, true);
            s.Set(FeatureKeys.SmoothScrollEnabled, true);
            s.Set(FeatureKeys.ScrollInverterEnabled, true);
            s.Set(InputSettings.ScrollInverterExceptions, [@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"]);
        });
        TestApp.Snapshot(Page(new MouseSettingsPage(services)), $"input-mouse-{theme}", 900, theme: Theme(theme));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Mouse_buttons_page_renders(string theme)
    {
        _ = TestApp.Host;
        var (services, _) = Services(s =>
        {
            s.Set(FeatureKeys.MouseButtonShortcutsEnabled, true);
            s.Set(InputSettings.MouseButtonShortcuts, new Dictionary<string, string> { ["3"] = "alt:0x25", ["4"] = "alt:0x27", ["-1"] = "ctrl:0x09", ["-2"] = "ctrl+shift:0x09" });
            s.Set(InputSettings.DesktopGestureEnabled, true);
            s.Set(InputSettings.DesktopGestureButton, 5);
        });
        TestApp.Snapshot(Page(new MouseButtonsSettingsPage(services)), $"input-mousebuttons-{theme}", 900, theme: Theme(theme));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Debounce_page_renders(string theme)
    {
        _ = TestApp.Host;
        var (services, _) = Services(s =>
        {
            s.Set(FeatureKeys.KeyboardDebounceEnabled, true);
            s.Set(InputSettings.KeyDebounceWindowMs, 12);
            s.Set(InputSettings.KeyDebounceKeyWindows, "sc12:40,sc39:0,sc1E:100");
        });
        TestApp.Snapshot(Page(new KeyDebounceSettingsPage(services)), $"input-debounce-{theme}", 900, theme: Theme(theme));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Super_key_page_renders(string theme)
    {
        _ = TestApp.Host;
        var (services, _) = Services(s =>
        {
            s.Set(FeatureKeys.SuperKeyEnabled, true);
            s.Set(InputSettings.SuperKeySoloAction, SuperKeySoloActions.Escape);
        });
        TestApp.Snapshot(Page(new SuperKeySettingsPage(services)), $"input-superkey-{theme}", 900, theme: Theme(theme));

        var (office, _) = Services(s =>
        {
            s.Set(FeatureKeys.SuperKeyEnabled, true);
            s.Set(InputSettings.SuperKeyModifiers, "control+option+shift+command");
            s.Set(InputSettings.SuperKeySource, SuperKeySources.RightControl);
        });
        TestApp.Snapshot(Page(new SuperKeySettingsPage(office)), $"input-superkey-office-{theme}", 900, theme: Theme(theme));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Quit_protection_page_renders(string theme)
    {
        _ = TestApp.Host;
        var (services, _) = Services(s =>
        {
            s.Set(FeatureKeys.QuitProtectionQuitEnabled, true);
            s.Set(FeatureKeys.QuitProtectionCloseEnabled, true);
            s.Set(QuitProtectionSlotSettings.Close.Mode, QuitProtectionModes.ExtraModifier);
            s.Set(QuitProtectionSlotSettings.Close.Scope, QuitProtectionScopes.AllExceptSelected);
            s.Set(QuitProtectionSlotSettings.Close.Exceptions, [@"C:\Program Files\Mozilla Firefox\firefox.exe"]);
        });
        TestApp.Snapshot(Page(new QuitProtectionSettingsPage(services)), $"input-quitprotection-{theme}", 900, theme: Theme(theme));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Controls_rows_render(string theme)
    {
        _ = TestApp.Host;
        var settings = SettingsStore.InMemory();
        settings.Set(FeatureKeys.ScrollInverterEnabled, true);
        settings.Set(FeatureKeys.KeyboardDebounceEnabled, true);
        settings.Set(FeatureKeys.QuitProtectionQuitEnabled, true);
        var panel = new PanelRegistry();
        InputModule.AddToggles(panel);
        Assert.Equal(8, panel.Toggles.Count);
        Assert.All(panel.Toggles, t => Assert.True(IconConverter.IsKnown(t.Icon), t.Icon));
        var services = new OverlayServices(TestApp.Host.Services)
            .With<ISettingsStore>(settings)
            .With(new FeatureRuntime(settings))
            .With(panel);
        var surface = new Border { Padding = new Thickness(12), Child = new ControlsSection(services) };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        TestApp.Snapshot(surface, $"input-controls-{theme}", 340, theme: Theme(theme));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Quit_protection_hud_renders(string theme)
    {
        _ = TestApp.Host;
        var hold = new QuitProtectionHudView();
        hold.Update(new QuitHudContent("Hold Alt+F4 to quit", "Esc cancels", TimeSpan.FromMilliseconds(800)), 0.4);
        var press = new QuitProtectionHudView();
        press.Update(new QuitHudContent("Press Ctrl+W again to close", "Esc cancels", null), 0);
        var extra = new QuitProtectionHudView();
        extra.Update(new QuitHudContent("Use Ctrl+Shift+W to close", null, null), 0);
        var stack = new StackPanel { Spacing = 16, Margin = new Thickness(24), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Children = { hold, press, extra } };
        TestApp.Snapshot(stack, $"input-quithud-{theme}", 420, theme: Theme(theme));
        Assert.True(hold.HasProgress);
        Assert.False(press.HasProgress);
        Assert.InRange(hold.Bounds.Height, 55.5, 56.5);
        Assert.InRange(press.Bounds.Height, 47.5, 48.5);
        Assert.True(press.Bounds.Width >= QuitProtectionHudView.MinPillWidth - 0.5);
    }

    [AvaloniaFact]
    public void Input_pages_and_toggles_are_registered_with_known_icons()
    {
        var host = TestApp.Host;
        var pages = host.Services.GetRequiredService<SettingsPageRegistry>();
        foreach (var id in new[] { InputModule.MousePageId, InputModule.MouseButtonsPageId, InputModule.KeyDebouncePageId, InputModule.SuperKeyPageId, InputModule.QuitProtectionPageId })
        {
            var page = pages.Find(id);
            Assert.NotNull(page);
            Assert.Equal(SettingsCategory.MouseKeyboard, page!.Category);
            Assert.True(IconConverter.IsKnown(page.Icon), page.Icon);
        }

        Assert.Equal(InputModule.MousePageId, pages.ForFeature(FeatureIds.SmoothScroll)?.Id);
        Assert.Equal(InputModule.QuitProtectionPageId, pages.ForFeature(FeatureIds.QuitWindowProtection)?.Id);
        foreach (var icon in new[] { "Timer", "TopSpeed", "ArrowSwap", "DesktopArrowRight", "ArrowBidirectionalLeftRight", "Eye", "AppsList", "KeyboardShiftUppercase", "Add", "Cursor", "HourglassHalf", "Keyboard" })
        {
            Assert.True(IconConverter.IsKnown(icon), icon);
        }
    }
}
