// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Clipboard;

/// <summary>
/// Synthetic keyboard input for pastes and typed text (spec 06 §3.1.3,
/// §3.1.5, §7.1). Everything goes through <see cref="IInputHooks"/>, whose
/// injected events carry the app's marker so its own hook ignores them.
/// </summary>
public sealed class SyntheticInput(IInputHooks hooks)
{
    /// <summary>Unicode text is typed in chunks of at most this many UTF-16 units.</summary>
    public const int ChunkSize = 20;

    private static readonly int[] ModifierKeys =
    [
        VirtualKeys.Shift, VirtualKeys.Control, VirtualKeys.Menu, VirtualKeys.LWin, VirtualKeys.RWin,
    ];

    public IInputHooks Hooks { get; } = hooks;

    /// <summary>
    /// Waits until Ctrl, Alt, Shift and Win are physically up (15 ms × at most
    /// 100 polls, ~1.5 s). Returns false when one is still held. Injecting while
    /// Win is held can open Start when it is released.
    /// </summary>
    public async Task<bool> WaitForModifierReleaseAsync(int maxPolls = 100, int intervalMs = 15)
    {
        for (var poll = 0; poll < maxPolls; poll++)
        {
            if (!AnyModifierDown())
            {
                return true;
            }

            await Task.Delay(intervalMs).ConfigureAwait(true);
        }

        return !AnyModifierDown();
    }

    public bool AnyModifierDown() => ModifierKeys.Any(Hooks.IsKeyDown);

    /// <summary>Ctrl↓ V↓, 40 ms, V↑ Ctrl↑.</summary>
    public async Task PostPasteAsync()
    {
        Hooks.SendKeys([(VirtualKeys.Control, KeyAction.Down), ('V', KeyAction.Down)]);
        await Task.Delay(40).ConfigureAwait(true);
        Hooks.SendKeys([('V', KeyAction.Up), (VirtualKeys.Control, KeyAction.Up)]);
    }

    public void PostBackspaces(int count)
    {
        if (count <= 0)
        {
            return;
        }

        var strokes = new List<(int, KeyAction)>(count * 2);
        for (var i = 0; i < count; i++)
        {
            strokes.Add((VirtualKeys.Back, KeyAction.Down));
            strokes.Add((VirtualKeys.Back, KeyAction.Up));
        }

        Hooks.SendKeys(strokes);
    }

    /// <summary>A key press (down and up) of one virtual key, e.g. the re-emitted delimiter.</summary>
    public void PostKey(int virtualKey) =>
        Hooks.SendKeys([(virtualKey, KeyAction.Down), (virtualKey, KeyAction.Up)]);

    /// <summary>Types text as Unicode key events in chunks of at most 20 UTF-16 units.</summary>
    public void TypeText(string text)
    {
        foreach (var chunk in Chunks(text, ChunkSize))
        {
            Hooks.SendText(chunk);
        }
    }

    /// <summary>Splits into chunks of at most <paramref name="size"/> units, never splitting a surrogate pair.</summary>
    public static IEnumerable<string> Chunks(string text, int size = ChunkSize)
    {
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(size, text.Length - start);
            if (length < text.Length - start && char.IsHighSurrogate(text[start + length - 1]))
            {
                length--;
            }

            if (length <= 0)
            {
                length = Math.Min(2, text.Length - start);
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }

    /// <summary>Any line-break character (they make snippets use the paste path).</summary>
    public static bool HasLineBreak(string text) =>
        text.AsSpan().IndexOfAny("\n\r\u2028\u2029\u0085\u000B\u000C") >= 0;
}
