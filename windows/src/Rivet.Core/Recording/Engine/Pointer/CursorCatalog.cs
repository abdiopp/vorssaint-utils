// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Recording.Engine.Pointer;

/// <summary>
/// Every distinct cursor picture seen during a recording, in order of first
/// appearance (index 0 is the cursor on screen when recording began).
/// Pictures are deduplicated by content identity (§6.24): Windows may hand
/// out a new handle for the same picture, and the same handle can be reused.
/// </summary>
public sealed class CursorCatalog
{
    /// <summary>Indices are stored as u16 in pointer.bin; far more than any recording shows.</summary>
    public const int MaxShapes = 1024;

    private readonly Dictionary<ulong, ushort> _byIdentity = [];
    private readonly List<CursorShapeSnapshot> _shapes = [];

    public IReadOnlyList<CursorShapeSnapshot> Shapes => _shapes;

    /// <summary>The index of <paramref name="shape"/>, adding it on first sight. Null when the table is full.</summary>
    public ushort? Add(CursorShapeSnapshot shape)
    {
        if (_byIdentity.TryGetValue(shape.Identity, out var index))
        {
            return index;
        }

        if (_shapes.Count >= MaxShapes || shape.Png.Length == 0 || !(shape.Width > 0) || !(shape.Height > 0))
        {
            return null;
        }

        index = (ushort)_shapes.Count;
        _shapes.Add(shape);
        _byIdentity[shape.Identity] = index;
        return index;
    }

    /// <summary>The shapes in the take format.</summary>
    public IReadOnlyList<PointerShape> ToPointerShapes() =>
        _shapes.Select(s => new PointerShape(
            Math.Clamp(s.HotX, 0, s.Width),
            Math.Clamp(s.HotY, 0, s.Height),
            s.Width,
            s.Height,
            s.Png)).ToList();
}
