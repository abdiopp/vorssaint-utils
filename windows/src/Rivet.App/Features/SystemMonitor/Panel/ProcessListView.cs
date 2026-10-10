// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Panel;

/// <summary>Holds a lease while a control is attached to a visible window.</summary>
internal sealed class VisibilityLease
{
    private readonly Control _owner;
    private readonly Func<IDisposable> _acquire;
    private Window? _window;
    private IDisposable? _lease;
    private bool _attached;

    public VisibilityLease(Control owner, Func<IDisposable> acquire)
    {
        _owner = owner;
        _acquire = acquire;
        owner.AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            _window = TopLevel.GetTopLevel(owner) as Window;
            if (_window is not null)
            {
                _window.PropertyChanged += OnWindowChanged;
            }

            Update();
        };
        owner.DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            if (_window is not null)
            {
                _window.PropertyChanged -= OnWindowChanged;
                _window = null;
            }

            Update();
        };
    }

    public bool IsActive => _lease is not null;

    /// <summary>Raised after the lease state changed.</summary>
    public event EventHandler? Changed;

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        var want = _attached && (_window?.IsVisible ?? true) && _owner.IsVisible;
        if (want && _lease is null)
        {
            _lease = _acquire();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else if (!want && _lease is not null)
        {
            _lease.Dispose();
            _lease = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// A per-app list (spec §3.13): app name and value; a click brings the app to
/// the front; with the Kill Process feature installed the context menu offers
/// Force Kill (only for rows whose start time still matches and that are not
/// protected). Refreshes while visible; "Measuring…" until the first result.
/// </summary>
internal sealed class ProcessListView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly ProcessUsageService _usage;
    private readonly ProcessListKind _kind;
    private readonly int _limit;
    private readonly string? _emptyKey;
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly ISettingsStore _settings;

    public ProcessListView(IServiceProvider services, ProcessListKind kind, int limit = ProcessUsageService.PanelLimit, string? emptyKey = null)
    {
        _services = services;
        _usage = services.GetRequiredService<ProcessUsageService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _kind = kind;
        _limit = limit;
        _emptyKey = emptyKey;
        Content = _rows;
        _ = new VisibilityLease(this, () => _usage.Acquire(kind));
        AttachedToVisualTree += (_, _) =>
        {
            _usage.RowsChanged += OnRowsChanged;
            Render();
        };
        DetachedFromVisualTree += (_, _) => _usage.RowsChanged -= OnRowsChanged;
        Render();
    }

    private void OnRowsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Render();
        }
        else
        {
            Dispatcher.UIThread.Post(Render);
        }
    }

    private void Render()
    {
        _rows.Children.Clear();
        var rows = _usage.Rows(_kind);
        if (rows is null)
        {
            _rows.Children.Add(MonitorUi.Caption(L.Get("Strings.breakdownMeasuring")));
            return;
        }

        var shown = rows.Take(_limit).ToList();
        if (shown.Count == 0)
        {
            _rows.Children.Add(MonitorUi.Caption(L.Get(_emptyKey ?? "Strings.breakdownMeasuring")));
            return;
        }

        foreach (var row in shown)
        {
            _rows.Children.Add(BuildRow(row));
        }
    }

    private string FormatValue(ProcessUsageRow row) => _kind == ProcessListKind.Memory
        ? MetricFormat.MemoryBytes(row.Value)
        : row.Value.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "%";

    private Control BuildRow(ProcessUsageRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(MonitorUi.Icon("Apps", 13, "TextSecondaryBrush"));
        var name = MonitorUi.Text(row.Name, 12);
        name.TextTrimming = TextTrimming.PrefixCharacterEllipsis;
        ToolTip.SetTip(name, row.Name);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var value = MonitorUi.Value(FormatValue(row), 11.5, FontWeight.Normal, "TextSecondaryBrush");
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        var button = new Button { Classes = { "row" }, Padding = new Thickness(4, 3), Content = grid };
        AutomationProperties.SetName(button, $"{row.Name} {FormatValue(row)}");
        button.Click += (_, _) => Activate(row);
        var runtime = _services.GetRequiredService<FeatureRuntime>();
        if (runtime.IsAvailable(FeatureIds.KillProcess))
        {
            var kill = new MenuItem { Header = L.Get("killProcess.forceKillButton"), IsEnabled = ProcessSafety.CanKill(row) };
            kill.Click += async (_, _) => await ForceKillAsync(row);
            button.ContextMenu = new ContextMenu { Items = { kill } };
        }

        return button;
    }

    private void Activate(ProcessUsageRow row)
    {
        if (!row.HasWindow)
        {
            return;
        }

        var control = _services.GetRequiredService<IProcessControl>();
        Task.Run(() => control.Activate(row.Pid));
    }

    private async Task ForceKillAsync(ProcessUsageRow row)
    {
        if (!ProcessSafety.CanKill(row) || row.StartTime is not { } start)
        {
            return;
        }

        var shell = _services.GetService<IAppShell>();
        using var keepOpen = shell?.KeepPanelOpen("monitor.forceKill");
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(owner,
            L.Format("killProcess.confirmForceKillFormat", row.Name),
            L.Format("killProcess.pidLabelFormat", row.Pid),
            L.Get("killProcess.forceKillButton"),
            L.Get("hub.presetConfirmCancel"),
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        var control = _services.GetRequiredService<IProcessControl>();
        var result = await Task.Run(() => control.Kill(row.Pid, start));
        if (result is KillResult.AccessDenied or KillResult.Protected)
        {
            // Standard users cannot end elevated or system processes; the app never asks for admin here.
            _services.GetService<IHud>()?.Show(L.Get("killProcess.killFailedTitle") + " " + L.Get("killProcess.killFailedMessage"), HudStyle.Error);
        }
    }
}
