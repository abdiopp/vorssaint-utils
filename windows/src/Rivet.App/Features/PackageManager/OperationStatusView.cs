// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Platform;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.PackageManager;

/// <summary>
/// The shared operation card (spec §3.4.5): what runs, its phase and elapsed
/// time, a determinate or waiting progress bar, the latest activity line,
/// Cancel while running, and the technical log on demand. The panel stays
/// open while an operation runs.
/// </summary>
public sealed class OperationStatusView : UserControl
{
    private static bool _showLog;

    private readonly PackageOperationLane _lane;
    private readonly IClipboardService? _clipboard;
    private readonly IAppShell? _shell;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IDisposable? _keepOpen;

    public OperationStatusView(IServiceProvider services)
    {
        _lane = services.GetRequiredService<PackageOperationLane>();
        _clipboard = services.GetService<IClipboardService>();
        _shell = services.GetService<IAppShell>();
        _timer.Tick += (_, _) => Build();
        Build();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lane.Changed += OnChanged;
        Build();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _lane.Changed -= OnChanged;
        _timer.Stop();
        _keepOpen?.Dispose();
        _keepOpen = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>"Installing Git", "Git updated.", "Could not finish Git."…</summary>
    public static string TitleOf(PackageOperationState state)
    {
        var name = state.PackageName ?? L.Get("Strings.homebrewAllPackages");
        if (state.IsRunning)
        {
            if (state.BatchCount > 1)
            {
                return L.Format("win.packageManager.operationBatchFormat", name, state.BatchIndex, state.BatchCount);
            }

            return state.Kind switch
            {
                PackageOperationKind.Install => L.Format("Strings.homebrewOperationInstallFormat", name),
                PackageOperationKind.Uninstall => L.Format("Strings.homebrewOperationUninstallFormat", name),
                PackageOperationKind.Upgrade => L.Format("Strings.homebrewOperationUpgradeFormat", name),
                PackageOperationKind.UpgradeAll => L.Get("Strings.homebrewOperationUpgradeAll"),
                _ => L.Get("win.packageManager.operationSources"),
            };
        }

        return state.Result switch
        {
            OperationResult.Cancelled => L.Get("Strings.homebrewOperationCancelled"),
            OperationResult.Failed => L.Format("Strings.homebrewOperationFailedFormat", name),
            _ => state.Kind switch
            {
                PackageOperationKind.Install => L.Format("Strings.homebrewOperationInstalledFormat", name),
                PackageOperationKind.Uninstall => L.Format("Strings.homebrewOperationUninstalledFormat", name),
                PackageOperationKind.Upgrade => L.Format("Strings.homebrewOperationUpgradedFormat", name),
                PackageOperationKind.UpgradeAll => L.Get("Strings.homebrewOperationUpgradedAll"),
                _ => L.Get("win.packageManager.operationSourcesDone"),
            },
        };
    }

    public static string PhaseText(OperationPhase phase) => L.Get(phase switch
    {
        OperationPhase.Downloading => "Strings.homebrewOperationDownloading",
        OperationPhase.Installing => "Strings.homebrewOperationInstalling",
        OperationPhase.Removing => "Strings.homebrewOperationUninstalling",
        OperationPhase.Updating => "Strings.homebrewOperationUpgrading",
        OperationPhase.Refreshing => "Strings.homebrewOperationRefreshing",
        OperationPhase.Finishing => "Strings.homebrewOperationFinalizing",
        _ => "Strings.homebrewOperationPreparing",
    });

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Build);

    private void Build()
    {
        var state = _lane.Current;
        IsVisible = state is not null;
        if (state is null)
        {
            _timer.Stop();
            _keepOpen?.Dispose();
            _keepOpen = null;
            Content = null;
            return;
        }

        if (state.IsRunning)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }

            _keepOpen ??= _shell?.KeepPanelOpen("package-operation");
        }
        else
        {
            _timer.Stop();
            _keepOpen?.Dispose();
            _keepOpen = null;
        }

        var (icon, brush) = state.Result switch
        {
            OperationResult.Running => ("ArrowDownload", "AccentBrush"),
            OperationResult.Succeeded => ("CheckmarkCircle", "SuccessBrush"),
            OperationResult.Cancelled => ("DismissCircle", "TextSecondaryBrush"),
            _ => ("Warning", "WarningBrush"),
        };
        var trailing = state.IsRunning
            ? Button(L.Get("Strings.homebrewCancelOperation"), _lane.Cancel)
            : IconButton("Dismiss", L.Get("Strings.menuClose"), _lane.Dismiss, 12);
        var stack = VStack(6, Columns("Auto,*,Auto", 8, Icon(icon, 18, brush), Text(TitleOf(state), 13, FontWeight.SemiBold), trailing));

        if (state.IsRunning)
        {
            stack.Children.Add(Caption(PhaseText(state.Phase) + "  ·  " + L.Format("Strings.homebrewOperationElapsedFormat", WingetProgress.FormatElapsed(state.Elapsed))));
            stack.Children.Add(Progress(state.Progress));
            if (state.Progress is null)
            {
                stack.Children.Add(Caption(L.Get("Strings.homebrewOperationProgressUnknown")));
            }

            if (state.Activity is { } activity)
            {
                stack.Children.Add(new TextBlock { Text = activity, Classes = { "caption", "mono" }, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 });
            }
        }
        else if (state.Message is { } message)
        {
            stack.Children.Add(Colored(message, state.Result == OperationResult.Succeeded ? "TextSecondaryBrush" : "WarningBrush", 12));
        }

        var log = _lane.LogText;
        if (log.Length > 0)
        {
            var toggle = LinkButton(L.Get(_showLog ? "Strings.homebrewOperationHideDetails" : "Strings.homebrewOperationShowDetails"), () =>
            {
                _showLog = !_showLog;
                Build();
            });
            var actions = HStack(12, toggle);
            if (!state.IsRunning)
            {
                actions.Children.Add(LinkButton(L.Get("Strings.homebrewClearLog"), _lane.ClearLog));
            }

            if (_showLog && _clipboard is { } clipboard)
            {
                actions.Children.Add(LinkButton(L.Get("clipboard.copy"), () => clipboard.SetText(log)));
            }

            stack.Children.Add(actions);
            if (_showLog)
            {
                stack.Children.Add(new ScrollViewer
                {
                    MaxHeight = 160,
                    Content = new SelectableTextBlock { Text = log, Classes = { "mono" }, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                });
            }
        }

        Content = Card(stack, 8);
    }
}

/// <summary>"winget not found" and the one-time source agreement consent.</summary>
internal static class WingetCards
{
    /// <summary>App Installer's Microsoft Store product id (it ships winget).</summary>
    public const string AppInstallerStoreUri = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

    public const string StoreTermsUrl = "https://aka.ms/microsoft-store-terms-of-transaction";

    public static Control Missing(IShellService shell, Action refresh) => Card(VStack(6,
        Columns("Auto,*", 8, Icon("Box", 18, "WarningBrush"), Text(L.Get("win.packageManager.missingTitle"), 13, FontWeight.SemiBold)),
        Caption(L.Get("win.packageManager.missingBody")),
        HStack(8, Button(L.Get("win.packageManager.getAppInstaller"), () => shell.OpenUrl(AppInstallerStoreUri), "StoreMicrosoft", accent: true),
            Button(L.Get("portManager.refresh"), refresh, "ArrowSync"))));

    public static Control Agreement(IShellService shell, Action accept) => Card(VStack(6,
        Columns("Auto,*", 8, Icon("Shield", 18), Text(L.Get("win.packageManager.agreementTitle"), 13, FontWeight.SemiBold)),
        Caption(L.Get("win.packageManager.agreementBody")),
        HStack(8, Button(L.Get("win.packageManager.accept"), accept, accent: true),
            LinkButton(L.Get("win.packageManager.viewTerms"), () => shell.OpenUrl(StoreTermsUrl)))));
}
