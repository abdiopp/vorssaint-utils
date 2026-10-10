// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.App.Shell;
using Rivet.Core.Actions;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && AppHost.Current is { } host)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiThread.Configure(Dispatcher.UIThread.CheckAccess, action => Dispatcher.UIThread.Post(action));
            AppLifetime.Configure(() => Dispatcher.UIThread.Post(() =>
            {
                host.Services.GetService<AppShell>()?.Shutdown();
                desktop.Shutdown();
            }));
            StartShell(host, desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void StartShell(AppHost host, IReadOnlyList<string> args)
    {
        var services = host.Services;
        var settings = services.GetRequiredService<ISettingsStore>();

        // A start that never finished (crash during startup) skips optional windows next time.
        var previousStartCrashed = settings.Get(ShellSettings.StartupDidNotFinish);
        settings.Set(ShellSettings.StartupDidNotFinish, true);
        DispatcherTimer.RunOnce(() => settings.Set(ShellSettings.StartupDidNotFinish, false), TimeSpan.FromSeconds(20));

        services.GetRequiredService<ThemeController>().Apply();
        host.Start();
        services.GetRequiredService<TrayController>().Start();
        services.GetRequiredService<UpdateService>().StartAutomaticChecks();

        var instance = services.GetRequiredService<ISingleInstanceService>();
        instance.ActivationRequested += (_, forwarded) => HandleArguments(services, forwarded, fromSecondLaunch: true);
        services.GetRequiredService<INotificationService>().Activated += (_, actionId) =>
            _ = services.GetRequiredService<ActionRegistry>().InvokeAsync(actionId, ActionSource.Other);

        HandleArguments(services, args, fromSecondLaunch: false);

        if (!settings.Get(ShellSettings.HasOnboarded) && !previousStartCrashed)
        {
            // First run: welcome, a starting bundle, and where the tray icon lives.
            var onboarding = new OnboardingWindow(services);
            onboarding.Completed += (_, _) => services.GetRequiredService<IAppShell>().ShowPanel();
            onboarding.Show();
        }
    }

    /// <summary>
    /// Command-line and forwarded arguments: deep links (<c>&lt;id&gt;://action/…</c>),
    /// <c>--settings[=page]</c> and <c>--panel[=section]</c>. A plain second
    /// launch shows the panel (or Settings when the tray icon is unavailable).
    /// </summary>
    private static void HandleArguments(IServiceProvider services, IReadOnlyList<string> args, bool fromSecondLaunch)
    {
        var shell = services.GetRequiredService<IAppShell>();
        var handled = false;
        foreach (var arg in args)
        {
            if (AppLifetime.ParseActionUri(arg) is { } actionId)
            {
                _ = services.GetRequiredService<ActionRegistry>().InvokeAsync(actionId, ActionSource.Other);
                handled = true;
            }
            else if (arg.StartsWith("--settings", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                shell.OpenSettings(eq > 0 ? arg[(eq + 1)..] : null);
                handled = true;
            }
            else if (arg.StartsWith("--panel", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                shell.ShowPanel(eq > 0 ? arg[(eq + 1)..] : null);
                handled = true;
            }
        }

        if (!handled && fromSecondLaunch)
        {
            if (services.GetService<ITrayIcon>()?.Bounds is not null)
            {
                shell.ShowPanel();
            }
            else
            {
                shell.OpenSettings();
            }
        }

        Log.Info("app", $"Handled arguments: {string.Join(' ', args)}");
    }
}
