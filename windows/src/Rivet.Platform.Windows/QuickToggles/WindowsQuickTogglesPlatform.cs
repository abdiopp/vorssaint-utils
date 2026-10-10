// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Toggles;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Toggles;

/// <summary>
/// Quick toggles on Windows (spec 06 §7.5). Dark mode writes both theme
/// values and broadcasts ImmersiveColorSet; hidden files, extensions and
/// desktop icons write Explorer's Advanced keys and refresh open Explorer
/// views live (the desktop-icons command 0x7402 is undocumented); the Recycle
/// Bin empties without Windows' dialog because the app already confirmed;
/// external disks are removed with CM_Request_Device_Eject (a veto is a failure).
/// </summary>
public sealed unsafe class WindowsQuickTogglesPlatform : IQuickTogglesPlatform
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const uint RefreshCommand = 0x7103;
    private const uint ToggleDesktopIconsCommand = 0x7402;

    private readonly NativeWindowHost _host;

    public WindowsQuickTogglesPlatform(NativeWindowHost host)
    {
        _host = host;
    }

    // ── Dark mode ──────────────────────────────────────────────────────

    public bool IsDarkMode => ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;

    public bool SetDarkMode(bool dark)
    {
        var value = dark ? 0 : 1;
        if (!WriteDword(PersonalizeKey, "AppsUseLightTheme", value) || !WriteDword(PersonalizeKey, "SystemUsesLightTheme", value))
        {
            return false;
        }

        Broadcast("ImmersiveColorSet");
        return true;
    }

    // ── Explorer settings ──────────────────────────────────────────────

    public bool HiddenFilesShown => ReadDword(AdvancedKey, "Hidden", 2) == 1;

    public bool SetHiddenFilesShown(bool show)
    {
        if (!WriteDword(AdvancedKey, "Hidden", show ? 1 : 2))
        {
            return false;
        }

        RefreshExplorer();
        return true;
    }

    public bool FileExtensionsShown => ReadDword(AdvancedKey, "HideFileExt", 1) == 0;

    public bool SetFileExtensionsShown(bool show)
    {
        if (!WriteDword(AdvancedKey, "HideFileExt", show ? 0 : 1))
        {
            return false;
        }

        RefreshExplorer();
        return true;
    }

    public bool DesktopIconsHidden => ReadDword(AdvancedKey, "HideIcons", 0) == 1;

    public bool SetDesktopIconsHidden(bool hide)
    {
        var view = DesktopView();
        if (!view.IsNull)
        {
            var list = PInvoke.FindWindowEx(view, HWND.Null, "SysListView32", null);
            var visible = !list.IsNull && PInvoke.IsWindowVisible(list);
            if (visible == hide)
            {
                // The command toggles, so send it only when the state differs.
                PInvoke.SendMessage(view, PInvoke.WM_COMMAND, ToggleDesktopIconsCommand, 0);
            }
        }

        return WriteDword(AdvancedKey, "HideIcons", hide ? 1 : 0);
    }

    /// <summary>The desktop's SHELLDLL_DefView: under Progman, or under a WorkerW when a wallpaper slideshow runs.</summary>
    private static HWND DesktopView()
    {
        var progman = PInvoke.FindWindow("Progman", null);
        var view = PInvoke.FindWindowEx(progman, HWND.Null, "SHELLDLL_DefView", null);
        if (!view.IsNull)
        {
            return view;
        }

        var worker = HWND.Null;
        while (!(worker = PInvoke.FindWindowEx(HWND.Null, worker, "WorkerW", null)).IsNull)
        {
            view = PInvoke.FindWindowEx(worker, HWND.Null, "SHELLDLL_DefView", null);
            if (!view.IsNull)
            {
                return view;
            }
        }

        return HWND.Null;
    }

    /// <summary>Tells the shell settings changed and refreshes every open Explorer view (F5).</summary>
    private static void RefreshExplorer()
    {
        PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
        Broadcast("ShellState");
        var views = new List<HWND>();
        WNDENUMPROC callback = (hwnd, _) =>
        {
            var name = stackalloc char[64];
            var length = PInvoke.GetClassName(hwnd, new PWSTR(name), 64);
            var className = new string(name, 0, Math.Max(0, length));
            if (className is "CabinetWClass" or "ExploreWClass")
            {
                var view = FindDescendant(hwnd, "SHELLDLL_DefView", 0);
                if (!view.IsNull)
                {
                    views.Add(view);
                }
            }

            return true;
        };
        PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);
        var desktop = DesktopView();
        if (!desktop.IsNull)
        {
            views.Add(desktop);
        }

        foreach (var view in views)
        {
            PInvoke.PostMessage(view, PInvoke.WM_COMMAND, RefreshCommand, 0);
        }
    }

    private static HWND FindDescendant(HWND parent, string className, int depth)
    {
        if (depth > 6)
        {
            return HWND.Null;
        }

        var child = HWND.Null;
        var name = stackalloc char[64];
        while (!(child = PInvoke.FindWindowEx(parent, child, null, null)).IsNull)
        {
            var length = PInvoke.GetClassName(child, new PWSTR(name), 64);
            if (new string(name, 0, Math.Max(0, length)) == className)
            {
                return child;
            }

            var found = FindDescendant(child, className, depth + 1);
            if (!found.IsNull)
            {
                return found;
            }
        }

        return HWND.Null;
    }

    // ── Recycle Bin ────────────────────────────────────────────────────

    public (long Items, long Bytes)? RecycleBinInfo()
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)sizeof(SHQUERYRBINFO) };
        return PInvoke.SHQueryRecycleBin(null, ref info).Succeeded ? (info.i64NumItems, info.i64Size) : null;
    }

    public bool EmptyRecycleBin()
    {
        if (RecycleBinInfo() is { Items: 0 })
        {
            return true;
        }

        var result = PInvoke.SHEmptyRecycleBin(HWND.Null, null, PInvoke.SHERB_NOCONFIRMATION | PInvoke.SHERB_NOPROGRESSUI | PInvoke.SHERB_NOSOUND);
        return result.Succeeded || RecycleBinInfo() is { Items: 0 };
    }

    // ── Session and display ────────────────────────────────────────────

    public bool LockScreen() => PInvoke.LockWorkStation();

    public bool IsScreenSaverConfigured
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("SCRNSAVE.EXE") is string saver && saver.Trim().Length > 0;
        }
    }

    public bool StartScreenSaver()
    {
        if (!IsScreenSaverConfigured)
        {
            return false;
        }

        return PInvoke.PostMessage(HostWindow, PInvoke.WM_SYSCOMMAND, PInvoke.SC_SCREENSAVE, 0);
    }

    /// <summary>SC_MONITORPOWER 2 through our own window (a broadcast could wait on hung apps).</summary>
    public bool TurnOffDisplay() => PInvoke.PostMessage(HostWindow, PInvoke.WM_SYSCOMMAND, PInvoke.SC_MONITORPOWER, 2);

    public bool Sleep() => PInvoke.SetSuspendState(false, false, false);

    private HWND HostWindow => new((void*)_host.Handle);

    // ── Eject all disks ────────────────────────────────────────────────

    public IReadOnlyList<EjectableVolume> EjectableVolumes(IReadOnlyCollection<string> excluded)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var result = new List<EjectableVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var qualifies = drive.DriveType is DriveType.Removable or DriveType.CDRom
                                || (drive.DriveType == DriveType.Fixed && IsExternalBus(drive.Name));
                if (!qualifies)
                {
                    continue;
                }

                var volumeGuid = VolumeGuid(drive.Name);
                var label = drive.VolumeLabel;
                if (excluded.Any(e => string.Equals(e, label, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(e.TrimEnd('\\'), drive.Name.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                      || (volumeGuid is not null && string.Equals(e, volumeGuid, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                result.Add(new EjectableVolume(drive.Name, label, volumeGuid));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    public Task<EjectResult> EjectAllAsync(IReadOnlyCollection<string> excluded) => Task.Run(() =>
    {
        var volumes = EjectableVolumes(excluded);
        var failed = new List<string>();
        var ejected = 0;
        foreach (var volume in volumes)
        {
            // A volume that vanished because a sibling partition's eject removed its disk is not a failure.
            if (!Directory.Exists(volume.Root))
            {
                ejected++;
                continue;
            }

            if (Eject(volume.Root))
            {
                ejected++;
            }
            else if (Directory.Exists(volume.Root))
            {
                failed.Add(volume.Label.Length > 0 ? volume.Label : volume.Root);
            }
        }

        return new EjectResult(ejected, failed.Count, failed);
    });

    private static bool IsExternalBus(string root)
    {
        using var device = OpenVolume(root);
        if (device is null)
        {
            return false;
        }

        var query = stackalloc byte[12];
        ((int*)query)[0] = (int)STORAGE_PROPERTY_ID.StorageDeviceProperty;
        ((int*)query)[1] = (int)STORAGE_QUERY_TYPE.PropertyStandardQuery;
        var output = stackalloc byte[1024];
        uint returned;
        if (!PInvoke.DeviceIoControl((HANDLE)device.DangerousGetHandle(), PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, query, 12, output, 1024, &returned, null))
        {
            return false;
        }

        var descriptor = (STORAGE_DEVICE_DESCRIPTOR*)output;
        return descriptor->BusType is STORAGE_BUS_TYPE.BusTypeUsb or STORAGE_BUS_TYPE.BusType1394 or STORAGE_BUS_TYPE.BusTypeSd or STORAGE_BUS_TYPE.BusTypeMmc;
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle? OpenVolume(string root)
    {
        var path = @"\\.\" + root.TrimEnd('\\');
        var handle = PInvoke.CreateFile(path, 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static string? VolumeGuid(string root)
    {
        var buffer = new char[64];
        return PInvoke.GetVolumeNameForVolumeMountPoint(root, buffer) ? new string(buffer).TrimEnd('\0') : null;
    }

    /// <summary>Finds the disk (or optical drive) device behind the volume and asks its parent to eject.</summary>
    private static bool Eject(string root)
    {
        uint deviceNumber;
        uint deviceType;
        using (var volume = OpenVolume(root))
        {
            if (volume is null)
            {
                return false;
            }

            STORAGE_DEVICE_NUMBER number;
            uint returned;
            if (!PInvoke.DeviceIoControl((HANDLE)volume.DangerousGetHandle(), PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, &number, (uint)sizeof(STORAGE_DEVICE_NUMBER), &returned, null))
            {
                return false;
            }

            deviceNumber = number.DeviceNumber;
            deviceType = number.DeviceType;
        }

        // FILE_DEVICE_CD_ROM = 2, FILE_DEVICE_DVD = 0x33.
        var interfaceGuid = deviceType is 2 or 0x33 ? PInvoke.GUID_DEVINTERFACE_CDROM : PInvoke.GUID_DEVINTERFACE_DISK;
        var devInst = DeviceInstanceFor(interfaceGuid, deviceNumber);
        if (devInst == 0 || PInvoke.CM_Get_Parent(out var parent, devInst, 0) != CONFIGRET.CR_SUCCESS)
        {
            return false;
        }

        var vetoName = stackalloc char[260];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            PNP_VETO_TYPE veto;
            var result = PInvoke.CM_Request_Device_Eject(parent, &veto, new PWSTR(vetoName), 260, 0);
            if (result == CONFIGRET.CR_SUCCESS && veto == PNP_VETO_TYPE.PNP_VetoTypeUnknown)
            {
                return true;
            }

            Log.Info("quickToggles", $"Eject of {root} vetoed ({veto}).");
            Thread.Sleep(300);
        }

        return false;
    }

    private static uint DeviceInstanceFor(Guid interfaceGuid, uint deviceNumber)
    {
        var set = PInvoke.SetupDiGetClassDevs(&interfaceGuid, (PCWSTR)null, HWND.Null, SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_DEVICEINTERFACE);
        if (set.IsNull || set.Value == -1)
        {
            return 0;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
                if (!PInvoke.SetupDiEnumDeviceInterfaces(set, null, &interfaceGuid, index, &data))
                {
                    return 0;
                }

                uint required;
                PInvoke.SetupDiGetDeviceInterfaceDetail(set, &data, null, 0, &required, null);
                if (required == 0)
                {
                    continue;
                }

                var bytes = new byte[required];
                var info = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
                string path;
                fixed (byte* buffer = bytes)
                {
                    var detail = (SP_DEVICE_INTERFACE_DETAIL_DATA_W*)buffer;
                    detail->cbSize = (uint)(IntPtr.Size == 8 ? 8 : 6);
                    if (!PInvoke.SetupDiGetDeviceInterfaceDetail(set, &data, detail, required, null, &info))
                    {
                        continue;
                    }

                    path = new string((char*)(buffer + 4));
                }

                var handle = PInvoke.CreateFile(path, 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0, null);
                using (handle)
                {
                    if (handle.IsInvalid)
                    {
                        continue;
                    }

                    STORAGE_DEVICE_NUMBER number;
                    uint returned;
                    if (PInvoke.DeviceIoControl((HANDLE)handle.DangerousGetHandle(), PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, &number, (uint)sizeof(STORAGE_DEVICE_NUMBER), &returned, null)
                        && number.DeviceNumber == deviceNumber)
                    {
                        return info.DevInst;
                    }
                }
            }
        }
        finally
        {
            PInvoke.SetupDiDestroyDeviceInfoList(set);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void Broadcast(string area)
    {
        fixed (char* text = area)
        {
            PInvoke.SendMessageTimeout(HWND.HWND_BROADCAST, PInvoke.WM_SETTINGCHANGE, 0, (nint)text, SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 100, null);
        }
    }

    private static int ReadDword(string path, string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) is int value ? value : fallback;
    }

    private static bool WriteDword(string path, string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warn("quickToggles", $"Could not write {name}.", ex);
            return false;
        }
    }
}

public sealed class WindowsQuickTogglesRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) =>
        services.AddSingleton<IQuickTogglesPlatform, WindowsQuickTogglesPlatform>();
}
