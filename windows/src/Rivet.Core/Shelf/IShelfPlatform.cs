// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Modules.Shelf;

/// <summary>
/// The Windows pieces the shelf needs (spec 07 §7.1): the window and app under
/// the pointer, the move/size loop and drag-image signals that separate a
/// content drag from a window move, file identities to heal moved files,
/// shell thumbnails and type names, the File Explorer selection, and the
/// system share sheet.
/// </summary>
public interface IShelfPlatform
{
    /// <summary>Full path of the executable owning the top-level window at <paramref name="point"/>, or null.</summary>
    string? ProcessPathAt(PixelPoint point);

    /// <summary>The window at <paramref name="point"/> belongs to this app.</summary>
    bool IsOwnWindowAt(PixelPoint point);

    /// <summary>Left and right buttons are swapped in Windows settings.</summary>
    bool MouseButtonsSwapped { get; }

    /// <summary>SM_CXDRAG / SM_CYDRAG in physical pixels.</summary>
    (int X, int Y) DragThreshold { get; }

    /// <summary>The primary button is physically down (watchdog for swallowed button-ups).</summary>
    bool IsPrimaryButtonDown();

    /// <summary>Calls back on the UI thread with true when a window move/resize loop starts and false when it ends.</summary>
    IDisposable WatchMoveSize(Action<bool> changed);

    /// <summary>
    /// A shell drag image is visible (apps that use IDragSourceHelper, such as
    /// File Explorer and browsers, show one during a real OLE drag). A strong
    /// hint for a content drag; false does not prove there is none.
    /// </summary>
    bool IsDragImageVisible();

    /// <summary>An identity that finds the file again after a move or rename (volume serial + file id), or null.</summary>
    string? CreateBookmark(string path);

    /// <summary>The current path of a bookmarked file, without UI and without mounting anything; null when gone.</summary>
    string? ResolveBookmark(string bookmark);

    /// <summary>Localized file type name ("PNG File"), or empty.</summary>
    string FileKind(string path);

    /// <summary>Shell thumbnail (images, videos, documents) or null.</summary>
    PixelBuffer? Thumbnail(string path, int sizePx);

    /// <summary>Shell icon of the file or folder.</summary>
    PixelBuffer? Icon(string path, int sizePx);

    /// <summary>The selected items of the File Explorer window in front (or of the desktop); null when Explorer is not in front.</summary>
    IReadOnlyList<string>? ForegroundExplorerSelection();

    /// <summary>Opens the Windows share sheet for files. False when sharing is not available.</summary>
    bool Share(nint ownerWindow, IReadOnlyList<string> paths, string title);

    /// <summary>Selects the files in File Explorer, one window per folder.</summary>
    void Reveal(IReadOnlyList<string> paths);

    void Beep();
}
