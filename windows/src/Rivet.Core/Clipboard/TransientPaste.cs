// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Util;

namespace Rivet.Core.Clipboard;

/// <summary>
/// Write, paste, restore (spec 06 §3.1.4): snapshot every clipboard format,
/// put text on the clipboard, inject Ctrl+V, and 0.5 s later put the
/// snapshot back unless the user copied something else. A snapshot that
/// cannot be taken in full fails open (nothing is changed). Both the write
/// and the restore carry the transient marks, so Win+V history and other
/// clipboard managers ignore them, and the history skips their counters.
/// </summary>
public sealed class TransientPaste
{
    /// <summary>Snapshots above this size fail open (huge Office copies).</summary>
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LaneTimeout = TimeSpan.FromSeconds(3);

    private readonly ClipboardWatcher _watcher;
    private readonly SyntheticInput _input;
    private (ClipboardSnapshot Snapshot, uint Counter)? _pendingRestore;
    private CancellationTokenSource? _restoreCancel;
    private bool _performing;

    public TransientPaste(ClipboardWatcher watcher, SyntheticInput input)
    {
        _watcher = watcher;
        _input = input;
    }

    public bool IsBusy => _performing;

    /// <summary>
    /// Pastes <paramref name="text"/> through the clipboard. Returns false at
    /// once when another transient paste is running (callers fall back).
    /// <paramref name="willPost"/> runs right before Ctrl+V (snippets post
    /// their backspaces there). The task completes with the outcome.
    /// </summary>
    public bool TryPaste(string text, Action? willPost, Action? didPost, Action<bool>? completed = null)
    {
        if (_performing)
        {
            return false;
        }

        _performing = true;
        _restoreCancel?.Cancel();
        var reuse = _pendingRestore;
        _watcher.Lane.Run<(ClipboardSnapshot Snapshot, uint Counter)?>(LaneTimeout, (clipboard, _) =>
        {
            var before = clipboard.SequenceNumber;
            var snapshot = reuse is { } pending && pending.Counter == before ? pending.Snapshot : clipboard.Snapshot(MaxSnapshotBytes);
            if (snapshot is null || clipboard.SequenceNumber != before)
            {
                return null;
            }

            if (!clipboard.Write(ClipboardWriteData.FromText(text), ClipboardWriteMarks.Transient))
            {
                clipboard.Restore(snapshot, ClipboardWriteMarks.Transient);
                return null;
            }

            return (snapshot, clipboard.SequenceNumber);
        }, (result, ok) => _ = AfterWriteAsync(ok ? result : null, willPost, didPost, completed));
        return true;
    }

    /// <summary>Forwards a normal Ctrl+V without touching the clipboard (media copies).</summary>
    public async Task PasteCurrentContentsAsync()
    {
        await _input.WaitForModifierReleaseAsync().ConfigureAwait(true);
        await Task.Delay(60).ConfigureAwait(true);
        await _input.PostPasteAsync().ConfigureAwait(true);
    }

    private async Task AfterWriteAsync((ClipboardSnapshot Snapshot, uint Counter)? written, Action? willPost, Action? didPost, Action<bool>? completed)
    {
        if (written is not { } state)
        {
            _performing = false;
            Log.Info("clipboard", "Transient paste skipped: the clipboard could not be preserved.");
            completed?.Invoke(false);
            return;
        }

        _watcher.NoteOwnWrite(state.Counter);
        _pendingRestore = state;
        try
        {
            // Still-held modifiers would turn Ctrl+V into another shortcut; post anyway after ~1.5 s.
            await _input.WaitForModifierReleaseAsync().ConfigureAwait(true);
            await Task.Delay(60).ConfigureAwait(true);
            willPost?.Invoke();
            await _input.PostPasteAsync().ConfigureAwait(true);
            didPost?.Invoke();
        }
        finally
        {
            _performing = false;
        }

        ScheduleRestore(state);
        completed?.Invoke(true);
    }

    private void ScheduleRestore((ClipboardSnapshot Snapshot, uint Counter) state)
    {
        var cancel = new CancellationTokenSource();
        _restoreCancel = cancel;
        Task.Delay(RestoreDelay, cancel.Token).ContinueWith(task =>
        {
            if (task.IsCanceled)
            {
                return;
            }

            UiThread.Post(() =>
            {
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                _watcher.Lane.Run<uint?>(LaneTimeout, (clipboard, _) =>
                {
                    // Leave a newer copy alone.
                    if (clipboard.SequenceNumber != state.Counter)
                    {
                        return null;
                    }

                    return clipboard.Restore(state.Snapshot, ClipboardWriteMarks.Transient) ? clipboard.SequenceNumber : null;
                }, (counter, ok) =>
                {
                    if (_pendingRestore?.Counter == state.Counter)
                    {
                        _pendingRestore = null;
                    }

                    if (ok && counter is { } c)
                    {
                        _watcher.NoteOwnWrite(c);
                    }
                });
            });
        }, TaskScheduler.Default);
    }
}
