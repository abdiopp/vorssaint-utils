// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Maintenance;
using Rivet.App.Modules;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.Cleaner;

/// <summary>
/// The Cleaner (spec §3.1.1): Idle → Scanning → Results → Cleaning → Done.
/// One view for the panel (compact, scrolls with the panel so the footer stays
/// reachable) and the Settings page. The panel stays open while a scan or a
/// clean runs.
/// </summary>
public sealed class CleanerView : UserControl
{
    private static readonly HashSet<CleanerGroup> Expanded = [];
    private static bool _scheduleOpen;

    private readonly IServiceProvider _services;
    private readonly CleanerService _cleaner;
    private readonly ISettingsStore _settings;
    private readonly IShellService _shell;
    private readonly IElevationService _elevation;
    private readonly IAppShell? _appShell;
    private readonly bool _compact;
    private IDisposable? _keepOpen;
    private IDisposable? _ageSubscription;

    public CleanerView(IServiceProvider services, bool compact)
    {
        _services = services;
        _cleaner = services.GetRequiredService<CleanerService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _shell = services.GetRequiredService<IShellService>();
        _elevation = services.GetRequiredService<IElevationService>();
        _appShell = services.GetService<IAppShell>();
        _compact = compact;
        Build();
    }

    /// <summary>Opens groups (previews and tests); the panel remembers open groups while the app runs.</summary>
    internal static void ExpandGroups(params CleanerGroup[] groups)
    {
        Expanded.Clear();
        Expanded.UnionWith(groups);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cleaner.Changed += OnChanged;
        _ageSubscription = _settings.Observe(() => Dispatcher.UIThread.Post(Build), CleanerSettings.ScreenshotAgeDays);
        Build();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cleaner.Changed -= OnChanged;
        _ageSubscription?.Dispose();
        _keepOpen?.Dispose();
        _keepOpen = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Build);

    private void Build()
    {
        var busy = _cleaner.Phase is CleanerPhase.Scanning or CleanerPhase.Cleaning;
        if (busy && _keepOpen is null && _appShell is not null)
        {
            _keepOpen = _appShell.KeepPanelOpen("cleaner");
        }
        else if (!busy)
        {
            _keepOpen?.Dispose();
            _keepOpen = null;
        }

        Content = _cleaner.Phase switch
        {
            CleanerPhase.Scanning => Busy(L.Get("Strings.cleanerScanning"), ScanningLine(), cancel: true),
            CleanerPhase.Results => Results(),
            CleanerPhase.Cleaning => Busy(L.Get("Strings.cleanerCleaning"), null, cancel: false),
            CleanerPhase.Done => Done(),
            _ => Idle(),
        };
    }

    private string? ScanningLine() =>
        _cleaner.ScanningCategory is { } category ? L.Format("win.cleaner.scanningFormat", L.Get(CategoryTitleKey(category))) : null;

    private static string CategoryTitleKey(CleanerCategory category) => category switch
    {
        CleanerCategory.Leftovers => "Strings.cleanerCatLeftovers",
        CleanerCategory.LoginItems => "Strings.cleanerCatLoginItems",
        CleanerCategory.Logs => "Strings.cleanerCatLogs",
        CleanerCategory.Developer => "Strings.cleanerCatDeveloper",
        CleanerCategory.Trash => "Strings.cleanerCatTrash",
        CleanerCategory.DeviceBackups => "Strings.cleanerCatDeviceBackups",
        CleanerCategory.Screenshots => "Strings.cleanerCatScreenshots",
        _ => "Strings.cleanerCatCaches",
    };

    // ── Idle ─────────────────────────────────────────────────────────

    private Control Idle()
    {
        var stack = VStack(10);
        stack.Children.Add(VStack(6,
            Icon("Sparkle", 32),
            new TextBlock { Text = L.Get("Strings.cleanerIntroTitle"), FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center },
            new TextBlock { Text = L.Get("Strings.cleanerIntroCaption"), Classes = { "caption" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }));
        var scan = Button(L.Get("Strings.cleanerScan"), () => _ = _cleaner.ScanAsync(attended: true), "Search", accent: true, stretch: _compact);
        if (!_compact)
        {
            scan.HorizontalAlignment = HorizontalAlignment.Center;
            scan.MinWidth = 160;
        }

        stack.Children.Add(scan);
        if (!_cleaner.IsElevated)
        {
            stack.Children.Add(AdminNote());
        }

        stack.Children.Add(ScheduleCard());
        stack.Children.Add(ScreenshotsCard());
        return stack;
    }

    private Control AdminNote() => Card(VStack(4,
        Caption(L.Get("win.cleaner.adminNote")),
        LinkButton(L.Get("win.cleaner.restartAsAdmin"), () => _elevation.RestartElevated())), 8);

    private Control ScheduleCard()
    {
        var editor = new CleanerScheduleEditor(_services);
        if (!_compact)
        {
            return Card(editor);
        }

        // In the panel the card folds to one line: icon, title, summary, chevron.
        var header = new Button { Classes = { "row" }, Padding = new Thickness(2) };
        header.Content = Columns("Auto,*,Auto,Auto", 8,
            Icon("Clock", 16),
            Text(L.Get("Strings.cleanerScheduleTitle"), 13, FontWeight.SemiBold),
            Caption(CleanerScheduleEditor.Summary(_settings), wrap: false),
            Icon(_scheduleOpen ? "ChevronUp" : "ChevronDown", 12, "TextSecondaryBrush"));
        header.Click += (_, _) =>
        {
            _scheduleOpen = !_scheduleOpen;
            Build();
        };
        return Card(_scheduleOpen ? VStack(8, header, editor) : header, 6);
    }

    private Control ScreenshotsCard()
    {
        var days = _settings.Get(CleanerSettings.ScreenshotAgeDays);
        var options = CleanerSettings.ScreenshotAgeOptions.ToList();
        if (!options.Contains(days))
        {
            options.Add(days);
            options.Sort();
        }

        var combo = new ComboBox
        {
            ItemsSource = options.Select(d => d == 0 ? L.Get("Strings.cleanerScheduleOff") : L.Format("Strings.cleanerScreenshotsAfterFormat", d)).ToList(),
            SelectedIndex = options.IndexOf(days),
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                _settings.Set(CleanerSettings.ScreenshotAgeDays, options[combo.SelectedIndex]);
            }
        };
        var row = Columns("Auto,*,Auto", 8, Icon("Camera", 16), Text(L.Get("Strings.cleanerCatScreenshots")), combo);
        return Card(_compact ? row : VStack(6, row, Caption(L.Get("Strings.cleanerScreenshotsSettingCaption"))), _compact ? 6 : 10);
    }

    // ── Scanning / cleaning ──────────────────────────────────────────

    private Control Busy(string title, string? line, bool cancel)
    {
        var stack = VStack(8,
            Icon("Sparkle", 28),
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            line is null ? null : new TextBlock { Text = line, Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center },
            Progress(null, 160));
        stack.Children[^1].HorizontalAlignment = HorizontalAlignment.Center;
        if (cancel)
        {
            var button = Button(L.Get("Strings.uninstallerCancel"), _cleaner.Reset);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(button);
        }

        stack.Margin = new Thickness(0, 16);
        return stack;
    }

    // ── Results ──────────────────────────────────────────────────────

    private Control Results()
    {
        var items = _cleaner.Items;
        var stack = VStack(10);
        stack.Children.Add(Columns("Auto,*,Auto", 8,
            Icon("Sparkle", 20),
            VStack(0, Text(L.Get("Strings.cleanerName"), 14, FontWeight.SemiBold), Caption(L.Format("win.cleaner.foundFormat", Size(_cleaner.TotalBytes)))),
            IconButton("Dismiss", L.Get("Strings.menuClose"), _cleaner.Reset)));

        if (_cleaner.SkippedRunning.Count > 0)
        {
            stack.Children.Add(Colored(L.Format("win.cleaner.skippedRunningFormat", string.Join(", ", _cleaner.SkippedRunning)), "WarningBrush"));
        }

        if (_cleaner.AdministratorSkipped && !_cleaner.IsElevated)
        {
            stack.Children.Add(AdminNote());
        }

        if (items.Count == 0)
        {
            stack.Children.Add(VStack(6, Icon("CheckmarkStarburst", 28, "SuccessBrush"),
                new TextBlock { Text = L.Get("Strings.cleanerNothingFound"), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }));
            stack.Children.Add(Button(L.Get("Strings.cleanerAgain"), _cleaner.Reset));
            return stack;
        }

        AddSection(stack, L.Get("Strings.cleanerSafeSection"), CleanerGroups.Safe, items);
        AddSection(stack, L.Get("Strings.cleanerOptionalSection"), CleanerGroups.Optional, items);

        var clean = Button(L.Format("Strings.cleanerCleanSizeFormat", Size(_cleaner.SelectedBytes)), () => _ = CleanAsync(), accent: true);
        clean.IsEnabled = _cleaner.SelectedCount > 0;
        stack.Children.Add(Columns("*,Auto,Auto", 8,
            Caption(L.Format("Strings.uninstallerSelectedFormat", _cleaner.SelectedCount, items.Count)),
            Button(L.Get("Strings.uninstallerCancel"), _cleaner.Reset),
            clean));
        return stack;
    }

    private void AddSection(StackPanel stack, string title, IReadOnlyList<CleanerGroup> groups, IReadOnlyList<CleanerItem> items)
    {
        var present = groups.Where(g => items.Any(i => i.Group == g)).ToList();
        if (present.Count == 0)
        {
            return;
        }

        stack.Children.Add(SectionTitle(title));
        var card = VStack(2);
        foreach (var group in present)
        {
            card.Children.Add(GroupRow(group, items.Where(i => i.Group == group).ToList()));
        }

        stack.Children.Add(Card(card, 4));
    }

    private Control GroupRow(CleanerGroup group, List<CleanerItem> members)
    {
        var state = _cleaner.GroupState(group);
        var box = new CheckBox { IsChecked = state, IsThreeState = true, MinWidth = 0, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetName(box, L.Get(CleanerGroups.TitleKey(group)));
        box.Click += (_, _) => _cleaner.SetGroupIncluded(group, state != true);

        var caption = group == CleanerGroup.Screenshots
            ? L.Format(CleanerGroups.CaptionKey(group), _settings.Get(CleanerSettings.ScreenshotAgeDays))
            : L.Get(CleanerGroups.CaptionKey(group));
        // The title wraps (a StackPanel row would let it run under the size); the badge goes below it.
        var titleRow = VStack(3, Text(L.Get(CleanerGroups.TitleKey(group)), 13, FontWeight.SemiBold));
        if (members.Any(m => m.Permanent))
        {
            titleRow.Children.Add(Pill(L.Get("win.cleaner.permanentBadge"), "WarningSoftBrush", "WarningBrush"));
        }

        var expanded = Expanded.Contains(group);
        var header = new Button { Classes = { "row" }, Padding = new Thickness(4, 6) };
        var groupIcon = Icon(CleanerGroups.Icon(group), 16);
        groupIcon.VerticalAlignment = VerticalAlignment.Top;
        groupIcon.Margin = new Thickness(0, 1, 0, 0);
        var total = members.Sum(m => m.Size);
        var sizeText = Text(total > 0 ? Size(total) : string.Empty, 12);
        sizeText.VerticalAlignment = VerticalAlignment.Top;
        var chevron = Icon(expanded ? "ChevronUp" : "ChevronDown", 12, "TextSecondaryBrush");
        chevron.VerticalAlignment = VerticalAlignment.Top;
        chevron.Margin = new Thickness(0, 3, 0, 0);
        header.Content = Columns("Auto,*,Auto,Auto", 8, groupIcon, VStack(1, titleRow, Caption(caption)), sizeText, chevron);
        header.Click += (_, _) =>
        {
            if (!Expanded.Remove(group))
            {
                Expanded.Add(group);
            }

            Build();
        };

        var body = VStack(2, Columns("Auto,*", 6, box, header));
        if (!expanded)
        {
            return body;
        }

        var details = VStack(2);
        details.Margin = new Thickness(26, 0, 0, 6);
        var note = group switch
        {
            CleanerGroup.Leftovers => "Strings.cleanerLeftoversNote",
            CleanerGroup.LoginItems => "Strings.cleanerLoginItemsNote",
            _ => null,
        };
        if (note is not null)
        {
            details.Children.Add(Colored(L.Get(note), group == CleanerGroup.Leftovers ? "WarningBrush" : "TextSecondaryBrush", 11));
        }

        foreach (var item in members.Take(200))
        {
            details.Children.Add(ItemRow(item));
        }

        body.Children.Add(details);
        return body;
    }

    private Control ItemRow(CleanerItem item)
    {
        var box = CheckBox(_cleaner.IsIncluded(item), include => _cleaner.SetIncluded(item, include), item.Name);
        var name = item.Kind == CleanerItemKind.RecycleBin ? L.Get("Strings.cleanerCatTrash") : item.Name;
        var secondary = item.Kind switch
        {
            CleanerItemKind.RecycleBin => null,
            CleanerItemKind.StartupValue => item.Path,
            _ => item.Detail ?? SafePaths.Display(SafePaths.Parent(item.Path) ?? item.Path, ProfileFolder()),
        };
        var texts = VStack(0, Text(name, 12, wrap: false, trimming: TextTrimming.CharacterEllipsis), secondary is null ? null : PathText(secondary));
        var row = Columns("Auto,*,Auto", 6, box, texts, Caption(item.Kind == CleanerItemKind.StartupValue ? string.Empty : Size(item.Size), wrap: false));
        ToolTip.SetTip(row, item.Path);
        if (item.Kind is CleanerItemKind.File or CleanerItemKind.Folder)
        {
            row.ContextMenu = Menu((L.Get("Strings.cleanerRevealInFinder"), () => _shell.RevealInExplorer(item.Path), true));
        }

        return row;
    }

    private string? ProfileFolder() => _services.GetService<IKnownFolders>()?.Folders.UserProfile;

    private async Task CleanAsync()
    {
        if (_cleaner.Items.Any(i => i.Kind == CleanerItemKind.RecycleBin && _cleaner.IsIncluded(i))
            && !await ConfirmAsync(this, L.Get("win.cleaner.emptyBinTitle"), L.Get("win.cleaner.emptyBinMessage"), L.Format("Strings.cleanerCleanSizeFormat", Size(_cleaner.SelectedBytes)), destructive: true))
        {
            return;
        }

        await _cleaner.CleanSelectedAsync(escalate: true);
    }

    // ── Done ─────────────────────────────────────────────────────────

    private Control Done()
    {
        var result = _cleaner.LastResult ?? new CleanResult();
        var stack = VStack(8,
            Icon(result.Failed == 0 ? "CheckmarkCircle" : "Warning", 30, result.Failed == 0 ? "SuccessBrush" : "WarningBrush"),
            new TextBlock { Text = L.Get("Strings.uninstallerDoneTitle"), FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = L.Format("Strings.uninstallerFreedFormat", Size(result.FreedBytes)), HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = L.Get("Strings.cleanerDoneNote"), Classes = { "caption" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (result.Failed > 0)
        {
            stack.Children.Add(Colored(L.Get("Strings.uninstallerSomeFailed"), "WarningBrush"));
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
            stack.Children.Add(Caption(L.Get("win.cleaner.backupNote")));
            stack.Children.Add(LinkButton(L.Get("win.cleaner.showBackup"), () => _shell.OpenFile(backup)));
        }

        var again = Button(L.Get("Strings.cleanerAgain"), _cleaner.Reset, accent: true);
        again.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(again);
        stack.Margin = new Thickness(0, 8);
        return stack;
    }
}
