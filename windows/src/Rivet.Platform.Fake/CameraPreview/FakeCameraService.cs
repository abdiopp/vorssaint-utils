// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Modules.CameraPreview;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Platform.Fake.CameraPreview;

/// <summary>
/// Two generated "cameras" for the development build: an animated test card
/// (colour bars, a moving disc and a clock) at about 15 frames per second.
/// Nothing touches real hardware.
/// </summary>
public sealed class FakeCameraService : ICameraService
{
    private readonly List<Action> _watchers = [];

    public List<CameraDevice> Devices { get; } =
    [
        new("fake:front", "Integrated Camera (simulated)"),
        new("fake:usb", "USB Webcam (simulated)"),
    ];

    /// <summary>The simulated privacy switch (tests and the dev build's "denied" demo).</summary>
    public CameraAccess Access { get; set; } = CameraAccess.Allowed;

    /// <summary>Set to make the next start fail with this state (tests).</summary>
    public CameraPreviewState? NextFailure { get; set; }

    public string PrivacySettingsUri => "ms-settings:privacy-webcam";

    public Task<IReadOnlyList<CameraDevice>> GetCamerasAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CameraDevice>>(Devices.ToList());

    public Task<CameraStartResult> StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (Access == CameraAccess.Denied)
        {
            return Task.FromResult(CameraStartResult.Failed(CameraPreviewState.Denied));
        }

        if (NextFailure is { } failure)
        {
            NextFailure = null;
            return Task.FromResult(CameraStartResult.Failed(failure));
        }

        if (Devices.All(d => d.Id != deviceId))
        {
            return Task.FromResult(CameraStartResult.Failed(CameraPreviewState.NoCamera));
        }

        return Task.FromResult(CameraStartResult.Started(new TestCardSession(deviceId, deviceId.EndsWith("usb", StringComparison.Ordinal))));
    }

    public IDisposable WatchDevices(Action changed)
    {
        _watchers.Add(changed);
        return new Token(() => _watchers.Remove(changed));
    }

    public void RaiseDevicesChanged()
    {
        foreach (var watcher in _watchers.ToArray())
        {
            watcher();
        }
    }

    /// <summary>Renders one test-card frame (also used by the UI snapshot tests).</summary>
    public static PixelBuffer RenderTestCard(int width, int height, double seconds, bool alternate)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(width, height));
        var canvas = surface.Canvas;
        var top = alternate ? new SKColor(40, 24, 64) : new SKColor(16, 40, 64);
        var bottom = alternate ? new SKColor(160, 70, 120) : new SKColor(40, 140, 170);
        using (var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height), [top, bottom], SKShaderTileMode.Clamp) })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        SKColor[] bars = [SKColors.White, SKColors.Yellow, SKColors.Cyan, SKColors.Lime, SKColors.Magenta, SKColors.Red, SKColors.Blue];
        var barWidth = width / (float)bars.Length;
        for (var i = 0; i < bars.Length; i++)
        {
            using var bar = new SKPaint { Color = bars[i].WithAlpha(150) };
            canvas.DrawRect(i * barWidth, height * 0.72f, barWidth, height * 0.12f, bar);
        }

        var x = (float)((Math.Sin(seconds * 1.3) * 0.35 + 0.5) * width);
        var y = (float)((Math.Cos(seconds * 0.9) * 0.18 + 0.38) * height);
        using (var disc = new SKPaint { Color = new SKColor(255, 220, 180), IsAntialias = true })
        {
            canvas.DrawCircle(x, y, height * 0.12f, disc);
        }

        // A marker on the left so mirroring is visible.
        using (var marker = new SKPaint { Color = SKColors.Orange, IsAntialias = true })
        {
            canvas.DrawRect(width * 0.04f, height * 0.06f, width * 0.08f, height * 0.08f, marker);
        }

        using var typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold);
        using var font = new SKFont(typeface, height * 0.07f);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f", System.Globalization.CultureInfo.InvariantCulture), width * 0.62f, height * 0.13f, font, text);
        return SkiaConvert.ToPixelBuffer(surface.Snapshot());
    }

    private sealed class TestCardSession : ICameraSession
    {
        private readonly Timer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly bool _alternate;
        private int _busy;
        private int _disposed;

        public TestCardSession(string deviceId, bool alternate)
        {
            DeviceId = deviceId;
            _alternate = alternate;
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(66));
        }

        public string DeviceId { get; }

        public event EventHandler<PixelBuffer>? FrameArrived;

        public event EventHandler<CameraFault>? Faulted
        {
            add { }
            remove { }
        }

        private void Tick()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _busy, 1) != 0)
            {
                return;
            }

            try
            {
                FrameArrived?.Invoke(this, RenderTestCard(640, 480, _clock.Elapsed.TotalSeconds, _alternate));
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _timer.Dispose();
            }
        }
    }

    private sealed class Token(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class CameraPreviewFakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeCameraService>();
        services.AddSingleton<ICameraService>(sp => sp.GetRequiredService<FakeCameraService>());
    }
}
