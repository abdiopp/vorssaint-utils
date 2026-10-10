// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Rivet.Core.Clipboard;
using Rivet.Core.Diagnostics;
using Rivet.Core.Util;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Clipboard;

/// <summary>
/// Foreground bookkeeping for "act at the caret" flows and the snippet engine
/// (spec 06 §7.1): which app is in front (and whether it runs elevated, so
/// injected input would be dropped by UIPI), bringing it back, and focus
/// tracking with WinEvent hooks. Password fields are detected with the
/// classic ES_PASSWORD style and UI Automation's IsPassword, on a worker
/// thread with a time limit because UIA calls into other processes can hang.
/// </summary>
public sealed class WindowsForegroundService : IForegroundService, IDisposable
{
    private static readonly TimeSpan PasswordCheckLimit = TimeSpan.FromMilliseconds(500);

    private readonly WINEVENTPROC _eventProc;
    private readonly BlockingCollection<long> _checks = new(new ConcurrentQueue<long>());
    private readonly Thread _uiaThread;
    private HWINEVENTHOOK _foregroundHook;
    private HWINEVENTHOOK _focusHook;
    private int _trackers;
    private long _generation;
    private long _checkedGeneration;
    private long _checkStartedTicks;
    private volatile bool _isPassword;

    public WindowsForegroundService()
    {
        _eventProc = OnWinEvent;
        _uiaThread = new Thread(PasswordChecks) { IsBackground = true, Name = "FocusPasswordCheck" };
        _uiaThread.SetApartmentState(ApartmentState.MTA);
        _uiaThread.Start();
    }

    public event EventHandler? FocusChanged;

    public bool IsPasswordFieldFocused
    {
        get
        {
            if (_isPassword)
            {
                return true;
            }

            // While a check for the current focus is running, be careful for a short while.
            var pending = Interlocked.Read(ref _checkedGeneration) != Interlocked.Read(ref _generation);
            return pending && Environment.TickCount64 - Interlocked.Read(ref _checkStartedTicks) < PasswordCheckLimit.TotalMilliseconds;
        }
    }

    public unsafe ForegroundApp? Current()
    {
        var window = PInvoke.GetForegroundWindow();
        if (window.IsNull)
        {
            return null;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(window, &pid);
        if (pid == Environment.ProcessId)
        {
            return new ForegroundApp { Window = (nint)window.Value, ProcessId = (int)pid, IsSelf = true, Name = Rivet.Core.App.AppIdentity.DisplayName };
        }

        var identity = ProcessIdentity.Of((int)pid);
        return new ForegroundApp
        {
            Window = (nint)window.Value,
            ProcessId = (int)pid,
            Identity = identity?.Identity,
            Name = identity?.Name,
            IsElevated = identity?.IsElevatedAboveUs ?? false,
        };
    }

    public async Task<bool> ActivateAsync(ForegroundApp target, TimeSpan timeout)
    {
        var hwnd = ToHwnd(target.Window);
        if (!PInvoke.IsWindow(hwnd))
        {
            return false;
        }

        if (IsTargetInFront(target))
        {
            return true;
        }

        BringToFront(hwnd);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (IsTargetInFront(target))
            {
                return true;
            }

            await Task.Delay(15).ConfigureAwait(true);
        }

        return IsTargetInFront(target);
    }

    private static unsafe HWND ToHwnd(nint value) => new((void*)value);

    private static unsafe bool IsTargetInFront(ForegroundApp target)
    {
        var front = PInvoke.GetForegroundWindow();
        if ((nint)front.Value == target.Window)
        {
            return true;
        }

        // Some apps hand focus to another top-level window of theirs (dialogs, tool windows).
        uint pid;
        PInvoke.GetWindowThreadProcessId(front, &pid);
        return pid == target.ProcessId && pid != 0;
    }

    private static unsafe void BringToFront(HWND hwnd)
    {
        if (PInvoke.IsIconic(hwnd))
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        if (PInvoke.SetForegroundWindow(hwnd))
        {
            return;
        }

        var foreground = PInvoke.GetForegroundWindow();
        var foregroundThread = PInvoke.GetWindowThreadProcessId(foreground, null);
        var thisThread = PInvoke.GetCurrentThreadId();
        if (foregroundThread != thisThread && PInvoke.AttachThreadInput(thisThread, foregroundThread, true))
        {
            try
            {
                PInvoke.BringWindowToTop(hwnd);
                PInvoke.SetForegroundWindow(hwnd);
            }
            finally
            {
                PInvoke.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }

    public IDisposable TrackFocus()
    {
        if (Interlocked.Increment(ref _trackers) == 1)
        {
            // WinEvent hooks deliver to the installing thread's message loop: the UI thread.
            UiThread.Run(InstallHooks);
        }

        return new Release(() =>
        {
            if (Interlocked.Decrement(ref _trackers) == 0)
            {
                UiThread.Run(RemoveHooks);
            }
        });
    }

    private void InstallHooks()
    {
        if (!_foregroundHook.IsNull || Volatile.Read(ref _trackers) == 0)
        {
            return;
        }

        const uint flags = PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS;
        _foregroundHook = PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND, HMODULE.Null, _eventProc, 0, 0, flags);
        _focusHook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_FOCUS, PInvoke.EVENT_OBJECT_FOCUS, HMODULE.Null, _eventProc, 0, 0, flags);
        RequestPasswordCheck();
    }

    private void RemoveHooks()
    {
        if (Volatile.Read(ref _trackers) > 0)
        {
            return;
        }

        if (!_foregroundHook.IsNull)
        {
            PInvoke.UnhookWinEvent(_foregroundHook);
            _foregroundHook = default;
        }

        if (!_focusHook.IsNull)
        {
            PInvoke.UnhookWinEvent(_focusHook);
            _focusHook = default;
        }

        _isPassword = false;
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        RequestPasswordCheck();
        try
        {
            FocusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("focus", "A focus change handler failed.", ex);
        }
    }

    private void RequestPasswordCheck()
    {
        var generation = Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _checkStartedTicks, Environment.TickCount64);
        _isPassword = false;
        _checks.Add(generation);
    }

    /// <summary>Worker: the newest request wins; ES_PASSWORD first (cheap), then UIA IsPassword.</summary>
    private void PasswordChecks()
    {
        IUIAutomation? automation = null;
        try
        {
            PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
            automation = (IUIAutomation)new CUIAutomation();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Warn("focus", "UI Automation is not available; only classic password boxes are detected.", ex);
        }

        foreach (var generation in _checks.GetConsumingEnumerable())
        {
            if (generation != Interlocked.Read(ref _generation))
            {
                continue; // a newer focus change is queued
            }

            var isPassword = ClassicPasswordBoxFocused() || UiaPasswordFocused(automation);
            if (generation == Interlocked.Read(ref _generation))
            {
                _isPassword = isPassword;
                Interlocked.Exchange(ref _checkedGeneration, generation);
            }
        }
    }

    private static unsafe bool ClassicPasswordBoxFocused()
    {
        var foreground = PInvoke.GetForegroundWindow();
        var thread = PInvoke.GetWindowThreadProcessId(foreground, null);
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        if (thread == 0 || !PInvoke.GetGUIThreadInfo(thread, ref info) || info.hwndFocus.IsNull)
        {
            return false;
        }

        var name = stackalloc char[64];
        var length = PInvoke.GetClassName(info.hwndFocus, new PWSTR(name), 64);
        var className = new string(name, 0, Math.Max(0, length));
        if (!className.Contains("Edit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var style = (nint)PInvoke.GetWindowLongPtr(info.hwndFocus, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        return (style & PInvoke.ES_PASSWORD) != 0;
    }

    private static bool UiaPasswordFocused(IUIAutomation? automation)
    {
        if (automation is null)
        {
            return false;
        }

        try
        {
            var element = automation.GetFocusedElement();
            return element is not null && element.CurrentIsPassword;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Beep() => PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_OK);

    public unsafe IReadOnlyList<RunningApp> RunningApps()
    {
        var processes = new Dictionary<uint, string>();
        WNDENUMPROC callback = (hwnd, _) =>
        {
            if (IsAppWindow(hwnd))
            {
                uint pid;
                PInvoke.GetWindowThreadProcessId(hwnd, &pid);
                if (pid != 0 && pid != Environment.ProcessId)
                {
                    processes.TryAdd(pid, string.Empty);
                }
            }

            return true;
        };
        PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);
        return processes.Keys
            .Select(pid => ProcessIdentity.Of((int)pid))
            .OfType<ProcessIdentity>()
            .Where(p => p.Path.Length > 0)
            .GroupBy(p => p.Identity)
            .Select(g => new RunningApp(g.Key, g.First().Name, g.First().Path))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A visible, unowned, titled, not cloaked, non-tool top-level window.</summary>
    internal static unsafe bool IsAppWindow(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || !PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull)
        {
            return false;
        }

        var exStyle = (WINDOW_EX_STYLE)(nint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) || PInvoke.GetWindowTextLength(hwnd) == 0)
        {
            return false;
        }

        int cloaked = 0;
        return !PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int)).Succeeded || cloaked == 0;
    }

    public void Dispose()
    {
        _checks.CompleteAdding();
        UiThread.Run(() =>
        {
            Volatile.Write(ref _trackers, 0);
            RemoveHooks();
        });
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
