// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Platform;

namespace Rivet.Core.Modules.RadialMenu;

/// <summary>What the playing media session reports (only a playing session produces one).</summary>
public sealed record NowPlayingSnapshot
{
    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string Album { get; init; } = string.Empty;

    /// <summary>Encoded artwork (≤ 12 MiB), or null.</summary>
    public byte[]? Artwork { get; init; }

    /// <summary>The player's AppUserModelID (Windows) used to bring it forward.</summary>
    public string? AppId { get; init; }

    public string? AppName { get; init; }

    public bool IsPlaying { get; init; }

    /// <summary>"Title⏎Artist" label (falls back to the app name when the title is empty).</summary>
    public string Label => Artist.Length > 0 ? $"{DisplayTitle}\n{Artist}" : DisplayTitle;

    public string DisplayTitle => Title.Length > 0 ? Title : AppName ?? string.Empty;
}

public static class NowPlayingSanitizer
{
    public const int TextCap = 300;
    public const int ArtworkCap = 12_582_912;
    public const int AppIdCap = 255;
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Trimmed, control characters stripped, capped at 300 characters.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, TextCap));
        foreach (var c in text)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length > TextCap)
        {
            var cut = TextCap;
            if (char.IsHighSurrogate(cleaned[cut - 1]))
            {
                cut--;
            }

            cleaned = cleaned[..cut];
        }

        return cleaned;
    }

    /// <summary>Applies every cap; returns null for a session that is not playing.</summary>
    public static NowPlayingSnapshot? Sanitize(NowPlayingSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsPlaying)
        {
            return null;
        }

        var appId = snapshot.AppId is { Length: > 0 } id ? (id.Length > AppIdCap ? id[..AppIdCap] : id) : null;
        return snapshot with
        {
            Title = Clean(snapshot.Title),
            Artist = Clean(snapshot.Artist),
            Album = Clean(snapshot.Album),
            AppName = snapshot.AppName is null ? null : Clean(snapshot.AppName),
            AppId = appId,
            Artwork = snapshot.Artwork is { Length: > 0 and <= ArtworkCap } art ? art : null,
        };
    }
}

/// <summary>The system media session (GlobalSystemMediaTransportControlsSessionManager on Windows).</summary>
public interface INowPlayingService
{
    /// <summary>The playing session, or null when nothing plays (bounded by a 2 s timeout).</summary>
    Task<NowPlayingSnapshot?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Brings the player forward (restore + activate, or launch by AUMID). Returns false when it could not.</summary>
    bool Activate(NowPlayingSnapshot snapshot);
}

/// <summary>Launching, icons and window placement for radial slices.</summary>
public interface IRadialPlatform
{
    /// <summary>Activates a running copy of the app, or launches it. False when the path does not exist.</summary>
    bool LaunchOrActivateApp(string path);

    /// <summary>Opens a file or folder with its default handler. False when it does not exist.</summary>
    bool OpenPath(string path);

    /// <summary>Opens a link with the default handler (any scheme).</summary>
    void OpenUrl(string url);

    /// <summary>The name Explorer shows for the path (app description, folder name…).</summary>
    string DisplayName(string path);

    /// <summary>The shell icon of a path at <paramref name="sizePx"/> pixels, or null.</summary>
    PixelBuffer? Icon(string path, int sizePx);

    /// <summary>The window in front (captured before the wheel shows, for layout and shortcut slices).</summary>
    nint ForegroundWindow();

    /// <summary>Gives the foreground back to <paramref name="hwnd"/> after the wheel closes.</summary>
    void Activate(nint hwnd);

    /// <summary>Applies one of the 41 layout actions to <paramref name="hwnd"/>; false when there is no suitable window.</summary>
    bool ApplyWindowLayout(nint hwnd, string layoutId);

    /// <summary>Expands "~" and environment variables in a stored path.</summary>
    string ExpandPath(string path);

    void Beep();
}
