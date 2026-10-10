// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Modules.Shelf;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;

namespace Rivet.Platform.Fake.Shelf;

/// <summary>
/// Development stand-in: no global drag signals (drops onto the shelf still
/// work through the UI framework), image thumbnails decoded with SkiaSharp,
/// no Explorer selection and no share sheet.
/// </summary>
public sealed class FakeShelfPlatform : IShelfPlatform
{
    public List<string> Revealed { get; } = [];

    public int Beeps { get; private set; }

    public bool MouseButtonsSwapped => false;

    public (int X, int Y) DragThreshold => (4, 4);

    /// <summary>Tests can pretend a drag image is visible.</summary>
    public bool DragImageVisible { get; set; }

    public string? ProcessPathAt(PixelPoint point) => null;

    public bool IsOwnWindowAt(PixelPoint point) => false;

    public bool IsPrimaryButtonDown() => false;

    public IDisposable WatchMoveSize(Action<bool> changed) => new Token(() => { });

    public bool IsDragImageVisible() => DragImageVisible;

    public string? CreateBookmark(string path) => null;

    public string? ResolveBookmark(string bookmark) => null;

    public string FileKind(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return Directory.Exists(path) ? "File folder" : extension.Length == 0 ? "File" : $"{extension} File";
    }

    public PixelBuffer? Thumbnail(string path, int sizePx)
    {
        if (!ShelfFileTypes.IsImage(path))
        {
            return null;
        }

        using var image = SkiaConvert.LoadImage(path, sizePx);
        return image is null ? null : SkiaConvert.ToPixelBuffer(image);
    }

    public PixelBuffer? Icon(string path, int sizePx) => null;

    public IReadOnlyList<string>? ForegroundExplorerSelection() => null;

    public bool Share(nint ownerWindow, IReadOnlyList<string> paths, string title)
    {
        Log.Info("shelf", $"[fake] share {paths.Count} files");
        return false;
    }

    public void Reveal(IReadOnlyList<string> paths) => Revealed.AddRange(paths);

    public void Beep() => Beeps++;

    private sealed class Token(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class ShelfFakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeShelfPlatform>();
        services.AddSingleton<IShelfPlatform>(sp => sp.GetRequiredService<FakeShelfPlatform>());
    }
}
