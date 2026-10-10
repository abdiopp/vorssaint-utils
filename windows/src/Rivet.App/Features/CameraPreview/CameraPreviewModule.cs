// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Scratchpad;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Modules.CameraPreview;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.CameraPreview;

/// <summary>Camera preview: a floating mirrored camera view for checking yourself before a call.</summary>
public sealed class CameraPreviewModule : IFeatureModule
{
    // Built by concatenation so the string-key test does not mistake the ids for catalog keys.
    private const string ActionPrefix = "cameraPreview";
    public const string ToggleActionId = ActionPrefix + ".toggle";
    public const string OpenActionId = ActionPrefix + ".open";
    public const string PageId = "cameraPreview";

    public static readonly ShortcutRole Role = new()
    {
        Id = "cameraPreview",
        FeatureId = FeatureIds.CameraPreview,
        TitleKey = "cameraPreview.pageTitle",
        Storage = CameraPreviewSettings.Shortcut,
        Default = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Letter('W')),
        RequiredEnableKeys = [CameraPreviewSettings.ShortcutEnabled],
        ActionId = ToggleActionId,
    };

    public string Id => "cameraPreview";

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<CameraPreviewService>();

    public void Initialize(ModuleContext context)
    {
        var service = context.Get<CameraPreviewService>();
        context.Features.RegisterController(FeatureIds.CameraPreview, new DelegateFeatureController(available =>
        {
            if (!available)
            {
                service.Hide(CameraHideReason.Uninstall);
            }
        }));

        context.Actions.Register(new AppAction
        {
            Id = ToggleActionId,
            FeatureId = FeatureIds.CameraPreview,
            TitleKey = "cameraPreview.pageTitle",
            Icon = "Camera",
            Run = _ =>
            {
                service.Toggle();
                return Task.CompletedTask;
            },
        });
        context.Actions.Register(new AppAction
        {
            Id = OpenActionId,
            FeatureId = FeatureIds.CameraPreview,
            TitleKey = "cameraPreview.pageTitle",
            SubtitleKey = "cameraPreview.panelCaption",
            Icon = "Camera",
            Keywords = ["camera", "webcam", "mirror", "video call"],
            Run = async ctx =>
            {
                // Other surfaces hide first; the mirror shows 0.15 s later.
                if (ctx.Source is ActionSource.CommandBar or ActionSource.QuickPanel or ActionSource.RadialMenu)
                {
                    await Task.Delay(CameraPreviewLayout.LaunchDelay).ConfigureAwait(true);
                }

                service.Show();
            },
        });

        context.Shortcuts.Register(Role);

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "cameraPreview",
            FeatureId = FeatureIds.CameraPreview,
            TitleKey = "cameraPreview.pageTitle",
            CaptionKey = "cameraPreview.panelCaption",
            Icon = "Camera",
            Order = 45,
            ActionId = OpenActionId,
            ShortcutRoleId = Role.Id,
            SettingsPageId = PageId,
            LiveCaption = () => service.AccessDenied ? L.Get("win.cameraPreview.permissionRequired") : null,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = PageId,
            TitleKey = "cameraPreview.pageTitle",
            Icon = "Camera",
            Category = SettingsCategory.Capture,
            Order = 60,
            FeatureIds = [FeatureIds.CameraPreview],
            CreateView = sp => new CameraPreviewSettingsPage(sp),
            KeywordKeys = ["cameraPreview.openButton", "cameraPreview.permName"],
            Keywords = ["camera", "webcam", "mirror"],
        });
    }
}

/// <summary>Settings › Capture › Camera preview.</summary>
public sealed class CameraPreviewSettingsPage : SettingsPage
{
    private readonly CameraPreviewService _service;
    private bool? _shownDenied;

    public CameraPreviewSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _service = services.GetRequiredService<CameraPreviewService>();
        Build();
        _service.AccessChanged += OnAccessChanged;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _service.AccessChanged -= OnAccessChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAccessChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Build);

    /// <summary>The "Camera / Not granted" row exists only while access is off (cards draw a divider per row).</summary>
    private void Build()
    {
        var denied = _service.AccessDenied;
        if (_shownDenied == denied)
        {
            return;
        }

        _shownDenied = denied;
        Content = Stack(
            Header("cameraPreview.pageTitle", "cameraPreview.hubDescription"),
            Card(null,
                Row("Camera", L.Get("cameraPreview.openButton"), L.Get("cameraPreview.panelCaption"), ActionButton(L.Get("cameraPreview.openButton"), _service.Show, "Open", accent: true)),
                denied
                    ? Row("Warning", L.Get("cameraPreview.permName"), L.Get("win.cameraPreview.notGranted"), ActionButton(L.Get("win.cameraPreview.openPrivacySettings"), _service.OpenPrivacySettings, "Open"))
                    : null,
                Row("ShieldCheckmark", L.Get("win.cameraPreview.privacyTitle"), L.Get("win.cameraPreview.privacyCaption")),
                Row("Dismiss", L.Get("win.cameraPreview.dismissTitle"), L.Get("win.cameraPreview.dismissCaption"))),
            Card("Strings.shortcutsPageTitle",
                Toggle(CameraPreviewSettings.ShortcutEnabled, "Keyboard", "win.cameraPreview.shortcutToggle"),
                new ShortcutRoleRow(CameraPreviewModule.Role)));
    }
}
