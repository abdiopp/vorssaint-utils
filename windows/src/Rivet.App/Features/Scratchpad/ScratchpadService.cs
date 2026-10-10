// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Shell;
using Rivet.Core.App;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Modules.Scratchpad;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Scratchpad;

/// <summary>
/// The scratchpad's behaviour (spec 07 §3.3): smart toggle, show and hide,
/// outside-click dismissal through the shared mouse hook (only while shown
/// and unpinned), autosave 0.8 s after typing plus immediate saves for every
/// tab action, mark, clear, export, hide and quit, the write-first rule, and
/// exports to .txt or .md. Notes live in <c>Scratchpad.json</c> in the
/// roaming data folder.
/// </summary>
public sealed class ScratchpadService : IDisposable
{
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromSeconds(0.8);

    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly IHud _hud;
    private readonly IInputHooks _hooks;
    private readonly IScreenService _screens;
    private readonly IScratchpadPlatform? _platform;
    private readonly List<IDisposable> _observers = [];
    private ScratchpadWindow? _window;
    private DispatcherTimer? _autosave;
    private IDisposable? _outsideClick;
    private int _dialogs;

    public ScratchpadService(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _hud = services.GetRequiredService<IHud>();
        _hooks = services.GetRequiredService<IInputHooks>();
        _screens = services.GetRequiredService<IScreenService>();
        _platform = services.GetService<IScratchpadPlatform>();
        var folder = services.GetRequiredService<AppPaths>().RoamingRoot;
        Store = new ScratchpadStore(folder, () => L.Get("scratchpad.pageTitle"), () => ScratchpadSettings.ParseRetention(_settings.Get(ScratchpadSettings.Retention)));
        Store.Changed += (_, _) => Render();
        _observers.Add(_settings.Observe(() => Dispatcher.UIThread.Post(OnSettingsChanged),
            ScratchpadSettings.TextSize, ScratchpadSettings.BackgroundOpacity, ScratchpadSettings.CloseOnClickOutside, ScratchpadSettings.AlwaysOnTop));
    }

    public ScratchpadStore Store { get; }

    public ScratchpadDocument? Document => Store.Document;

    public ScratchpadWindow? Window => _window;

    public bool IsVisible => _window?.IsVisible == true;

    public bool DialogOpen => _dialogs > 0;

    /// <summary>The hotkey: hidden → show; shown but not focused → focus; focused → hide. Nothing while a dialog is up.</summary>
    public void Toggle()
    {
        if (DialogOpen)
        {
            return;
        }

        if (_window is { IsVisible: true } window)
        {
            if (window.IsActive)
            {
                Hide();
            }
            else
            {
                Focus();
            }

            return;
        }

        Show();
    }

    public void Show()
    {
        if (DialogOpen || !_services.GetRequiredService<FeatureRuntime>().IsAvailable(FeatureIds.Scratchpad))
        {
            return;
        }

        if (_window is { IsVisible: true })
        {
            Focus();
            return;
        }

        if (!Store.Load())
        {
            _hud.Show(L.Get("scratchpad.loadFailed"), HudStyle.Error, "Note");
            return;
        }

        var created = _window is null;
        _window ??= CreateWindow();
        _window.ResetForShow(pinned: !_settings.Get(ScratchpadSettings.CloseOnClickOutside));
        _window.Topmost = _settings.Get(ScratchpadSettings.AlwaysOnTop);
        Render(forceText: true);
        var screen = _screens.ScreenFromPoint(_screens.CursorPosition);
        if (created || !IsOnAnyWorkArea(_window))
        {
            _window.PlaceInitially(screen);
        }

        _services.GetService<IFocusHandoff>()?.Remember();
        _window.Opacity = 0;
        _window.Show();
        if (created)
        {
            // The position needs the window's real scaling once it is shown.
            _window.PlaceInitially(screen);
        }

        Focus();
        FadeIn(_window);
        UpdateOutsideClick();
    }

    public void Hide()
    {
        if (_window is not { IsVisible: true } window)
        {
            return;
        }

        FlushNow(showFailure: true);
        _outsideClick?.Dispose();
        _outsideClick = null;
        window.CloseFind();
        window.Hide();
        _services.GetService<IFocusHandoff>()?.Restore();
    }

    public void TogglePin()
    {
        if (_window is null)
        {
            return;
        }

        _window.SetPinned(!_window.Pinned);
        UpdateOutsideClick();
    }

    public void OnTextEdited(Guid padId, string text)
    {
        Store.UpdateText(padId, text);
        _autosave ??= CreateAutosaveTimer();
        _autosave.Stop();
        _autosave.Start();
    }

    public void SelectTab(Guid padId)
    {
        if (Store.Document is not { } document || document.SelectedId == padId)
        {
            return;
        }

        FlushNow(showFailure: false);
        if (Store.Commit(Store.Document!.WithSelected(padId)))
        {
            _window?.FocusEditor();
        }
    }

    public Task NewTabAsync()
    {
        if (Store.Document?.WithNewPad(L.Get("scratchpad.pageTitle")) is { } next)
        {
            FlushNow(showFailure: false);
            if (Store.Commit(next))
            {
                _window?.FocusEditor();
            }
        }

        return Task.CompletedTask;
    }

    public async Task RenameTabAsync(Guid? padId)
    {
        if (Store.Document is not { } document || DialogOpen)
        {
            return;
        }

        var pad = document.Find(padId ?? document.SelectedId);
        if (pad is null)
        {
            return;
        }

        _dialogs++;
        string? name;
        try
        {
            name = await TextPromptDialog.ShowAsync(_window, L.Get("scratchpad.renamePad"), pad.Name, L.Get("scratchpad.saveName"), L.Get("scratchpad.cancel"), maxLength: 200);
        }
        finally
        {
            _dialogs--;
        }

        if (name is not null && Store.Document is { } current)
        {
            FlushNow(showFailure: false);
            Store.Commit(current.WithName(pad.Id, name));
        }

        _window?.FocusEditor();
    }

    /// <summary>Closes a tab (confirming when it has text). The last tab cannot close; Ctrl+W then hides the pad.</summary>
    public async Task CloseTabAsync(Guid? padId, bool hideWhenLast = false)
    {
        if (Store.Document is not { } document || DialogOpen)
        {
            return;
        }

        var pad = document.Find(padId ?? document.SelectedId);
        if (pad is null)
        {
            return;
        }

        if (document.Pads.Count <= 1)
        {
            if (hideWhenLast)
            {
                Hide();
            }

            return;
        }

        FlushNow(showFailure: false);
        pad = Store.Document!.Find(pad.Id)!;
        if (!pad.IsEmpty)
        {
            _dialogs++;
            bool confirmed;
            try
            {
                confirmed = await ConfirmDialog.ShowAsync(_window, L.Get("scratchpad.closePad"), L.Format("scratchpad.deletePadMessageFormat", pad.Name), L.Get("scratchpad.closePad"), L.Get("scratchpad.cancel"), destructive: true);
            }
            finally
            {
                _dialogs--;
            }

            if (!confirmed)
            {
                return;
            }
        }

        if (Store.Document?.WithoutPad(pad.Id) is { } next)
        {
            Store.Commit(next);
        }

        _window?.FocusEditor();
    }

    public void ApplyMark(MarkdownMark mark)
    {
        if (_window is null || _window.PreviewOn)
        {
            return;
        }

        var editor = _window.Editor;
        var start = Math.Min(editor.SelectionStart, editor.SelectionEnd);
        var length = Math.Abs(editor.SelectionEnd - editor.SelectionStart);
        var edit = MarkdownMarks.Apply(mark, editor.Text ?? string.Empty, start, length);
        _window.ApplyEdit(edit);
        FlushNow(showFailure: false);
    }

    /// <summary>No confirmation: one undo restores it. Leaves the preview and saves.</summary>
    public void ClearTab()
    {
        if (_window is null || string.IsNullOrEmpty(_window.ShownText))
        {
            return;
        }

        _window.SetPreview(false);
        _window.ApplyEdit(new Core.Modules.Scratchpad.TextEdit(string.Empty, 0, 0));
        FlushNow(showFailure: false);
    }

    public void CopyAll()
    {
        var text = _window?.ShownText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _services.GetRequiredService<IClipboardService>().SetText(text);
        _window!.ShowCopied();
    }

    /// <summary>Saves the requesting tab's text as it is at confirmation, UTF-8 without BOM, atomically.</summary>
    public async Task ExportAsync()
    {
        if (_window is null || DialogOpen || Store.Document is not { } document || document.Selected.IsEmpty)
        {
            return;
        }

        FlushNow(showFailure: false);
        var padId = document.SelectedId;
        var name = document.Selected.Name;
        _dialogs++;
        IStorageFile? file;
        try
        {
            file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = ScratchpadNames.ExportFileName(name, DateTime.Now),
                DefaultExtension = "txt",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType(L.Get("win.scratchpad.plainText")) { Patterns = ["*.txt"] },
                    new FilePickerFileType(L.Get("win.scratchpad.markdown")) { Patterns = ["*.md"] },
                ],
            });
        }
        finally
        {
            _dialogs--;
        }

        if (file?.TryGetLocalPath() is { } path)
        {
            var pad = Store.Document?.Find(padId);
            var ok = pad is not null && ScratchpadStore.WriteVerified(path, new UTF8Encoding(false).GetBytes(pad.Text));
            if (!ok)
            {
                _hud.Show(L.Get("scratchpad.exportFailed"), HudStyle.Error, "Save");
            }
        }

        if (_window.IsVisible)
        {
            Focus();
        }
    }

    public void OpenLink(string url) => _services.GetRequiredService<IShellService>().OpenUrl(url);

    /// <summary>Feature uninstalled: hide and forget the window and its remembered frame.</summary>
    public void Discard()
    {
        Hide();
        _window?.Close();
        _window = null;
    }

    public void Dispose()
    {
        // Flush unconditionally on exit (the macOS port could skip it).
        FlushNow(showFailure: false);
        _outsideClick?.Dispose();
        foreach (var observer in _observers)
        {
            observer.Dispose();
        }
    }

    private ScratchpadWindow CreateWindow()
    {
        var window = new ScratchpadWindow(this);
        window.Closing += (_, e) =>
        {
            if (!e.IsProgrammatic)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // A window closed from outside (shutdown, a test harness) cannot be shown again: build a new one next time.
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                FlushNow(showFailure: false);
                _outsideClick?.Dispose();
                _outsideClick = null;
                _window = null;
            }
        };
        return window;
    }

    private void Focus()
    {
        if (_window is null)
        {
            return;
        }

        _window.Activate();
        _services.GetService<IWindowChrome>()?.BringToFront(WindowInterop.Handle(_window));
        _window.FocusEditor();
    }

    private void Render() => Render(forceText: false);

    private void Render(bool forceText)
    {
        if (_window is null || Store.Document is not { } document)
        {
            return;
        }

        _window.Render(document, Store.SaveFailed, _settings.Get(ScratchpadSettings.TextSize), _settings.Get(ScratchpadSettings.BackgroundOpacity), forceText);
    }

    private void OnSettingsChanged()
    {
        if (_window is null)
        {
            return;
        }

        _window.Topmost = _settings.Get(ScratchpadSettings.AlwaysOnTop);
        if (_window.IsVisible)
        {
            _window.SetPinned(!_settings.Get(ScratchpadSettings.CloseOnClickOutside));
            UpdateOutsideClick();
        }

        Render();
    }

    private DispatcherTimer CreateAutosaveTimer()
    {
        var timer = new DispatcherTimer { Interval = AutosaveDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Store.Flush();
        };
        return timer;
    }

    private void FlushNow(bool showFailure)
    {
        _autosave?.Stop();
        if (!Store.Flush() && showFailure)
        {
            _hud.Show(L.Get("scratchpad.saveFailed"), HudStyle.Warning, "Warning");
        }
    }

    /// <summary>The shared mouse hook watches clicks only while the pad is shown, unpinned and no dialog is up.</summary>
    private void UpdateOutsideClick()
    {
        var wanted = _window is { IsVisible: true, Pinned: false };
        if (wanted && _outsideClick is null)
        {
            _outsideClick = _hooks.SubscribeMouse(OnGlobalMouse);
        }
        else if (!wanted && _outsideClick is not null)
        {
            _outsideClick.Dispose();
            _outsideClick = null;
        }
    }

    private bool OnGlobalMouse(ref MouseHookEvent e)
    {
        if (e.Kind is not (MouseHookKind.LeftDown or MouseHookKind.RightDown or MouseHookKind.MiddleDown or MouseHookKind.XDown))
        {
            return false;
        }

        var point = e.Position;
        Dispatcher.UIThread.Post(() => OnMouseDownAnywhere(point));
        return false;
    }

    private void OnMouseDownAnywhere(Core.Platform.PixelPoint point)
    {
        if (_window is not { IsVisible: true, Pinned: false } window || DialogOpen)
        {
            return;
        }

        var scale = window.RenderScaling;
        var tolerance = (int)Math.Ceiling(2 * scale);
        var position = window.Position;
        var width = (int)Math.Ceiling(window.Bounds.Width * scale);
        var height = (int)Math.Ceiling(window.Bounds.Height * scale);
        var inside = point.X >= position.X - tolerance && point.X <= position.X + width + tolerance
                     && point.Y >= position.Y - tolerance && point.Y <= position.Y + height + tolerance;
        if (inside || _platform?.IsOnScreenKeyboardAt(point) == true)
        {
            return;
        }

        Hide();
    }

    private bool IsOnAnyWorkArea(ScratchpadWindow window)
    {
        var position = window.Position;
        return _screens.Screens.Any(s => s.WorkArea.Contains(new Core.Platform.PixelPoint(position.X + 40, position.Y + 10)));
    }

    private static void FadeIn(ScratchpadWindow window)
    {
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - started).TotalSeconds / 0.13;
            window.Opacity = Math.Min(1, t);
            if (t >= 1)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }
}
