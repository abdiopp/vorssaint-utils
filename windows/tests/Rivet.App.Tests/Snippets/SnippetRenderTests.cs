// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Features.Snippets;
using Rivet.App.Settings;
using Rivet.App.Tests.Clipboard;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Snippets;
using Xunit;

namespace Rivet.App.Tests.Snippets;

public class SnippetRenderTests
{
    internal static IReadOnlyList<TextSnippet> Seed(IServiceProvider services)
    {
        services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.TextSnippets, true);
        var settings = services.GetRequiredService<ISettingsStore>();
        if (settings.Get(SnippetSettings.Snippets).Count == 0)
        {
            settings.Set(SnippetSettings.Snippets,
            [
                new TextSnippet { Name = "Personal email", Trigger = ";email", Replacement = "me@example.com" },
                new TextSnippet { Name = "Home address", Trigger = ";addr", Replacement = "12 Harbour Street\nPortsmouth", Folder = "Personal" },
                new TextSnippet { Name = "Sign-off", Trigger = ";sig", Replacement = "Best regards,\nSam", Folder = "Work", Expansion = SnippetExpansion.Immediate },
                new TextSnippet { Name = "Meeting date", Trigger = ";today", Replacement = "Meeting on {{date-tz(Europe/London):EEEE d MMMM}}", Folder = "Work", IgnoresCase = true },
                new TextSnippet { Trigger = ";shrug", Replacement = "¯\\_(ツ)_/¯", Enabled = false },
            ]);
        }

        settings.Set(SnippetSettings.ExpansionEnabled, true);
        settings.Set(SnippetSettings.LibraryEnabled, true);
        return settings.Get(SnippetSettings.Snippets);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Library_window_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var window = new SnippetLibraryWindow(services) { RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Open();
        ClipboardRenderTests.SaveWindow(window, $"snippets-library-{theme}");
        window.Dismiss(FloatingCloseReason.Action);
    }

    [AvaloniaFact]
    public void Editor_dialog_renders()
    {
        var services = TestApp.Host.Services;
        var all = Seed(services);
        var dialog = new SnippetEditorDialog(all[3], all, isNew: false) { RequestedThemeVariant = ThemeVariant.Light };
        dialog.Show();
        ClipboardRenderTests.SaveWindow(dialog, "snippets-editor");
        dialog.Close();
    }

    [AvaloniaFact]
    public void Editor_flags_duplicate_trigger()
    {
        var services = TestApp.Host.Services;
        var all = Seed(services);
        var dialog = new SnippetEditorDialog(new TextSnippet { Trigger = ";EMAIL", Replacement = "x", IgnoresCase = true }, all, isNew: true);
        Assert.Equal(SnippetValidation.DuplicateTrigger, SnippetValidator.Validate(dialog.Edited, all));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Date_time_builder_renders(string theme)
    {
        var token = SnippetVariables.TokenAt("Due {{datetime-tz(Asia/Tokyo):yyyy-MM-dd HH:mm}}", 8);
        Assert.NotNull(token);
        TestApp.Snapshot(new Border { Padding = new Avalonia.Thickness(12), Child = new DateTimeBuilder(token) }, $"snippets-datetime-builder-{theme}", 380,
            theme: theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Seed(services);
        services.GetRequiredService<ISettingsStore>().Set(SnippetSettings.SoundEnabled, true);
        var vm = new SettingsViewModel(services);
        vm.Navigate(SnippetsModule.PageId);
        Assert.Equal(SnippetsModule.PageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 1100, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        ClipboardRenderTests.SaveWindow(window, $"settings-snippets-{theme}");
        window.Close();
    }
}
