// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Input;

/// <summary>
/// Which app owns the window under the pointer and the window in front. Every
/// read is a lock-free snapshot that never blocks the hook thread: the
/// resolver refreshes on a worker thread and serves the last answer for the
/// same window meanwhile (spec 07 §3.7.8 "Cache").
/// </summary>
public interface IAppIdentityResolver
{
    /// <summary>
    /// The app owning the window under <paramref name="point"/> (physical
    /// pixels), or null when it is not known yet. Callers treat null as
    /// "leave alone". Also schedules a refresh when the point left the cached window.
    /// </summary>
    AppIdentityInfo? PointerApp(PixelPoint point);

    /// <summary>The foreground app as last resolved (null when unknown).</summary>
    AppIdentityInfo? ForegroundApp { get; }

    /// <summary>
    /// Resolves the foreground window and its app right now. Uses only kernel
    /// and window-manager queries that never send messages to other apps, so
    /// it is safe on the hook thread for rare events (quit protection chords).
    /// </summary>
    (nint Window, AppIdentityInfo? App) ResolveForegroundNow();

    /// <summary>The foreground window handle (cheap).</summary>
    nint ForegroundWindow { get; }

    /// <summary>Keeps the background tracking alive while a feature needs identities.</summary>
    IDisposable Track();
}

/// <summary>Whether any app of a list is running, even in the background (Super key pause rule).</summary>
public interface IRunningAppsMonitor
{
    bool IsAnyRunning(AppExclusionList apps);

    /// <summary>Polls (every ~2 s) while watched and calls <paramref name="changed"/> when the set of running apps changes.</summary>
    IDisposable Watch(Action changed);
}

public enum WheelSource
{
    /// <summary>A notched mouse wheel (whole multiples of 120).</summary>
    MouseNotched,

    /// <summary>A high-resolution mouse wheel (fractions of a notch, no touchpad active).</summary>
    MouseHighResolution,

    /// <summary>A Precision Touchpad scroll.</summary>
    Touchpad,

    /// <summary>Fractional deltas from an unknown device: a touchpad or a high-resolution wheel.</summary>
    Unknown,
}

/// <summary>
/// Mouse wheel vs touchpad (spec 07 risk #2). WH_MOUSE_LL carries no device,
/// so the baseline treats whole notches as a mouse and everything else as
/// "unknown" (left alone). The Windows implementation adds Raw Input tracking
/// of Precision Touchpad contacts to tell high-resolution wheels from touchpads.
/// </summary>
public interface IWheelDeviceClassifier
{
    WheelSource Classify(int delta, long timestampNs);

    /// <summary>Keeps device tracking alive while a wheel feature runs.</summary>
    IDisposable Track();
}

/// <summary>The device-less baseline: multiples of 120 are a notched mouse wheel.</summary>
public sealed class NotchWheelClassifier : IWheelDeviceClassifier
{
    public const int WheelDelta = 120;

    public static WheelSource ClassifyDelta(int delta) =>
        delta != 0 && delta % WheelDelta == 0 ? WheelSource.MouseNotched : WheelSource.Unknown;

    public WheelSource Classify(int delta, long timestampNs) => ClassifyDelta(delta);

    public IDisposable Track() => NoopDisposable.Instance;
}

/// <summary>Keyboard facts and actions the Super key and key debounce need.</summary>
public interface IKeyboardInfo
{
    /// <summary>Delay before auto-repeat starts (250–1000 ms).</summary>
    int RepeatDelayMs { get; }

    /// <summary>Interval between auto-repeats.</summary>
    int RepeatIntervalMs { get; }

    bool IsCapsLockOn { get; }

    /// <summary>
    /// The physical state of a key when known (Raw Input sees keys a hook
    /// swallowed), or null when unknown. Only reliable while tracking.
    /// </summary>
    bool? IsPhysicallyDown(int virtualKey);

    /// <summary>Tracks physical key state while held.</summary>
    IDisposable TrackPhysicalKeys();

    /// <summary>Selects the next keyboard layout of the foreground window (asynchronous on Windows).</summary>
    void SelectNextInputSource();

    /// <summary>The virtual key the current layout gives a scan id (bit 0x100 = extended), 0 if none.</summary>
    int ScanToVirtualKey(int scanId);
}

/// <summary>Paces smooth-scroll frames at the refresh rate of the display under the pointer.</summary>
public interface IGlideFrameTimer
{
    /// <summary>
    /// Calls <paramref name="onFrame"/> with the seconds since the previous
    /// frame (first call: one frame interval) on a timer thread until it
    /// returns false or <see cref="Stop"/> runs. Starting while running keeps
    /// the current loop and swaps the callback.
    /// </summary>
    void Start(Func<double, bool> onFrame);

    void Stop();
}

/// <summary>An app the picker offers.</summary>
public sealed record AppCatalogEntry(string Name, string Path);

/// <summary>Apps to offer in the "Add an app…" picker.</summary>
public interface IAppCatalog
{
    /// <summary>Running apps with visible windows, plus installed apps when known. May take a moment; call off the UI thread.</summary>
    IReadOnlyList<AppCatalogEntry> ListApps();
}

/// <summary>
/// Optional contract for the radial menu: mouse buttons it uses as triggers.
/// The radial menu always wins over button shortcuts and the desktop drag
/// (spec 07 §3.7.3). Resolved with GetService; absent means "no claims".
/// </summary>
public interface IMouseButtonClaims
{
    /// <summary>True when the radial menu currently uses <paramref name="buttonId"/> (3–31).</summary>
    bool IsClaimed(int buttonId);
}

/// <summary>
/// Lets another feature pause the input fixes (Cleaning Mode pauses the click
/// filter, key debounce and button shortcuts while the keyboard is locked).
/// </summary>
public interface IInputFixesControl
{
    /// <summary>Suspends until the token is disposed. Tokens nest.</summary>
    IDisposable Suspend(string reason);

    bool IsSuspended { get; }

    event EventHandler? SuspensionChanged;
}

/// <summary>A disposable that does nothing.</summary>
public sealed class NoopDisposable : IDisposable
{
    public static NoopDisposable Instance { get; } = new();

    public void Dispose()
    {
    }
}
