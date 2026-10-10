// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Settings.Pages;

/// <summary>Backup, data folders and uninstalling the app.</summary>
public sealed class AdvancedPage : SettingsPage
{
    private readonly TextBlock _backupStatus = new() { Classes = { "caption" }, IsVisible = false };

    public AdvancedPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var store = services.GetRequiredService<SettingsStore>();
        var paths = services.GetRequiredService<AppPaths>();
        var shell = services.GetRequiredService<IShellService>();
        var relauncher = services.GetRequiredService<IRelauncher>();

        var export = ActionButton(L.Get("backup.exportButton"), async () => await ExportAsync(store), "ArrowUpload");
        var import = ActionButton(L.Get("backup.importButton"), async () => await ImportAsync(store, relauncher), "ArrowDownload");

        Content = Stack(
            Header("Strings.tabAdvanced"),
            Card("backup.title",
                Note(L.Get("win.shell.backupDescription")),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { export, import } },
                _backupStatus),
            Card("win.shell.dataFolderTitle",
                Row("Folder", paths.RoamingRoot, L.Get("win.shell.dataFolderCaption"),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            ActionButton(L.Get("win.shell.openDataFolder"), () => shell.OpenFile(AppPaths.EnsureDirectory(paths.RoamingRoot))),
                            ActionButton(L.Get("win.shell.openLogsFolder"), () => shell.OpenFile(AppPaths.EnsureDirectory(paths.Logs))),
                        },
                    })),
            Card("win.shell.uninstallSelfTitle",
                Row("Delete", L.Get("win.shell.uninstallSelfTitle"), L.Get("win.shell.uninstallSelfCaption"),
                    ActionButton(L.Get("win.shell.openAppsSettings"), () => shell.OpenSystemSettings("ms-settings:appsfeatures")))));
    }

    private async Task ExportAsync(SettingsStore store)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{AppIdentity.DisplayName} Settings.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            store.Flush();
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(SettingsBackup.Export(store));
            ShowStatus(L.Get("backup.exported"), "SuccessBrush");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("backup", "Export failed.", ex);
            ShowStatus(L.Get("backup.exportFailed"), "WarningBrush");
        }
    }

    private async Task ImportAsync(SettingsStore store, IRelauncher relauncher)
    {
        if (TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage } top)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        string json;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(L.Get("win.shell.backupInvalid"), "WarningBrush");
            return;
        }

        var backup = SettingsBackup.Parse(json);
        if (backup is null)
        {
            ShowStatus(L.Get("win.shell.backupInvalid"), "WarningBrush");
            return;
        }

        if (await ConfirmDialog.ShowAsync(top as Window, L.Get("backup.importConfirmTitle"), L.Get("backup.importConfirmBody"),
                L.Get("backup.importAction"), L.Get("hub.presetConfirmCancel")))
        {
            SettingsBackup.Apply(store, backup);
            relauncher.RelaunchAndExit();
        }
    }

    private void ShowStatus(string text, string brushKey)
    {
        _backupStatus.Text = text;
        _backupStatus.IsVisible = true;
        _backupStatus.Bind(TextBlock.ForegroundProperty, _backupStatus.GetResourceObservable(brushKey).ToBinding());
    }
}
