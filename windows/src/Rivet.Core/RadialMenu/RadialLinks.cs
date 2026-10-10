// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.RadialMenu;

/// <summary>Link handling for URL slices (spec 07 §6.2).</summary>
public static class RadialLinks
{
    /// <summary>
    /// Normalizes what the user typed: trim; reject empty or anything with a
    /// space; keep an explicit scheme (<c>://</c>, or <c>letter[letters digits + - .]*:</c>
    /// unless a digit follows the colon after something with a dot or
    /// <c>localhost</c>, which is a port); http/https need a host; otherwise
    /// prefix <c>https://</c> and require a host.
    /// </summary>
    public static string? Normalize(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (HasExplicitScheme(text))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                return text.Contains("://", StringComparison.Ordinal) ? null : text;
            }

            if (uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.Host))
            {
                return null;
            }

            return text;
        }

        var prefixed = "https://" + text;
        return Uri.TryCreate(prefixed, UriKind.Absolute, out var https) && !string.IsNullOrEmpty(https.Host) ? prefixed : null;
    }

    public static bool HasExplicitScheme(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        var colon = text.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(text[0]))
        {
            return false;
        }

        for (var i = 1; i < colon; i++)
        {
            var c = text[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not '+' and not '-' and not '.')
            {
                return false;
            }
        }

        // "example.com:8080" and "localhost:3000" are a host with a port, not a scheme.
        var before = text[..colon];
        var digitAfter = colon + 1 < text.Length && char.IsAsciiDigit(text[colon + 1]);
        if (digitAfter && (before.Contains('.', StringComparison.Ordinal) || before.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    /// <summary>The host shown as the automatic label (the whole URL when there is none).</summary>
    public static string HostLabel(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.OriginalString.IndexOf("://", StringComparison.Ordinal) == uri.Scheme.Length && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : url;

    /// <summary><c>/favicon.ico</c> at the same origin, keeping the port, dropping path, query and fragment.</summary>
    public static Uri? FaviconUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "/favicon.ico");
        return builder.Uri;
    }

    /// <summary>On Windows, links open only for these schemes from the preview; other kinds open any scheme the user chose.</summary>
    public static bool IsWebOrMail(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
}
