// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rivet.Core.Clipboard;

namespace Rivet.Core.Launcher;

public enum CommandBarLinkKind
{
    Link,
    Place,
    Script,
}

/// <summary>A saved link, place, search or script (spec 06 §3.8.10, <c>commandBarLinks</c>).</summary>
public sealed record CommandBarLink
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public CommandBarLinkKind Kind { get; init; } = CommandBarLinkKind.Link;

    public string Destination { get; init; } = string.Empty;

    /// <summary>Scripts: "Also run when its name is typed on its own".</summary>
    public bool RunsWithoutArgument { get; init; }

    /// <summary>Scripts: "Run from its global shortcut without opening the bar".</summary>
    public bool RunsDirectly { get; init; }

    /// <summary>A destination with <c>{query}</c> is a search.</summary>
    public bool IsSearch => Kind != CommandBarLinkKind.Script && Destination.Contains("{query}", StringComparison.Ordinal);
}

/// <summary>Values for the placeholders of a saved link.</summary>
public sealed record LinkPlaceholderValues(string? Query, string? Clipboard, string? Selection, DateTimeOffset Today, CultureInfo Culture);

/// <summary>Name matching, placeholder expansion and the typed-URL detector (spec 06 §3.8.10, §6.15).</summary>
public static partial class CommandBarLinks
{
    public const int MaxLinks = 60;

    /// <summary>Saved entries with an empty name or destination are dropped; at most 60.</summary>
    public static List<CommandBarLink> Sanitize(List<CommandBarLink> links) =>
        links.Where(l => l is not null)
            .Select(l => l with { Name = (l.Name ?? string.Empty).Trim(), Destination = (l.Destination ?? string.Empty).Trim() })
            .Where(l => l.Name.Length > 0 && l.Destination.Length > 0)
            .Take(MaxLinks)
            .ToList();

    /// <summary>
    /// The argument typed after a saved name: the folded query must start with
    /// the folded name plus a space; the argument keeps the original spelling.
    /// "ghost" does not match "gh", and "gh" alone has no argument.
    /// </summary>
    public static string? TrailingArgument(string query, string name)
    {
        var foldedQuery = TextFold.ForCommand(query);
        var foldedName = TextFold.ForCommand(name);
        if (foldedName.Length == 0 || !foldedQuery.StartsWith(foldedName + " ", StringComparison.Ordinal))
        {
            return null;
        }

        var nameWords = foldedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var original = query.Trim();
        var position = 0;
        for (var word = 0; word < nameWords; word++)
        {
            while (position < original.Length && char.IsWhiteSpace(original[position]))
            {
                position++;
            }

            while (position < original.Length && !char.IsWhiteSpace(original[position]))
            {
                position++;
            }
        }

        var argument = original[position..].Trim();
        return argument.Length == 0 ? null : argument;
    }

    /// <summary>Whether the query is exactly the saved name.</summary>
    public static bool NamesExactly(string query, string name) =>
        TextFold.ForCommand(query) == TextFold.ForCommand(name);

    /// <summary>
    /// Fills <c>{query}</c>, <c>{clipboard}</c>, <c>{selection}</c> and <c>{date}</c>.
    /// Link values are percent-encoded leaving only A–Z a–z 0–9 - . _ ~; place values are inserted raw.
    /// </summary>
    public static string Expand(string destination, CommandBarLinkKind kind, LinkPlaceholderValues values)
    {
        string Value(string? raw) => kind == CommandBarLinkKind.Link ? PercentEncode(raw ?? string.Empty) : raw ?? string.Empty;
        return destination
            .Replace("{query}", Value(values.Query), StringComparison.Ordinal)
            .Replace("{clipboard}", Value(values.Clipboard), StringComparison.Ordinal)
            .Replace("{selection}", Value(values.Selection), StringComparison.Ordinal)
            .Replace("{date}", Value(values.Today.ToString(values.Culture.DateTimeFormat.ShortDatePattern, values.Culture)), StringComparison.Ordinal);
    }

    /// <summary>"café com leite" → "caf%C3%A9%20com%20leite"; "a+b" → "a%2Bb".</summary>
    public static string PercentEncode(string value)
    {
        var builder = new StringBuilder(value.Length * 3);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>A link destination opened as a URL: no scheme means https.</summary>
    public static string LinkTarget(string expanded) =>
        expanded.Contains("://", StringComparison.Ordinal) || expanded.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            ? expanded
            : "https://" + expanded;

    /// <summary>A place destination: <c>~</c> expands to the user profile.</summary>
    public static string PlaceTarget(string expanded, string home) =>
        expanded == "~" ? home : expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, expanded[2..])
            : expanded;

    /// <summary>The inverse of <see cref="PlaceTarget"/>: a path inside the profile is stored as <c>~\…</c> (spec 06 §3.8.11).</summary>
    public static string AbbreviateHome(string path, string home)
    {
        var trimmedHome = home.TrimEnd('\\', '/');
        if (trimmedHome.Length == 0)
        {
            return path;
        }

        if (string.Equals(path.TrimEnd('\\', '/'), trimmedHome, StringComparison.OrdinalIgnoreCase))
        {
            return "~";
        }

        return path.Length > trimmedHome.Length + 1
               && path.StartsWith(trimmedHome, StringComparison.OrdinalIgnoreCase)
               && path[trimmedHome.Length] is '\\' or '/'
            ? "~" + path[trimmedHome.Length] + path[(trimmedHome.Length + 1)..]
            : path;
    }

    // ── Typed URL ──────────────────────────────────────────────────────

    private static readonly HashSet<string> GenericTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "edu", "gov", "mil", "int", "info", "biz", "name", "pro", "aero", "coop", "museum",
        "io", "ai", "app", "dev", "co", "me", "tv", "fm", "ly", "gg", "to", "xyz", "online", "site", "tech", "store",
        "blog", "cloud", "page", "news", "shop", "live", "art", "design", "games", "media", "network", "social",
        "space", "studio", "systems", "today", "world", "zone", "academy", "agency", "codes", "email", "global",
        "group", "host", "link", "ninja", "rocks", "run", "software", "solutions", "website", "wiki", "work",
        "eu", "asia", "africa", "berlin", "london", "nyc", "paris", "tokyo",
    };

    /// <summary>Two-letter TLDs that are mostly file extensions are not accepted without a scheme.</summary>
    private static readonly HashSet<string> FileLikeTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "py", "sh", "rs", "ps", "pl", "js", "cs", "db", "gz", "so", "mo", "ts", "ml", "jl", "nu", "rb",
    };

    /// <summary>
    /// A web address typed alone ("example.com/docs", "https://x.y"): explicit
    /// http(s) kept, otherwise https:// added; e-mail addresses, numbers and
    /// plain file names are rejected. Null when not an address.
    /// </summary>
    public static string? TypedUrl(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > 2048 || trimmed.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var hasScheme = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!hasScheme && (trimmed.Contains("://", StringComparison.Ordinal) || trimmed.Contains('@')))
        {
            return null;
        }

        var candidate = hasScheme ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        if (hasScheme)
        {
            return trimmed;
        }

        var host = uri.Host;
        if (!HostShape().IsMatch(host))
        {
            return null;
        }

        var tld = host[(host.LastIndexOf('.') + 1)..];
        var known = GenericTlds.Contains(tld) || (tld.Length == 2 && tld.All(char.IsAsciiLetter) && !FileLikeTlds.Contains(tld));
        return known ? candidate : null;
    }

    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}$", RegexOptions.IgnoreCase)]
    private static partial Regex HostShape();
}
