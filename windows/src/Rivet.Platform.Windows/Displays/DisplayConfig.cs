// SPDX-License-Identifier: GPL-3.0-or-later
using Windows.Win32;
using Windows.Win32.Devices.Display;

namespace Rivet.Platform.Windows.Displays;

/// <summary>An active display path as Windows' display configuration reports it.</summary>
internal sealed record DisplayTarget(
    string GdiDeviceName,
    string FriendlyName,
    int Technology,
    string DevicePath,
    ushort ManufacturerId,
    ushort ProductCodeId,
    uint ConnectorInstance)
{
    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values.
    public const int Lvds = 6;
    public const int DisplayPortEmbedded = 11;
    public const int UdiEmbedded = 13;
    public const int IndirectVirtual = 17;
    public const int Internal = unchecked((int)0x80000000);

    /// <summary>The laptop's own panel.</summary>
    public bool IsInternal => Technology is Internal or DisplayPortEmbedded or UdiEmbedded or Lvds;

    /// <summary>Software displays (remote desktop helpers, virtual monitors).</summary>
    public bool IsVirtual => Technology == IndirectVirtual;

    /// <summary>"vendor:product:connector" for remembering per-monitor choices.</summary>
    public string Fingerprint => $"{ManufacturerId:X4}:{ProductCodeId:X4}:{ConnectorInstance}";
}

/// <summary>QueryDisplayConfig + DisplayConfigGetDeviceInfo: friendly monitor names and connection types.</summary>
internal static unsafe class DisplayConfig
{
    public static List<DisplayTarget>? ActiveTargets()
    {
        const QUERY_DISPLAY_CONFIG_FLAGS flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (PInvoke.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != 0)
            {
                return null;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            var result = PInvoke.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes);
            if (result == global::Windows.Win32.Foundation.WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                continue; // The topology changed between the two calls.
            }

            if (result != 0)
            {
                return null;
            }

            var targets = new List<DisplayTarget>();
            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (PInvoke.DisplayConfigGetDeviceInfo((DISPLAYCONFIG_DEVICE_INFO_HEADER*)&source) != 0)
                {
                    continue;
                }

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                var hasTarget = PInvoke.DisplayConfigGetDeviceInfo((DISPLAYCONFIG_DEVICE_INFO_HEADER*)&target) == 0;
                targets.Add(new DisplayTarget(
                    source.viewGdiDeviceName.ToString(),
                    hasTarget ? target.monitorFriendlyDeviceName.ToString() : string.Empty,
                    hasTarget ? (int)target.outputTechnology : (int)path.targetInfo.outputTechnology,
                    hasTarget ? target.monitorDevicePath.ToString() : string.Empty,
                    hasTarget ? target.edidManufactureId : (ushort)0,
                    hasTarget ? target.edidProductCodeId : (ushort)0,
                    hasTarget ? target.connectorInstance : 0));
            }

            return targets;
        }

        return null;
    }
}
