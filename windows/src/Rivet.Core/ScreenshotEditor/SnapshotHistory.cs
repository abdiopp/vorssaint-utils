// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.ScreenshotEditor;

/// <summary>
/// Undo and redo over immutable snapshots. An entry is recorded at the start
/// of each mutation; a new record clears the redo stack; the oldest entries
/// are dropped beyond <see cref="Capacity"/> (60 in the editor).
/// </summary>
public sealed class SnapshotHistory<T>(int capacity = 60)
{
    private readonly LinkedList<T> _undo = new();
    private readonly Stack<T> _redo = new();

    public int Capacity { get; } = Math.Max(1, capacity);

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Record(T current)
    {
        _undo.AddLast(current);
        while (_undo.Count > Capacity)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
    }

    public bool TryUndo(T current, out T previous)
    {
        if (_undo.Last is not { } last)
        {
            previous = default!;
            return false;
        }

        previous = last.Value;
        _undo.RemoveLast();
        _redo.Push(current);
        return true;
    }

    public bool TryRedo(T current, out T next)
    {
        if (!_redo.TryPop(out next!))
        {
            return false;
        }

        _undo.AddLast(current);
        while (_undo.Count > Capacity)
        {
            _undo.RemoveFirst();
        }

        return true;
    }

    /// <summary>Drops the newest undo entries until <paramref name="count"/> remain (a new, empty text that never existed).</summary>
    public void TruncateTo(int count)
    {
        while (_undo.Count > Math.Max(0, count))
        {
            _undo.RemoveLast();
        }
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
