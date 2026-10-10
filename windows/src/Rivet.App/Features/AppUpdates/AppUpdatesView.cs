// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.PackageManager;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.AppUpdates;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.AppUpdates;

/// <summary>
/// The App updates list (spec §3.3.2) in compact (panel) or full (Settings)
/// form: summary and Check now, empty states, selectable rows with their
/// source, Update %d, rules, the incomplete-check warning, the shared
/// operation card and the last error.
/// </summary>
public sealed class AppUpdatesView : UserControl
{
    private const double RowHeight = 40;
    private const double RowGap = 5;

    private static readonly object IconGate = new();
    private static readonly Dictionary<string, PixelBuffer?> Icons = new(StringComparer.Ordinal);
    private static bool _rulesExpanded;

    private readonly AppUpdatesService _updates;
    private readonly IShellService _shell;
    private readonly ISettingsStore _settings;
    private readonly IInstalledAppsProvider? _apps;
    private readonly bool _compact;
    private readonly ScrollViewer _list;
    private readonly OperationStatusView _operation;
    private object? _shown;
    private bool _iconsLoading;
    private int _iconsVersion;

    public AppUpdatesView(IServiceProvider services, bool compact)
    {
        _updates = services.GetRequiredService<AppUpdatesService>();
        _shell = services.GetRequiredService<IShellService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _apps = services.GetService<IInstalledAppsProvider>();
        _compact = compact;
        _list = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _operation = new OperationStatusView(services);
        Build();
    }

    /// <summary>Whether the rules disclosure is open (remembered while the app runs; tests set it).</summary>
    internal static bool RulesExpanded
    {
        get => _rulesExpanded;
        set => _rulesExpanded = value;
    }

    /// <summary>Every row's icon lookup has finished (tests wait for it before a snapshot).</summary>
    internal static bool IconsCached(IEnumerable<string> rowIds)
    {
        lock (IconGate)
        {
            return rowIds.All(Icons.ContainsKey);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _updates.Changed += OnChanged;
        _updates.CheckIfNeeded();
        Build();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _updates.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The installed app a row stands for, to borrow its icon: by key for feed rows, by name otherwise.</summary>
    public static InstalledApp? MatchApp(AppUpdateRow row, IReadOnlyList<InstalledApp> apps)
    {
        if (row.Kind == AppUpdateKind.Online)
        {
            return apps.FirstOrDefault(a => OnlineUpdateSource.RuleKeyFor(a) == row.RuleKey);
        }

        var name = row.Name.TrimEnd(WingetPackage.Ellipsis).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        return apps.FirstOrDefault(a => string.Equals(a.DisplayName, name, StringComparison.OrdinalIgnoreCase))
               ?? (row.Name.EndsWith(WingetPackage.Ellipsis) && name.Length >= 4
                   ? apps.FirstOrDefault(a => a.DisplayName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                   : null);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Build);

    /// <summary>Everything the list shows; progress of a running upgrade only redraws the operation card.</summary>
    private object Capture() => (
        _updates.Rows, string.Join(',', _updates.Selection.Order(StringComparer.Ordinal)), _updates.IsChecking, _updates.CheckedThisSession,
        _updates.PackageManagerMissing, _updates.NeedsAgreement, _updates.OnlineIncomplete, _updates.LastError,
        _settings.Get(AppUpdatesSettings.Rules), _updates.LastCheckUtc, _updates.Lane.IsRunning, (_iconsVersion, _rulesExpanded));

    private void Build()
    {
        var state = Capture();
        if (Content is not null && Equals(_shown, state))
        {
            return;
        }

        _shown = state;
        var rows = _updates.Rows;
        var stack = VStack(8, SummaryRow());

        if (_updates.NeedsAgreement)
        {
            stack.Children.Add(WingetCards.Agreement(_shell, () =>
            {
                _settings.Set(PackageManagerSettings.SourceAgreementsAccepted, true);
                _ = _updates.CheckAsync();
            }));
        }

        if (_updates.PackageManagerMissing)
        {
            stack.Children.Add(Card(VStack(6,
                Columns("Auto,*", 8, Icon("Warning", 16, "WarningBrush"), Text(L.Get("appUpdates.packageMissing"), 12)),
                HStack(8,
                    Button(L.Get("win.packageManager.getAppInstaller"), () => _shell.OpenUrl(WingetCards.AppInstallerStoreUri), "StoreMicrosoft"),
                    LinkButton(L.Get("appUpdates.checkNow"), () => _ = _updates.CheckAsync()))), 8));
        }

        if (rows.Count == 0)
        {
            if (_updates.CheckedThisSession && !_updates.IsChecking)
            {
                stack.Children.Add(EmptyState());
            }
        }
        else
        {
            stack.Children.Add(RowsSection(rows));
        }

        if (_updates.CheckedThisSession && _updates.OnlineIncomplete)
        {
            stack.Children.Add(IncompleteWarning());
        }

        if (_updates.Rules.Count > 0)
        {
            stack.Children.Add(RulesSection(_updates.Rules));
        }

        stack.Children.Add(Reuse(_operation));
        if (_updates.LastError is { } error)
        {
            var text = Colored(error, "WarningBrush");
            text.MaxLines = 3;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(text);
        }

        if (!_compact || rows.Count == 0)
        {
            stack.Children.Add(Caption(L.Get("appUpdates.coverageNote")));
        }

        Content = stack;
        RequestIcons(rows);
    }

    private Control SummaryRow()
    {
        var last = _updates.LastCheckUtc is { } utc ? L.Format("appUpdates.lastCheckFormat", When(utc)) : L.Get("appUpdates.neverChecked");
        Control check = _updates.IsChecking
            ? HStack(6, Progress(null, 36), Caption(L.Get("appUpdates.checking"), wrap: false))
            : Button(L.Get("appUpdates.checkNow"), () => _ = _updates.CheckAsync(), "ArrowSync");
        check.IsEnabled = _updates.IsChecking || !_updates.Lane.IsRunning;
        return Columns("*,Auto", 8, Caption(last), check);
    }

    private Control EmptyState()
    {
        var (icon, brush, key) = _updates.OnlineIncomplete
            ? ("Info", "AccentBrush", "appUpdates.partialUpToDate")
            : _updates.Rules.Count > 0
                ? ("Info", "AccentBrush", "appUpdates.noVisibleUpdates")
                : ("CheckmarkCircle", "SuccessBrush", "appUpdates.upToDate");
        return Columns("Auto,*", 8, Icon(icon, 18, brush), Text(L.Get(key), 13, FontWeight.SemiBold));
    }

    private Control RowsSection(IReadOnlyList<AppUpdateRow> rows)
    {
        var selection = _updates.Selection;
        var stack = VStack(6);
        if (rows.Any(r => r.IsSelectable))
        {
            stack.Children.Add(HStack(12,
                LinkButton(L.Get("appUpdates.selectAll"), _updates.SelectAll),
                LinkButton(L.Get("appUpdates.clearSelection"), _updates.ClearSelection)));
        }

        var list = VStack(RowGap);
        // Keeps the overlay scroll bar off the row buttons.
        list.Margin = new Thickness(0, 0, 10, 0);
        foreach (var row in rows)
        {
            list.Children.Add(Row(row, selection.Contains(row.Id)));
        }

        _list.Content = list;
        _list.MaxHeight = _compact ? AppUpdateList.CompactHeight(rows.Count) : 440;
        stack.Children.Add(Reuse(_list));

        var count = _updates.SelectedCount;
        var update = Button(L.Format("appUpdates.updateSelectedFormat", count), () => _ = UpdateSelectedAsync(), "ArrowDownload", accent: true, stretch: _compact);
        update.IsEnabled = count > 0 && !_updates.IsBusy;
        stack.Children.Add(update);
        return stack;
    }

    private Control Row(AppUpdateRow row, bool selected)
    {
        Control lead = row.IsSelectable
            ? CheckBox(selected, value => _updates.SetSelected(row.Id, value), row.Name)
            : new Border { Width = 20 };
        if (lead is CheckBox box)
        {
            box.Width = 20;
            box.IsEnabled = !_updates.IsBusy;
        }

        PixelBuffer? pixels;
        lock (IconGate)
        {
            Icons.TryGetValue(row.Id, out pixels);
        }

        var icon = AppIcon(pixels, 28, row.Kind switch
        {
            AppUpdateKind.Store => "StoreMicrosoft",
            AppUpdateKind.Online => "Globe",
            _ => "Box",
        });

        var badge = row.Kind switch
        {
            AppUpdateKind.Store => Pill(L.Get("appUpdates.appStoreBadge")),
            AppUpdateKind.Online => Pill(L.Get("appUpdates.onlineBadge")),
            _ => Pill(L.Get("appUpdates.homebrewBadge")),
        };
        var name = Text(row.Name, 12, FontWeight.SemiBold, wrap: false);
        var texts = VStack(1,
            Columns("Auto,Auto", 6, name, badge),
            Caption($"{row.InstalledVersion} → {row.LatestVersion}", wrap: false));
        name.MaxWidth = _compact ? 132 : 320;

        var busy = _updates.IsBusy;
        var (label, tip) = row.Kind switch
        {
            AppUpdateKind.Store => (L.Get("appUpdates.appStoreBadge"), L.Get("appUpdates.storeHint")),
            AppUpdateKind.Online => (L.Get("appUpdates.openApp"), L.Get("appUpdates.openAppHint")),
            _ => (L.Get("appUpdates.updateOne"), row.IdUnresolved ? L.Get("win.appUpdates.idUnresolved") : null),
        };
        var action = Button(label, () => _ = UpdateOneAsync(row));
        action.Padding = new Thickness(10, 3);
        action.FontSize = 12;
        action.IsEnabled = row.Kind switch
        {
            AppUpdateKind.PackageManager => !busy && !row.IdUnresolved,
            AppUpdateKind.Online => row.AppPath is not null,
            _ => true,
        };
        if (tip is not null)
        {
            ToolTip.SetTip(action, tip);
        }

        var line = new Border
        {
            Height = RowHeight,
            Background = Brushes.Transparent,
            Child = Columns("Auto,Auto,*,Auto", 8, lead, icon, texts, action),
        };
        AutomationProperties.SetName(line, row.Name);
        var rules = new List<(string, Action, bool)>();
        if (!VersionComparer.IsUncomparable(row.LatestVersion))
        {
            rules.Add((L.Format("appUpdates.skipVersionFormat", row.LatestVersion), () => _updates.SkipVersion(row), !_updates.IsChecking));
        }

        rules.Add((L.Get("appUpdates.excludeApp"), () => _updates.ExcludeApp(row), !_updates.IsChecking));
        line.ContextMenu = Menu(rules.ToArray());
        return line;
    }

    private Control IncompleteWarning()
    {
        var stack = VStack(2, Colored(L.Get("appUpdates.incompleteCheck"), "WarningBrush", 12, FontWeight.SemiBold));
        if (_updates.UncheckedNames.Count > 0)
        {
            stack.Children.Add(Caption(string.Join(", ", _updates.UncheckedNames)));
        }

        stack.Children.Add(Caption(L.Get("appUpdates.onlineUnavailable")));
        return stack;
    }

    private Control RulesSection(IReadOnlyList<UpdateRule> rules)
    {
        var list = VStack(4);
        foreach (var rule in rules)
        {
            var remove = LinkButton(L.Get("appUpdates.removeRule"), () => _updates.RemoveRule(rule));
            remove.IsEnabled = !_updates.IsBusy;
            list.Children.Add(Columns("*,Auto", 8,
                VStack(0,
                    Text(rule.Name, 12, FontWeight.SemiBold, wrap: false),
                    Caption(rule.Version is { } version ? L.Format("appUpdates.skippedVersionFormat", version) : L.Get("appUpdates.excludedApp"))),
                remove));
        }

        Control body = list;
        if (_compact)
        {
            body = new ScrollViewer { Content = list, MaxHeight = Math.Min(rules.Count, 3) * 54, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        var expander = new Expander
        {
            Header = new TextBlock { Text = $"{L.Get("appUpdates.rulesTitle")} ({rules.Count})", FontSize = 12 },
            IsExpanded = _rulesExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = VStack(6, Caption(L.Get("appUpdates.rulesHint")), body),
        };
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty)
            {
                _rulesExpanded = expander.IsExpanded;
            }
        };
        return expander;
    }

    private async Task UpdateSelectedAsync()
    {
        var selection = _updates.Selection;
        var selected = _updates.Rows.Where(r => r.IsSelectable && selection.Contains(r.Id)).ToList();
        var packages = selected.Count(r => r.Kind == AppUpdateKind.PackageManager);
        var store = selected.Count(r => r.Kind == AppUpdateKind.Store);
        if (packages > 0)
        {
            var message = L.Format("win.appUpdates.confirmPackagesFormat", packages);
            if (store > 0)
            {
                message += "\n\n" + L.Get("win.appUpdates.confirmStoreNote");
            }

            if (!await ConfirmAsync(this, L.Get("win.appUpdates.confirmTitle"), message, L.Format("appUpdates.updateSelectedFormat", selected.Count)))
            {
                return;
            }
        }

        await _updates.UpdateSelectedAsync();
    }

    private async Task UpdateOneAsync(AppUpdateRow row)
    {
        if (row.Kind == AppUpdateKind.PackageManager
            && !await ConfirmAsync(this, L.Get("win.packageManager.confirmUpgradeTitle"), L.Format("win.packageManager.confirmUpgradeFormat", row.Name), L.Get("appUpdates.updateOne")))
        {
            return;
        }

        await _updates.UpdateOneAsync(row);
    }

    /// <summary>Loads missing row icons once, off the UI thread (enumerating installed apps reads the registry).</summary>
    private void RequestIcons(IReadOnlyList<AppUpdateRow> rows)
    {
        if (_apps is not { } apps || _iconsLoading)
        {
            return;
        }

        List<AppUpdateRow> missing;
        lock (IconGate)
        {
            missing = rows.Where(r => !Icons.ContainsKey(r.Id)).ToList();
        }

        if (missing.Count == 0)
        {
            return;
        }

        _iconsLoading = true;
        _ = Task.Run(() =>
        {
            IReadOnlyList<InstalledApp> installed = [];
            try
            {
                installed = apps.Enumerate();
            }
            catch (Exception ex)
            {
                Log.Warn("appUpdates", "Could not list installed apps for icons.", ex);
            }

            foreach (var row in missing)
            {
                PixelBuffer? icon = null;
                try
                {
                    if (MatchApp(row, installed) is { } app)
                    {
                        icon = apps.LoadIcon(app, 32);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("appUpdates", $"No icon for {row.Name}: {ex.Message}");
                }

                lock (IconGate)
                {
                    Icons[row.Id] = icon;
                }
            }
        }).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _iconsLoading = false;
            _iconsVersion++;
            Build();
        }), TaskScheduler.Default);
    }
}
