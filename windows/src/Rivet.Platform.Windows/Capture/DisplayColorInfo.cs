// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Vortice.DXGI;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace Rivet.Platform.Windows.Capture;

/// <summary>Per-monitor colour facts: whether HDR (advanced colour) is on, and the SDR white level.</summary>
internal sealed record MonitorColor(string DeviceName, nint Monitor, bool IsHdr, double SdrWhiteScale);

/// <summary>
/// Reads HDR state from DXGI (IDXGIOutput6 colour space) and the SDR content
/// brightness from the display configuration (1.0 = 80 nits, Windows'
/// default slider position is about 2.5).
/// </summary>
internal static unsafe class DisplayColorInfo
{
    public static Dictionary<string, MonitorColor> Query()
    {
        var result = new Dictionary<string, MonitorColor>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var whiteLevels = SdrWhiteLevels();
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            var name = output.Description.DeviceName;
                            var monitor = output.Description.Monitor;
                            var hdr = false;
                            try
                            {
                                using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                                hdr = output6 is not null && output6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            }
                            catch (SharpGen.Runtime.SharpGenException)
                            {
                            }

                            var white = whiteLevels.TryGetValue(name, out var level) ? level : 2.5;
                            result[name] = new MonitorColor(name, monitor, hdr, white);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("capture", "Could not read monitor colour information.", ex);
        }

        return result;
    }

    /// <summary>GDI device name → SDR white scale (SDRWhiteLevel / 1000).</summary>
    private static Dictionary<string, double> SdrWhiteLevels()
    {
        var levels = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return levels;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
        {
            if (PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, &pathCount, p, &modeCount, m, null) != WIN32_ERROR.ERROR_SUCCESS)
            {
                return levels;
            }
        }

        for (var i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0)
            {
                continue;
            }

            var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
            white.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
            white.header.size = (uint)sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL);
            white.header.adapterId = path.targetInfo.adapterId;
            white.header.id = path.targetInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&white.header) == 0 && white.SDRWhiteLevel > 0)
            {
                levels[source.viewGdiDeviceName.ToString()] = white.SDRWhiteLevel / 1000.0;
            }
        }

        return levels;
    }
}
