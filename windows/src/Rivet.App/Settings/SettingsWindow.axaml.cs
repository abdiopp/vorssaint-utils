// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.App.Shell;
using Rivet.Core.App;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
        : this(new SettingsViewModel(AppHost.Current!.Services))
    {
    }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        AvaloniaXamlLoader.Load(this);
        Icon = AppIcons.Window;
        RestoreSize();
        ApplyBackdrop();
        ActualThemeVariantChanged += (_, _) => ApplyBackdrop();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CurrentPage))
            {
                this.FindControl<ScrollViewer>("PageScroller")?.ScrollToHome();
            }
        };
        Closing += (_, _) => SaveSize();
    }

    public SettingsViewModel ViewModel { get; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Mouse side buttons navigate the page history.
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed)
        {
            ViewModel.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (properties.IsXButton2Pressed)
        {
            ViewModel.GoForwardCommand.Execute(null);
            e.Handled = true;
        }

        base.OnPointerPressed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Left)
        {
            ViewModel.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Right)
        {
            ViewModel.GoForwardCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void ApplyBackdrop()
    {
        var settings = AppHost.Current?.Services.GetService<ISettingsStore>();
        var translucent = settings?.Get(ShellSettings.TranslucencyEnabled) ?? true;
        TransparencyLevelHint = translucent ? [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None] : [WindowTransparencyLevel.None];
        if (translucent && ActualTransparencyLevel == WindowTransparencyLevel.Mica)
        {
            Background = Brushes.Transparent;
        }
        else
        {
            Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush").ToBinding());
        }

        var handle = WindowInterop.Handle(this);
        if (handle != 0)
        {
            AppHost.Current?.Services.GetService<IWindowChrome>()?.SetDarkTitleBar(handle, ActualThemeVariant == ThemeVariant.Dark);
        }
    }

    private void RestoreSize()
    {
        var settings = AppHost.Current?.Services.GetService<ISettingsStore>();
        if (settings is null)
        {
            return;
        }

        var width = settings.Get(ShellSettings.SettingsWindowWidth);
        var height = settings.Get(ShellSettings.SettingsWindowHeight);
        if (width >= MinWidth && height >= MinHeight)
        {
            Width = width;
            Height = height;
        }
    }

    private void SaveSize()
    {
        var settings = AppHost.Current?.Services.GetService<ISettingsStore>();
        if (settings is null || WindowState != WindowState.Normal)
        {
            return;
        }

        settings.Set(ShellSettings.SettingsWindowWidth, Bounds.Width);
        settings.Set(ShellSettings.SettingsWindowHeight, Bounds.Height);
    }
}
