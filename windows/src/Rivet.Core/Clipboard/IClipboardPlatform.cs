// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Clipboard;

[Flags]
public enum ClipboardReadParts
{
    /// <summary>Only the format list, the concealed check and the sequence number.</summary>
    None = 0,
    Text = 1,
    Html = 2,
    Rtf = 4,
    Url = 8,
    Image = 16,
    Files = 32,

    /// <summary>The app that owns the clipboard (GetClipboardOwner → process).</summary>
    Owner = 64,
    All = Text | Html | Rtf | Url | Image | Files | Owner,
}

/// <summary>What one read of the OS clipboard found. Secrets are never read: <see cref="IsConcealed"/> wins.</summary>
public sealed record ClipboardContent
{
    public static ClipboardContent Empty { get; } = new();

    public uint Sequence { get; init; }

    /// <summary>Every format on the clipboard, predefined ones by their CF_ name.</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];

    /// <summary>A password manager (or any writer) excluded the copy from clipboard managers.</summary>
    public bool IsConcealed { get; init; }

    /// <summary>This app wrote it (its private source marker is present).</summary>
    public bool IsOwnWrite { get; init; }

    public string? Text { get; init; }

    /// <summary>The raw CF_HTML payload (header plus document).</summary>
    public string? Html { get; init; }

    public string? Rtf { get; init; }

    /// <summary>First link of the URL flavours (UniformResourceLocatorW, text/x-moz-url).</summary>
    public string? Url { get; init; }

    /// <summary>PNG bytes (the PNG format, or a DIB converted to PNG), at most 16 MiB.</summary>
    public byte[]? Png { get; init; }

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    /// <summary>CF_HDROP paths, in order.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>Identity of the clipboard owner's app (lower-case exe path or AUMID), when known.</summary>
    public string? OwnerApp { get; init; }
}

/// <summary>Content to put on the clipboard. Several flavours are written together.</summary>
public sealed record ClipboardWriteData
{
    public string? Text { get; init; }

    /// <summary>Also written as UniformResourceLocator(W) so browsers and Office see a link.</summary>
    public string? Url { get; init; }

    public byte[]? Png { get; init; }

    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>An HTML fragment, wrapped in the CF_HTML header by the platform.</summary>
    public string? HtmlFragment { get; init; }

    public string? Rtf { get; init; }

    public static ClipboardWriteData FromText(string text) => new() { Text = text };
}

[Flags]
public enum ClipboardWriteMarks
{
    None = 0,

    /// <summary>Adds the app's private source marker (history attributes it to no app).</summary>
    OwnSource = 1,

    /// <summary>
    /// A write that is about to be undone (transient paste and its restore):
    /// adds ExcludeClipboardContentFromMonitorProcessing, CanIncludeInClipboardHistory = 0
    /// and CanUploadToCloudClipboard = 0 so Win+V history and other managers ignore it.
    /// </summary>
    Transient = 2,
}

/// <summary>An opaque copy of every clipboard format, taken to restore it later.</summary>
public abstract class ClipboardSnapshot
{
    public abstract long ByteCount { get; }
}

/// <summary>
/// The OS clipboard. Every member except <see cref="Post"/>, the listener
/// switches and <see cref="Changed"/> must run on the clipboard thread: the
/// one serial worker through which all clipboard access goes, because a
/// clipboard owner rendering data lazily can stop answering and hang its
/// reader (spec 06 §3.1.1). Nothing on the UI thread ever waits for it.
/// </summary>
public interface IClipboardPlatform
{
    /// <summary>Queues work on the clipboard thread, in order.</summary>
    void Post(Action work);

    /// <summary>Raised (on any thread) after the clipboard content changed (WM_CLIPBOARDUPDATE).</summary>
    event EventHandler? Changed;

    void StartListening();

    void StopListening();

    /// <summary>GetClipboardSequenceNumber.</summary>
    uint SequenceNumber { get; }

    ClipboardContent Read(ClipboardReadParts parts);

    bool Write(ClipboardWriteData data, ClipboardWriteMarks marks);

    bool Clear();

    /// <summary>Copies every format; null when one cannot be preserved or the total exceeds <paramref name="maxBytes"/>.</summary>
    ClipboardSnapshot? Snapshot(long maxBytes);

    bool Restore(ClipboardSnapshot snapshot, ClipboardWriteMarks marks);
}

/// <summary>Session events auto clear listens to.</summary>
public enum SessionEventKind
{
    /// <summary>The PC is going to sleep (PBT_APMSUSPEND).</summary>
    Sleep,

    /// <summary>The displays turned off (GUID_CONSOLE_DISPLAY_STATE = 0).</summary>
    DisplaySleep,

    /// <summary>The session was locked (WTS_SESSION_LOCK).</summary>
    Lock,

    Unlock,

    /// <summary>Fast user switching away from this session.</summary>
    ConsoleDisconnect,

    ConsoleConnect,
}

public interface ISessionEvents
{
    /// <summary>Raised on the UI thread.</summary>
    event EventHandler<SessionEventKind>? Occurred;
}
