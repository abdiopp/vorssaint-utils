// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;
using SkiaSharp;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Rivet.Platform.Windows.Recording;

/// <summary>
/// One recording's picture. Windows Graphics Capture delivers a frame each
/// time the monitor or window changes (cursor capture off: the editor redraws
/// the pointer from the pointer track). Each frame is cropped to the region on
/// the GPU (<c>CopySubresourceRegion</c>) into the "latest" texture; window
/// recordings that were resized are scaled to fit the fixed output size (on
/// the CPU, which is rare). An encode thread ticks at the frame rate and
/// writes the latest picture only when it changed (variable frame rate, with
/// a 1 s heartbeat so long still stretches keep regular key frames), timed by
/// its capture time through the pause clock. The first frame is forced to
/// t = 0 and the last one is appended again at the stop time.
/// </summary>
internal sealed class WgcCaptureSession : IVideoCaptureSession
{
    private const double HeartbeatSeconds = 1.0;
    private const double MinimumStep = 0.0001;
    private const DirectXPixelFormat PoolFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly VideoCaptureRequest _request;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly bool _isWindow;
    private readonly PixelRect _crop;
    private readonly int _itemWidth;
    private readonly int _itemHeight;
    private readonly bool _scaleOnCpu;
    private readonly IHostClock _host;
    private readonly object _frameGate = new();
    private readonly object _lifeGate = new();
    private readonly H264Writer _writer;
    private readonly TexturePool? _gpuTextures;
    private readonly ID3D11Texture2D _latest;
    private readonly ID3D11Texture2D? _readback;
    private ID3D11Texture2D? _scaleStaging;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private int _contentWidth;
    private int _contentHeight;
    private bool _haveFrame;
    private bool _dirty;
    private double _latestTime;
    private Thread? _encodeThread;
    private volatile bool _stopEncoding;
    private volatile bool _stopping;
    private PauseClock? _clock;
    private int _framesWritten;
    private double _lastTime;
    private double _lastWriteHost;
    private volatile bool _failed;
    private string? _failure;
    private int _endedRaised;
    private bool _finished;
    private bool _disposed;
    private bool _mediaFoundationStarted;

    public WgcCaptureSession(VideoCaptureRequest request, ID3D11Device device, ID3D11DeviceContext context, bool videoSupport, GraphicsCaptureItem item, bool borderless)
    {
        _request = request;
        _device = device;
        _context = context;
        _item = item;
        _host = request.Clock;
        _isWindow = request.Target.Kind == RecordingTargetKind.Window;
        _itemWidth = item.Size.Width;
        _itemHeight = item.Size.Height;
        if (_itemWidth <= 0 || _itemHeight <= 0)
        {
            throw new InvalidOperationException("The capture item has no size (minimized window?).");
        }

        int inputWidth, inputHeight;
        if (_isWindow)
        {
            (inputWidth, inputHeight) = VideoSizeLimits.Fit(RegionSnapping.EvenSide(_itemWidth), RegionSnapping.EvenSide(_itemHeight));
            Width = inputWidth;
            Height = inputHeight;
        }
        else
        {
            var monitor = request.Target.Monitor.Bounds;
            var region = request.Target.Region;
            var x = Math.Clamp(region.X - monitor.X, 0, _itemWidth - 2);
            var y = Math.Clamp(region.Y - monitor.Y, 0, _itemHeight - 2);
            var w = RegionSnapping.EvenSide(Math.Min(region.Width, _itemWidth - x));
            var h = RegionSnapping.EvenSide(Math.Min(region.Height, _itemHeight - y));
            _crop = new PixelRect(x, y, Math.Min(w, _itemWidth - x), Math.Min(h, _itemHeight - y));
            (inputWidth, inputHeight) = (_crop.Width, _crop.Height);
            (Width, Height) = VideoSizeLimits.Fit(inputWidth, inputHeight);
        }

        _contentWidth = _itemWidth;
        _contentHeight = _itemHeight;

        MediaFoundationRuntime.Startup();
        _mediaFoundationStarted = true;
        try
        {
            (_writer, _scaleOnCpu) = CreateWriter(request.OutputPath, inputWidth, inputHeight, Width, Height, request.FrameRate, videoSupport ? device : null);
            if (_scaleOnCpu)
            {
                (inputWidth, inputHeight) = (Width, Height);
            }

            _latest = _device.CreateTexture2D(Describe(inputWidth, inputHeight, ResourceUsage.Default));
            if (_writer.UsesGpuInput)
            {
                _gpuTextures = new TexturePool(_device, Describe(inputWidth, inputHeight, ResourceUsage.Default));
            }
            else
            {
                _readback = _device.CreateTexture2D(Describe(inputWidth, inputHeight, ResourceUsage.Staging));
            }

            _winrtDevice = CaptureInterop.CreateWinRtDevice(device);
            _poolSize = item.Size;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, PoolFormat, 2, _poolSize);
            _session = _framePool.CreateCaptureSession(item);
            ConfigureSession(_session, borderless);
            _item.Closed += OnItemClosed;
            _framePool.FrameArrived += OnFrameArrived;
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    public int Width { get; }

    public int Height { get; }

    public (int Width, int Height) ContentSize => (Volatile.Read(ref _contentWidth), Volatile.Read(ref _contentHeight));

    public event EventHandler<CaptureEndedEventArgs>? Ended;

    public void Start(PauseClock clock)
    {
        _clock = clock;
        _session!.StartCapture();
        _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "RecorderEncode", Priority = ThreadPriority.AboveNormal };
        _encodeThread.Start();
    }

    public Task<VideoCaptureResult> FinishAsync(double endTime)
    {
        lock (_lifeGate)
        {
            if (_finished)
            {
                return Task.FromResult(new VideoCaptureResult { Written = false, Error = "Already finished." });
            }

            _finished = true;
        }

        StopCapture();
        StopEncoder();
        return Task.Run(() =>
        {
            try
            {
                if (_haveFrame && _clock is not null)
                {
                    try
                    {
                        if (_framesWritten == 0)
                        {
                            Commit(PrepareLocked(0, waitForTexture: true));
                        }

                        if (_framesWritten > 0 && endTime > _lastTime + MinimumStep)
                        {
                            Commit(PrepareLocked(endTime, waitForTexture: true));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("recorder", "Appending the last frame failed.", ex);
                    }
                }

                if (_framesWritten == 0)
                {
                    return new VideoCaptureResult { Written = false, Error = _failure ?? "No frame was captured." };
                }

                _writer.Finish();
                if (_failed)
                {
                    Log.Warn("recorder", $"The master was finalized after an error: {_failure}");
                }

                return new VideoCaptureResult { Written = true, FramesWritten = _framesWritten, Width = Width, Height = Height };
            }
            catch (Exception ex)
            {
                Log.Error("recorder", "Finalizing the master failed.", ex);
                return new VideoCaptureResult { Written = false, FramesWritten = _framesWritten, Error = ex.Message };
            }
        });
    }

    public Task CancelAsync()
    {
        lock (_lifeGate)
        {
            _finished = true;
        }

        StopCapture();
        StopEncoder();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        StopCapture();
        StopEncoder();
        lock (_frameGate)
        {
            ReleaseResources();
        }

        return ValueTask.CompletedTask;
    }

    private static (H264Writer Writer, bool ScaleOnCpu) CreateWriter(string path, int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps, ID3D11Device? device)
    {
        var errors = new List<string>();
        if (device is not null)
        {
            try
            {
                return (H264Writer.Create(path, inputWidth, inputHeight, outputWidth, outputHeight, fps, device), false);
            }
            catch (Exception ex)
            {
                errors.Add($"GPU: {ex.Message}");
            }
        }

        try
        {
            return (H264Writer.Create(path, inputWidth, inputHeight, outputWidth, outputHeight, fps, null), false);
        }
        catch (Exception ex) when (inputWidth != outputWidth || inputHeight != outputHeight)
        {
            errors.Add($"CPU scaled: {ex.Message}");
        }
        catch (Exception ex)
        {
            errors.Add($"CPU: {ex.Message}");
            throw new InvalidOperationException("The H.264 encoder refused every configuration: " + string.Join("; ", errors), ex);
        }

        // Last resort: the encoder at its own size, scaled by us.
        try
        {
            var writer = H264Writer.Create(path, outputWidth, outputHeight, outputWidth, outputHeight, fps, null);
            Log.Info("recorder", "Scaling frames on the CPU: " + string.Join("; ", errors));
            return (writer, true);
        }
        catch (Exception ex)
        {
            errors.Add($"CPU: {ex.Message}");
            throw new InvalidOperationException("The H.264 encoder refused every configuration: " + string.Join("; ", errors), ex);
        }
    }

    private static void ConfigureSession(GraphicsCaptureSession session, bool borderlessGranted)
    {
        const string sessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
        try
        {
            if (ApiInformation.IsPropertyPresent(sessionType, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
            {
                session.IsCursorCaptureEnabled = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Could not hide the cursor from the capture.", ex);
        }

        try
        {
            if (ApiInformation.IsPropertyPresent(sessionType, nameof(GraphicsCaptureSession.IsBorderRequired)))
            {
                // Windows 11: no yellow frame around what is recorded (unpackaged apps may drop it).
                session.IsBorderRequired = false;
            }
        }
        catch (Exception ex)
        {
            Log.Info("recorder", $"The capture border stays (access granted: {borderlessGranted}): {ex.Message}");
        }
    }

    private static Texture2DDescription Describe(int width, int height, ResourceUsage usage) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = usage == ResourceUsage.Staging ? BindFlags.None : BindFlags.ShaderResource | BindFlags.RenderTarget,
        CPUAccessFlags = usage == ResourceUsage.Staging ? CpuAccessFlags.Read : CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    };

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_stopping)
        {
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || _stopping)
            {
                return;
            }

            var content = frame.ContentSize;
            Volatile.Write(ref _contentWidth, content.Width);
            Volatile.Write(ref _contentHeight, content.Height);
            if (!_isWindow && (content.Width != _itemWidth || content.Height != _itemHeight))
            {
                RaiseEnded(CaptureEndReason.DisplayChanged, $"monitor is now {content.Width}×{content.Height}");
                return;
            }

            if (_isWindow && content.Width > 0 && content.Height > 0 && (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
            {
                // The window was resized: later frames come at the new size.
                _poolSize = content;
                sender.Recreate(_winrtDevice, PoolFormat, 2, content);
            }

            using var surface = frame.Surface;
            using var texture = CaptureInterop.GetTexture(surface);
            var description = texture.Description;
            lock (_frameGate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_isWindow)
                {
                    CopyWindow(texture, (int)description.Width, (int)description.Height, content.Width, content.Height);
                }
                else
                {
                    CopyRegion(texture, (int)description.Width, (int)description.Height);
                }

                _latestTime = QpcClock.FromHundredNanoseconds(frame.SystemRelativeTime.Ticks);
                _dirty = true;
                _haveFrame = true;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the WinRT callback; while stopping, a
            // late frame racing the pool's disposal is expected and ignored.
            if (!_stopping)
            {
                Fail(CaptureEndReason.CaptureLost, ex);
            }
        }
    }

    private void CopyRegion(ID3D11Texture2D texture, int textureWidth, int textureHeight)
    {
        if (_scaleOnCpu)
        {
            var fit = WindowFit.Compute(_crop.Width, _crop.Height, Width, Height);
            ScaleIntoLatest(texture, _crop.X, _crop.Y, Math.Min(_crop.Width, textureWidth - _crop.X), Math.Min(_crop.Height, textureHeight - _crop.Y), fit);
            return;
        }

        var right = Math.Min(_crop.Right, textureWidth);
        var bottom = Math.Min(_crop.Bottom, textureHeight);
        if (right <= _crop.X || bottom <= _crop.Y)
        {
            return;
        }

        _context.CopySubresourceRegion(_latest, 0, 0, 0, 0, texture, 0, new Box(_crop.X, _crop.Y, 0, right, bottom, 1));
    }

    private void CopyWindow(ID3D11Texture2D texture, int textureWidth, int textureHeight, int contentWidth, int contentHeight)
    {
        var width = Math.Min(contentWidth, textureWidth);
        var height = Math.Min(contentHeight, textureHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var fit = WindowFit.Compute(contentWidth, contentHeight, Width, Height);
        if (fit.IsCrop && width >= Width && height >= Height)
        {
            _context.CopySubresourceRegion(_latest, 0, 0, 0, 0, texture, 0, new Box(0, 0, 0, Width, Height, 1));
            return;
        }

        if (fit.IsCrop)
        {
            // The new frame size is not live yet (pool recreated): scale what is there.
            fit = WindowFit.Compute(width, height, Width, Height);
        }

        ScaleIntoLatest(texture, 0, 0, width, height, fit);
    }

    /// <summary>Scale-to-fit through a staging copy and Skia (window resized, or a size the encoder would not scale).</summary>
    private void ScaleIntoLatest(ID3D11Texture2D source, int x, int y, int width, int height, FitTransform fit)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_scaleStaging is null || _scaleStaging.Description.Width != width || _scaleStaging.Description.Height != height)
        {
            _scaleStaging?.Dispose();
            _scaleStaging = _device.CreateTexture2D(Describe(width, height, ResourceUsage.Staging));
        }

        _context.CopySubresourceRegion(_scaleStaging, 0, 0, 0, 0, source, 0, new Box(x, y, 0, x + width, y + height, 1));
        var mapped = _context.Map(_scaleStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            using var sourceBitmap = new SKBitmap();
            sourceBitmap.InstallPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), mapped.DataPointer, (int)mapped.RowPitch);
            using var target = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(target))
            {
                canvas.Clear(SKColors.Black);
                using var image = SKImage.FromBitmap(sourceBitmap);
                var destination = SKRect.Create((float)fit.OffsetX, (float)fit.OffsetY, (float)(width * fit.Scale), (float)(height * fit.Scale));
                canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            _context.UpdateSubresource(_latest, 0, null, target.GetPixels(), (uint)target.RowBytes, 0);
        }
        finally
        {
            _context.Unmap(_scaleStaging, 0);
        }
    }

    private void EncodeLoop()
    {
        try
        {
            using var timer = new HighResolutionTimer();
            var interval = 1.0 / Math.Max(1, _request.FrameRate);
            var next = _host.Now;
            while (!_stopEncoding)
            {
                next += interval;
                var wait = next - _host.Now;
                if (wait < -0.25)
                {
                    next = _host.Now;
                }
                else if (wait > 0)
                {
                    timer.Wait(TimeSpan.FromSeconds(wait));
                }

                if (_stopEncoding || _failed)
                {
                    break;
                }

                EncodeTick();
            }
        }
        catch (Exception ex)
        {
            Fail(CaptureEndReason.EncoderFailed, ex);
        }
    }

    private void EncodeTick()
    {
        var clock = _clock;
        if (clock is null || clock.IsPaused)
        {
            return;
        }

        var now = _host.Now;
        PendingFrame? pending;
        lock (_frameGate)
        {
            if (!_haveFrame || _disposed)
            {
                return;
            }

            var heartbeat = _framesWritten > 0 && now - _lastWriteHost >= HeartbeatSeconds;
            if (_framesWritten > 0 && !_dirty && !heartbeat)
            {
                return;
            }

            var time = _framesWritten == 0
                ? 0
                : _dirty ? clock.EventTime(_latestTime) ?? clock.Elapsed(now) : clock.Elapsed(now);
            pending = Prepare(time, waitForTexture: false);
        }

        // Outside the frame lock: the sink writer may block when the encoder is behind.
        Commit(pending);
    }

    private PendingFrame? PrepareLocked(double time, bool waitForTexture)
    {
        lock (_frameGate)
        {
            return _disposed ? null : Prepare(time, waitForTexture);
        }
    }

    /// <summary>Snapshots the latest picture for writing at <paramref name="time"/> (under the frame lock).</summary>
    private PendingFrame? Prepare(double time, bool waitForTexture)
    {
        if (_framesWritten > 0 && time <= _lastTime)
        {
            time = _lastTime + MinimumStep;
        }

        ID3D11Texture2D? texture = null;
        if (_writer.UsesGpuInput)
        {
            texture = _gpuTextures!.Acquire(waitForTexture ? TimeSpan.FromSeconds(1) : TimeSpan.Zero);
            if (texture is null)
            {
                return null; // the encoder still holds every texture; the picture stays pending
            }

            _context.CopyResource(texture, _latest);
        }
        else
        {
            _context.CopyResource(_readback!, _latest);
        }

        _dirty = false;
        return new PendingFrame(texture, time);
    }

    private void Commit(PendingFrame? pending)
    {
        if (pending is not { } frame)
        {
            return;
        }

        if (frame.Texture is { } texture)
        {
            _writer.WriteTexture(texture, frame.Time);
        }
        else
        {
            var mapped = _context.Map(_readback!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                _writer.WriteBytes(mapped.DataPointer, (int)mapped.RowPitch, frame.Time);
            }
            finally
            {
                _context.Unmap(_readback!, 0);
            }
        }

        _framesWritten++;
        _lastTime = frame.Time;
        _lastWriteHost = _host.Now;
    }

    private readonly record struct PendingFrame(ID3D11Texture2D? Texture, double Time);

    private void OnItemClosed(GraphicsCaptureItem sender, object args) =>
        RaiseEnded(_isWindow ? CaptureEndReason.WindowClosed : CaptureEndReason.DisplayChanged, "capture item closed");

    private void Fail(CaptureEndReason reason, Exception ex)
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        _failure = ex.Message;
        Log.Error("recorder", $"Capture failed ({reason}).", ex);
        RaiseEnded(reason, ex.Message);
    }

    private void RaiseEnded(CaptureEndReason reason, string detail)
    {
        if (_stopping || Interlocked.Exchange(ref _endedRaised, 1) == 1)
        {
            return;
        }

        Ended?.Invoke(this, new CaptureEndedEventArgs(reason, detail));
    }

    private void StopCapture()
    {
        _stopping = true;
        var pool = Interlocked.Exchange(ref _framePool, null);
        var session = Interlocked.Exchange(ref _session, null);
        try
        {
            if (pool is not null)
            {
                pool.FrameArrived -= OnFrameArrived;
            }

            _item.Closed -= OnItemClosed;
            session?.Dispose();
            pool?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Closing the capture session failed.", ex);
        }
    }

    private void StopEncoder()
    {
        _stopEncoding = true;
        if (_encodeThread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void ReleaseResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release(_gpuTextures);
        Release(_latest);
        Release(_readback);
        Release(_scaleStaging);
        Release(_writer);
        Release(_winrtDevice);
        Release(_context);
        Release(_device);
        if (_mediaFoundationStarted)
        {
            _mediaFoundationStarted = false;
            MediaFoundationRuntime.Shutdown();
        }
    }

    private static void Release(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Releasing a capture resource failed.", ex);
        }
    }

    /// <summary>
    /// Textures handed to the encoder. Media Foundation keeps a reference to a
    /// texture until it has encoded it, so a texture is reused only when this
    /// pool holds the last reference; otherwise a new one is made (up to a cap).
    /// </summary>
    private sealed class TexturePool(ID3D11Device device, Texture2DDescription description) : IDisposable
    {
        private const int MaxTextures = 12;
        private readonly List<ID3D11Texture2D> _textures = [];

        public ID3D11Texture2D? Acquire(TimeSpan wait)
        {
            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                foreach (var texture in _textures)
                {
                    if (ReferenceCount(texture.NativePointer) == 1)
                    {
                        return texture;
                    }
                }

                if (_textures.Count < MaxTextures)
                {
                    var created = device.CreateTexture2D(description);
                    _textures.Add(created);
                    return created;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            foreach (var texture in _textures)
            {
                texture.Dispose();
            }

            _textures.Clear();
        }

        private static int ReferenceCount(nint unknown)
        {
            Marshal.AddRef(unknown);
            return Marshal.Release(unknown);
        }
    }
}
