// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.ScreenshotEditor;
using Rivet.Core.Shortcuts;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Rivet.Platform.Windows.ScreenshotEditor;

/// <summary>
/// The desktop picture of every monitor through <c>IDesktopWallpaper</c>
/// (Windows 8+), falling back to <c>SPI_GETDESKWALLPAPER</c>. Only existing
/// local files are offered (slideshows and Spotlight may report cache paths).
/// </summary>
public sealed partial class WindowsDesktopWallpapers : IDesktopWallpaperProvider
{
    private const uint SpiGetDeskWallpaper = 0x0073;

    public IReadOnlyList<string> GetWallpaperPaths()
    {
        var paths = new List<string>();
        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            try
            {
                var count = wallpaper.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    try
                    {
                        var monitor = wallpaper.GetMonitorDevicePathAt(i);
                        Add(paths, wallpaper.GetWallpaper(monitor));
                    }
                    catch (COMException)
                    {
                        // A detached monitor id; skip it.
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(wallpaper);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            Log.Warn("screenshotEditor", "IDesktopWallpaper is unavailable; using SPI_GETDESKWALLPAPER.", ex);
        }

        if (paths.Count == 0)
        {
            Add(paths, ReadSystemWallpaper());
        }

        return paths;
    }

    private static void Add(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full) && !paths.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(full);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
        }
    }

    private static unsafe string? ReadSystemWallpaper()
    {
        var buffer = stackalloc char[520];
        return SystemParametersInfoW(SpiGetDeskWallpaper, 520, buffer, 0) != 0 ? new string(buffer) : null;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static unsafe partial int SystemParametersInfoW(uint action, uint param, char* value, uint winIni);

    /// <summary>The first four methods of IDesktopWallpaper (vtable order), which is all this needs.</summary>
    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();
    }

    [ComImport]
    [Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass
    {
    }
}

/// <summary>
/// What a key types on the active keyboard layout (ToUnicodeEx with the
/// current Caps Lock and Num Lock state), so AZERTY's Shift+&amp; counts as 1.
/// </summary>
public sealed partial class WindowsKeyboardLayout : IKeyboardLayoutInfo
{
    private const int VkShift = 0x10;
    private const int VkCapital = 0x14;
    private const int VkNumLock = 0x90;
    private const uint MapVkToVsc = 0;

    /// <summary>Do not change the keyboard state (dead keys) — Windows 10 1607 and later.</summary>
    private const uint DontChangeKeyboardState = 0x4;

    public int? DigitTypedBy(KeyChord chord)
    {
        if (chord.IsEmpty || (chord.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0)
        {
            return null;
        }

        var state = new byte[256];
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            state[VkShift] = 0x80;
        }

        state[VkCapital] = (byte)(GetKeyState(VkCapital) & 1);
        state[VkNumLock] = (byte)(GetKeyState(VkNumLock) & 1);
        var layout = GetKeyboardLayout(0);
        var scan = MapVirtualKeyExW((uint)chord.VirtualKey, MapVkToVsc, layout);
        Span<char> buffer = stackalloc char[8];
        int written;
        unsafe
        {
            fixed (byte* keys = state)
            fixed (char* chars = buffer)
            {
                written = ToUnicodeEx((uint)chord.VirtualKey, scan, keys, chars, buffer.Length, DontChangeKeyboardState, layout);
            }
        }

        return written == 1 && buffer[0] is >= '0' and <= '9' ? buffer[0] - '0' : null;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    [LibraryImport("user32.dll")]
    private static unsafe partial int ToUnicodeEx(uint virtualKey, uint scanCode, byte* keyState, char* buffer, int bufferSize, uint flags, nint layout);
}

/// <summary>The Windows share sheet for a file (DataTransferManager through its window interop).</summary>
public sealed class WindowsShareSheet : IShareSheet
{
    public bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public async Task<bool> ShareFileAsync(nint windowHandle, string filePath, string title, Action targetChosen)
    {
        if (windowHandle == 0 || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var manager = DataTransferManagerInterop.GetForWindow(windowHandle);
            void OnRequested(DataTransferManager sender, DataRequestedEventArgs args)
            {
                sender.DataRequested -= OnRequested;
                args.Request.Data.Properties.Title = title;
                args.Request.Data.SetStorageItems([file], true);
            }

            void OnChosen(DataTransferManager sender, TargetApplicationChosenEventArgs args)
            {
                sender.TargetApplicationChosen -= OnChosen;
                targetChosen();
            }

            manager.DataRequested += OnRequested;
            manager.TargetApplicationChosen += OnChosen;
            DataTransferManagerInterop.ShowShareUIForWindow(windowHandle);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("screenshotEditor", "The share sheet could not open.", ex);
            return false;
        }
    }
}

public sealed class ScreenshotEditorWindowsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ITextRecognizer, WindowsTextRecognizer>();
        services.AddSingleton<IDesktopWallpaperProvider, WindowsDesktopWallpapers>();
        services.AddSingleton<IKeyboardLayoutInfo, WindowsKeyboardLayout>();
        services.AddSingleton<IShareSheet, WindowsShareSheet>();
    }
}
