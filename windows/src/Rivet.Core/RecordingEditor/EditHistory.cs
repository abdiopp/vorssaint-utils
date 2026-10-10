// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor;

/// <summary>What a document change requires the editor to rebuild (§3.32).</summary>
[Flags]
public enum EditChange
{
    None = 0,

    /// <summary>Trim or cuts: the edited timeline (and the picture).</summary>
    Timing = 1,

    /// <summary>Kept tracks or gains: the audio mix only.</summary>
    Audio = 2,

    /// <summary>Background, shape, pointer, zooms, overlays: the compositor.</summary>
    Picture = 4,

    /// <summary>Export-only fields (quality, speed, GIF): persisted, nothing rebuilt.</summary>
    ExportOnly = 8,
}

/// <summary>
/// Unlimited undo/redo of whole-document snapshots (spec 02 §3.32). Plain
/// changes push one entry each; an interaction (a drag, a slider, typing)
/// snapshots the document at its start, applies live values without history,
/// and commits as one entry. Every committed change is persisted by the
/// owner (the <see cref="Committed"/> event), never during a drag.
/// </summary>
public sealed class EditHistory
{
    private readonly Stack<EditDocument> _undo = new();
    private readonly Stack<EditDocument> _redo = new();
    private EditDocument? _interactionStart;

    public EditHistory(EditDocument initial)
    {
        Current = initial;
    }

    public EditDocument Current { get; private set; }

    public bool CanUndo => _undo.Count > 0 && _interactionStart is null;

    public bool CanRedo => _redo.Count > 0 && _interactionStart is null;

    public bool InInteraction => _interactionStart is not null;

    public int UndoCount => _undo.Count;

    /// <summary>Raised after every change of <see cref="Current"/> (live ones included) with what changed.</summary>
    public event Action<EditDocument, EditDocument, EditChange>? Changed;

    /// <summary>Raised when a change becomes final (persist now).</summary>
    public event Action<EditDocument>? Committed;

    public static EditChange Classify(EditDocument before, EditDocument after)
    {
        var change = EditChange.None;
        if (EditDocument.AffectsTiming(before, after))
        {
            change |= EditChange.Timing;
        }

        if (EditDocument.AffectsAudio(before, after))
        {
            change |= EditChange.Audio;
        }

        if (EditDocument.AffectsPicture(before, after))
        {
            change |= EditChange.Picture;
        }

        if (change == EditChange.None && !before.Equals(after))
        {
            change = EditChange.ExportOnly;
        }

        return change;
    }

    /// <summary>One undoable change (no-op when nothing differs).</summary>
    public bool Apply(EditDocument next)
    {
        if (_interactionStart is not null)
        {
            SetLive(next);
            return true;
        }

        if (next.Equals(Current))
        {
            return false;
        }

        var before = Current;
        _undo.Push(before);
        _redo.Clear();
        Current = next;
        Changed?.Invoke(before, next, Classify(before, next));
        Committed?.Invoke(next);
        return true;
    }

    /// <summary>Replaces the document without an undo entry, persisted (automatic zoom generation at first open).</summary>
    public void ReplaceWithoutUndo(EditDocument next)
    {
        var before = Current;
        Current = next;
        Changed?.Invoke(before, next, Classify(before, next));
        Committed?.Invoke(next);
    }

    public void BeginInteraction()
    {
        _interactionStart ??= Current;
    }

    /// <summary>A live value during an interaction: no history, no persisting.</summary>
    public void SetLive(EditDocument next)
    {
        if (next.Equals(Current))
        {
            return;
        }

        var before = Current;
        Current = next;
        Changed?.Invoke(before, next, Classify(before, next));
    }

    /// <summary>Ends the interaction: one undo entry if anything changed.</summary>
    public bool CommitInteraction()
    {
        if (_interactionStart is not { } start)
        {
            return false;
        }

        _interactionStart = null;
        if (start.Equals(Current))
        {
            return false;
        }

        _undo.Push(start);
        _redo.Clear();
        Committed?.Invoke(Current);
        return true;
    }

    /// <summary>Abandons the interaction and restores the document it started from.</summary>
    public void CancelInteraction()
    {
        if (_interactionStart is not { } start)
        {
            return;
        }

        _interactionStart = null;
        SetLive(start);
    }

    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        var before = Current;
        _redo.Push(before);
        Current = _undo.Pop();
        Changed?.Invoke(before, Current, Classify(before, Current));
        Committed?.Invoke(Current);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        var before = Current;
        _undo.Push(before);
        Current = _redo.Pop();
        Changed?.Invoke(before, Current, Classify(before, Current));
        Committed?.Invoke(Current);
        return true;
    }
}
