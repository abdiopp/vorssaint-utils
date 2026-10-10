// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Clipboard;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Util;

namespace Rivet.Core.Snippets;

/// <summary>
/// Typed-trigger expansion (spec 06 §3.6.2): one subscription on the shared
/// keyboard hook feeds <see cref="SnippetEngine"/>; a match swallows the last
/// keystroke and the expansion is typed (or pasted, for multi-line text) from
/// the UI thread. Runs only while the feature is installed, switched on and
/// at least one snippet is enabled. The buffer resets on every focus change,
/// on clicks, while a password field has the focus and while the library or
/// the Command Bar is open; it is never persisted or logged.
/// </summary>
public sealed class SnippetExpansionService : IFeatureController, IDisposable
{
    private const int VkProcessKey = 0xE5;
    private const int VkPacket = 0xE7;

    private readonly ISettingsStore _settings;
    private readonly IInputHooks _hooks;
    private readonly IKeyTranslator _translator;
    private readonly IForegroundService _foreground;
    private readonly TextInserter _inserter;
    private readonly TransientPaste _transient;
    private readonly ClipboardLane _lane;
    private readonly ISnippetSounds _sounds;
    private readonly SnippetEngine _engine = new();
    private readonly IDisposable _settingsSubscription;
    private IDisposable? _keyboard;
    private IDisposable? _mouse;
    private IDisposable? _focusTracking;
    private bool _available;
    private int _suspendCount;
    private int _resetGeneration;
    private int _seenGeneration;

    public SnippetExpansionService(ISettingsStore settings, IInputHooks hooks, IKeyTranslator translator, IForegroundService foreground,
        TextInserter inserter, TransientPaste transient, ClipboardLane lane, ISnippetSounds sounds)
    {
        _settings = settings;
        _hooks = hooks;
        _translator = translator;
        _foreground = foreground;
        _inserter = inserter;
        _transient = transient;
        _lane = lane;
        _sounds = sounds;
        _settingsSubscription = settings.Observe(() => UiThread.Run(Apply), SnippetSettings.Snippets, SnippetSettings.ExpansionEnabled);
    }

    public bool IsRunning => _keyboard is not null;

    /// <summary>Test and diagnostics view of the engine (never shown to the user).</summary>
    internal SnippetEngine Engine => _engine;

    public void Sync(bool available)
    {
        UiThread.Run(() =>
        {
            _available = available;
            Apply();
        });
    }

    /// <summary>Suspends expansion while the snippet library or the Command Bar is visible.</summary>
    public IDisposable Suspend()
    {
        Interlocked.Increment(ref _suspendCount);
        RequestReset();
        return new Release(() => Interlocked.Decrement(ref _suspendCount));
    }

    private void Apply()
    {
        var snippets = _settings.Get(SnippetSettings.Snippets);
        _engine.Configure(snippets);
        var want = _available && _settings.Get(SnippetSettings.ExpansionEnabled) && _engine.HasSnippets;
        if (want && _keyboard is null)
        {
            _foreground.FocusChanged += OnFocusChanged;
            _focusTracking = _foreground.TrackFocus();
            _keyboard = _hooks.SubscribeKeyboard(OnKey, priority: 10);
            _mouse = _hooks.SubscribeMouse(OnMouse, priority: 10);
            Log.Info("snippets", "Snippet expansion started.");
        }
        else if (!want && _keyboard is not null)
        {
            _keyboard.Dispose();
            _mouse?.Dispose();
            _focusTracking?.Dispose();
            _keyboard = _mouse = _focusTracking = null;
            _foreground.FocusChanged -= OnFocusChanged;
            RequestReset();
            Log.Info("snippets", "Snippet expansion stopped.");
        }
    }

    private void OnFocusChanged(object? sender, EventArgs e) => RequestReset();

    private void RequestReset() => Interlocked.Increment(ref _resetGeneration);

    private bool OnMouse(ref MouseHookEvent e)
    {
        if (e.Kind is MouseHookKind.LeftDown or MouseHookKind.RightDown)
        {
            RequestReset();
        }

        return false;
    }

    /// <summary>Hook thread: classify, update the buffer, swallow on a match. Must stay O(1)-ish.</summary>
    private bool OnKey(ref KeyboardHookEvent e)
    {
        if (e.Action != KeyAction.Down)
        {
            return false;
        }

        var generation = Volatile.Read(ref _resetGeneration);
        if (generation != _seenGeneration)
        {
            _seenGeneration = generation;
            _engine.Reset();
        }

        if (Volatile.Read(ref _suspendCount) > 0 || _foreground.IsPasswordFieldFocused)
        {
            _engine.Reset();
            return false;
        }

        var key = Classify(ref e);
        var match = _engine.Process(key);
        if (match is null)
        {
            return false;
        }

        if (SyntheticInput.HasLineBreak(match.Snippet.Replacement) && _transient.IsBusy)
        {
            return false; // the paste path is busy: let the keystroke through, typing is never lost
        }

        UiThread.Post(() => _ = ExpandAsync(match));
        return true;
    }

    private SnippetKey Classify(ref KeyboardHookEvent e)
    {
        var vk = e.VirtualKey;
        switch (vk)
        {
            case VirtualKeys.Back:
                return new SnippetKey(SnippetKeyKind.Backspace);
            case VirtualKeys.Left or VirtualKeys.Right or VirtualKeys.Up or VirtualKeys.Down or VirtualKeys.Escape
                or VirtualKeys.Home or VirtualKeys.End or VirtualKeys.Prior or VirtualKeys.Next or VirtualKeys.Delete
                or VkProcessKey:
                return new SnippetKey(SnippetKeyKind.Navigation);
            case VkPacket:
                // Unicode injected by another app (touch keyboard, IME tools): the character is in the scan code.
                return new SnippetKey(SnippetKeyKind.Character, ((char)e.ScanCode).ToString(), 0);
        }

        if (VirtualKeys.IsModifier(vk) || vk is VirtualKeys.Capital or 0x90 or 0x91)
        {
            return new SnippetKey(SnippetKeyKind.Ignored);
        }

        var modifiers = e.Modifiers;
        var altGr = modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Alt);
        if (modifiers.HasFlag(KeyModifiers.Win) || (modifiers.HasFlag(KeyModifiers.Control) && !altGr) || (modifiers.HasFlag(KeyModifiers.Alt) && !altGr))
        {
            return new SnippetKey(SnippetKeyKind.Shortcut);
        }

        var text = _translator.Translate(vk, e.ScanCode, modifiers);
        if (text.Length == 0)
        {
            // AltGr chords that type nothing are shortcuts; dead keys change nothing.
            return new SnippetKey(altGr ? SnippetKeyKind.Shortcut : SnippetKeyKind.Ignored);
        }

        return new SnippetKey(SnippetKeyKind.Character, text, vk);
    }

    private async Task ExpandAsync(SnippetMatch match)
    {
        try
        {
            string? clipboard = null;
            if (SnippetVariables.UsesClipboard(match.Snippet.Replacement))
            {
                var (content, completed) = await _lane.RunAsync(TimeSpan.FromMilliseconds(300), c => c.Read(ClipboardReadParts.Text)).ConfigureAwait(true);
                clipboard = completed && content is { IsConcealed: false } ? content.Text : null;
            }

            var text = SnippetVariables.Expand(match.Snippet.Replacement, DateTimeOffset.Now, CultureInfo.CurrentCulture, TimeZoneInfo.Local, clipboard);
            var accepted = _inserter.Post(match.DeleteCount, text, match.TrailingKey, match.TrailingText, match.FailureText);
            if (!accepted)
            {
                // The keystroke was swallowed: give it back.
                if (match.TrailingKey is { } key)
                {
                    _inserter.Input.PostKey(key);
                }
                else
                {
                    _inserter.Input.TypeText(match.FailureText);
                }

                return;
            }

            if (_settings.Get(SnippetSettings.SoundEnabled))
            {
                var name = _settings.Get(SnippetSettings.SoundName);
                _ = Task.Run(() => _sounds.Play(name));
            }
        }
        catch (Exception ex)
        {
            Log.Error("snippets", "A snippet expansion failed.", ex);
        }
    }

    public void Dispose()
    {
        _settingsSubscription.Dispose();
        _keyboard?.Dispose();
        _mouse?.Dispose();
        _focusTracking?.Dispose();
        _foreground.FocusChanged -= OnFocusChanged;
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
