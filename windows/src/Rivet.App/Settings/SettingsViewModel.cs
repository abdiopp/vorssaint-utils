// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;

namespace Rivet.App.Settings;

public sealed partial class SidebarItem(SettingsPageDescriptor page) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public SettingsPageDescriptor Page { get; } = page;

    public string PageId => Page.Id;

    public string Title => L.Get(Page.TitleKey);

    public Symbol Icon => IconConverter.Parse(Page.Icon);
}

public sealed class SidebarGroup(string title, IReadOnlyList<SidebarItem> items)
{
    public string Title { get; } = title;

    public IReadOnlyList<SidebarItem> Items { get; } = items;
}

public sealed record SearchHit(string PageId, string Title, string? Detail, Symbol Icon, string? RevealFeatureId);

/// <summary>Settings navigation: sidebar, search, history and the current page.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly SettingsPageRegistry _registry;
    private readonly FeatureRuntime _runtime;
    private readonly SettingsNavigationState _navigation;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private Control? _currentPage;

    [ObservableProperty]
    private string _currentPageId = SettingsPageIds.General;

    public SettingsViewModel(IServiceProvider services)
    {
        _services = services;
        _registry = services.GetRequiredService<SettingsPageRegistry>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _navigation = services.GetRequiredService<SettingsNavigationState>();
        _runtime.Changed += (_, _) => RebuildSidebar(rebuildPage: !IsVisible(CurrentPageId));
        _registry.Changed += (_, _) => RebuildSidebar(rebuildPage: false);
        Localizer.Current.LanguageChanged += (_, _) =>
        {
            RebuildSidebar(rebuildPage: true);
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(SearchWatermark));
        };
        RebuildSidebar(rebuildPage: false);
    }

    public ObservableCollection<SidebarGroup> Groups { get; } = [];

    public ObservableCollection<SearchHit> SearchResults { get; } = [];

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public string WindowTitle => L.Format("win.shell.settingsWindowTitle", AppIdentity.DisplayName);

    public string SearchWatermark => L.Get("win.shell.searchSettings");

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public static string CategoryTitleKey(SettingsCategory category) => category switch
    {
        SettingsCategory.Essentials => "settingsCategories.essentials",
        SettingsCategory.Capture => "win.shell.groupCapture",
        SettingsCategory.Monitor => "hub.groupMonitor",
        SettingsCategory.Tools => "hub.groupTools",
        SettingsCategory.AppManagement => "settingsCategories.appManagement",
        SettingsCategory.ClipboardFiles => "hub.groupClipboardFiles",
        SettingsCategory.Sound => "hub.groupSound",
        SettingsCategory.EnergyDisplay => "hub.groupEnergyDisplay",
        SettingsCategory.MouseKeyboard => "hub.groupMouseKeyboard",
        _ => "settingsCategories.app",
    };

    public bool IsVisible(string pageId) =>
        _registry.Find(pageId) is { } page && (page.FeatureIds.Count == 0 || page.FeatureIds.Any(_runtime.IsAvailable));

    /// <summary>Opens a page (falls back to General when it is hidden) and optionally reveals a hub row.</summary>
    public void Navigate(string? pageId, string? revealFeatureId = null, bool recordHistory = true)
    {
        if (revealFeatureId is not null)
        {
            _navigation.PendingRevealFeature = revealFeatureId;
        }

        var target = pageId is not null && IsVisible(pageId) ? pageId : (pageId is null ? CurrentPageId : SettingsPageIds.Features);
        if (!IsVisible(target))
        {
            target = SettingsPageIds.General;
        }

        if (recordHistory && CurrentPage is not null && target != CurrentPageId)
        {
            _back.Push(CurrentPageId);
            _forward.Clear();
        }

        ShowPage(target);
    }

    [RelayCommand]
    private void GoBack()
    {
        while (_back.Count > 0)
        {
            var page = _back.Pop();
            if (IsVisible(page))
            {
                _forward.Push(CurrentPageId);
                ShowPage(page);
                return;
            }
        }
    }

    [RelayCommand]
    private void GoForward()
    {
        while (_forward.Count > 0)
        {
            var page = _forward.Pop();
            if (IsVisible(page))
            {
                _back.Push(CurrentPageId);
                ShowPage(page);
                return;
            }
        }
    }

    [RelayCommand]
    private void OpenPage(string pageId) => Navigate(pageId);

    [RelayCommand]
    private void OpenHit(SearchHit hit)
    {
        SearchText = string.Empty;
        Navigate(hit.PageId, hit.RevealFeatureId);
    }

    partial void OnSearchTextChanged(string value) => RunSearch(value);

    private void ShowPage(string pageId)
    {
        CurrentPageId = pageId;
        foreach (var item in Groups.SelectMany(g => g.Items))
        {
            item.IsSelected = item.PageId == pageId;
        }

        var page = _registry.Find(pageId);
        try
        {
            CurrentPage = page?.CreateView(_services);
        }
        catch (Exception ex)
        {
            Log.Error("settings", $"Page '{pageId}' failed to build.", ex);
            CurrentPage = new TextBlock { Text = ex.Message, Classes = { "caption" } };
        }

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void RebuildSidebar(bool rebuildPage)
    {
        Groups.Clear();
        foreach (var category in Enum.GetValues<SettingsCategory>())
        {
            var items = _registry.Pages
                .Where(p => p.Category == category && IsVisible(p.Id))
                .OrderBy(p => p.Order)
                .ThenBy(p => L.Get(p.TitleKey), StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true))
                .Select(p => new SidebarItem(p) { IsSelected = p.Id == CurrentPageId })
                .ToList();
            if (items.Count > 0)
            {
                Groups.Add(new SidebarGroup(L.Get(CategoryTitleKey(category)).ToUpper(Localizer.Current.Culture), items));
            }
        }

        if (rebuildPage || CurrentPage is null)
        {
            Navigate(CurrentPageId, recordHistory: false);
        }

        if (IsSearching)
        {
            RunSearch(SearchText);
        }
    }

    /// <summary>
    /// Case- and diacritic-insensitive containment over page titles, their
    /// keywords and feature titles. Uninstalled features route to the hub.
    /// </summary>
    private void RunSearch(string query)
    {
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;
        bool Matches(string text) => compare.IndexOf(text, query.Trim(), options) >= 0;

        var hits = new List<(int Rank, SearchHit Hit)>();
        foreach (var page in _registry.Pages.Where(p => IsVisible(p.Id)))
        {
            var title = L.Get(page.TitleKey);
            var icon = IconConverter.Parse(page.Icon);
            if (string.Equals(title, query.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                hits.Add((0, new SearchHit(page.Id, title, null, icon, null)));
            }
            else if (Matches(title))
            {
                hits.Add((1, new SearchHit(page.Id, title, null, icon, null)));
            }

            foreach (var keyword in page.KeywordKeys.Select(L.Get).Concat(page.Keywords).Where(Matches).Distinct())
            {
                hits.Add((2, new SearchHit(page.Id, keyword, title, icon, null)));
            }
        }

        foreach (var feature in FeatureCatalog.All)
        {
            var title = L.Get(feature.TitleKey);
            if (!Matches(title) && !Matches(L.Get(feature.DescriptionKey)))
            {
                continue;
            }

            var page = _runtime.IsAvailable(feature) ? _registry.ForFeature(feature.Id) : null;
            hits.Add(page is not null
                ? (1, new SearchHit(page.Id, title, L.Get(page.TitleKey), IconConverter.Parse(feature.Icon), null))
                : (3, new SearchHit(SettingsPageIds.Features, title, L.Get("hub.pageTitle"), IconConverter.Parse(feature.Icon), feature.Id)));
        }

        foreach (var (_, hit) in hits.OrderBy(h => h.Rank).DistinctBy(h => (h.Hit.PageId, h.Hit.Title)).Take(40))
        {
            SearchResults.Add(hit);
        }
    }
}
