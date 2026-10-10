// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string) is fine for snapshots.
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.CameraPreview;
using Rivet.App.Settings;
using Rivet.Core.Features;
using Rivet.Core.Modules.CameraPreview;
using Rivet.Core.Platform;
using Rivet.Platform.Fake.CameraPreview;
using Rivet.Platform.Fake.Shell;
using Xunit;

namespace Rivet.App.Tests.CameraPreview;

public class CameraPreviewRenderTests
{
    private static CameraPreviewService Service()
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.CameraPreview, true);
        var service = host.Services.GetRequiredService<CameraPreviewService>();

        // Each test starts from a hidden mirror and a fresh window, whatever an earlier test left
        // behind (the headless renderer can keep serving stale frames for a window reused across tests).
        service.Hide(CameraHideReason.Escape);
        service.Window?.Close();
        Assert.Null(service.Window);
        return service;
    }

    private static void Save(Window window, string name)
    {
        window.Opacity = 1;
        Dispatcher.UIThread.RunJobs();

        // The first capture can return the frame rendered before the latest changes (a live
        // video keeps the scene busy); the second one reflects them.
        window.CaptureRenderedFrame();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, name + ".png"));
    }

    private static int MaxPixel(Window window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var locked = frame.Lock();
        var max = 0;
        var bytes = new byte[locked.RowBytes * locked.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address, bytes, 0, bytes.Length);
        foreach (var b in bytes)
        {
            max = Math.Max(max, b);
        }

        return max;
    }

    private static void PumpUntil(Func<bool> done, double seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_running_mirror_shows_the_mirrored_frame_and_the_camera_picker()
    {
        var service = Service();
        var theme = TestApp.Host.Services.GetRequiredService<FakeTheme>();
        theme.ReduceMotion = true; // no fades: the snapshot shows the final look
        service.Show();
        theme.ReduceMotion = false;
        var window = service.Window!;
        Assert.True(window.ReduceMotion);
        PumpUntil(() => service.Controller.State == CameraPreviewState.Running && window.HasFrame);

        Assert.Equal(CameraPreviewState.Running, service.Controller.State);
        Assert.True(window.HasFrame);
        Assert.Equal(2, service.Controller.Cameras.Count);
        window.SetPointerInside(true);
        Assert.True(window.PickerVisible);
        Save(window, "camera-preview-running");
        window.SetPointerInside(false);
        Assert.False(window.PickerVisible);

        service.Hide(CameraHideReason.Escape);
        Assert.False(service.IsVisible);
        Assert.Equal(CameraPreviewState.Idle, service.Controller.State);
        Assert.False(service.Controller.IsShown);
    }

    [AvaloniaFact]
    public void A_still_frame_renders_mirrored_and_aspect_filled()
    {
        var service = Service();
        var window = new CameraPreviewWindow(service);
        window.Show();
        window.ShowState(CameraPreviewState.Running);
        window.ShowFrame(FakeCameraService.RenderTestCard(640, 480, 83.4, alternate: false));
        Save(window, "camera-preview-frame");
        window.ShowFrame(FakeCameraService.RenderTestCard(1280, 720, 83.4, alternate: true));
        Save(window, "camera-preview-frame-wide");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(CameraPreviewState.Starting, "starting")]
    [InlineData(CameraPreviewState.Denied, "denied")]
    [InlineData(CameraPreviewState.Unavailable, "unavailable")]
    [InlineData(CameraPreviewState.NoCamera, "nocamera")]
    public void Each_state_renders(CameraPreviewState state, string name)
    {
        var window = new CameraPreviewWindow(Service());
        window.Show();
        window.ShowState(state);
        Save(window, "camera-preview-" + name);
        window.Close();
    }

    [AvaloniaFact]
    public void Denied_access_shows_the_denied_state_and_the_tile_caption()
    {
        var service = Service();
        var cameras = TestApp.Host.Services.GetRequiredService<FakeCameraService>();
        cameras.Access = CameraAccess.Denied;
        try
        {
            Assert.True(service.AccessDenied);
            service.Show();
            PumpUntil(() => service.Controller.State == CameraPreviewState.Denied);
            Assert.Equal(CameraPreviewState.Denied, service.Window!.ShownState);
            service.Hide(CameraHideReason.Escape);
        }
        finally
        {
            cameras.Access = CameraAccess.Allowed;
        }

        // A later successful start clears the remembered denial.
        service.Show();
        PumpUntil(() => service.Controller.State == CameraPreviewState.Running);
        Assert.False(service.AccessDenied);
        service.Hide(CameraHideReason.Escape);
    }

    [AvaloniaFact]
    public void A_click_outside_the_frame_hides_it_and_a_click_inside_does_not()
    {
        var service = Service();
        var hooks = TestApp.Host.Services.GetRequiredService<FakeInputHooks>();
        var baseline = hooks.MouseSubscriberCount;
        service.Show();
        var window = service.Window!;
        PumpUntil(() => service.Controller.State == CameraPreviewState.Running);
        Assert.Equal(baseline + 1, hooks.MouseSubscriberCount);
        var frame = window.FrameRect();

        // Render once while shown: the headless renderer never draws a window again that was
        // shown and hidden before its first frame (later tests reuse this window). The fade-in
        // may still be at opacity 0 on a busy run, so settle it first.
        window.Opacity = 1;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Assert.True(MaxPixel(window) > 0);
        hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftDown, Position = new PixelPoint(frame.X + (frame.Width / 2), frame.Y + (frame.Height / 2)) });
        Dispatcher.UIThread.RunJobs();
        Assert.True(service.IsVisible);

        hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftDown, Position = new PixelPoint(frame.Right + 40, frame.Bottom + 40) });
        Dispatcher.UIThread.RunJobs();
        Assert.False(service.IsVisible);
        Assert.Equal(CameraPreviewState.Idle, service.Controller.State);
        Assert.Equal(baseline, hooks.MouseSubscriberCount);
    }

    [AvaloniaFact]
    public void Picking_a_camera_remembers_it_but_a_fallback_does_not()
    {
        var service = Service();
        var settings = TestApp.Host.Settings;
        var cameras = TestApp.Host.Services.GetRequiredService<FakeCameraService>();
        settings.Set(CameraPreviewSettings.DeviceId, string.Empty);
        service.Show();
        PumpUntil(() => service.Controller.State == CameraPreviewState.Running);
        Assert.Equal("fake:front", service.Controller.CurrentDeviceId);
        service.Controller.SelectCamera("fake:usb");
        PumpUntil(() => service.Controller.State == CameraPreviewState.Running && service.Controller.CurrentDeviceId == "fake:usb");
        Assert.Equal("fake:usb", settings.Get(CameraPreviewSettings.DeviceId));

        var usb = cameras.Devices[1];
        cameras.Devices.RemoveAt(1);
        try
        {
            cameras.RaiseDevicesChanged();
            PumpUntil(() => service.Controller.CurrentDeviceId == "fake:front" && service.Controller.State == CameraPreviewState.Running);
            Assert.Equal("fake:front", service.Controller.CurrentDeviceId);
            Assert.Equal("fake:usb", settings.Get(CameraPreviewSettings.DeviceId));
        }
        finally
        {
            cameras.Devices.Add(usb);
            service.Hide(CameraHideReason.Escape);
            settings.Set(CameraPreviewSettings.DeviceId, string.Empty);
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_settings_page_renders(string theme)
    {
        Service();
        var vm = new SettingsViewModel(TestApp.Host.Services);
        vm.Navigate(CameraPreviewModule.PageId);
        Assert.Equal(CameraPreviewModule.PageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 720, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        Save(window, "settings-cameraPreview-" + theme);
        window.Close();
    }
}
