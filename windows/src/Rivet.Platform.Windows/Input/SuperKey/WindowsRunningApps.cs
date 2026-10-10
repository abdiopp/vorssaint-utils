// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace Rivet.Platform.Windows.Input.SuperKey;

/// <summary>
/// "Pause while any of these apps runs" for the Super key: a Toolhelp process
/// snapshot every 2 s while watched. Only processes whose executable name
/// matches a listed entry get their full path resolved
/// (QueryFullProcessImageName), so a poll stays cheap.
/// </summary>
public sealed unsafe class WindowsRunningApps : IRunningAppsMonitor
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly List<Action> _watchers = [];
    private Timer? _timer;
    private HashSet<string> _lastNames = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAnyRunning(AppExclusionList apps)
    {
        if (apps.IsEmpty)
        {
            return false;
        }

        var wanted = new HashSet<string>(apps.Entries.Select(FileNameOf), StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, name) in Snapshot())
        {
            if (!wanted.Contains(name))
            {
                continue;
            }

            var path = ProcessPath(pid);
            if (apps.Matches(path ?? name))
            {
                return true;
            }
        }

        return false;
    }

    public IDisposable Watch(Action changed)
    {
        lock (_gate)
        {
            _watchers.Add(changed);
            _timer ??= new Timer(_ => Poll(), null, PollInterval, PollInterval);
        }

        return new Releaser(() =>
        {
            lock (_gate)
            {
                _watchers.Remove(changed);
                if (_watchers.Count == 0)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        });
    }

    /// <summary>Full image path of a process, or null (exited, or protected beyond limited query rights).</summary>
    public static string? ProcessPath(uint pid)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    private static string FileNameOf(string entry)
    {
        var normalized = entry.Replace('/', '\\');
        var slash = normalized.LastIndexOf('\\');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static List<(uint Pid, string Name)> Snapshot()
    {
        var result = new List<(uint, string)>(256);
        var snapshot = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsNull || snapshot.Value == (void*)-1)
        {
            return result;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (!PInvoke.Process32FirstW(snapshot, &entry))
            {
                return result;
            }

            do
            {
                result.Add((entry.th32ProcessID, entry.szExeFile.ToString()));
                entry.dwSize = (uint)sizeof(PROCESSENTRY32W);
            }
            while (PInvoke.Process32NextW(snapshot, &entry));
        }
        finally
        {
            PInvoke.CloseHandle(snapshot);
        }

        return result;
    }

    private void Poll()
    {
        try
        {
            var names = new HashSet<string>(Snapshot().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            Action[] watchers;
            lock (_gate)
            {
                if (names.SetEquals(_lastNames))
                {
                    return;
                }

                _lastNames = names;
                watchers = [.. _watchers];
            }

            foreach (var watcher in watchers)
            {
                watcher();
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Log.Warn("input", "Process poll failed.", ex);
        }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
