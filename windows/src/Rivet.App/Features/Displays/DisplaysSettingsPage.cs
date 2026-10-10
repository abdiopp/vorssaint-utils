// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Awake;
using Rivet.Core.Displays;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Displays;

/// <summary>Settings → Energy and display → Displays (spec 03 §3.19.7).</summary>
public sealed class DisplaysSettingsPage : SettingsPage
{
    private readonly BrightnessService _service;
    private readonly StackPanel _rows = new() { Spacing = 12 };
    private readonly TextBlock _note;

    public DisplaysSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _service = services.GetRequiredService<BrightnessService>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        _note = Note(string.Empty);

        var shortcutRows = new[] { DisplaysModule.DecreaseRoleId, DisplaysModule.IncreaseRoleId }
            .Select(shortcuts.Find)
            .Where(r => r is not null)
            .Select(r => (Control)new ShortcutRoleRow(r!))
            .ToArray();

        Content = Stack(
            Header("brightness.pageTitle", "win.displays.pageDescription"),
            Card(null, Toggle(DisplaySettings.Enabled, "Desktop", "brightness.enable", "brightness.enableCaption")),
            CardText(L.Get("brightness.pageTitle"), new StackPanel { Spacing = 8, Margin = new Thickness(0, 2, 0, 2), Children = { _rows, _note } }),
            Card("win.displays.moreOptions",
                Toggle(DisplaySettings.OsdEnabled, "BrightnessHigh", "brightness.osdToggle", "brightness.osdCaption"),
                Toggle(DisplaySettings.FollowPointer, "CursorClick", "win.displays.followPointer", "win.displays.followPointerCaption"),
                Choice(DisplaySettings.KeyStep, "Ruler", "brightness.keyStep", "brightness.keyStepCaption",
                    [("standard", L.Get("brightness.keyStepStandard")), ("half", L.Get("brightness.keyStepHalf")), ("quarter", L.Get("brightness.keyStepQuarter"))])),
            Card("win.displays.shortcutsTitle",
                [Toggle(DisplaySettings.ShortcutsEnabled, "Keyboard", "brightness.displayBrightnessShortcuts", "brightness.displayBrightnessShortcutCaption"), .. shortcutRows]),
            Note(L.Get("brightness.externalCaption")));

        Track(Settings.Observe(() => Dispatcher.UIThread.Post(Render), DisplaySettings.Enabled));
        AttachedToVisualTree += (_, _) =>
        {
            _service.Changed += OnChanged;
            _service.Refresh();
            Render();
        };
        DetachedFromVisualTree += (_, _) => _service.Changed -= OnChanged;
        Render();
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        var enabled = Settings.Get(DisplaySettings.Enabled);
        var displays = enabled ? _service.Displays : [];
        DisplaysSection.SyncRows(_rows, displays, (status, index) => new DisplayRow(_service, Settings, status, index, wide: true));
        _rows.IsVisible = displays.Count > 0;
        _note.Text = !enabled ? L.Get("win.displays.disabledCaption")
            : displays.Count > 0 ? null
            : _service.IsRunning && !_service.IsReady ? L.Get("win.displays.scanning")
            : L.Get("brightness.noDisplays");
        _note.IsVisible = _note.Text is not null;
    }
}
