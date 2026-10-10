// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The top band (spec 02 §3.20.3): name, recent captures, undo/redo and
/// presets on the left; the finished-file chip or the export progress, then
/// Delete, Copy and delete, Copy (with Copy as GIF) and Save (with Save as…,
/// Save as GIF and Save to…) on the right.
/// </summary>
public sealed class TopBand : Border
{
    private readonly EditorSession _session;
    private readonly EditorWindow _window;
    private readonly Button _recent;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _presets;
    private readonly Border _finished;
    private readonly TextBlock _finishedName;
    private readonly Border _progress;
    private readonly ProgressBar _progressBar;
    private readonly Button _delete;
    private readonly Button _copyAndDelete;
    private readonly Button _copy;
    private readonly Button _copyMenu;
    private readonly Button _save;
    private readonly Button _saveMenu;
    private IStorageFile? _finishedItem;

    public TopBand(EditorSession session, EditorWindow window)
    {
        _session = session;
        _window = window;
        Height = 54;
        this.Themed(BackgroundProperty, "EditorBandBrush");
        this.Themed(BorderBrushProperty, "EditorHairlineBrush");
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(14, 0, 14, 0);

        var glyph = new AppGlyph { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        glyph.Themed(AppGlyph.ForegroundProperty, "AccentBrush");
        _recent = EditorUi.Button(null, "History", L.Get("recentCaptures.title"), OpenRecentCaptures);
        _recent.IsVisible = RecentCapturesAction() is not null;
        _undo = EditorUi.Button(null, "ArrowUndo", $"{L.Get("Strings.menuUndo")} (Ctrl+Z)", session.Undo);
        _redo = EditorUi.Button(null, "ArrowRedo", $"{L.Get("Strings.menuRedo")} (Ctrl+Y)", session.Redo);
        AutomationProperties.SetName(_undo, L.Get("Strings.menuUndo"));
        AutomationProperties.SetName(_redo, L.Get("Strings.menuRedo"));
        _presets = EditorUi.Button(L.Get("recorder.presetsButton"), "Options", null, ShowPresetsMenu);

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                glyph,
                EditorUi.Label(L.Get("recorder.editorTitle"), 13, FontWeight.SemiBold),
                new Border { Width = 4 },
                _recent,
                EditorUi.Divider(vertical: true),
                _undo,
                _redo,
                _presets,
            },
        };

        _finishedName = new TextBlock { FontSize = 11, MaxWidth = 190, VerticalAlignment = VerticalAlignment.Center };
        var check = EditorUi.Icon("CheckmarkCircle", 14);
        check.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        _finished = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { check, _finishedName } },
        }.Themed(BackgroundProperty, "EditorChipBrush");
        ToolTip.SetTip(_finished, L.Get("win.recordingEditor.showInFolder"));
        AutomationProperties.SetName(_finished, L.Get("win.recordingEditor.showInFolder"));
        _finished.PointerPressed += OnFinishedPressed;

        _progressBar = new ProgressBar { Width = 110, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        var cancel = new Button { Content = L.Get("recorder.cancelButton"), Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center };
        cancel.Click += (_, _) => session.CancelExport();
        _progress = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { _progressBar, new TextBlock { Text = L.Get("recorder.exportingLabel"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap }, cancel },
            },
        }.Themed(BackgroundProperty, "EditorChipBrush");

        _delete = EditorUi.Button(null, "Delete", L.Get("recorder.discardButton"), () => _ = window.DiscardAsync(), "danger");
        _copyAndDelete = EditorUi.Button(L.Get("recorder.copyAndDeleteButton"), null, "Ctrl+Alt+C", () => _ = session.ExportAsync(ExportKind.CopyAndDelete));
        _copy = EditorUi.Button(L.Get("recorder.copyButton"), "Copy", "Ctrl+C", () => _ = session.ExportAsync(ExportKind.Copy));
        _copy.CornerRadius = new CornerRadius(8, 0, 0, 8);
        _copyMenu = EditorUi.Button(null, "ChevronDown", L.Get("recorder.copyGIFButton"), ShowCopyMenu);
        _copyMenu.CornerRadius = new CornerRadius(0, 8, 8, 0);
        _copyMenu.Width = 26;
        _copyMenu.Margin = new Thickness(-1, 0, 0, 0);
        _save = EditorUi.Button(L.Get("recorder.saveVideoButton"), "Save", "Ctrl+S", () => _ = session.ExportAsync(ExportKind.Save), "primary");
        _save.CornerRadius = new CornerRadius(8, 0, 0, 8);
        _saveMenu = EditorUi.Button(null, "ChevronDown", L.Get("recorder.saveAsButton"), ShowSaveMenu, "primary");
        _saveMenu.CornerRadius = new CornerRadius(0, 8, 8, 0);
        _saveMenu.Width = 26;
        _saveMenu.Margin = new Thickness(1, 0, 0, 0);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _finished,
                _progress,
                _delete,
                _copyAndDelete,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _copy, _copyMenu } },
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _save, _saveMenu } },
            },
        };

        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(left);
        dock.Children.Add(right);
        Child = dock;
        session.Changed += OnChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _session.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(SessionChange change)
    {
        if ((change & (SessionChange.Document | SessionChange.Export | SessionChange.Presets)) != 0)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        var ready = _session.IsReady;
        var exporting = _session.IsExporting;
        _undo.IsEnabled = _session.History.CanUndo;
        _redo.IsEnabled = _session.History.CanRedo;
        _presets.IsEnabled = ready && !_session.IsPresetBusy;
        _progress.IsVisible = exporting;
        _progressBar.Value = _session.ExportProgress;
        _finished.IsVisible = !exporting && _session.FinishedFile is not null;
        if (_session.FinishedFile is { } file)
        {
            var name = Path.GetFileName(file);
            _finishedName.Text = MiddleTruncate(name, 30);
            _ = PrepareDragItemAsync(file);
        }

        foreach (var button in new[] { _copyAndDelete, _copy, _copyMenu, _saveMenu })
        {
            button.IsEnabled = ready && !exporting;
        }

        // Save stays available when the master cannot be decoded: it then keeps the raw file.
        _save.IsEnabled = !exporting && (ready || File.Exists(_session.Take.VideoPath));

        _delete.IsEnabled = true;
    }

    /// <summary>"Recording 2026-10-09 at 14.05.09.mp4" → "Recording 2026-10…14.05.09.mp4".</summary>
    public static string MiddleTruncate(string text, int max)
    {
        if (text.Length <= max || max < 5)
        {
            return text;
        }

        var keep = max - 1;
        var head = (keep + 1) / 2;
        return text[..head] + "…" + text[^(keep - head)..];
    }

    private async Task PrepareDragItemAsync(string path)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || _finishedItem?.TryGetLocalPath() == path)
        {
            return;
        }

        try
        {
            _finishedItem = await top.StorageProvider.TryGetFileFromPathAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _finishedItem = null;
        }
    }

    /// <summary>Click reveals the file; dragging hands the file itself to another app (e.g. a chat).</summary>
    private async void OnFinishedPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_session.FinishedFile is not { } path || !e.GetCurrentPoint(_finished).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var start = e.GetPosition(this);
        if (_finishedItem is { } item)
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(item));
            var effect = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
            if (effect != DragDropEffects.None)
            {
                return;
            }
        }

        _ = start;
        _session.Services.GetService<IShellService>()?.RevealInExplorer(path);
    }

    private AppAction? RecentCapturesAction()
    {
        var actions = _session.Services.GetService<ActionRegistry>();
        return actions?.Get(RecentCaptureActions.ShowPalette) is { } action && actions.IsEnabled(action) ? action : null;
    }

    private void OpenRecentCaptures()
    {
        if (RecentCapturesAction() is { } action)
        {
            _ = _session.Services.GetRequiredService<ActionRegistry>().InvokeAsync(action.Id, ActionSource.Other);
        }
    }

    private void ShowPresetsMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (look, key) in new[] { (RecorderLook.Original, "recorder.lookRaw"), (RecorderLook.Smooth, "recorder.lookClean"), (RecorderLook.Studio, "recorder.lookStudio") })
        {
            var item = new MenuItem { Header = L.Get(key) };
            item.Click += (_, _) => _session.ApplyLook(look);
            menu.Items.Add(item);
        }

        _session.ReloadSharedLists();
        if (_session.Presets.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var preset in _session.Presets)
            {
                var item = new MenuItem { Header = preset.Name };
                item.Click += (_, _) => _ = _session.ApplyPresetAsync(preset);
                menu.Items.Add(item);
            }

            var remove = new MenuItem { Header = L.Get("recorder.removePreset") };
            foreach (var preset in _session.Presets)
            {
                var item = new MenuItem { Header = preset.Name };
                item.Click += (_, _) => _session.RemovePreset(preset);
                remove.Items.Add(item);
            }

            menu.Items.Add(remove);
        }

        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = L.Get("recorder.savePreset") };
        save.Click += async (_, _) =>
        {
            var name = await PromptDialog.ShowAsync(_window, L.Get("recorder.savePreset"), L.Get("recorder.presetNamePlaceholder"),
                L.Get("recorder.saveVideoButton"), L.Get("recorder.cancelButton"));
            if (!string.IsNullOrWhiteSpace(name))
            {
                await _session.SavePresetAsync(name);
            }
        };
        menu.Items.Add(save);
        menu.ShowAt(_presets);
    }

    private void ShowCopyMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var gif = new MenuItem { Header = L.Get("recorder.copyGIFButton") };
        gif.Click += (_, _) => _ = _session.ExportAsync(ExportKind.CopyGif);
        menu.Items.Add(gif);
        menu.ShowAt(_copyMenu);
    }

    private void ShowSaveMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var save = new MenuItem { Header = L.Get("recorder.saveVideoButton"), InputGesture = new KeyGesture(Key.S, KeyModifiers.Control) };
        save.Click += (_, _) => _ = _session.ExportAsync(ExportKind.Save);
        var saveAs = new MenuItem { Header = L.Get("recorder.saveAsButton"), InputGesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift) };
        saveAs.Click += (_, _) => _ = _window.SaveAsAsync();
        var gif = new MenuItem { Header = L.Get("recorder.saveGIFButton") };
        gif.Click += (_, _) => _ = _session.ExportAsync(ExportKind.SaveGif);
        var folder = new MenuItem { Header = L.Get("win.recordingEditor.saveTo") };
        folder.Click += (_, _) => _ = _window.ChooseSaveFolderAsync();
        menu.Items.Add(save);
        menu.Items.Add(saveAs);
        menu.Items.Add(gif);
        menu.Items.Add(new Separator());
        menu.Items.Add(folder);
        menu.ShowAt(_saveMenu);
    }
}
