// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.SystemMonitor;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>
/// Volumes with drive letters (local fixed, removable, RAM and optical with
/// media; network shares excluded), their file system and label, internal or
/// external by bus type, and I/O counters per physical disk from
/// IOCTL_DISK_PERFORMANCE on a zero-access handle (no administrator rights).
/// Eject asks Plug and Play to remove the drive (CM_Request_Device_Eject),
/// which flushes and dismounts it, or reports what keeps it busy.
/// </summary>
public sealed unsafe class WindowsDiskSensor : IDiskSensor, IDiskActions, IDisposable
{
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;
    private const uint DriveRemote = 4;
    private const uint DriveCdRom = 5;
    private const uint DriveRamDisk = 6;

    private readonly ConcurrentDictionary<string, (bool Internal, bool Ejectable, string? DiskId, DateTime At)> _identity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SafeFileHandle> _diskHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    public IReadOnlyList<DiskVolumeInfo>? ReadVolumes()
    {
        var mask = PInvoke.GetLogicalDrives();
        if (mask == 0)
        {
            return null;
        }

        var volumes = new List<DiskVolumeInfo>();

        // Empty card readers and optical drives must never pop up "There is no disk in the drive".
        PInvoke.SetThreadErrorMode(global::Windows.Win32.System.Diagnostics.Debug.THREAD_ERROR_MODE.SEM_FAILCRITICALERRORS, out var previousMode);
        try
        {
            ReadVolumesInto(volumes, mask);
        }
        finally
        {
            PInvoke.SetThreadErrorMode(previousMode, out _);
        }

        return volumes;
    }

    private void ReadVolumesInto(List<DiskVolumeInfo> volumes, uint mask)
    {
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0)
            {
                continue;
            }

            var root = $"{(char)('A' + i)}:\\";
            var type = PInvoke.GetDriveType(root);
            if (type is not (DriveFixed or DriveRemovable or DriveCdRom or DriveRamDisk) || type == DriveRemote)
            {
                continue;
            }

            if (!PInvoke.GetDiskFreeSpaceEx(root, out var freeToCaller, out var total, out _) || total == 0)
            {
                continue;
            }

            var label = new char[261];
            var fs = new char[261];
            string? fileSystem = null;
            string name;
            if (PInvoke.GetVolumeInformation(root, label.AsSpan(), out _, out _, out _, fs.AsSpan()))
            {
                name = new string(label).TrimEnd('\0').Trim();
                fileSystem = FileSystemTag(new string(fs).TrimEnd('\0'));
            }
            else
            {
                name = string.Empty;
            }

            var id = VolumeGuid(root) ?? root;
            var identity = Identity(root, type);
            if (name.Length == 0)
            {
                name = L.Get(identity.Internal ? "win.systemMonitor.localDisk" : "win.systemMonitor.usbDrive");
            }

            volumes.Add(new DiskVolumeInfo
            {
                Id = id,
                Name = name,
                MountPath = root,
                FileSystem = fileSystem,
                IsInternal = identity.Internal,
                IsSystem = string.Equals(root, _systemRoot, StringComparison.OrdinalIgnoreCase),
                IsEjectable = identity.Ejectable,
                Total = total,
                Free = freeToCaller,
                DiskId = identity.DiskId,
            });
        }
    }

    /// <summary>NTFS, exFAT, FAT32, ReFS as File Explorer writes them; other short names upper-cased.</summary>
    internal static string? FileSystemTag(string name)
    {
        var trimmed = name.Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "" => null,
            "NTFS" => "NTFS",
            "EXFAT" => "exFAT",
            "FAT32" => "FAT32",
            "FAT" => "FAT",
            "REFS" => "ReFS",
            "CDFS" => "CDFS",
            "UDF" => "UDF",
            var other when other.Length is >= 2 and <= 6 && other.All(char.IsLetterOrDigit) && other.Any(char.IsLetter) => other,
            _ => null,
        };
    }

    private static string? VolumeGuid(string root)
    {
        var buffer = new char[64];
        return PInvoke.GetVolumeNameForVolumeMountPoint(root, buffer.AsSpan()) ? new string(buffer).TrimEnd('\0') : null;
    }

    /// <summary>Bus type and physical disk of a volume; cached for a minute (it only changes on remount).</summary>
    private (bool Internal, bool Ejectable, string? DiskId) Identity(string root, uint driveType)
    {
        if (_identity.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1))
        {
            return (cached.Internal, cached.Ejectable, cached.DiskId);
        }

        var isInternal = driveType != DriveRemovable;
        var ejectable = driveType == DriveRemovable;
        string? diskId = null;
        using var handle = PInvoke.CreateFile($"\\\\.\\{root[0]}:", 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, null);
        if (!handle.IsInvalid)
        {
            if (QueryBus(handle) is { } bus)
            {
                var external = bus.Removable || bus.Bus is STORAGE_BUS_TYPE.BusTypeUsb or STORAGE_BUS_TYPE.BusType1394 or STORAGE_BUS_TYPE.BusTypeSd or STORAGE_BUS_TYPE.BusTypeMmc;
                isInternal = !external;
                ejectable = external;
            }

            if (DeviceNumber(handle) is { } number)
            {
                diskId = $"PhysicalDrive{number}";
            }
        }

        _identity[root] = (isInternal, ejectable, diskId, DateTime.UtcNow);
        return (isInternal, ejectable, diskId);
    }

    private static (bool Removable, STORAGE_BUS_TYPE Bus)? QueryBus(SafeFileHandle handle)
    {
        var query = new STORAGE_PROPERTY_QUERY { PropertyId = STORAGE_PROPERTY_ID.StorageDeviceProperty, QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery };
        var output = new byte[1024];
        fixed (byte* o = output)
        {
            uint returned;
            if (!Ioctl(handle, PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(STORAGE_PROPERTY_QUERY), o, output.Length, out returned)
                || returned < 32)
            {
                return null;
            }

            var descriptor = (STORAGE_DEVICE_DESCRIPTOR*)o;
            return (descriptor->RemovableMedia, descriptor->BusType);
        }
    }

    private static uint? DeviceNumber(SafeFileHandle handle)
    {
        STORAGE_DEVICE_NUMBER number;
        return Ioctl(handle, PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, &number, sizeof(STORAGE_DEVICE_NUMBER), out _)
            ? number.DeviceNumber
            : null;
    }

    public IReadOnlyDictionary<string, DiskCounters>? ReadCounters(IReadOnlyCollection<string> diskIds)
    {
        var result = new Dictionary<string, DiskCounters>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in diskIds)
        {
            if (!_diskHandles.TryGetValue(id, out var handle) || handle.IsInvalid || handle.IsClosed)
            {
                handle = PInvoke.CreateFile($"\\\\.\\{id}", 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null,
                    FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, null);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    continue;
                }

                _diskHandles[id] = handle;
            }

            DISK_PERFORMANCE performance;
            if (Ioctl(handle, PInvoke.IOCTL_DISK_PERFORMANCE, null, 0, &performance, sizeof(DISK_PERFORMANCE), out _))
            {
                result[id] = new DiskCounters((ulong)Math.Max(0, performance.BytesRead), (ulong)Math.Max(0, performance.BytesWritten));
            }
            else
            {
                // The disk may be gone; reopen next time.
                handle.Dispose();
                _diskHandles.Remove(id);
            }
        }

        return result;
    }

    public (EjectResult Result, string? Detail) Eject(DiskVolumeInfo volume)
    {
        if (!volume.IsEjectable || volume.DiskId is null)
        {
            return (EjectResult.NotEjectable, null);
        }

        var number = uint.Parse(volume.DiskId["PhysicalDrive".Length..], System.Globalization.CultureInfo.InvariantCulture);
        var devInst = DiskDeviceInstance(number);
        if (devInst is not { } disk)
        {
            return (EjectResult.Failed, "device not found");
        }

        // The removable parent (USB mass storage, card reader) is what Windows ejects.
        if (PInvoke.CM_Get_Parent(out var parent, disk, 0) != CONFIGRET.CR_SUCCESS)
        {
            parent = disk;
        }

        string? veto = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var name = new char[260];
            var result = PInvoke.CM_Request_Device_Eject(parent, out var vetoType, name.AsSpan(), 0);
            if (result == CONFIGRET.CR_SUCCESS && vetoType == PNP_VETO_TYPE.PNP_VetoTypeUnknown)
            {
                _identity.TryRemove(volume.MountPath, out _);
                return (EjectResult.Ejected, null);
            }

            veto = $"{vetoType}: {new string(name).TrimEnd('\0')}";
            Thread.Sleep(500);
        }

        Log.Info("monitor", $"Eject of {volume.MountPath} vetoed ({veto}).");
        return (EjectResult.Failed, veto);
    }

    /// <summary>The device instance of PhysicalDrive<paramref name="number"/> (matched through the disk interface).</summary>
    private static uint? DiskDeviceInstance(uint number)
    {
        var diskInterface = PInvoke.GUID_DEVINTERFACE_DISK;
        using var set = PInvoke.SetupDiGetClassDevs(diskInterface, null, default, SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_DEVICEINTERFACE);
        if (set.IsInvalid)
        {
            return null;
        }

        for (uint i = 0; i < 64; i++)
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
            if (!PInvoke.SetupDiEnumDeviceInterfaces(set, null, diskInterface, i, ref data))
            {
                break;
            }

            var info = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            if (DevicePath(set, data, ref info) is not { } path)
            {
                continue;
            }

            using var handle = PInvoke.CreateFile(path, 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, null);
            if (!handle.IsInvalid && DeviceNumber(handle) == number)
            {
                return info.DevInst;
            }
        }

        return null;
    }

    /// <summary>The device path of an interface (and its device info).</summary>
    internal static string? DevicePath(SafeHandle set, SP_DEVICE_INTERFACE_DATA data, ref SP_DEVINFO_DATA info)
    {
        PInvoke.SetupDiGetDeviceInterfaceDetail(set, data, default, out var required, ref info);
        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        fixed (byte* p = buffer)
        {
            // cbSize is the size of the fixed part: 8 on 64-bit (DWORD + one WCHAR, aligned).
            *(uint*)p = (uint)(IntPtr.Size == 8 ? 8 : 6);
            if (!PInvoke.SetupDiGetDeviceInterfaceDetail(set, data, buffer, out _, ref info))
            {
                return null;
            }

            return new string((char*)(p + 4)).TrimEnd('\0');
        }
    }

    /// <summary>DeviceIoControl over raw buffers (an empty input is passed as null).</summary>
    internal static bool Ioctl(SafeHandle handle, uint code, void* input, int inputSize, void* output, int outputSize, out uint returned) =>
        PInvoke.DeviceIoControl(handle, code, new ReadOnlySpan<byte>(input, inputSize), new Span<byte>(output, outputSize), out returned, null);

    public void Dispose()
    {
        foreach (var handle in _diskHandles.Values)
        {
            handle.Dispose();
        }

        _diskHandles.Clear();
    }
}
