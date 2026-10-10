// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.App.Features.Input;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Fake.Input;
using Rivet.Platform.Fake.Shell;

namespace Rivet.App.Tests.Input;

/// <summary>Fresh fakes for one input-service test: settings, hooks, clock and platform stand-ins.</summary>
internal sealed class InputRig
{
    public SettingsStore Settings { get; } = SettingsStore.InMemory();

    public FakeInputHooks Hooks { get; } = new();

    public ManualInputClock Clock { get; } = new();

    public FakeAppIdentityResolver Resolver { get; } = new();

    public FakeWheelClassifier Classifier { get; } = new();

    public FakeKeyboardInfo Keyboard { get; } = new();

    public FakeRunningApps Running { get; } = new();

    public FakeGlideFrameTimer Timer { get; } = new();

    public InputFixesControl Control { get; } = new();

    public ScrollDirectionPolicy Direction { get; } = new();

    public static MouseHookEvent Mouse(MouseHookKind kind, int x = 500, int y = 500, int delta = 0, int xButton = 0, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { Kind = kind, Position = new PixelPoint(x, y), WheelDelta = delta, XButton = xButton, Modifiers = modifiers };

    public static KeyboardHookEvent Key(int vk, bool down, int scan = 0, KeyModifiers modifiers = KeyModifiers.None, bool extended = false) =>
        new() { VirtualKey = vk, ScanCode = scan, Action = down ? KeyAction.Down : KeyAction.Up, Modifiers = modifiers, IsExtended = extended };

    public bool Press(int vk, int scan = 0, KeyModifiers modifiers = KeyModifiers.None) => Hooks.RaiseKey(Key(vk, true, scan, modifiers));

    public bool Release(int vk, int scan = 0, KeyModifiers modifiers = KeyModifiers.None) => Hooks.RaiseKey(Key(vk, false, scan, modifiers));

    public bool Raise(MouseHookKind kind, int x = 500, int y = 500, int delta = 0, int xButton = 0, KeyModifiers modifiers = KeyModifiers.None) =>
        Hooks.RaiseMouse(Mouse(kind, x, y, delta, xButton, modifiers));

    public List<string> TakeSent()
    {
        var sent = Hooks.Sent.ToList();
        Hooks.Sent.Clear();
        return sent;
    }

    public ClickFilterService ClickFilter() => new(Settings, Hooks, Clock, Control);

    public KeyDebounceService KeyDebounce() => new(Settings, Hooks, Clock, Keyboard, Control);

    public MouseButtonShortcutsService MouseButtons() => new(Settings, Hooks, Clock, Resolver, Classifier, control: Control);

    public SmoothScrollService SmoothScroll() => new(Settings, Hooks, Clock, Resolver, Classifier, Timer, Direction);

    public ScrollInverterService Inverter() => new(Settings, Hooks, Clock, Resolver, Classifier, Direction);

    public SuperKeyService SuperKey() => new(Settings, Hooks, Clock, Keyboard, Running);

    public QuitProtectionService QuitProtection(IQuitProtectionHud hud) => new(Settings, Hooks, Clock, Resolver, hud);
}

internal sealed class RecordingHud : IQuitProtectionHud
{
    public List<QuitHudContent> Shown { get; } = [];

    public int Hides { get; private set; }

    public QuitHudContent? Current { get; private set; }

    public void Show(QuitHudContent content)
    {
        Shown.Add(content);
        Current = content;
    }

    public void Hide()
    {
        Hides++;
        Current = null;
    }
}
