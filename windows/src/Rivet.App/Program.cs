// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;

namespace Rivet.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
#if WINDOWS_PLATFORM
            Rivet.Platform.Windows.Shell.WindowsProcessSetup.AttachParentConsole();
#endif
            return SelfTest.Run();
        }

#if WINDOWS_PLATFORM
        Rivet.Platform.Windows.Shell.WindowsProcessSetup.SetAppUserModelId();
#endif

        WaitForRelaunchedProcess(args);

        var paths = AppPaths.ForCurrentUser();
        Log.Initialize(paths.Logs);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("app", "Unhandled exception.", e.ExceptionObject as Exception);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("app", "Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        Log.Info("app", $"Starting {AppIdentity.DisplayName} {AppIdentity.VersionString} ({Environment.OSVersion})");
        var host = AppHost.Create(paths);
        var instance = host.Services.GetRequiredService<ISingleInstanceService>();
        if (!instance.TryAcquire())
        {
            var forwarded = args.Where(a => a != "--autostart").ToArray();
            instance.SendToPrimary(forwarded);
            Log.Info("app", "Another copy is running; forwarded the arguments and exiting.");
            host.Dispose();
            Log.Flush();
            return 0;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Log.Error("app", "Fatal error.", ex);
            return 1;
        }
        finally
        {
            Log.Info("app", "Shutting down.");
            host.Dispose();
            instance.Dispose();
            Log.Flush();
        }

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new FontManagerOptions
            {
                DefaultFamilyName = "Segoe UI Variable Text",
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("Segoe UI") }, new FontFallback { FontFamily = new FontFamily("Segoe UI Emoji") }],
            });
        }
        else
        {
            builder = builder.WithInterFont().With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
        }

        return builder;
    }

    /// <summary><c>--relaunch-after &lt;pid&gt;</c>: wait (up to 20 s) for the old process to exit first.</summary>
    private static void WaitForRelaunchedProcess(string[] args)
    {
        var index = Array.IndexOf(args, "--relaunch-after");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
        }
    }
}
