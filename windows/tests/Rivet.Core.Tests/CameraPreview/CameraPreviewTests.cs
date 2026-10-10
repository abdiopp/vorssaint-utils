// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Modules.CameraPreview;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests.CameraPreview;

public class CameraPreviewLayoutTests
{
    private static ScreenInfo Screen(double scale = 1.0) => new()
    {
        Id = "1",
        FriendlyName = "test",
        Bounds = new PixelRect(0, 0, (int)(1920 * scale), (int)(1080 * scale)),
        WorkArea = new PixelRect(0, 0, (int)(1920 * scale), (int)(1032 * scale)),
        Scale = scale,
        IsPrimary = true,
    };

    [Fact]
    public void The_mirror_sits_48_dip_below_the_top_centred()
    {
        var position = CameraPreviewLayout.Position(Screen());
        Assert.Equal(new PixelPoint(800, 48), position);
        var scaled = CameraPreviewLayout.Position(Screen(1.5));
        Assert.Equal(new PixelPoint((2880 - 480) / 2, 72), scaled);
    }

    [Fact]
    public void A_short_work_area_keeps_the_bottom_16_dip_inside()
    {
        var tiny = new ScreenInfo
        {
            Id = "2", FriendlyName = "tiny", Bounds = new PixelRect(0, 0, 400, 280), WorkArea = new PixelRect(0, 0, 400, 280), Scale = 1, IsPrimary = false,
        };
        var position = CameraPreviewLayout.Position(tiny);
        Assert.Equal(280 - 240 - 16, position.Y);
        Assert.Equal(40, position.X);
    }

    [Fact]
    public void Aspect_fill_crops_the_long_side_evenly()
    {
        var (x, y, w, h) = CameraPreviewLayout.AspectFillSource(1280, 720, 320, 240);
        Assert.Equal(960, w, 3);
        Assert.Equal(720, h, 3);
        Assert.Equal(160, x, 3);
        Assert.Equal(0, y, 3);
        var (_, y2, w2, h2) = CameraPreviewLayout.AspectFillSource(480, 640, 320, 240);
        Assert.Equal(480, w2, 3);
        Assert.Equal(360, h2, 3);
        Assert.Equal(140, y2, 3);
    }

    [Fact]
    public void The_shortcut_is_off_by_default()
    {
        var store = SettingsStore.InMemory();
        Assert.False(store.Get(CameraPreviewSettings.ShortcutEnabled));
        Assert.True(CameraPreviewSettings.DeviceId.IsMachineState);
    }
}

public class CameraPreviewControllerTests
{
    private sealed class FakeSession(string id) : ICameraSession
    {
        public string DeviceId { get; } = id;

        public bool Disposed { get; private set; }

        public event EventHandler<PixelBuffer>? FrameArrived;

        public event EventHandler<CameraFault>? Faulted;

        public void Frame() => FrameArrived?.Invoke(this, new PixelBuffer(4, 4));

        public void Fault(CameraFault fault) => Faulted?.Invoke(this, fault);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeCameras : ICameraService
    {
        public List<CameraDevice> Devices { get; } = [new("a", "Front"), new("b", "USB")];

        public CameraPreviewState? FailWith { get; set; }

        public List<FakeSession> Sessions { get; } = [];

        public Action? DevicesChanged { get; private set; }

        public string PrivacySettingsUri => "ms-settings:privacy-webcam";

        public CameraAccess Access => CameraAccess.Allowed;

        public Task<IReadOnlyList<CameraDevice>> GetCamerasAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CameraDevice>>(Devices.ToList());

        public Task<CameraStartResult> StartAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            if (FailWith is { } state)
            {
                return Task.FromResult(CameraStartResult.Failed(state));
            }

            var session = new FakeSession(deviceId);
            Sessions.Add(session);
            return Task.FromResult(CameraStartResult.Started(session));
        }

        public IDisposable WatchDevices(Action changed)
        {
            DevicesChanged = changed;
            return new Unwatch(() => DevicesChanged = null);
        }

        private sealed class Unwatch(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }

    [Fact]
    public void Starts_with_the_remembered_camera_and_stops_completely()
    {
        var cameras = new FakeCameras();
        var settings = SettingsStore.InMemory();
        settings.Set(CameraPreviewSettings.DeviceId, "b");
        var controller = new CameraPreviewController(cameras, settings);
        controller.Start();
        Assert.Equal(CameraPreviewState.Running, controller.State);
        Assert.Equal("b", controller.CurrentDeviceId);
        controller.Stop();
        Assert.True(cameras.Sessions[0].Disposed);
        Assert.Equal(CameraPreviewState.Idle, controller.State);
        Assert.Null(cameras.DevicesChanged);
    }

    [Fact]
    public void An_explicit_pick_is_remembered_but_a_fallback_is_not()
    {
        var cameras = new FakeCameras();
        var settings = SettingsStore.InMemory();
        var controller = new CameraPreviewController(cameras, settings);
        controller.Start();
        Assert.Equal("a", controller.CurrentDeviceId);
        controller.SelectCamera("b");
        Assert.Equal("b", settings.Get(CameraPreviewSettings.DeviceId));
        Assert.True(cameras.Sessions[0].Disposed);

        cameras.Devices.RemoveAll(d => d.Id == "b");
        cameras.DevicesChanged!();
        Assert.Equal("a", controller.CurrentDeviceId);
        Assert.Equal(CameraPreviewState.Running, controller.State);
        Assert.Equal("b", settings.Get(CameraPreviewSettings.DeviceId));
    }

    [Fact]
    public void Losing_the_last_camera_shows_no_camera_and_a_new_one_starts_capture()
    {
        var cameras = new FakeCameras();
        var controller = new CameraPreviewController(cameras, SettingsStore.InMemory());
        controller.Start();
        cameras.Devices.Clear();
        cameras.DevicesChanged!();
        Assert.Equal(CameraPreviewState.NoCamera, controller.State);
        cameras.Devices.Add(new CameraDevice("c", "New"));
        cameras.DevicesChanged!();
        Assert.Equal(CameraPreviewState.Running, controller.State);
        Assert.Equal("c", controller.CurrentDeviceId);
    }

    [Fact]
    public void Denied_and_unavailable_states_come_from_the_platform()
    {
        var cameras = new FakeCameras { FailWith = CameraPreviewState.Denied };
        var controller = new CameraPreviewController(cameras, SettingsStore.InMemory());
        controller.Start();
        Assert.Equal(CameraPreviewState.Denied, controller.State);
        controller.Stop();

        cameras.FailWith = null;
        controller.Start();
        cameras.Sessions[^1].Fault(CameraFault.Unavailable);
        Assert.Equal(CameraPreviewState.Unavailable, controller.State);
        controller.Retry();
        Assert.Equal(CameraPreviewState.Running, controller.State);
    }

    [Fact]
    public void Frames_are_coalesced_and_late_frames_after_stop_are_dropped()
    {
        var cameras = new FakeCameras();
        var controller = new CameraPreviewController(cameras, SettingsStore.InMemory());
        var ready = 0;
        controller.FrameReady += (_, _) => ready++;
        controller.Start();
        var session = cameras.Sessions[0];
        session.Frame();
        Assert.Equal(1, ready);
        Assert.NotNull(controller.TakeFrame());
        Assert.Null(controller.TakeFrame());
        controller.Stop();
        session.Frame();
        Assert.Equal(1, ready);
        Assert.Null(controller.TakeFrame());
    }

    [Fact]
    public void No_cameras_at_all_is_its_own_state()
    {
        var cameras = new FakeCameras();
        cameras.Devices.Clear();
        var controller = new CameraPreviewController(cameras, SettingsStore.InMemory());
        controller.Start();
        Assert.Equal(CameraPreviewState.NoCamera, controller.State);
    }
}
