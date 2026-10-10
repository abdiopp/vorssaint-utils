// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules.Shelf;

namespace Rivet.App.Features.Shelf;

/// <summary>
/// Reads a drop into the parser's entries (spec 07 §3.1.10). A Windows OLE
/// drop is one data object, so it becomes one entry: file paths, else
/// bitmap data (stored as PNG), else the browser's <c>UniformResourceLocatorW</c>
/// link, else plain text. Virtual files (<c>FileGroupDescriptorW</c> +
/// <c>FileContents</c>, mail attachments) are not readable through the UI
/// framework's drop API and are refused (docs/modules/shelf.md).
/// </summary>
public static class ShelfDropReader
{
    private static readonly DataFormat<byte[]> UrlFormat = DataFormat.CreateBytesPlatformFormat("UniformResourceLocatorW");
    private static readonly DataFormat<byte[]> GifFormat = DataFormat.CreateBytesPlatformFormat("GIF");

    public static bool CanAccept(IDataTransfer data) =>
        data.Contains(DataFormat.File) || data.Contains(DataFormat.Bitmap) || data.Contains(UrlFormat) || data.Contains(GifFormat)
        || (data.Contains(DataFormat.Text) && !string.IsNullOrWhiteSpace(SafeText(data)));

    public static IReadOnlyList<ShelfDropEntry> Read(IDataTransfer data)
    {
        try
        {
            var files = data.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            var url = ReadUrl(data);
            byte[]? gif = data.Contains(GifFormat) ? data.TryGetValue(GifFormat) : null;
            byte[]? image = null;
            if (files.Count == 0 && gif is null && data.TryGetBitmap() is { } bitmap)
            {
                using (bitmap)
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                    image = stream.ToArray();
                }
            }

            return [new ShelfDropEntry { Files = files, GifData = gif, ImageData = image, Url = url, Text = SafeText(data) }];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("shelf", "Reading a drop failed.", ex);
            return [];
        }
    }

    private static string? ReadUrl(IDataTransfer data)
    {
        if (!data.Contains(UrlFormat) || data.TryGetValue(UrlFormat) is not { Length: > 1 } bytes)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? SafeText(IDataTransfer data)
    {
        try
        {
            return data.TryGetText();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
