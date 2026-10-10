// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.Output;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Capture.Settings;

/// <summary>
/// Settings → Screenshot (spec 01 §4.0): capture actions and shortcuts,
/// selection, magnifier, after-capture and output options. Editor tools
/// belong to the editor's own page; temporary links are not offered.
/// </summary>
internal sealed class ScreenshotSettingsPage : CaptureSettingsPage
{
    public ScreenshotSettingsPage(IServiceProvider services)
        : base(services)
    {
        Content = Stack(
            Header("screenshot.pageTitle", "screenshot.panelCaption"),
            Card(null,
                ActionsRow("Screenshot", L.Get("screenshot.screenCaptureTitle"), L.Get("screenshot.panelCaption"),
                    (L.Get("screenshot.captureButton"), CaptureModule.CaptureActionId, "Screenshot", true),
                    (L.Get("screenshot.scrollingCaptureButton"), CaptureModule.ScrollingActionId, "ArrowSortDownLines", false))),
            ShortcutsCard(),
            CaptureCard(),
            MagnifierCard(),
            AfterCaptureCard(),
            OutputCard());
    }

    private Control ShortcutsCard()
    {
        var showMenu = GatedToggle(CaptureSettings.ScreenshotShowCaptureMenu, CaptureSettings.ScreenshotShortcutEnabled, null, "screenshot.showCaptureMenuOnShortcut");
        return Card("win.capture.sectionShortcuts",
            ShortcutRow(CaptureModule.ScreenshotRoleId, CaptureSettings.ScreenshotShortcutEnabled),
            Indented(showMenu),
            ShortcutRow(CaptureModule.FullScreenRoleId, CaptureSettings.FullScreenShortcutEnabled),
            ShortcutRow(CaptureModule.LastCaptureRoleId, CaptureSettings.LastCaptureShortcutEnabled),
            ShortcutRow(CaptureModule.ClipboardRoleId, CaptureSettings.ClipboardShortcutEnabled),
            ShortcutRow(CaptureModule.RecentCapturesRoleId, CaptureSettings.RecentCapturesShortcutEnabled),
            Toggle(CaptureSettings.PrintScreenEnabled, "Keyboard", "win.capture.printScreenToggle", "win.capture.printScreenCaption"));
    }

    private Control CaptureCard()
    {
        return Card("win.capture.sectionCapture",
            Toggle(CaptureSettings.Freeze, "Snowflake", "screenshot.freezeToggle", "screenshot.freezeCaption"),
            Toggle(CaptureSettings.HideOwnWindows, "EyeOff", "screenshot.hideVorssaintWindowsToggle", "win.capture.hideOwnWindowsCaption"),
            Choice(CaptureSettings.Delay, "Timer", "screenshot.delayLabel", null,
            [
                (0, L.Get("screenshot.delayOff")),
                (3, L.Format("screenshot.delaySecondsFormat", 3)),
                (5, L.Format("screenshot.delaySecondsFormat", 5)),
                (10, L.Format("screenshot.delaySecondsFormat", 10)),
            ]),
            Toggle(CaptureSettings.IncludePointer, "Cursor", "screenshot.pointerToggle"),
            Toggle(CaptureSettings.ShowLastRegion, "SelectObject", "screenshot.lastRegionToggle"));
    }

    private Control MagnifierCard()
    {
        var defaultZoom = Choice(CaptureSettings.LoupeDefaultZoom, null, "screenshot.loupeDefaultZoomLabel", null,
            LoupeMath.DefaultZooms.Select(z => (z, $"{z:0.#}×")).ToList());
        When(() => defaultZoom.IsVisible = !Settings.Get(CaptureSettings.LoupeRememberZoom), CaptureSettings.LoupeRememberZoom);
        return LiveCard("win.capture.sectionMagnifier",
            Toggle(CaptureSettings.LoupeStartsOn, "ZoomIn", "screenshot.loupeStartsOnToggle"),
            Toggle(CaptureSettings.LoupeRememberZoom, "History", "screenshot.loupeRememberZoomToggle"),
            Indented(defaultZoom),
            Choice(CaptureSettings.LoupeSteppedZoomByDefault, "ScrollVertical", "screenshot.loupeWheelZoomLabel", "screenshot.loupeZoomOptionCaption",
            [
                (false, L.Get("screenshot.loupeZoomFast")),
                (true, L.Get("screenshot.loupeZoomStepped")),
            ]));
    }

    private Control AfterCaptureCard()
    {
        var action = Choice(CaptureSettings.DefaultAction, "Flash", "screenshot.defaultActionLabel", "screenshot.defaultActionCaption",
        [
            (string.Empty, L.Get("screenshot.defaultActionNone")),
            ("save", L.Get("screenshot.saveButton")),
            ("saveAndCopy", L.Get("screenshot.defaultActionSaveAndCopy")),
            ("copy", L.Get("screenshot.copyButton")),
            ("edit", L.Get("screenshot.editButton")),
        ]);
        var preview = Toggle(CaptureSettings.PreviewEnabled, null, "screenshot.confirmationPreviewToggle", "screenshot.confirmationPreviewCaption");
        var duration = Choice(CaptureSettings.PreviewDuration, null, "screenshot.confirmationPreviewDurationLabel", null,
        [
            (1, L.Format("screenshot.delaySecondsFormat", 1)),
            (2, L.Format("screenshot.delaySecondsFormat", 2)),
            (3, L.Format("screenshot.delaySecondsFormat", 3)),
            (5, L.Format("screenshot.delaySecondsFormat", 5)),
            (10, L.Format("screenshot.delaySecondsFormat", 10)),
            (0, L.Get("screenshot.confirmationPreviewUntilDismissed")),
        ]);
        var previewIndented = Indented(preview);
        var durationIndented = Indented(duration);
        When(() =>
        {
            var value = CaptureSettings.ParseDefaultAction(Settings.Get(CaptureSettings.DefaultAction));
            action.Description = value == ScreenshotDefaultAction.Edit ? null : L.Get("screenshot.defaultActionCaption");
            var automatic = value is ScreenshotDefaultAction.Save or ScreenshotDefaultAction.SaveAndCopy or ScreenshotDefaultAction.Copy;
            previewIndented.IsVisible = automatic;
            durationIndented.IsVisible = automatic && Settings.Get(CaptureSettings.PreviewEnabled);
        }, CaptureSettings.DefaultAction, CaptureSettings.PreviewEnabled);

        return LiveCard("win.capture.sectionAfterCapture",
            action,
            previewIndented,
            durationIndented,
            Choice(CaptureSettings.PreviewPositionValue, "PictureInPicture", "screenshot.previewPositionLabel", null,
            [
                (string.Empty, L.Get("screenshot.previewPositionAutomatic")),
                ("topLeft", L.Get("screenshot.previewPositionTopLeft")),
                ("topRight", L.Get("screenshot.previewPositionTopRight")),
                ("bottomLeft", L.Get("screenshot.previewPositionBottomLeft")),
                ("bottomRight", L.Get("screenshot.previewPositionBottomRight")),
            ]),
            Toggle(CaptureSettings.PreviewTakesFocus, "Keyboard", "screenshot.previewFocusToggle", "screenshot.previewFocusCaption"));
    }

    private Control OutputCard()
    {
        return LiveCard("win.capture.sectionOutput",
            AutoCopyRow(),
            ShelfRow(),
            FolderRow(),
            PatternRow(CaptureSettings.SaveSubfolder, "FolderArrowRight", "screenshot.subfolderLabel", "screenshot.subfolderCaption", subfolder: true),
            PatternRow(CaptureSettings.FileNamePattern, "Rename", "screenshot.fileNamePatternLabel", "screenshot.fileNamePatternCaption", subfolder: false),
            NumberRow(),
            Toggle(CaptureSettings.Downscale, "ResizeSmall", "screenshot.downscaleToggle", "screenshot.downscaleCaption"));
    }

    /// <summary>
    /// Reads on when either automatic copy or the default action copies;
    /// turning it off also strips the copy half from the default action.
    /// </summary>
    private Control AutoCopyRow()
    {
        var toggle = new ToggleSwitch { Classes = { "compact" } };
        void Refresh()
        {
            var action = CaptureSettings.ParseDefaultAction(Settings.Get(CaptureSettings.DefaultAction));
            toggle.IsChecked = Settings.Get(CaptureSettings.CopyToClipboard) || action is ScreenshotDefaultAction.Copy or ScreenshotDefaultAction.SaveAndCopy;
        }

        When(Refresh, CaptureSettings.CopyToClipboard, CaptureSettings.DefaultAction);
        toggle.IsCheckedChanged += (_, _) =>
        {
            var on = toggle.IsChecked == true;
            Settings.Set(CaptureSettings.CopyToClipboard, on);
            if (!on)
            {
                var action = CaptureSettings.ParseDefaultAction(Settings.Get(CaptureSettings.DefaultAction));
                Settings.Set(CaptureSettings.DefaultAction, CaptureSettings.ToStorage(CaptureSettings.WithoutCopy(action)));
            }
        };
        AutomationProperties.SetName(toggle, L.Get("screenshot.autoCopyToggle"));
        return Row("Copy", L.Get("screenshot.autoCopyToggle"), L.Get("screenshot.autoCopyCaption"), toggle);
    }

    /// <summary>Disabled (reading off, value kept) while the shelf is not installed and on.</summary>
    private Control ShelfRow()
    {
        var runtime = Services.GetRequiredService<FeatureRuntime>();
        var row = Toggle(CaptureSettings.AddToShelf, "Archive", "screenshot.addToShelfToggle", "screenshot.addToShelfCaption");
        var toggle = (ToggleSwitch)row.Content!;
        void Refresh()
        {
            var shelfOn = runtime.IsEngaged(FeatureIds.Shelf) && Services.GetService<IShelfIntake>() is not null;
            row.IsEnabled = shelfOn;
            toggle.IsChecked = shelfOn && Settings.Get(CaptureSettings.AddToShelf);
        }

        When(Refresh, CaptureSettings.AddToShelf, FeatureKeys.ShelfEnabled);
        return row;
    }

    private Control FolderRow()
    {
        var output = Services.GetRequiredService<CaptureOutputService>();
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = Avalonia.Media.TextTrimming.PrefixCharacterEllipsis };
        var reset = CaptureUi.IconButton("Dismiss", L.Get("win.capture.folderReset"), () => Settings.Set(CaptureSettings.SaveFolder, string.Empty), 13);
        var choose = ActionButton(L.Get("screenshot.folderChoose"), () => _ = ChooseFolderAsync());
        When(() =>
        {
            var folder = output.ResolveFolder();
            name.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } leaf ? leaf : folder;
            ToolTip.SetTip(name, folder);
            reset.IsVisible = Settings.Get(CaptureSettings.SaveFolder).Length > 0;
        }, CaptureSettings.SaveFolder);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { name, reset, choose } };
        return Row("Folder", L.Get("screenshot.folderLabel"), null, panel);
    }

    private async Task ChooseFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var output = Services.GetRequiredService<CaptureOutputService>();
        IStorageFolder? start = null;
        try
        {
            start = await storage.TryGetFolderFromPathAsync(new Uri(output.ResolveFolder()));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.Get("win.capture.folderChooseTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            Settings.Set(CaptureSettings.SaveFolder, path);
        }
    }

    private Control PatternRow(Rivet.Core.Settings.Setting<string> setting, string icon, string titleKey, string captionKey, bool subfolder)
    {
        var box = new TextBox { Width = 170, Text = Settings.Get(setting), PlaceholderText = subfolder ? "%y-%mo" : CaptureOutputService.Prefix + " %#" };
        AutomationProperties.SetName(box, L.Get(titleKey));
        var example = Note(string.Empty);
        void Refresh()
        {
            var pattern = Settings.Get(setting);
            var now = DateTime.Now;
            if (subfolder)
            {
                var expanded = CaptureNaming.Subfolder(pattern, now);
                example.Text = expanded.Length == 0 ? string.Empty : L.Format("win.capture.exampleFormat", expanded);
                example.IsVisible = expanded.Length > 0;
            }
            else
            {
                example.Text = L.Format("win.capture.exampleFormat", CaptureNaming.FileName(pattern, now, FileNumberSequence.Peek(Settings), CaptureOutputService.Prefix));
            }
        }

        box.TextChanged += (_, _) =>
        {
            if (box.Text != Settings.Get(setting))
            {
                Settings.Set(setting, box.Text ?? string.Empty);
            }
        };
        When(Refresh, setting, CaptureSettings.FileNumberNext);
        var row = Row(icon, L.Get(titleKey), L.Get(captionKey), box);
        return new StackPanel { Spacing = 2, Children = { row, Indented(example) } };
    }

    /// <summary>"Starts at", Reset and "Next: N", shown while the file name uses %#.</summary>
    private Control NumberRow()
    {
        var start = new NumericUpDown { Minimum = 0, Maximum = 999_999, Increment = 1, FormatString = "0", Width = 130, Value = Settings.Get(CaptureSettings.FileNumberStart) };
        AutomationProperties.SetName(start, L.Get("screenshot.fileNumberStartLabel"));
        start.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value && (int)value != Settings.Get(CaptureSettings.FileNumberStart))
            {
                Settings.Set(CaptureSettings.FileNumberStart, (int)value);
                FileNumberSequence.Restart(Settings);
            }
        };
        var next = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Classes = { "caption" } };
        var reset = ActionButton(L.Get("screenshot.fileNumberResetButton"), () => FileNumberSequence.Restart(Settings));
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { next, start, reset } };
        var row = Row(null, L.Get("screenshot.fileNumberStartLabel"), null, panel);
        var indented = Indented(row);
        When(() =>
        {
            next.Text = L.Format("screenshot.fileNumberNextFormat", FileNumberSequence.Peek(Settings));
            indented.IsVisible = CaptureNaming.UsesNumber(Settings.Get(CaptureSettings.FileNamePattern));
        }, CaptureSettings.FileNamePattern, CaptureSettings.FileNumberNext);
        return indented;
    }
}
