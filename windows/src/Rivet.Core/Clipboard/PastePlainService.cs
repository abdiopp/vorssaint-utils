// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Clipboard;

/// <summary>
/// Paste as plain text (spec 06 §3.4): pastes the clipboard's text without
/// formatting and restores the original rich content 0.5 s later. File,
/// image, audio and document copies are forwarded as a normal paste. The
/// macOS "Paste and Match Style" menu probe has no Windows counterpart
/// (Ctrl+Shift+V means different things per app), so strip-and-restore is
/// always used.
/// </summary>
public sealed class PastePlainService
{
    private readonly ClipboardWatcher _watcher;
    private readonly TransientPaste _transient;
    private readonly ShortcutManager _shortcuts;
    private readonly ISettingsStore _settings;
    private bool _forwarding;

    public PastePlainService(ClipboardWatcher watcher, TransientPaste transient, ShortcutManager shortcuts, ISettingsStore settings)
    {
        _watcher = watcher;
        _transient = transient;
        _shortcuts = shortcuts;
        _settings = settings;
    }

    /// <summary>The plain text a paste would use, or null for media copies and empty clipboards.</summary>
    public static string? PlainText(ClipboardContent content)
    {
        // A secret (password manager copy) is never read: it is forwarded as a normal paste.
        if (content.IsConcealed || ClipboardFormats.HasMedia(content.Formats))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(content.Text))
        {
            return content.Text;
        }

        if (!string.IsNullOrEmpty(content.Rtf))
        {
            var text = RichText.RtfToText(content.Rtf);
            if (text.Length > 0)
            {
                return text;
            }
        }

        if (!string.IsNullOrEmpty(content.Html))
        {
            var text = RichText.HtmlToText(RichText.HtmlFragment(content.Html));
            if (text.Length > 0)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>Runs on the shortcut. Never overlaps with itself.</summary>
    public async Task PasteAsync()
    {
        if (_forwarding)
        {
            return;
        }

        _forwarding = true;
        try
        {
            var (content, completed) = await _watcher.Lane.RunAsync(TimeSpan.FromSeconds(2),
                c => c.Read(ClipboardReadParts.Text | ClipboardReadParts.Rtf | ClipboardReadParts.Html)).ConfigureAwait(true);
            if (!completed || content is null)
            {
                return;
            }

            var plain = PlainText(content);
            var bareCtrlV = IsBareCtrlV();
            if (bareCtrlV)
            {
                // A shortcut of plain Ctrl+V would fire on our own injected Ctrl+V.
                _shortcuts.Suspend();
            }

            try
            {
                if (string.IsNullOrEmpty(plain))
                {
                    await _transient.PasteCurrentContentsAsync().ConfigureAwait(true);
                    return;
                }

                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_transient.TryPaste(plain, null, null, ok => done.TrySetResult(ok)))
                {
                    return;
                }

                await done.Task.ConfigureAwait(true);
            }
            finally
            {
                if (bareCtrlV)
                {
                    _shortcuts.Resume();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("pastePlain", "Paste as plain text failed.", ex);
        }
        finally
        {
            _forwarding = false;
        }
    }

    private bool IsBareCtrlV() =>
        KeyChord.TryParse(_settings.Get(ClipboardSettings.PastePlainShortcut), out var chord)
        && chord == new KeyChord(KeyModifiers.Control, 'V');
}
