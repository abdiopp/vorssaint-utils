// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Clipboard;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Clipboard;

/// <summary>Settings → Clean URL: the automatic switch, the manual cleaner and the rules editor (spec 06 §3.5.4).</summary>
public sealed class UrlCleanerSettingsPage : SettingsPage
{
    private readonly UrlCleanerService _cleaner;
    private readonly StackPanel _groups = new() { Spacing = 6 };
    private readonly TextBlock _lastRemoved = new() { Classes = { "caption" } };
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    public UrlCleanerSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _cleaner = services.GetRequiredService<UrlCleanerService>();
        _cleaner.RulesChanged += OnRulesChanged;
        _cleaner.Cleaned += OnCleaned;
        UpdateLastRemoved();

        var addSite = new TextBox { PlaceholderText = "example.com", Width = 200 };
        var addName = new TextBox { PlaceholderText = L.Get("Strings.urlCleanerRulesParameterPlaceholder"), Width = 200 };
        AutomationProperties.SetName(addSite, "example.com");
        AutomationProperties.SetName(addName, L.Get("Strings.urlCleanerRulesParameterPlaceholder"));
        var addSiteButton = new Button { Content = L.Get("Strings.urlCleanerRulesAddButton") };
        addSiteButton.Click += (_, _) =>
        {
            if (UrlCleaning.ValidSiteKey(addSite.Text ?? string.Empty) is { } site && UrlCleaning.ValidParameterName(addName.Text ?? string.Empty) is { } name)
            {
                _cleaner.SaveRules(UrlCleaning.AddName(_cleaner.Rules, site, name));
                _expanded.Add(site);
                addSite.Text = addName.Text = string.Empty;
                Rebuild();
            }
        };
        var addSiteExpander = new Expander
        {
            Header = L.Get("Strings.urlCleanerRulesAddSite"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addSite, addName, addSiteButton } },
        };

        Content = Stack(
            Header("Strings.urlCleanerName", "Strings.urlCleanerEnableCaption"),
            Card(null,
                Toggle(ClipboardSettings.UrlCleanerEnabled, "Link", "Strings.urlCleanerEnable", "Strings.urlCleanerEnableCaption"),
                new StackPanel { Spacing = 4, Children = { _lastRemoved, Note(L.Get("Strings.urlCleanerLocalNote")) } }),
            Card("Strings.urlCleanerManualTitle", new UrlCleanerView(services, showAutomaticSwitch: false, showTitle: false)),
            Card("Strings.urlCleanerRulesTitle",
                Caption(L.Get("Strings.urlCleanerRulesCaption")),
                Caption(L.Get("Strings.urlCleanerRulesCoverageCaption")),
                _groups,
                addSiteExpander));
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cleaner.RulesChanged -= OnRulesChanged;
        _cleaner.Cleaned -= OnCleaned;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRulesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private void OnCleaned(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateLastRemoved);

    private void UpdateLastRemoved()
    {
        var removed = _cleaner.LastRemoved;
        _lastRemoved.Text = removed.Count > 0 ? L.Format("Strings.urlCleanerRemovedFormat", string.Join(", ", removed)) : string.Empty;
        _lastRemoved.IsVisible = removed.Count > 0;
        if (_cleaner.IsActive)
        {
            _lastRemoved.Text = L.Get("Strings.urlCleanerActiveNow") + (removed.Count > 0 ? " · " + _lastRemoved.Text : string.Empty);
            _lastRemoved.IsVisible = true;
        }
    }

    private void Rebuild()
    {
        _groups.Children.Clear();
        foreach (var group in UrlCleaning.RuleGroups(_cleaner.Rules))
        {
            _groups.Children.Add(GroupView(group));
        }
    }

    private Control GroupView(UrlCleaning.RuleGroup group)
    {
        var site = group.Site;
        var title = site.Length == 0 ? L.Get("Strings.urlCleanerRulesAllSites") : site;
        var count = group.EnabledCount == 1 ? L.Get("Strings.urlCleanerRulesCountSingular") : L.Format("Strings.urlCleanerRulesCountPluralFormat", group.EnabledCount);
        var anyOn = group.EnabledCount > 0;
        var siteSwitch = new Button { Classes = { "icon" }, Content = ClipboardUi.Icon(anyOn ? "Subtract" : "Add", 12, "TextPrimaryBrush") };
        var switchTip = L.Get(anyOn ? "Strings.urlCleanerRulesRemoveSiteButton" : "Strings.urlCleanerRulesRestoreSiteButton");
        ToolTip.SetTip(siteSwitch, switchTip);
        AutomationProperties.SetName(siteSwitch, switchTip);
        siteSwitch.Click += (_, _) => _cleaner.SaveRules(UrlCleaning.SetSiteEnabled(_cleaner.Rules, site, !anyOn));

        var grid = new UniformGrid { Columns = 2 };
        foreach (var entry in group.Entries)
        {
            var check = new CheckBox { Content = new TextBlock { Text = entry.Name, Classes = { "mono" }, FontSize = 12 }, IsChecked = entry.IsEnabled };
            var name = entry.Name;
            check.IsCheckedChanged += (_, _) => _cleaner.SaveRules(UrlCleaning.SetEnabled(_cleaner.Rules, site, name, check.IsChecked == true));
            if (entry.IsBuiltIn)
            {
                grid.Children.Add(check);
            }
            else
            {
                var delete = ClipboardUi.IconButton("Dismiss", L.Get("Strings.urlCleanerRulesRemoveButton"), () => _cleaner.SaveRules(UrlCleaning.DeleteName(_cleaner.Rules, site, name)), 11);
                grid.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { check, delete } });
            }
        }

        var addName = new TextBox { PlaceholderText = L.Get("Strings.urlCleanerRulesParameterPlaceholder"), Width = 220 };
        AutomationProperties.SetName(addName, L.Get("Strings.urlCleanerRulesParameterPlaceholder"));
        var add = new Button { Content = L.Get("Strings.urlCleanerRulesAddButton") };
        add.Click += (_, _) =>
        {
            if (UrlCleaning.ValidParameterName(addName.Text ?? string.Empty) is { } valid)
            {
                _cleaner.SaveRules(UrlCleaning.AddName(_cleaner.Rules, site, valid));
            }
        };

        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _expanded.Contains(site),
            Header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                ColumnSpacing = 8,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    grid,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addName, add } },
                    Caption(L.Get("Strings.urlCleanerRulesMatchCaption")),
                },
            },
        };
        var header = (Grid)expander.Header!;
        var countText = new TextBlock { Text = count, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(countText, 1);
        header.Children.Add(countText);
        Grid.SetColumn(siteSwitch, 2);
        header.Children.Add(siteSwitch);
        expander.Expanded += (_, _) => _expanded.Add(site);
        expander.Collapsed += (_, _) => _expanded.Remove(site);
        return expander;
    }
}
