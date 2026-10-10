// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): the replacement overload is not needed for test snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Recording;
using Rivet.App.Features.Recording.Views;
using Rivet.App.Settings;
using Rivet.Core.Platform;
using Xunit;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Tests.Recording;

public class RecordingRenderTests
{
    [AvaloniaFact]
    public void Indicator_pill_renders_recording_paused_and_confirming_discard()
    {
        _ = TestApp.Host;
        var recording = new RecordingIndicatorView();
        recording.Update("1:02:03", paused: false);
        TestApp.Snapshot(Centered(recording), "recording-indicator", 260);

        var paused = new RecordingIndicatorView();
        paused.Update("12:34", paused: true);
        Assert.True(paused.IsPaused);
        TestApp.Snapshot(Centered(paused), "recording-indicator-paused", 260, theme: ThemeVariant.Dark);
    }

    [AvaloniaFact]
    public void Discard_needs_a_second_click()
    {
        _ = TestApp.Host;
        var view = new RecordingIndicatorView();
        var window = new Window { Content = view, Width = 260, Height = 80 };
        window.Show();
        var confirmed = 0;
        view.DiscardConfirmed += (_, _) => confirmed++;
        var discard = view.GetVisualDescendants().OfType<Button>().Last();
        discard.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(view.IsConfirmingDiscard);
        Assert.Equal(0, confirmed);
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, "recording-indicator-discard.png"));
        discard.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, confirmed);
        Assert.False(view.IsConfirmingDiscard);
        window.Close();
    }

    [AvaloniaFact]
    public void Countdown_renders_the_number_and_a_draining_ring()
    {
        _ = TestApp.Host;
        var view = new CountdownView();
        view.Start(3);
        view.Progress = 0.6;
        TestApp.Snapshot(Centered(view), "recording-countdown", 160, theme: ThemeVariant.Dark);
        Assert.Equal(3, view.Number);
    }

    [AvaloniaFact]
    public void Region_guide_dims_outside_the_region_at_any_scale()
    {
        _ = TestApp.Host;
        // A 1920×1080 monitor at 200 %: the window is 960×540 DIPs.
        var view = new RegionGuideView
        {
            Monitor = new PixelRect(0, 0, 1920, 1080),
            Region = new PixelRect(400, 200, 800, 500),
            PixelScale = 2,
        };
        var backdrop = new Grid
        {
            Width = 960,
            Height = 540,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(240, 200, 120), 0), new GradientStop(Color.FromRgb(90, 160, 230), 1) },
            },
            Children = { view },
        };
        TestApp.Snapshot(backdrop, "recording-region-guide", 960, 540);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var host = TestApp.Host;
        var vm = new SettingsViewModel(host.Services);
        vm.Navigate(RecordingModule.SettingsPageId);
        Assert.Equal(RecordingModule.SettingsPageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 1300, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"recording-settings-{theme}.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void Every_icon_the_module_uses_exists()
    {
        string[] icons = ["Record", "RecordStop", "Pause", "Play", "Stop", "Delete", "Checkmark", "Timer", "VideoClip", "Speaker2", "SpeakerOff", "Mic", "MicOff", "Keyboard", "FolderOpen", "Edit", "Dismiss", "CheckmarkCircle"];
        Assert.All(icons, i => Assert.True(IconConverter.IsKnown(i), i));
    }

    private static Control Centered(Control content) => new Border
    {
        Padding = new Thickness(20),
        Child = new Border { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Child = content },
    };
}
