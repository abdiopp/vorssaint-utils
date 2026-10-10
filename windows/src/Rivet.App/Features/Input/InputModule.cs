// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Input;

/// <summary>
/// The input fixes (spec 07 §3.7): extra click filter, key debounce, mouse
/// button shortcuts with the desktop drag, smooth scrolling, the scroll
/// direction inverter, the Super key and quit/close protection. Every feature
/// subscribes to the shared low-level hooks only while it is installed and
/// switched on.
/// </summary>
public sealed class InputModule : IFeatureModule
{
    public const string MousePageId = "mouse";
    public const string MouseButtonsPageId = "mouseButtons";
    public const string KeyDebouncePageId = "keyDebounce";
    public const string SuperKeyPageId = "superKey";
    public const string QuitProtectionPageId = "quitProtection";

    public string Id => "input";

    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<IInputClock>(SystemInputClock.Instance);
        services.TryAddSingleton<IWheelDeviceClassifier, NotchWheelClassifier>();
        services.AddSingleton<InputFixesControl>();
        services.AddSingleton<IInputFixesControl>(sp => sp.GetRequiredService<InputFixesControl>());
        services.AddSingleton<ScrollDirectionPolicy>();
        services.AddSingleton<QuitProtectionHudHost>();
        services.AddSingleton<IQuitProtectionHud>(sp => sp.GetRequiredService<QuitProtectionHudHost>());

        services.AddSingleton(sp => new ClickFilterService(
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IInputHooks>(), sp.GetRequiredService<IInputClock>(),
            sp.GetRequiredService<InputFixesControl>()));
        services.AddSingleton(sp => new KeyDebounceService(
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IInputHooks>(), sp.GetRequiredService<IInputClock>(),
            sp.GetRequiredService<IKeyboardInfo>(), sp.GetRequiredService<InputFixesControl>()));
        services.AddSingleton(sp => new MouseButtonShortcutsService(
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IInputHooks>(), sp.GetRequiredService<IInputClock>(),
            sp.GetRequiredService<IAppIdentityResolver>(), sp.GetRequiredService<IWheelDeviceClassifier>(),
            sp.GetService<IScreenService>(), sp.GetService<IMouseButtonClaims>, sp.GetRequiredService<InputFixesControl>()));
        services.AddSingleton(sp => new SmoothScrollService(
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IInputHooks>(), sp.GetRequiredService<IInputClock>(),
            sp.GetRequiredService<IAppIdentityResolver>(), sp.GetRequiredService<IWheelDeviceClassifier>(),
            sp.GetRequiredService<IGlideFrameTimer>(), sp.GetRequiredService<ScrollDirectionPolicy>()));
        services.AddSingleton(sp => new ScrollInverterService(
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IInputHooks>(), sp.GetRequiredService<IInputClock>(),
            sp.GetRequiredService<IAppIdentityResolver>(), sp.GetRequiredService<IWheelDeviceClassifier>(),
            sp.GetRequiredService<ScrollDirectionPolicy>()));
        services.AddSingleton(sp => new SuperKeyService(
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IInputHooks>(), sp.GetRequiredService<IInputClock>(),
            sp.GetRequiredService<IKeyboardInfo>(), sp.GetRequiredService<IRunningAppsMonitor>()));
        services.AddSingleton(sp => new QuitProtectionService(
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IInputHooks>(), sp.GetRequiredService<IInputClock>(),
            sp.GetRequiredService<IAppIdentityResolver>(), sp.GetRequiredService<IQuitProtectionHud>()));
    }

    public void Initialize(ModuleContext context)
    {
        var features = context.Features;
        features.RegisterController(FeatureIds.MouseClickDebounce, context.Get<ClickFilterService>());
        features.RegisterController(FeatureIds.KeyboardDebounce, context.Get<KeyDebounceService>());
        features.RegisterController(FeatureIds.MouseButtonShortcuts, context.Get<MouseButtonShortcutsService>());
        // The inverter configures the shared direction policy, so it syncs before smooth scrolling.
        features.RegisterController(FeatureIds.ScrollInverter, context.Get<ScrollInverterService>());
        features.RegisterController(FeatureIds.SmoothScroll, context.Get<SmoothScrollService>());
        features.RegisterController(FeatureIds.SuperKey, context.Get<SuperKeyService>());
        features.RegisterController(FeatureIds.QuitWindowProtection, context.Get<QuitProtectionService>());

        AddToggles(context.Panel);
        AddPages(context.SettingsPages);
        AddActions(context);
    }

    internal static void AddToggles(PanelRegistry panel)
    {
        panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "mouseScroll", FeatureId = FeatureIds.ScrollInverter, TitleKey = "Strings.invertMouseScroll",
            CaptionKey = "Strings.invertMouseScrollCaption", Icon = "ArrowSort", Setting = FeatureKeys.ScrollInverterEnabled,
            Category = PanelToggleCategory.Input, Order = 10, SettingsPageId = MousePageId,
        });
        panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "smoothScroll", FeatureId = FeatureIds.SmoothScroll, TitleKey = "Strings.smoothScrollName",
            CaptionKey = "Strings.smoothScrollCaption", Icon = "CursorHover", Setting = FeatureKeys.SmoothScrollEnabled,
            Category = PanelToggleCategory.Input, Order = 12, SettingsPageId = MousePageId,
        });
        panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "keyDebounce", FeatureId = FeatureIds.KeyboardDebounce, TitleKey = "Strings.keyDebounceName",
            CaptionKey = "Strings.keyDebounceCaption", Icon = "Keyboard", Setting = FeatureKeys.KeyboardDebounceEnabled,
            Category = PanelToggleCategory.Input, Order = 50, SettingsPageId = KeyDebouncePageId,
        });
        panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "mouseButtonShortcuts", FeatureId = FeatureIds.MouseButtonShortcuts, TitleKey = "mouseButtons.pageTitle",
            CaptionKey = "mouseButtons.panelCaption", Icon = "ControlButton", Setting = FeatureKeys.MouseButtonShortcutsEnabled,
            Category = PanelToggleCategory.Input, Order = 90, SettingsPageId = MouseButtonsPageId,
        });
        panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "superKey", FeatureId = FeatureIds.SuperKey, TitleKey = "superKey.pageTitle",
            CaptionKey = "superKey.hubDescription", Icon = "KeyboardShift", Setting = FeatureKeys.SuperKeyEnabled,
            Category = PanelToggleCategory.Input, Order = 100, SettingsPageId = SuperKeyPageId,
        });
        panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "mouseClickDebounce", FeatureId = FeatureIds.MouseClickDebounce, TitleKey = "mouseClickDebounce.title",
            CaptionKey = "mouseClickDebounce.caption", Icon = "CursorClick", Setting = FeatureKeys.MouseClickDebounceEnabled,
            Category = PanelToggleCategory.Input, Order = 110, SettingsPageId = MousePageId,
        });
        panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "quitProtectionQuit", FeatureId = FeatureIds.QuitWindowProtection, TitleKey = "win.input.quitToggle",
            CaptionKey = "win.shell.quitProtectionDescription", Icon = "Shield", Setting = FeatureKeys.QuitProtectionQuitEnabled,
            Category = PanelToggleCategory.Input, Order = 120, SettingsPageId = QuitProtectionPageId,
        });
        panel.AddToggle(new PanelToggleDescriptor
        {
            Id = "quitProtectionClose", FeatureId = FeatureIds.QuitWindowProtection, TitleKey = "win.input.closeToggle",
            CaptionKey = "win.shell.quitProtectionDescription", Icon = "Shield", Setting = FeatureKeys.QuitProtectionCloseEnabled,
            Category = PanelToggleCategory.Input, Order = 121, SettingsPageId = QuitProtectionPageId,
        });
    }

    private static void AddPages(SettingsPageRegistry pages)
    {
        pages.Add(new SettingsPageDescriptor
        {
            Id = MousePageId, TitleKey = "Strings.tabMouse", Icon = "Cursor", Category = SettingsCategory.MouseKeyboard, Order = 0,
            FeatureIds = [FeatureIds.MouseClickDebounce, FeatureIds.SmoothScroll, FeatureIds.ScrollInverter],
            CreateView = sp => new MouseSettingsPage(sp),
            KeywordKeys = ["mouseClickDebounce.title", "Strings.smoothScrollName", "Strings.invertMouseScroll", "Strings.scrollSection", "mouseExceptions.listTitle"],
            Keywords = ["wheel", "scroll", "touchpad", "debounce"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = MouseButtonsPageId, TitleKey = "mouseButtons.pageTitle", Icon = "ControlButton", Category = SettingsCategory.MouseKeyboard, Order = 1,
            FeatureIds = [FeatureIds.MouseButtonShortcuts],
            CreateView = sp => new MouseButtonsSettingsPage(sp),
            KeywordKeys = ["mouseButtons.backButtonName", "mouseButtons.sideWheelLeftName", "mouseButtons.spacesEnableLabel"],
            Keywords = ["XButton", "Task View", "desktop"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = KeyDebouncePageId, TitleKey = "Strings.keyDebounceName", Icon = "Keyboard", Category = SettingsCategory.MouseKeyboard, Order = 2,
            FeatureIds = [FeatureIds.KeyboardDebounce],
            CreateView = sp => new KeyDebounceSettingsPage(sp),
            KeywordKeys = ["Strings.keyDebounceEnable", "Strings.keyDebouncePerKeySection"],
            Keywords = ["chatter", "bounce"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = SuperKeyPageId, TitleKey = "superKey.pageTitle", Icon = "KeyboardShift", Category = SettingsCategory.MouseKeyboard, Order = 3,
            FeatureIds = [FeatureIds.SuperKey],
            CreateView = sp => new SuperKeySettingsPage(sp),
            KeywordKeys = ["superKey.capsLockKey", "superKey.soloSection"],
            Keywords = ["hyper", "Caps Lock", "meh"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = QuitProtectionPageId, TitleKey = "quitProtection.name", Icon = "Shield", Category = SettingsCategory.MouseKeyboard, Order = 4,
            FeatureIds = [FeatureIds.QuitWindowProtection],
            CreateView = sp => new QuitProtectionSettingsPage(sp),
            KeywordKeys = ["quitProtection.hold", "quitProtection.doublePress"],
            Keywords = ["Alt+F4", "Ctrl+W", "Ctrl+Q"],
        });
    }

    /// <summary>Command Bar toggles for the switches (the panel rows are the other way in).</summary>
    private static void AddActions(ModuleContext context)
    {
        var settings = context.Settings;
        void Toggle(string id, string featureId, string titleKey, string icon, Setting<bool> setting) =>
            context.Actions.Register(new AppAction
            {
                Id = id, FeatureId = featureId, TitleKey = titleKey, Icon = icon, ClosesPanel = false,
                Run = _ =>
                {
                    settings.Set(setting, !settings.Get(setting));
                    return Task.CompletedTask;
                },
            });

        Toggle("input.toggleScrollInverter", FeatureIds.ScrollInverter, "Strings.invertMouseScroll", "ArrowSort", FeatureKeys.ScrollInverterEnabled);
        Toggle("input.toggleSmoothScroll", FeatureIds.SmoothScroll, "Strings.smoothScrollName", "CursorHover", FeatureKeys.SmoothScrollEnabled);
        Toggle("input.toggleClickFilter", FeatureIds.MouseClickDebounce, "mouseClickDebounce.title", "CursorClick", FeatureKeys.MouseClickDebounceEnabled);
        Toggle("input.toggleKeyDebounce", FeatureIds.KeyboardDebounce, "Strings.keyDebounceName", "Keyboard", FeatureKeys.KeyboardDebounceEnabled);
        Toggle("input.toggleMouseButtons", FeatureIds.MouseButtonShortcuts, "mouseButtons.pageTitle", "ControlButton", FeatureKeys.MouseButtonShortcutsEnabled);
        Toggle("input.toggleSuperKey", FeatureIds.SuperKey, "superKey.pageTitle", "KeyboardShift", FeatureKeys.SuperKeyEnabled);
    }
}
