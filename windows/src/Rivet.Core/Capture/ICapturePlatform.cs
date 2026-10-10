// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Capture;

/// <summary>What to keep out of (or put into) captured pixels.</summary>
public sealed record DisplayCaptureOptions
{
    public bool IncludeCursor { get; init; }

    /// <summary>Keep every window of this app out of the pixels for the duration of the capture.</summary>
    public bool HideOwnWindows { get; init; }

    /// <summary>Further windows of this app to keep out (pinned captures while recording).</summary>
    public IReadOnlyCollection<nint> ExcludedWindows { get; init; } = [];
}

/// <summary>
/// Pixel acquisition (spec 01 §3.5). Windows: Windows.Graphics.Capture with
/// GDI fallbacks; the fake renders a synthetic desktop. Buffers come back at
/// the display's native resolution with <see cref="PixelBuffer.Scale"/> set.
/// </summary>
public interface IScreenCapturer
{
    /// <summary>Photographs each display; a display that failed is missing from the result.</summary>
    Task<IReadOnlyDictionary<string, PixelBuffer>> CaptureDisplaysAsync(IReadOnlyList<ScreenInfo> displays, DisplayCaptureOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// The window's own pixels (even when covered), with its attached dialogs
    /// drawn over it (back to front). Null when every method failed.
    /// </summary>
    Task<PixelBuffer?> CaptureWindowAsync(CaptureWindowInfo window, IReadOnlyList<CaptureWindowInfo> attached, DisplayCaptureOptions options, CancellationToken cancellationToken = default);

    /// <summary>A repeated capture of one rectangle (scrolling capture); null when the rectangle is unusable.</summary>
    IRegionCapture? CreateRegionCapture(ScreenInfo display, PixelRect rect, DisplayCaptureOptions options);
}

/// <summary>Cheap repeated frames of a fixed rectangle. Not thread-safe; dispose when done.</summary>
public interface IRegionCapture : IDisposable
{
    PixelBuffer? CaptureFrame();
}

/// <summary>The on-screen windows that can be picked.</summary>
public interface IWindowEnumerator
{
    /// <summary>
    /// Top-level windows front to back, already without invisible, cloaked
    /// (other virtual desktops), minimized, tool and shell windows.
    /// </summary>
    IReadOnlyList<CaptureWindowInfo> EnumerateWindows();
}

/// <summary>An image read from the clipboard.</summary>
public sealed record ClipboardImage(PixelBuffer Image, string? SourceFile);

/// <summary>
/// Clipboard writes for captures: one item with the PNG, a bitmap and the
/// file (so apps that paste files get one too), marked as coming from this
/// app so clipboard history can recognize copied screenshots.
/// </summary>
public interface ICaptureClipboard
{
    /// <summary>Increments whenever the clipboard changes (to discard stale automatic copies).</summary>
    long ChangeCount { get; }

    bool SetImage(PixelBuffer image, byte[] png, string? filePath);

    bool SetText(string text);

    /// <summary>Prefers an image file, then PNG data, then a bitmap. Images over <paramref name="maxPixels"/> are refused.</summary>
    ClipboardImage? ReadImage(long maxPixels);
}

/// <summary>Whether text recognition can run, and in which languages.</summary>
public sealed record OcrStatus(bool IsAvailable, IReadOnlyList<string> LanguageNames);

/// <summary>On-device text recognition (Windows.Media.Ocr).</summary>
public interface IOcrEngine
{
    OcrStatus GetStatus();

    /// <summary>Recognizes lines and words; null when no recognizer is available.</summary>
    Task<OcrPage?> RecognizeAsync(PixelBuffer image, IReadOnlyList<string> preferredLanguageTags, CancellationToken cancellationToken = default);
}

/// <summary>Small OS services the capture tools need.</summary>
public interface ICapturePlatform
{
    /// <summary>The Windows "Screenshots" known folder (Pictures\Screenshots), created on demand.</summary>
    string DefaultScreenshotFolder { get; }

    /// <summary>Moves the mouse pointer (loupe nudging), physical pixels.</summary>
    void SetCursorPosition(PixelPoint point);

    /// <summary>Puts a window exactly over a physical-pixel rectangle, top-most, without activating it.</summary>
    void PlaceWindow(nint hwnd, PixelRect bounds);

    /// <summary>The system "error" sound used for failures.</summary>
    void Beep();

    /// <summary>
    /// Opens the Windows share sheet for a file. <paramref name="completed"/>
    /// reports whether a target app was chosen. Returns false when sharing is unavailable.
    /// </summary>
    bool ShareFile(nint ownerWindow, string filePath, string title, Action<bool>? completed);

    /// <summary>Lets mouse input pass through a top-level window (layered + transparent, kept fully opaque).</summary>
    void SetClickThrough(nint hwnd, bool enabled);
}
