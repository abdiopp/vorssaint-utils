// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.App;
using Rivet.Core.Awake;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Awake;

/// <summary>Settings → Energy and display → Keep awake (spec §3.18.7).</summary>
public sealed class KeepAwakeSettingsPage : SettingsPage
{
    internal static readonly (string Name, string Key)[] Tints =
    [
        ("orange", "Strings.keepAwakeIconTintOrange"),
        ("green", "Strings.keepAwakeIconTintGreen"),
        ("blue", "Strings.keepAwakeIconTintBlue"),
        ("purple", "Strings.keepAwakeIconTintPurple"),
        ("pink", "Strings.keepAwakeIconTintPink"),
        ("none", "Strings.keepAwakeIconTintNone"),
    ];

    private readonly KeepAwakeManager _manager;
    private readonly TextBlock _status = new() { Classes = { "caption" } };
    private readonly ToggleSwitch _switch = new() { Classes = { "compact" } };
    private readonly TextBlock _lidCaption = new() { Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(40, 0, 0, 6) };
    private DispatcherTimer? _ticker;
    private bool _updating;

    public KeepAwakeSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _manager = services.GetRequiredService<KeepAwakeManager>();
        var lid = services.GetRequiredService<ILidActionController>();
        var power = services.GetRequiredService<IPowerSource>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        AutomationProperties.SetName(_switch, L.Get("Strings.keepAwakeTitle"));
        _switch.IsCheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            if (_switch.IsChecked == true)
            {
                _manager.StartLastPick();
            }
            else
            {
                _manager.Stop();
            }
        };

        var durations = KeepAwakeSettings.Durations.Select(m => (m, KeepAwakeFormat.DurationTitle(m) == L.Get("Strings.indefinitely") ? L.Get("Strings.indefinite") : KeepAwakeFormat.DurationTitle(m))).ToList();
        var session = new SettingsRow { Icon = "WeatherMoon", Title = L.Get("Strings.keepAwakeTitle"), Content = _switch };
        session.Bind(SettingsRow.DescriptionProperty, new Avalonia.Data.Binding { Source = _status, Path = nameof(TextBlock.Text) });

        var role = shortcuts.Find(KeepAwakeModule.ShortcutRoleId);
        Content = Stack(
            Header("Strings.keepAwakeTitle", "win.keepAwake.pageDescription"),
            Card(null,
                session,
                Choice(KeepAwakeSettings.DefaultDurationMinutes, "Timer", "Strings.defaultDurationLabel", null, durations)),
            Card("keepAwakeAutomation.automationSection",
                Note(L.Get("keepAwakeAutomation.automationCaption")),
                new AutomationEditor(services, compact: false)),
            Card("Strings.keepAwakeOptions",
                Toggle(KeepAwakeSettings.AutoStart, "Rocket", "Strings.keepAwakeAutoStart", "Strings.keepAwakeAutoStartCaption"),
                Toggle(ShellSettings.KeepAwakeRightClickToggle, "CursorClick", "Strings.keepAwakeRightClickToggle", "Strings.keepAwakeRightClickToggleCaption"),
                Toggle(KeepAwakeSettings.ShowCountdown, "Timer", "Strings.showCountdown"),
                Toggle(KeepAwakeSettings.AllowDisplaySleep, "Desktop", "keepAwakeDisplaySleep.allowDisplaySleep", "keepAwakeDisplaySleep.allowDisplaySleepCaption"),
                Toggle(KeepAwakeSettings.MouseJiggle, "CursorClick", "Strings.keepAwakeMouseJiggle", "Strings.keepAwakeMouseJiggleCaption"),
                Choice(KeepAwakeSettings.MouseJiggleIntervalMinutes, "Clock", "Strings.keepAwakeMouseJiggleInterval", null,
                    new[] { 1, 2, 5, 10, 15 }.Select(m => (m, L.Format("win.keepAwake.intervalMinutesFormat", m))).ToList()),
                Row("Color", L.Get("Strings.keepAwakeIconTintLabel"), L.Get("win.keepAwake.iconCaption"), KeepAwakeCard.TintSwatches(Settings, withTitle: false)),
                power.HasBattery
                    ? Choice(KeepAwakeSettings.BatteryLimitPercent, "Battery2", "Strings.batteryDisableBelow", "Strings.batteryProtectionCaption",
                        [(0, L.Get("Strings.batteryNever")), (5, "5%"), (10, "10%"), (15, "15%"), (20, "20%")])
                    : null),
            lid.HasLid
                ? Card("Strings.clamshellSection",
                    new StackPanel { Children = { Toggle(KeepAwakeSettings.LidModePreferred, "Laptop", "Strings.clamshellTitle"), _lidCaption } },
                    Note(L.Get("Strings.clamshellExplanation")))
                : null,
            role is null
                ? null
                : Card("win.keepAwake.shortcutSection",
                    Toggle(KeepAwakeSettings.HotkeyEnabled, "Keyboard", "Strings.hotkeyToggle"),
                    new ShortcutRoleRow(role)));

        AttachedToVisualTree += (_, _) =>
        {
            _manager.StateChanged += OnStateChanged;
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ticker.Tick += (_, _) => Render();
            _ticker.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _manager.StateChanged -= OnStateChanged;
            _ticker?.Stop();
        };
        Render();
    }

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        _updating = true;
        try
        {
            _switch.IsChecked = _manager.IsActive;
            _status.Text = KeepAwakeCard.StatusText(_manager);
            // The explanation below already says what the option does; the caption only reports a state.
            var lidMode = _manager.State.LidMode;
            _lidCaption.Text = lidMode switch
            {
                LidModeStatus.Failed => L.Get("Strings.sudoersFailed"),
                LidModeStatus.Active => L.Get("Strings.clamshellOnCaption"),
                LidModeStatus.Ready => L.Get("Strings.clamshellNeedsSession"),
                _ => null,
            };
            _lidCaption.IsVisible = _lidCaption.Text is not null;
            _lidCaption.Bind(TextBlock.ForegroundProperty, _lidCaption.GetResourceObservable(lidMode is LidModeStatus.Failed or LidModeStatus.Active ? "WarningBrush" : "TextSecondaryBrush").ToBinding());
        }
        finally
        {
            _updating = false;
        }
    }
}
