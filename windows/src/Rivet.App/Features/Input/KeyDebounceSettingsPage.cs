// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Input;

/// <summary>Settings › Debounce: the global window and per-key overrides (stored by scan code).</summary>
public sealed class KeyDebounceSettingsPage : InputSettingsPage
{
    private readonly IKeyboardInfo? _keyboard;
    private readonly IKeyNameProvider? _names;
    private readonly StackPanel _overrideRows = new() { Spacing = 2 };

    public KeyDebounceSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _keyboard = services.GetService<IKeyboardInfo>();
        _names = services.GetService<IKeyNameProvider>();
        var service = services.GetService<KeyDebounceService>();

        var active = StatusText(L.Get("Strings.keyDebounceActiveNow"));
        void UpdateActive() => active.IsVisible = service?.IsRunning == true;
        UpdateActive();
        if (service is not null)
        {
            EventHandler handler = (_, _) => Dispatcher.UIThread.Post(UpdateActive);
            service.StatusChanged += handler;
            Track(new Unsubscribe(() => service.StatusChanged -= handler));
        }

        // Key picker + window + "Add key".
        // Letters, then digits, then named keys and punctuation, as on the keyboard's legends.
        var keys = DebounceKeyCatalog.Keys
            .Select(k => (k.ScanId, Label: KeyLabel(k.ScanId), Group: k.UsVirtualKey is >= VirtualKeys.A and <= VirtualKeys.Z ? 0 : k.UsVirtualKey is >= VirtualKeys.D0 and <= VirtualKeys.D9 ? 1 : 2))
            .OrderBy(k => k.Group).ThenBy(k => k.Group == 2 ? string.Empty : k.Label, StringComparer.CurrentCulture)
            .Select(k => (k.ScanId, k.Label))
            .ToList();
        var picker = new ComboBox { MinWidth = 120, ItemsSource = keys.Select(k => k.Label).ToList(), SelectedIndex = 0 };
        AutomationProperties.SetName(picker, L.Get("Strings.keyDebounceKeyLabel"));
        var window = new NumericUpDown { Minimum = 0, Maximum = 500, Increment = 1, Value = KeyDebounceOverrides.DefaultMs, FormatString = "0", Width = 110, ClipValueToMinMax = true };
        AutomationProperties.SetName(window, L.Get("Strings.keyDebounceWindowLabel"));
        var addKey = ActionButton(L.Get("Strings.keyDebounceAddKey"), () =>
        {
            if (picker.SelectedIndex < 0)
            {
                return;
            }

            var overrides = KeyDebounceOverrides.Decode(Settings.Get(InputSettings.KeyDebounceKeyWindows));
            // Adding an existing key overwrites it.
            overrides[keys[picker.SelectedIndex].ScanId] = (int)(window.Value ?? KeyDebounceOverrides.DefaultMs);
            Settings.Set(InputSettings.KeyDebounceKeyWindows, KeyDebounceOverrides.Encode(overrides));
        }, "Add");
        var addRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(40, 4, 0, 4),
            Children =
            {
                new TextBlock { Text = L.Get("Strings.keyDebounceKeyLabel"), VerticalAlignment = VerticalAlignment.Center },
                picker,
                new TextBlock { Text = L.Get("Strings.keyDebounceWindowLabel"), VerticalAlignment = VerticalAlignment.Center },
                window,
                new TextBlock { Text = "ms", VerticalAlignment = VerticalAlignment.Center },
                addKey,
            },
        };

        Content = Stack(
            Header("Strings.keyDebounceName", "hub.descKeyboardDebounce"),
            LiveCard(null,
                Toggle(FeatureKeys.KeyboardDebounceEnabled, "Keyboard", "Strings.keyDebounceEnable", "Strings.keyDebounceCaption"),
                active,
                Stepper(InputSettings.KeyDebounceWindowMs, "Timer", "Strings.keyDebounceGlobalWindow", null, 0, 500, 1),
                IndentedNote(L.Get("win.input.debounceRepeatNote"))),
            Card("Strings.keyDebouncePerKeySection",
                IndentedNote(L.Get("Strings.keyDebouncePerKeyCaption")),
                addRow,
                _overrideRows));

        Track(Settings.Observe(InputSettings.KeyDebounceKeyWindows.Key, () => Dispatcher.UIThread.Post(RebuildOverrides)));
        RebuildOverrides();
    }

    private string KeyLabel(int scanId)
    {
        var vk = _keyboard?.ScanToVirtualKey(scanId) ?? 0;
        if (vk == 0)
        {
            vk = DebounceKeyCatalog.UsVirtualKey(scanId) ?? 0;
        }

        return vk == 0 ? "#" + KeyDebounceOverrides.EncodeScanId(scanId) : _names?.NameOf(vk) ?? VirtualKeys.DefaultName(vk);
    }

    private void RebuildOverrides()
    {
        _overrideRows.Children.Clear();
        var overrides = KeyDebounceOverrides.Decode(Settings.Get(InputSettings.KeyDebounceKeyWindows));
        if (overrides.Count == 0)
        {
            _overrideRows.Children.Add(new TextBlock { Text = L.Get("Strings.keyDebounceNoOverrides"), Classes = { "caption", "tertiary" }, Margin = new Thickness(40, 0, 0, 0) });
            return;
        }

        foreach (var (scanId, ms) in overrides.Select(kv => (kv.Key, kv.Value)).OrderBy(kv => KeyLabel(kv.Key), StringComparer.CurrentCulture))
        {
            var box = new NumericUpDown { Minimum = 0, Maximum = 500, Increment = 1, Value = ms, FormatString = "0", Width = 110, ClipValueToMinMax = true };
            AutomationProperties.SetName(box, KeyLabel(scanId));
            box.ValueChanged += (_, e) =>
            {
                if (e.NewValue is { } value)
                {
                    var map = KeyDebounceOverrides.Decode(Settings.Get(InputSettings.KeyDebounceKeyWindows));
                    map[scanId] = (int)value;
                    Settings.Set(InputSettings.KeyDebounceKeyWindows, KeyDebounceOverrides.Encode(map));
                }
            };
            var remove = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Delete, FontSize = 15 } };
            ToolTip.SetTip(remove, L.Get("Strings.keyDebounceRemoveKey"));
            AutomationProperties.SetName(remove, L.Get("Strings.keyDebounceRemoveKey"));
            remove.Click += (_, _) =>
            {
                var map = KeyDebounceOverrides.Decode(Settings.Get(InputSettings.KeyDebounceKeyWindows));
                map.Remove(scanId);
                Settings.Set(InputSettings.KeyDebounceKeyWindows, KeyDebounceOverrides.Encode(map));
            };

            _overrideRows.Children.Add(Row("Keyboard", KeyLabel(scanId), null,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { box, new TextBlock { Text = "ms", VerticalAlignment = VerticalAlignment.Center }, remove } }));
        }
    }
}
