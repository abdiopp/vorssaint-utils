// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Awake;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.Awake;

/// <summary>
/// Keep Awake: sessions, automation, battery protection, pointer jiggle,
/// pause while locked and closed-lid mode; the panel card, the tray tint and
/// tooltip, tray menu entries, the global shortcut and Settings.
/// </summary>
public sealed class KeepAwakeModule : IFeatureModule
{
    public const string SectionId = "keepAwake";
    public const string SettingsPageId = "keepAwake";
    public const string ToggleActionId = "keepAwake.toggle";
    public const string ShortcutRoleId = "keepAwake";

    public string Id => "keepAwake";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new KeepAwakeManager(
            sp.GetRequiredService<ISettingsStore>(),
            new SystemKeepAwakeTimers(),
            sp.GetRequiredService<IPowerRequests>(),
            sp.GetRequiredService<IPowerSource>(),
            sp.GetRequiredService<ISessionLockMonitor>(),
            sp.GetRequiredService<IDisplayTopology>(),
            sp.GetRequiredService<IRunningApps>(),
            sp.GetRequiredService<IPointerJiggler>(),
            sp.GetRequiredService<ILidActionController>(),
            sp.GetService<INotificationService>()));
        services.AddSingleton(sp => new KeepAwakePresenter(
            sp.GetRequiredService<KeepAwakeManager>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetService<ITrayPresence>()));
        services.AddSingleton<IReadoutCountdownSource>(sp => sp.GetRequiredService<KeepAwakePresenter>());
    }

    public void Initialize(ModuleContext context)
    {
        var manager = context.Get<KeepAwakeManager>();
        var presenter = context.Get<KeepAwakePresenter>();
        context.Features.RegisterController(FeatureIds.KeepAwake, new DelegateFeatureController(available =>
        {
            manager.Sync(available);
            presenter.Sync(available);
        }));

        var actions = context.Actions;
        actions.Register(new AppAction
        {
            Id = ToggleActionId,
            FeatureId = FeatureIds.KeepAwake,
            TitleKey = "Strings.keepAwakeTitle",
            Icon = "WeatherMoon",
            ClosesPanel = false,
            Keywords = ["caffeine", "awake", "sleep"],
            IsOn = () => manager.IsActive,
            Run = _ =>
            {
                manager.Toggle();
                return Task.CompletedTask;
            },
        });
        foreach (var minutes in KeepAwakeSettings.Durations)
        {
            var preset = minutes;
            actions.Register(new AppAction
            {
                Id = $"keepAwake.start.{minutes}",
                FeatureId = FeatureIds.KeepAwake,
                TitleKey = minutes switch
                {
                    15 => "Strings.minutes15",
                    30 => "Strings.minutes30",
                    60 => "Strings.hour1",
                    120 => "Strings.hours2",
                    240 => "Strings.hours4",
                    480 => "Strings.hours8",
                    _ => "Strings.indefinitely",
                },
                SubtitleKey = "Strings.keepAwakeTitle",
                Icon = "Timer",
                ClosesPanel = false,
                Keywords = ["keep awake", "caffeine"],
                Run = _ =>
                {
                    manager.Activate(preset);
                    return Task.CompletedTask;
                },
            });
        }

        context.Shortcuts.Register(new ShortcutRole
        {
            Id = ShortcutRoleId,
            FeatureId = FeatureIds.KeepAwake,
            TitleKey = "Strings.keepAwakeTitle",
            Storage = KeepAwakeSettings.Shortcut,
            Default = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Letter('K')),
            RequiredEnableKeys = [KeepAwakeSettings.HotkeyEnabled],
            ActionId = ToggleActionId,
        });

        // Tray menu (spec 05 §3.3.5): enable/disable, then "Activate for…" presets while idle.
        context.TrayMenu.Add(new TrayMenuItem
        {
            Id = "keepAwake.toggle",
            FeatureId = FeatureIds.KeepAwake,
            Order = -40,
            Icon = "WeatherMoon",
            Title = () => L.Get(manager.IsActive ? "Strings.menuDisableAwake" : "Strings.menuEnableAwake"),
            Invoke = manager.Toggle,
        });
        var order = -39;
        foreach (var minutes in KeepAwakeSettings.Durations)
        {
            var preset = minutes;
            context.TrayMenu.Add(new TrayMenuItem
            {
                Id = $"keepAwake.activate.{minutes}",
                FeatureId = FeatureIds.KeepAwake,
                Order = order++,
                Icon = minutes == 0 ? "ArrowRepeatAll" : "Timer",
                IsVisible = () => !manager.IsActive,
                Title = () => preset == 0
                    ? L.Get("win.keepAwake.trayActivateIndefinitely")
                    : L.Format("win.keepAwake.trayActivateForFormat", KeepAwakeFormat.DurationTitle(preset)),
                Invoke = () => manager.Activate(preset),
            });
        }

        context.Panel.AddSection(new PanelSectionDescriptor
        {
            Id = SectionId,
            TitleKey = "Strings.keepAwakeTitle",
            Icon = "WeatherMoon",
            FeatureIds = [FeatureIds.KeepAwake],
            Order = 4,
            CreateView = sp => new KeepAwakeCard(sp),
            SettingsPageId = SettingsPageId,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageId,
            TitleKey = "Strings.keepAwakeTitle",
            Icon = "WeatherMoon",
            Category = SettingsCategory.EnergyDisplay,
            Order = 0,
            FeatureIds = [FeatureIds.KeepAwake],
            CreateView = sp => new KeepAwakeSettingsPage(sp),
            KeywordKeys =
            [
                "keepAwakeAutomation.automationSection", "Strings.defaultDurationLabel", "Strings.clamshellTitle",
                "Strings.keepAwakeMouseJiggle", "Strings.batteryDisableBelow", "Strings.hotkeyToggle",
            ],
            Keywords = ["caffeine", "sleep", "lid", "jiggle", "power"],
        });
    }
}
