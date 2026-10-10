// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Input;

/// <summary>
/// Smooth scrolling (spec 07 §3.7.4): each notch of a mouse wheel becomes an
/// eased stream of smaller wheel deltas sent at the display's refresh rate.
/// Only whole notches (multiples of 120) from a mouse are glided: touchpad
/// and high-resolution input, Ctrl+wheel (zoom) and excluded apps pass
/// untouched, and the inverter may still flip what passes. Shift is not
/// swapped: frames are sent with Shift still held and apps scroll sideways
/// themselves.
/// </summary>
public sealed class SmoothScrollService : InputFeatureService
{
    private readonly IAppIdentityResolver _resolver;
    private readonly IWheelDeviceClassifier _classifier;
    private readonly IGlideFrameTimer _timer;
    private readonly ScrollDirectionPolicy _direction;
    private readonly SmoothScrollEngine _engine = new();
    private readonly Func<double, bool> _onFrame;
    private volatile Config _config = new(InputSettings.DefaultSmoothStep, AppExclusionList.Empty);

    public SmoothScrollService(
        ISettingsStore settings,
        IInputHooks hooks,
        IInputClock clock,
        IAppIdentityResolver resolver,
        IWheelDeviceClassifier classifier,
        IGlideFrameTimer timer,
        ScrollDirectionPolicy direction)
        : base(settings, hooks, clock)
    {
        _resolver = resolver;
        _classifier = classifier;
        _timer = timer;
        _direction = direction;
        _onFrame = OnFrame;
        Observe(InputSettings.SmoothScrollStep, InputSettings.SmoothScrollResponse, InputSettings.SmoothScrollCoast, InputSettings.SmoothScrollExceptions);
    }

    protected override bool WantsRunning() => Settings.Get(FeatureKeys.SmoothScrollEnabled);

    protected override void OnConfigure()
    {
        _engine.Response = Settings.Get(InputSettings.SmoothScrollResponse);
        _engine.Coast = Settings.Get(InputSettings.SmoothScrollCoast);
        _config = new Config(Settings.Get(InputSettings.SmoothScrollStep), new AppExclusionList(Settings.Get(InputSettings.SmoothScrollExceptions)));
    }

    protected override IDisposable StartSession()
    {
        _engine.Reset();
        var subscription = Hooks.SubscribeMouse(Handle, InputPriorities.SmoothScroll);
        return new CompositeDisposable(_resolver.Track(), _classifier.Track(), subscription);
    }

    protected override void StopSession(IDisposable session)
    {
        session.Dispose();
        _timer.Stop();
        _engine.Reset();
    }

    private bool Handle(ref MouseHookEvent e)
    {
        if (e.Kind == MouseHookKind.Move)
        {
            if (!_config.Exceptions.IsEmpty || !_direction.Current.Exceptions.IsEmpty)
            {
                _resolver.PointerApp(e.Position);
            }

            return false;
        }

        if (e.Kind is not (MouseHookKind.Wheel or MouseHookKind.HorizontalWheel) || e.WheelDelta == 0)
        {
            return false;
        }

        if (e.Modifiers.HasFlag(KeyModifiers.Control))
        {
            // Native zoom: pass raw (the inverter may still flip it).
            return false;
        }

        if (_classifier.Classify(e.WheelDelta, Clock.NowNs) != WheelSource.MouseNotched)
        {
            return false;
        }

        var config = _config;
        var direction = _direction.Current;
        var horizontal = e.Kind == MouseHookKind.HorizontalWheel;
        var shift = e.Modifiers.HasFlag(KeyModifiers.Shift);
        var invert = direction.ShouldInvert(horizontal, shift);
        if (!config.Exceptions.IsEmpty || (invert && !direction.Exceptions.IsEmpty))
        {
            var app = _resolver.PointerApp(e.Position);
            if (app is null)
            {
                // Not identified yet: leave the notch alone.
                return false;
            }

            if (config.Exceptions.Matches(app.Path))
            {
                return false;
            }

            if (invert && direction.Exceptions.Matches(app.Path))
            {
                invert = false;
            }
        }

        var pixels = e.WheelDelta / (double)NotchWheelClassifier.WheelDelta * config.Step;
        _engine.Add(invert ? -pixels : pixels, horizontal, shift);
        _timer.Start(_onFrame);
        return true;
    }

    private bool OnFrame(double elapsedSeconds)
    {
        var (vertical, horizontal, active) = _engine.Frame(elapsedSeconds);
        if (vertical != 0)
        {
            Hooks.SendWheel(vertical, horizontal: false);
        }

        if (horizontal != 0)
        {
            Hooks.SendWheel(horizontal, horizontal: true);
        }

        return active;
    }

    private sealed record Config(int Step, AppExclusionList Exceptions);
}

/// <summary>
/// Scroll direction inverter (spec 07 §3.7.5): flips mouse wheels only. The
/// event is swallowed and re-sent with the opposite delta. Touchpads keep the
/// direction set in Windows Settings: fractional deltas from an unknown device
/// are left alone, and with Raw Input tracking a touchpad is recognized by
/// its contacts. Notches glided by smooth scrolling are inverted there, so
/// this only sees what smooth scrolling passed.
/// </summary>
public sealed class ScrollInverterService : InputFeatureService
{
    private readonly IAppIdentityResolver _resolver;
    private readonly IWheelDeviceClassifier _classifier;
    private readonly ScrollDirectionPolicy _direction;

    public ScrollInverterService(
        ISettingsStore settings,
        IInputHooks hooks,
        IInputClock clock,
        IAppIdentityResolver resolver,
        IWheelDeviceClassifier classifier,
        ScrollDirectionPolicy direction)
        : base(settings, hooks, clock)
    {
        _resolver = resolver;
        _classifier = classifier;
        _direction = direction;
        Observe(InputSettings.ScrollInverterHorizontal, InputSettings.ScrollInverterExceptions);
    }

    protected override bool WantsRunning() =>
        ScrollInverterSettings.Vertical(Settings) || ScrollInverterSettings.Horizontal(Settings);

    protected override void OnConfigure()
    {
        var vertical = ScrollInverterSettings.Vertical(Settings);
        var horizontal = ScrollInverterSettings.Horizontal(Settings);
        _direction.Update(new ScrollDirectionPolicy.Snapshot(
            IsAvailable && (vertical || horizontal),
            vertical,
            horizontal,
            new AppExclusionList(Settings.Get(InputSettings.ScrollInverterExceptions))));
    }

    protected override IDisposable StartSession()
    {
        var subscription = Hooks.SubscribeMouse(Handle, InputPriorities.ScrollInverter);
        return new CompositeDisposable(_resolver.Track(), _classifier.Track(), subscription);
    }

    private bool Handle(ref MouseHookEvent e)
    {
        if (e.Kind is not (MouseHookKind.Wheel or MouseHookKind.HorizontalWheel) || e.WheelDelta == 0)
        {
            return false;
        }

        var direction = _direction.Current;
        var horizontal = e.Kind == MouseHookKind.HorizontalWheel;
        if (!direction.ShouldInvert(horizontal, e.Modifiers.HasFlag(KeyModifiers.Shift)))
        {
            return false;
        }

        if (_classifier.Classify(e.WheelDelta, Clock.NowNs) is WheelSource.Touchpad or WheelSource.Unknown)
        {
            return false;
        }

        if (!direction.Exceptions.IsEmpty)
        {
            var app = _resolver.PointerApp(e.Position);
            if (app is null || direction.Exceptions.Matches(app.Path))
            {
                return false;
            }
        }

        Hooks.SendWheel(-e.WheelDelta, horizontal);
        return true;
    }
}
