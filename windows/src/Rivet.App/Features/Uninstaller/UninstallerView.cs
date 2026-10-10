// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Platform;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.Uninstaller;

/// <summary>
/// The Uninstaller on Windows (spec §3.2.11): pick an app, close it, run its
/// own uninstaller (not reversible, and the copy says so), verify it is gone,
/// then review what it left behind. Files go to the Recycle Bin; registry
/// keys are saved to a .reg file first; scheduled tasks and services are
/// listed for review only. One shared flow for the panel and Settings.
/// </summary>
public sealed class UninstallerView : UserControl
{
    private static readonly object IconGate = new();
    private static readonly Dictionary<string, PixelBuffer?> Icons = new(StringComparer.Ordinal);

    private readonly UninstallerService _service;
    private readonly IInstalledAppsProvider _apps;
    private readonly IShellService _shell;
    private readonly IElevationService _elevation;
    private readonly IAppShell? _appShell;
    private readonly bool _compact;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private IDisposable? _keepOpen;
    private object? _shown;
    private bool _iconsLoading;
    private int _iconsVersion;

    public UninstallerView(IServiceProvider services, bool compact)
    {
        _service = services.GetRequiredService<UninstallerService>();
        _apps = services.GetRequiredService<IInstalledAppsProvider>();
        _shell = services.GetRequiredService<IShellService>();
        _elevation = services.GetRequiredService<IElevationService>();
        _appShell = services.GetService<IAppShell>();
        _compact = compact;

        _search = new TextBox { PlaceholderText = L.Get("Strings.uninstallerPickerSearch"), MinWidth = 0 };
        AutomationProperties.SetName(_search, L.Get("Strings.uninstallerPickerSearch"));
        _search.TextChanged += (_, _) => Filter();
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            MaxHeight = compact ? 290 : 420,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<InstalledApp>((app, _) => app is null ? new Panel() : AppRow(app)),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is InstalledApp app)
            {
                _list.SelectedItem = null;
                _ = _service.SelectAsync(app);
            }
        };
        _empty = Caption(L.Get("Strings.uninstallerPickerEmpty"));
        Build();
    }

    /// <summary>Every app's icon lookup has finished (tests wait for it before a snapshot).</summary>
    internal static bool IconsCached(IEnumerable<string> appKeys)
    {
        lock (IconGate)
        {
            return appKeys.All(Icons.ContainsKey);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _service.Changed += OnChanged;
        _ = _service.LoadAppsAsync();
        Build();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _service.Changed -= OnChanged;
        _keepOpen?.Dispose();
        _keepOpen = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Short kind label: Store/MSIX package, MSI or classic installer.</summary>
    public static string KindLabel(InstalledApp app) => L.Get(app.Kind switch
    {
        InstalledAppKind.Msix => "win.uninstaller.kindMsix",
        InstalledAppKind.Msi => "win.uninstaller.kindMsi",
        _ => "win.uninstaller.kindWin32",
    });

    /// <summary>"Contoso Ltd. · 4.2.1 · 1.2 GB".</summary>
    public static string Details(InstalledApp app)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(app.Publisher))
        {
            parts.Add(app.Publisher!);
        }

        if (!string.IsNullOrWhiteSpace(app.Version))
        {
            parts.Add(app.Version!);
        }

        if (app.SizeBytes is > 0 and var size)
        {
            parts.Add(Size(size));
        }

        return string.Join("  ·  ", parts);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Build);

    private object Capture() => (
        _service.Phase, _service.Apps, _service.LoadingApps, _service.Selected, _service.Fingerprint, _service.RunningCopies,
        Math.Round(_service.Progress ?? -1, 2), _service.Message, _service.Leftovers,
        string.Join(',', _service.Leftovers.Where(_service.IsIncluded).Select(i => i.Id)), _service.LastResult,
        (_service.Quiet, _service.SortOrder, _iconsVersion));

    private void Build()
    {
        var busy = _service.IsBusy;
        if (busy && _keepOpen is null && _appShell is not null)
        {
            _keepOpen = _appShell.KeepPanelOpen("uninstaller");
        }
        else if (!busy)
        {
            _keepOpen?.Dispose();
            _keepOpen = null;
        }

        var state = Capture();
        if (Content is not null && Equals(_shown, state))
        {
            return;
        }

        _shown = state;
        Content = _service.Phase switch
        {
            UninstallerPhase.Selected => SelectedApp(),
            UninstallerPhase.Uninstalling => Uninstalling(),
            UninstallerPhase.StillInstalled => StillInstalled(),
            UninstallerPhase.Scanning => Busy(L.Get("Strings.uninstallerScanning")),
            UninstallerPhase.Results => Results(),
            UninstallerPhase.Removing => Busy(L.Get("Strings.uninstallerRemoving")),
            UninstallerPhase.Done => Done(),
            _ => AppList(),
        };
    }

    // ── The app list ─────────────────────────────────────────────────

    private Control AppList()
    {
        var sort = new ComboBox
        {
            ItemsSource = new[] { "win.uninstaller.sortName", "win.uninstaller.sortSize", "win.uninstaller.sortDate", "win.uninstaller.sortPublisher" }.Select(L.Get).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(SortKeys, _service.SortOrder)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
        };
        ToolTip.SetTip(sort, L.Get("win.uninstaller.sortLabel"));
        AutomationProperties.SetName(sort, L.Get("win.uninstaller.sortLabel"));
        sort.SelectionChanged += (_, _) =>
        {
            if (sort.SelectedIndex >= 0 && SortKeys[sort.SelectedIndex] != _service.SortOrder)
            {
                _service.SortOrder = SortKeys[sort.SelectedIndex];
            }
        };

        var stack = VStack(8);
        if (!_compact)
        {
            stack.Children.Add(Steps());
        }
        else
        {
            stack.Children.Add(Caption(L.Get("win.uninstaller.intro")));
        }

        stack.Children.Add(Columns("*,Auto", 6, Reuse(_search), sort));
        if (_service.LoadingApps && _service.Apps.Count == 0)
        {
            stack.Children.Add(HStack(8, Progress(null, 80), Caption(L.Get("Strings.homebrewLoading"))));
        }
        else
        {
            stack.Children.Add(Card(Reuse(_list), 2));
            stack.Children.Add(Reuse(_empty));
            Filter();
        }

        stack.Children.Add(Columns("Auto,*", 6, Icon("ShieldCheckmark", 14, "TextSecondaryBrush"), Caption(L.Get("Strings.uninstallerEmptyNote"))));
        RequestIcons();
        return stack;
    }

    private static readonly string[] SortKeys = ["name", "size", "date", "publisher"];

    private Control Steps()
    {
        var steps = VStack(4);
        var keys = new[] { "Strings.uninstallerStep1", "Strings.uninstallerStep2", "Strings.uninstallerStep3" };
        for (var i = 0; i < keys.Length; i++)
        {
            steps.Children.Add(Columns("Auto,*", 8,
                new Border { Classes = { "pill" }, Child = new TextBlock { Text = (i + 1).ToString(CultureInfo.CurrentCulture) } },
                Caption(L.Get(keys[i]))));
        }

        return steps;
    }

    private void Filter()
    {
        var apps = _service.Filtered(_search.Text);
        if (!ReferenceEquals(_list.ItemsSource, apps))
        {
            _list.ItemsSource = apps;
        }

        _list.IsVisible = apps.Count > 0;
        _empty.IsVisible = apps.Count == 0 && _service.AppsLoaded;
    }

    private Control AppRow(InstalledApp app)
    {
        PixelBuffer? pixels;
        lock (IconGate)
        {
            Icons.TryGetValue(app.Key, out pixels);
        }

        var row = Columns("Auto,*", 8,
            AppIcon(pixels, 24, app.Kind == InstalledAppKind.Msix ? "StoreMicrosoft" : "Apps"),
            VStack(0, Text(app.DisplayName, 12, FontWeight.SemiBold, wrap: false), Caption(Details(app), wrap: false)));
        row.Margin = new Thickness(0, 1);
        AutomationProperties.SetName(row, app.DisplayName);
        return row;
    }

    /// <summary>Loads every app's icon once, off the UI thread, then refreshes the list.</summary>
    private void RequestIcons()
    {
        if (_iconsLoading || !_service.AppsLoaded)
        {
            return;
        }

        List<InstalledApp> missing;
        lock (IconGate)
        {
            missing = _service.Apps.Where(a => !Icons.ContainsKey(a.Key)).Take(600).ToList();
        }

        if (missing.Count == 0)
        {
            return;
        }

        _iconsLoading = true;
        _ = Task.Run(() =>
        {
            foreach (var app in missing)
            {
                PixelBuffer? icon = null;
                try
                {
                    icon = _apps.LoadIcon(app, 32);
                }
                catch (Exception ex)
                {
                    Log.Debug("uninstaller", $"No icon for {app.DisplayName}: {ex.Message}");
                }

                lock (IconGate)
                {
                    Icons[app.Key] = icon;
                }
            }
        }).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _iconsLoading = false;
            _iconsVersion++;
            _list.ItemsSource = null;
            Build();
        }), TaskScheduler.Default);
    }

    // ── One app selected ─────────────────────────────────────────────

    private Control Header(InstalledApp app, bool closable)
    {
        PixelBuffer? pixels;
        lock (IconGate)
        {
            Icons.TryGetValue(app.Key, out pixels);
        }

        Control? close = null;
        if (closable)
        {
            close = IconButton("Dismiss", L.Get("Strings.uninstallerCancel"), _service.Reset, 12);
        }

        return Columns("Auto,*,Auto", 10,
            AppIcon(pixels, 40, app.Kind == InstalledAppKind.Msix ? "StoreMicrosoft" : "Apps"),
            VStack(1, Text(app.DisplayName, 14, FontWeight.SemiBold), Caption(Details(app))),
            close);
    }

    private Control SelectedApp()
    {
        var app = _service.Selected!;
        var stack = VStack(10, Header(app, closable: true));
        var facts = VStack(2, Columns("Auto,*", 6, Pill(KindLabel(app)), app.IsPerMachine ? Caption(L.Get("win.uninstaller.allUsers"), wrap: false) : null));
        if (app.InstallLocation is { } location)
        {
            facts.Children.Add(PathText(location));
        }

        stack.Children.Add(facts);

        if (_service.Fingerprint is null)
        {
            stack.Children.Add(Progress(null));
        }
        else if (!_service.Fingerprint.Trusted)
        {
            stack.Children.Add(Note("Info", L.Get("win.uninstaller.notTrusted"), "TextSecondaryBrush"));
        }

        if (_service.RunningCopies.Count > 0)
        {
            stack.Children.Add(RunningCard(app));
        }

        if (app.HasQuietUninstall)
        {
            var quiet = CheckBox(_service.Quiet, value => _service.Quiet = value, L.Get("win.uninstaller.quiet"));
            stack.Children.Add(Columns("Auto,*", 8, quiet, VStack(0, Text(L.Get("win.uninstaller.quiet"), 12), Caption(L.Get("win.uninstaller.quietCaption")))));
        }

        if (app.IsPerMachine && !_service.IsElevated)
        {
            stack.Children.Add(Card(VStack(4,
                Caption(L.Get("win.uninstaller.adminNote")),
                LinkButton(L.Get("win.cleaner.restartAsAdmin"), () => _elevation.RestartElevated())), 8));
        }

        stack.Children.Add(Note("Warning", L.Get("win.uninstaller.irreversibleNote"), "WarningBrush"));
        if (_service.Message is { } message)
        {
            stack.Children.Add(Colored(message, "WarningBrush"));
        }

        var uninstall = Button(L.Get("win.uninstaller.uninstall"), () => _ = ConfirmUninstallAsync(app), "Delete", danger: true, stretch: _compact);
        uninstall.IsEnabled = _service.Fingerprint is not null && app.CanUninstall;
        var cancel = Button(L.Get("Strings.uninstallerCancel"), _service.Reset, stretch: _compact);
        stack.Children.Add(_compact ? Columns("*,*", 8, cancel, uninstall) : HStack(8, uninstall, cancel));
        return stack;
    }

    private Control RunningCard(InstalledApp app)
    {
        var copies = _service.RunningCopies;
        var names = string.Join(", ", copies.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
        var close = Button(L.Get("win.uninstaller.closeApp"), () => _ = _service.CloseRunningAsync(force: false));
        var force = Button(L.Get("win.uninstaller.forceQuit"), () => _ = ForceQuitAsync(app), danger: false);
        var protectedOnly = copies.All(c => c.IsProtected);
        close.IsEnabled = force.IsEnabled = !protectedOnly;
        return Card(VStack(6,
            Columns("Auto,*", 8, Icon("Warning", 16, "WarningBrush"), Text(L.Format("win.uninstaller.runningFormat", app.DisplayName), 12)),
            Caption(names),
            new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { close, force } }), 8);
    }

    private async Task ForceQuitAsync(InstalledApp app)
    {
        if (await ConfirmAsync(this, L.Get("win.uninstaller.forceQuitTitle"), L.Format("win.uninstaller.forceQuitFormat", app.DisplayName), L.Get("win.uninstaller.forceQuit"), destructive: true))
        {
            await _service.CloseRunningAsync(force: true);
        }
    }

    private async Task ConfirmUninstallAsync(InstalledApp app)
    {
        var message = L.Get("win.uninstaller.confirmMessage");
        if (_service.RunningCopies.Count > 0)
        {
            message += "\n\n" + L.Get("win.uninstaller.confirmRunning");
        }

        if (await ConfirmAsync(this, L.Format("win.uninstaller.confirmTitleFormat", app.DisplayName), message, L.Get("win.uninstaller.uninstall"), destructive: true))
        {
            await _service.UninstallAsync();
        }
    }

    // ── Uninstalling ─────────────────────────────────────────────────

    private Control Uninstalling()
    {
        var app = _service.Selected!;
        var stack = VStack(10, Header(app, closable: false));
        stack.Children.Add(Text(L.Format("win.uninstaller.waitingFormat", app.DisplayName), 13, FontWeight.SemiBold));
        stack.Children.Add(Progress(_service.Progress));
        if (app.Kind != InstalledAppKind.Msix)
        {
            stack.Children.Add(Caption(L.Get("win.uninstaller.waitingHint")));
            stack.Children.Add(Button(L.Get("win.uninstaller.finished"), _service.StopWaiting, "Checkmark", stretch: _compact));
        }

        return stack;
    }

    private Control StillInstalled()
    {
        var app = _service.Selected!;
        var retry = Button(L.Get("win.uninstaller.tryAgain"), () => _ = _service.UninstallAsync(), "ArrowSync");
        var back = Button(L.Get("win.uninstaller.back"), _service.Reset);
        return VStack(10,
            Header(app, closable: true),
            Note("Warning", L.Format("win.uninstaller.stillInstalledFormat", app.DisplayName), "WarningBrush"),
            new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { retry, back } });
    }

    private Control Busy(string title)
    {
        var stack = VStack(10);
        if (_service.Selected is { } app)
        {
            stack.Children.Add(Header(app, closable: false));
        }

        stack.Children.Add(HStack(8, Progress(null, 60), Text(title, 13)));
        return stack;
    }

    // ── Leftovers review ─────────────────────────────────────────────

    private Control Results()
    {
        var app = _service.Selected!;
        var leftovers = _service.Leftovers;
        var stack = VStack(10, Header(app, closable: false));
        stack.Children.Add(Columns("Auto,*", 8, Icon("CheckmarkCircle", 18, "SuccessBrush"), Text(L.Format("win.uninstaller.uninstalledFormat", app.DisplayName), 13, FontWeight.SemiBold)));

        if (leftovers.Count == 0)
        {
            stack.Children.Add(Caption(L.Get("win.uninstaller.nothingLeft")));
            stack.Children.Add(Button(L.Get("Strings.uninstallerAnother"), _service.FinishWithoutRemoving, stretch: _compact));
            return stack;
        }

        stack.Children.Add(Caption(L.Format("win.uninstaller.foundFormat", leftovers.Count, Size(_service.TotalBytes))));
        var groups = VStack(10);
        foreach (var group in leftovers.GroupBy(l => l.Category).OrderBy(g => g.Key))
        {
            groups.Children.Add(Group(group.Key, group.ToList()));
        }

        Control list = groups;
        if (_compact)
        {
            // The margin keeps the overlay scroll bar off the reveal buttons.
            groups.Margin = new Thickness(0, 0, 10, 0);
            list = new ScrollViewer { Content = groups, MaxHeight = 300, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        stack.Children.Add(Card(list, 8));
        if (leftovers.Any(l => l.RequiresAdministrator && l.Kind is LeftoverKind.RegistryKey or LeftoverKind.StartupValue) && !_service.IsElevated)
        {
            stack.Children.Add(Caption(L.Get("win.uninstaller.registryAdminNote")));
        }

        var selectable = leftovers.Count(l => !l.IsReviewOnly);
        stack.Children.Add(Columns("*,Auto", 8,
            Caption(L.Format("Strings.uninstallerSelectedFormat", _service.SelectedCount, selectable)),
            Text(Size(_service.SelectedBytes), 12, FontWeight.SemiBold)));
        var remove = Button(L.Get("Strings.uninstallerRemove"), () => _ = ConfirmRemoveAsync(), "Delete", accent: true, stretch: _compact);
        remove.IsEnabled = _service.SelectedCount > 0;
        var skip = Button(L.Get("win.uninstaller.keepAll"), _service.FinishWithoutRemoving, stretch: _compact);
        // "Move to Recycle Bin" is the long label; it gets the room it needs in the panel.
        stack.Children.Add(_compact ? Columns("Auto,*", 8, skip, remove) : HStack(8, remove, skip));
        return stack;
    }

    private Control Group(LeftoverCategory category, IReadOnlyList<LeftoverItem> items)
    {
        var stack = VStack(4, SectionTitle(L.Get(CategoryKey(category))));
        if (category == LeftoverCategory.Review)
        {
            stack.Children.Add(Caption(L.Get("win.uninstaller.reviewNote")));
            var tools = new WrapPanel { ItemSpacing = 12, LineSpacing = 4 };
            if (items.Any(i => i.Kind == LeftoverKind.ScheduledTask))
            {
                tools.Children.Add(LinkButton(L.Get("win.uninstaller.openTaskScheduler"), () => _shell.OpenFile("taskschd.msc")));
            }

            if (items.Any(i => i.Kind == LeftoverKind.Service))
            {
                tools.Children.Add(LinkButton(L.Get("win.uninstaller.openServices"), () => _shell.OpenFile("services.msc")));
            }

            stack.Children.Add(tools);
        }

        foreach (var item in items)
        {
            stack.Children.Add(LeftoverRow(item));
        }

        return stack;
    }

    private Control LeftoverRow(LeftoverItem item)
    {
        var blocked = item.IsReviewOnly || (item.RequiresAdministrator && item.Kind is LeftoverKind.RegistryKey or LeftoverKind.StartupValue && !_service.IsElevated);
        Control lead;
        if (blocked)
        {
            lead = new Border { Width = 20 };
        }
        else
        {
            var box = CheckBox(_service.IsIncluded(item), value => _service.SetIncluded(item, value), item.Name);
            box.Width = 20;
            lead = box;
        }

        var icon = Icon(item.Kind switch
        {
            LeftoverKind.Folder => "Folder",
            LeftoverKind.RegistryKey => "Database",
            LeftoverKind.StartupValue => "Rocket",
            LeftoverKind.ScheduledTask => "CalendarClock",
            LeftoverKind.Service => "Settings",
            _ => "Document",
        }, 16, "TextSecondaryBrush");

        var badges = HStack(4);
        if (!item.Exact && !item.IsReviewOnly)
        {
            var optional = Pill(L.Get("win.uninstaller.optional"), "WarningSoftBrush", "WarningBrush");
            ToolTip.SetTip(optional, item.Evidence is { } evidence ? L.Format("win.uninstaller.relatedFormat", evidence) : L.Get("win.uninstaller.optional"));
            badges.Children.Add(optional);
        }

        if (item.RequiresAdministrator && !_service.IsElevated)
        {
            badges.Children.Add(Pill(L.Get("win.cleaner.adminBadge")));
        }

        var name = Text(item.Name, 12, FontWeight.SemiBold, wrap: false);
        name.MaxWidth = _compact ? 150 : 380;
        var texts = VStack(0, Columns("Auto,Auto", 6, name, badges), PathText(item.Detail ?? item.Path));
        Control? reveal = item.Kind is LeftoverKind.File or LeftoverKind.Folder
            ? IconButton("FolderOpen", L.Get("Strings.cleanerRevealInFinder"), () => _shell.RevealInExplorer(item.Path), 12)
            : null;
        var size = item.Size > 0 ? Caption(Size(item.Size), wrap: false) : null;
        var row = Columns("Auto,Auto,*,Auto,Auto", 6, lead, icon, texts, size, reveal);
        AutomationProperties.SetName(row, item.Name);
        return row;
    }

    private static string CategoryKey(LeftoverCategory category) => category switch
    {
        LeftoverCategory.Application => "Strings.uninstallerCatApp",
        LeftoverCategory.Package => "win.uninstaller.catPackage",
        LeftoverCategory.Support => "Strings.uninstallerCatSupport",
        LeftoverCategory.Caches => "Strings.uninstallerCatCaches",
        LeftoverCategory.Logs => "Strings.uninstallerCatLogs",
        LeftoverCategory.Registry => "win.uninstaller.catRegistry",
        LeftoverCategory.Shortcuts => "win.uninstaller.catShortcuts",
        LeftoverCategory.Startup => "win.uninstaller.catStartup",
        _ => "win.uninstaller.catReview",
    };

    private async Task ConfirmRemoveAsync()
    {
        var chosen = _service.Leftovers.Where(_service.IsIncluded).ToList();
        var message = L.Format("win.uninstaller.removeMessageFormat", chosen.Count, Size(_service.SelectedBytes));
        if (chosen.Any(c => c.Kind is LeftoverKind.RegistryKey or LeftoverKind.StartupValue))
        {
            message += "\n\n" + L.Get("win.uninstaller.removeRegistryNote");
        }

        if (await ConfirmAsync(this, L.Get("win.uninstaller.removeTitle"), message, L.Get("Strings.uninstallerRemove")))
        {
            await _service.RemoveSelectedAsync();
        }
    }

    // ── Done ─────────────────────────────────────────────────────────

    private Control Done()
    {
        var result = _service.LastResult ?? new LeftoverResult();
        var stack = VStack(10);
        stack.Children.Add(VStack(4,
            Icon(result.Succeeded ? "CheckmarkCircle" : "Warning", 32, result.Succeeded ? "SuccessBrush" : "WarningBrush"),
            new TextBlock { Text = L.Get("Strings.uninstallerDoneTitle"), FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = L.Format("Strings.uninstallerFreedFormat", Size(result.FreedBytes)), Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center }));

        if (result.FailedNames.Count > 0)
        {
            var names = string.Join(", ", result.FailedNames.Take(4));
            if (result.FailedNames.Count > 4)
            {
                names += " " + L.Format("Strings.uninstallerFailedMoreFormat", result.FailedNames.Count - 4);
            }

            stack.Children.Add(Note("Warning", L.Get("Strings.uninstallerSomeFailed") + " " + names, "WarningBrush"));
        }

        if (result.InUse > 0)
        {
            stack.Children.Add(Caption(L.Format("win.cleaner.inUseFormat", result.InUse)));
        }

        if (result.DeletedPermanently > 0)
        {
            stack.Children.Add(Colored(L.Format("win.cleaner.permanentlyDeletedFormat", result.DeletedPermanently), "WarningBrush"));
        }

        if (result.BackupFolder is { } backup)
        {
            stack.Children.Add(VStack(2, Caption(L.Get("win.uninstaller.backupNote")), LinkButton(L.Get("win.cleaner.showBackup"), () => _shell.RevealInExplorer(backup))));
        }
        else if (result.Removed > 0)
        {
            stack.Children.Add(Caption(L.Get("Strings.cleanerDoneNote")));
        }

        var another = Button(L.Get("Strings.uninstallerAnother"), _service.Reset, "ArrowCounterclockwise", accent: true, stretch: _compact);
        if (!_compact)
        {
            another.HorizontalAlignment = HorizontalAlignment.Center;
        }

        stack.Children.Add(another);
        return stack;
    }

    private static Control Note(string icon, string text, string brushKey) =>
        Columns("Auto,*", 8, Icon(icon, 14, brushKey), Colored(text, brushKey == "TextSecondaryBrush" ? "TextSecondaryBrush" : brushKey));
}
