// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Capture;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.App.Tests.Capture;

/// <summary>The module's registrations in the real host.</summary>
public class CaptureModuleTests
{
    private static IServiceProvider Services => TestApp.Host.Services;

    [AvaloniaFact]
    public void Contracts_resolve_to_this_module()
    {
        Assert.IsType<CaptureCoordinator>(Services.GetRequiredService<ICaptureSelector>());
        Assert.NotNull(Services.GetRequiredService<ICaptureOutput>());
        Assert.NotNull(Services.GetRequiredService<IRecentCaptures>());
    }

    [AvaloniaFact]
    public void Actions_roles_tiles_and_pages_are_registered()
    {
        var actions = Services.GetRequiredService<ActionRegistry>();
        foreach (var id in new[] { CaptureModule.CaptureActionId, CaptureModule.FullScreenActionId, CaptureModule.ScrollingActionId, CaptureModule.EditLatestActionId,
                     CaptureModule.EditClipboardActionId, CaptureModule.RecentActionId, CaptureModule.OcrActionId, CaptureModule.ColorActionId })
        {
            Assert.NotNull(actions.Get(id));
        }

        Assert.Equal("screenshot.capture", CaptureModule.CaptureActionId);
        Assert.Equal("colorPicker.pick", CaptureModule.ColorActionId);

        var shortcuts = Services.GetRequiredService<ShortcutManager>();
        var screenshot = shortcuts.Find(CaptureModule.ScreenshotRoleId)!;
        Assert.Equal(KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Digit(4)), screenshot.Default);
        Assert.Equal(HotkeyOptions.OverrideSystem, shortcuts.Find(CaptureModule.PrintScreenRoleId)!.Options);
        Assert.All(new[] { CaptureModule.FullScreenRoleId, CaptureModule.LastCaptureRoleId, CaptureModule.ClipboardRoleId, CaptureModule.RecentCapturesRoleId, CaptureModule.OcrRoleId, CaptureModule.ColorRoleId },
            id => Assert.NotNull(shortcuts.Find(id)));

        // This module's defaults collide with no other role's default (defaults skip the recorder's checks).
        var mine = new[] { CaptureModule.ScreenshotRoleId, CaptureModule.FullScreenRoleId, CaptureModule.LastCaptureRoleId, CaptureModule.ClipboardRoleId,
            CaptureModule.RecentCapturesRoleId, CaptureModule.PrintScreenRoleId, CaptureModule.OcrRoleId, CaptureModule.ColorRoleId };
        foreach (var role in shortcuts.Roles.Where(r => mine.Contains(r.Id)))
        {
            Assert.False(role.Default.IsEmpty, role.Id);
            Assert.DoesNotContain(shortcuts.Roles, other => other.Id != role.Id && other.Default == role.Default);
        }
        Assert.All(shortcuts.Roles.Where(r => r.Id is CaptureModule.ScreenshotRoleId or CaptureModule.OcrRoleId or CaptureModule.ColorRoleId),
            r => Assert.False(ReservedShortcuts.IsReserved(r.Default)));

        var panel = Services.GetRequiredService<PanelRegistry>();
        Assert.Contains(panel.Tiles, t => t.Id == "screenshot" && t.ActionId == CaptureModule.CaptureActionId && t.Accessory?.CreateHostedView is not null);
        Assert.Contains(panel.Tiles, t => t.Id == "screenOCR" && t.FeatureId == FeatureIds.ScreenOcr);
        Assert.Contains(panel.Tiles, t => t.Id == "colorPicker" && t.FeatureId == FeatureIds.ColorPicker);
        Assert.All(panel.Tiles.Where(t => t.FeatureId is FeatureIds.Screenshot or FeatureIds.ScreenOcr or FeatureIds.ColorPicker), t => Assert.True(IconConverter.IsKnown(t.Icon), t.Icon));

        var pages = Services.GetRequiredService<SettingsPageRegistry>();
        Assert.Equal(CaptureModule.ScreenshotPageId, pages.ForFeature(FeatureIds.Screenshot)!.Id);
        Assert.Equal(CaptureModule.OcrPageId, pages.ForFeature(FeatureIds.ScreenOcr)!.Id);
        Assert.Equal(CaptureModule.ColorPageId, pages.ForFeature(FeatureIds.ColorPicker)!.Id);
        Assert.All(new[] { CaptureModule.ScreenshotPageId, CaptureModule.OcrPageId, CaptureModule.ColorPageId }, id => Assert.Equal(SettingsCategory.Capture, pages.Find(id)!.Category));
    }
}
