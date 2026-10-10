// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Clipboard;

/// <summary>The app that owned the foreground window when one of our panels was summoned.</summary>
public sealed record ForegroundApp
{
    public nint Window { get; init; }

    public int ProcessId { get; init; }

    /// <summary>Lower-case executable path, or the AppUserModelID for packaged apps.</summary>
    public string? Identity { get; init; }

    /// <summary>Display name (file description or executable name).</summary>
    public string? Name { get; init; }

    /// <summary>Runs at a higher integrity level than this app (input injected there is dropped by UIPI).</summary>
    public bool IsElevated { get; init; }

    /// <summary>The window belongs to this app.</summary>
    public bool IsSelf { get; init; }
}

/// <summary>An app with visible windows, for pickers such as "Apps to skip".</summary>
public sealed record RunningApp(string Identity, string Name, string? ExecutablePath);

/// <summary>
/// Foreground window bookkeeping for "act at the caret" flows (spec 06
/// §7.1): Windows has no non-activating key panels, so panels remember the
/// target, take focus, then give it back and wait for the target to be in
/// front again before injecting input. Also tracks keyboard focus changes and
/// whether a password field holds the focus (UI Automation IsPassword).
/// </summary>
public interface IForegroundService
{
    ForegroundApp? Current();

    /// <summary>
    /// Brings <paramref name="target"/> to the front if needed and waits until
    /// it is (at most <paramref name="timeout"/>). False when it never got there.
    /// </summary>
    Task<bool> ActivateAsync(ForegroundApp target, TimeSpan timeout);

    /// <summary>Last known answer for the focused control (refreshed on every focus change while tracking).</summary>
    bool IsPasswordFieldFocused { get; }

    /// <summary>Raised (on any thread) when the foreground window or the focused control changes, while tracking.</summary>
    event EventHandler? FocusChanged;

    /// <summary>Starts focus tracking until the returned handle is disposed (ref-counted).</summary>
    IDisposable TrackFocus();

    /// <summary>The system "can't do that" sound.</summary>
    void Beep();

    IReadOnlyList<RunningApp> RunningApps();
}
