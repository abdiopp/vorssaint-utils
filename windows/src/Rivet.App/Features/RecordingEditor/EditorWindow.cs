// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Shell;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The recording editor window (spec 02 §3.20): top band, stage and inspector,
/// timeline band. A normal, resizable taskbar window; closing it deletes the
/// take (after asking when nothing was saved or copied).
/// </summary>
public sealed class EditorWindow : Window
{
    private readonly EditorSession _session;
    private readonly InspectorView _inspector;
    private bool _closeConfirmed;
    private bool _closing;

    public EditorWindow(EditorSession session)
    {
        _session = session;
        Title = L.Get("recorder.editorTitle");
        MinWidth = 940;
        MinHeight = 560;
        Width = 1180;
        Height = 760;
        ShowInTaskbar = true;
        Styles.Add(new EditorStyles());
        this.Themed(BackgroundProperty, "EditorWindowBrush");

        var stage = new StageView(session) { Margin = new Thickness(18, 14) };
        _inspector = new InspectorView(session);
        var divider = new Border { Width = 1 }.Themed(Border.BackgroundProperty, "EditorHairlineBrush");
        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,272"), Children = { stage, divider, _inspector } };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(_inspector, 2);

        var top = new TopBand(session, this);
        var timeline = new TimelineView(session, AddImageAsync);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(timeline, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(timeline);
        root.Children.Add(middle);
        var sheen = new Border { Height = 160, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false }.Themed(Border.BackgroundProperty, "EditorWindowSheenBrush");
        Content = new Panel { Children = { sheen, root } };

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        session.CloseRequested += () =>
        {
            _closeConfirmed = true;
            Close();
        };
        Opened += (_, _) =>
        {
            ApplyNativeChrome();
            session.Playback.Start();
            session.StartMediaPreviews();
        };
        ActualThemeVariantChanged += (_, _) => ApplyNativeChrome();
    }

    public EditorSession Session => _session;

    /// <summary>Initial size from the screen under the pointer (spec 02 §3.20.1), centred on it.</summary>
    public void PlaceOnPointerScreen()
    {
        var screen = WindowInterop.ScreenAtCursor(this);
        if (screen is null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var w = area.Width / scale;
        var h = area.Height / scale;
        Width = Math.Round(Math.Min(Math.Max(1060, 0.70 * w), w - 60));
        Height = Math.Round(Math.Min(Math.Max(600, 0.68 * h), h - 80));
        Position = new Avalonia.PixelPoint(
            area.X + (int)((area.Width - (Width * scale)) / 2),
            area.Y + (int)((area.Height - (Height * scale)) / 2));
    }

    private void ApplyNativeChrome()
    {
        var handle = WindowInterop.Handle(this);
        if (handle != 0)
        {
            _session.Services.GetService<IWindowChrome>()?.SetDarkTitleBar(handle, ActualThemeVariant == ThemeVariant.Dark);
        }
    }

    // ── Keyboard (§3.33, Windows keys) ────────────────────────────────

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;

        // Save and Save as work even while typing in a caption.
        if (ctrl && e.Key == Key.S)
        {
            e.Handled = true;
            if (shift)
            {
                _ = SaveAsAsync();
            }
            else
            {
                _ = _session.ExportAsync(ExportKind.Save);
            }

            return;
        }

        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            if (e.Key == Key.Escape)
            {
                Focus();
                e.Handled = true;
            }

            return;
        }

        if (ctrl && !alt && (e.Key == Key.Y || (e.Key == Key.Z && shift)))
        {
            _session.Redo();
            e.Handled = true;
        }
        else if (ctrl && !alt && e.Key == Key.Z)
        {
            _session.Undo();
            e.Handled = true;
        }
        else if (ctrl && alt && e.Key == Key.C)
        {
            _ = _session.ExportAsync(ExportKind.CopyAndDelete);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C)
        {
            _ = _session.ExportAsync(ExportKind.Copy);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            _session.TogglePlay();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && e.KeyModifiers == KeyModifiers.None)
        {
            HandleDelete();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = HandleEscape();
        }
    }

    /// <summary>Cut the selection → remove the selected zoom, caption, image or blur → otherwise delete the recording.</summary>
    private void HandleDelete()
    {
        if (_session.CutSelection is not null)
        {
            _session.CutOut();
            return;
        }

        if (_session.SelectedKind is not null)
        {
            _session.RemoveSelected();
            return;
        }

        _ = DiscardAsync();
    }

    /// <summary>Cancel the export → end aiming → end drawing → deselect; otherwise pass through.</summary>
    private bool HandleEscape()
    {
        if (_session.IsExporting)
        {
            _session.CancelExport();
            return true;
        }

        if (_session.IsAiming || _session.IsDrawingBlur)
        {
            _session.EndModes();
            return true;
        }

        return _inspector.HandleEscape();
    }

    // ── Files ─────────────────────────────────────────────────────────

    private async Task AddImageAsync(double time)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = L.Get("recorder.addImageButton"),
            FileTypeFilter = [BackgroundPicker.ImageFileTypes],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await _session.AddImageAsync(path, time);
        }
    }

    public async Task SaveAsAsync()
    {
        if (_session.IsExporting)
        {
            return;
        }

        IStorageFolder? start = null;
        try
        {
            start = await StorageProvider.TryGetFolderFromPathAsync(_session.SaveFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("recording-editor", "The save folder is not accessible.", ex);
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = EditorSession.DefaultBaseName() + ".mp4",
            SuggestedStartLocation = start,
            DefaultExtension = "mp4",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(L.Get("win.recordingEditor.videoFilter")) { Patterns = ["*.mp4"], MimeTypes = ["video/mp4"] }],
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await _session.ExportAsync(ExportKind.SaveAs, path);
        }
    }

    public async Task ChooseSaveFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = L.Get("win.recordingEditor.saveTo"),
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _session.SetSaveFolder(path);
        }
    }

    // ── Discard and close (§3.36) ─────────────────────────────────────

    public async Task DiscardAsync()
    {
        _session.Playback.Pause();
        if (await ConfirmDeleteAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private Task<bool> ConfirmDeleteAsync() =>
        ConfirmDialog.ShowAsync(
            this,
            L.Get("recorder.discardTitle"),
            L.Get(_session.HasExported ? "recorder.discardSavedMessage" : "recorder.discardMessage"),
            L.Get("recorder.discardButton"),
            L.Get("recorder.cancelButton"),
            destructive: true);

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closing)
        {
            return;
        }

        _session.CancelExport();
        if (!_closeConfirmed && !_session.HasExported && !e.IsProgrammatic)
        {
            e.Cancel = true;
            _session.Playback.Pause();
            if (await ConfirmDeleteAsync())
            {
                _closeConfirmed = true;
                Close();
            }

            return;
        }

        _closing = true;
    }

    /// <summary>Closes without asking (feature removed, editor service shutting down).</summary>
    public void ForceClose()
    {
        _closeConfirmed = true;
        Close();
    }
}
