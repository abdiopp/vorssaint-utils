// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): fine for test snapshots.
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Settings;
using Rivet.Core.Clipboard;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.App.Tests.Clipboard;

public class ClipboardRenderTests
{
    internal static void Seed(IServiceProvider services)
    {
        var runtime = services.GetRequiredService<FeatureRuntime>();
        runtime.SetAvailable(FeatureIds.ClipboardHistory, true);
        runtime.SetAvailable(FeatureIds.PastePlain, true);
        runtime.SetAvailable(FeatureIds.UrlCleaner, true);
        var settings = services.GetRequiredService<ISettingsStore>();
        settings.Set(ClipboardSettings.Enabled, true);
        var history = services.GetRequiredService<ClipboardHistoryService>().History;
        if (history.Entries.Count > 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        history.Record(new ClipboardEntry { Text = "Meeting notes\n- ship the Windows port\n- review the clipboard history", CopiedAt = now.AddMinutes(-50), SourceApp = "c:\\windows\\system32\\notepad.exe" });
        history.Record(new ClipboardEntry { Text = "#336699", CopiedAt = now.AddMinutes(-40), SourceApp = "c:\\apps\\figma.exe" });
        history.Record(new ClipboardEntry { Text = "https://example.com/docs/getting-started", CopiedAt = now.AddMinutes(-30), SourceApp = "c:\\program files\\microsoft\\edge\\msedge.exe" });
        history.Record(new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = ["C:\\Users\\me\\Documents\\Quarterly report.docx", "C:\\Users\\me\\Documents\\Budget 2026.xlsx", "C:\\Users\\me\\Pictures\\team.png"], CopiedAt = now.AddMinutes(-20), SourceApp = "c:\\windows\\explorer.exe" });
        var pinned = history.Record(new ClipboardEntry { Text = "{\"name\":\"Rivet\",\"version\":\"0.1.0\",\"features\":[\"clipboard\",\"snippets\"]}", CopiedAt = now.AddMinutes(-10), SourceApp = "c:\\apps\\code.exe" });
        history.Pin(pinned.Id);
        history.Record(new ClipboardEntry { Text = "Thanks! I'll send the build tonight.", CopiedAt = now.AddMinutes(-2), SourceApp = "c:\\apps\\slack.exe" });
    }

    internal static string SaveWindow(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(TestApp.SnapshotDirectory, name + ".png");
        frame!.Save(path);
        return path;
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void History_window_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var window = new ClipboardHistoryWindow(services) { RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Open();
        window.Width = 1240;
        window.Height = 338;
        SaveWindow(window, $"clipboard-history-{theme}");
        window.Dismiss(FloatingCloseReason.Action);
    }

    [AvaloniaFact]
    public void History_window_with_preview_and_search_renders()
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var settings = services.GetRequiredService<ISettingsStore>();
        settings.Set(ClipboardSettings.QuickPreview, true);
        var window = new ClipboardHistoryWindow(services) { RequestedThemeVariant = ThemeVariant.Light };
        window.Open();
        window.Width = 1240;
        window.Height = 338;
        SaveWindow(window, "clipboard-history-preview");
        window.Dismiss(FloatingCloseReason.Action);
        settings.Set(ClipboardSettings.QuickPreview, false);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Panel_view_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Seed(services);
        TestApp.Snapshot(new Border { Padding = new Avalonia.Thickness(12), Child = new ClipboardPanelView(services) }, $"clipboard-panel-{theme}", 340, theme: theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    [AvaloniaFact]
    public void Url_cleaner_view_renders()
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var view = new UrlCleanerView(services, showAutomaticSwitch: true);
        view.SetInput("https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=abc&feature=share");
        TestApp.Snapshot(new Border { Padding = new Avalonia.Thickness(12), Child = view }, "url-cleaner-panel", 340);
    }

    [AvaloniaTheory]
    [InlineData(ClipboardModule.ClipboardPageId, "light")]
    [InlineData(ClipboardModule.ClipboardPageId, "dark")]
    [InlineData(ClipboardModule.UrlCleanerPageId, "light")]
    [InlineData(ClipboardModule.UrlCleanerPageId, "dark")]
    public void Settings_pages_render(string pageId, string theme)
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var vm = new SettingsViewModel(services);
        vm.Navigate(pageId);
        Assert.Equal(pageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 1100, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        SaveWindow(window, $"settings-{pageId}-{theme}");
        window.Close();
    }
}
