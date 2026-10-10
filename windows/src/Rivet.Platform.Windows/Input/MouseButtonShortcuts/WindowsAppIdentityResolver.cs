// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Rivet.Core.Diagnostics;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Platform.Windows.Input.SuperKey;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Input.MouseButtonShortcuts;

/// <summary>
/// Which app owns the window under the pointer and the window in front, for
/// the input fixes' "apps to leave alone" (spec 07 §3.7.8). The hook thread
/// only reads immutable snapshots: pointer lookups run on a worker thread
/// (WindowFromPoint can send WM_NCHITTEST to a hung app, so never inside the
/// hook), keyed by the root window under the cursor and reused for 0.5 s while
/// the pointer stays inside that window. Foreground changes arrive through
/// an out-of-context WinEvent hook. Packaged apps resolve through
/// ApplicationFrameHost to the process of their CoreWindow.
/// </summary>
public sealed unsafe class WindowsAppIdentityResolver : IAppIdentityResolver, IDisposable
{
    private const uint MsgResolvePointer = PInvoke.WM_APP + 20;
    private const uint MsgTrackingChanged = PInvoke.WM_APP + 21;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private static readonly long FreshTicks = Stopwatch.Frequency / 2;
    private static readonly long PathTtlTicks = Stopwatch.Frequency * 10;

    private readonly ConcurrentDictionary<uint, (string? Path, long Stamp)> _paths = new();
    private readonly object _gate = new();
    private readonly WINEVENTPROC _winEventProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile PointerSnapshot? _pointer;
    private volatile ForegroundSnapshot? _foreground;
    private long _requestedPoint;
    private int _pointerPending;
    private int _tracking;
    private Thread? _thread;
    private uint _threadId;
    private HWINEVENTHOOK _hook;

    public WindowsAppIdentityResolver()
    {
        _winEventProc = OnWinEvent;
    }

    public nint ForegroundWindow => (nint)PInvoke.GetForegroundWindow().Value;

    public AppIdentityInfo? ForegroundApp
    {
        get
        {
            var snapshot = _foreground;
            var current = PInvoke.GetForegroundWindow();
            if (snapshot is not null && snapshot.Window == (nint)current.Value)
            {
                return snapshot.App;
            }

            PostToWorker(MsgTrackingChanged);
            return null;
        }
    }

    public AppIdentityInfo? PointerApp(PixelPoint point)
    {
        var snapshot = _pointer;
        if (snapshot is not null && snapshot.Bounds.Contains(point))
        {
            if (Stopwatch.GetTimestamp() - snapshot.Stamp > FreshTicks)
            {
                RequestPointer(point);
            }

            return snapshot.App;
        }

        RequestPointer(point);
        return null;
    }

    public (nint Window, AppIdentityInfo? App) ResolveForegroundNow()
    {
        var window = PInvoke.GetForegroundWindow();
        return ((nint)window.Value, window.IsNull ? null : AppForWindow(window));
    }

    public IDisposable Track()
    {
        lock (_gate)
        {
            _tracking++;
            EnsureThread();
        }

        PostToWorker(MsgTrackingChanged);
        return new Releaser(() =>
        {
            lock (_gate)
            {
                _tracking--;
            }

            PostToWorker(MsgTrackingChanged);
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tracking = 0;
        }

        if (_thread is not null)
        {
            PInvoke.PostThreadMessage(_threadId, MsgTrackingChanged, default, default);
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        }
    }

    /// <summary>The app owning a top-level window, resolving packaged apps behind ApplicationFrameHost.</summary>
    private AppIdentityInfo? AppForWindow(HWND window)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(window, &pid);
        var path = PathOf(pid);
        if (path is not null && path.EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var child = HWND.Null;
            while (!(child = PInvoke.FindWindowEx(window, child, "Windows.UI.Core.CoreWindow", null)).IsNull)
            {
                uint childPid;
                PInvoke.GetWindowThreadProcessId(child, &childPid);
                if (childPid != pid && PathOf(childPid) is { } inner)
                {
                    path = inner;
                    break;
                }
            }
        }

        return path is null ? null : new AppIdentityInfo(path);
    }

    private string? PathOf(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        var now = Stopwatch.GetTimestamp();
        if (_paths.TryGetValue(pid, out var cached) && now - cached.Stamp < PathTtlTicks)
        {
            return cached.Path;
        }

        var path = WindowsRunningApps.ProcessPath(pid);
        _paths[pid] = (path, now);
        if (_paths.Count > 512)
        {
            foreach (var key in _paths.Where(kv => now - kv.Value.Stamp >= PathTtlTicks).Select(kv => kv.Key).ToList())
            {
                _paths.TryRemove(key, out _);
            }
        }

        return path;
    }

    private void RequestPointer(PixelPoint point)
    {
        Interlocked.Exchange(ref _requestedPoint, ((long)point.X << 32) | (uint)point.Y);
        if (Interlocked.Exchange(ref _pointerPending, 1) == 0)
        {
            PostToWorker(MsgResolvePointer);
        }
    }

    private void PostToWorker(uint message)
    {
        if (_thread is not null)
        {
            PInvoke.PostThreadMessage(_threadId, message, default, default);
        }
    }

    private void EnsureThread()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "AppIdentity", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        PInvoke.PostThreadMessage(_threadId, PInvoke.WM_NULL, default, default);
        _ready.Set();
        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0) > 0)
        {
            if (msg.hwnd.IsNull)
            {
                try
                {
                    switch (msg.message)
                    {
                        case MsgResolvePointer:
                            ResolvePointer();
                            continue;
                        case MsgTrackingChanged:
                            UpdateHook();
                            continue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("input", "App identity lookup failed.", ex);
                    continue;
                }
            }

            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        if (!_hook.IsNull)
        {
            PInvoke.UnhookWinEvent(_hook);
            _hook = default;
        }
    }

    private void UpdateHook()
    {
        bool want;
        lock (_gate)
        {
            want = _tracking > 0;
        }

        if (want && _hook.IsNull)
        {
            _hook = PInvoke.SetWinEventHook(EventSystemForeground, EventSystemForeground, HMODULE.Null, _winEventProc, 0, 0, WinEventOutOfContext);
        }
        else if (!want && !_hook.IsNull)
        {
            PInvoke.UnhookWinEvent(_hook);
            _hook = default;
        }

        ResolveForeground(PInvoke.GetForegroundWindow());
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND window, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            ResolveForeground(window);
        }
        catch (Exception ex)
        {
            Log.Warn("input", "Foreground lookup failed.", ex);
        }
    }

    private void ResolveForeground(HWND window)
    {
        if (window.IsNull)
        {
            return;
        }

        _foreground = new ForegroundSnapshot((nint)window.Value, AppForWindow(window));
    }

    private void ResolvePointer()
    {
        Interlocked.Exchange(ref _pointerPending, 0);
        var packed = Interlocked.Read(ref _requestedPoint);
        var point = new System.Drawing.Point((int)(packed >> 32), (int)(uint)packed);
        var hit = PInvoke.WindowFromPoint(point);
        if (hit.IsNull)
        {
            return;
        }

        var root = PInvoke.GetAncestor(hit, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (root.IsNull)
        {
            root = hit;
        }

        if (!PInvoke.GetWindowRect(root, out var rect))
        {
            return;
        }

        var bounds = new PixelRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        _pointer = new PointerSnapshot(bounds, AppForWindow(root), Stopwatch.GetTimestamp());
    }

    private sealed record PointerSnapshot(PixelRect Bounds, AppIdentityInfo? App, long Stamp);

    private sealed record ForegroundSnapshot(nint Window, AppIdentityInfo? App);

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
