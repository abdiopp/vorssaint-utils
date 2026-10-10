// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Rivet.Platform.Windows.Recording;

/// <summary>
/// The classic-COM glue Windows Graphics Capture needs from a desktop app:
/// <c>IGraphicsCaptureItemInterop</c> (capture items for an HMONITOR or HWND),
/// <c>CreateDirect3D11DeviceFromDXGIDevice</c> (the WinRT device for the frame
/// pool) and <c>IDirect3DDxgiInterfaceAccess</c> (the D3D11 texture behind a
/// frame). Calls go through the vtables directly, so no runtime-callable
/// wrappers are involved and reference counts stay explicit.
/// </summary>
internal static unsafe partial class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiInterfaceAccessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private const string CaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    public static GraphicsCaptureItem CreateItemForMonitor(nint monitor) => CreateItem(monitor, slot: 4);

    public static GraphicsCaptureItem CreateItemForWindow(nint window) => CreateItem(window, slot: 3);

    /// <summary>The WinRT device wrapping a D3D11 device, for the frame pool.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>The D3D11 texture behind a capture frame's surface (caller disposes it).</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in DxgiInterfaceAccessId, out var access));
            try
            {
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3];
                var iid = Texture2DId;
                nint texture;
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static GraphicsCaptureItem CreateItem(nint handle, int slot)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(CaptureItemClass, CaptureItemClass.Length, out var className));
        nint factory;
        try
        {
            var interopId = GraphicsCaptureItemInteropId;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, &interopId, &factory));
        }
        finally
        {
            WindowsDeleteString(className);
        }

        try
        {
            // IGraphicsCaptureItemInterop: IUnknown (0–2), CreateForWindow (3), CreateForMonitor (4).
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)factory)[slot];
            var itemId = GraphicsCaptureItemId;
            nint item;
            Marshal.ThrowExceptionForHR(create(factory, handle, &itemId, &item));
            try
            {
                return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);
}
