// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Tests.CleaningMode;

/// <summary>A minimal in-process IInputHooks for Core tests (priority-ordered, swallow-aware).</summary>
public sealed class TestInputHooks : IInputHooks
{
    private readonly List<(int Priority, KeyboardHookHandler Handler)> _keyboard = [];
    private readonly List<(int Priority, MouseHookHandler Handler)> _mouse = [];
    private readonly HashSet<int> _down = [];

    public List<string> Sent { get; } = [];

    public bool ThrowOnSubscribe { get; set; }

    public int KeyboardCount => _keyboard.Count;

    public int MouseCount => _mouse.Count;

    public IDisposable SubscribeKeyboard(KeyboardHookHandler handler, int priority = 0)
    {
        if (ThrowOnSubscribe)
        {
            throw new InvalidOperationException("hooks unavailable");
        }

        _keyboard.Add((priority, handler));
        _keyboard.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return new Token(() => _keyboard.RemoveAll(h => h.Handler == handler));
    }

    public IDisposable SubscribeMouse(MouseHookHandler handler, int priority = 0)
    {
        _mouse.Add((priority, handler));
        _mouse.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return new Token(() => _mouse.RemoveAll(h => h.Handler == handler));
    }

    /// <summary>Returns true when a subscriber swallowed the key.</summary>
    public bool Key(int vk, KeyAction action)
    {
        if (action == KeyAction.Down) _down.Add(vk); else _down.Remove(vk);
        var e = new KeyboardHookEvent { VirtualKey = vk, Action = action };
        foreach (var (_, handler) in _keyboard.ToArray())
        {
            if (handler(ref e)) return true;
        }

        return false;
    }

    public bool Press(int vk) => Key(vk, KeyAction.Down) | Key(vk, KeyAction.Up);

    public bool Mouse(MouseHookKind kind, int x = 0, int y = 0, int xButton = 0)
    {
        var e = new MouseHookEvent { Kind = kind, Position = new PixelPoint(x, y), XButton = xButton };
        foreach (var (_, handler) in _mouse.ToArray())
        {
            if (handler(ref e)) return true;
        }

        return false;
    }

    public void SendKeys(IReadOnlyList<(int VirtualKey, KeyAction Action)> strokes) =>
        Sent.Add(string.Join(',', strokes.Select(s => $"{s.VirtualKey:X2}{(s.Action == KeyAction.Down ? "+" : "-")}")));

    public void SendText(string text) => Sent.Add("text:" + text);

    public void SendWheel(int delta, bool horizontal) => Sent.Add($"wheel:{delta}");

    public void SendMouse(MouseHookKind kind, int xButton = 0) => Sent.Add($"mouse:{kind}");

    public bool IsKeyDown(int virtualKey) => _down.Contains(virtualKey);

    private sealed class Token(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
