// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;

namespace Rivet.Core.ScreenshotEditor;

/// <summary>The rail order (= digit mapping 1–9), stored as a CSV of tool ids.</summary>
public static class ToolOrder
{
    public static IReadOnlyList<EditorTool> Default => EditorTools.Canonical;

    public static string DefaultCsv => Format(Default);

    /// <summary>Invalid and duplicate ids are dropped; tools missing from the list are appended in canonical order.</summary>
    public static IReadOnlyList<EditorTool> Parse(string? csv)
    {
        var order = new List<EditorTool>(EditorTools.Canonical.Count);
        foreach (var token in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (EditorTools.TryParse(token, out var tool) && !order.Contains(tool))
            {
                order.Add(tool);
            }
        }

        foreach (var tool in EditorTools.Canonical)
        {
            if (!order.Contains(tool))
            {
                order.Add(tool);
            }
        }

        return order;
    }

    public static string Format(IEnumerable<EditorTool> order) => string.Join(',', order.Select(EditorTools.Id));

    /// <summary>The 1-based digit of a tool in the first nine slots, else null.</summary>
    public static int? DigitOf(IReadOnlyList<EditorTool> order, EditorTool tool)
    {
        var index = IndexOf(order, tool);
        return index is >= 0 and < 9 ? index + 1 : null;
    }

    public static IReadOnlyList<EditorTool> MoveTo(IReadOnlyList<EditorTool> order, EditorTool tool, int index)
    {
        var list = order.Where(t => t != tool).ToList();
        list.Insert(Math.Clamp(index, 0, list.Count), tool);
        return list;
    }

    /// <summary>Swaps the tool with its neighbour (−1 up, +1 down); no change at the ends.</summary>
    public static IReadOnlyList<EditorTool> Swap(IReadOnlyList<EditorTool> order, EditorTool tool, int direction)
    {
        var list = order.ToList();
        var index = list.IndexOf(tool);
        var other = index + Math.Sign(direction);
        if (index < 0 || other < 0 || other >= list.Count)
        {
            return order;
        }

        (list[index], list[other]) = (list[other], list[index]);
        return list;
    }

    private static int IndexOf(IReadOnlyList<EditorTool> order, EditorTool tool)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == tool)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Keys that always belong to the editor and can never become a tool's key
/// (spec 01 §3.10.14 with Windows equivalents): with Ctrl (plus any other
/// modifier) C, S, Z, Y, P, 0, 1, =, −, numpad + and −, Backspace, Delete, W;
/// Alt+F4; and without Ctrl or Alt: Esc, Enter, Backspace and Delete.
/// </summary>
public static class EditorReservedKeys
{
    public static bool IsReserved(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return false;
        }

        var vk = chord.VirtualKey;
        if (chord.Modifiers.HasFlag(KeyModifiers.Control))
        {
            return vk is VirtualKeys.Back or VirtualKeys.Delete or VirtualKeys.OemPlus or VirtualKeys.OemMinus
                       or VirtualKeys.Add or VirtualKeys.Subtract
                   || vk == VirtualKeys.Letter('C') || vk == VirtualKeys.Letter('S') || vk == VirtualKeys.Letter('Z')
                   || vk == VirtualKeys.Letter('Y') || vk == VirtualKeys.Letter('P') || vk == VirtualKeys.Letter('W')
                   || vk == VirtualKeys.Digit(0) || vk == VirtualKeys.Digit(1)
                   || vk == VirtualKeys.NumPad0 || vk == VirtualKeys.NumPad0 + 1;
        }

        if (chord.Modifiers.HasFlag(KeyModifiers.Alt) && vk == VirtualKeys.Function(4))
        {
            return true;
        }

        if ((chord.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) == 0)
        {
            return vk is VirtualKeys.Escape or VirtualKeys.Return or VirtualKeys.Back or VirtualKeys.Delete;
        }

        return false;
    }
}

/// <summary>Custom per-tool keys, stored as <c>tool=chord,…</c> with <see cref="KeyChord.ToStorageString"/> chords.</summary>
public sealed class ToolBindings
{
    private readonly Dictionary<EditorTool, KeyChord> _keys;

    public ToolBindings(IReadOnlyDictionary<EditorTool, KeyChord>? keys = null)
    {
        _keys = keys is null ? [] : new Dictionary<EditorTool, KeyChord>(keys.Where(kv => !kv.Value.IsEmpty));
    }

    public static ToolBindings Empty { get; } = new();

    public IReadOnlyDictionary<EditorTool, KeyChord> Keys => _keys;

    public KeyChord? KeyFor(EditorTool tool) => _keys.TryGetValue(tool, out var chord) ? chord : null;

    public EditorTool? ToolFor(KeyChord chord)
    {
        foreach (var (tool, key) in _keys)
        {
            if (key == chord)
            {
                return tool;
            }
        }

        return null;
    }

    public ToolBindings With(EditorTool tool, KeyChord? chord)
    {
        var copy = new Dictionary<EditorTool, KeyChord>(_keys);
        if (chord is { IsEmpty: false } key)
        {
            copy[tool] = key;
        }
        else
        {
            copy.Remove(tool);
        }

        return new ToolBindings(copy);
    }

    /// <summary>
    /// Invalid, reserved or duplicate entries are dropped; when two tools
    /// claim the same key, the one first in canonical order keeps it.
    /// </summary>
    public static ToolBindings Parse(string? csv)
    {
        var claimed = new Dictionary<EditorTool, KeyChord>();
        foreach (var entry in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0
                || !EditorTools.TryParse(entry[..eq], out var tool)
                || !KeyChord.TryParse(entry[(eq + 1)..], out var chord)
                || EditorReservedKeys.IsReserved(chord)
                || claimed.ContainsKey(tool))
            {
                continue;
            }

            claimed[tool] = chord;
        }

        var result = new Dictionary<EditorTool, KeyChord>();
        foreach (var tool in EditorTools.Canonical)
        {
            if (claimed.TryGetValue(tool, out var chord) && !result.ContainsValue(chord))
            {
                result[tool] = chord;
            }
        }

        return new ToolBindings(result);
    }

    public string Format() =>
        string.Join(',', EditorTools.Canonical.Where(_keys.ContainsKey).Select(t => $"{EditorTools.Id(t)}={_keys[t].ToStorageString()}"));
}

/// <summary>Why a key could not become a tool's shortcut.</summary>
public enum ToolKeyRejection
{
    None,

    /// <summary>"This key belongs to the editor."</summary>
    ReservedByEditor,

    /// <summary>Used by another tool (the conflicting name is reported).</summary>
    UsedByTool,

    /// <summary>A global shortcut of an enabled feature.</summary>
    UsedByShortcut,

    /// <summary>A Windows shortcut.</summary>
    UsedBySystem,
}

/// <summary>The outcome of recording a key in a tool's shortcut field.</summary>
public sealed record ToolKeyOutcome(
    IReadOnlyList<EditorTool> Order,
    ToolBindings Bindings,
    ToolKeyRejection Rejection = ToolKeyRejection.None,
    string? ConflictName = null)
{
    public bool Accepted => Rejection == ToolKeyRejection.None;
}

/// <summary>
/// Recording a tool key (spec 01 §3.10.15): a digit 1–9 moves the tool into
/// that slot and clears its key; Delete clears the key and moves the tool just
/// below slot 9; any other key is checked (editor, other tools, global
/// shortcuts, Windows) and becomes the tool's custom key. A rejected key
/// leaves the previous binding untouched.
/// </summary>
public static class ToolKeyRecorder
{
    public static ToolKeyOutcome Record(
        IReadOnlyList<EditorTool> order,
        ToolBindings bindings,
        EditorTool tool,
        KeyChord chord,
        int? typedDigit,
        Func<KeyChord, string?>? globalShortcutOwner = null,
        Func<KeyChord, bool>? isSystemShortcut = null,
        Func<EditorTool, string>? toolName = null)
    {
        if (typedDigit is >= 1 and <= 9 && (chord.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) == 0)
        {
            return new ToolKeyOutcome(ToolOrder.MoveTo(order, tool, typedDigit.Value - 1), bindings.With(tool, null));
        }

        if (chord.IsEmpty || (chord.VirtualKey is VirtualKeys.Delete or VirtualKeys.Back && chord.Modifiers == KeyModifiers.None))
        {
            return new ToolKeyOutcome(ToolOrder.MoveTo(order, tool, 9), bindings.With(tool, null));
        }

        if (EditorReservedKeys.IsReserved(chord))
        {
            return new ToolKeyOutcome(order, bindings, ToolKeyRejection.ReservedByEditor);
        }

        if (bindings.ToolFor(chord) is { } other && other != tool)
        {
            return new ToolKeyOutcome(order, bindings, ToolKeyRejection.UsedByTool, toolName?.Invoke(other) ?? EditorTools.Id(other));
        }

        if (globalShortcutOwner?.Invoke(chord) is { } owner)
        {
            return new ToolKeyOutcome(order, bindings, ToolKeyRejection.UsedByShortcut, owner);
        }

        if (isSystemShortcut?.Invoke(chord) == true)
        {
            return new ToolKeyOutcome(order, bindings, ToolKeyRejection.UsedBySystem);
        }

        return new ToolKeyOutcome(order, bindings.With(tool, chord));
    }
}

/// <summary>Resolves editor key presses to tools and produces the rail badges.</summary>
public static class ToolKeyMap
{
    /// <summary>
    /// Custom keys first (exact modifier match, unless the layout makes the key
    /// type a digit), then digits 1–9 without Ctrl/Alt/Win for tools without a
    /// custom key. Nothing when tool shortcuts are off.
    /// </summary>
    public static EditorTool? Resolve(
        IReadOnlyList<EditorTool> order,
        ToolBindings bindings,
        bool enabled,
        KeyChord pressed,
        int? typedDigit,
        Func<KeyChord, int?>? digitTypedBy = null)
    {
        if (!enabled || pressed.IsEmpty)
        {
            return null;
        }

        if (bindings.ToolFor(pressed) is { } bound && !IsSuspended(pressed, digitTypedBy))
        {
            return bound;
        }

        if (typedDigit is >= 1 and <= 9 && (pressed.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) == 0)
        {
            var index = typedDigit.Value - 1;
            if (index < order.Count)
            {
                var tool = order[index];
                if (bindings.KeyFor(tool) is not { } key || IsSuspended(key, digitTypedBy))
                {
                    return tool;
                }
            }
        }

        return null;
    }

    /// <summary>A custom key that the current layout (or Caps Lock) makes type a digit is suspended, not erased.</summary>
    public static bool IsSuspended(KeyChord key, Func<KeyChord, int?>? digitTypedBy) =>
        digitTypedBy?.Invoke(key) is >= 0 and <= 9;

    /// <summary>The badge on the rail: the custom key, or the position digit for the first nine, or nothing.</summary>
    public static string? Badge(IReadOnlyList<EditorTool> order, ToolBindings bindings, EditorTool tool, bool enabled, Func<KeyChord, string> keyName, Func<KeyChord, int?>? digitTypedBy = null)
    {
        if (!enabled)
        {
            return null;
        }

        if (bindings.KeyFor(tool) is { } key && !IsSuspended(key, digitTypedBy))
        {
            return keyName(key);
        }

        return ToolOrder.DigitOf(order, tool)?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
