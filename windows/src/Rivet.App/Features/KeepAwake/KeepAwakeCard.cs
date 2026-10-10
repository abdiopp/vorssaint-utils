// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.App.Modules;
using Rivet.Core.Awake;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Awake;

/// <summary>
/// The panel's Keep Awake card (spec 05 §3.4.10, 03 §3.18.6): status line,
/// extend chips and the main switch; duration chips with a live countdown on
/// the highlighted one and an "Until…" chip; the hint (or battery note);
/// Options; and the closed-lid row on PCs with a lid.
/// </summary>
internal sealed class KeepAwakeCard : UserControl
{
    private static readonly int[] ChipMinutes = [15, 30, 60, 120, 240, 480, 0];
    private static readonly int[] ExtendMinutes = [15, 30, 60];

    private readonly IServiceProvider _services;
    private readonly KeepAwakeManager _manager;
    private readonly ISettingsStore _settings;
    private readonly ILidActionController _lid;
    private readonly TextBlock _status = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _extend = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _switch = new() { Classes = { "compact" } };
    private readonly UniformGrid _chips = new() { Columns = 4, ColumnSpacing = 6, RowSpacing = 6 };
    private readonly TextBlock _hint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly SymbolIcon _hintIcon = new() { Symbol = FluentIcons.Common.Symbol.Battery2, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _lidSwitch = new() { Classes = { "compact" } };
    private readonly TextBlock _lidCaption = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<int, (Button Button, TextBlock Text)> _chipViews = [];
    private Button? _untilChip;
    private TextBlock? _untilText;
    private DispatcherTimer? _ticker;
    private bool _updating;

    public KeepAwakeCard(IServiceProvider services)
    {
        _services = services;
        _manager = services.GetRequiredService<KeepAwakeManager>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _lid = services.GetRequiredService<ILidActionController>();
        AutomationProperties.SetName(_switch, L.Get("Strings.keepAwakeTitle"));
        _switch.IsCheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            if (_switch.IsChecked == true && !_manager.IsActive)
            {
                _manager.StartLastPick();
            }
            else if (_switch.IsChecked != true && _manager.IsActive)
            {
                _manager.Stop();
            }

            Render();
        };
        _lidSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_updating)
            {
                _settings.Set(KeepAwakeSettings.LidModePreferred, _lidSwitch.IsChecked == true);
            }
        };
        AutomationProperties.SetName(_lidSwitch, L.Get("Strings.clamshellTitle"));
        Content = Build();
        AttachedToVisualTree += (_, _) =>
        {
            _manager.StateChanged += OnStateChanged;
            _subscriptions.Add(_settings.Observe(() => Dispatcher.UIThread.Post(Render), KeepAwakeSettings.LidModePreferred, KeepAwakeSettings.BatteryLimitPercent));
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ticker.Tick += (_, _) => Tick();
            _ticker.Start();
            Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _manager.StateChanged -= OnStateChanged;
            _ticker?.Stop();
            _ticker = null;
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        };
        Render();
    }

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);

    private Control Build()
    {
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, MinHeight = 28 };
        top.Children.Add(_status);
        Grid.SetColumn(_switch, 1);
        top.Children.Add(_switch);
        foreach (var minutes in ExtendMinutes)
        {
            var label = L.Format("win.keepAwake.extendFormat", KeepAwakeFormat.ChipLabel(minutes));
            var chip = new Button { Classes = { "icon" }, Padding = new Thickness(5, 1), MinHeight = 20, Content = new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeight.SemiBold } };
            chip.Bind(Button.BackgroundProperty, chip.GetResourceObservable("ChipBrush").ToBinding());
            ToolTip.SetTip(chip, label);
            AutomationProperties.SetName(chip, label);
            var extend = minutes;
            chip.Click += (_, _) => _manager.Extend(extend);
            _extend.Children.Add(chip);
        }

        foreach (var minutes in ChipMinutes)
        {
            var text = new TextBlock { Text = KeepAwakeFormat.ChipLabel(minutes), FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, FontFeatures = SystemMonitor.Panel.MonitorUi.TabularDigits };
            var chip = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(2, 5), Content = text };
            AutomationProperties.SetName(chip, KeepAwakeFormat.DurationTitle(minutes));
            ToolTip.SetTip(chip, KeepAwakeFormat.DurationTitle(minutes));
            var preset = minutes;
            chip.Click += (_, _) => OnChip(preset);
            _chipViews[minutes] = (chip, text);
            _chips.Children.Add(chip);
        }

        _untilText = new TextBlock { Text = L.Get("win.keepAwake.untilChip"), FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        _untilChip = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(2, 5), Content = _untilText };
        AutomationProperties.SetName(_untilChip, L.Get("Strings.keepAwakeUntilLabel"));
        _untilChip.Click += (_, _) => OnUntilChip();
        _chips.Children.Add(_untilChip);

        // The hint (or the battery note) shares its row with the extend chips of a timed session.
        var hintRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 5 };
        hintRow.Children.Add(_hintIcon);
        Grid.SetColumn(_hint, 1);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        hintRow.Children.Add(_hint);
        Grid.SetColumn(_extend, 2);
        hintRow.Children.Add(_extend);
        var options = new Fold(L.Get("Strings.keepAwakeOptions"), BuildOptions, initiallyOpen: false, fontSize: 12, indent: 4);
        var stack = new StackPanel { Spacing = 8, Children = { top, _chips, hintRow, new Border { Classes = { "separator" } }, options } };
        if (_lid.HasLid)
        {
            var lidRow = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 6 };
            lidRow.Children.Add(new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Laptop, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var lidText = new StackPanel { Spacing = 1, Children = { new TextBlock { Text = L.Get("Strings.clamshellTitle"), FontSize = 12.5, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap }, _lidCaption } };
            Grid.SetColumn(lidText, 1);
            lidRow.Children.Add(lidText);
            Grid.SetColumn(_lidSwitch, 2);
            lidRow.Children.Add(_lidSwitch);
            ToolTip.SetTip(lidRow, L.Get("Strings.clamshellExplanation"));
            stack.Children.Add(new Border { Classes = { "separator" } });
            stack.Children.Add(lidRow);
        }

        var title = new TextBlock { Text = L.Get("Strings.keepAwakeTitle").ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, Margin = new Thickness(4, 0) };
        return new StackPanel { Spacing = 8, Children = { title, new Border { Classes = { "card" }, Child = stack } } };
    }

    private Control BuildOptions()
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(OptionToggle(KeepAwakeSettings.AllowDisplaySleep, "Desktop", "keepAwakeDisplaySleep.allowDisplaySleep", "keepAwakeDisplaySleep.allowDisplaySleepCaption"));
        stack.Children.Add(OptionToggle(KeepAwakeSettings.AutoStart, "Rocket", "Strings.keepAwakeAutoStart", "Strings.keepAwakeAutoStartCaption"));
        var automationTitle = $"{L.Get("keepAwakeAutomation.automationSection")} · {AutomationEditor.Summary(_settings)}";
        stack.Children.Add(new Fold(automationTitle, () => new AutomationEditor(_services, compact: true), initiallyOpen: false, fontSize: 12, indent: 8));
        var jiggle = OptionToggle(KeepAwakeSettings.MouseJiggle, "CursorClick", "Strings.keepAwakeMouseJiggle", "Strings.keepAwakeMouseJiggleCaption");
        var intervals = new[] { 1, 2, 5, 10, 15 };
        var interval = new ComboBox
        {
            MinWidth = 84,
            FontSize = 12,
            ItemsSource = intervals.Select(m => L.Format("win.keepAwake.intervalMinutesFormat", m)).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(intervals, _settings.Get(KeepAwakeSettings.MouseJiggleIntervalMinutes))),
        };
        AutomationProperties.SetName(interval, L.Get("Strings.keepAwakeMouseJiggleInterval"));
        interval.SelectionChanged += (_, _) =>
        {
            if (interval.SelectedIndex >= 0)
            {
                _settings.Set(KeepAwakeSettings.MouseJiggleIntervalMinutes, intervals[interval.SelectedIndex]);
            }
        };
        stack.Children.Add(jiggle);
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(26, 0, 0, 0),
            Children = { new TextBlock { Text = L.Get("Strings.keepAwakeMouseJiggleInterval"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, interval },
        });
        stack.Children.Add(TintPicker());
        return stack;
    }

    private Control OptionToggle(Setting<bool> setting, string icon, string titleKey, string captionKey)
    {
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = _settings.Get(setting) };
        toggle.IsCheckedChanged += (_, _) => _settings.Set(setting, toggle.IsChecked == true);
        AutomationProperties.SetName(toggle, L.Get(titleKey));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = L.Get(titleKey), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);
        ToolTip.SetTip(grid, L.Get(captionKey));
        return grid;
    }

    /// <summary>The active icon colour (the glyph shape is fixed on Windows).</summary>
    private Control TintPicker() => TintSwatches(_settings);

    /// <summary>Six swatches (five colours and "No color"); the chosen one gets a strong border.</summary>
    internal static Control TintSwatches(ISettingsStore settings, bool withTitle = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var swatches = new List<(string Name, Border Swatch)>();
        void Mark()
        {
            var current = settings.Get(KeepAwakeSettings.IconTint);
            foreach (var (name, swatch) in swatches)
            {
                swatch.BorderThickness = new Thickness(name == current ? 2 : 1);
                swatch.Bind(Border.BorderBrushProperty, swatch.GetResourceObservable(name == current ? "TextPrimaryBrush" : "PanelCardBorderBrush").ToBinding());
            }
        }

        foreach (var (name, key) in KeepAwakeSettingsPage.Tints)
        {
            var color = KeepAwakePresenter.Tint(name);
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = color is { } c ? new SolidColorBrush(Color.FromUInt32(c)) : Brushes.Transparent,
            };
            swatches.Add((name, swatch));
            var button = new Button { Classes = { "icon" }, Padding = new Thickness(2), Content = swatch };
            ToolTip.SetTip(button, L.Get(key));
            AutomationProperties.SetName(button, L.Get(key));
            var chosen = name;
            button.Click += (_, _) =>
            {
                settings.Set(KeepAwakeSettings.IconTint, chosen);
                Mark();
            };
            row.Children.Add(button);
        }

        Mark();
        if (!withTitle)
        {
            return row;
        }

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = L.Get("Strings.keepAwakeIconTintLabel"), FontSize = 12 },
                row,
            },
        };
    }

    private void OnChip(int minutes)
    {
        var state = _manager.State;
        if (IsHighlighted(state, minutes))
        {
            _manager.Stop();
        }
        else
        {
            _manager.Activate(minutes);
        }
    }

    private static bool IsHighlighted(KeepAwakeState state, int minutes) =>
        state is { IsActive: true, Trigger: KeepAwakeTrigger.Manual } && state.SessionMinutes == minutes;

    private void OnUntilChip()
    {
        var state = _manager.State;
        if (state is { IsActive: true, Trigger: KeepAwakeTrigger.Manual, SessionMinutes: null, EndTime: not null })
        {
            _manager.Stop();
            return;
        }

        var saved = _settings.Get(KeepAwakeSettings.UntilTime) > 0
            ? KeepAwakeSettings.FromReferenceSeconds(_settings.Get(KeepAwakeSettings.UntilTime)).ToLocalTime()
            : DateTimeOffset.Now.AddHours(1);
        var hour = new NumericUpDown { Minimum = 0, Maximum = 23, Value = saved.Hour, FormatString = "00", Width = 96, Increment = 1 };
        var minute = new NumericUpDown { Minimum = 0, Maximum = 59, Value = saved.Minute - (saved.Minute % 5), FormatString = "00", Width = 96, Increment = 5 };
        AutomationProperties.SetName(hour, L.Get("Strings.keepAwakeUntilLabel"));
        AutomationProperties.SetName(minute, L.Get("Strings.keepAwakeUntilLabel"));
        var summary = new TextBlock { Classes = { "caption" } };
        TimeOnly Selected() => new((int)(hour.Value ?? 0), (int)(minute.Value ?? 0));
        void UpdateSummary() => summary.Text = KeepAwakeFormat.UntilSummary(KeepAwakeManager.ResolveUntil(Selected(), DateTimeOffset.Now), DateTimeOffset.Now);
        hour.ValueChanged += (_, _) => UpdateSummary();
        minute.ValueChanged += (_, _) => UpdateSummary();
        UpdateSummary();
        var start = new Button { Content = L.Get("Strings.keepAwakeUntilStart"), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = new StackPanel
            {
                Spacing = 8,
                Width = 220,
                Children =
                {
                    new TextBlock { Text = L.Get("Strings.keepAwakeUntilLabel"), FontWeight = FontWeight.SemiBold },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { hour, new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center }, minute } },
                    summary,
                    start,
                },
            },
        };
        var refresher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        refresher.Tick += (_, _) => UpdateSummary();
        refresher.Start();
        var keepOpen = _services.GetService<IAppShell>()?.KeepPanelOpen("keepAwake.until");
        flyout.Closed += (_, _) =>
        {
            refresher.Stop();
            keepOpen?.Dispose();
        };
        start.Click += (_, _) =>
        {
            _manager.ActivateUntil(KeepAwakeManager.ResolveUntil(Selected(), DateTimeOffset.Now));
            flyout.Hide();
        };
        flyout.ShowAt(_untilChip!);
    }

    private void Tick()
    {
        if (_manager.State is { IsActive: true, EndTime: not null })
        {
            Render();
        }
    }

    private void Render()
    {
        _updating = true;
        try
        {
            var state = _manager.State;
            _switch.IsChecked = state.IsActive;
            _status.Text = StatusText(state);
            ToolTip.SetTip(_status, _status.Text);
            _extend.IsVisible = state is { IsActive: true, EndTime: not null } && state.Trigger == KeepAwakeTrigger.Manual;
            foreach (var (minutes, (button, text)) in _chipViews)
            {
                var highlighted = IsHighlighted(state, minutes);
                SetChipStyle(button, highlighted);
                text.Text = highlighted && minutes != 0 && _manager.Remaining is { } left ? KeepAwakeFormat.Chip(left) : KeepAwakeFormat.ChipLabel(minutes);
            }

            if (_untilChip is not null && _untilText is not null)
            {
                var untilActive = state is { IsActive: true, Trigger: KeepAwakeTrigger.Manual, SessionMinutes: null, EndTime: not null };
                SetChipStyle(_untilChip, untilActive);
                _untilText.Text = untilActive ? KeepAwakeFormat.Time(state.EndTime!.Value) : L.Get("win.keepAwake.untilChip");
            }

            // The hint line is always present so the card height never jumps.
            if (state.BatteryBlockedPercent is { } percent && !state.IsActive)
            {
                _hint.Text = L.Format("win.keepAwake.batteryNoteFormat", percent);
                _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable("WarningBrush").ToBinding());
                _hintIcon.IsVisible = true;
                _hintIcon.Bind(SymbolIcon.ForegroundProperty, _hintIcon.GetResourceObservable("WarningBrush").ToBinding());
            }
            else
            {
                _hint.Text = L.Get("win.keepAwake.chipHint");
                _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable("TextTertiaryBrush").ToBinding());
                _hintIcon.IsVisible = false;
            }

            _lidSwitch.IsChecked = _settings.Get(KeepAwakeSettings.LidModePreferred);
            _lidCaption.Text = state.LidMode switch
            {
                LidModeStatus.Failed => L.Get("Strings.sudoersFailed"),
                LidModeStatus.Active => L.Get("Strings.clamshellOnCaption"),
                LidModeStatus.Ready => L.Get("Strings.clamshellNeedsSession"),
                _ => L.Get("win.keepAwake.lidOffCaption"),
            };
            _lidCaption.Bind(TextBlock.ForegroundProperty, _lidCaption.GetResourceObservable(state.LidMode is LidModeStatus.Failed or LidModeStatus.Active ? "WarningBrush" : "TextSecondaryBrush").ToBinding());
        }
        finally
        {
            _updating = false;
        }
    }

    private static void SetChipStyle(Button chip, bool highlighted)
    {
        chip.Classes.Set("accent", highlighted);
        if (!highlighted)
        {
            chip.Bind(Button.BackgroundProperty, chip.GetResourceObservable("ChipBrush").ToBinding());
        }
        else
        {
            chip.ClearValue(Button.BackgroundProperty);
        }
    }

    /// <summary>"Ends in 1 h 05 min", "Active until you turn it off", the automation reason, or normal rules.</summary>
    public static string StatusText(KeepAwakeManager manager)
    {
        var state = manager.State;
        if (!state.IsActive)
        {
            return L.Get("Strings.keepAwakeNormalRules");
        }

        if (state.IsPaused)
        {
            return L.Get("win.keepAwake.pausedWhileLocked");
        }

        if (state.Trigger == KeepAwakeTrigger.Automation)
        {
            return KeepAwakePresenter.AutomationStatus(state.ActiveConditions);
        }

        return manager.Remaining is { } left
            ? $"{L.Get("Strings.keepAwakeEndsIn")} {KeepAwakeFormat.Remaining(left)}"
            : L.Get("Strings.keepAwakeUntilDisabled");
    }

    private string StatusText(KeepAwakeState state) => StatusText(_manager);
}
