// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Input;

/// <summary>
/// Hook priorities of the input fixes (higher runs first). They reproduce the
/// spec's pipeline order: debounce → click filter → Super key → (shortcut
/// recorder, 1000) → quit protection → button shortcuts / side wheel / drag →
/// smooth scroll → inverter. The Super key runs before the recorder so a held
/// Super key records as its full modifier set. The radial menu must sit above
/// button shortcuts (it always wins).
/// </summary>
public static class InputPriorities
{
    public const int KeyDebounce = 3000;
    public const int ClickFilter = 3000;
    public const int SuperKey = 2000;
    public const int MouseButtonShortcuts = 1500;
    public const int SmoothScroll = 1400;
    public const int ScrollInverter = 1300;
    public const int QuitProtection = 900;
}

/// <summary>
/// Common lifecycle of an input fix: the hooks are subscribed only while the
/// feature is installed, switched on and not suspended, and every hook
/// session gets fresh state. <see cref="Sync"/> is idempotent and may run on
/// any thread; changes of the feature's own extra settings call <see cref="Refresh"/>.
/// </summary>
public abstract class InputFeatureService : IFeatureController, IDisposable
{
    private readonly object _lifecycle = new();
    private readonly List<IDisposable> _observers = [];
    private IDisposable? _session;
    private bool _available;
    private bool _disposed;

    protected InputFeatureService(ISettingsStore settings, IInputHooks hooks, IInputClock clock, InputFixesControl? control = null, bool suspendable = false)
    {
        Settings = settings;
        Hooks = hooks;
        Clock = clock;
        Control = control;
        if (suspendable && control is not null)
        {
            control.SuspensionChanged += OnSuspensionChanged;
        }

        Suspendable = suspendable;
    }

    /// <summary>Raised when <see cref="IsRunning"/> or a status shown in Settings changes.</summary>
    public event EventHandler? StatusChanged;

    protected ISettingsStore Settings { get; }

    protected IInputHooks Hooks { get; }

    protected IInputClock Clock { get; }

    protected InputFixesControl? Control { get; }

    private bool Suspendable { get; }

    /// <summary>The feature is installed (last <see cref="Sync"/>).</summary>
    public bool IsAvailable => Volatile.Read(ref _available);

    /// <summary>The hooks are subscribed right now.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lifecycle)
            {
                return _session is not null;
            }
        }
    }

    public void Sync(bool available)
    {
        Volatile.Write(ref _available, available);
        Refresh();
    }

    /// <summary>Re-evaluates whether the hooks should run and rebuilds the configuration snapshot.</summary>
    public void Refresh()
    {
        bool changed;
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            OnConfigure();
            var want = _available && WantsRunning() && !(Suspendable && Control?.IsSuspended == true);
            changed = (want ? 1 : 0) != (_session is null ? 0 : 1);
            if (want && _session is null)
            {
                try
                {
                    _session = StartSession();
                }
                catch (Exception ex)
                {
                    Log.Error("input", $"{GetType().Name} could not start.", ex);
                    _session = null;
                }
            }
            else if (!want && _session is not null)
            {
                var session = _session;
                _session = null;
                StopSession(session);
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    public virtual void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_session is not null)
            {
                var session = _session;
                _session = null;
                StopSession(session);
            }
        }

        foreach (var observer in _observers)
        {
            observer.Dispose();
        }

        _observers.Clear();
        if (Control is not null)
        {
            Control.SuspensionChanged -= OnSuspensionChanged;
        }
    }

    protected void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Watches extra settings: any change rebuilds the snapshot and re-evaluates.</summary>
    protected void Observe(params SettingDefinition[] settings) =>
        _observers.Add(Settings.Observe(Refresh, settings));

    /// <summary>The feature's own switches say "on".</summary>
    protected abstract bool WantsRunning();

    /// <summary>Rebuilds immutable configuration snapshots read by the hook thread (called under the lifecycle lock).</summary>
    protected virtual void OnConfigure()
    {
    }

    /// <summary>Subscribes the hooks with fresh state; the returned object is disposed to stop.</summary>
    protected abstract IDisposable StartSession();

    /// <summary>Stops a session. The default disposes it; features holding keys or buttons release them first.</summary>
    protected virtual void StopSession(IDisposable session) => session.Dispose();

    private void OnSuspensionChanged(object? sender, EventArgs e) => Refresh();
}

/// <summary>Several disposables as one, disposed in reverse order.</summary>
internal sealed class CompositeDisposable(params IDisposable?[] items) : IDisposable
{
    private IDisposable?[]? _items = items;

    public void Dispose()
    {
        var items = Interlocked.Exchange(ref _items, null);
        if (items is null)
        {
            return;
        }

        for (var i = items.Length - 1; i >= 0; i--)
        {
            try
            {
                items[i]?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("input", "Dispose failed.", ex);
            }
        }
    }
}

/// <summary>Counts suspension tokens (Cleaning Mode and similar).</summary>
public sealed class InputFixesControl : IInputFixesControl
{
    private int _count;

    public event EventHandler? SuspensionChanged;

    public bool IsSuspended => Volatile.Read(ref _count) > 0;

    public IDisposable Suspend(string reason)
    {
        Log.Info("input", $"Input fixes suspended: {reason}");
        if (Interlocked.Increment(ref _count) == 1)
        {
            SuspensionChanged?.Invoke(this, EventArgs.Empty);
        }

        return new Token(this);
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _count) == 0)
        {
            SuspensionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Token(InputFixesControl owner) : IDisposable
    {
        private InputFixesControl? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

/// <summary>
/// The scroll-direction policy shared by smooth scrolling (which inverts the
/// notches it glides) and the inverter (which flips everything else), so the
/// two never cancel each other out.
/// </summary>
public sealed class ScrollDirectionPolicy
{
    private volatile Snapshot _current = new(false, false, false, AppExclusionList.Empty);

    public Snapshot Current => _current;

    internal void Update(Snapshot snapshot) => _current = snapshot;

    /// <param name="Active">The inverter feature is installed and one of its directions is on.</param>
    public sealed record Snapshot(bool Active, bool InvertVertical, bool InvertHorizontal, AppExclusionList Exceptions)
    {
        /// <summary>Whether a wheel event should be flipped, before the app check.</summary>
        public bool ShouldInvert(bool horizontalEvent, bool shiftHeld) =>
            Active && ScrollDirection.ShouldInvert(horizontalEvent, shiftHeld, InvertVertical, InvertHorizontal);
    }
}

/// <summary>Reads the effective horizontal inversion: when never saved it follows the vertical switch (macOS migration).</summary>
public static class ScrollInverterSettings
{
    public static bool Vertical(ISettingsStore settings) => settings.Get(FeatureKeys.ScrollInverterEnabled);

    public static bool Horizontal(ISettingsStore settings) =>
        settings.IsSaved(InputSettings.ScrollInverterHorizontal)
            ? settings.Get(InputSettings.ScrollInverterHorizontal)
            : settings.Get(FeatureKeys.ScrollInverterEnabled);
}
