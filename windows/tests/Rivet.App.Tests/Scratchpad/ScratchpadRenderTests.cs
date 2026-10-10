// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string) is fine for snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Scratchpad;
using Rivet.App.Settings;
using Rivet.Core.Features;
using Rivet.Core.Modules.Scratchpad;
using Xunit;

namespace Rivet.App.Tests.Scratchpad;

public class ScratchpadRenderTests
{
    private const string Sample = "# Groceries\n\n- **Milk** and eggs\n- ~~Bread~~\n- Coffee from [the shop](https://example.com)\n\n> Remember the `bags`\n\n1. one\n2. two";

    private static ScratchpadService OpenPad(ThemeVariant theme)
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.Scratchpad, true);
        var service = host.Services.GetRequiredService<ScratchpadService>();
        service.Show();
        Assert.True(service.IsVisible);
        var window = service.Window!;
        window.RequestedThemeVariant = theme;
        window.Width = 420;
        window.Height = 380;
        return service;
    }

    private static void Save(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, name + ".png"));
    }

    private static ScratchpadWindow NewWindow(ThemeVariant theme, string text, bool twoTabs)
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.Scratchpad, true);
        var service = host.Services.GetRequiredService<ScratchpadService>();
        var first = new ScratchpadPad(Guid.NewGuid(), "Groceries", text, DateTimeOffset.Now);
        var pads = twoTabs ? new[] { first, new ScratchpadPad(Guid.NewGuid(), "Scratchpad 2", string.Empty, null) } : [first];
        var document = new ScratchpadDocument(pads, first.Id);
        var window = new ScratchpadWindow(service) { Width = 420, Height = 380, RequestedThemeVariant = theme };
        window.Show();
        window.ResetForShow(pinned: false);
        window.Render(document, saveFailed: false, textSize: 13, opacity: 0, forceText: true);
        return window;
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_pad_renders_with_tabs_and_the_format_row(string theme)
    {
        var window = NewWindow(theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light, Sample, twoTabs: true);
        var formatToggle = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(t => t is not ToggleSwitch);
        formatToggle.IsChecked = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Save(window, $"scratchpad-{theme}");
        window.Close();
    }

    [AvaloniaFact]
    public void The_save_failure_banner_and_find_bar_render()
    {
        var window = NewWindow(ThemeVariant.Light, "find me, then find me again", twoTabs: false);
        var host = TestApp.Host;
        var document = new ScratchpadDocument([new ScratchpadPad(Guid.NewGuid(), "Notes", "find me, then find me again", null)], Guid.Empty);
        window.Render(new ScratchpadDocument(document.Pads, document.Pads[0].Id), saveFailed: true, textSize: 13, opacity: 0, forceText: true);
        window.OpenFind();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Save(window, "scratchpad-banner-find");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_preview_renders_markdown(string theme)
    {
        var window = NewWindow(theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light, Sample, twoTabs: true);
        window.SetPreview(true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(window.PreviewOn);
        Save(window, $"scratchpad-preview-{theme}");
        window.SetPreview(false);
        Assert.Equal(Sample, window.Editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void A_mark_is_one_undo_step_and_the_text_is_saved()
    {
        var service = OpenPad(ThemeVariant.Light);
        var window = service.Window!;
        window.Editor.Text = "hello world";
        window.Editor.SelectionStart = 0;
        window.Editor.SelectionEnd = 5;
        service.ApplyMark(MarkdownMark.Bold);
        Assert.Equal("**hello** world", window.Editor.Text);
        Assert.Equal("hello", window.Editor.SelectedText);
        Assert.Equal("**hello** world", service.Document!.Selected.Text);
        window.Editor.Undo();
        Assert.Equal("hello world", window.Editor.Text);
        service.Hide();
    }

    [AvaloniaFact]
    public void Preview_rendering_keeps_the_text_of_every_block()
    {
        var rendered = MarkdownPreview.Render(Sample, new MarkdownPreview.Style(13, Brushes.Black, Brushes.Gray, Brushes.Blue, null));
        var text = MarkdownPreview.PlainText(rendered);
        Assert.Contains("Groceries", text, StringComparison.Ordinal);
        Assert.Contains("• Milk and eggs", text, StringComparison.Ordinal);
        Assert.Contains("▏ Remember the bags", text, StringComparison.Ordinal);
        Assert.Contains("1. one", text, StringComparison.Ordinal);
        Assert.Contains("2. two", text, StringComparison.Ordinal);
        Assert.Contains("the shop", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void The_settings_page_renders()
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.Scratchpad, true);
        foreach (var theme in new[] { "light", "dark" })
        {
            var vm = new SettingsViewModel(host.Services);
            vm.Navigate(ScratchpadModule.PageId);
            Assert.Equal(ScratchpadModule.PageId, vm.CurrentPageId);
            var window = new SettingsWindow(vm) { Width = 1080, Height = 860, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
            window.Show();
            Save(window, $"settings-scratchpad-{theme}");
            window.Close();
        }
    }
}
