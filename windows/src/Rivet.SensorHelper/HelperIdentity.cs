// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Principal;
using Rivet.Core.SystemMonitor;

namespace Rivet.SensorHelper;

internal static class ExitCodes
{
    public const int Done = 0;
    public const int Failed = 1;
    public const int BadArguments = 2;
    public const int NotElevated = 5;
    public const int CopyFailed = 10;
    public const int TaskFailed = 11;
}

/// <summary>Product identity (from Directory.Build.props) and the paths that derive from it.</summary>
internal static class HelperIdentity
{
    public static string ProductId { get; } = Metadata("ProductId") ?? "Rivet";

    public static string DisplayName { get; } = Metadata("ProductDisplayName") ?? ProductId;

    public static string ExecutableName => SensorHelperProtocol.ExecutableName(ProductId);

    public static string InstallFolder =>
        SensorHelperProtocol.InstallFolder(ProductId, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    /// <summary>This copy runs from the protected install folder (not from the app's folder).</summary>
    public static bool IsInstalledCopy =>
        string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), InstallFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The account the helper serves: a single user account, never a group
    /// such as Everyone, because the SID ends up in the pipe's and the task's
    /// access lists.
    /// </summary>
    public static bool TryParseUser(string text, [NotNullWhen(true)] out SecurityIdentifier? user)
    {
        user = null;
        try
        {
            var sid = new SecurityIdentifier(text);

            // Local and domain accounts, and Microsoft Entra accounts (S-1-12-1-…).
            if (sid.IsAccountSid() || sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            {
                user = sid;
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        return false;
    }

    private static string? Metadata(string key) =>
        typeof(HelperIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().LastOrDefault(a => a.Key == key)?.Value;
}

/// <summary>
/// A small log next to the installed helper. Program Files is writable by
/// administrators only, so a standard user cannot redirect what SYSTEM writes.
/// </summary>
internal static class HelperLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var folder = HelperIdentity.InstallFolder;
            if (!Directory.Exists(folder))
            {
                return;
            }

            var path = Path.Combine(folder, "helper.log");
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the helper down.
        }
    }
}
