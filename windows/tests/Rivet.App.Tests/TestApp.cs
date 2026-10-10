// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): the replacement overload is not needed for test snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Rivet.App.Hosting;
using Rivet.App.Modules;
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Xunit;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Rivet.App.Tests.TestApp))]

namespace Rivet.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });

    // Test classes run in parallel: build the host once, and hand it out only after Start()
    // has registered every module (a half-started host has an empty panel and catalog).
    private static readonly Lazy<AppHost> SharedHost = new(CreateHost, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>One host per test run: fake platform, in-memory settings, every module.</summary>
    public static AppHost Host => SharedHost.Value;

    private static AppHost CreateHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "rivet-app-tests-" + Guid.NewGuid());
        var host = AppHost.Create(AppPaths.ForTemporaryDirectory(root), SettingsStore.InMemory());
        host.Settings.Set(ShellSettings.HasOnboarded, true);
        host.Start();
        return host;
    }

    public static string SnapshotDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("RIVET_SNAPSHOTS")
                      ?? Path.Combine(FindRepoRoot(), "tests", "artifacts", "snapshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Shows <paramref name="content"/> in a window, renders it and saves a PNG.</summary>
    public static string Snapshot(Control content, string name, double width, double? height = null, Avalonia.Styling.ThemeVariant? theme = null)
    {
        var window = new Window
        {
            Width = width,
            SizeToContent = height is null ? SizeToContent.Height : SizeToContent.Manual,
            Content = content,
            RequestedThemeVariant = theme ?? Avalonia.Styling.ThemeVariant.Light,
        };
        if (height is { } h)
        {
            window.Height = h;
        }

        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("WindowBackgroundBrush").ToBinding());
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(SnapshotDirectory, name + ".png");
        frame!.Save(path);
        window.Close();
        return path;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rivet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Path.GetTempPath();
    }

    /// <summary>Registers stand-in Utilities tiles and Controls switches until the real modules exist.</summary>
    public static void AddSampleContent(PanelRegistry panel)
    {
        if (panel.Tiles.Count > 0)
        {
            return;
        }

        var tiles = new (string Id, string Feature, string Title, string Icon)[]
        {
            ("screenshot", FeatureIds.Screenshot, "screenshot.pageTitle", "Screenshot"),
            ("screenRecorder", FeatureIds.ScreenRecorder, "recorder.pageTitle", "Record"),
            ("cleaner", FeatureIds.Cleaner, "Strings.cleanerName", "Sparkle"),
            ("screenOCR", FeatureIds.ScreenOcr, "Strings.ocrName", "ScanText"),
            ("colorPicker", FeatureIds.ColorPicker, "Strings.colorPickerName", "Eyedropper"),
        };
        var order = 0;
        foreach (var (id, feature, title, icon) in tiles)
        {
            panel.AddTile(new PanelTileDescriptor { Id = id, FeatureId = feature, TitleKey = title, Icon = icon, Order = order += 10, ActionId = id });
        }

        panel.AddToggle(new PanelToggleDescriptor { Id = "smoothScroll", FeatureId = FeatureIds.SmoothScroll, TitleKey = "Strings.smoothScrollName", Icon = "CursorHover", Setting = FeatureKeys.SmoothScrollEnabled });
    }
}
