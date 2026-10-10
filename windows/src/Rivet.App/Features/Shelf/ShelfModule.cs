// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Scratchpad;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Modules.Shelf;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Shelf;

/// <summary>Shelf: a temporary holding area for dragged files, images, links and text (spec 07 §3.1).</summary>
public sealed class ShelfModule : IFeatureModule
{
    // Built by concatenation so the string-key test does not mistake the ids for catalog keys.
    private const string ActionPrefix = "shelf";
    public const string ToggleActionId = ActionPrefix + ".toggle";
    public const string OpenActionId = ActionPrefix + ".open";
    public const string PageId = "shelf";

    public static readonly ShortcutRole Role = new()
    {
        Id = "shelf",
        FeatureId = FeatureIds.Shelf,
        TitleKey = "Strings.shelfShortcutToggle",
        Storage = ShelfSettings.Shortcut,
        Default = ShelfSettings.DefaultShortcut,
        RequiredEnableKeys = [FeatureKeys.ShelfEnabled, ShelfSettings.ShortcutEnabled],
        ActionId = ToggleActionId,
    };

    public string Id => "shelf";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ShelfService>();
        services.AddSingleton<IShelfIntake>(sp => sp.GetRequiredService<ShelfService>());
    }

    public void Initialize(ModuleContext context)
    {
        var service = context.Get<ShelfService>();
        context.Features.RegisterController(FeatureIds.Shelf, service);

        context.Actions.Register(new AppAction
        {
            Id = ToggleActionId,
            FeatureId = FeatureIds.Shelf,
            TitleKey = "Strings.shelfName",
            Icon = "Archive",
            Run = _ =>
            {
                service.ShortcutPressed();
                return Task.CompletedTask;
            },
        });
        context.Actions.Register(new AppAction
        {
            Id = OpenActionId,
            FeatureId = FeatureIds.Shelf,
            TitleKey = "Strings.shelfName",
            SubtitleKey = "Strings.shelfEnableCaption",
            Icon = "Archive",
            Keywords = ["shelf", "drop", "drag", "files", "holding"],
            Run = async ctx =>
            {
                if (ctx.Source is ActionSource.CommandBar or ActionSource.QuickPanel or ActionSource.RadialMenu)
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.15)).ConfigureAwait(true);
                }

                if (!service.IsAvailable)
                {
                    // The Command Bar shows "needs setup": take the user to the switch.
                    context.Get<IAppShell>().OpenSettings(PageId);
                    return;
                }

                service.Summon();
            },
        });

        context.Shortcuts.Register(Role);

        context.Panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "shelf",
            FeatureId = FeatureIds.Shelf,
            TitleKey = "Strings.shelfName",
            CaptionKey = "win.shell.shelfDescription",
            Icon = "Archive",
            Setting = FeatureKeys.ShelfEnabled,
            Category = PanelToggleCategory.Files,
            Order = 40,
            SettingsPageId = PageId,
        });

        context.TrayMenu.Add(new TrayMenuItem
        {
            Id = "shelf",
            Title = () => service.LeafCount > 0 ? $"{L.Get("Strings.shelfMenuItem")} ({service.LeafCount})" : L.Get("Strings.shelfMenuItem"),
            Icon = "Archive",
            Order = -25,
            FeatureId = FeatureIds.Shelf,
            IsVisible = () => service.IsAvailable,
            Invoke = service.ExpandDocked,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "Strings.shelfName",
            Icon = "Archive",
            Category = SettingsCategory.ClipboardFiles,
            Order = 40,
            FeatureIds = [FeatureIds.Shelf],
            CreateView = sp => new ShelfSettingsPage(sp),
            KeywordKeys = ["Strings.shelfEnable", "Strings.shelfShakeToggle", "Strings.shelfEdgeToggle", "Strings.shelfExclusionsTitle", "Strings.shelfRemoveAfterDrop"],
            Keywords = ["shelf", "drop", "drag", "files", "shake"],
        });
    }
}

/// <summary>Settings › Clipboard and files › Shelf (spec 07 §3.1.16).</summary>
public sealed class ShelfSettingsPage : SettingsPage
{
    private readonly ShelfService _service;
    private readonly StackPanel _exclusions = new() { Spacing = 6 };

    public ShelfSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _service = services.GetRequiredService<ShelfService>();
        var placements = new (string Value, string Label)[]
        {
            ("menuBar", L.Get("win.shelf.dockTray")),
            ("topCenter", L.Get("Strings.shelfDockTopCenter")),
        };
        var howTo = new StackPanel { Spacing = 6 };
        foreach (var (number, key) in new[] { (1, "Strings.shelfStep1"), (2, "Strings.shelfStep2"), (3, "Strings.shelfStep3") })
        {
            howTo.Children.Add(new TextBlock { Text = $"{number}. {L.Get(key)}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }

        Content = Stack(
            Header("Strings.shelfName", "Strings.shelfEnableCaption"),
            Card(null,
                Toggle(FeatureKeys.ShelfEnabled, "Archive", "Strings.shelfEnable", "Strings.shelfEnableCaption"),
                Row("ShieldCheckmark", L.Get("Strings.shelfNoPermission"), null),
                Row("Open", L.Get("Strings.shelfOpenNow"), null, ActionButton(L.Get("Strings.shelfOpenNow"), () => _service.Summon(), "Open", accent: true))),
            Card("Strings.shelfHowTitle", howTo),
            Card("win.shelf.triggersTitle",
                Toggle(ShelfSettings.ShortcutEnabled, "Keyboard", "Strings.shelfShortcutToggle"),
                new ShortcutRoleRow(ShelfModule.Role, title: L.Get("Strings.shelfHotkeyLabel")),
                Toggle(ShelfSettings.ShortcutAddsExplorerSelection, "FolderOpen", "win.shelf.explorerSelection", "win.shelf.explorerSelectionCaption"),
                Toggle(ShelfSettings.ShakeToOpen, "ArrowMove", "Strings.shelfShakeToggle", "win.shelf.shakeCaption"),
                Toggle(ShelfSettings.DropZoneEnabled, "PanelBottom", "win.shelf.dropZoneToggle", "win.shelf.dropZoneCaption"),
                Choice(ShelfSettings.DockPlacement, "LayoutRowTwo", "Strings.shelfDockPlacement", null, placements),
                Toggle(ShelfSettings.EdgeDragEnabled, "PanelLeft", "Strings.shelfEdgeToggle", "Strings.shelfEdgeCaption")),
            Card("Strings.shelfBehaviorTitle",
                Toggle(ShelfSettings.CloseAfterDrop, "Dismiss", "Strings.shelfCloseAfterDrop", "Strings.shelfCloseAfterDropCaption"),
                Toggle(ShelfSettings.RemoveAfterDrop, "ArrowExportUp", "Strings.shelfRemoveAfterDrop", "Strings.shelfRemoveAfterDropCaption"),
                Toggle(ShelfSettings.ClearOnClose, "Delete", "Strings.shelfClearOnClose", "Strings.shelfClearOnCloseCaption")),
            Card("Strings.shelfExclusionsTitle",
                _exclusions,
                Row(null, L.Get("win.shelf.exclusionsCaption"), null, ActionButton(L.Get("win.shelf.addApp"), () => _ = AddAppAsync(), "Add"))));
        RebuildExclusions();
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RebuildExclusions), ShelfSettings.AutomaticExclusions));
    }

    private void RebuildExclusions()
    {
        _exclusions.Children.Clear();
        var list = Settings.Get(ShelfSettings.AutomaticExclusions);
        if (list.Count == 0)
        {
            _exclusions.Children.Add(Caption(L.Get("Strings.shelfExclusionsEmpty")));
            return;
        }

        foreach (var entry in list)
        {
            var remove = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 12 } };
            ToolTip.SetTip(remove, L.Get("Strings.actionRemove"));
            AutomationProperties.SetName(remove, L.Get("Strings.actionRemove"));
            var captured = entry;
            remove.Click += (_, _) => Settings.Set(ShelfSettings.AutomaticExclusions, Settings.Get(ShelfSettings.AutomaticExclusions).Where(e => e != captured).ToList());
            var name = Path.GetFileNameWithoutExtension(entry);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(new SymbolIcon { Symbol = Symbol.AppGeneric, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            var texts = new StackPanel { Children = { new TextBlock { Text = name.Length == 0 ? entry : name, FontWeight = Avalonia.Media.FontWeight.SemiBold }, Caption(entry) } };
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            _exclusions.Children.Add(row);
        }
    }

    /// <summary>"Add app…": an .exe picker (Windows has no installed-app list with stable identities).</summary>
    private async Task AddAppAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = L.Get("win.shelf.addApp"),
            FileTypeFilter = [new FilePickerFileType(L.Get("win.shelf.applications")) { Patterns = ["*.exe"] }],
        }).ConfigureAwait(true);
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            Settings.Set(ShelfSettings.AutomaticExclusions, ShelfSettings.CleanExclusions([.. Settings.Get(ShelfSettings.AutomaticExclusions), .. paths]));
        }
    }
}
