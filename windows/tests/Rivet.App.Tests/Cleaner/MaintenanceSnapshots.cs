// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): fine for test snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Settings;
using Rivet.Core.Localization;
using Xunit;

namespace Rivet.App.Tests.Maintenance;

/// <summary>Renders maintenance views the way the panel and Settings show them, in both themes.</summary>
internal static class MaintenanceSnapshots
{
    public static readonly (string Name, ThemeVariant Theme)[] Themes = [("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark)];

    /// <summary>A panel-like surface (340 wide, 12 padding) with the hosted tool's back header.</summary>
    public static string Panel(Func<Control> build, string name, string titleKey, string icon, ThemeVariant theme, double? height = null)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new SymbolIcon { Symbol = FluentIcons.Common.Symbol.ChevronLeft, FontSize = 14, Margin = new Thickness(6) },
                new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 15, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = L.Get(titleKey), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var surface = new Border
        {
            Width = 340,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 8, Children = { header, build() } },
        };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        surface.Bind(Border.BorderBrushProperty, surface.GetResourceObservable("PanelBorderBrush").ToBinding());
        Settle();
        return TestApp.Snapshot(surface, name, 342, height, theme);
    }

    /// <summary>A Settings page inside the real Settings window.</summary>
    public static string SettingsPage(IServiceProvider services, string pageId, string name, ThemeVariant theme, double height = 1200)
    {
        var vm = new SettingsViewModel(services);
        vm.Navigate(pageId);
        Assert.Equal(pageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = height, RequestedThemeVariant = theme };
        window.Show();
        Settle();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(TestApp.SnapshotDirectory, name + ".png");
        frame!.Save(path);
        window.Close();
        return path;
    }

    /// <summary>Runs posted UI work (views rebuild through the dispatcher).</summary>
    public static void Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Waits for a background operation while keeping the dispatcher running.</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
            waited += 20;
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Timed out waiting for the condition.");
    }
}

/// <summary>The test host's services with a few replaced (fresh state per test).</summary>
internal sealed class OverrideServices(IServiceProvider inner, params (Type Type, object Instance)[] overrides) : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        foreach (var (type, instance) in overrides)
        {
            if (type == serviceType)
            {
                return instance;
            }
        }

        return inner.GetService(serviceType);
    }
}
