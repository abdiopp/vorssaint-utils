// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Modules.CleaningMode;
using Rivet.Core.Util;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.CleaningMode;

/// <summary>
/// Session notifications (WTSRegisterSessionNotification on the shared host
/// window), foreground changes (an out-of-context EVENT_SYSTEM_FOREGROUND
/// WinEvent hook on the UI thread), GetLastInputInfo for the hook liveness
/// probe, and process paths for the Task Manager exception.
/// </summary>
public sealed unsafe class WindowsCleaningPlatform : ICleaningPlatform, IDisposable
{
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private readonly NativeWindowHost _host;
    private readonly List<Action<SessionChange>> _sessionHandlers = [];
    private readonly WindowMessageHandler _messageHandler;
    private bool _sessionRegistered;

    public WindowsCleaningPlatform(NativeWindowHost host)
    {
        _host = host;
        _messageHandler = OnMessage;
    }

    public IDisposable WatchSession(Action<SessionChange> changed)
    {
        lock (_sessionHandlers)
        {
            _sessionHandlers.Add(changed);
        }

        if (!_sessionRegistered)
        {
            _sessionRegistered = true;
            _host.AddHandler(_messageHandler);
            // NOTIFY_FOR_THIS_SESSION. Never unregistered: other features may share the host window.
            if (!PInvoke.WTSRegisterSessionNotification(new HWND((void*)_host.Handle), 0))
            {
                Log.Warn("cleaning", $"WTSRegisterSessionNotification failed: {Marshal.GetLastWin32Error()}");
            }
        }

        return new Token(() =>
        {
            lock (_sessionHandlers)
            {
                _sessionHandlers.Remove(changed);
            }
        });
    }

    public IDisposable WatchForeground(Action<nint> changed)
    {
        // The delegate must stay alive as long as the hook exists.
        WINEVENTPROC callback = (_, eventType, hwnd, _, _, _, _) =>
        {
            if (eventType == EventSystemForeground)
            {
                changed((nint)hwnd.Value);
            }
        };
        var hook = PInvoke.SetWinEventHook(EventSystemForeground, EventSystemForeground, HMODULE.Null, callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
        if (hook.IsNull)
        {
            Log.Warn("cleaning", "SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed.");
            return new Token(() => GC.KeepAlive(callback));
        }

        return new Token(() =>
        {
            PInvoke.UnhookWinEvent(hook);
            GC.KeepAlive(callback);
        });
    }

    public string? ProcessPathOf(nint hwnd) => Win32Windows.ProcessPathOf(hwnd);

    public int? LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
        return PInvoke.GetLastInputInfo(ref info) ? unchecked((int)info.dwTime) : null;
    }

    public void Beep() => PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_OK);

    public void Dispose() => _host.RemoveHandler(_messageHandler);

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message != WmWtsSessionChange)
        {
            return null;
        }

        SessionChange? change = (uint)wParam switch
        {
            2 or 4 => SessionChange.Disconnected, // WTS_CONSOLE_DISCONNECT, WTS_REMOTE_DISCONNECT
            1 or 3 => SessionChange.Reconnected,  // WTS_CONSOLE_CONNECT, WTS_REMOTE_CONNECT
            7 => SessionChange.Locked,            // WTS_SESSION_LOCK
            8 => SessionChange.Unlocked,          // WTS_SESSION_UNLOCK
            _ => null,
        };
        if (change is { } value)
        {
            Action<SessionChange>[] handlers;
            lock (_sessionHandlers)
            {
                handlers = _sessionHandlers.ToArray();
            }

            UiThread.Post(() =>
            {
                foreach (var handler in handlers)
                {
                    handler(value);
                }
            });
        }

        return null;
    }

    private sealed class Token(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>Small window/process helpers shared by the everyday-tools modules.</summary>
internal static unsafe class Win32Windows
{
    public static string? ProcessPathOf(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(new HWND((void*)hwnd), &pid);
        return ProcessPath(pid);
    }

    public static string? ProcessPath(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(buffer), &size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}

public sealed class CleaningModeWindowsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<WindowsCleaningPlatform>();
        services.AddSingleton<ICleaningPlatform>(sp => sp.GetRequiredService<WindowsCleaningPlatform>());
    }
}
