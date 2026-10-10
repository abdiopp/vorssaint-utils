// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Shell;

/// <summary>
/// First run: welcome and language, then a bundle of features to start with,
/// then where to find the tray icon. Closing early keeps the Essentials bundle.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly ContentControl _page = new();
    private int _step;
    private FeaturePreset? _preset = FeaturePresets.Essentials;

    public OnboardingWindow(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        Width = 620;
        Height = 660;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcons.Window;
        Title = AppIdentity.DisplayName;
        this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush").ToBinding());
        Content = new Border { Padding = new Thickness(36, 28), Child = _page };
        Closed += (_, _) => Finish(applyPreset: false);
        Show(0);
    }

    /// <summary>Raised once, when onboarding completes or the window closes.</summary>
    public event EventHandler? Completed;

    public int Step => _step;

    public void Show(int step)
    {
        _step = step;
        _page.Content = step switch
        {
            0 => WelcomePage(),
            1 => BundlePage(),
            _ => DonePage(),
        };
        _settings.Set(ShellSettings.OnboardingStep, Math.Max(1, step));
    }

    private Control WelcomePage()
    {
        var languages = AppLanguages.All.ToList();
        var language = new ComboBox
        {
            MinWidth = 220,
            ItemsSource = languages.Select(l => l.NativeName()).ToList(),
            SelectedIndex = languages.IndexOf(Localizer.Current.Language),
        };
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedIndex >= 0)
            {
                var chosen = languages[language.SelectedIndex];
                _settings.Set(ShellSettings.AppLanguage, chosen.Code());
                Localizer.Current.Language = chosen;
                Show(0);
            }
        };

        var bullets = new StackPanel { Spacing = 14, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (icon, title, body) in new[]
                 {
                     ("Screenshot", "win.shell.obBullet1Title", "win.shell.obBullet1Body"),
                     ("DataTrending", "win.shell.obBullet2Title", "win.shell.obBullet2Body"),
                     ("WrenchScrewdriver", "win.shell.obBullet3Title", "win.shell.obBullet3Body"),
                 })
        {
            bullets.Children.Add(Bullet(icon, L.Get(title), L.Get(body)));
        }

        return Layout(
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Hero(),
                    new TextBlock { Text = L.Get("Strings.obStepWelcomeTitle"), FontSize = 28, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = L.Get("Strings.obStepWelcomeBody"), Classes = { "caption" }, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center },
                    bullets,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0),
                        Children = { new TextBlock { Text = L.Get("Strings.obLanguageLabel"), VerticalAlignment = VerticalAlignment.Center }, language },
                    },
                },
            },
            back: null,
            next: (L.Get("Strings.obContinue"), () => Show(1)));
    }

    private Control BundlePage()
    {
        var cards = new StackPanel { Spacing = 10 };
        var choices = new List<(FeaturePreset? Preset, Border Card)>();
        void Select(FeaturePreset? preset)
        {
            _preset = preset;
            foreach (var (p, card) in choices)
            {
                var selected = ReferenceEquals(p, preset);
                card.Bind(Border.BorderBrushProperty, card.GetResourceObservable(selected ? "AccentBrush" : "SettingsCardBorderBrush").ToBinding());
                card.BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }

        foreach (var preset in FeaturePresets.All)
        {
            var card = new Border
            {
                Classes = { "settingsCard" },
                Padding = new Thickness(14),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    ColumnSpacing = 14,
                    Children =
                    {
                        new Border { Classes = { "iconTile" }, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top, Child = new SymbolIcon { Symbol = IconConverter.Parse(preset.Icon), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                        Column(new StackPanel
                        {
                            Spacing = 3,
                            Children =
                            {
                                new TextBlock { Text = L.Get(preset.NameKey), FontWeight = FontWeight.SemiBold, FontSize = 15 },
                                new TextBlock { Text = L.Get(preset.DescriptionKey), Classes = { "caption" } },
                            },
                        }, 1),
                    },
                },
            };
            AutomationProperties.SetName(card, L.Get(preset.NameKey));
            var captured = preset;
            card.PointerPressed += (_, _) => Select(captured);
            choices.Add((preset, card));
            cards.Children.Add(card);
        }

        Select(_preset);
        return Layout(
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = L.Get("Strings.obPurposeTitle"), FontSize = 24, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = L.Get("Strings.obPurposeBody"), Classes = { "caption" }, FontSize = 14 },
                    cards,
                    new TextBlock { Text = L.Get("Strings.obPurposeSkip"), Classes = { "caption", "tertiary" } },
                },
            },
            back: (L.Get("Strings.obBack"), () => Show(0)),
            next: (L.Get("Strings.obContinue"), () => Show(2)));
    }

    private Control DonePage()
    {
        var autostart = _services.GetRequiredService<IAutostartService>();
        var launch = new ToggleSwitch { Classes = { "compact" }, IsChecked = autostart.State != AutostartState.Disabled };
        launch.IsCheckedChanged += (_, _) =>
        {
            _settings.Set(ShellSettings.LaunchAtLoginWanted, launch.IsChecked == true);
            autostart.SetEnabled(launch.IsChecked == true);
        };
        var openTaskbar = new Button { Content = L.Get("win.shell.trayPinGuideOpenSettings") };
        openTaskbar.Click += (_, _) => _services.GetRequiredService<IShellService>().OpenSystemSettings("ms-settings:taskbar");

        return Layout(
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    Hero(),
                    new TextBlock { Text = L.Get("Strings.obStepDoneTitle"), FontSize = 28, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = L.Format("win.shell.obDoneBodyFormat", AppIdentity.DisplayName), Classes = { "caption" }, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center },
                    new Border
                    {
                        Classes = { "settingsCard" },
                        Child = new StackPanel
                        {
                            Spacing = 12,
                            Children =
                            {
                                new TextBlock { Text = L.Get("win.shell.obDoneHint"), TextWrapping = TextWrapping.Wrap },
                                openTaskbar,
                                new Border { Classes = { "separator" } },
                                new SettingsRow { Icon = "Rocket", Title = L.Get("Strings.launchAtLogin"), Description = L.Get("win.shell.launchAtLoginCaption"), Content = launch },
                            },
                        },
                    },
                },
            },
            back: (L.Get("Strings.obBack"), () => Show(1)),
            next: (L.Get("Strings.obStart"), () => Finish(applyPreset: true)));
    }

    private bool _finished;

    private void Finish(bool applyPreset)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (applyPreset && _preset is { } preset)
        {
            _services.GetRequiredService<FeatureRuntime>().ReplaceAvailable(preset.Features, preset.EnableKeys);
        }

        _settings.Set(ShellSettings.HasOnboarded, true);
        _settings.Set(ShellSettings.TrayPinGuideShown, true);
        Completed?.Invoke(this, EventArgs.Empty);
        if (IsVisible)
        {
            Close();
        }
    }

    private static Control Hero()
    {
        var image = AppIcons.Image;
        return image is null
            ? new AppGlyph { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center }
            : new Image { Source = image, Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Center };
    }

    private static Control Bullet(string icon, string title, string body) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        ColumnSpacing = 14,
        Children =
        {
            new Border { Classes = { "iconTile" }, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top, Child = new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
            Column(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = body, Classes = { "caption" } } } }, 1),
        },
    };

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Control Layout(Control body, (string Text, Action Click)? back, (string Text, Action Click) next)
    {
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (back is { } b)
        {
            var backButton = new Button { Content = b.Text, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
            backButton.Click += (_, _) => b.Click();
            buttons.Children.Add(backButton);
        }

        var nextButton = new Button { Content = next.Text, MinWidth = 140, Classes = { "accent" }, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        nextButton.Click += (_, _) => next.Click();
        Grid.SetColumn(nextButton, 2);
        buttons.Children.Add(nextButton);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(buttons, 1);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(buttons);
        return root;
    }
}
