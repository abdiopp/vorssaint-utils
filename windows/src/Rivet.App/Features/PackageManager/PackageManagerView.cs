// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Platform;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.PackageManager;

/// <summary>
/// The Homebrew manager's flows on winget (spec §3.4.7): search and install,
/// the installed list with updates, details, confirmed install / update /
/// uninstall, "update all", source refresh, and the shared operation card.
/// Formula/cask kinds become a source filter; dependencies, taps and
/// popularity have no winget equivalent.
/// </summary>
public sealed class PackageManagerView : UserControl
{
    private static bool _searchMode;

    private readonly PackageManagerService _manager;
    private readonly IShellService _shell;
    private readonly bool _compact;
    private readonly TextBox _searchBox;
    private readonly ScrollViewer _list;
    private readonly OperationStatusView _operation;
    private ViewState? _shown;
    private bool _loadRequested;

    public PackageManagerView(IServiceProvider services, bool compact)
    {
        _manager = services.GetRequiredService<PackageManagerService>();
        _shell = services.GetRequiredService<IShellService>();
        _compact = compact;
        _searchBox = new TextBox { Text = _manager.LastQuery ?? string.Empty, PlaceholderText = L.Get("Strings.homebrewSearchPlaceholder"), MinWidth = 0 };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Search();
            }
        };
        _list = new ScrollViewer { MaxHeight = compact ? 162 : 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _operation = new OperationStatusView(services);
        Build();
    }

    /// <summary>Search or installed mode, shared by the panel and Settings (tests set it directly).</summary>
    internal static bool SearchMode
    {
        get => _searchMode;
        set => _searchMode = value;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _manager.Changed += OnChanged;
        _ = StartAsync();
        Build();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _manager.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private async Task StartAsync()
    {
        await _manager.DetectAsync();
        EnsureLoaded();
    }

    /// <summary>Loads the installed list once winget is usable (also after "Refresh" or accepting the agreements).</summary>
    private void EnsureLoaded()
    {
        if (!_loadRequested && _manager.Availability == WingetAvailability.Available && _manager.AgreementsAccepted
            && !_manager.InstalledLoaded && !_manager.LoadingInstalled)
        {
            _loadRequested = true;
            _ = _manager.LoadInstalledAsync();
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        EnsureLoaded();
        Build();
    });

    private ViewState Capture() => new(
        _manager.Availability, _manager.AgreementsAccepted, _searchMode, _manager.SourceFilter,
        _manager.Installed, _manager.SearchResults, _manager.LastQuery, _manager.Selected, _manager.Details,
        _manager.LoadingInstalled, _manager.Searching, _manager.LoadingDetails, _manager.Error, _manager.Lane.IsRunning);

    /// <summary>
    /// Rebuilds only when something this view shows changed: progress of a
    /// running operation updates the operation card alone, so the list keeps
    /// its scroll position and the search box keeps focus.
    /// </summary>
    private void Build()
    {
        var state = Capture();
        if (_shown == state && Content is not null)
        {
            return;
        }

        _shown = state;
        var stack = VStack(8);
        switch (_manager.Availability)
        {
            case WingetAvailability.Unknown:
                stack.Children.Add(HStack(8, Progress(null, 80), Caption(L.Get("Strings.homebrewLoading"))));
                Content = stack;
                return;
            case WingetAvailability.Missing:
                stack.Children.Add(WingetCards.Missing(_shell, () =>
                {
                    _loadRequested = false;
                    _ = _manager.DetectAsync(force: true);
                }));
                Content = stack;
                return;
        }

        if (!_manager.AgreementsAccepted)
        {
            stack.Children.Add(WingetCards.Agreement(_shell, () =>
            {
                _loadRequested = false;
                _manager.AcceptAgreements();
            }));
            Content = stack;
            return;
        }

        stack.Children.Add(ModePicker());
        stack.Children.Add(_searchMode ? SearchPane() : InstalledPane());
        if (_manager.Error is { } error)
        {
            stack.Children.Add(Colored(error, "WarningBrush"));
        }

        if (_manager.Selected is { } selected)
        {
            stack.Children.Add(DetailsCard(selected));
        }
        else if (!_compact)
        {
            stack.Children.Add(Caption(L.Get("win.packageManager.noSelection")));
        }

        stack.Children.Add(Reuse(_operation));
        Content = stack;
    }

    private void Search()
    {
        var text = _searchBox.Text ?? string.Empty;
        if (text.Trim().Length > 0)
        {
            _ = _manager.SearchAsync(text);
        }
        else
        {
            _manager.ClearSearch();
        }
    }

    private Control ModePicker()
    {
        RadioButton Tab(string text, bool selected, bool search)
        {
            var button = new RadioButton { Classes = { "tab" }, Content = new TextBlock { Text = text, FontSize = 12 }, IsChecked = selected, GroupName = "PackageManagerMode" + GetHashCode() };
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true && _searchMode != search)
                {
                    _searchMode = search;
                    Dispatcher.UIThread.Post(() =>
                    {
                        Build();
                        if (search)
                        {
                            _searchBox.Focus();
                        }
                    });
                }
            };
            return button;
        }

        var grid = new UniformGrid { Rows = 1 };
        grid.Children.Add(Tab(L.Get("win.packageManager.modeSearch"), _searchMode, true));
        grid.Children.Add(Tab(L.Format("win.packageManager.modeInstalledFormat", _manager.Installed.Count), !_searchMode, false));
        return Card(grid, 3);
    }

    private Control SearchPane()
    {
        var search = IconButton("Search", L.Get("Strings.homebrewSearchButton"), Search);
        search.IsEnabled = !_manager.Searching;

        var stack = VStack(6, Columns("*,Auto,Auto", 6, Reuse(_searchBox), SourceFilter(includeLocal: false), search));
        if (_compact && _manager.LastQuery is null)
        {
            stack.Children.Add(Caption(L.Get("win.packageManager.searchHint")));
        }

        if (_manager.Searching)
        {
            stack.Children.Add(Progress(null));
        }
        else if (_manager.LastQuery is not null && _manager.SearchResults.Count == 0 && _manager.Error is null)
        {
            stack.Children.Add(Caption(L.Get("Strings.homebrewSearchEmpty")));
        }

        if (_manager.SearchResults.Count > 0)
        {
            stack.Children.Add(PackageList(_manager.SearchResults, searchResults: true));
        }

        return stack;
    }

    private Control InstalledPane()
    {
        var updates = _manager.UpdateCount;
        var refresh = IconButton("ArrowSync", L.Get("Strings.homebrewCheckPackages"), () => _ = _manager.LoadInstalledAsync());
        refresh.IsEnabled = !_manager.IsBusy;
        var sources = IconButton("CloudArrowDown", L.Get("win.packageManager.updateSources"), () => _ = UpdateSourcesAsync());
        sources.IsEnabled = !_manager.IsBusy;
        var stack = VStack(6, Columns("*,Auto,Auto", 4, SourceFilter(includeLocal: true), refresh, sources));

        if (updates > 0)
        {
            var all = Button(L.Get("Strings.homebrewUpgradeAll"), () => _ = UpgradeAllAsync(), "ArrowCircleUp", accent: true);
            all.IsEnabled = !_manager.IsBusy;
            stack.Children.Add(Columns("Auto,Auto,*,Auto", 6, Icon("ArrowCircleUp", 16, "WarningBrush"), Text(L.Get("Strings.homebrewUpdates"), 12), Pill(Number(updates), "WarningSoftBrush", "WarningBrush"), all));
        }

        if (_manager.LoadingInstalled && _manager.Installed.Count == 0)
        {
            stack.Children.Add(HStack(8, Progress(null, 80), Caption(L.Get("Strings.homebrewLoading"))));
        }
        else if (_manager.InstalledLoaded)
        {
            var rows = _manager.FilteredInstalled();
            stack.Children.Add(rows.Count == 0 ? Caption(L.Get("Strings.homebrewNoPackages")) : PackageList(rows, searchResults: false));
            if (_manager.LoadingInstalled)
            {
                stack.Children.Add(Progress(null));
            }
        }

        return stack;
    }

    private ComboBox SourceFilter(bool includeLocal)
    {
        var options = new List<(string Key, string Label)>
        {
            ("all", L.Get("win.packageManager.filterAll")),
            ("winget", L.Get("win.packageManager.filterWinget")),
            ("msstore", L.Get("win.packageManager.filterStore")),
        };
        if (includeLocal)
        {
            options.Add(("local", L.Get("win.packageManager.filterLocal")));
        }

        var labels = options.Select(o => includeLocal ? L.Format("win.packageManager.filterFormat", o.Label, _manager.CountFor(o.Key)) : o.Label).ToList();
        var current = !includeLocal && _manager.SourceFilter == "local" ? "all" : _manager.SourceFilter;
        var index = Math.Max(0, options.FindIndex(o => o.Key == current));
        var combo = new ComboBox { ItemsSource = labels, SelectedIndex = index, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        ToolTip.SetTip(combo, L.Get("win.packageManager.sourceLabel"));
        Avalonia.Automation.AutomationProperties.SetName(combo, L.Get("win.packageManager.sourceLabel"));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && options[combo.SelectedIndex].Key != _manager.SourceFilter)
            {
                _manager.SourceFilter = options[combo.SelectedIndex].Key;
            }
        };
        return combo;
    }

    private Control PackageList(IReadOnlyList<WingetPackage> packages, bool searchResults)
    {
        var list = VStack(1);
        // Keeps the overlay scroll bar off the row buttons.
        list.Margin = new Thickness(0, 0, 10, 0);
        foreach (var package in packages.Take(300))
        {
            list.Children.Add(PackageRow(package, searchResults));
        }

        _list.Content = list;
        return Card(Reuse(_list), 4);
    }

    private Control PackageRow(WingetPackage package, bool searchResult)
    {
        var installed = searchResult ? _manager.InstalledEntry(package.Id) : package;
        var line = installed?.HasUpdate == true && !installed.IsLocal ? $"{installed.Version} → {installed.Available}" : package.Version;
        var badges = HStack(4);
        if (package.IsStore)
        {
            badges.Children.Add(Pill(L.Get("win.packageManager.storeBadge")));
        }
        else if (package.IsLocal)
        {
            var local = Pill(L.Get("win.packageManager.localBadge"));
            ToolTip.SetTip(local, L.Get("win.packageManager.localHint"));
            badges.Children.Add(local);
        }

        if (searchResult && installed is not null)
        {
            badges.Children.Add(Pill(L.Get("Strings.homebrewInstalledBadge"), null, "SuccessBrush"));
        }

        Button? action = null;
        if (searchResult && installed is null)
        {
            action = IconButton("ArrowDownload", L.Get("Strings.homebrewInstall"), () => _ = ConfirmAndRunAsync(PackageOperationKind.Install, package));
        }
        else if (installed is { HasUpdate: true, IsLocal: false })
        {
            action = IconButton("ArrowCircleUp", L.Get("Strings.homebrewUpgrade"), () => _ = ConfirmAndRunAsync(PackageOperationKind.Upgrade, installed));
        }

        if (action is not null)
        {
            action.IsEnabled = !_manager.Lane.IsRunning;
        }

        var name = Text(package.Name, 12, FontWeight.SemiBold, wrap: false);
        var texts = VStack(0,
            Columns("Auto,*", 6, name, badges),
            Caption($"{package.Id}  ·  {line}", wrap: false));
        name.MaxWidth = _compact ? 170 : 360;
        var row = new Button { Classes = { "row" }, Padding = new Thickness(6, 4), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Columns("*,Auto", 6, texts, action) };
        Avalonia.Automation.AutomationProperties.SetName(row, package.Name);
        if (_manager.Selected?.Id == package.Id)
        {
            Brush(row, Avalonia.Controls.Button.BackgroundProperty, "AccentFaintBrush");
        }

        row.Click += (_, _) => _ = _manager.SelectAsync(package);
        return row;
    }

    private Control DetailsCard(WingetPackage package)
    {
        var installed = _manager.InstalledEntry(package.Id);
        var details = _manager.Details;
        var status = installed is null
            ? Pill(L.Get("Strings.homebrewNotInstalledBadge"))
            : installed.HasUpdate && !installed.IsLocal
                ? Pill(L.Get("Strings.homebrewUpdateAvailableBadge"), "WarningSoftBrush", "WarningBrush")
                : Pill(L.Get("Strings.homebrewInstalledBadge"), null, "SuccessBrush");
        var stack = VStack(6, Columns("*,Auto,Auto", 6,
            VStack(0, Text(details?.Name ?? package.Name, 14, FontWeight.SemiBold), Caption(package.Id, wrap: false)),
            status,
            IconButton("Dismiss", L.Get("Strings.menuClose"), () => _ = _manager.SelectAsync(null), 12)));

        if (_manager.LoadingDetails)
        {
            stack.Children.Add(Progress(null));
        }

        if (details?.Description is { } description)
        {
            stack.Children.Add(Caption(description));
        }

        if (package.IsLocal)
        {
            stack.Children.Add(Caption(L.Get("win.packageManager.localHint")));
        }

        var facts = new List<(string Label, string Value)>();
        if (installed is not null && installed.Version.Length > 0)
        {
            facts.Add((L.Get("Strings.homebrewVersion"), installed.Version));
        }

        if ((installed?.Available ?? details?.Version) is { Length: > 0 } latest)
        {
            facts.Add((L.Get("Strings.homebrewLatestVersion"), latest));
        }

        if (details?.Publisher is { } publisher)
        {
            facts.Add((L.Get("win.packageManager.publisher"), publisher));
        }

        if (details?.License is { } license)
        {
            facts.Add((L.Get("win.packageManager.license"), license));
        }

        foreach (var (label, value) in facts)
        {
            stack.Children.Add(Columns("100,*", 8, Caption(label), Text(value, 12)));
        }

        var busy = _manager.Lane.IsRunning;
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        void Add(Button button, bool enabled)
        {
            button.IsEnabled = enabled;
            buttons.Children.Add(button);
        }

        if (installed is null && !package.IsLocal)
        {
            Add(Button(L.Get("Strings.homebrewInstall"), () => _ = ConfirmAndRunAsync(PackageOperationKind.Install, package), "ArrowDownload", accent: true), !busy && !package.IdTruncated);
        }

        if (installed is { HasUpdate: true, IsLocal: false })
        {
            Add(Button(L.Get("Strings.homebrewUpgrade"), () => _ = ConfirmAndRunAsync(PackageOperationKind.Upgrade, installed), "ArrowCircleUp", accent: true), !busy);
        }

        if (installed is not null)
        {
            Add(Button(L.Get("Strings.homebrewUninstall"), () => _ = ConfirmAndRunAsync(PackageOperationKind.Uninstall, installed), "Delete"), !busy);
        }

        if (details?.Homepage is { } homepage && Uri.TryCreate(homepage, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            Add(LinkButton(L.Get("Strings.homebrewHomepage"), () => _shell.OpenUrl(uri.AbsoluteUri)), true);
        }

        if (buttons.Children.Count > 0)
        {
            stack.Children.Add(buttons);
        }

        return Card(stack);
    }

    private async Task ConfirmAndRunAsync(PackageOperationKind kind, WingetPackage package)
    {
        var (title, message, confirm, destructive) = kind switch
        {
            PackageOperationKind.Install => (L.Get("win.packageManager.confirmInstallTitle"), L.Format("win.packageManager.confirmInstallFormat", package.Name), L.Get("Strings.homebrewInstall"), false),
            PackageOperationKind.Uninstall => (L.Get("win.packageManager.confirmUninstallTitle"), L.Format("win.packageManager.confirmUninstallFormat", package.Name), L.Get("Strings.homebrewUninstall"), true),
            _ => (L.Get("win.packageManager.confirmUpgradeTitle"), L.Format("win.packageManager.confirmUpgradeFormat", package.Name), L.Get("Strings.homebrewUpgrade"), false),
        };
        if (await ConfirmAsync(this, title, message, confirm, destructive))
        {
            await _manager.RunAsync(kind, package);
        }
    }

    private async Task UpgradeAllAsync()
    {
        if (await ConfirmAsync(this, L.Get("win.packageManager.confirmUpgradeAllTitle"), L.Get("win.packageManager.confirmUpgradeAllMessage"), L.Get("Strings.homebrewUpgradeAll")))
        {
            await _manager.RunAsync(PackageOperationKind.UpgradeAll, null);
        }
    }

    private async Task UpdateSourcesAsync()
    {
        if (await ConfirmAsync(this, L.Get("win.packageManager.confirmSourcesTitle"), L.Get("win.packageManager.confirmSourcesMessage"), L.Get("win.packageManager.updateSources")))
        {
            await _manager.RunAsync(PackageOperationKind.UpdateSources, null);
        }
    }

    private readonly record struct ViewState(
        WingetAvailability Availability,
        bool Accepted,
        bool SearchMode,
        string Filter,
        IReadOnlyList<WingetPackage> Installed,
        IReadOnlyList<WingetPackage> Results,
        string? Query,
        WingetPackage? Selected,
        WingetDetails? Details,
        bool LoadingInstalled,
        bool Searching,
        bool LoadingDetails,
        string? Error,
        bool Running);
}
