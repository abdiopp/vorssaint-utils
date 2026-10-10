// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Actions;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Capture.Settings;

/// <summary>Shared pieces of the capture tools' Settings pages.</summary>
internal abstract class CaptureSettingsPage : SettingsPage
{
    protected CaptureSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        Services = services;
    }

    protected IServiceProvider Services { get; }

    /// <summary>An enable switch in front of a shortcut role row (title, recorder, reset, status).</summary>
    protected Control? ShortcutRow(string roleId, Setting<bool> enabled)
    {
        var shortcuts = Services.GetRequiredService<ShortcutManager>();
        if (shortcuts.Find(roleId) is not { } role)
        {
            return null;
        }

        var property = Track(Settings.Bind(enabled));
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = property.Value, VerticalAlignment = VerticalAlignment.Center };
        toggle.IsCheckedChanged += (_, _) => property.Value = toggle.IsChecked == true;
        property.PropertyChanged += (_, _) => toggle.IsChecked = property.Value;
        AutomationProperties.SetName(toggle, L.Get(role.TitleKey));

        var row = new ShortcutRoleRow(role);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        grid.Children.Add(toggle);
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return grid;
    }

    /// <summary>A toggle row enabled only while <paramref name="gate"/> is on.</summary>
    protected SettingsRow GatedToggle(Setting<bool> setting, Setting<bool> gate, string? icon, string titleKey, string? descriptionKey = null)
    {
        var row = Toggle(setting, icon, titleKey, descriptionKey);
        row.IsEnabled = Settings.Get(gate);
        Track(Settings.Observe(gate.Key, () => Rivet.Core.Util.UiThread.Run(() => row.IsEnabled = Settings.Get(gate))));
        return row;
    }

    /// <summary>A row with action buttons that run capture actions (the action closes this window's focus itself).</summary>
    protected SettingsRow ActionsRow(string? icon, string title, string? description, params (string Text, string ActionId, string Icon, bool Accent)[] buttons)
    {
        var actions = Services.GetRequiredService<ActionRegistry>();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (text, actionId, buttonIcon, accent) in buttons)
        {
            panel.Children.Add(ActionButton(text, () => _ = actions.InvokeAsync(actionId, ActionSource.Other), buttonIcon, accent));
        }

        return Row(icon, title, description, panel);
    }

    /// <summary>Re-evaluates <paramref name="update"/> whenever one of the settings changes (UI thread).</summary>
    protected void When(Action update, params SettingDefinition[] settings)
    {
        update();
        Track(Settings.Observe(() => Rivet.Core.Util.UiThread.Run(update), settings));
    }

    protected static Control Indented(Control control)
    {
        control.Margin = new Thickness(40, 0, 0, 0);
        return control;
    }

    /// <summary>
    /// A card whose separators follow the rows' visibility, so a row hidden by
    /// another option leaves no empty band (the shared Card draws them statically).
    /// </summary>
    protected static SettingsCard LiveCard(string? titleKey, params Control?[] rows)
    {
        var stack = new StackPanel { Spacing = 2 };
        var items = new List<(Border? Separator, Control Row)>();
        foreach (var row in rows.OfType<Control>())
        {
            Border? separator = null;
            if (items.Count > 0)
            {
                separator = new Border { Classes = { "separator" }, Margin = new Thickness(40, 4, 0, 4) };
                stack.Children.Add(separator);
            }

            stack.Children.Add(row);
            items.Add((separator, row));
        }

        void Update()
        {
            var anyBefore = false;
            foreach (var (separator, row) in items)
            {
                if (separator is not null)
                {
                    separator.IsVisible = row.IsVisible && anyBefore;
                }

                anyBefore |= row.IsVisible;
            }
        }

        foreach (var (_, row) in items)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty)
                {
                    Update();
                }
            };
        }

        Update();
        return new SettingsCard { Header = titleKey is null ? null : L.Get(titleKey), Content = stack };
    }
}
