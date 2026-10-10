// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Localization;

namespace Rivet.Core.Modules.Shelf;

/// <summary>The shelf presentation a drag came from.</summary>
public enum ShelfSurface
{
    Classic,
    Docked,
}

/// <summary>Drag-out rules (spec 07 §3.1.14).</summary>
public static class ShelfDragOutPolicy
{
    /// <summary>
    /// Outside the app: Copy + Move only when "Remove items after dropping" is on
    /// and no dragged leaf is protected; otherwise Copy only, so a destination can
    /// never move away a file the shelf keeps. (Inside the app tiles move.)
    /// </summary>
    public static bool AllowsMove(bool removeAfterDrop, bool anyProtected) => removeAfterDrop && !anyProtected;

    /// <summary>
    /// After the drag: if it was accepted and was not a merge onto another tile,
    /// remove the dragged unprotected items (when enabled) and close the source
    /// surface (when enabled and the surface is not pinned).
    /// </summary>
    public static (bool RemoveItems, bool CloseSurface) AfterDrop(bool accepted, bool merged, bool removeAfterDrop, bool closeAfterDrop, bool surfacePinned) =>
        accepted && !merged ? (removeAfterDrop, closeAfterDrop && !surfacePinned) : (false, false);
}

/// <summary>Tile tooltip texts (spec 07 §3.1.13).</summary>
public static class ShelfTooltips
{
    public static string Text(ShelfItem item, Func<string, string>? fileKind = null)
    {
        switch (item.Kind)
        {
            case ShelfItemKind.File:
                var name = item.Path is null ? item.Title : Path.GetFileName(item.Path);
                var kind = item.Path is null ? null : fileKind?.Invoke(item.Path);
                return string.IsNullOrEmpty(kind) ? name : $"{name}\n{kind}";
            case ShelfItemKind.Text:
                return Cap(item.Text?.Trim() ?? string.Empty);
            case ShelfItemKind.Link:
                return Cap(item.Url ?? string.Empty);
            default:
                return PileText(item);
        }
    }

    /// <summary>"N items: a images, b files, c notes, d links", only non-zero kinds, singular or plural per count.</summary>
    public static string PileText(ShelfItem pile)
    {
        var total = ShelfTree.LeafCount(pile);
        var (images, files, notes, links) = ShelfTree.Breakdown(pile);
        var parts = new List<string>(4);
        if (images > 0)
        {
            parts.Add(L.Plural(images, "Strings.shelfTooltipImageSingular", "Strings.shelfTooltipImageFew", "Strings.shelfTooltipImagePlural"));
        }

        if (files > 0)
        {
            parts.Add(L.Plural(files, "Strings.shelfTooltipFileSingular", "Strings.shelfTooltipFileFew", "Strings.shelfTooltipFilePlural"));
        }

        if (notes > 0)
        {
            parts.Add(L.Plural(notes, "Strings.shelfTooltipNoteSingular", "Strings.shelfTooltipNoteFew", "Strings.shelfTooltipNotePlural"));
        }

        if (links > 0)
        {
            parts.Add(L.Plural(links, "Strings.shelfTooltipLinkSingular", "Strings.shelfTooltipLinkFew", "Strings.shelfTooltipLinkPlural"));
        }

        var head = L.Plural(total, "Strings.shelfTooltipItemsFormat", "Strings.shelfTooltipItemsFew", "Strings.shelfTooltipItemsFormat");
        return parts.Count == 0 ? head : $"{head}: {string.Join(", ", parts)}";
    }

    /// <summary>At most 500 characters, then "…".</summary>
    public static string Cap(string text)
    {
        if (text.Length <= ShelfConstants.TooltipTextCap)
        {
            return text;
        }

        var cut = ShelfConstants.TooltipTextCap;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "…";
    }
}
