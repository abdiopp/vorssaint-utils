// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules.RadialMenu;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.RadialMenu;

/// <summary>
/// Now Playing from the system media session
/// (GlobalSystemMediaTransportControlsSessionManager), in process with a 2 s
/// bound. Only a playing session produces a snapshot.
/// </summary>
public sealed class WindowsNowPlaying : INowPlayingService
{
    public async Task<NowPlayingSnapshot?> GetAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NowPlayingSanitizer.ReplyTimeout);
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(timeout.Token).ConfigureAwait(false);
            var session = manager.GetCurrentSession();
            if (session is null)
            {
                return null;
            }

            var playback = session.GetPlaybackInfo();
            if (playback?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return null;
            }

            var properties = await session.TryGetMediaPropertiesAsync().AsTask(timeout.Token).ConfigureAwait(false);
            byte[]? artwork = null;
            if (properties?.Thumbnail is { } thumbnail)
            {
                artwork = await ReadThumbnailAsync(thumbnail, timeout.Token).ConfigureAwait(false);
            }

            var appId = session.SourceAppUserModelId;
            return NowPlayingSanitizer.Sanitize(new NowPlayingSnapshot
            {
                Title = properties?.Title ?? string.Empty,
                Artist = properties?.Artist ?? string.Empty,
                Album = properties?.AlbumTitle ?? string.Empty,
                Artwork = artwork,
                AppId = appId,
                AppName = AppNameFrom(appId),
                IsPlaying = true,
            });
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn("radial", "Reading the media session failed.", ex);
            return null;
        }
    }

    public bool Activate(NowPlayingSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.AppId))
        {
            return false;
        }

        // Desktop players usually report their executable name.
        if (snapshot.AppId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && WindowsRadialPlatform.FindWindowByExeName(snapshot.AppId) is { } hwnd)
        {
            if (PInvoke.IsIconic(hwnd))
            {
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
            }

            return PInvoke.SetForegroundWindow(hwnd);
        }

        // Packaged players: activating by AppUserModelID brings a running one forward or starts it.
        return WindowsRadialPlatform.Start("explorer.exe", $"shell:AppsFolder\\{snapshot.AppId}");
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference reference, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await reference.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (stream.Size == 0 || stream.Size > NowPlayingSanitizer.ArtworkCap)
            {
                return null;
            }

            using var managed = stream.AsStreamForRead();
            using var copy = new MemoryStream();
            await managed.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? AppNameFrom(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return null;
        }

        if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return appId[..^4];
        }

        // "Publisher.App_hash!App" → "App"
        var bang = appId.LastIndexOf('!');
        var name = bang >= 0 ? appId[(bang + 1)..] : appId;
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}
