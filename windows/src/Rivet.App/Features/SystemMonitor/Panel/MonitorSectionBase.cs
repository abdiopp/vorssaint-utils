// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Shell;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Panel;

/// <summary>A block of a monitor section: its live view and how to refresh it.</summary>
internal abstract class BlockView
{
    public abstract Control Root { get; }

    public abstract void Update(MonitorSnapshot snapshot, MonitorUi.Formats formats);

    /// <summary>The block leaves the screen (release leases).</summary>
    public virtual void Detach()
    {
    }
}

/// <summary>A block definition: id (persisted in the order key), title, visibility switches, availability.</summary>
internal sealed record BlockSpec(
    string Id,
    string TitleKey,
    string Icon,
    IReadOnlyList<(string LabelKey, Setting<bool> Setting)> Switches,
    Func<bool> IsAvailable,
    Func<BlockView> Create)
{
    public bool IsVisible(ISettingsStore settings) => Switches.Count == 0 || Switches.Any(s => settings.Get(s.Setting));
}

/// <summary>Remembers a detail view requested from outside the panel (a mini monitor click).</summary>
public sealed class MonitorNavigation
{
    private MetricKind? _pending;

    /// <summary>Raised after a request, so an already visible section can open the detail at once.</summary>
    public event EventHandler? Requested;

    public void Request(MetricKind kind)
    {
        _pending = kind;
        Requested?.Invoke(this, EventArgs.Empty);
    }

    public MetricKind? Take(Func<MetricKind, bool> belongsHere)
    {
        if (_pending is { } kind && belongsHere(kind))
        {
            _pending = null;
            return kind;
        }

        return null;
    }

    /// <summary>The panel section that hosts a detail kind (and "back" returns to).</summary>
    public static string SectionFor(MetricKind kind) => kind switch
    {
        MetricKind.Network => SystemMonitorModule.NetworkSectionId,
        MetricKind.Disk => SystemMonitorModule.DiskSectionId,
        MetricKind.Battery or MetricKind.Power => SystemMonitorModule.PowerSectionId,
        _ => SystemMonitorModule.SystemSectionId,
    };
}

/// <summary>
/// Common behaviour of the System, Network, Disks and Power tabs: leases the
/// sampler only while the tab is on screen in a visible panel, re-renders on
/// every published snapshot (on the UI thread), offers the in-section edit
/// mode (reorder, hide, reset) and hosts metric detail views in place.
/// </summary>
internal abstract class MonitorSectionBase : UserControl
{
    private readonly MonitorSurface _surface;
    private readonly ContentControl _body = new();
    private readonly Dictionary<string, BlockView> _live = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private Window? _window;
    private IDisposable? _lease;
    private bool _editing;
    private MetricDetailView? _detail;
    private bool _attached;

    protected MonitorSectionBase(IServiceProvider services, MonitorSurface surface, string titleKey, Setting<string> orderSetting)
    {
        Services = services;
        _surface = surface;
        TitleKey = titleKey;
        OrderSetting = orderSetting;
        Monitor = services.GetRequiredService<SystemMonitorService>();
        Settings = services.GetRequiredService<ISettingsStore>();
        Runtime = services.GetRequiredService<FeatureRuntime>();
        Content = _body;
    }

    protected IServiceProvider Services { get; }

    protected SystemMonitorService Monitor { get; }

    protected ISettingsStore Settings { get; }

    protected FeatureRuntime Runtime { get; }

    protected string TitleKey { get; }

    protected Setting<string> OrderSetting { get; }

    /// <summary>Every block of the section, in default order.</summary>
    protected abstract IReadOnlyList<BlockSpec> Blocks { get; }

    /// <summary>Settings whose change rebuilds the blocks (beyond order and switches).</summary>
    protected virtual IEnumerable<SettingDefinition> LayoutKeys => [];

    /// <summary>Shown when no block is visible.</summary>
    protected virtual string? EmptyText => null;

    /// <summary>Content between the header and the blocks (the disk selector); rebuilt with the blocks.</summary>
    protected virtual Control? CreatePrelude() => null;

    /// <summary>When true the blocks are replaced by <see cref="EmptyText"/> (no disk, no power data).</summary>
    protected virtual bool ShowEmptyState(MonitorSnapshot snapshot) => false;

    /// <summary>Detail kinds this section hosts.</summary>
    protected virtual bool HostsDetail(MetricKind kind) => MonitorNavigation.SectionFor(kind) == SectionId;

    protected abstract string SectionId { get; }

    public void ShowDetail(MetricKind kind)
    {
        _detail?.Detach();
        _detail = new MetricDetailView(Services, kind, CloseDetail);
        Rebuild();
    }

    protected void CloseDetail()
    {
        _detail?.Detach();
        _detail = null;
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
        }

        Monitor.SnapshotPublished += OnSnapshot;
        var keys = Blocks.SelectMany(b => b.Switches.Select(s => (SettingDefinition)s.Setting))
            .Append(OrderSetting).Concat(LayoutKeys).Append(MonitorSettings.GraphScale)
            .Append(MonitorSettings.TemperatureUnit).Append(MonitorSettings.NetworkSpeedUnit).Append(MonitorSettings.MemoryMetric);
        _subscriptions.Add(Settings.Observe(() => Dispatcher.UIThread.Post(Rebuild), keys.Distinct().ToArray()));
        Runtime.Changed += OnRuntimeChanged;
        if (Services.GetService<MonitorNavigation>() is { } navigation)
        {
            navigation.Requested += OnNavigationRequested;
            if (navigation.Take(HostsDetail) is { } pending)
            {
                _detail = new MetricDetailView(Services, pending, CloseDetail);
            }
        }

        Rebuild();
        UpdateLease();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        Monitor.SnapshotPublished -= OnSnapshot;
        Runtime.Changed -= OnRuntimeChanged;
        if (Services.GetService<MonitorNavigation>() is { } navigation)
        {
            navigation.Requested -= OnNavigationRequested;
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        DetachBlocks();
        _detail?.Detach();
        _lease?.Dispose();
        _lease = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRuntimeChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private void OnNavigationRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_attached && Services.GetService<MonitorNavigation>()?.Take(HostsDetail) is { } kind)
        {
            ShowDetail(kind);
        }
    });

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            UpdateLease();
            if (_window?.IsVisible == false)
            {
                // The panel closed: per-app lists wind down and edit mode ends.
                _editing = false;
                Dispatcher.UIThread.Post(Rebuild);
            }
        }
    }

    /// <summary>Lease the section (or the open detail) only while really on screen.</summary>
    private void UpdateLease()
    {
        var visible = _attached && (_window?.IsVisible ?? true);
        _lease?.Dispose();
        _lease = null;
        _detail?.SetVisible(visible && _detail is not null);
        if (visible && _detail is null)
        {
            _lease = Monitor.AcquireSurface(_surface);
        }
    }

    private void OnSnapshot(object? sender, MonitorSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply(snapshot);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Apply(snapshot));
        }
    }

    private void Apply(MonitorSnapshot snapshot)
    {
        if (!_attached)
        {
            return;
        }

        var formats = MonitorUi.Formats.From(Settings);
        if (_detail is not null)
        {
            _detail.Update(snapshot, formats);
            return;
        }

        foreach (var block in _live.Values)
        {
            try
            {
                block.Update(snapshot, formats);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.Log.Warn("monitor", "A panel block failed to update.", ex);
            }
        }

        AfterUpdate(snapshot);
    }

    /// <summary>Hook for sections that react to the snapshot as a whole (empty states).</summary>
    protected virtual void AfterUpdate(MonitorSnapshot snapshot)
    {
    }

    /// <summary>Re-creates the visible blocks (order, switches, availability or mode changed).</summary>
    protected void Rebuild()
    {
        if (!_attached)
        {
            return;
        }

        DetachBlocks();
        if (_detail is not null)
        {
            _body.Content = _detail;
            UpdateLease();
            _detail.Update(Monitor.Latest, MonitorUi.Formats.From(Settings));
            return;
        }

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(Header());
        var ordered = Ordered();
        if (_editing)
        {
            root.Children.Add(EditList(ordered));
        }
        else if (ShowEmptyState(Monitor.Latest) && EmptyText is { } emptyState)
        {
            root.Children.Add(MonitorUi.Card(MonitorUi.Caption(emptyState)));
        }
        else
        {
            if (CreatePrelude() is { } prelude)
            {
                root.Children.Add(prelude);
            }

            var any = false;
            foreach (var spec in ordered.Where(b => b.IsVisible(Settings)))
            {
                BlockView view;
                try
                {
                    view = spec.Create();
                }
                catch (Exception ex)
                {
                    Core.Diagnostics.Log.Warn("monitor", $"Block '{spec.Id}' failed to build.", ex);
                    continue;
                }

                _live[spec.Id] = view;
                root.Children.Add(view.Root);
                any = true;
            }

            if (!any && EmptyText is { } empty)
            {
                root.Children.Add(MonitorUi.Card(MonitorUi.Caption(empty)));
            }
        }

        _body.Content = root;
        UpdateLease();
        Apply(Monitor.Latest);
    }

    private void DetachBlocks()
    {
        foreach (var block in _live.Values)
        {
            block.Detach();
        }

        _live.Clear();
    }

    private List<BlockSpec> Ordered() =>
        PanelLayout.Order(Blocks.Where(b => b.IsAvailable()), b => b.Id, b => Blocks.ToList().IndexOf(b), Settings.Get(OrderSetting));

    private Control Header()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4, Margin = new Thickness(4, 0, 0, 0), MinHeight = 24 };
        grid.Children.Add(MonitorUi.SectionTitle(L.Get(TitleKey)));
        if (_editing)
        {
            var reset = MonitorUi.IconButton("ArrowCounterclockwise", L.Get("win.shell.reset"), ResetLayout);
            Grid.SetColumn(reset, 1);
            grid.Children.Add(reset);
            var done = new Button
            {
                Classes = { "accent" },
                Padding = new Thickness(8, 2),
                MinHeight = 24,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { MonitorUi.Icon("Checkmark", 12), new TextBlock { Text = L.Get("win.shell.done"), FontSize = 11, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            ToolTip.SetTip(done, L.Get("win.shell.done"));
            done.Click += (_, _) =>
            {
                _editing = false;
                Rebuild();
            };
            Grid.SetColumn(done, 2);
            grid.Children.Add(done);
        }
        else
        {
            var edit = MonitorUi.IconButton("Options", L.Get("win.shell.editPanel"), () =>
            {
                _editing = true;
                Rebuild();
            });
            Grid.SetColumn(edit, 2);
            grid.Children.Add(edit);
        }

        return grid;
    }

    private Control EditList(List<BlockSpec> ordered)
    {
        var list = new StackPanel { Spacing = 2 };
        var ids = ordered.Select(b => b.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var spec = ordered[i];
            var index = i;
            var visible = spec.IsVisible(Settings);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,22,*,Auto"), ColumnSpacing = 4, Margin = new Thickness(0, 1), Opacity = visible ? 1 : 0.55 };
            var up = MonitorUi.IconButton("ChevronUp", L.Get("Strings.monitorOrderHint"), () => Move(ids, index, -1), 12);
            up.IsEnabled = index > 0;
            var down = MonitorUi.IconButton("ChevronDown", L.Get("Strings.monitorOrderHint"), () => Move(ids, index, 1), 12);
            down.IsEnabled = index < ordered.Count - 1;
            row.Children.Add(up);
            Grid.SetColumn(down, 1);
            row.Children.Add(down);
            var icon = MonitorUi.Icon(spec.Icon, 14, "AccentBrush");
            Grid.SetColumn(icon, 2);
            row.Children.Add(icon);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(MonitorUi.Text(L.Get(spec.TitleKey), 12.5, FontWeight.Medium));
            if (!visible)
            {
                title.Children.Add(new Border { Classes = { "pill" }, Child = new TextBlock { Text = L.Get("Strings.panelHiddenItem") } });
            }

            Grid.SetColumn(title, 3);
            row.Children.Add(title);
            var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (spec.Switches.Count == 1)
            {
                var setting = spec.Switches[0].Setting;
                var on = Settings.Get(setting);
                toggles.Children.Add(MonitorUi.IconButton(on ? "Eye" : "EyeOff", L.Get(on ? "Strings.panelHideItem" : "Strings.panelShowItem"), () => Settings.Set(setting, !on)));
            }
            else
            {
                foreach (var (labelKey, setting) in spec.Switches)
                {
                    var on = Settings.Get(setting);
                    var chip = new ToggleButton
                    {
                        IsChecked = on,
                        Padding = new Thickness(6, 1),
                        MinHeight = 22,
                        Content = new TextBlock { Text = L.Get(labelKey), FontSize = 10.5, FontWeight = FontWeight.SemiBold },
                    };
                    AutomationProperties.SetName(chip, L.Get(labelKey));
                    chip.IsCheckedChanged += (_, _) => Settings.Set(setting, chip.IsChecked == true);
                    toggles.Children.Add(chip);
                }
            }

            Grid.SetColumn(toggles, 4);
            row.Children.Add(toggles);
            list.Children.Add(row);
        }

        return MonitorUi.Card(new StackPanel
        {
            Spacing = 6,
            Children = { list, MonitorUi.Note(L.Get("Strings.monitorOrderHint")) },
        });
    }

    private void Move(List<string> ids, int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= ids.Count)
        {
            return;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);
        Settings.Set(OrderSetting, string.Join(',', ids));
    }

    /// <summary>Default order and every item visible.</summary>
    private void ResetLayout()
    {
        Settings.Reset(OrderSetting.Key);
        foreach (var spec in Blocks)
        {
            foreach (var (_, setting) in spec.Switches)
            {
                Settings.Set(setting, true);
            }
        }
    }
}
