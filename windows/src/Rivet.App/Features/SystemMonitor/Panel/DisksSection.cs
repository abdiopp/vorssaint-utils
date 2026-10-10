// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Panel;

public enum EjectState
{
    Idle,
    Ejecting,
    Ready,
    Failed,
}

/// <summary>Eject bookkeeping shared by the Disks tab and Settings (in memory only).</summary>
public sealed class DiskEjectTracker(IDiskActions actions, ISettingsStore settings, SystemMonitorService monitor)
{
    private readonly Dictionary<string, EjectState> _states = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public EjectState State(string volumeId) => _states.GetValueOrDefault(volumeId);

    /// <summary>Excluded drives match the volume name, id or mount path, ignoring case.</summary>
    public bool IsExcluded(DiskVolumeInfo volume) =>
        settings.Get(MonitorSettings.DiskEjectExcluded).Any(e =>
            string.Equals(e, volume.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e, volume.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.TrimEnd('\\'), volume.MountPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    public void Eject(DiskVolumeInfo volume)
    {
        if (!volume.IsEjectable || State(volume.Id) == EjectState.Ejecting)
        {
            return;
        }

        Set(volume.Id, EjectState.Ejecting);
        Task.Run(() =>
        {
            var (result, detail) = actions.Eject(volume);
            if (detail is not null)
            {
                Core.Diagnostics.Log.Info("monitor", $"Eject {volume.MountPath}: {result} ({detail})");
            }

            Dispatcher.UIThread.Post(() =>
            {
                Set(volume.Id, result == EjectResult.Ejected ? EjectState.Ready : EjectState.Failed);
                monitor.RequestRefresh();
            });
        });
    }

    /// <summary>Every ejectable disk once per physical disk, minus excluded drives.</summary>
    public IReadOnlyList<DiskVolumeInfo> EjectAllCandidates(IEnumerable<DiskVolume> disks) =>
        disks.Select(d => d.Info)
            .Where(v => v.IsEjectable && !IsExcluded(v))
            .GroupBy(v => v.DiskId ?? v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    public void EjectAll(IEnumerable<DiskVolume> disks)
    {
        foreach (var volume in EjectAllCandidates(disks))
        {
            Eject(volume);
        }
    }

    private void Set(string id, EjectState state)
    {
        _states[id] = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The Disks tab (spec §3.8.4): disk selector, then usage, activity, protection and tools for the selected disk.</summary>
internal sealed class DisksSection : MonitorSectionBase
{
    /// <summary>The selection survives tab switches while the disk stays mounted.</summary>
    private static string? _selectedId;

    private readonly DiskEjectTracker _eject;
    private string _volumeSignature = string.Empty;

    public DisksSection(IServiceProvider services)
        : base(services, MonitorSurface.Disk, "Strings.diskSection", MonitorSettings.DiskOrder)
    {
        _eject = services.GetRequiredService<DiskEjectTracker>();
        AttachedToVisualTree += (_, _) => _eject.Changed += OnEjectChanged;
        DetachedFromVisualTree += (_, _) => _eject.Changed -= OnEjectChanged;
    }

    protected override string SectionId => SystemMonitorModule.DiskSectionId;

    protected override string? EmptyText => L.Get("Strings.diskNoDisks");

    protected override IEnumerable<SettingDefinition> LayoutKeys => [MonitorSettings.GraphDisk, MonitorSettings.DiskEjectExcluded];

    protected override IReadOnlyList<BlockSpec> Blocks =>
    [
        new("usage", "Strings.monitorItemDiskUsage", "Storage", [("Strings.monitorItemDiskUsage", MonitorSettings.DiskUsage)], () => true, () => new UsageBlock(this)),
        new("activity", "Strings.monitorItemDiskActivity", "DataArea", [("Strings.monitorItemDiskActivity", MonitorSettings.DiskActivity)], () => true, () => new ActivityBlock(this)),
        new("smart", "Strings.monitorItemDiskSMART", "HeartPulse", [("Strings.monitorItemDiskSMART", MonitorSettings.DiskSmart)], () => false, () => throw new NotSupportedException()),
        new("protection", "Strings.monitorItemDiskProtection", "ArrowEject", [("Strings.monitorItemDiskProtection", MonitorSettings.DiskProtection)], () => true, () => new ProtectionBlock(this)),
        new("tools", "Strings.monitorItemDiskTools", "Wrench", [("Strings.monitorItemDiskTools", MonitorSettings.DiskTools)], () => true, () => new ToolsBlock(this)),
    ];

    private DiskVolume? Selected(MonitorSnapshot s) =>
        s.Disks.FirstOrDefault(d => d.Info.Id == _selectedId) ?? s.Disks.FirstOrDefault();

    protected override bool ShowEmptyState(MonitorSnapshot snapshot) => snapshot.Plan.Disk && snapshot.Disks.Count == 0;

    protected override Control? CreatePrelude()
    {
        var disks = Monitor.Latest.Disks;
        if (disks.Count == 0)
        {
            return null;
        }

        var selected = Selected(Monitor.Latest);
        var grid = new UniformGrid { Columns = disks.Count == 1 ? 1 : 2, ColumnSpacing = 6, RowSpacing = 6 };
        foreach (var disk in disks)
        {
            grid.Children.Add(SelectorButton(disk, disk.Info.Id == selected?.Info.Id));
        }

        return new StackPanel { Spacing = 5, Children = { MonitorUi.Caption(L.Get("Strings.diskSelect")), grid } };
    }

    protected override void AfterUpdate(MonitorSnapshot snapshot)
    {
        var signature = string.Join('|', snapshot.Disks.Select(d => d.Info.Id));
        if (signature != _volumeSignature)
        {
            _volumeSignature = signature;
            if (snapshot.Disks.All(d => d.Info.Id != _selectedId))
            {
                _selectedId = snapshot.Disks.FirstOrDefault()?.Info.Id;
            }

            Dispatcher.UIThread.Post(Rebuild);
        }
    }

    private void OnEjectChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private Control SelectorButton(DiskVolume disk, bool selected)
    {
        var info = disk.Info;
        var name = MonitorUi.Text(DisplayName(info), 12, FontWeight.SemiBold);
        name.TextTrimming = TextTrimming.PrefixCharacterEllipsis;
        ToolTip.SetTip(name, DisplayName(info));
        var used = MonitorUi.Caption($"{MetricFormat.Percent(info.UsedFraction)} {L.Get("Strings.diskUsed")}");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 5 };
        grid.Children.Add(MonitorUi.Icon(info.IsInternal ? "HardDrive" : "UsbStick", 15, selected ? "AccentBrush" : "TextSecondaryBrush"));
        var texts = new StackPanel { Spacing = 0, Children = { name, used } };
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (info.IsEjectable)
        {
            var state = _eject.State(info.Id);
            var (icon, tip, brush) = state switch
            {
                EjectState.Ejecting => ("ArrowSync", L.Get("Strings.diskEjecting"), "TextSecondaryBrush"),
                EjectState.Ready => ("Checkmark", L.Get("Strings.diskReadyToRemove"), "MetricGreenBrush"),
                EjectState.Failed => ("ArrowEject", L.Get("Strings.diskEjectFailed"), "MetricRedBrush"),
                _ => ("ArrowEject", L.Get("Strings.diskEject"), "TextSecondaryBrush"),
            };
            var eject = new Button { Classes = { "icon" }, Padding = new Thickness(3), Content = MonitorUi.Icon(icon, 12, brush), IsEnabled = state != EjectState.Ejecting };
            ToolTip.SetTip(eject, tip);
            AutomationProperties.SetName(eject, tip);
            eject.Click += (_, _) => _eject.Eject(info);
            Grid.SetColumn(eject, 2);
            grid.Children.Add(eject);
        }

        var button = new Button { Classes = { "row" }, Padding = new Thickness(6, 5), Content = grid };
        button.Bind(Button.BackgroundProperty, button.GetResourceObservable(selected ? "AccentFaintBrush" : "ChipBrush").ToBinding());
        AutomationProperties.SetName(button, DisplayName(info));
        button.Click += (_, _) =>
        {
            _selectedId = info.Id;
            Rebuild();
        };
        return button;
    }

    /// <summary>"Windows (C:)" like File Explorer.</summary>
    public static string DisplayName(DiskVolumeInfo info)
    {
        var letter = info.MountPath.TrimEnd('\\');
        return letter.Length == 2 && letter[1] == ':' ? $"{info.Name} ({letter})" : info.Name;
    }

    private sealed class UsageBlock : BlockView
    {
        private readonly DisksSection _owner;
        private readonly TextBlock _name = MonitorUi.Text(null, 13, FontWeight.SemiBold);
        private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        private readonly UsageBar _bar = new() { Height = 6 };
        private readonly TextBlock _used = MonitorUi.Value(null, 11.5, FontWeight.SemiBold);
        private readonly TextBlock _available = MonitorUi.Value(null, 11.5, FontWeight.Normal, "TextSecondaryBrush");
        private readonly TextBlock _capacity = MonitorUi.Value(null, 11.5, FontWeight.Normal, "TextSecondaryBrush");
        private readonly SymbolIconBox _icon = new();

        public UsageBlock(DisksSection owner)
        {
            _owner = owner;
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _icon.Icon, _name, _tags } };
            var numbers = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            numbers.Children.Add(_used);
            Grid.SetColumn(_available, 1);
            numbers.Children.Add(_available);
            var details = MonitorUi.IconButton("ChevronRight", L.Get("Strings.homebrewOperationShowDetails"), () => owner.ShowDetail(MetricKind.Disk));
            Root = MonitorUi.Block(L.Get("Strings.monitorItemDiskUsage"), new StackPanel { Spacing = 5, Children = { title, _bar, numbers, _capacity } }, details);
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            if (_owner.Selected(s) is not { } disk)
            {
                return;
            }

            var info = disk.Info;
            _icon.Set(info.IsInternal ? "HardDrive" : "UsbStick");
            _name.Text = DisplayName(info);
            _tags.Children.Clear();
            if (info.FileSystem is { } fs)
            {
                _tags.Children.Add(Tag(fs));
            }

            _tags.Children.Add(Tag(L.Get(info.IsInternal ? "Strings.diskInternal" : "Strings.diskExternal")));
            _bar.Fraction = info.UsedFraction;
            _bar.FillKey = MonitorBrushes.KeyFor(UsageTone.ForDisk(info.UsedFraction));
            _used.Text = $"{MetricFormat.Percent(info.UsedFraction, f.Culture)} {L.Get("Strings.diskUsed")}";
            _available.Text = $"{MetricFormat.DiskBytes(info.Free, f.Culture)} {L.Get("Strings.diskAvailable")}";
            _capacity.Text = $"{MetricFormat.DiskBytes(info.Used, f.Culture)} / {MetricFormat.DiskBytes(info.Total, f.Culture)}";
        }

        private static Border Tag(string text) => new() { Classes = { "pill" }, Child = new TextBlock { Text = text } };
    }

    private sealed class ActivityBlock : BlockView
    {
        private readonly DisksSection _owner;
        private readonly TextBlock _read = MonitorUi.Value(null, 13, FontWeight.SemiBold);
        private readonly TextBlock _write = MonitorUi.Value(null, 13, FontWeight.SemiBold);
        private readonly TextBlock _session = MonitorUi.Value(null, 11.5, FontWeight.Normal, "TextSecondaryBrush");
        private readonly Sparkline _graph = MonitorUi.Graph("AccentBrush", 30, "MetricPinkBrush");

        public ActivityBlock(DisksSection owner)
        {
            _owner = owner;
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
            columns.Children.Add(Column(L.Get("Strings.diskRead"), "AccentBrush", _read));
            var write = Column(L.Get("Strings.diskWrite"), "MetricPinkBrush", _write);
            Grid.SetColumn(write, 1);
            columns.Children.Add(write);
            var session = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            session.Children.Add(MonitorUi.Caption(L.Get("Strings.networkThisSession")));
            Grid.SetColumn(_session, 1);
            session.Children.Add(_session);
            Root = MonitorUi.Block(L.Get("Strings.monitorItemDiskActivity"), new StackPanel { Spacing = 6, Children = { columns, _graph, session } });
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            var disk = _owner.Selected(s);
            _read.Text = disk?.ReadRate is { } r ? MetricFormat.BytesPerSec(r, f.Culture) : L.Get("Strings.networkMeasuring");
            _write.Text = disk?.WriteRate is { } w ? MetricFormat.BytesPerSec(w, f.Culture) : L.Get("Strings.networkMeasuring");
            _session.Text = disk is null ? "-" : $"↓{MetricFormat.DiskBytes(disk.SessionRead, f.Culture)}  ↑{MetricFormat.DiskBytes(disk.SessionWritten, f.Culture)}";
            var peak = Math.Max(1, Math.Max(MonitorUi.Peak(s.Histories.DiskRead), MonitorUi.Peak(s.Histories.DiskWrite)));
            var ceiling = GraphScale.Ceiling(peak, 1024);
            MonitorUi.SetGraph(_graph, s.Histories.DiskRead, ceiling, MetricFormat.BytesPerSec(ceiling, f.Culture), f.Scale, s.Histories.DiskWrite);
            _graph.IsVisible &= _owner.Settings.Get(MonitorSettings.GraphDisk);
        }

        private static Control Column(string title, string brush, TextBlock value) => new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { MonitorUi.Dot(brush), MonitorUi.Caption(title) } },
                value,
            },
        };
    }

    private sealed class ProtectionBlock : BlockView
    {
        private readonly DisksSection _owner;
        private readonly Button _eject;
        private readonly Button _ejectAll;
        private readonly TextBlock _caption = MonitorUi.Caption(null);

        public ProtectionBlock(DisksSection owner)
        {
            _owner = owner;
            _eject = new Button { Content = L.Get("Strings.diskEject"), Padding = new Thickness(10, 3) };
            _ejectAll = new Button { Content = L.Get("Strings.diskEjectAll"), Padding = new Thickness(10, 3) };
            _eject.Click += (_, _) =>
            {
                if (owner.Selected(owner.Monitor.Latest) is { } disk)
                {
                    owner._eject.Eject(disk.Info);
                }
            };
            _ejectAll.Click += (_, _) => owner._eject.EjectAll(owner.Monitor.Latest.Disks);
            _caption.TextWrapping = TextWrapping.Wrap;
            _caption.TextTrimming = TextTrimming.None;
            Root = MonitorUi.Block(L.Get("Strings.monitorItemDiskProtection"), new StackPanel
            {
                Spacing = 6,
                Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _eject, _ejectAll } }, _caption },
            });
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            var disk = _owner.Selected(s);
            var state = disk is null ? EjectState.Idle : _owner._eject.State(disk.Info.Id);
            _eject.IsEnabled = disk is { Info.IsEjectable: true } && state != EjectState.Ejecting;
            _ejectAll.IsEnabled = _owner._eject.EjectAllCandidates(s.Disks).Count > 0;
            _caption.Text = disk is not { Info.IsEjectable: true }
                ? L.Get("Strings.diskNoExternal")
                : state switch
                {
                    EjectState.Ejecting => L.Get("Strings.diskEjecting"),
                    EjectState.Ready => L.Get("Strings.diskReadyToRemove"),
                    EjectState.Failed => L.Get("Strings.diskEjectFailed"),
                    _ => L.Get("Strings.diskProtectionCaption"),
                };
        }
    }

    private sealed class ToolsBlock : BlockView
    {
        private readonly DisksSection _owner;
        private readonly TextBlock _name = MonitorUi.Text(null, 12.5, FontWeight.Medium);

        public ToolsBlock(DisksSection owner)
        {
            _owner = owner;
            var shell = owner.Services.GetRequiredService<IShellService>();
            var open = new Button { Content = L.Get("Strings.diskOpenInFinder"), Padding = new Thickness(10, 3) };
            open.Click += (_, _) =>
            {
                if (owner.Selected(owner.Monitor.Latest) is { } disk)
                {
                    shell.OpenFile(disk.Info.MountPath);
                }
            };
            var storage = new Button { Content = L.Get("Strings.diskStorageSettings"), Padding = new Thickness(10, 3) };
            storage.Click += (_, _) => shell.OpenSystemSettings("ms-settings:storagesense");
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            grid.Children.Add(_name);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { open, storage } };
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            Root = MonitorUi.Block(L.Get("Strings.monitorItemDiskTools"), grid);
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f) =>
            _name.Text = _owner.Selected(s) is { } disk ? DisplayName(disk.Info) : "-";
    }

    private sealed class SymbolIconBox
    {
        public FluentIcons.Avalonia.SymbolIcon Icon { get; } = MonitorUi.Icon("HardDrive", 14, "AccentBrush");

        public void Set(string name) => Icon.Symbol = Rivet.App.Controls.IconConverter.Parse(name);
    }
}
