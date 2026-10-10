// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Rivet.Platform.Windows.Capture;

/// <summary>
/// One-frame captures with Windows.Graphics.Capture (monitor or window) on a
/// shared Direct3D 11 device. HDR monitors are captured in FP16 scRGB and
/// tone-mapped to sRGB with the monitor's SDR white level, so SDR content
/// looks as it does on screen and HDR highlights clip. On Windows 11 the
/// yellow capture border is turned off (borderless access); Windows 10 cannot
/// do that, so the caller prefers GDI there.
/// </summary>
internal sealed class WgcCapture : IDisposable
{
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private bool? _borderlessAllowed;

    /// <summary>WGC is present (Windows 10 1903+) and usable.</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException)
            {
                return false;
            }
        }
    }

    /// <summary>Windows 11 lets unpackaged apps turn the capture border off.</summary>
    public async Task<bool> CanCaptureBorderlessAsync()
    {
        if (_borderlessAllowed is { } known)
        {
            return known;
        }

        var allowed = false;
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
                && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                allowed = status == global::Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or TypeLoadException)
        {
            Log.Warn("capture", "Borderless capture access could not be requested.", ex);
        }

        _borderlessAllowed = allowed;
        return allowed;
    }

    public Task<PixelBuffer?> CaptureMonitorAsync(nint monitor, double scale, bool includeCursor, bool borderless, double? hdrWhiteScale, CancellationToken cancellationToken) =>
        CaptureAsync(CreateItem(0, monitor), scale, includeCursor, borderless, hdrWhiteScale, cancellationToken);

    public Task<PixelBuffer?> CaptureWindowAsync(nint window, double scale, bool borderless, CancellationToken cancellationToken) =>
        CaptureAsync(CreateItem(window, 0), scale, includeCursor: false, borderless, hdrWhiteScale: null, cancellationToken);

    private async Task<PixelBuffer?> CaptureAsync(GraphicsCaptureItem? item, double scale, bool includeCursor, bool borderless, double? hdrWhiteScale, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return null;
        }

        var device = EnsureDevice();
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        var hdr = hdrWhiteScale is not null;
        var format = hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, format, 1, size);
        using var session = pool.CreateCaptureSession(item);
        TrySet(() => session.IsCursorCaptureEnabled = includeCursor);
        if (borderless)
        {
            TrySet(() => session.IsBorderRequired = false);
        }

        var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            var frame = sender.TryGetNextFrame();
            if (frame is not null && !arrived.TrySetResult(frame))
            {
                frame.Dispose();
            }
        };

        session.StartCapture();
        Direct3D11CaptureFrame captured;
        try
        {
            captured = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("capture", "Windows.Graphics.Capture delivered no frame in time.");
            return null;
        }

        using (captured)
        {
            return Copy(captured, scale, hdrWhiteScale);
        }
    }

    private unsafe PixelBuffer? Copy(Direct3D11CaptureFrame frame, double scale, double? hdrWhiteScale)
    {
        var content = frame.ContentSize;
        var surfacePointer = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        if (surfacePointer == 0)
        {
            return null;
        }

        try
        {
            var accessIid = IidDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, in accessIid, out var access));
            try
            {
                var textureIid = typeof(ID3D11Texture2D).GUID;
                nint texturePointer;
                var vtable = *(void***)access;
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[3];
                Marshal.ThrowExceptionForHR(getInterface(access, &textureIid, &texturePointer));
                using var texture = new ID3D11Texture2D(texturePointer);
                return Read(texture, content.Width, content.Height, scale, hdrWhiteScale);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surfacePointer);
        }
    }

    private unsafe PixelBuffer? Read(ID3D11Texture2D texture, int contentWidth, int contentHeight, double scale, double? hdrWhiteScale)
    {
        lock (_gate)
        {
            var description = texture.Description;
            var width = Math.Min(contentWidth, (int)description.Width);
            var height = Math.Min(contentHeight, (int)description.Height);
            if (width <= 0 || height <= 0 || _device is null || _context is null)
            {
                return null;
            }

            var staging = description;
            staging.Usage = ResourceUsage.Staging;
            staging.BindFlags = BindFlags.None;
            staging.CPUAccessFlags = CpuAccessFlags.Read;
            staging.MiscFlags = ResourceOptionFlags.None;
            staging.MipLevels = 1;
            staging.ArraySize = 1;
            staging.SampleDescription = new SampleDescription(1, 0);
            using var copy = _device.CreateTexture2D(staging);
            _context.CopyResource(copy, texture);
            var mapped = _context.Map(copy, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var buffer = new PixelBuffer(width, height) { Scale = scale };
                var source = (byte*)mapped.DataPointer;
                fixed (byte* destination = buffer.Pixels)
                {
                    if (hdrWhiteScale is { } white)
                    {
                        var lut = HdrToneMap.Lut(white);
                        for (var y = 0; y < height; y++)
                        {
                            var row = (ushort*)(source + ((long)y * mapped.RowPitch));
                            var target = destination + ((long)y * buffer.Stride);
                            for (var x = 0; x < width; x++)
                            {
                                target[(x * 4) + 2] = lut[row[x * 4]];
                                target[(x * 4) + 1] = lut[row[(x * 4) + 1]];
                                target[x * 4] = lut[row[(x * 4) + 2]];
                                target[(x * 4) + 3] = 255;
                            }
                        }
                    }
                    else
                    {
                        var rowBytes = width * 4;
                        for (var y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(source + ((long)y * mapped.RowPitch), destination + ((long)y * buffer.Stride), rowBytes, rowBytes);
                        }
                    }
                }

                return buffer;
            }
            finally
            {
                _context.Unmap(copy, 0);
            }
        }
    }

    private IDirect3DDevice EnsureDevice()
    {
        lock (_gate)
        {
            if (_winrtDevice is not null)
            {
                return _winrtDevice;
            }

            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
            var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device, out var context);
            if (result.Failure)
            {
                result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context);
            }

            result.CheckError();
            _device = device;
            _context = context;

            // The capture frame pool works on its own threads: serialize access to the device.
            using (var multithread = device!.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                multithread?.SetMultithreadProtected(true);
            }

            using var dxgiDevice = device!.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
            try
            {
                _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }

            return _winrtDevice;
        }
    }

    /// <summary>GraphicsCaptureItem for a window or a monitor through IGraphicsCaptureItemInterop.</summary>
    private static unsafe GraphicsCaptureItem? CreateItem(nint window, nint monitor)
    {
        try
        {
            var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", IidGraphicsCaptureItemInterop);
            var interop = factory.ThisPtr;
            var vtable = *(void***)interop;
            var iid = IidGraphicsCaptureItem;
            nint item;
            int hr;
            if (window != 0)
            {
                var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[3];
                hr = createForWindow(interop, window, &iid, &item);
            }
            else
            {
                var createForMonitor = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[4];
                hr = createForMonitor(interop, monitor, &iid, &item);
            }

            GC.KeepAlive(factory);
            if (hr < 0 || item == 0)
            {
                Log.Warn("capture", $"GraphicsCaptureItem creation failed (0x{hr:X8}).");
                return null;
            }

            try
            {
                return ABI.Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or TypeLoadException)
        {
            Log.Warn("capture", "GraphicsCaptureItem could not be created.", ex);
            return null;
        }
    }

    private static void TrySet(Action set)
    {
        try
        {
            set();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException or ArgumentException)
        {
            // Property missing on this Windows version: keep the default.
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public void Dispose()
    {
        lock (_gate)
        {
            _winrtDevice = null;
            _context?.Dispose();
            _device?.Dispose();
            _context = null;
            _device = null;
        }
    }
}

/// <summary>scRGB (linear FP16, 1.0 = 80 nits) to 8-bit sRGB with the SDR white level as paper white.</summary>
internal static class HdrToneMap
{
    private static readonly Dictionary<int, byte[]> Cache = [];

    /// <summary>A 65 536-entry table from half-float bits to an sRGB byte.</summary>
    public static byte[] Lut(double whiteScale)
    {
        var key = (int)Math.Round(whiteScale * 1000);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var lut = new byte[65536];
            var white = Math.Max(0.25, whiteScale);
            for (var i = 0; i < 65536; i++)
            {
                var linear = (double)BitConverter.UInt16BitsToHalf((ushort)i);
                if (!double.IsFinite(linear) || linear <= 0)
                {
                    lut[i] = 0;
                    continue;
                }

                var c = Math.Min(1, linear / white);
                var encoded = c <= 0.0031308 ? 12.92 * c : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055;
                lut[i] = (byte)Math.Clamp(Math.Round(encoded * 255), 0, 255);
            }

            Cache[key] = lut;
            return lut;
        }
    }
}
