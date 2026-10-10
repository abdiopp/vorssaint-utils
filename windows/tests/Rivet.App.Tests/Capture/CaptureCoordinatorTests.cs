// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture;
using Rivet.Core.Actions;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.App.Tests.Capture;

/// <summary>The chooser around a running recording (spec 01 §3.3 step 2) and the Recording hand-off.</summary>
public class CaptureCoordinatorTests
{
    private sealed class FakeRecorder : IRecorderLink
    {
        public bool IsBusy { get; set; }

        public List<CaptureSelection> Handed { get; } = [];

        public Task<bool> TryRecordSelectionAsync(CaptureSelection selection)
        {
            Handed.Add(selection);
            return Task.FromResult(true);
        }
    }

    /// <summary>The test host with the recorder link swapped for a fake.</summary>
    private sealed class HostWithRecorder(IServiceProvider inner, IRecorderLink recorder) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IRecorderLink) ? recorder : inner.GetService(serviceType);
    }

    private static ISettingsStore Settings => TestApp.Host.Services.GetRequiredService<ISettingsStore>();

    private static (CaptureCoordinator Coordinator, FakeRecorder Recorder) Create(bool busy)
    {
        SelectorModel.ResetLastRegion();
        var recorder = new FakeRecorder { IsBusy = busy };
        return (new CaptureCoordinator(new HostWithRecorder(TestApp.Host.Services, recorder)), recorder);
    }

    private static void WaitForChooser(CaptureCoordinator coordinator, Task run) =>
        SelectorSessionTests.Pump(() => coordinator.Session is { Overlays.Count: > 0 } || run.IsCompleted);

    private static void Cancel(CaptureCoordinator coordinator, Task run)
    {
        coordinator.Session?.OnKey(SelectorKey.Escape, KeyAction.Down, KeyModifiers.None);
        SelectorSessionTests.Pump(() => run.IsCompleted);
    }

    [AvaloniaFact]
    public void A_running_recording_keeps_the_chooser_closed_for_buttons_and_menu_shortcuts()
    {
        var (coordinator, _) = Create(busy: true);

        Assert.True(coordinator.StartAsync(CaptureTool.Screenshot, ActionSource.Panel).IsCompleted);
        Assert.True(coordinator.StartAsync(CaptureTool.Text, ActionSource.CommandBar).IsCompleted);
        // From its shortcut with "show capture menu" on (the default).
        Assert.True(coordinator.StartAsync(CaptureTool.Color, ActionSource.Shortcut).IsCompleted);
        Assert.False(coordinator.IsActive);

        // Another module asking with the menu shown is turned away too.
        var select = coordinator.SelectAsync(new CaptureSelectorRequest { Tool = CaptureTool.Screenshot });
        Assert.True(select.IsCompleted);
        Assert.Null(select.Result);
    }

    [AvaloniaFact]
    public void A_menu_less_shortcut_opens_over_a_recording_with_only_its_tool()
    {
        Settings.Set(CaptureSettings.ScreenshotShowCaptureMenu, false);
        try
        {
            var (coordinator, _) = Create(busy: true);
            var run = coordinator.StartAsync(CaptureTool.Screenshot, ActionSource.Shortcut);
            WaitForChooser(coordinator, run);

            var session = coordinator.Session;
            Assert.NotNull(session);
            Assert.Equal([CaptureTool.Screenshot], session!.Model!.Config.Tools);
            // The digit for Recording does nothing.
            session.OnKey(SelectorKey.Digit2, KeyAction.Down, KeyModifiers.None);
            Assert.Equal(CaptureTool.Screenshot, session.Model.Tool);
            Cancel(coordinator, run);
        }
        finally
        {
            Settings.Reset(CaptureSettings.ScreenshotShowCaptureMenu.Key);
        }
    }

    [AvaloniaFact]
    public void The_recorders_own_request_opens_while_it_prepares_and_digits_still_switch()
    {
        var (coordinator, _) = Create(busy: true);
        var run = coordinator.SelectAsync(new CaptureSelectorRequest { Tool = CaptureTool.Recording, AllowToolSwitching = false });
        WaitForChooser(coordinator, run);

        var session = coordinator.Session;
        Assert.NotNull(session);
        Assert.Equal(CaptureTool.Recording, session!.Model!.Tool);
        Assert.Contains(CaptureTool.Screenshot, session.Model.Config.Tools);
        session.OnPointerMoved(new PointD(800, 500), KeyModifiers.None);
        var hint = session.HintStateFor(TestApp.Host.Services.GetRequiredService<IScreenService>().Primary);
        Assert.NotNull(hint);
        Assert.False(hint!.ShowPalette);
        Cancel(coordinator, run);
        Assert.Null(run.Result);
    }

    [AvaloniaFact]
    public void Recording_picked_in_another_tools_chooser_goes_to_the_recorder()
    {
        var runtime = TestApp.Host.Services.GetRequiredService<FeatureRuntime>();
        var wasAvailable = runtime.IsAvailable(FeatureIds.ScreenRecorder);
        runtime.SetAvailable(FeatureIds.ScreenRecorder, true);
        try
        {
            var (coordinator, recorder) = Create(busy: false);
            var run = coordinator.StartAsync(CaptureTool.Screenshot, ActionSource.Panel);
            WaitForChooser(coordinator, run);

            var session = coordinator.Session!;
            Assert.Contains(CaptureTool.Recording, session.Model!.Config.Tools);
            session.OnKey(SelectorKey.Digit2, KeyAction.Down, KeyModifiers.None);
            SelectorSessionTests.Pump(() => !session.Model.RefreshPending);
            Assert.Equal(CaptureTool.Recording, session.Model.Tool);

            session.OnPointerPressed(new PointD(11, 21), KeyModifiers.None);
            session.OnPointerMoved(new PointD(212, 160), KeyModifiers.None);
            session.OnPointerReleased(new PointD(212, 160), KeyModifiers.None);
            SelectorSessionTests.Pump(() => run.IsCompleted);

            var handed = Assert.Single(recorder.Handed);
            Assert.Equal(CaptureTool.Recording, handed.Tool);
            Assert.Equal(CaptureTargetKind.Area, handed.Kind);
            Assert.Null(handed.FrozenImage);
            Assert.Equal(0, handed.Bounds.Width % 2);
            Assert.Equal(0, handed.Bounds.Height % 2);
            Assert.True(handed.Bounds.Width >= 200 && handed.Bounds.Height >= 138);
        }
        finally
        {
            runtime.SetAvailable(FeatureIds.ScreenRecorder, wasAvailable);
        }
    }

    [AvaloniaFact]
    public void Without_the_recording_module_the_link_reports_idle_and_hands_nothing_over()
    {
        var link = new RecorderLink(new ServiceCollection().BuildServiceProvider());
        Assert.False(link.IsBusy);
        var handed = link.TryRecordSelectionAsync(new CaptureSelection
        {
            Tool = CaptureTool.Recording,
            Kind = CaptureTargetKind.Area,
            Bounds = new PixelRect(0, 0, 100, 100),
            ScreenId = "none",
        });
        Assert.True(handed.IsCompleted);
        Assert.False(handed.Result);
    }
}
