// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Rivet.Platform.Windows.Sound;

/// <summary>
/// The few flat Win32/WinRT functions the sound module needs, declared here
/// (rather than in a NativeMethods.txt) so the module stays self-contained and
/// its COM objects never go through the built-in COM marshaller.
/// </summary>
internal static unsafe partial class SoundNative
{
    public const uint ClsctxAll = 0x17;
    public const uint CoinitMultithreaded = 0x0;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const int AppModelErrorNoApplication = 15703;
    public const int ErrorInsufficientBuffer = 122;
    public const int SOk = 0;
    public const int SFalse = 1;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll")]
    public static partial int WindowsCreateString(char* sourceString, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    public static partial char* WindowsGetStringRawBuffer(nint hstring, out uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll")]
    public static partial int GetApplicationUserModelId(nint process, ref uint length, char* buffer);

    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHDefExtractIcon(string iconFile, int index, uint flags, out nint largeIcon, out nint smallIcon, uint iconSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(nint icon, out IconInfo info);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    public static partial int GetObjectW(nint handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage);

    /// <summary>Creates an HSTRING the caller deletes with <see cref="WindowsDeleteString"/>.</summary>
    public static nint CreateHString(string value)
    {
        fixed (char* chars = value)
        {
            return WindowsCreateString(chars, (uint)value.Length, out var hstring) >= 0 ? hstring : 0;
        }
    }

    public static string? ReadHString(nint hstring)
    {
        if (hstring == 0)
        {
            return null;
        }

        var chars = WindowsGetStringRawBuffer(hstring, out var length);
        return chars == null ? null : new string(chars, 0, (int)length);
    }

    /// <summary>Reads and frees a CoTaskMem string returned by a COM method.</summary>
    public static string? TakeCoTaskString(nint pointer)
    {
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Bitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }
}
