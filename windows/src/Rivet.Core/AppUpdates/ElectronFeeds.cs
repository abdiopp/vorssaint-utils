// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;

namespace Rivet.Core.Maintenance.AppUpdates;

/// <summary>
/// Developer feeds of Electron apps (electron-builder/electron-updater):
/// <c>&lt;InstallLocation&gt;\resources\app-update.yml</c> names the feed whose
/// <c>latest.yml</c> carries the newest version. Ported from the macOS rules
/// (spec §3.3.6) with the Windows manifest name. Only public https feeds that
/// need no token are ever contacted.
/// </summary>
public static partial class ElectronFeeds
{
    /// <summary>app-update.yml files larger than this are ignored.</summary>
    public const int MaxConfigBytes = 64 * 1024;

    /// <summary>
    /// Flat <c>key: value</c> lines only: quoted values are decoded, anchors,
    /// tags, flow values and nested blocks are ignored, and a duplicate key
    /// rejects the whole file (null).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ParseFlatYaml(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.TrimStart().StartsWith('#') || char.IsWhiteSpace(line[0]) || line.StartsWith('-'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            if (!KeyPattern().IsMatch(key))
            {
                continue;
            }

            var raw = line[(colon + 1)..].Trim();
            if (values.ContainsKey(key))
            {
                return null;
            }

            if (raw.Length == 0 || raw[0] is '&' or '*' or '!' or '[' or '{' or '|' or '>')
            {
                // Nested blocks, anchors, tags and flow values carry nothing this needs.
                values[key] = string.Empty;
                continue;
            }

            values[key] = Unquote(StripComment(raw));
        }

        return values;
    }

    /// <summary>
    /// Public https URL: no user, password or fragment; a host with a dot and
    /// a letter, no port or IPv6 literal, not .local/.localhost, labels of
    /// letters, digits and dashes.
    /// </summary>
    public static bool IsPublicUrl(string? value)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort || uri.HostNameType is UriHostNameType.IPv6)
        {
            return false;
        }

        var host = uri.IdnHost;
        if (!host.Contains('.') || !host.Any(char.IsAsciiLetter) || host.Contains(':'))
        {
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return host.Split('.').All(label => label.Length > 0 && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    /// <summary>
    /// The latest.yml URL an app-update.yml points at, or null when the feed
    /// is private, needs a token or headers, is not on the "latest" channel,
    /// or uses a provider this does not support.
    /// </summary>
    public static string? ManifestUrl(IReadOnlyDictionary<string, string> config)
    {
        string? Get(string key) => config.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

        if (Get("channel") is { } channel && channel != "latest")
        {
            return null;
        }

        if (string.Equals(Get("private"), "true", StringComparison.OrdinalIgnoreCase) || config.ContainsKey("token") || config.ContainsKey("requestHeaders"))
        {
            return null;
        }

        switch (Get("provider"))
        {
            case "generic":
                var url = Get("url");
                if (!IsPublicUrl(url) || new Uri(url!).Query.Length > 0)
                {
                    return null;
                }

                return url!.TrimEnd('/') + "/latest.yml";
            case "github":
                var host = Get("host");
                var owner = Get("owner");
                var repo = Get("repo");
                if ((host is not null && host != "github.com") || !IsRepoPart(owner) || !IsRepoPart(repo))
                {
                    return null;
                }

                return $"https://github.com/{owner}/{repo}/releases/latest/download/latest.yml";
            default:
                return null;
        }
    }

    /// <summary>latest.yml: needs <c>version</c> and <c>files</c> or <c>path</c>; returns the version.</summary>
    public static string? ManifestVersion(string text)
    {
        var values = ParseFlatYaml(text);
        if (values is null || !values.TryGetValue("version", out var version) || version.Length == 0)
        {
            return null;
        }

        return values.ContainsKey("files") || (values.TryGetValue("path", out var path) && path.Length > 0) ? version : null;
    }

    /// <summary>Stable versions only: start with a digit, digits and dots only.</summary>
    public static bool IsStable(string? version) =>
        version is { Length: > 0 } && char.IsAsciiDigit(version[0]) && version.All(c => char.IsAsciiDigit(c) || c == '.');

    /// <summary>A release is offered when both versions are stable and it is newer.</summary>
    public static bool IsEligible(string? latest, string? installed) =>
        IsStable(latest) && IsStable(installed) && VersionComparer.IsNewer(latest, installed);

    private static bool IsRepoPart(string? value) =>
        value is { Length: > 0 } && value != "." && value != ".." && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string StripComment(string value)
    {
        if (value.StartsWith('"') || value.StartsWith('\''))
        {
            return value;
        }

        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? value[..hash].TrimEnd() : value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            var builder = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length)
                {
                    i++;
                    builder.Append(inner[i] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        var other => other,
                    });
                }
                else
                {
                    builder.Append(inner[i]);
                }
            }

            return builder.ToString();
        }

        return value;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex KeyPattern();
}
