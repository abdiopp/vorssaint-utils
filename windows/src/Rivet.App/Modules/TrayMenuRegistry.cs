// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.App.Modules;

/// <summary>An entry of the tray icon's right-click menu (between the built-in top and bottom items).</summary>
public sealed record TrayMenuItem
{
    public required string Id { get; init; }

    /// <summary>The (localized) label, read each time the menu opens.</summary>
    public required Func<string> Title { get; init; }

    public string? Icon { get; init; }

    public int Order { get; init; }

    /// <summary>Owning feature; the item is hidden while it is uninstalled.</summary>
    public string? FeatureId { get; init; }

    public Func<bool>? IsVisible { get; init; }

    public required Action Invoke { get; init; }
}

public sealed class TrayMenuRegistry
{
    private readonly List<TrayMenuItem> _items = [];

    public IReadOnlyList<TrayMenuItem> Items => _items;

    public void Add(TrayMenuItem item)
    {
        _items.RemoveAll(i => i.Id == item.Id);
        _items.Add(item);
    }
}
