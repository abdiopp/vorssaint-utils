// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Clipboard;
using Rivet.Core.Modules;

namespace Rivet.Platform.Fake.Clipboard;

/// <summary>
/// An in-memory clipboard for the development build and UI tests. Work posted
/// to the "clipboard thread" runs on a background thread, in order, like the
/// real lane; tests can push copies with <see cref="Copy"/>.
/// </summary>
public sealed class FakeClipboardPlatform : IClipboardPlatform, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private ClipboardContent _content = ClipboardContent.Empty;
    private uint _sequence = 1;

    public FakeClipboardPlatform()
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Rivet.Core.Diagnostics.Log.Error("clipboard", "Fake clipboard work failed.", ex);
                }
            }
        })
        { IsBackground = true, Name = "FakeClipboardLane" };
        _thread.Start();
    }

    public event EventHandler? Changed;

    public bool Listening { get; private set; }

    public uint SequenceNumber
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public void Post(Action work) => _queue.Add(work);

    public void StartListening() => Listening = true;

    public void StopListening() => Listening = false;

    /// <summary>Simulates another app copying (dev build and tests).</summary>
    public void Copy(ClipboardContent content)
    {
        lock (_gate)
        {
            _sequence++;
            _content = content with { Sequence = _sequence };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ClipboardContent Read(ClipboardReadParts parts)
    {
        lock (_gate)
        {
            if (_content.IsConcealed)
            {
                return new ClipboardContent { Sequence = _sequence, Formats = _content.Formats, IsConcealed = true };
            }

            return _content with
            {
                Sequence = _sequence,
                Text = parts.HasFlag(ClipboardReadParts.Text) ? _content.Text : null,
                Html = parts.HasFlag(ClipboardReadParts.Html) ? _content.Html : null,
                Rtf = parts.HasFlag(ClipboardReadParts.Rtf) ? _content.Rtf : null,
                Url = parts.HasFlag(ClipboardReadParts.Url) ? _content.Url : null,
                Png = parts.HasFlag(ClipboardReadParts.Image) ? _content.Png : null,
                Files = parts.HasFlag(ClipboardReadParts.Files) ? _content.Files : null,
                OwnerApp = parts.HasFlag(ClipboardReadParts.Owner) ? _content.OwnerApp : null,
            };
        }
    }

    public bool Write(ClipboardWriteData data, ClipboardWriteMarks marks)
    {
        var formats = new List<string>();
        if (data.Text is not null) formats.AddRange([ClipboardFormats.UnicodeText, ClipboardFormats.Text, ClipboardFormats.Locale]);
        if (data.Url is not null) formats.Add(ClipboardFormats.UrlW);
        if (data.HtmlFragment is not null) formats.Add(ClipboardFormats.Html);
        if (data.Rtf is not null) formats.Add(ClipboardFormats.Rtf);
        if (data.Png is not null) formats.AddRange([ClipboardFormats.Png, "CF_DIBV5"]);
        if (data.Files is not null) formats.Add(ClipboardFormats.Hdrop);
        if (marks.HasFlag(ClipboardWriteMarks.OwnSource)) formats.Add(ClipboardFormats.OwnSource);
        if (marks.HasFlag(ClipboardWriteMarks.Transient)) formats.Add(ClipboardFormats.ExcludeFromMonitors);
        lock (_gate)
        {
            _sequence++;
            _content = new ClipboardContent
            {
                Sequence = _sequence,
                Formats = formats,
                Text = data.Text,
                Url = data.Url,
                Html = data.HtmlFragment,
                Rtf = data.Rtf,
                Png = data.Png,
                Files = data.Files,
                IsOwnWrite = marks.HasFlag(ClipboardWriteMarks.OwnSource),
                IsConcealed = marks.HasFlag(ClipboardWriteMarks.Transient),
            };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Clear()
    {
        lock (_gate)
        {
            _sequence++;
            _content = new ClipboardContent { Sequence = _sequence };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public ClipboardSnapshot? Snapshot(long maxBytes)
    {
        lock (_gate)
        {
            return new FakeSnapshot(_content);
        }
    }

    public bool Restore(ClipboardSnapshot snapshot, ClipboardWriteMarks marks)
    {
        if (snapshot is not FakeSnapshot fake)
        {
            return false;
        }

        lock (_gate)
        {
            _sequence++;
            _content = fake.Content with { Sequence = _sequence };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Dispose() => _queue.CompleteAdding();

    private sealed class FakeSnapshot(ClipboardContent content) : ClipboardSnapshot
    {
        public ClipboardContent Content { get; } = content;

        public override long ByteCount => (Content.Text?.Length ?? 0) * 2L + (Content.Png?.Length ?? 0);
    }
}

/// <summary>Session events never fire in the development build; tests raise them.</summary>
public sealed class FakeSessionEvents : ISessionEvents
{
    public event EventHandler<SessionEventKind>? Occurred;

    public void Raise(SessionEventKind kind) => Occurred?.Invoke(this, kind);
}

/// <summary>A pretend foreground app ("Notepad"); activation always succeeds instantly.</summary>
public sealed class FakeForegroundService : IForegroundService
{
    private int _tracking;

    public ForegroundApp? App { get; set; } = new()
    {
        Window = 0x1234,
        ProcessId = 4242,
        Identity = "c:\\windows\\system32\\notepad.exe",
        Name = "Notepad",
    };

    public bool IsPasswordFieldFocused { get; set; }

    public bool IsTracking => Volatile.Read(ref _tracking) > 0;

    public event EventHandler? FocusChanged;

    public ForegroundApp? Current() => App;

    public Task<bool> ActivateAsync(ForegroundApp target, TimeSpan timeout) => Task.FromResult(true);

    public IDisposable TrackFocus()
    {
        Interlocked.Increment(ref _tracking);
        return new Release(() => Interlocked.Decrement(ref _tracking));
    }

    public void Beep() => Rivet.Core.Diagnostics.Log.Info("clipboard", "[fake] beep");

    public IReadOnlyList<RunningApp> RunningApps() =>
    [
        new("c:\\windows\\system32\\notepad.exe", "Notepad", "C:\\Windows\\System32\\notepad.exe"),
        new("c:\\program files\\keepass\\keepass.exe", "KeePass", "C:\\Program Files\\KeePass\\KeePass.exe"),
    ];

    public void RaiseFocusChanged() => FocusChanged?.Invoke(this, EventArgs.Empty);

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

public sealed class FakeClipboardRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeClipboardPlatform>();
        services.AddSingleton<IClipboardPlatform>(sp => sp.GetRequiredService<FakeClipboardPlatform>());
        services.AddSingleton<FakeSessionEvents>();
        services.AddSingleton<ISessionEvents>(sp => sp.GetRequiredService<FakeSessionEvents>());
        services.AddSingleton<FakeForegroundService>();
        services.AddSingleton<IForegroundService>(sp => sp.GetRequiredService<FakeForegroundService>());
    }
}
