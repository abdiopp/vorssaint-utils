// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.App;

/// <summary>Lets services ask the app to shut down gracefully (restores system state on the way out).</summary>
public static class AppLifetime
{
    private static Action? _shutdown;

    public static void Configure(Action shutdown) => _shutdown = shutdown;

    public static void RequestShutdown()
    {
        if (_shutdown is { } shutdown)
        {
            shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }

    /// <summary>The URI scheme toasts and deep links use: <c>&lt;id&gt;://action/&lt;actionId&gt;</c>.</summary>
    public static string UriScheme => AppIdentity.Id.ToLowerInvariant();

    public static string ActionUri(string actionId) => $"{UriScheme}://action/{Uri.EscapeDataString(actionId)}";

    /// <summary>Extracts the action id from a deep link, or null.</summary>
    public static string? ParseActionUri(string argument)
    {
        if (!Uri.TryCreate(argument, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, UriScheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "action", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        return id.Length == 0 ? null : id;
    }
}
