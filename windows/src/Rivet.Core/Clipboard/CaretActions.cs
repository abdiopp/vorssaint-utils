// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;

namespace Rivet.Core.Clipboard;

public enum CaretOutcome
{
    Done,

    /// <summary>Copied only: there was no app to paste into.</summary>
    CopiedOnly,

    CopyFailed,

    /// <summary>The target is this app itself.</summary>
    OwnApp,

    /// <summary>The target never came back to the front.</summary>
    NotActivated,

    ModifiersHeld,

    /// <summary>A password field has the focus: nothing is typed there.</summary>
    PasswordField,

    /// <summary>The target runs as administrator; Windows drops input injected into it (UIPI).</summary>
    Elevated,

    Busy,
}

/// <summary>
/// Types or pastes text at the caret (spec 06 §3.1.5): a replacement with a
/// line break goes through the transient paste (backspaces right before
/// Ctrl+V); anything else is typed as Unicode keystrokes in chunks of 20,
/// followed by the trailing key. The clipboard is untouched on the typed path.
/// </summary>
public sealed class TextInserter(SyntheticInput input, TransientPaste transient)
{
    public SyntheticInput Input { get; } = input;

    /// <param name="deleteCount">Backspaces to post first (the typed trigger).</param>
    /// <param name="text">The replacement.</param>
    /// <param name="trailingKey">A key to re-emit after the text (the swallowed delimiter).</param>
    /// <param name="trailingText">The delimiter's character, kept inside a paste (<c>\r</c> becomes <c>\n</c>).</param>
    /// <param name="failureText">Typed again when the paste path fails, so the user's keystroke is not lost.</param>
    /// <returns>False when the paste path is busy; the caller then lets the original key through.</returns>
    public bool Post(int deleteCount, string text, int? trailingKey = null, string trailingText = "", string? failureText = null)
    {
        if (SyntheticInput.HasLineBreak(text))
        {
            var payload = text + trailingText.Replace('\r', '\n');
            return transient.TryPaste(payload,
                willPost: () => Input.PostBackspaces(deleteCount),
                didPost: null,
                completed: ok =>
                {
                    if (!ok && !string.IsNullOrEmpty(failureText))
                    {
                        Input.TypeText(failureText);
                    }
                });
        }

        Input.PostBackspaces(deleteCount);
        Input.TypeText(text);
        if (trailingKey is { } key)
        {
            Input.PostKey(key);
        }

        return true;
    }
}

/// <summary>
/// Give focus back to the app that was in front, then paste or type there
/// (spec 06 §3.1.3, §7.1): hide the panel, restore the foreground window,
/// wait until it really is in front (≤ 500 ms), settle, wait for the
/// modifiers to be released, refuse password fields and elevated targets, inject.
/// </summary>
public sealed class CaretActions
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMilliseconds(500);

    private readonly IForegroundService _foreground;
    private readonly IFocusHandoff _focus;
    private readonly TextInserter _inserter;

    public CaretActions(IForegroundService foreground, IFocusHandoff focus, TextInserter inserter)
    {
        _foreground = foreground;
        _focus = focus;
        _inserter = inserter;
    }

    public IForegroundService Foreground => _foreground;

    /// <summary>Remembers the app in front before a panel takes focus.</summary>
    public ForegroundApp? CaptureTarget()
    {
        var target = _foreground.Current();
        _focus.Remember();
        return target;
    }

    /// <summary>Gives focus back without acting (Esc / cancel).</summary>
    public void RestoreFocus(ForegroundApp? target)
    {
        if (target is null || target.IsSelf)
        {
            return;
        }

        _focus.Restore();
        _ = _foreground.ActivateAsync(target, ActivationTimeout);
    }

    /// <summary>
    /// Pastes what is on the clipboard into <paramref name="target"/>.
    /// <paramref name="strictModifiers"/> gives up (beep) when keys stay held,
    /// as the Command Bar does; the clipboard window posts anyway.
    /// </summary>
    public async Task<CaretOutcome> PasteAsync(ForegroundApp? target, bool strictModifiers, TimeSpan settle)
    {
        var ready = await PrepareAsync(target, strictModifiers, settle, refusePasswordFields: strictModifiers).ConfigureAwait(true);
        if (ready != CaretOutcome.Done)
        {
            return ready;
        }

        await _inserter.Input.PostPasteAsync().ConfigureAwait(true);
        return CaretOutcome.Done;
    }

    /// <summary>Types <paramref name="text"/> at the caret of <paramref name="target"/> (snippet library, emoji, snippets in the bar).</summary>
    public async Task<CaretOutcome> TypeAsync(ForegroundApp? target, string text)
    {
        var ready = await PrepareAsync(target, strictModifiers: true, TimeSpan.FromMilliseconds(150), refusePasswordFields: true).ConfigureAwait(true);
        if (ready != CaretOutcome.Done)
        {
            return ready;
        }

        return _inserter.Post(0, text) ? CaretOutcome.Done : CaretOutcome.Busy;
    }

    private async Task<CaretOutcome> PrepareAsync(ForegroundApp? target, bool strictModifiers, TimeSpan settle, bool refusePasswordFields)
    {
        if (target is null)
        {
            return CaretOutcome.CopiedOnly;
        }

        if (target.IsSelf)
        {
            _foreground.Beep();
            return CaretOutcome.OwnApp;
        }

        // Track focus from before the hand-back so the password check sees the target's focused control.
        using var tracking = _foreground.TrackFocus();
        _focus.Restore();
        if (!await _foreground.ActivateAsync(target, ActivationTimeout).ConfigureAwait(true))
        {
            _foreground.Beep();
            Log.Info("caret", "The previous app did not come back to the front; nothing was injected.");
            return CaretOutcome.NotActivated;
        }

        await Task.Delay(settle).ConfigureAwait(true);
        var released = await _inserter.Input.WaitForModifierReleaseAsync().ConfigureAwait(true);
        if (!released && strictModifiers)
        {
            _foreground.Beep();
            return CaretOutcome.ModifiersHeld;
        }

        await Task.Delay(60).ConfigureAwait(true);
        if (target.IsElevated)
        {
            _foreground.Beep();
            return CaretOutcome.Elevated;
        }

        if (refusePasswordFields && _foreground.IsPasswordFieldFocused)
        {
            _foreground.Beep();
            return CaretOutcome.PasswordField;
        }

        return CaretOutcome.Done;
    }
}
