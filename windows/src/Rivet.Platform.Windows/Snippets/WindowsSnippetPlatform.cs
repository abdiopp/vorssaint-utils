// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Shortcuts;
using Rivet.Core.Snippets;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace Rivet.Platform.Windows.Snippets;

/// <summary>
/// The characters a key types in the layout of the window in front
/// (ToUnicodeEx with flag 0x4, Windows 10 1607+, so a pending dead key in the
/// real input is never disturbed). Ctrl+Alt counts as AltGr. Dead keys yield
/// nothing: a trigger typed with a dead-key accent is seen without it.
/// </summary>
public sealed unsafe class WindowsKeyTranslator : IKeyTranslator
{
    private const uint DoNotChangeKeyboardState = 0x4;

    public string Translate(int virtualKey, int scanCode, KeyModifiers modifiers)
    {
        var foreground = PInvoke.GetForegroundWindow();
        var thread = PInvoke.GetWindowThreadProcessId(foreground, null);
        var layout = PInvoke.GetKeyboardLayout(thread);
        var state = stackalloc byte[256];
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            state[VirtualKeys.Shift] = state[VirtualKeys.LShift] = 0x80;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            state[VirtualKeys.Control] = state[VirtualKeys.LControl] = 0x80;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            state[VirtualKeys.Menu] = state[VirtualKeys.RMenu] = 0x80;
        }

        if ((PInvoke.GetKeyState(VirtualKeys.Capital) & 1) != 0)
        {
            state[VirtualKeys.Capital] = 0x01;
        }

        var buffer = stackalloc char[8];
        var count = PInvoke.ToUnicodeEx((uint)virtualKey, (uint)scanCode, state, buffer, 8, DoNotChangeKeyboardState, layout);
        if (count <= 0)
        {
            return string.Empty;
        }

        var text = new string(buffer, 0, count);
        return text.Length > 0 && char.IsControl(text[0]) && text[0] is not ('\r' or '\t' or '\n') ? string.Empty : text;
    }
}

/// <summary>The expansion cue: the .wav files of %windir%\Media, played asynchronously.</summary>
public sealed class WindowsSnippetSounds : ISnippetSounds
{
    private static string MediaFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    public IReadOnlyList<string> Available()
    {
        try
        {
            return Directory.EnumerateFiles(MediaFolder, "*.wav")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [SnippetSettings.DefaultSound];
        }
    }

    public void Play(string name)
    {
        var path = Path.Combine(MediaFolder, name + ".wav");
        if (!File.Exists(path))
        {
            path = Path.Combine(MediaFolder, SnippetSettings.DefaultSound + ".wav");
        }

        if (!PInvoke.PlaySound(path, null, SND_FLAGS.SND_ASYNC | SND_FLAGS.SND_FILENAME | SND_FLAGS.SND_NODEFAULT))
        {
            Log.Debug("snippets", "The expansion sound could not be played.");
        }
    }
}

public sealed class WindowsSnippetsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IKeyTranslator, WindowsKeyTranslator>();
        services.AddSingleton<ISnippetSounds, WindowsSnippetSounds>();
    }
}
