// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.App;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Settings.Pages;

public sealed class GeneralPage : SettingsPage
{
    public GeneralPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var autostart = services.GetRequiredService<IAutostartService>();
        var shell = services.GetRequiredService<IShellService>();

        // Launch at sign-in reflects the real registration, seeded from the stored wish.
        var state = autostart.State;
        var launchToggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = state != AutostartState.Disabled };
        var launchNote = Note(L.Get("win.shell.launchAtLoginDisabledByUser"), "WarningBrush");
        launchNote.IsVisible = state == AutostartState.DisabledByUser;
        var openStartup = ActionButton(L.Get("win.shell.openStartupSettings"), () => shell.OpenSystemSettings("ms-settings:startupapps"));
        openStartup.IsVisible = launchNote.IsVisible;
        launchToggle.IsCheckedChanged += (_, _) =>
        {
            var wanted = launchToggle.IsChecked == true;
            Settings.Set(ShellSettings.LaunchAtLoginWanted, wanted);
            var result = autostart.SetEnabled(wanted);
            launchNote.IsVisible = openStartup.IsVisible = result == AutostartState.DisabledByUser;
        };

        // Language: the 15 languages in their own names; applied live.
        var languages = AppLanguages.All.ToList();
        var languageBox = new ComboBox
        {
            MinWidth = 200,
            ItemsSource = languages.Select(l => l.NativeName()).ToList(),
            SelectedIndex = languages.IndexOf(Localizer.Current.Language),
        };
        languageBox.SelectionChanged += (_, _) =>
        {
            if (languageBox.SelectedIndex >= 0)
            {
                var language = languages[languageBox.SelectedIndex];
                Settings.Set(ShellSettings.AppLanguage, language.Code());
                Localizer.Current.Language = language;
            }
        };

        // Appearance: System / Light / Dark.
        var appearance = Track(Settings.Bind(ShellSettings.Appearance));
        var appearancePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (value, key) in new[] { (AppAppearance.System, "appearance.system"), (AppAppearance.Light, "appearance.light"), (AppAppearance.Dark, "appearance.dark") })
        {
            var radio = new RadioButton { Content = L.Get(key), GroupName = "appAppearance", IsChecked = appearance.Value == value };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    appearance.Value = value;
                }
            };
            appearancePanel.Children.Add(radio);
        }

        Content = Stack(
            Header("Strings.tabGeneral"),
            Card(null,
                Row("Rocket", L.Get("Strings.launchAtLogin"), L.Get("win.shell.launchAtLoginCaption"), launchToggle),
                launchNote.IsVisible ? new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(40, 0, 0, 0), Children = { launchNote, openStartup } } : null,
                Row("LocalLanguage", L.Get("Strings.languageLabel"), null, languageBox)),
            Card("appearance.label",
                Row("DarkTheme", L.Get("appearance.label"), L.Get("win.shell.appearanceCaption"), appearancePanel),
                Toggle(ShellSettings.TranslucencyEnabled, "Blur", "win.shell.translucencyTitle", "win.shell.translucencyCaption")));
    }
}
