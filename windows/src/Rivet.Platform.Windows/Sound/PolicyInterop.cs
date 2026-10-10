// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;

namespace Rivet.Platform.Windows.Sound;

/// <summary>
/// The undocumented <c>IPolicyConfig</c> (PolicyConfigClient), the only way to
/// change the default audio device. Its layout has been stable since Windows 7
/// (used by EarTrumpet, SoundSwitch, AudioDeviceCmdlets). Only
/// <c>SetDefaultEndpoint</c> is called, through its vtable slot, so a
/// mismatch in the other (unused) methods cannot matter. Audio thread only.
/// </summary>
internal sealed unsafe class PolicyConfigClient : IDisposable
{
    private static readonly Guid Clsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid Iid = new("f8679f50-850a-41cf-9c72-430f290290c8");

    /// <summary>IUnknown (3) + GetMixFormat, GetDeviceFormat, ResetDeviceFormat, SetDeviceFormat,
    /// GetProcessingPeriod, SetProcessingPeriod, GetShareMode, SetShareMode, GetPropertyValue, SetPropertyValue (10).</summary>
    private const int SetDefaultEndpointSlot = 13;

    private nint _instance;

    private PolicyConfigClient(nint instance)
    {
        _instance = instance;
    }

    /// <summary>Null when the class or interface is not there (a future Windows could remove it).</summary>
    public static PolicyConfigClient? TryCreate()
    {
        try
        {
            var hr = SoundNative.CoCreateInstance(Clsid, 0, SoundNative.ClsctxAll, Iid, out var instance);
            if (hr >= 0 && instance != 0)
            {
                return new PolicyConfigClient(instance);
            }

            Log.Warn("sound", $"Changing the default device is not available (0x{hr:X8}).");
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Changing the default device is not available.", ex);
        }

        return null;
    }

    public int SetDefaultEndpoint(string deviceId, ERole role)
    {
        if (_instance == 0)
        {
            return unchecked((int)0x80004005);
        }

        var vtable = *(nint**)_instance;
        var setDefault = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)vtable[SetDefaultEndpointSlot];
        fixed (char* id = deviceId)
        {
            return setDefault(_instance, id, (int)role);
        }
    }

    public void Dispose()
    {
        if (_instance != 0)
        {
            Marshal.Release(_instance);
            _instance = 0;
        }
    }
}

/// <summary>
/// The undocumented <c>IAudioPolicyConfigFactory</c> behind Windows' "App
/// volume and device preferences": a per-app persisted output. The interface
/// id changed with Windows 11 (build 21390); both are probed and the routing
/// option is hidden when neither answers. Only the three persisted-endpoint
/// methods are called, through their vtable slots (the layout EarTrumpet
/// uses). Audio thread only.
/// </summary>
internal sealed unsafe class AudioPolicyRouting : IDisposable
{
    private const string ActivatableClass = "Windows.Media.Internal.AudioPolicyConfig";
    private const int Windows11Build = 21390;
    private static readonly Guid IidCurrent = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid IidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

    /// <summary>IUnknown (3) + IInspectable (3) + 19 methods before the persisted-endpoint ones.</summary>
    private const int SetPersistedSlot = 25;
    private const int GetPersistedSlot = 26;
    private const int ClearAllSlot = 27;

    private const string MmDevApiPrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureInterfaceSuffix = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    private nint _factory;

    private AudioPolicyRouting(nint factory)
    {
        _factory = factory;
    }

    public static AudioPolicyRouting? TryCreate(int build)
    {
        var order = build >= Windows11Build ? new[] { IidCurrent, IidDownlevel } : new[] { IidDownlevel, IidCurrent };
        var className = SoundNative.CreateHString(ActivatableClass);
        if (className == 0)
        {
            return null;
        }

        try
        {
            foreach (var iid in order)
            {
                if (SoundNative.RoGetActivationFactory(className, iid, out var factory) >= 0 && factory != 0)
                {
                    return new AudioPolicyRouting(factory);
                }
            }

            Log.Info("sound", "Per-app output is not available on this version of Windows.");
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Per-app output is not available.", ex);
        }
        finally
        {
            SoundNative.WindowsDeleteString(className);
        }

        return null;
    }

    /// <summary>Routes the app of <paramref name="processId"/> to <paramref name="deviceId"/> (null = follow the default).</summary>
    public int SetPersistedDefaultEndpoint(uint processId, EDataFlow flow, ERole role, string? deviceId)
    {
        var hstring = string.IsNullOrEmpty(deviceId) ? 0 : SoundNative.CreateHString(Pack(deviceId, flow));
        try
        {
            var set = (delegate* unmanaged[Stdcall]<nint, uint, int, int, nint, int>)Slot(SetPersistedSlot);
            return set(_factory, processId, (int)flow, (int)role, hstring);
        }
        finally
        {
            if (hstring != 0)
            {
                SoundNative.WindowsDeleteString(hstring);
            }
        }
    }

    /// <summary>The app's persisted output (an endpoint id), or null when it follows the default.</summary>
    public string? GetPersistedDefaultEndpoint(uint processId, EDataFlow flow, ERole role)
    {
        nint hstring = 0;
        var get = (delegate* unmanaged[Stdcall]<nint, uint, int, int, nint*, int>)Slot(GetPersistedSlot);
        var hr = get(_factory, processId, (int)flow, (int)role, &hstring);
        try
        {
            return hr >= 0 ? Unpack(SoundNative.ReadHString(hstring)) : null;
        }
        finally
        {
            if (hstring != 0)
            {
                SoundNative.WindowsDeleteString(hstring);
            }
        }
    }

    public int ClearAllPersistedApplicationDefaultEndpoints()
    {
        var clear = (delegate* unmanaged[Stdcall]<nint, int>)Slot(ClearAllSlot);
        return clear(_factory);
    }

    /// <summary>The policy store wants the device interface path, not the bare endpoint id.</summary>
    internal static string Pack(string deviceId, EDataFlow flow) =>
        MmDevApiPrefix + deviceId + (flow == EDataFlow.Render ? RenderInterfaceSuffix : CaptureInterfaceSuffix);

    internal static string? Unpack(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var id = path;
        if (id.StartsWith(MmDevApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[MmDevApiPrefix.Length..];
        }

        if (id.EndsWith(RenderInterfaceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^RenderInterfaceSuffix.Length];
        }
        else if (id.EndsWith(CaptureInterfaceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^CaptureInterfaceSuffix.Length];
        }

        return id.Length == 0 ? null : id;
    }

    private nint Slot(int index) => (*(nint**)_factory)[index];

    public void Dispose()
    {
        if (_factory != 0)
        {
            Marshal.Release(_factory);
            _factory = 0;
        }
    }
}
