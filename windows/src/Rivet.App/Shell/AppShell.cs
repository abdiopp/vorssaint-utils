// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Shell;

/// <summary>Owns the panel window and the Settings window and implements <see cref="IAppShell"/>.</summary>
public sealed class AppShell : IAppShell
{
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(350);

    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly IFocusHandoff _focus;
    private readonly HashSet<KeepOpenToken> _keepOpen = [];
    private PanelViewModel? _panelViewModel;
    private PanelWindow? _panel;
    private SettingsWindow? _settingsWindow;
    private DateTime _lastDeactivationClose = DateTime.MinValue;

    public AppShell(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _focus = services.GetRequiredService<IFocusHandoff>();
    }

    public bool IsPanelOpen => _panel?.IsVisible == true;

    public PanelViewModel PanelViewModel => _panelViewModel ??= CreatePanelViewModel();

    /// <summary>Tray left-click: toggles the panel, ignoring the click that just dismissed it.</summary>
    public void TogglePanelFromTray(PixelRect? trayRect, PixelPoint clickPoint)
    {
        if (IsPanelOpen)
        {
            ClosePanel(PanelCloseReason.TrayIcon);
            return;
        }

        if (DateTime.UtcNow - _lastDeactivationClose < ReopenGuard)
        {
            return;
        }

        _focus.Remember();
        ShowPanelCore(null, trayRect, clickPoint);
    }

    public void ShowPanel(string? sectionId = null)
    {
        var screens = _services.GetRequiredService<IScreenService>();
        var tray = _services.GetService<ITrayIcon>();
        _focus.Remember();
        ShowPanelCore(sectionId, tray?.Bounds, screens.CursorPosition);
    }

    public void ClosePanel() => ClosePanel(PanelCloseReason.Action);

    public void ClosePanel(PanelCloseReason reason)
    {
        if (_panel is null || !_panel.IsVisible)
        {
            return;
        }

        _panel.Hide();
        if (reason is PanelCloseReason.Escape or PanelCloseReason.TrayIcon)
        {
            Dispatcher.UIThread.Post(_focus.Restore, DispatcherPriority.Background);
        }
    }

    public IDisposable KeepPanelOpen(string reason)
    {
        var token = new KeepOpenToken(this, reason);
        _keepOpen.Add(token);
        return token;
    }

    public void OpenSettings(string? pageId = null, string? revealFeatureId = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(new SettingsViewModel(_services));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.ViewModel.Navigate(pageId ?? (revealFeatureId is null ? null : SettingsPageIds.Features), revealFeatureId);
        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
        {
            _settingsWindow.WindowState = Avalonia.Controls.WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    public void Quit()
    {
        Log.Info("shell", "Quit requested.");
        AppLifetime.RequestShutdown();
    }

    /// <summary>Closes every window for good (app shutdown).</summary>
    public void Shutdown()
    {
        if (_panel is not null)
        {
            _panel.ReallyClose = true;
            _panel.Close();
        }

        _settingsWindow?.Close();
    }

    private PanelViewModel CreatePanelViewModel()
    {
        var vm = new PanelViewModel(_services);
        vm.OpenSettingsRequested += (_, _) =>
        {
            var page = vm.CurrentSection?.SettingsPageId;
            OpenSettings(page);
        };
        vm.QuitRequested += (_, _) => Quit();
        vm.Rebuild();
        return vm;
    }

    private void ShowPanelCore(string? sectionId, PixelRect? trayRect, PixelPoint clickPoint)
    {
        if (_panel is null)
        {
            _panel = new PanelWindow(PanelViewModel);
            _panel.CloseRequested += (_, reason) => ClosePanel(reason);
            _panel.Deactivated += (_, _) => OnPanelDeactivated();
        }

        _panel.UseTranslucency = _settings.Get(ShellSettings.TranslucencyEnabled);
        if (sectionId is not null)
        {
            PanelViewModel.Focus(sectionId);
        }

        _panel.PlaceNear(trayRect, clickPoint);
        _panel.Show();
        _panel.Activate();
        _services.GetService<IWindowChrome>()?.BringToFront(WindowInterop.Handle(_panel));
        _panel.PlaceNear(trayRect, clickPoint);
    }

    private void OnPanelDeactivated()
    {
        if (_panel is null || !_panel.IsVisible)
        {
            return;
        }

        var section = PanelViewModel.CurrentSection;
        if (_keepOpen.Count > 0 || section?.KeepsPanelOpen == true)
        {
            return;
        }

        _lastDeactivationClose = DateTime.UtcNow;
        ClosePanel(PanelCloseReason.OutsideClick);
    }

    private sealed class KeepOpenToken(AppShell owner, string reason) : IDisposable
    {
        public string Reason { get; } = reason;

        public void Dispose() => owner._keepOpen.Remove(this);
    }
}
