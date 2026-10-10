// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string) is fine for snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.CleaningMode;
using Rivet.App.Settings;
using Rivet.Core.Features;
using Rivet.Core.Modules.CleaningMode;
using Rivet.Core.Platform;
using Rivet.Platform.Fake.CleaningMode;
using Rivet.Platform.Fake.Shell;
using Xunit;

namespace Rivet.App.Tests.CleaningMode;

public class CleaningModeRenderTests
{
    [AvaloniaFact]
    public void The_black_overlay_renders()
    {
        var dots = new CleaningProgressDots(12, 11, Brushes.White, new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)));
        dots.SetProgress(2);
        var content = new Border { Background = Brushes.Black, Child = CleaningOverlayWindow.BuildContent(() => { }, dots) };
        TestApp.Snapshot(content, "cleaning-overlay", 960, 600, ThemeVariant.Dark);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_corner_indicator_renders(string theme)
    {
        var dots = new CleaningProgressDots(6, 4, Brushes.DodgerBlue, Brushes.Gray);
        dots.SetProgress(3);
        var host = new Border { Padding = new Thickness(20) };
        host.Child = CleaningIndicatorWindow.BuildContent(() => { }, dots, host);
        TestApp.Snapshot(host, $"cleaning-indicator-{theme}", 460, theme: theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    [AvaloniaFact]
    public void The_settings_page_renders()
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.CleaningMode, true);
        foreach (var theme in new[] { "light", "dark" })
        {
            var vm = new SettingsViewModel(host.Services);
            vm.Navigate(CleaningModeModule.PageId);
            Assert.Equal(CleaningModeModule.PageId, vm.CurrentPageId);
            var window = new SettingsWindow(vm) { Width = 1080, Height = 760, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
            window.Show();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"settings-cleaningMode-{theme}.png"));
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Locking_shows_one_overlay_per_monitor_and_escape_five_times_unlocks()
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.CleaningMode, true);
        var service = host.Services.GetRequiredService<CleaningModeService>();
        var hooks = host.Services.GetRequiredService<FakeInputHooks>();
        service.Activate();
        Assert.True(service.IsActive);
        Assert.NotEmpty(service.Windows);
        Assert.True(hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x41, Action = KeyAction.Down }));
        Assert.True(hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.Wheel }));
        Assert.False(hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftDown }));
        hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftUp });
        for (var i = 0; i < 5; i++)
        {
            hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x1B, Action = KeyAction.Down });
            hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x1B, Action = KeyAction.Up });
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(service.IsActive);
        Assert.Empty(service.Windows);
        Assert.False(hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x41, Action = KeyAction.Down }));
        hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x41, Action = KeyAction.Up });
    }

    [AvaloniaFact]
    public void A_session_lock_ends_cleaning_mode_and_restores_input()
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.CleaningMode, true);
        var service = host.Services.GetRequiredService<CleaningModeService>();
        var platform = host.Services.GetRequiredService<FakeCleaningPlatform>();
        var hooks = host.Services.GetRequiredService<FakeInputHooks>();
        service.Activate();
        Assert.True(service.IsActive);
        platform.RaiseSession(SessionChange.Locked);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(service.IsActive);
        Assert.False(hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x42, Action = KeyAction.Down }));
        hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = 0x42, Action = KeyAction.Up });
    }
}
