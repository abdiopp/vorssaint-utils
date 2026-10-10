// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Clipboard;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Clipboard;

/// <summary>Small shared pieces of the clipboard surfaces.</summary>
internal static class ClipboardUi
{
    private static readonly ConcurrentDictionary<string, string> SourceNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Bitmap?> Thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> ThumbnailOrder = new();
    private static readonly SemaphoreSlim DecodeSlots = new(2);

    /// <summary>The monospace family of the shared <c>TextBlock.mono</c> style, for text boxes.</summary>
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Menlo, monospace");

    /// <summary>A readable app name for a stored identity (file description, else the executable name).</summary>
    public static string SourceName(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
        {
            return string.Empty;
        }

        return SourceNames.GetOrAdd(identity, id =>
        {
            if (id.Contains('!', StringComparison.Ordinal) && !id.Contains('\\', StringComparison.Ordinal))
            {
                // A packaged app's AppUserModelID: "Publisher.App_hash!App".
                var package = id[..id.IndexOf('!')];
                var underscore = package.IndexOf('_');
                var name = underscore > 0 ? package[..underscore] : package;
                return name[(name.LastIndexOf('.') + 1)..];
            }

            try
            {
                if (OperatingSystem.IsWindows() && File.Exists(id))
                {
                    var description = FileVersionInfo.GetVersionInfo(id).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        return description.Trim();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            var file = ClipboardEntry.FileName(id);
            var dot = file.LastIndexOf('.');
            var stem = dot > 0 ? file[..dot] : file;
            return stem.Length > 0 ? char.ToUpper(stem[0], CultureInfo.CurrentCulture) + stem[1..] : stem;
        });
    }

    public static string KindIcon(ClipboardEntry entry) => entry.Kind switch
    {
        ClipboardEntryKind.Image => "Image",
        ClipboardEntryKind.Files when entry.IsSingleImageFile => "Image",
        ClipboardEntryKind.Files => entry.FilePaths.Count == 1 ? "Document" : "DocumentCopy",
        _ => entry.Color is not null ? "Color" : "TextDescription",
    };

    public static string ShortTime(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        var time = local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        return local.Date == DateTime.Today ? time : local.ToString(culture.DateTimeFormat.ShortDatePattern, culture) + " " + time;
    }

    /// <summary>"Image · 640×480".</summary>
    public static string ImageCaption(ClipboardEntry entry) => $"{L.Get("clipboard.imageEntryLabel")} · {entry.DimensionsText}";

    /// <summary>"N files" or the single file name.</summary>
    public static string FilesTitle(ClipboardEntry entry) =>
        entry.FilePaths.Count == 1 ? ClipboardEntry.FileName(entry.FilePaths[0]) : L.Format("clipboard.fileCountFormat", entry.FilePaths.Count);

    /// <summary>A text block whose matched ranges are semibold in the accent colour.</summary>
    public static TextBlock Highlighted(string text, string query, double fontSize, int maxLines)
    {
        var block = new TextBlock { FontSize = fontSize, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = maxLines };
        var ranges = string.IsNullOrWhiteSpace(query) ? [] : ClipboardSearch.Highlights(text, query);
        if (ranges.Count == 0)
        {
            block.Text = text;
            return block;
        }

        var accent = Application.Current?.FindResource("AccentBrush") as IBrush;
        var position = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > position)
            {
                block.Inlines!.Add(new Run(text[position..start]));
            }

            block.Inlines!.Add(new Run(text.Substring(start, length)) { FontWeight = FontWeight.SemiBold, Foreground = accent });
            position = start + length;
        }

        if (position < text.Length)
        {
            block.Inlines!.Add(new Run(text[position..]));
        }

        return block;
    }

    public static Border Swatch(uint argb, double size = 14) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
        Background = new SolidColorBrush(Color.FromUInt32(argb)),
        VerticalAlignment = VerticalAlignment.Top,
    };

    public static SymbolIcon Icon(string name, double size = 14, string brush = "TextSecondaryBrush")
    {
        var icon = new SymbolIcon { Symbol = IconConverter.Parse(name), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable(brush).ToBinding());
        return icon;
    }

    public static Button IconButton(string icon, string tooltip, Action click, double size = 14)
    {
        var button = new Button { Classes = { "icon" }, Content = Icon(icon, size, "TextPrimaryBrush") };
        ToolTip.SetTip(button, tooltip);
        Avalonia.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Loads a downsampled thumbnail off the UI thread (2 at a time, 120 cached) into <paramref name="image"/>.</summary>
    public static void LoadThumbnail(Image image, string path, int maxWidth)
    {
        var key = $"{path}|{maxWidth}";
        lock (Thumbnails)
        {
            if (Thumbnails.TryGetValue(key, out var cached))
            {
                image.Source = cached;
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            await DecodeSlots.WaitAsync().ConfigureAwait(false);
            Bitmap? bitmap;
            try
            {
                bitmap = ImageInterop.LoadThumbnail(path, maxWidth);
            }
            finally
            {
                DecodeSlots.Release();
            }

            lock (Thumbnails)
            {
                Thumbnails[key] = bitmap;
                ThumbnailOrder.AddLast(key);
                while (ThumbnailOrder.Count > 120)
                {
                    var oldest = ThumbnailOrder.First!.Value;
                    ThumbnailOrder.RemoveFirst();
                    Thumbnails.Remove(oldest);
                }
            }

            Dispatcher.UIThread.Post(() => image.Source = bitmap);
        });
    }

    /// <summary>The thumbnail for an entry's picture: the stored PNG, or a single image file.</summary>
    public static string? PicturePath(ClipboardEntry entry, ClipboardImageStore store) => entry.Kind switch
    {
        ClipboardEntryKind.Image when entry.ImageFile is { } file => store.PathOf(file),
        ClipboardEntryKind.Files when entry.IsSingleImageFile => entry.FilePaths[0],
        _ => null,
    };
}
