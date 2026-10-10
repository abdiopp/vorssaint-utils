// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Processes;

/// <summary>
/// Processes on Windows. One <c>NtQuerySystemInformation</c> call gives every
/// process with its parent, creation time, CPU times and working set without
/// opening handles; paths, users and the critical flag are read per process
/// with the least access that works. Every kill opens the process once and
/// checks its creation time on that same handle before acting.
/// </summary>
public sealed partial class WindowsProcessPlatform : IProcessPlatform
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const int ProcessBreakOnTermination = 29;
    private const int ProcessCommandLineInformation = 60;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInvalidParameter = 87;
    private const uint StillActive = 259;

    private int _bufferSize = 1 << 20;

    public int CurrentProcessId => Environment.ProcessId;

    public int LogicalProcessorCount => Environment.ProcessorCount;

    public unsafe IReadOnlyList<RawProcess> Snapshot()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new byte[_bufferSize];
            fixed (byte* start = buffer)
            {
                var status = unchecked((uint)NtQuerySystemInformation(SystemProcessInformation, start, (uint)buffer.Length, out var needed));
                if (status == StatusInfoLengthMismatch)
                {
                    _bufferSize = (int)Math.Min(64L << 20, Math.Max(needed, (uint)_bufferSize) + (256 * 1024));
                    continue;
                }

                if (status != 0)
                {
                    Log.Warn("processes", $"NtQuerySystemInformation failed: 0x{status:X8}.");
                    return [];
                }

                return Parse(start, buffer.Length);
            }
        }

        return [];
    }

    public string? ImagePath(ProcessIdentity identity)
    {
        using var handle = Open(identity.Pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (handle is null || !SameProcess(handle, identity))
        {
            return null;
        }

        return QueryPath(handle);
    }

    public ExecutableDescription? Describe(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new ExecutableDescription(Clean(info.FileDescription), Clean(info.CompanyName), Clean(info.ProductName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public unsafe string? UserName(ProcessIdentity identity)
    {
        using var handle = Open(identity.Pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (handle is null || !SameProcess(handle, identity) || !PInvoke.OpenProcessToken(handle, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return null;
        }

        using (token)
        {
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, out var length);
            if (length == 0 || length > 4096)
            {
                return null;
            }

            var buffer = stackalloc byte[(int)length];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, length, out _))
            {
                return null;
            }

            var sid = ((TOKEN_USER*)buffer)->User.Sid;
            uint nameLength = 256;
            uint domainLength = 256;
            var name = stackalloc char[256];
            var domain = stackalloc char[256];
            SID_NAME_USE use;
            if (!PInvoke.LookupAccountSid((PCWSTR)null, sid, new PWSTR(name), &nameLength, new PWSTR(domain), &domainLength, &use))
            {
                return null;
            }

            var user = new string(name, 0, (int)nameLength);
            var domainName = new string(domain, 0, (int)domainLength);
            return domainName.Length > 0 ? domainName + "\\" + user : user;
        }
    }

    public unsafe bool IsCritical(ProcessIdentity identity)
    {
        using var handle = Open(identity.Pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION);
        if (handle is not null)
        {
            if (!SameProcess(handle, identity))
            {
                return false;
            }

            uint flag = 0;
            var status = NtQueryInformationProcess(handle.DangerousGetHandle(), ProcessBreakOnTermination, &flag, sizeof(uint), out _);
            if (status == 0)
            {
                return flag != 0;
            }
        }

        // The flag cannot be read: a session 0 process from System32 might be critical
        // (ending one stops Windows), so it is treated as critical.
        using var limited = Open(identity.Pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        var path = limited is null ? null : QueryPath(limited);
        if (path is null)
        {
            return true;
        }

        var system32 = Environment.SystemDirectory;
        return path.StartsWith(system32 + "\\", StringComparison.OrdinalIgnoreCase) && SessionOf(identity.Pid) == 0;
    }

    public IReadOnlySet<int> ProcessesWithWindows()
    {
        var pids = new HashSet<int>();
        foreach (var (window, pid) in TopLevelWindows())
        {
            pids.Add(pid);
        }

        return pids;
    }

    public KillOutcome CloseWindows(ProcessIdentity identity)
    {
        using var handle = Open(identity.Pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE);
        if (handle is null)
        {
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter ? KillOutcome.AlreadyGone : KillOutcome.AccessDenied;
        }

        if (!SameProcess(handle, identity))
        {
            return KillOutcome.IdentityChanged;
        }

        var windows = TopLevelWindows().Where(w => w.Pid == identity.Pid).Select(w => w.Window).ToList();
        if (windows.Count == 0)
        {
            return KillOutcome.NoWindows;
        }

        var posted = false;
        foreach (var window in windows)
        {
            posted |= PInvoke.PostMessage(window, PInvoke.WM_CLOSE, default, default);
        }

        return posted ? KillOutcome.Done : KillOutcome.AccessDenied;
    }

    public KillOutcome Terminate(ProcessIdentity identity)
    {
        using var handle = Open(identity.Pid, PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (handle is null)
        {
            var error = (uint)Marshal.GetLastPInvokeError();
            return error == ErrorInvalidParameter ? KillOutcome.AlreadyGone : error == ErrorAccessDenied ? KillOutcome.AccessDenied : KillOutcome.Failed;
        }

        // Same handle for the identity check and the kill: no window for a PID swap.
        if (!SameProcess(handle, identity))
        {
            return KillOutcome.IdentityChanged;
        }

        if (PInvoke.TerminateProcess(handle, 1))
        {
            return KillOutcome.Done;
        }

        var failure = (uint)Marshal.GetLastPInvokeError();
        if (PInvoke.GetExitCodeProcess(handle, out var code) && code != StillActive)
        {
            return KillOutcome.AlreadyGone;
        }

        return failure == ErrorAccessDenied ? KillOutcome.AccessDenied : KillOutcome.Failed;
    }

    public async Task<bool> WaitForExitAsync(ProcessIdentity identity, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var handle = Open(identity.Pid, PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (handle is null)
        {
            return true;
        }

        using (handle)
        {
            if (!SameProcess(handle, identity))
            {
                return true;
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PInvoke.WaitForSingleObject(handle, 0) == WAIT_EVENT.WAIT_OBJECT_0)
                {
                    return true;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return PInvoke.WaitForSingleObject(handle, 0) == WAIT_EVENT.WAIT_OBJECT_0;
        }
    }

    public unsafe string? CommandLine(ProcessIdentity identity)
    {
        using var handle = Open(identity.Pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (handle is null || !SameProcess(handle, identity))
        {
            return null;
        }

        NtQueryInformationProcess(handle.DangerousGetHandle(), ProcessCommandLineInformation, null, 0, out var needed);
        if (needed == 0 || needed > 1 << 20)
        {
            return null;
        }

        var buffer = new byte[needed];
        fixed (byte* data = buffer)
        {
            if (NtQueryInformationProcess(handle.DangerousGetHandle(), ProcessCommandLineInformation, data, needed, out _) != 0)
            {
                return null;
            }

            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } followed by the text.
            var length = *(ushort*)data;
            var text = *(char**)(data + IntPtr.Size);
            return text == null || length == 0 ? null : new string(text, 0, length / 2);
        }
    }

    public bool Launch(string path, string? arguments, string? workingDirectory)
    {
        try
        {
            if (Environment.IsPrivilegedProcess)
            {
                // From an elevated app, Explorer starts the program as the signed-in user
                // (without elevation); it cannot pass arguments.
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
                return true;
            }

            var info = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = workingDirectory ?? string.Empty };
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                info.Arguments = arguments;
            }

            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn("processes", $"Could not start {path}.", ex);
            return false;
        }
    }

    private static unsafe List<RawProcess> Parse(byte* start, int length)
    {
        var list = new List<RawProcess>(512);
        var offset = 0;
        while (offset >= 0 && offset + 208 <= length)
        {
            var entry = start + offset;
            var nameLength = *(ushort*)(entry + 56);
            var namePointer = *(char**)(entry + 64);
            var pid = (int)*(nint*)(entry + 80);
            var name = namePointer != null && nameLength > 0
                ? new string(namePointer, 0, nameLength / 2)
                : pid switch { 0 => "System Idle Process", 4 => "System", _ => "?" };
            list.Add(new RawProcess
            {
                Pid = pid,
                ParentPid = (int)*(nint*)(entry + 88),
                ImageName = name,
                CreationTime = *(long*)(entry + 32),
                CpuTime = *(long*)(entry + 40) + *(long*)(entry + 48),
                SessionId = (int)*(uint*)(entry + 100),
                WorkingSetBytes = (long)*(nuint*)(entry + 144),
                PrivateBytes = (long)*(nuint*)(entry + 200),
            });

            var next = *(uint*)entry;
            if (next == 0)
            {
                break;
            }

            offset += (int)next;
        }

        return list;
    }

    private static SafeFileHandle? Open(int pid, PROCESS_ACCESS_RIGHTS rights)
    {
        if (pid <= 0)
        {
            return null;
        }

        var handle = PInvoke.OpenProcess_SafeHandle(rights, false, (uint)pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static bool SameProcess(SafeFileHandle handle, ProcessIdentity identity)
    {
        if (!PInvoke.GetProcessTimes(handle, out var created, out _, out _, out _))
        {
            return false;
        }

        var ticks = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return identity.CreationTime == 0 || ticks == identity.CreationTime;
    }

    private static string? QueryPath(SafeFileHandle handle)
    {
        Span<char> buffer = stackalloc char[1024];
        uint size = (uint)buffer.Length;
        return PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size) ? new string(buffer[..(int)size]) : null;
    }

    private int SessionOf(int pid) => Snapshot().FirstOrDefault(p => p.Pid == pid)?.SessionId ?? -1;

    private static List<(HWND Window, int Pid)> TopLevelWindows()
    {
        var windows = new List<(HWND, int)>();
        var gc = GCHandle.Alloc(windows);
        try
        {
            PInvoke.EnumWindows(EnumProc, (LPARAM)GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        return windows;
    }

    private static unsafe BOOL EnumProc(HWND window, LPARAM state)
    {
        var list = (List<(HWND, int)>)GCHandle.FromIntPtr(state.Value).Target!;
        if (!PInvoke.IsWindowVisible(window) || PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_OWNER) != HWND.Null || PInvoke.GetWindowTextLength(window) == 0)
        {
            return true;
        }

        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW))
        {
            return true;
        }

        // Cloaked windows (suspended Store apps, other virtual desktops' shells) are not real windows here.
        int cloaked = 0;
        if (PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int)).Succeeded && cloaked != 0)
        {
            return true;
        }

        PInvoke.GetWindowThreadProcessId(window, out var pid);
        if (pid != 0)
        {
            list.Add((window, (int)pid));
        }

        return true;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint systemInformationLength, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint processHandle, int processInformationClass, void* processInformation, uint processInformationLength, out uint returnLength);
}
