// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Shell;
using Rivet.Core.App;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Settings.Pages;

/// <summary>Version, project link and update checks.</summary>
public sealed class AboutPage : SettingsPage
{
    private readonly UpdateService _updates;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _lastChecked = new() { Classes = { "caption" } };
    private readonly Button _install;

    public AboutPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _updates = services.GetRequiredService<UpdateService>();
        var shell = services.GetRequiredService<IShellService>();
        var platform = services.GetRequiredService<IPlatformInfo>();
        var check = ActionButton(L.Get("Strings.checkNowButton"), () => _ = _updates.CheckAsync(), "ArrowSync");
        ToolTip.SetTip(check, L.Get("Strings.menuCheckUpdates"));
        _install = ActionButton(L.Get("Strings.updateInstallButton"), _updates.Install, "ArrowDownload", accent: true);

        var identity = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new AppGlyph { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center, [!AppGlyph.ForegroundProperty] = this.GetResourceObservable("AccentBrush").ToBinding() },
                new TextBlock { Text = AppIdentity.DisplayName, FontSize = 22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = L.Format("win.shell.versionFormat", AppIdentity.VersionString), Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = platform.OsDescription, Classes = { "caption", "tertiary" }, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = L.Get("win.shell.aboutDescription"), Classes = { "caption" }, TextAlignment = TextAlignment.Center, MaxWidth = 520, Margin = new Thickness(0, 8, 0, 0) },
                ActionButton(L.Get("Strings.viewOnGitHub"), () => shell.OpenUrl($"https://github.com/{AppIdentity.UpdateRepository}"), "Open"),
            },
        };
        foreach (var child in identity.Children.OfType<Button>())
        {
            child.HorizontalAlignment = HorizontalAlignment.Center;
        }

        Content = Stack(
            Header("Strings.tabAbout"),
            CardText(null, identity),
            Card("Strings.updatesSection",
                Toggle(ShellSettings.AutoCheckUpdates, "ArrowSync", "Strings.autoCheckToggle"),
                Toggle(ShellSettings.IncludeBetaUpdates, "Beaker", "Strings.includeBetaUpdatesToggle", "Strings.includeBetaUpdatesCaption"),
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                    ColumnSpacing = 8,
                    Children = { new StackPanel { Spacing = 2, Children = { _status, _lastChecked } }, Column(check, 1), Column(_install, 2) },
                }));

        _updates.PropertyChanged += OnUpdatesChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _updates.PropertyChanged -= OnUpdatesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    private void OnUpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        _status.Text = _updates.State switch
        {
            UpdateState.Checking => L.Get("Strings.updateChecking"),
            UpdateState.UpToDate => L.Get("Strings.updateUpToDate"),
            UpdateState.Available => $"{L.Get("Strings.updateAvailablePrefix")} {_updates.Available?.Version}",
            UpdateState.Failed => $"{L.Get("Strings.updateFailedPrefix")} {_updates.Error}",
            _ => string.Empty,
        };
        _install.IsVisible = _updates.State == UpdateState.Available;
        _lastChecked.Text = _updates.LastChecked is { } last
            ? $"{L.Get("Strings.updateLastChecked")} {last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
            : string.Empty;
        _lastChecked.IsVisible = _lastChecked.Text.Length > 0;
    }
}
