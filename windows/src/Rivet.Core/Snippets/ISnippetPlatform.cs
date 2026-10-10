// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Snippets;

/// <summary>
/// Turns a key event into the characters the active keyboard layout types
/// (ToUnicodeEx with the foreground thread's layout, without disturbing a
/// pending dead key). Called on the hook thread: must be fast.
/// </summary>
public interface IKeyTranslator
{
    /// <summary>The text the key types with <paramref name="modifiers"/> held; empty for dead keys and non-character keys.</summary>
    string Translate(int virtualKey, int scanCode, KeyModifiers modifiers);
}

/// <summary>System alert sounds for the expansion cue (%windir%\Media\*.wav on Windows).</summary>
public interface ISnippetSounds
{
    /// <summary>Sound names (file names without extension), sorted.</summary>
    IReadOnlyList<string> Available();

    /// <summary>Plays asynchronously; unknown names fall back to the default sound.</summary>
    void Play(string name);
}
