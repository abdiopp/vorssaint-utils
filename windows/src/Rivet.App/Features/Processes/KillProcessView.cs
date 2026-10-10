// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
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
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Platform;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.Processes;

/// <summary>
/// Kill Process (spec §3.6): filter, sortable columns, a virtualized list
/// refreshed every 3 s while its window is active, Kill and the "…" menu
/// (Force Kill, Kill All, Kill Process Tree, Restart, Copy PID, Copy Path),
/// each confirmed. Processes Windows refuses to end offer "Restart as
/// administrator"; nothing runs elevated otherwise.
/// </summary>
public sealed class KillProcessView : UserControl
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly ProcessService _processes;
    private readonly IElevationService _elevation;
    private readonly IClipboardService? _clipboard;
    private readonly bool _compact;
    private readonly TextBox _filter;
    private readonly ObservableCollection<ProcessRow> _rows = [];
    private readonly ListBox _list;
    private readonly TextBlock _count = new() { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _busy = Progress(null, 40);
    private readonly ContentControl _headers = new();
    private readonly StackPanel _empty;
    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };

    public KillProcessView(IServiceProvider services, bool compact)
    {
        _processes = services.GetRequiredService<ProcessService>();
        _elevation = services.GetRequiredService<IElevationService>();
        _clipboard = services.GetService<IClipboardService>();
        _compact = compact;

        _filter = new TextBox { PlaceholderText = L.Get("killProcess.searchPlaceholder"), MinWidth = 0 };
        AutomationProperties.SetName(_filter, L.Get("killProcess.searchPlaceholder"));
        _filter.TextChanged += (_, _) => Apply();
        _list = new ListBox
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Single,
            MaxHeight = compact ? 300 : 460,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<ProcessRow>((row, _) => row is null ? new Panel() : Row(row)),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => _list.SelectedItem = null;
        _empty = VStack(2, Text(L.Get("killProcess.emptyStateTitle"), 13, FontWeight.SemiBold), Caption(L.Get("portManager.emptyHint")));
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Margin = new Thickness(0, 12);
        _timer.Tick += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window || window.IsActive)
            {
                _ = _processes.RefreshAsync(force: true);
            }
        };

        var refresh = IconButton("ArrowSync", L.Get("killProcess.refreshTooltip"), () => _ = _processes.RefreshAsync(force: true));
        Content = VStack(8,
            _compact
                ? Columns("*,Auto,Auto", 6, _filter, _busy, refresh)
                : VStack(8, Columns("*,Auto,Auto", 8, _count, _busy, refresh), _filter),
            _headers,
            Card(VStack(0, _list, _empty), 2),
            _compact ? _count : null);
        Apply();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _processes.Changed += OnChanged;
        _ = _processes.RefreshAsync(force: true);
        _timer.Start();
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _processes.Changed -= OnChanged;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Apply);

    /// <summary>Refreshes the visible rows in place, plus the count, headers and busy indicator.</summary>
    private void Apply()
    {
        var rows = _processes.Rows.Where(r => ProcessListRules.Matches(r, _filter.Text)).ToList();
        SyncList(_rows, rows);
        _count.Text = L.Format("killProcess.processCountFormat", _processes.ProcessCount);
        _busy.IsVisible = _processes.IsRefreshing;
        _empty.IsVisible = rows.Count == 0 && _processes.HasSnapshot;
        _list.IsVisible = rows.Count > 0;
        _headers.Content = Headers();
    }

    private Control Headers()
    {
        Control Header(string key, ProcessSortColumn column, HorizontalAlignment alignment)
        {
            var active = _processes.SortColumn == column;
            var label = new TextBlock { Text = L.Get(key), FontSize = 11, FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
            var content = HStack(2, label, active ? Icon(_processes.SortAscending ? "ChevronUp" : "ChevronDown", 9, "TextSecondaryBrush") : null);
            content.HorizontalAlignment = alignment;
            var button = new Button { Classes = { "row" }, Padding = new Thickness(4, 2), Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = alignment };
            AutomationProperties.SetName(button, L.Get(key));
            button.Click += (_, _) => _processes.ClickColumn(column);
            return button;
        }

        return _compact
            ? Columns("*,Auto,Auto,Auto", 2,
                Header("killProcess.columnProcess", ProcessSortColumn.Name, HorizontalAlignment.Left),
                Header("killProcess.columnCPU", ProcessSortColumn.Cpu, HorizontalAlignment.Right),
                Header("killProcess.columnMemory", ProcessSortColumn.Memory, HorizontalAlignment.Right),
                Header("killProcess.columnPID", ProcessSortColumn.Pid, HorizontalAlignment.Right))
            : Columns($"*,{CpuWidth},{MemoryWidth},{PidWidth},{ActionsWidth}", 4,
                Header("killProcess.columnProcess", ProcessSortColumn.Name, HorizontalAlignment.Left),
                Header("killProcess.columnCPU", ProcessSortColumn.Cpu, HorizontalAlignment.Right),
                Header("killProcess.columnMemory", ProcessSortColumn.Memory, HorizontalAlignment.Right),
                Header("killProcess.columnPID", ProcessSortColumn.Pid, HorizontalAlignment.Right),
                null);
    }

    private const int CpuWidth = 56;
    private const int MemoryWidth = 76;
    private const int PidWidth = 60;
    private const int ActionsWidth = 92;

    private Control Row(ProcessRow row)
    {
        var name = Text(row.Name, 12, FontWeight.SemiBold, wrap: false);
        var title = row.MemberCount > 1
            ? Columns("Auto,Auto", 6, name, Pill(L.Format("killProcess.processCountFormat", row.MemberCount)))
            : (Control)name;
        var icon = Icon(row.HasWindows ? "Window" : "Cube", 18, row.IsProtected ? "TextTertiaryBrush" : "TextSecondaryBrush");
        var kill = Button(L.Get("killProcess.killButton"), () => _ = RunAsync(row, KillMode.Kill));
        kill.Padding = new Thickness(8, 2);
        kill.FontSize = 11;
        kill.IsEnabled = !row.IsProtected;
        var more = IconButton("MoreHorizontal", L.Get("win.processes.moreActions"), () => { }, 14);
        more.Flyout = MoreMenu(row);

        Control line;
        if (_compact)
        {
            var details = VStack(0, title, Caption($"{Percent(row.CpuPercent)}  ·  {Size(row.MemoryBytes)}  ·  {L.Format("killProcess.pidLabelFormat", row.Pid)}", wrap: false));
            line = Columns("Auto,*,Auto,Auto", 6, icon, details, kill, more);
        }
        else
        {
            var texts = VStack(0, title, row.Path is { } path ? PathText(path) : Caption(row.ImageName, wrap: false));
            line = Columns($"Auto,*,{CpuWidth},{MemoryWidth},{PidWidth},{ActionsWidth}", 4,
                icon,
                texts,
                Right(Percent(row.CpuPercent)),
                Right(Size(row.MemoryBytes)),
                Right(row.Pid.ToString(CultureInfo.InvariantCulture)),
                HStack(2, kill, more));
        }

        line.Margin = new Thickness(0, 1);
        AutomationProperties.SetName(line, row.Name);
        if (row.IsProtected)
        {
            ToolTip.SetTip(line, L.Get("win.processes.protected"));
        }

        return line;
    }

    private static TextBlock Right(string text) =>
        new() { Text = text, FontSize = 11, Classes = { "caption", "mono" }, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private MenuFlyout MoreMenu(ProcessRow row)
    {
        var flyout = new MenuFlyout();
        void Add(string text, Action click, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => click();
            flyout.Items.Add(item);
        }

        Add(L.Get("killProcess.forceKillButton"), () => _ = RunAsync(row, KillMode.ForceKill), !row.IsProtected);
        Add(L.Format("killProcess.killAllFormat", row.Name), () => _ = RunAsync(row, KillMode.KillAll), !row.IsProtected);
        Add(L.Get("killProcess.killTreeButton"), () => _ = RunAsync(row, KillMode.KillTree), !row.IsProtected);
        if (row.CanRestart)
        {
            Add(L.Get("killProcess.restartButton"), () => _ = RunAsync(row, KillMode.Restart));
        }

        flyout.Items.Add(new Separator());
        Add(L.Get("killProcess.copyPID"), () => _clipboard?.SetText(row.Pid.ToString(CultureInfo.InvariantCulture)), _clipboard is not null);
        Add(L.Get("killProcess.copyPath"), () => _clipboard?.SetText(row.Path ?? string.Empty), _clipboard is not null && row.Path is not null);
        return flyout;
    }

    /// <summary>Confirms, kills, and explains a refusal (offering "Restart as administrator" for processes Windows protects from us).</summary>
    private async Task RunAsync(ProcessRow row, KillMode mode)
    {
        var (title, confirm) = mode switch
        {
            KillMode.ForceKill => (L.Format("killProcess.confirmForceKillFormat", row.Name), L.Get("killProcess.forceKillButton")),
            KillMode.KillAll => (L.Format("killProcess.confirmKillAllFormat", row.Name), L.Get("killProcess.killButton")),
            KillMode.KillTree => (L.Format("killProcess.confirmKillTreeFormat", row.Name), L.Get("killProcess.killTreeButton")),
            KillMode.Restart => (L.Format("win.processes.confirmRestartFormat", row.Name), L.Get("killProcess.restartButton")),
            _ => (L.Format("killProcess.confirmKillFormat", row.Name), L.Get("killProcess.killButton")),
        };
        var message = mode == KillMode.KillAll ? string.Empty : row.Path ?? row.ImageName;
        if (mode == KillMode.Kill && !row.HasWindows)
        {
            message = (message + "\n\n" + L.Get("win.processes.noWindowsNote")).Trim();
        }

        if (!await ConfirmAsync(this, title, message, confirm, destructive: mode != KillMode.Restart))
        {
            return;
        }

        var report = await _processes.KillAsync(row, mode);
        await ExplainAsync(this, report, _elevation);
    }

    /// <summary>Shared by Kill Process and Port Manager: what to say when a kill did not fully work.</summary>
    internal static async Task ExplainAsync(Control anchor, KillReport report, IElevationService elevation)
    {
        if (report.NeedsAdministrator.Count > 0 && !elevation.IsElevated)
        {
            if (await ConfirmAsync(anchor, L.Get("killProcess.killFailedTitle"), L.Format("win.processes.accessDeniedFormat", report.TargetName), L.Get("win.cleaner.restartAsAdmin")))
            {
                elevation.RestartElevated();
            }
        }
        else if (report.RelaunchFailed)
        {
            await NotifyAsync(anchor, L.Get("killProcess.killFailedTitle"), L.Format("win.processes.relaunchFailedFormat", report.TargetName));
        }
        else if (report.Failed > 0 || report.NeedsAdministrator.Count > 0)
        {
            await NotifyAsync(anchor, L.Get("killProcess.killFailedTitle"), L.Get("killProcess.killFailedMessage"));
        }
    }
}
