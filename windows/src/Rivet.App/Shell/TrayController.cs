// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.Core.Actions;
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Shell;

/// <summary>The notification-area icon: glyph, tooltip, left/right clicks and the context menu.</summary>
public sealed class TrayController : ITrayPresence, IDisposable
{
    private readonly ITrayIcon _tray;
    private readonly IThemeService _theme;
    private readonly AppShell _shell;
    private readonly ISettingsStore _settings;
    private readonly ActionRegistry _actions;
    private readonly FeatureRuntime _runtime;
    private readonly TrayMenuRegistry _menu;
    private readonly Dictionary<string, TrayIndicator> _indicators = new(StringComparer.Ordinal);
    private TrayMenuWindow? _menuWindow;
    private object? _renderedIcon;

    public TrayController(IServiceProvider services)
    {
        _tray = services.GetRequiredService<ITrayIcon>();
        _theme = services.GetRequiredService<IThemeService>();
        _shell = services.GetRequiredService<AppShell>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _actions = services.GetRequiredService<ActionRegistry>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _menu = services.GetRequiredService<TrayMenuRegistry>();
    }

    public void Start()
    {
        _tray.Clicked += OnClicked;
        _tray.TaskbarThemeChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _theme.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Localizer.Current.LanguageChanged += (_, _) => Refresh();
        Refresh();
        _tray.Show();
    }

    public void SetIndicator(string source, TrayIndicator? indicator)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (indicator is null)
            {
                _indicators.Remove(source);
            }
            else
            {
                _indicators[source] = indicator;
            }

            Refresh();
        });
    }

    private void Refresh()
    {
        var tint = _indicators.Values.Where(i => i.Tint.HasValue).OrderByDescending(i => i.Priority).FirstOrDefault()?.Tint;
        var badge = _indicators.Values.Any(i => i.Badge);
        // Tooltip lines change often (countdowns, readouts); redraw the icon only when its look changes.
        var look = (_theme.SystemUsesLightTheme, tint, badge);
        if (!look.Equals(_renderedIcon))
        {
            _tray.SetIcon(TrayIconRenderer.Render(look.SystemUsesLightTheme, tint, badge));
            _renderedIcon = look;
        }

        var lines = new List<string> { AppIdentity.DisplayName };
        lines.AddRange(_indicators.Values.Select(i => i.TooltipLine).OfType<string>());
        _tray.SetTooltip(string.Join('\n', lines));
    }

    private void OnClicked(object? sender, TrayClickEventArgs e)
    {
        switch (e.Button)
        {
            case TrayMouseButton.Left:
                _menuWindow?.Hide();
                _shell.TogglePanelFromTray(_tray.Bounds, e.Position);
                break;
            case TrayMouseButton.Right:
                if (_settings.Get(ShellSettings.KeepAwakeRightClickToggle)
                    && _runtime.IsAvailable(FeatureIds.KeepAwake)
                    && _actions.Get("keepAwake.toggle") is not null)
                {
                    _ = _actions.InvokeAsync("keepAwake.toggle", ActionSource.Tray);
                    return;
                }

                ShowMenu(e.Position);
                break;
        }
    }

    private void ShowMenu(PixelPoint point)
    {
        _shell.ClosePanel(PanelCloseReason.Action);
        var entries = new List<TrayMenuEntry>();
        var moduleItems = _menu.Items
            .Where(i => (i.FeatureId is null || _runtime.IsAvailable(i.FeatureId)) && (i.IsVisible?.Invoke() ?? true))
            .OrderBy(i => i.Order)
            .ToList();
        foreach (var item in moduleItems.Where(i => i.Order < 0))
        {
            entries.Add(new TrayMenuEntry(item.Title(), item.Icon, item.Invoke));
        }

        if (entries.Count > 0)
        {
            entries.Add(TrayMenuEntry.Separator);
        }

        entries.Add(new TrayMenuEntry(L.Get("Strings.menuSettings"), "Settings", () => _shell.OpenSettings()));
        entries.Add(new TrayMenuEntry(L.Format("win.shell.trayMenuAbout", AppIdentity.DisplayName), "Info", () => _shell.OpenSettings(SettingsPageIds.About)));
        foreach (var item in moduleItems.Where(i => i.Order >= 0))
        {
            entries.Add(new TrayMenuEntry(item.Title(), item.Icon, item.Invoke));
        }

        entries.Add(TrayMenuEntry.Separator);
        entries.Add(new TrayMenuEntry(L.Format("win.shell.trayMenuQuit", AppIdentity.DisplayName), "Power", _shell.Quit));

        _menuWindow ??= new TrayMenuWindow();
        _menuWindow.ShowAt(point, entries);
    }

    public void Dispose()
    {
        _tray.Clicked -= OnClicked;
        _menuWindow?.Close();
        _tray.Hide();
    }
}
