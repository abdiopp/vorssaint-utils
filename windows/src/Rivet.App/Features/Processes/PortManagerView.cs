// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.Processes;

/// <summary>
/// Port Manager (spec §3.5): the PC's listening TCP ports (and UDP sockets
/// when turned on) for every user, filter, copy and open actions, and Kill /
/// Force Kill through Kill Process when that feature is installed.
/// Refreshes on appear and with the button only.
/// </summary>
public sealed class PortManagerView : UserControl
{
    private readonly PortService _ports;
    private readonly ProcessService _processes;
    private readonly IElevationService _elevation;
    private readonly IShellService _shell;
    private readonly IClipboardService? _clipboard;
    private readonly FeatureRuntime? _features;
    private readonly ISettingsStore _settings;
    private readonly bool _compact;
    private readonly TextBox _filter;
    private readonly ScrollViewer _list;
    private readonly ContentControl _body = new();
    private readonly TextBlock _count = new() { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _busy = Progress(null, 40);
    private IDisposable? _udpSubscription;

    public PortManagerView(IServiceProvider services, bool compact)
    {
        _ports = services.GetRequiredService<PortService>();
        _processes = services.GetRequiredService<ProcessService>();
        _elevation = services.GetRequiredService<IElevationService>();
        _shell = services.GetRequiredService<IShellService>();
        _clipboard = services.GetService<IClipboardService>();
        _features = services.GetService<FeatureRuntime>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _compact = compact;

        _filter = new TextBox { PlaceholderText = L.Get("portManager.filter"), MinWidth = 0 };
        AutomationProperties.SetName(_filter, L.Get("portManager.filter"));
        _filter.TextChanged += (_, _) => Apply();
        _list = new ScrollViewer { MaxHeight = compact ? 260 : 460, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var refresh = IconButton("ArrowSync", L.Get("portManager.refresh"), () => _ = _ports.RefreshAsync());
        Content = VStack(8,
            Columns("*,Auto,Auto,Auto", 6, Caption(L.Get("portManager.listeningCaption"), wrap: false), _count, _busy, refresh),
            _filter,
            _body);
        Apply();
    }

    private bool CanKill => _features?.IsAvailable(FeatureIds.KillProcess) ?? true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ports.Changed += OnChanged;
        _udpSubscription = _settings.Observe(() => Dispatcher.UIThread.Post(() => _ = _ports.RefreshAsync()), ProcessSettings.PortManagerShowUdp);
        _ = _ports.RefreshAsync();
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ports.Changed -= OnChanged;
        _udpSubscription?.Dispose();
        _udpSubscription = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Apply);

    private void Apply()
    {
        var all = _ports.Rows;
        var rows = all.Where(r => PortRules.Matches(r, _filter.Text)).ToList();
        _count.Text = _ports.HasLoaded ? L.Format("portManager.openFormat", all.Count) : string.Empty;
        _busy.IsVisible = _ports.IsRefreshing;

        var stack = VStack(6);
        if (_ports.LastRefreshFailed)
        {
            stack.Children.Add(Columns("Auto,*", 6, Icon("Warning", 14, "WarningBrush"), Colored(L.Get("portManager.loadFailed"), "WarningBrush")));
        }

        if (!_ports.HasLoaded && _ports.IsRefreshing)
        {
            stack.Children.Add(Progress(null));
        }
        else if (rows.Count == 0 && _ports.HasLoaded)
        {
            var empty = VStack(2, Text(L.Get("portManager.empty"), 13, FontWeight.SemiBold), Caption(L.Get("portManager.emptyHint")));
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 12);
            stack.Children.Add(empty);
        }
        else if (rows.Count > 0)
        {
            var list = VStack(2);
            // Keeps the overlay scroll bar off the row buttons.
            list.Margin = new Thickness(0, 0, 10, 0);
            foreach (var row in rows)
            {
                list.Children.Add(Row(row));
            }

            _list.Content = list;
            stack.Children.Add(Card(Reuse(_list), 4));
        }

        _body.Content = stack;
    }

    private Control Row(PortRow row)
    {
        var port = new TextBlock { Text = row.Port.ToString(CultureInfo.InvariantCulture), FontSize = 13, FontWeight = FontWeight.Bold, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        var badges = HStack(4, Pill(row.Protocol == PortProtocol.Udp ? "UDP" : "TCP"));
        if (row.IsAllInterfaces)
        {
            Control everywhere = _compact
                ? Icon("Globe", 13, "WarningBrush")
                : HStack(3, Icon("Globe", 12, "WarningBrush"), new TextBlock { Text = L.Get("portManager.allInterfaces"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            ToolTip.SetTip(everywhere, L.Get("portManager.allInterfacesHelp"));
            AutomationProperties.SetName(everywhere, L.Get("portManager.allInterfaces"));
            badges.Children.Add(everywhere);
        }

        var detail = string.Create(CultureInfo.InvariantCulture, $"PID {row.Pid}  •  {row.Address}");
        if (!_compact && row.User is { Length: > 0 } user)
        {
            detail += "  •  " + user;
        }

        var name = Text(row.ProcessName, 12, FontWeight.SemiBold, wrap: false);
        var texts = VStack(0, Columns("Auto,Auto", 6, name, badges), new TextBlock
        {
            Text = detail, FontSize = 11, Classes = { "caption", "mono" }, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        });
        name.MaxWidth = _compact ? 120 : 300;

        Control? actions = null;
        if (CanKill)
        {
            var kill = Button(L.Get("portManager.kill"), () => _ = KillAsync(row, force: false));
            kill.Padding = new Thickness(8, 2);
            kill.FontSize = 11;
            var force = IconButton("Flash", L.Get("portManager.forceKill"), () => _ = KillAsync(row, force: true), 14);
            kill.IsEnabled = force.IsEnabled = row.CanKill;
            actions = HStack(2, kill, force);
        }

        var line = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 3),
            Child = Columns("Auto,Auto,*,Auto", 8, Icon("PlugConnected", 16, "TextSecondaryBrush"), port, texts, actions),
        };
        AutomationProperties.SetName(line, string.Create(CultureInfo.InvariantCulture, $"{row.Port} {row.ProcessName}"));
        var menu = new List<(string, Action, bool)>
        {
            (L.Get("portManager.copyPort"), () => _clipboard?.SetText(row.Port.ToString(CultureInfo.InvariantCulture)), _clipboard is not null),
            (L.Get("killProcess.copyPID"), () => _clipboard?.SetText(row.Pid.ToString(CultureInfo.InvariantCulture)), _clipboard is not null),
            (L.Get("portManager.copyAddress"), () => _clipboard?.SetText(PortRules.AddressAndPort(row)), _clipboard is not null),
        };
        if (PortRules.BrowserUrl(row) is { } url)
        {
            menu.Add((L.Get("win.processes.openInBrowser"), () => _shell.OpenUrl(url), true));
        }

        line.ContextMenu = Menu(menu.ToArray());
        return line;
    }

    private async Task KillAsync(PortRow row, bool force)
    {
        var confirm = L.Get(force ? "portManager.forceKill" : "portManager.kill");
        if (!await ConfirmAsync(this, L.Format("portManager.terminateFormat", row.ProcessName), L.Format("portManager.terminateMessageFormat", row.Port, row.Pid), confirm, destructive: true))
        {
            return;
        }

        var report = await _processes.KillAsync(row.Identity, row.ProcessName, force);
        await Task.Delay(ProcessService.PostKillRefreshDelay);
        await _ports.RefreshAsync();
        await KillProcessView.ExplainAsync(this, report, _elevation);
    }
}
