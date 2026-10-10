// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Snippets;

namespace Rivet.App.Features.Snippets;

/// <summary>
/// Text snippets (spec 06 §3.6) and the quick snippet menu (§3.7): the
/// typed-trigger engine, the menu window, its shortcut role, the Controls
/// switch and the Settings page.
/// </summary>
public sealed class SnippetsModule : IFeatureModule
{
    public const string PageId = "snippets";
    public const string LibraryRoleId = "snippetLibrary";
    public const string ShowLibraryActionId = "snippetLibrary.show";

    public string Id => "snippets";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SnippetExpansionService>();
        services.AddSingleton<SnippetLibraryHost>();
    }

    public void Initialize(ModuleContext context)
    {
        var services = context.Services;
        context.Features.RegisterController(FeatureIds.TextSnippets, context.Get<SnippetExpansionService>());
        context.Features.RegisterController(FeatureIds.TextSnippets, context.Get<SnippetLibraryHost>());

        context.Actions.Register(new AppAction
        {
            Id = ShowLibraryActionId,
            FeatureId = FeatureIds.TextSnippets,
            TitleKey = "snippets.libraryTitle",
            Icon = "TextBulletListSquare",
            Keywords = ["snippet", "snippets", "text"],
            Run = _ =>
            {
                Dispatcher.UIThread.Post(() => services.GetRequiredService<SnippetLibraryHost>().Toggle());
                return Task.CompletedTask;
            },
        });

        context.Shortcuts.Register(new ShortcutRole
        {
            Id = LibraryRoleId,
            FeatureId = FeatureIds.TextSnippets,
            TitleKey = "snippets.libraryTitle",
            Storage = SnippetSettings.LibraryShortcut,
            Default = SnippetSettings.DefaultLibraryShortcut,
            RequiredEnableKeys = [SnippetSettings.LibraryEnabled],
            ActionId = ShowLibraryActionId,
        });

        context.Panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "textSnippets",
            FeatureId = FeatureIds.TextSnippets,
            TitleKey = "snippets.pageTitle",
            CaptionKey = "snippets.hubDescription",
            Icon = "TextExpand",
            Setting = SnippetSettings.ExpansionEnabled,
            Category = PanelToggleCategory.Input,
            Order = 160,
            SettingsPageId = PageId,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "snippets.pageTitle",
            Icon = "TextExpand",
            Category = SettingsCategory.MouseKeyboard,
            Order = 40,
            FeatureIds = [FeatureIds.TextSnippets],
            CreateView = sp => new SnippetsSettingsPage(sp),
            KeywordKeys = ["snippets.enable", "snippets.libraryTitle", "snippets.soundToggle", "snippets.addButton"],
            Keywords = ["snippet", "snippets", "text expansion", "autotext"],
        });
    }
}

/// <summary>
/// Owns the quick snippet menu: created on first use (UI thread), closed when
/// its switch turns off or the feature goes away.
/// </summary>
public sealed class SnippetLibraryHost : IFeatureController, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IDisposable _subscription;
    private SnippetLibraryWindow? _window;

    public SnippetLibraryHost(IServiceProvider services, ISettingsStore settings)
    {
        _services = services;
        _subscription = settings.Observe(() => Dispatcher.UIThread.Post(() =>
        {
            if (!settings.Get(SnippetSettings.LibraryEnabled))
            {
                Close();
            }
        }), SnippetSettings.LibraryEnabled);
    }

    public SnippetLibraryWindow Window => _window ??= new SnippetLibraryWindow(_services);

    public void Toggle() => Window.Open();

    public void Sync(bool available)
    {
        if (!available)
        {
            Dispatcher.UIThread.Post(Close);
        }
    }

    public void Dispose() => _subscription.Dispose();

    private void Close()
    {
        if (_window is { IsVisible: true })
        {
            _window.Dismiss(FloatingCloseReason.Action);
        }
    }
}
