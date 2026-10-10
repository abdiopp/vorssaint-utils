// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Clipboard;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Clipboard;

/// <summary>Settings → Clipboard: history, apps to skip, auto clear and paste as plain text.</summary>
public sealed class ClipboardSettingsPage : SettingsPage
{
    private readonly IForegroundService _foreground;
    private readonly StackPanel _ignored = new() { Spacing = 4 };

    public ClipboardSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _foreground = services.GetRequiredService<IForegroundService>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        var limitOptions = ClipboardSettings.LimitChoices
            .Select(v => (v, v == 0 ? L.Get("clipboard.limitUnlimited") : v.ToString("N0", CultureInfo.CurrentCulture)))
            .ToList();

        Track(Settings.Observe(() => Avalonia.Threading.Dispatcher.UIThread.Post(RebuildIgnored), ClipboardSettings.IgnoredApps));

        Content = Stack(
            Header("clipboard.title", "clipboard.caption"),
            Card("clipboard.title",
                Toggle(ClipboardSettings.Enabled, "ClipboardPaste", "clipboard.enable", "clipboard.caption"),
                Choice(ClipboardSettings.Limit, "TextNumberList", "clipboard.limit", null, limitOptions),
                Toggle(ClipboardSettings.IncludeImagesFiles, "ImageMultiple", "clipboard.includeImagesFiles", "clipboard.includeImagesFilesCaption"),
                Toggle(ClipboardSettings.SkipSensitive, "ShieldLock", "clipboard.skipSensitive", "clipboard.skipSensitiveCaption"),
                Toggle(ClipboardSettings.ShortcutEnabled, "Keyboard", "clipboard.shortcut", "clipboard.shortcutCaption"),
                shortcuts.Find("clipboard") is { } role ? new ShortcutRoleRow(role) : null,
                Note(L.Get("clipboard.localNote"))),
            Card("clipboardIgnoredApps.listTitle",
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Caption(L.Get("clipboardIgnoredApps.caption")),
                        _ignored,
                        ActionButton(L.Get("clipboardIgnoredApps.addButton"), () => _ = AddAppAsync(), "Add"),
                    },
                }),
            Card("win.clipboard.autoClearTitle",
                DelayRow(),
                Toggle(ClipboardSettings.AutoClearOnSleep, "WeatherMoon", "clipboard.autoClearOnSleep"),
                Toggle(ClipboardSettings.AutoClearOnDisplaySleep, "DesktopOff", "clipboard.autoClearOnDisplaySleep"),
                Toggle(ClipboardSettings.AutoClearOnScreenLock, "LockClosed", "clipboard.autoClearOnScreenLock"),
                Note(L.Get("clipboard.autoClearCaption"))),
            Card("Strings.pastePlainName",
                Toggle(ClipboardSettings.PastePlainEnabled, "DocumentText", "Strings.pastePlainName", "Strings.pastePlainCaption"),
                shortcuts.Find("pastePlain") is { } plain ? new ShortcutRoleRow(plain) : null));
        RebuildIgnored();
    }

    private Control DelayRow()
    {
        var enabled = Track(Settings.Bind(ClipboardSettings.AutoClearOnDelay));
        var delay = Track(Settings.Bind(ClipboardSettings.AutoClearDelaySeconds));
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = enabled.Value };
        toggle.IsCheckedChanged += (_, _) => enabled.Value = toggle.IsChecked == true;
        enabled.PropertyChanged += (_, _) => toggle.IsChecked = enabled.Value;
        var seconds = new NumericUpDown { Minimum = 5, Maximum = 3600, Increment = 5, Value = delay.Value, Width = 130, FormatString = "0" };
        AutomationProperties.SetName(seconds, L.Get("clipboard.autoClearSecondsSuffix"));
        seconds.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                delay.Value = (int)value;
            }
        };
        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                seconds,
                new TextBlock { Text = L.Get("clipboard.autoClearSecondsSuffix"), VerticalAlignment = VerticalAlignment.Center },
                toggle,
            },
        };
        AutomationProperties.SetName(toggle, L.Get("clipboard.autoClearEnable"));
        return Row("Timer", L.Get("clipboard.autoClearEnable"), null, trailing);
    }

    private void RebuildIgnored()
    {
        _ignored.Children.Clear();
        foreach (var app in Settings.Get(ClipboardSettings.IgnoredApps))
        {
            var remove = new Button { Content = L.Get("clipboardIgnoredApps.removeButton") };
            var captured = app;
            remove.Click += (_, _) => Settings.Set(ClipboardSettings.IgnoredApps, Settings.Get(ClipboardSettings.IgnoredApps).Where(a => !string.Equals(a, captured, StringComparison.OrdinalIgnoreCase)).ToList());
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = ClipboardUi.SourceName(app), FontSize = 13 },
                    new TextBlock { Text = app, Classes = { "caption", "tertiary" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _ignored.Children.Add(row);
        }
    }

    private void AddIgnored(string identity)
    {
        var list = Settings.Get(ClipboardSettings.IgnoredApps).ToList();
        if (!list.Contains(identity, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(identity);
            Settings.Set(ClipboardSettings.IgnoredApps, list);
        }
    }

    /// <summary>"Add an app…": a running app, or any executable picked from disk.</summary>
    private async Task AddAppAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new Window
        {
            Title = L.Get("clipboardIgnoredApps.addButton"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        var list = new StackPanel { Spacing = 2 };
        foreach (var app in _foreground.RunningApps())
        {
            var button = new Button { Classes = { "row" }, Content = new StackPanel { Children = { new TextBlock { Text = app.Name }, new TextBlock { Text = app.ExecutablePath ?? app.Identity, Classes = { "caption", "tertiary" }, FontSize = 11 } } } };
            button.Click += (_, _) =>
            {
                AddIgnored(app.Identity);
                dialog.Close();
            };
            list.Children.Add(button);
        }

        var browse = new Button { Content = L.Get("win.clipboard.browseApp") };
        browse.Click += async (_, _) =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("exe") { Patterns = ["*.exe"] }],
            }).ConfigureAwait(true);
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                AddIgnored(path.Replace('/', '\\').ToLowerInvariant());
                dialog.Close();
            }
        };
        var cancel = new Button { Content = L.Get("clipboard.cancel"), IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = L.Get("clipboardIgnoredApps.caption"), Classes = { "caption" } },
                new ScrollViewer { MaxHeight = 360, Content = list },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { browse, cancel } },
            },
        };
        if (owner is not null)
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            dialog.Show();
        }
    }
}
