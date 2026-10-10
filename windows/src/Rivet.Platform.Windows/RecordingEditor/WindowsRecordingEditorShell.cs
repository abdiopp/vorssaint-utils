// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.RecordingEditor;

namespace Rivet.Platform.Windows.RecordingEditor;

/// <summary>
/// Desktop wallpapers per monitor (<c>IDesktopWallpaper::GetWallpaper</c>,
/// falling back to <c>SPI_GETDESKWALLPAPER</c>) and the default alert sound.
/// </summary>
internal sealed partial class WindowsRecordingEditorShell : IRecordingEditorShell
{
    private const uint SpiGetDeskWallpaper = 0x0073;
    private const uint MbOk = 0x0;

    public IReadOnlyList<string> CurrentWallpapers()
    {
        var result = new List<string>();
        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperCoClass();
            try
            {
                var count = wallpaper.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    var monitor = wallpaper.GetMonitorDevicePathAt(i);
                    Add(result, wallpaper.GetWallpaper(monitor));
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(wallpaper);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            Log.Info("recording-editor", "IDesktopWallpaper is unavailable; reading the single desktop wallpaper.");
        }

        if (result.Count == 0)
        {
            Add(result, DesktopWallpaperPath());
        }

        return result;
    }

    public void Beep() => MessageBeep(MbOk);

    private static unsafe string? DesktopWallpaperPath()
    {
        var buffer = stackalloc char[1024];
        return SystemParametersInfo(SpiGetDeskWallpaper, 1024, buffer, 0) ? new string(buffer).TrimEnd('\0') : null;
    }

    private static void Add(List<string> list, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(path);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SystemParametersInfo(uint action, uint param, char* buffer, uint winIni);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);

    /// <summary>The first methods of IDesktopWallpaper (shobjidl_core.h), in vtable order.</summary>
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
    private class DesktopWallpaperCoClass
    {
    }
}
