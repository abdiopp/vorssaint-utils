// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Core.Modules.Shelf;

public enum ShelfLoadOutcome
{
    /// <summary>Everything stored decoded (or nothing was stored).</summary>
    Items,

    /// <summary>Some entries were unreadable and were skipped.</summary>
    Partial,

    /// <summary>The stored value is not an array, or a non-empty array where nothing decodes.</summary>
    Unreadable,
}

public sealed record ShelfLoadResult(ShelfLoadOutcome Outcome, IReadOnlyList<ShelfItem> Items);

/// <summary>Callbacks the restore pass uses to check files on disk.</summary>
public sealed record ShelfRestoreContext
{
    public Func<string, bool> Exists { get; init; } = p => File.Exists(p) || Directory.Exists(p);

    /// <summary>The drive or UNC root is present (an absent one means the drive is just not mounted: keep the item).</summary>
    public Func<string, bool> RootAvailable { get; init; } = DefaultRootAvailable;

    /// <summary>Finds a moved file again from its bookmark; returns the new path or null.</summary>
    public Func<ShelfItem, string?> Heal { get; init; } = _ => null;

    public static bool DefaultRootAvailable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) || Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// The shelf's saved items (spec 07 §5.1). There is exactly one decoder, and
/// it reports one of three outcomes; restore then sanitizes: at most 200
/// leaves and nesting below 4, missing files healed or dropped (files on an
/// unmounted drive are kept), blank texts and file: links dropped, piles of
/// one replaced by their child (which inherits the pin).
/// </summary>
public static class ShelfCodec
{
    public static byte[] Encode(IReadOnlyList<ShelfItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(EncodeItem(item));
        }

        return JsonSerializer.SerializeToUtf8Bytes(array, new JsonSerializerOptions { WriteIndented = false });
    }

    public static ShelfLoadResult Decode(byte[]? stored)
    {
        if (stored is null || stored.Length == 0)
        {
            return new ShelfLoadResult(ShelfLoadOutcome.Items, []);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(stored);
        }
        catch (JsonException)
        {
            return new ShelfLoadResult(ShelfLoadOutcome.Unreadable, []);
        }

        if (root is not JsonArray array)
        {
            return new ShelfLoadResult(ShelfLoadOutcome.Unreadable, []);
        }

        var counter = new Counter();
        var items = DecodeList(array, counter);
        if (array.Count > 0 && items.Count == 0)
        {
            return new ShelfLoadResult(ShelfLoadOutcome.Unreadable, []);
        }

        return new ShelfLoadResult(counter.Failed == 0 ? ShelfLoadOutcome.Items : ShelfLoadOutcome.Partial, items);
    }

    /// <summary>Applies the restore rules; items that no longer fit the 200-leaf cap are dropped whole.</summary>
    public static IReadOnlyList<ShelfItem> Sanitize(IReadOnlyList<ShelfItem> items, ShelfRestoreContext context)
    {
        var result = new List<ShelfItem>();
        var leaves = 0;
        foreach (var item in items)
        {
            if (SanitizeItem(item, context, depth: 0) is not { } clean)
            {
                continue;
            }

            var count = ShelfTree.LeafCount(clean);
            if (leaves + count > ShelfConstants.MaxLeaves)
            {
                continue;
            }

            leaves += count;
            result.Add(clean);
        }

        return result;
    }

    /// <summary>Restored items go before anything added while the restore ran; the total stays within 200 (restored items that don't fit are dropped whole).</summary>
    public static IReadOnlyList<ShelfItem> MergeRestored(IReadOnlyList<ShelfItem> restored, IReadOnlyList<ShelfItem> addedMeanwhile)
    {
        var budget = ShelfConstants.MaxLeaves - ShelfTree.LeafCount(addedMeanwhile);
        var kept = new List<ShelfItem>();
        foreach (var item in restored)
        {
            var count = ShelfTree.LeafCount(item);
            if (count <= budget)
            {
                kept.Add(item);
                budget -= count;
            }
        }

        return [.. kept, .. addedMeanwhile];
    }

    private static ShelfItem? SanitizeItem(ShelfItem item, ShelfRestoreContext context, int depth)
    {
        switch (item.Kind)
        {
            case ShelfItemKind.File:
                if (string.IsNullOrEmpty(item.Path))
                {
                    return null;
                }

                if (context.Exists(item.Path) || !context.RootAvailable(item.Path))
                {
                    return item.WithTitleFromPayload();
                }

                var healed = context.Heal(item);
                return healed is null ? null : item with { Path = healed, Title = Path.GetFileName(healed), Revision = item.Revision + 1 };

            case ShelfItemKind.Text:
                if (string.IsNullOrWhiteSpace(item.Text))
                {
                    return null;
                }

                var text = item.Text.Length > ShelfConstants.MaxTextLength ? item.Text[..ShelfConstants.MaxTextLength] : item.Text;
                return (item with { Text = text }).WithTitleFromPayload();

            case ShelfItemKind.Link:
                return item.Url is not null && Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile
                    ? item.WithTitleFromPayload()
                    : null;

            case ShelfItemKind.Batch:
                if (depth + 1 >= ShelfConstants.RestoreDepth)
                {
                    return null;
                }

                var children = item.Children.Select(c => SanitizeItem(c, context, depth + 1)).OfType<ShelfItem>().ToList();
                if (children.Count == 0)
                {
                    return null;
                }

                if (children.Count == 1)
                {
                    var only = children[0];
                    return item.Pinned && !only.Pinned ? only with { Pinned = true } : only;
                }

                return item with { Children = children, Title = item.Title.Length > 0 ? item.Title : ShelfTree.PileTitle(children) };

            default:
                return null;
        }
    }

    private static JsonObject EncodeItem(ShelfItem item)
    {
        var obj = new JsonObject
        {
            ["id"] = item.Id.ToString("D").ToUpperInvariant(),
            ["kind"] = item.Kind switch
            {
                ShelfItemKind.File => "file",
                ShelfItemKind.Text => "text",
                ShelfItemKind.Link => "link",
                _ => "batch",
            },
            ["title"] = item.Title,
        };
        switch (item.Kind)
        {
            case ShelfItemKind.File:
                obj["path"] = item.Path;
                if (item.Bookmark is { Length: > 0 } bookmark)
                {
                    obj["bookmark"] = bookmark;
                }

                break;
            case ShelfItemKind.Text:
                obj["text"] = item.Text;
                break;
            case ShelfItemKind.Link:
                obj["url"] = item.Url;
                break;
            case ShelfItemKind.Batch:
                var children = new JsonArray();
                foreach (var child in item.Children)
                {
                    children.Add(EncodeItem(child));
                }

                obj["children"] = children;
                break;
        }

        if (item.Pinned)
        {
            obj["pinned"] = true;
        }

        return obj;
    }

    private sealed class Counter
    {
        public int Failed { get; set; }
    }

    private static List<ShelfItem> DecodeList(JsonArray array, Counter counter)
    {
        var items = new List<ShelfItem>();
        foreach (var node in array)
        {
            if (DecodeItem(node, counter) is { } item)
            {
                items.Add(item);
            }
            else
            {
                counter.Failed++;
            }
        }

        return items;
    }

    private static ShelfItem? DecodeItem(JsonNode? node, Counter counter)
    {
        if (node is not JsonObject obj || Str(obj, "kind") is not { } kind)
        {
            return null;
        }

        var id = Str(obj, "id") is { } idText && Guid.TryParse(idText, out var parsed) ? parsed : Guid.NewGuid();
        var title = Str(obj, "title") ?? string.Empty;
        var pinned = obj["pinned"] is JsonValue pinnedValue && pinnedValue.TryGetValue<bool>(out var p) && p;
        switch (kind)
        {
            case "file":
                return Str(obj, "path") is { Length: > 0 } path
                    ? new ShelfItem { Id = id, Kind = ShelfItemKind.File, Path = path, Bookmark = Str(obj, "bookmark"), Title = title, Pinned = pinned }
                    : null;
            case "text":
                return Str(obj, "text") is { } text
                    ? new ShelfItem { Id = id, Kind = ShelfItemKind.Text, Text = text, Title = title, Pinned = pinned }
                    : null;
            case "link":
                return Str(obj, "url") is { } url
                    ? new ShelfItem { Id = id, Kind = ShelfItemKind.Link, Url = url, Title = title, Pinned = pinned }
                    : null;
            case "batch":
                if (obj["children"] is not JsonArray children)
                {
                    return null;
                }

                var decoded = DecodeList(children, counter);
                return decoded.Count == 0 && children.Count > 0
                    ? null
                    : new ShelfItem { Id = id, Kind = ShelfItemKind.Batch, Children = decoded, Title = title, Pinned = pinned };
            default:
                return null;
        }
    }

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
