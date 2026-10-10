// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Input;

/// <summary>
/// Extra click filter (spec 07 §3.7.1) on the shared mouse hook: left, right
/// and middle buttons only. Moves are never swallowed, so a swallowed bounce
/// just looks like pointer motion to apps; side buttons pass untouched.
/// </summary>
public sealed class ClickFilterService : InputFeatureService
{
    private long _windowNs = InputSettings.DefaultClickWindowMs * InputTime.NsPerMs;

    public ClickFilterService(ISettingsStore settings, IInputHooks hooks, IInputClock clock, InputFixesControl? control = null)
        : base(settings, hooks, clock, control, suspendable: true)
    {
        Observe(InputSettings.ClickDebounceWindowMs);
    }

    protected override bool WantsRunning() => Settings.Get(FeatureKeys.MouseClickDebounceEnabled);

    protected override void OnConfigure() =>
        Volatile.Write(ref _windowNs, Settings.Get(InputSettings.ClickDebounceWindowMs) * InputTime.NsPerMs);

    protected override IDisposable StartSession()
    {
        var filter = new ClickDebounceFilter(Volatile.Read(ref _windowNs));
        return Hooks.SubscribeMouse((ref MouseHookEvent e) => Handle(filter, ref e), InputPriorities.ClickFilter);
    }

    private bool Handle(ClickDebounceFilter filter, ref MouseHookEvent e)
    {
        ClickButton button;
        bool down;
        switch (e.Kind)
        {
            case MouseHookKind.LeftDown: button = ClickButton.Left; down = true; break;
            case MouseHookKind.LeftUp: button = ClickButton.Left; down = false; break;
            case MouseHookKind.RightDown: button = ClickButton.Right; down = true; break;
            case MouseHookKind.RightUp: button = ClickButton.Right; down = false; break;
            case MouseHookKind.MiddleDown: button = ClickButton.Middle; down = true; break;
            case MouseHookKind.MiddleUp: button = ClickButton.Middle; down = false; break;
            default: return false;
        }

        filter.WindowNs = Volatile.Read(ref _windowNs);
        var now = Clock.NowNs;
        return down ? filter.OnDown(button, now) : filter.OnUp(button, now);
    }
}
