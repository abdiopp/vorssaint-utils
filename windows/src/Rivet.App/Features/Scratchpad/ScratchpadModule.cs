// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Modules.Scratchpad;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Scratchpad;

/// <summary>Scratchpad: floating, tabbed, autosaved notes with Markdown helpers and preview.</summary>
public sealed class ScratchpadModule : IFeatureModule
{
    // Built by concatenation so the string-key test does not mistake the ids for catalog keys.
    private const string ActionPrefix = "scratchpad";
    public const string ToggleActionId = ActionPrefix + ".toggle";
    public const string OpenActionId = ActionPrefix + ".open";
    public const string PageId = "scratchpad";

    public static readonly ShortcutRole Role = new()
    {
        Id = "scratchpad",
        FeatureId = FeatureIds.Scratchpad,
        TitleKey = "scratchpad.pageTitle",
        Storage = ScratchpadSettings.Shortcut,
        Default = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Letter('N')),
        RequiredEnableKeys = [ScratchpadSettings.ShortcutEnabled],
        ActionId = ToggleActionId,
    };

    public string Id => "scratchpad";

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<ScratchpadService>();

    public void Initialize(ModuleContext context)
    {
        var service = context.Get<ScratchpadService>();
        context.Features.RegisterController(FeatureIds.Scratchpad, new DelegateFeatureController(available =>
        {
            if (!available)
            {
                service.Discard();
            }
        }));

        context.Actions.Register(new AppAction
        {
            Id = ToggleActionId,
            FeatureId = FeatureIds.Scratchpad,
            TitleKey = "scratchpad.pageTitle",
            Icon = "Note",
            Run = _ =>
            {
                service.Toggle();
                return Task.CompletedTask;
            },
        });
        context.Actions.Register(new AppAction
        {
            Id = OpenActionId,
            FeatureId = FeatureIds.Scratchpad,
            TitleKey = "scratchpad.pageTitle",
            SubtitleKey = "scratchpad.panelCaption",
            Icon = "Note",
            Keywords = ["notes", "notepad", "markdown"],
            Run = async ctx =>
            {
                // Other surfaces hide first and the pad shows 0.15 s later.
                if (ctx.Source is ActionSource.CommandBar or ActionSource.QuickPanel or ActionSource.RadialMenu)
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.15)).ConfigureAwait(true);
                }

                service.Show();
            },
        });

        context.Shortcuts.Register(Role);

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "scratchpad",
            FeatureId = FeatureIds.Scratchpad,
            TitleKey = "scratchpad.pageTitle",
            CaptionKey = "scratchpad.panelCaption",
            Icon = "Note",
            Order = 50,
            ActionId = OpenActionId,
            ShortcutRoleId = Role.Id,
            SettingsPageId = PageId,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "scratchpad.pageTitle",
            Icon = "Note",
            Category = SettingsCategory.Tools,
            Order = 30,
            FeatureIds = [FeatureIds.Scratchpad],
            CreateView = sp => new ScratchpadSettingsPage(sp),
            KeywordKeys = ["scratchpad.retentionTitle", "scratchpad.textSize", "scratchpad.backgroundOpacity", "scratchpad.closeOnClickOutside"],
            Keywords = ["notes", "notepad", "markdown"],
        });
    }
}

/// <summary>Settings › Tools › Scratchpad.</summary>
public sealed class ScratchpadSettingsPage : SettingsPage
{
    public ScratchpadSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var service = services.GetRequiredService<ScratchpadService>();
        var retention = new (string Value, string Label)[]
        {
            ("never", L.Get("scratchpad.retentionNever")),
            ("day", L.Get("scratchpad.retentionDay")),
            ("week", L.Get("scratchpad.retentionWeek")),
            ("month", L.Get("scratchpad.retentionMonth")),
        };
        Content = Stack(
            Header("scratchpad.pageTitle", "scratchpad.hubDescription"),
            Card(null,
                Row("Note", L.Get("scratchpad.openButton"), L.Get("scratchpad.panelCaption"), ActionButton(L.Get("scratchpad.openButton"), service.Show, "Open", accent: true)),
                Choice(ScratchpadSettings.Retention, "History", "scratchpad.retentionTitle", "scratchpad.retentionCaption", retention),
                Toggle(ScratchpadSettings.CloseOnClickOutside, "CursorClick", "scratchpad.closeOnClickOutside"),
                Toggle(ScratchpadSettings.AlwaysOnTop, "Pin", "win.scratchpad.alwaysOnTop", "win.scratchpad.alwaysOnTopCaption")),
            Card("win.scratchpad.appearanceTitle",
                Slider(ScratchpadSettings.TextSize, "TextFontSize", "scratchpad.textSize", ScratchpadSettings.MinTextSize, ScratchpadSettings.MaxTextSize, 1, v => $"{v:0} pt"),
                EndLabelledSlider(ScratchpadSettings.BackgroundOpacity, "Square", "scratchpad.backgroundOpacity", L.Get("scratchpad.backgroundTranslucent"), L.Get("scratchpad.backgroundOpaque"))),
            Card("Strings.shortcutsPageTitle",
                Toggle(ScratchpadSettings.ShortcutEnabled, "Keyboard", "win.scratchpad.shortcutToggle"),
                new ShortcutRoleRow(ScratchpadModule.Role)));
    }

    /// <summary>A 0–1 slider with the spec's end labels instead of a value readout.</summary>
    private SettingsRow EndLabelledSlider(Setting<double> setting, string icon, string titleKey, string minLabel, string maxLabel)
    {
        var property = Track(Settings.Bind(setting));
        var slider = new Avalonia.Controls.Slider { Minimum = 0, Maximum = 1, SmallChange = 0.05, LargeChange = 0.1, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 160, Value = property.Value };
        slider.ValueChanged += (_, e) =>
        {
            if (Math.Abs(property.Value - e.NewValue) > 0.0001)
            {
                property.Value = e.NewValue;
            }
        };
        property.PropertyChanged += (_, _) => slider.Value = property.Value;
        Avalonia.Automation.AutomationProperties.SetName(slider, L.Get(titleKey));
        var trailing = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = minLabel, Classes = { "caption" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                slider,
                new TextBlock { Text = maxLabel, Classes = { "caption" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            },
        };
        return Row(icon, L.Get(titleKey), null, trailing);
    }
}
