// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.SystemMonitor;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>
/// Per-process snapshots from one NtQuerySystemInformation call (no handle
/// per process). Friendly names come from the executable's file description
/// (cached by path); "has a window" from the visible top-level windows.
/// </summary>
public sealed unsafe class WindowsProcessSampler : IProcessSampler
{
    private readonly ConcurrentDictionary<int, (long Create, string? Path)> _paths = new();
    private readonly ConcurrentDictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _windowsGate = new();
    private HashSet<int> _windowPids = [];
    private DateTime _windowsAt = DateTime.MinValue;

    public IReadOnlyList<ProcessSample>? Snapshot() => NtQuery.Processes();

    public AppDescription Describe(ProcessSample process)
    {
        var path = PathOf(process);
        string name;
        if (path is null)
        {
            name = ProcessUsageService.DisplayFallback(process);
        }
        else
        {
            name = _descriptions.GetOrAdd(path, static p =>
            {
                try
                {
                    var description = FileVersionInfo.GetVersionInfo(p).FileDescription?.Trim();
                    return string.IsNullOrEmpty(description) ? Path.GetFileNameWithoutExtension(p) : description;
                }
                catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
                {
                    return Path.GetFileNameWithoutExtension(p);
                }
            });
        }

        return new AppDescription(name, path, WindowPids().Contains(process.Pid));
    }

    private string? PathOf(ProcessSample process)
    {
        if (_paths.TryGetValue(process.Pid, out var cached) && cached.Create == process.CreateTime)
        {
            return cached.Path;
        }

        string? path = null;
        using (var handle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)process.Pid))
        {
            if (!handle.IsInvalid)
            {
                var buffer = new char[1024];
                var size = (uint)buffer.Length;
                if (PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer.AsSpan(), ref size))
                {
                    path = new string(buffer, 0, (int)size);
                }
            }
        }

        if (_paths.Count > 2048)
        {
            _paths.Clear();
        }

        _paths[process.Pid] = (process.CreateTime, path);
        return path;
    }

    /// <summary>Processes owning a visible, unowned top-level window (refreshed every 2 s).</summary>
    internal HashSet<int> WindowPids()
    {
        lock (_windowsGate)
        {
            if (DateTime.UtcNow - _windowsAt < TimeSpan.FromSeconds(2))
            {
                return _windowPids;
            }

            var pids = new HashSet<int>();
            PInvoke.EnumWindows((hwnd, _) =>
            {
                if (PInvoke.IsWindowVisible(hwnd) && PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull && PInvoke.GetWindowTextLength(hwnd) > 0)
                {
                    uint pid;
                    PInvoke.GetWindowThreadProcessId(hwnd, &pid);
                    pids.Add((int)pid);
                }

                return true;
            }, default);
            _windowPids = pids;
            _windowsAt = DateTime.UtcNow;
            return pids;
        }
    }
}

/// <summary>Bring an app to the front, force-terminate a process (with a start-time check), open Task Manager.</summary>
public sealed unsafe class WindowsProcessControl(IWindowChrome chrome) : IProcessControl
{
    public bool Activate(int pid)
    {
        var target = HWND.Null;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            uint owner;
            PInvoke.GetWindowThreadProcessId(hwnd, &owner);
            if (owner == pid && PInvoke.IsWindowVisible(hwnd) && PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull && PInvoke.GetWindowTextLength(hwnd) > 0)
            {
                target = hwnd;
                return false;
            }

            return true;
        }, default);
        return !target.IsNull && chrome.BringToFront((nint)target.Value);
    }

    public KillResult Kill(int pid, long createTime)
    {
        if (ProcessSafety.IsProtected(pid, null))
        {
            return KillResult.Protected;
        }

        using var handle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid)
        {
            var error = (WIN32_ERROR)System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            return error == WIN32_ERROR.ERROR_ACCESS_DENIED ? KillResult.AccessDenied : KillResult.NotFound;
        }

        // PID reuse guard: the start time must still match what the row showed.
        if (!PInvoke.GetProcessTimes(handle, out var created, out _, out _, out _)
            || (((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime) != createTime)
        {
            return KillResult.IdentityMismatch;
        }

        if (PInvoke.TerminateProcess(handle, 1))
        {
            Log.Info("monitor", $"Force-killed pid {pid}.");
            return KillResult.Killed;
        }

        var lastError = (WIN32_ERROR)System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return lastError == WIN32_ERROR.ERROR_ACCESS_DENIED ? KillResult.AccessDenied : KillResult.NotFound;
    }

    public void OpenTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("monitor", "Could not open Task Manager.", ex);
        }
    }
}
