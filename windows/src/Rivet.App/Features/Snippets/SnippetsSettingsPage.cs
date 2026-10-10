// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Clipboard;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Snippets;

namespace Rivet.App.Features.Snippets;

/// <summary>
/// Settings → Text snippets (spec 06 §3.6.1, §3.6.5, §3.7): typed expansion,
/// the expansion sound, the quick snippet menu and the snippet list. Every
/// edit persists at once; the engine and the menu follow the setting.
/// </summary>
public sealed class SnippetsSettingsPage : SettingsPage
{
    private readonly ISnippetSounds _sounds;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly ComboBox _soundPicker = new() { MinWidth = 200 };
    private readonly Control _soundRow;
    private readonly Control _soundToggle;
    private bool _syncingSound;

    public SnippetsSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _sounds = services.GetRequiredService<ISnippetSounds>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        AutomationProperties.SetName(_soundPicker, L.Get("snippets.soundPickerLabel"));
        _soundPicker.SelectionChanged += (_, _) => OnSoundPicked();
        _soundRow = Row("MusicNote2", L.Get("snippets.soundPickerLabel"), null, _soundPicker);
        _soundToggle = Toggle(SnippetSettings.SoundEnabled, "Speaker2", "snippets.soundToggle", "snippets.soundCaption");
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RefreshVisibility), SnippetSettings.ExpansionEnabled, SnippetSettings.SoundEnabled));
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RebuildList), SnippetSettings.Snippets));
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RefreshSounds), SnippetSettings.SoundName));

        var add = ActionButton(L.Get("snippets.addButton"), () => _ = EditAsync(null), "Add", accent: true);
        Content = Stack(
            Header("snippets.pageTitle", "snippets.hubDescription"),
            Card(null,
                Toggle(SnippetSettings.ExpansionEnabled, "TextExpand", "snippets.enable", "snippets.enableCaption"),
                _soundToggle,
                _soundRow,
                Note(L.Get("win.snippets.limitsNote"))),
            Card("snippets.libraryTitle",
                Toggle(SnippetSettings.LibraryEnabled, "TextBulletListSquare", "snippets.libraryToggle", "snippets.libraryCaption"),
                shortcuts.Find(SnippetsModule.LibraryRoleId) is { } role ? new ShortcutRoleRow(role) : null),
            Card("snippets.pageTitle",
                _list,
                add,
                new StackPanel
                {
                    Spacing = 4,
                    Margin = new Thickness(0, 6, 0, 0),
                    Children =
                    {
                        Caption(L.Get("snippets.variablesHint")),
                        Caption(L.Get("snippets.variablesCaption")),
                        Caption(L.Get("snippets.variablesFormatCaption")),
                    },
                }));
        RefreshSounds();
        RefreshVisibility();
        RebuildList();
    }

    private void RefreshVisibility()
    {
        var expanding = Settings.Get(SnippetSettings.ExpansionEnabled);
        _soundToggle.IsVisible = expanding;
        _soundRow.IsVisible = expanding && Settings.Get(SnippetSettings.SoundEnabled);
    }

    /// <summary>The picker: the system sounds, plus "Sound unavailable" for a stored name that no longer exists.</summary>
    private void RefreshSounds()
    {
        _syncingSound = true;
        try
        {
            var available = _sounds.Available();
            var stored = Settings.Get(SnippetSettings.SoundName);
            var items = available.ToList();
            var index = items.FindIndex(n => string.Equals(n, stored, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                items.Insert(0, L.Get("snippets.soundUnavailable"));
                index = 0;
            }

            _soundPicker.ItemsSource = items;
            _soundPicker.Tag = index == 0 && items.Count != available.Count;
            _soundPicker.SelectedIndex = index;
        }
        finally
        {
            _syncingSound = false;
        }
    }

    private void OnSoundPicked()
    {
        if (_syncingSound || _soundPicker.SelectedItem is not string name)
        {
            return;
        }

        if (_soundPicker.Tag is true && _soundPicker.SelectedIndex == 0)
        {
            return; // The "Sound unavailable" row.
        }

        Settings.Set(SnippetSettings.SoundName, name);
        _sounds.Play(name);
    }

    private void RebuildList()
    {
        _list.Children.Clear();
        var snippets = Settings.Get(SnippetSettings.Snippets);
        if (snippets.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = L.Get("snippets.emptyList"), Classes = { "caption" }, Margin = new Thickness(0, 4) });
            return;
        }

        foreach (var snippet in snippets)
        {
            _list.Children.Add(SnippetRow(snippet));
        }
    }

    private Control SnippetRow(TextSnippet snippet)
    {
        var chip = new Border
        {
            Classes = { "pill" },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = snippet.Trigger, Classes = { "mono" }, FontSize = 11 },
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = snippet.DisplayName, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, chip } };
        if (snippet.Folder.Length > 0)
        {
            title.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { ClipboardUi.Icon("Folder", 11, "TextSecondaryBrush"), new TextBlock { Text = snippet.Folder, Classes = { "caption" }, FontSize = 11 } },
            });
        }

        var mode = L.Get(snippet.Expansion == SnippetExpansion.Immediate ? "snippets.expansionImmediate" : "snippets.expansionDelimiter");
        var details = new TextBlock
        {
            Text = snippet.PreviewLine + "  ·  " + mode,
            Classes = { "caption" },
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var enabled = new ToggleSwitch { Classes = { "compact" }, IsChecked = snippet.Enabled, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(enabled, snippet.DisplayName);
        enabled.IsCheckedChanged += (_, _) => Replace(snippet with { Enabled = enabled.IsChecked == true });

        var open = new Button
        {
            Classes = { "row" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 2, Children = { title, details } },
        };
        AutomationProperties.SetName(open, L.Get("snippets.editTitle") + ": " + snippet.DisplayName);
        open.Click += (_, _) => _ = EditAsync(snippet);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(open);
        Grid.SetColumn(enabled, 1);
        grid.Children.Add(enabled);
        return grid;
    }

    private void Replace(TextSnippet updated)
    {
        var list = Settings.Get(SnippetSettings.Snippets).ToList();
        var index = list.FindIndex(s => s.Id == updated.Id);
        if (index >= 0)
        {
            list[index] = updated;
        }
        else
        {
            list.Add(updated);
        }

        Settings.Set(SnippetSettings.Snippets, list);
    }

    private async Task EditAsync(TextSnippet? existing)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var all = Settings.Get(SnippetSettings.Snippets);
        var dialog = new SnippetEditorDialog(existing ?? new TextSnippet(), all, isNew: existing is null);
        switch (await dialog.ShowAsync(owner).ConfigureAwait(true))
        {
            case SnippetEditorResult.Saved:
                Replace(dialog.Edited);
                break;
            case SnippetEditorResult.Deleted when existing is not null:
                Settings.Set(SnippetSettings.Snippets, Settings.Get(SnippetSettings.Snippets).Where(s => s.Id != existing.Id).ToList());
                break;
        }
    }
}
