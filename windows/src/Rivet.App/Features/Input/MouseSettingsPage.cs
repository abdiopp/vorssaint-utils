// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Input;

/// <summary>
/// Settings › Mouse &amp; touchpad: the extra click filter, smooth scrolling and
/// the scroll direction, each card shown while its feature is installed.
/// </summary>
public sealed class MouseSettingsPage : InputSettingsPage
{
    public MouseSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var runtime = services.GetRequiredService<FeatureRuntime>();
        var catalog = services.GetService<IAppCatalog>();
        var inverter = services.GetService<ScrollInverterService>();

        Control? clickCard = null;
        if (runtime.IsAvailable(FeatureIds.MouseClickDebounce))
        {
            var window = Stepper(InputSettings.ClickDebounceWindowMs, "Timer", "mouseClickDebounce.windowLabel", "mouseClickDebounce.windowCaption", 5, 100, 1);
            clickCard = LiveCard(null,
                Toggle(FeatureKeys.MouseClickDebounceEnabled, "CursorClick", "mouseClickDebounce.title", "mouseClickDebounce.caption"),
                ShowWhen(window, FeatureKeys.MouseClickDebounceEnabled));
        }

        Control? smoothCard = null;
        if (runtime.IsAvailable(FeatureIds.SmoothScroll))
        {
            var more = new Disclosure(L.Get("mouseClickDebounce.moreOptions"), new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    IntSlider(InputSettings.SmoothScrollResponse, null, "Strings.smoothScrollResponseLabel", 0, 100, 5, v => $"{v} %"),
                    IntSlider(InputSettings.SmoothScrollCoast, null, "Strings.smoothScrollCoastLabel", 0, 100, 5, v => $"{v} %"),
                },
            })
            {
                Margin = new Avalonia.Thickness(36, 0, 0, 0),
            };
            smoothCard = LiveCard(null,
                Toggle(FeatureKeys.SmoothScrollEnabled, "CursorHover", "Strings.smoothScrollName", "Strings.smoothScrollCaption"),
                ShowWhen(IntSlider(InputSettings.SmoothScrollStep, "TopSpeed", "Strings.smoothScrollStepLabel", 20, 100, 10, v => $"{v} px"), FeatureKeys.SmoothScrollEnabled),
                ShowWhen(more, FeatureKeys.SmoothScrollEnabled),
                ShowWhen(new AppExclusionEditor(Settings, InputSettings.SmoothScrollExceptions, catalog, "mouseExceptions.captionSmoothScroll"), FeatureKeys.SmoothScrollEnabled),
                IndentedNote(L.Get("win.input.wheelHeuristicNote")));
        }

        Control? directionCard = null;
        if (runtime.IsAvailable(FeatureIds.ScrollInverter))
        {
            var active = StatusText(L.Get("Strings.scrollActiveNow"));
            void UpdateActive() => active.IsVisible = inverter?.IsRunning == true;
            UpdateActive();
            if (inverter is not null)
            {
                EventHandler handler = (_, _) => Dispatcher.UIThread.Post(UpdateActive);
                inverter.StatusChanged += handler;
                Track(new Unsubscribe(() => inverter.StatusChanged -= handler));
            }

            var exceptions = new AppExclusionEditor(Settings, InputSettings.ScrollInverterExceptions, catalog, "mouseExceptions.captionScrollDirection");
            void UpdateExceptions() => exceptions.IsVisible = ScrollInverterSettings.Vertical(Settings) || ScrollInverterSettings.Horizontal(Settings);
            UpdateExceptions();
            Track(Settings.Observe(() => Dispatcher.UIThread.Post(UpdateExceptions), FeatureKeys.ScrollInverterEnabled, InputSettings.ScrollInverterHorizontal));

            directionCard = LiveCard("Strings.scrollSection",
                Toggle(FeatureKeys.ScrollInverterEnabled, "ArrowSort", "Strings.invertVerticalScroll"),
                HorizontalToggle(),
                active,
                exceptions,
                IndentedNote(L.Get("Strings.scrollTrackpadNote")));
        }

        Content = Stack(
            Header("Strings.tabMouse", "win.input.mousePageDescription"),
            clickCard,
            smoothCard,
            directionCard,
            Note(L.Get("win.input.elevatedNote")));
    }

    /// <summary>
    /// "Invert horizontal scrolling": until saved it mirrors the vertical
    /// switch (the macOS migration), so the switch shows the effective value.
    /// </summary>
    private SettingsRow HorizontalToggle()
    {
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = ScrollInverterSettings.Horizontal(Settings) };
        var syncing = false;
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!syncing)
            {
                Settings.Set(InputSettings.ScrollInverterHorizontal, toggle.IsChecked == true);
            }
        };
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(() =>
        {
            syncing = true;
            toggle.IsChecked = ScrollInverterSettings.Horizontal(Settings);
            syncing = false;
        }), FeatureKeys.ScrollInverterEnabled, InputSettings.ScrollInverterHorizontal));
        AutomationProperties.SetName(toggle, L.Get("Strings.invertHorizontalScroll"));
        return Row("ArrowSwap", L.Get("Strings.invertHorizontalScroll"), null, toggle);
    }
}

internal sealed class Unsubscribe(Action action) : IDisposable
{
    private Action? _action = action;

    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}
