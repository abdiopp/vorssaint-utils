// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Settings.Pages;

/// <summary>Every global shortcut of the installed features, grouped like the Features hub.</summary>
public sealed class ShortcutsPage : SettingsPage
{
    public ShortcutsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var runtime = services.GetRequiredService<FeatureRuntime>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        var order = FeatureCatalog.All.Select((f, i) => (f.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var cards = new List<Avalonia.Controls.Control?> { Header("Strings.shortcutsPageTitle", "win.shell.shortcutsIntro") };
        foreach (var group in Enum.GetValues<FeatureGroup>())
        {
            var roles = shortcuts.Roles
                .Where(r => FeatureCatalog.Find(r.FeatureId) is { } f && f.Group == group && runtime.IsAvailable(r.FeatureId))
                .OrderBy(r => order.GetValueOrDefault(r.FeatureId))
                .ToList();
            if (roles.Count == 0)
            {
                continue;
            }

            cards.Add(CardText(L.Get(FeatureCatalog.GroupTitleKey(group)), roles.Select(r => (Avalonia.Controls.Control?)new ShortcutRoleRow(r, showFeatureName: true)).ToArray()));
        }

        if (cards.Count == 1)
        {
            cards.Add(Caption(L.Get("win.shell.noResults")));
        }

        Content = Stack(cards.ToArray());
    }
}
