// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.RecordingEditor;
using Vortice.MediaFoundation;

namespace Rivet.Platform.Windows.RecordingEditor;

/// <summary>
/// Starts Media Foundation once per process. Windows "N" editions ship
/// without it until the Media Feature Pack is installed: then every decode
/// and encode reports <see cref="MediaUnavailableException"/> and the editor
/// explains it instead of failing silently.
/// </summary>
internal static class MediaFoundationRuntime
{
    private static readonly Lazy<bool> Started = new(() =>
    {
        try
        {
            var result = MediaFactory.MFStartup(useLightVersion: false);
            if (result.Failure)
            {
                Log.Warn("recording-editor", $"MFStartup failed: 0x{result.Code:X8}.");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SharpGen.Runtime.SharpGenException or TypeInitializationException)
        {
            Log.Warn("recording-editor", "Media Foundation is not available on this PC.", ex);
            return false;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Started.Value;

    public static void EnsureStarted()
    {
        if (!Started.Value)
        {
            throw new MediaUnavailableException("Media Foundation is not available on this PC.");
        }
    }

    /// <summary>100-nanosecond units.</summary>
    public static long ToHns(double seconds) => (long)Math.Round(seconds * 10_000_000);

    public static double FromHns(long hns) => hns / 10_000_000.0;
}
