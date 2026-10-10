// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Rivet.Platform.Windows.Sound;

// Core Audio session interfaces (audiopolicy.h / mmdeviceapi.h), declared for
// source-generated COM. NAudio covers devices and endpoint volume, but its
// session wrappers write with an empty event context and keep the raw
// interfaces internal; these let the mixer tag its own writes and read the
// event context of every change. Methods keep the native vtable order and
// return the HRESULT (PreserveSig); strings and interfaces cross as pointers.

/// <summary>EDataFlow.</summary>
internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
}

/// <summary>ERole.</summary>
internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

/// <summary>AudioSessionState.</summary>
internal enum SessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2,
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out nint endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out nint device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint client);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, uint context, nint activationParams, out nint instance);

    [PreserveSig]
    int OpenPropertyStore(uint access, out nint properties);

    [PreserveSig]
    int GetId(out nint id);

    [PreserveSig]
    int GetState(out uint state);
}

[GeneratedComInterface]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(nint sessionGuid, uint streamFlags, out nint sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(nint sessionGuid, uint streamFlags, out nint audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out nint sessionEnum);

    [PreserveSig]
    int RegisterSessionNotification(nint sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(nint sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification(nint sessionId, nint duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(nint duckNotification);
}

[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    [PreserveSig]
    int GetSession(int sessionIndex, out nint session);
}

/// <summary>IAudioSessionControl2, with the IAudioSessionControl methods first (one flat vtable).</summary>
[GeneratedComInterface]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
internal partial interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out SessionState state);

    [PreserveSig]
    int GetDisplayName(out nint displayName);

    [PreserveSig]
    int SetDisplayName(nint value, nint eventContext);

    [PreserveSig]
    int GetIconPath(out nint iconPath);

    [PreserveSig]
    int SetIconPath(nint value, nint eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    int SetGroupingParam(nint groupingParam, nint eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(nint client);

    [PreserveSig]
    int UnregisterAudioSessionNotification(nint client);

    [PreserveSig]
    int GetSessionIdentifier(out nint identifier);

    [PreserveSig]
    int GetSessionInstanceIdentifier(out nint identifier);

    [PreserveSig]
    int GetProcessId(out uint processId);

    /// <summary>S_OK for the system sounds session, S_FALSE otherwise.</summary>
    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference(int optOut);
}

[GeneratedComInterface]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal partial interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, in Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute(int mute, in Guid eventContext);

    [PreserveSig]
    int GetMute(out int mute);
}

/// <summary>Implemented by the app: a new session appeared on an endpoint.</summary>
[GeneratedComInterface]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
internal partial interface IAudioSessionNotification
{
    [PreserveSig]
    int OnSessionCreated(nint newSession);
}

/// <summary>Implemented by the app: one session's changes.</summary>
[GeneratedComInterface]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
internal partial interface IAudioSessionEvents
{
    [PreserveSig]
    int OnDisplayNameChanged(nint newDisplayName, nint eventContext);

    [PreserveSig]
    int OnIconPathChanged(nint newIconPath, nint eventContext);

    [PreserveSig]
    int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext);

    [PreserveSig]
    int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumes, uint changedChannel, nint eventContext);

    [PreserveSig]
    int OnGroupingParamChanged(nint newGroupingParam, nint eventContext);

    [PreserveSig]
    int OnStateChanged(SessionState newState);

    [PreserveSig]
    int OnSessionDisconnected(int disconnectReason);
}

/// <summary>Wrapping and releasing COM objects with one source-generated ComWrappers instance.</summary>
internal static class ComObjects
{
    public static readonly Guid IidAudioSessionManager2 = typeof(IAudioSessionManager2).GUID;
    public static readonly Guid IidAudioSessionNotification = typeof(IAudioSessionNotification).GUID;
    public static readonly Guid IidAudioSessionEvents = typeof(IAudioSessionEvents).GUID;
    public static readonly Guid IidMMDeviceEnumerator = typeof(IMMDeviceEnumerator).GUID;
    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static StrategyBasedComWrappers Wrappers { get; } = new();

    /// <summary>Wraps <paramref name="pointer"/> (taking over its reference) as <typeparamref name="T"/>; null on failure.</summary>
    public static T? Wrap<T>(nint pointer)
        where T : class
    {
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            var wrapper = Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            if (wrapper is T typed)
            {
                return typed;
            }

            Release(wrapper);
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Releases the native object now instead of at garbage collection.</summary>
    public static void Release(object? wrapper)
    {
        if (wrapper is ComObject com)
        {
            try
            {
                com.FinalRelease();
            }
            catch (Exception)
            {
                // Already released or the object died with its process.
            }
        }
    }

    /// <summary>A COM pointer for a managed callback object, for one interface. The caller releases it.</summary>
    public static nint InterfaceFor(object callback, Guid iid)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(callback, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in iid, out var pointer) >= 0 ? pointer : 0;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
