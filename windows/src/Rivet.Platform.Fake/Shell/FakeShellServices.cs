// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;

namespace Rivet.Platform.Fake.Shell;

public sealed class FakePlatformInfo : IPlatformInfo
{
    public bool IsWindows => false;

    public string OsDescription => $"Development host ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})";

    public Version OsVersion { get; } = new(10, 0, 22631);

    public bool IsElevated => false;

    public string Architecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
}

/// <summary>Records subscriptions; tests can push synthetic events through <see cref="RaiseKey"/>/<see cref="RaiseMouse"/>.</summary>
public sealed class FakeInputHooks : IInputHooks
{
    private readonly List<(int Priority, KeyboardHookHandler Handler)> _keyboard = [];
    private readonly List<(int Priority, MouseHookHandler Handler)> _mouse = [];
    private readonly HashSet<int> _down = [];

    public List<string> Sent { get; } = [];

    public int KeyboardSubscriberCount => _keyboard.Count;

    public int MouseSubscriberCount => _mouse.Count;

    public IDisposable SubscribeKeyboard(KeyboardHookHandler handler, int priority = 0)
    {
        _keyboard.Add((priority, handler));
        _keyboard.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return new Unsubscriber(() => _keyboard.RemoveAll(h => h.Handler == handler));
    }

    public IDisposable SubscribeMouse(MouseHookHandler handler, int priority = 0)
    {
        _mouse.Add((priority, handler));
        _mouse.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return new Unsubscriber(() => _mouse.RemoveAll(h => h.Handler == handler));
    }

    /// <summary>Delivers a key event to subscribers; returns true when one swallowed it.</summary>
    public bool RaiseKey(KeyboardHookEvent e)
    {
        if (e.Action == KeyAction.Down) _down.Add(e.VirtualKey); else _down.Remove(e.VirtualKey);
        foreach (var (_, handler) in _keyboard.ToArray())
        {
            if (handler(ref e)) return true;
        }

        return false;
    }

    public bool RaiseMouse(MouseHookEvent e)
    {
        foreach (var (_, handler) in _mouse.ToArray())
        {
            if (handler(ref e)) return true;
        }

        return false;
    }

    public void SendKeys(IReadOnlyList<(int VirtualKey, KeyAction Action)> strokes) =>
        Sent.Add("keys:" + string.Join(',', strokes.Select(s => $"{s.VirtualKey:X2}{(s.Action == KeyAction.Down ? "↓" : "↑")}")));

    public void SendText(string text) => Sent.Add("text:" + text);

    public void SendWheel(int delta, bool horizontal) => Sent.Add($"wheel:{(horizontal ? "h" : "v")}{delta}");

    public void SendMouse(MouseHookKind kind, int xButton = 0) => Sent.Add($"mouse:{kind}{xButton}");

    public bool IsKeyDown(int virtualKey) => _down.Contains(virtualKey);

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>Accepts every registration unless the chord was marked taken; tests can "press" registered chords.</summary>
public sealed class FakeHotkeyService : IHotkeyService
{
    private readonly Dictionary<KeyChord, Action> _registered = [];

    public HashSet<KeyChord> Taken { get; } = [];

    public IReadOnlyCollection<KeyChord> Registered => _registered.Keys;

    public IDisposable? Register(KeyChord chord, Action onPressed, HotkeyOptions options = HotkeyOptions.None)
    {
        if (Taken.Contains(chord) || _registered.ContainsKey(chord))
        {
            return null;
        }

        _registered[chord] = onPressed;
        return new Registration(() => _registered.Remove(chord));
    }

    public bool Press(KeyChord chord)
    {
        if (_registered.TryGetValue(chord, out var callback))
        {
            callback();
            return true;
        }

        return false;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class FakeKeyNames : IKeyNameProvider
{
    public string NameOf(int virtualKey) => VirtualKeys.DefaultName(virtualKey);
}

public sealed class FakeTrayIcon : ITrayIcon
{
    public bool IsVisible { get; private set; }

    public string Tooltip { get; private set; } = string.Empty;

    public IReadOnlyList<PixelBuffer> Icon { get; private set; } = [];

    public PixelRect? Bounds => new PixelRect(1800, 1040, 24, 24);

    public event EventHandler<TrayClickEventArgs>? Clicked;

    public event EventHandler? TaskbarThemeChanged;

    public void Show() => IsVisible = true;

    public void Hide() => IsVisible = false;

    public void SetIcon(IReadOnlyList<PixelBuffer> sizes) => Icon = sizes;

    public void SetTooltip(string text) => Tooltip = text;

    public void Click(TrayMouseButton button) => Clicked?.Invoke(this, new TrayClickEventArgs(button, new PixelPoint(1812, 1052)));

    public void RaiseThemeChanged() => TaskbarThemeChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
    }
}

public sealed class FakeTheme : IThemeService
{
    private bool _light = true;

    public bool AppsUseLightTheme
    {
        get => _light;
        set
        {
            _light = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool SystemUsesLightTheme { get; set; }

    public uint AccentColor { get; set; } = 0xFF0067C0;

    public bool TransparencyEnabled { get; set; } = true;

    public bool HighContrast { get; set; }

    public bool ReduceMotion { get; set; }

    public event EventHandler? Changed;
}

public sealed class FakeAutostart : IAutostartService
{
    public AutostartState State { get; private set; }

    public AutostartState SetEnabled(bool enabled) => State = enabled ? AutostartState.Enabled : AutostartState.Disabled;
}

public sealed class FakeNotifications : INotificationService
{
    public List<NotificationRequest> Shown { get; } = [];

    public bool IsEnabled => true;

    public event EventHandler<string>? Activated;

    public void Show(NotificationRequest request)
    {
        Shown.Add(request);
        Log.Info("notifications", $"[fake toast] {request.Title}: {request.Body}");
    }

    public void Activate(string actionId) => Activated?.Invoke(this, actionId);
}

/// <summary>Opens URLs and files with the host OS for convenience; everything else is logged.</summary>
public sealed class FakeShell : IShellService
{
    public List<string> Recycled { get; } = [];

    public void OpenUrl(string url) => Open(url);

    public void OpenFile(string path) => Open(path);

    public void RevealInExplorer(string path) => Open(Path.GetDirectoryName(Path.GetFullPath(path)) ?? path);

    public void OpenSystemSettings(string uri) => Log.Info("shell", $"[fake] open Windows Settings {uri}");

    public bool MoveToRecycleBin(IEnumerable<string> paths)
    {
        Recycled.AddRange(paths);
        return true;
    }

    public void OpenWith(string path) => Log.Info("shell", $"[fake] open with {path}");

    private static void Open(string target)
    {
        if (Environment.GetEnvironmentVariable("RIVET_FAKE_SHELL_OPEN") != "1")
        {
            Log.Info("shell", $"[fake] open {target}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", target) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("shell", $"Could not open {target}.", ex);
        }
    }
}

public sealed class FakeSingleInstance : ISingleInstanceService
{
    public event EventHandler<IReadOnlyList<string>>? ActivationRequested;

    public bool TryAcquire() => true;

    public bool SendToPrimary(IReadOnlyList<string> args) => false;

    public void Activate(IReadOnlyList<string> args) => ActivationRequested?.Invoke(this, args);

    public void Dispose()
    {
    }
}

/// <summary>One 1920×1080 monitor at 100 % unless a test supplies others.</summary>
public sealed class FakeScreens : IScreenService
{
    private IReadOnlyList<ScreenInfo> _screens =
    [
        new ScreenInfo
        {
            Id = "\\\\.\\DISPLAY1",
            FriendlyName = "Fake display",
            Bounds = new PixelRect(0, 0, 1920, 1080),
            WorkArea = new PixelRect(0, 0, 1920, 1032),
            Scale = 1.0,
            IsPrimary = true,
        },
    ];

    public IReadOnlyList<ScreenInfo> Screens
    {
        get => _screens;
        set
        {
            _screens = value;
            ScreensChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ScreenInfo Primary => Screens.FirstOrDefault(s => s.IsPrimary) ?? Screens[0];

    public PixelRect VirtualScreen => Screens.Aggregate(default(PixelRect), (acc, s) => acc.Union(s.Bounds));

    public PixelPoint CursorPosition { get; set; } = new(960, 540);

    public event EventHandler? ScreensChanged;

    public ScreenInfo ScreenFromPoint(PixelPoint point) => Screens.FirstOrDefault(s => s.Bounds.Contains(point)) ?? Primary;
}

public sealed class FakeWindowChrome : IWindowChrome
{
    public void Apply(nint hwnd, WindowChromeOptions options)
    {
    }

    public void Remove(nint hwnd, WindowChromeOptions options)
    {
    }

    public bool SetBackdrop(nint hwnd, WindowBackdrop backdrop, bool dark) => false;

    public void SetDarkTitleBar(nint hwnd, bool dark)
    {
    }

    public void SetRoundedCorners(nint hwnd, bool rounded)
    {
    }

    public nint GetForegroundWindow() => 0;

    public bool BringToFront(nint hwnd) => true;
}

public sealed class FakeFocusHandoff : IFocusHandoff
{
    public void Remember()
    {
    }

    public void Restore()
    {
    }
}

public sealed class FakeRelauncher : IRelauncher
{
    public void RelaunchAndExit(IReadOnlyList<string>? args = null)
    {
        Log.Info("relaunch", "[fake] relaunch requested; shutting down.");
        AppLifetime.RequestShutdown();
    }
}

public sealed class FakeClipboard : IClipboardService
{
    private string? _text;
    private PixelBuffer? _image;
    private IReadOnlyList<string> _files = [];

    public string? GetText() => _text;

    public void SetText(string text)
    {
        _text = text;
        _image = null;
        _files = [];
    }

    public PixelBuffer? GetImage() => _image;

    public void SetImage(PixelBuffer image)
    {
        _image = image;
        _text = null;
        _files = [];
    }

    public void SetFiles(IReadOnlyList<string> paths)
    {
        _files = paths;
        _text = null;
        _image = null;
    }

    public IReadOnlyList<string> GetFiles() => _files;
}
