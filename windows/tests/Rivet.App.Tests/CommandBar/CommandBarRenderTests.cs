// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Features.Launcher;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.App.Tests.Clipboard;
using Rivet.App.Tests.QuickToggles;
using Rivet.App.Tests.Snippets;
using Rivet.Core.Features;
using Rivet.Core.Launcher;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.App.Tests.CommandBar;

public class CommandBarRenderTests
{
    internal static CommandBarController Seed(IServiceProvider services)
    {
        ClipboardRenderTests.Seed(services);
        SnippetRenderTests.Seed(services);
        QuickToggleRenderTests.Seed(services);
        services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.CommandBar, true);
        TestApp.AddSampleContent(services.GetRequiredService<PanelRegistry>());
        // A fresh controller (and window) per test: a headless window that was hidden and shown again keeps its old frame.
        var controller = new CommandBarController(services);
        controller.Sync(true);
        return controller;
    }

    private static async Task<CommandBarController> OpenAsync(string theme, string? query = null)
    {
        var services = TestApp.Host.Services;
        var controller = Seed(services);
        controller.Window.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        await controller.OpenAsync();
        await Settle();
        if (query is not null)
        {
            controller.SetQuery(query);
            await Settle();
        }

        return controller;
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(60);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Save(CommandBarController controller, string name)
    {
        controller.Window.Render();
        Dispatcher.UIThread.RunJobs();
        ClipboardRenderTests.SaveWindow(controller.Window, name);
        controller.Close(FloatingCloseReason.Action);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Home_renders(string theme)
    {
        var controller = await OpenAsync(theme);
        Assert.True(controller.Session.Items.Count > 0);
        Save(controller, $"command-bar-home-{theme}");
    }

    [AvaloniaTheory]
    [InlineData("100 km to mi", "units")]
    [InlineData("2+2*3", "math")]
    [InlineData("note", "mixed")]
    [InlineData(":fire", "emoji")]
    [InlineData("#336699", "color")]
    public async Task Typed_queries_render(string query, string name)
    {
        var controller = await OpenAsync("light", query);
        Assert.NotEmpty(controller.Session.Rows);
        Save(controller, $"command-bar-{name}");
    }

    [AvaloniaFact]
    public async Task Typed_query_renders_dark()
    {
        var controller = await OpenAsync("dark", "clip");
        Assert.NotEmpty(controller.Session.Rows);
        Save(controller, "command-bar-mixed-dark");
    }

    [AvaloniaFact]
    public async Task Calculator_answer_comes_first()
    {
        var controller = await OpenAsync("light", "2+2*3");
        Assert.Equal("8", controller.Session.Rows[0].Title);
        controller.Close(FloatingCloseReason.Action);
    }

    [AvaloniaFact]
    public async Task Actions_and_confirm_modes_render()
    {
        var controller = await OpenAsync("light", "notepad");
        Assert.True(controller.OpenActions());
        Assert.Equal(CommandBarMode.Actions, controller.Session.Mode);
        controller.Window.Render();
        Dispatcher.UIThread.RunJobs();
        ClipboardRenderTests.SaveWindow(controller.Window, "command-bar-actions");

        controller.Session.Escape();
        controller.SetQuery("restart");
        await Settle();
        var restart = controller.Session.Rows.First(r => r.Id == "action.power.restart");
        controller.Carry(controller.Session.Plan(restart));
        Assert.Equal(CommandBarMode.Confirm, controller.Session.Mode);
        Save(controller, "command-bar-confirm");
    }

    [AvaloniaFact]
    public async Task Providers_are_registered_and_search()
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var registry = services.GetRequiredService<SearchProviderRegistry>();
        var calculator = registry.Active.First(p => p.Id == "commandBar.calculator");
        var results = await calculator.SearchAsync("12*12", CancellationToken.None);
        Assert.Equal("144", Assert.Single(results).Title);
        var apps = registry.Active.First(p => p.Id == "commandBar.apps");
        Assert.Contains(await apps.SearchAsync("notep", CancellationToken.None), r => r.Title == "Notepad");
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Seed(services);
        var preferences = services.GetRequiredService<CommandBarPreferences>();
        var settings = services.GetRequiredService<ISettingsStore>();
        if (settings.Get(CommandBarSettings.Links).Count == 0)
        {
            settings.Set(CommandBarSettings.Links,
            [
                new CommandBarLink { Name = "gh", Destination = "https://github.com/search?q={query}" },
                new CommandBarLink { Name = "Projects", Kind = CommandBarLinkKind.Place, Destination = "~\\Projects" },
            ]);
            preferences.SetAlias("app.c:\\windows\\system32\\notepad.exe", "np");
            preferences.TogglePin("settings.clipboard");
            preferences.SetHidden("macsettings.ms-settings:about", true);
        }

        var vm = new SettingsViewModel(services);
        vm.Navigate(CommandBarModule.PageId);
        Assert.Equal(CommandBarModule.PageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 2600, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        ClipboardRenderTests.SaveWindow(window, $"settings-commandBar-{theme}");
        window.Close();
    }

    [AvaloniaFact]
    public void Link_editor_and_app_center_render()
    {
        var services = TestApp.Host.Services;
        var controller = Seed(services);
        var dialog = new CommandBarLinkDialog(new CommandBarLink { Name = "gh", Destination = "https://github.com/search?q={query}" }) { RequestedThemeVariant = ThemeVariant.Light };
        dialog.Show();
        ClipboardRenderTests.SaveWindow(dialog, "command-bar-link-editor");
        dialog.Close();

        var apps = services.GetRequiredService<ICommandBarPlatform>().GetAppsAsync(CancellationToken.None).Result;
        var center = new AppShortcutsDialog(services) { RequestedThemeVariant = ThemeVariant.Dark };
        center.ShowRows(controller.Catalog.AppRows(apps, []).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase));
        center.Show();
        ClipboardRenderTests.SaveWindow(center, "command-bar-app-center");
        center.Close();
    }

    [AvaloniaFact]
    public void Built_string_keys_exist()
    {
        var services = TestApp.Host.Services;
        var keys = WindowsSettingsPages.All.Select(p => p.TitleKey)
            .Concat(services.GetRequiredService<ICommandBarPlatform>().KnownFolders().Where(f => f.Key != "home").Select(f => "win.commandBar.folder." + f.Key));
        Assert.DoesNotContain(keys, k => !Rivet.Core.Localization.Localizer.Current.TryGet(k, out _));
    }
}
