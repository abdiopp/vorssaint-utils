// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Rivet.Platform.Windows.Clipboard;

/// <summary>Who a process is: its app identity (spec 06 §5.4), display name and integrity level.</summary>
internal sealed record ProcessIdentity(int ProcessId, string Identity, string Name, string Path, bool IsElevatedAboveUs)
{
    private static readonly Lazy<uint> OwnIntegrity = new(() => IntegrityOf(PInvoke.GetCurrentProcess()) ?? 0x2000);

    /// <summary>Lower-case full executable path (with backslashes) and a friendly name; null when the process is gone.</summary>
    public static unsafe ProcessIdentity? Of(int processId)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle.IsNull)
        {
            // Access denied is what protected and elevated processes answer: treat as above us.
            return new ProcessIdentity(processId, $"pid:{processId}", $"Process {processId}", string.Empty, IsElevatedAboveUs: true);
        }

        try
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            var path = PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(buffer), &size)
                ? new string(buffer, 0, (int)size)
                : string.Empty;
            var integrity = IntegrityOf(handle);
            var elevated = integrity is { } level && level > OwnIntegrity.Value;
            var identity = path.Length > 0 ? path.Replace('/', '\\').ToLowerInvariant() : $"pid:{processId}";
            return new ProcessIdentity(processId, identity, FriendlyName(path), path, elevated);
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    /// <summary>The file description of the executable, else its file name without extension.</summary>
    public static string FriendlyName(string path)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
        }

        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    private static unsafe uint? IntegrityOf(HANDLE process)
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
        {
            return null;
        }

        try
        {
            uint length;
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, null, 0, &length);
            if (length == 0)
            {
                return null;
            }

            var buffer = stackalloc byte[(int)length];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, length, &length))
            {
                return null;
            }

            var label = (TOKEN_MANDATORY_LABEL*)buffer;
            var sid = label->Label.Sid;
            var count = *PInvoke.GetSidSubAuthorityCount(sid);
            return count == 0 ? null : *PInvoke.GetSidSubAuthority(sid, (uint)(count - 1));
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }
}
