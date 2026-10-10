// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Clipboard;
using Rivet.Core.Platform;

namespace Rivet.Core.Tests.Clipboard;

/// <summary>An in-memory clipboard whose lane runs inline, with a GetClipboardSequenceNumber-like counter.</summary>
internal sealed class TestClipboard : IClipboardPlatform
{
    private sealed class Snap(ClipboardContent content) : ClipboardSnapshot
    {
        public ClipboardContent Content { get; } = content;

        public override long ByteCount => (Content.Text?.Length ?? 0) * 2;
    }

    public ClipboardContent Content { get; private set; } = ClipboardContent.Empty;

    public uint SequenceNumber { get; private set; } = 1;

    public bool Listening { get; private set; }

    public bool FailSnapshot { get; set; }

    public List<ClipboardWriteMarks> WriteMarks { get; } = [];

    public event EventHandler? Changed;

    public void Post(Action work) => work();

    public void StartListening() => Listening = true;

    public void StopListening() => Listening = false;

    /// <summary>Simulates another app copying.</summary>
    public void Copy(ClipboardContent content)
    {
        SequenceNumber++;
        Content = content with { Sequence = SequenceNumber };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CopyText(string text, string? owner = "c:\\apps\\editor.exe") =>
        Copy(new ClipboardContent { Text = text, Formats = ["CF_UNICODETEXT", "CF_TEXT", "CF_LOCALE", "CF_OEMTEXT"], OwnerApp = owner });

    public ClipboardContent Read(ClipboardReadParts parts)
    {
        if (Content.IsConcealed)
        {
            return new ClipboardContent { Sequence = SequenceNumber, Formats = Content.Formats, IsConcealed = true };
        }

        return Content with
        {
            Sequence = SequenceNumber,
            Text = parts.HasFlag(ClipboardReadParts.Text) ? Content.Text : null,
            Png = parts.HasFlag(ClipboardReadParts.Image) ? Content.Png : null,
            Files = parts.HasFlag(ClipboardReadParts.Files) ? Content.Files : null,
            OwnerApp = parts.HasFlag(ClipboardReadParts.Owner) ? Content.OwnerApp : null,
        };
    }

    public bool Write(ClipboardWriteData data, ClipboardWriteMarks marks)
    {
        WriteMarks.Add(marks);
        var formats = new List<string>();
        if (data.Text is not null) formats.Add("CF_UNICODETEXT");
        if (data.Url is not null) formats.Add("UniformResourceLocatorW");
        if (data.Png is not null) formats.Add("PNG");
        if (data.Files is not null) formats.Add("CF_HDROP");
        if (data.HtmlFragment is not null) formats.Add("HTML Format");
        if (marks.HasFlag(ClipboardWriteMarks.OwnSource)) formats.Add(ClipboardFormats.OwnSource);
        SequenceNumber++;
        Content = new ClipboardContent
        {
            Sequence = SequenceNumber,
            Text = data.Text,
            Url = data.Url,
            Png = data.Png,
            Files = data.Files,
            Formats = formats,
            IsOwnWrite = marks.HasFlag(ClipboardWriteMarks.OwnSource),
            Html = data.HtmlFragment,
            Rtf = data.Rtf,
        };
        return true;
    }

    public bool Clear()
    {
        SequenceNumber++;
        Content = new ClipboardContent { Sequence = SequenceNumber };
        return true;
    }

    public ClipboardSnapshot? Snapshot(long maxBytes) => FailSnapshot ? null : new Snap(Content);

    public bool Restore(ClipboardSnapshot snapshot, ClipboardWriteMarks marks)
    {
        SequenceNumber++;
        Content = ((Snap)snapshot).Content with { Sequence = SequenceNumber };
        return true;
    }
}

internal sealed class TestForeground : IForegroundService
{
    public ForegroundApp? App { get; set; } = new() { Window = 42, ProcessId = 7, Identity = "c:\\apps\\editor.exe", Name = "Editor" };

    public bool IsPasswordFieldFocused { get; set; }

    public int Beeps { get; private set; }

    public event EventHandler? FocusChanged;

    public ForegroundApp? Current() => App;

    public Task<bool> ActivateAsync(ForegroundApp target, TimeSpan timeout) => Task.FromResult(true);

    public IDisposable TrackFocus() => new Nothing();

    public void Beep() => Beeps++;

    public IReadOnlyList<RunningApp> RunningApps() => [];

    public void RaiseFocusChanged() => FocusChanged?.Invoke(this, EventArgs.Empty);

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class TestInputHooks : IInputHooks
{
    public List<string> Sent { get; } = [];

    public HashSet<int> Down { get; } = [];

    private readonly List<KeyboardHookHandler> _keyboard = [];
    private readonly List<MouseHookHandler> _mouse = [];

    public IDisposable SubscribeKeyboard(KeyboardHookHandler handler, int priority = 0)
    {
        _keyboard.Add(handler);
        return new Sub(() => _keyboard.Remove(handler));
    }

    public IDisposable SubscribeMouse(MouseHookHandler handler, int priority = 0)
    {
        _mouse.Add(handler);
        return new Sub(() => _mouse.Remove(handler));
    }

    public int KeyboardSubscribers => _keyboard.Count;

    public bool Press(KeyboardHookEvent e)
    {
        foreach (var handler in _keyboard.ToArray())
        {
            if (handler(ref e))
            {
                return true;
            }
        }

        return false;
    }

    public void Click()
    {
        var e = new MouseHookEvent { Kind = MouseHookKind.LeftDown };
        foreach (var handler in _mouse.ToArray())
        {
            handler(ref e);
        }
    }

    public void SendKeys(IReadOnlyList<(int VirtualKey, KeyAction Action)> strokes) =>
        Sent.Add("keys:" + string.Join(',', strokes.Select(s => $"{s.VirtualKey:X2}{(s.Action == KeyAction.Down ? "d" : "u")}")));

    public void SendText(string text) => Sent.Add("text:" + text);

    public void SendWheel(int delta, bool horizontal)
    {
    }

    public void SendMouse(MouseHookKind kind, int xButton = 0)
    {
    }

    public bool IsKeyDown(int virtualKey) => Down.Contains(virtualKey);

    private sealed class Sub(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
