// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.App.Tests.Capture;

/// <summary>
/// For tests whose flows open the shared chooser in the test host (the
/// recorder's toggle, for one): answers it the way a user would. Start the
/// flow without awaiting it, answer, then await:
/// <c>var start = controller.ToggleAsync(); ChooserDriver.ConfirmDisplay(); await start;</c>
/// </summary>
public static class ChooserDriver
{
    /// <summary>Waits for the host's chooser, then takes the whole display under the pointer (Enter).</summary>
    public static void ConfirmDisplay(double seconds = 10) => Answer(SelectorKey.Enter, seconds);

    /// <summary>Waits for the host's chooser, then cancels it (Esc).</summary>
    public static void Cancel(double seconds = 10) => Answer(SelectorKey.Escape, seconds);

    private static void Answer(SelectorKey key, double seconds)
    {
        var coordinator = TestApp.Host.Services.GetRequiredService<CaptureCoordinator>();
        SelectorSessionTests.Pump(() => coordinator.Session is { Overlays.Count: > 0 }, seconds);
        coordinator.Session!.OnKey(key, KeyAction.Down, KeyModifiers.None);
    }
}

public class ChooserDriverTests
{
    [AvaloniaFact]
    public void A_flow_waiting_on_the_chooser_gets_the_display_under_the_pointer()
    {
        var selector = TestApp.Host.Services.GetRequiredService<ICaptureSelector>();
        var screens = TestApp.Host.Services.GetRequiredService<IScreenService>();
        var run = selector.SelectAsync(new CaptureSelectorRequest { Tool = CaptureTool.Recording, AllowToolSwitching = false });
        ChooserDriver.ConfirmDisplay();
        SelectorSessionTests.Pump(() => run.IsCompleted);

        var selection = run.Result;
        Assert.NotNull(selection);
        Assert.Equal(CaptureTargetKind.Display, selection!.Kind);
        Assert.Equal(screens.ScreenFromPoint(screens.CursorPosition).Id, selection.ScreenId);
        Assert.False(selector.IsActive);
    }
}
