// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.ColorPicking;
using Rivet.App.Features.Capture.Hud;
using Rivet.App.Features.Capture.Output;
using Rivet.App.Features.Capture.Preview;
using Rivet.App.Features.Capture.Scrolling;
using Rivet.App.Features.Capture.Selector;
using Rivet.App.Features.Capture.Text;
using Rivet.Core.Actions;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture;

/// <summary>
/// Entry points of the capture tools (spec 01 §3.2, §3.3): one capture
/// surface at a time, the countdown (a repeat press cancels it), the tools
/// on offer, and where each confirmed result goes — screenshots to the
/// routing pipeline, text to OCR, colours to the clipboard, regions to the
/// recorder or to scrolling capture.
/// </summary>
internal sealed class CaptureCoordinator(IServiceProvider services) : ICaptureSelector
{
    /// <summary>Actions the recorder might register to start recording (see the module doc).</summary>
    private static readonly string[] RecorderActionIds =
    [
        FeatureIds.ScreenRecorder + ".toggle", FeatureIds.ScreenRecorder + ".start", FeatureIds.ScreenRecorder + ".record",
        RecorderPrefix + ".toggle", RecorderPrefix + ".start",
    ];

    private const string RecorderPrefix = "recorder";

    private readonly ISettingsStore _settings = services.GetRequiredService<ISettingsStore>();
    private readonly FeatureRuntime _runtime = services.GetRequiredService<FeatureRuntime>();
    private SelectorSession? _session;
    private CancellationTokenSource? _countdown;
    private CountdownWindow? _countdownWindow;
    private (CaptureSelection Selection, DateTime Until)? _pendingRecording;

    public bool IsActive => _session is not null || _countdown is not null;

    /// <summary>The chooser on screen, if any (tests).</summary>
    internal SelectorSession? Session => _session;

    /// <summary>The recorder's state and hand-off; absent without the recording module.</summary>
    private IRecorderLink? Recorder => services.GetService<IRecorderLink>();

    /// <summary>The tools offered right now: installed features in the fixed order.</summary>
    public IReadOnlyList<CaptureTool> AvailableTools()
    {
        var tools = new List<CaptureTool>(4);
        if (_runtime.IsAvailable(FeatureIds.Screenshot)) tools.Add(CaptureTool.Screenshot);
        if (_runtime.IsAvailable(FeatureIds.ScreenRecorder)) tools.Add(CaptureTool.Recording);
        if (_runtime.IsAvailable(FeatureIds.ScreenOcr)) tools.Add(CaptureTool.Text);
        if (_runtime.IsAvailable(FeatureIds.ColorPicker)) tools.Add(CaptureTool.Color);
        return tools;
    }

    /// <summary>Cancels a countdown or an open chooser (feature uninstalled, the set of tools changed).</summary>
    public void CancelAll()
    {
        CancelCountdown();
        _session?.Cancel();
        services.GetService<ScrollingCaptureController>()?.Cancel();
    }

    // ── Chooser entry points ────────────────────────────────────────────
    /// <summary>Opens the chooser on <paramref name="tool"/> (§3.3).</summary>
    public async Task StartAsync(CaptureTool tool, ActionSource source)
    {
        var scrolling = services.GetRequiredService<ScrollingCaptureController>();
        if (scrolling.IsRunning)
        {
            scrolling.Finish();
            return;
        }

        // Over a recording only another tool's own shortcut without the menu opens
        // the chooser, offering just that tool: Recording picked from a menu would stop the take (§3.3 step 2).
        var duringRecording = Recorder?.IsBusy == true;
        if (duringRecording && (tool == CaptureTool.Recording || source != ActionSource.Shortcut || ShowMenuPreference(tool)))
        {
            return;
        }

        if (_countdown is not null)
        {
            CancelCountdown();
            return;
        }

        if (_session is not null)
        {
            return;
        }

        var available = AvailableTools();
        if (available.Count == 0)
        {
            return;
        }

        var selected = available.Contains(tool) ? tool : available.Contains(CaptureTool.Screenshot) ? CaptureTool.Screenshot : available[0];
        if (duringRecording && selected != tool)
        {
            return;
        }

        IReadOnlyList<CaptureTool> tools = duringRecording ? [selected] : available;
        var showMenu = source != ActionSource.Shortcut || ShowMenuPreference(selected);
        services.GetRequiredService<QuickPreviewController>().Close();
        await ClosePanelAsync();
        if (selected == CaptureTool.Screenshot && !await CountdownAsync())
        {
            return;
        }

        var outcome = await RunSessionAsync(new SelectorRequest { Tools = tools, Tool = selected, ShowPalette = showMenu });
        if (outcome is not null)
        {
            await DeliverAsync(outcome, requestedTool: null);
        }
    }

    /// <summary>"Capture the whole screen" with no chooser (§3.6).</summary>
    public async Task FullScreenAsync()
    {
        var scrolling = services.GetRequiredService<ScrollingCaptureController>();
        if (scrolling.IsRunning)
        {
            scrolling.Finish();
            return;
        }

        if (_countdown is not null)
        {
            CancelCountdown();
            return;
        }

        if (_session is not null)
        {
            return;
        }

        await ClosePanelAsync();
        if (!await CountdownAsync())
        {
            return;
        }

        services.GetRequiredService<QuickPreviewController>().Close();
        var screens = services.GetRequiredService<IScreenService>();
        var display = screens.ScreenFromPoint(screens.CursorPosition);
        // Content windows (pins) follow the "Hide app windows" setting for screenshots (§3.1).
        var options = new DisplayCaptureOptions
        {
            IncludeCursor = _settings.Get(CaptureSettings.IncludePointer),
            HideOwnWindows = _settings.Get(CaptureSettings.HideOwnWindows),
        };
        IReadOnlyDictionary<string, PixelBuffer> captured;
        try
        {
            captured = await services.GetRequiredService<IScreenCapturer>().CaptureDisplaysAsync([display], options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("capture", "Full-screen capture failed.", ex);
            captured = new Dictionary<string, PixelBuffer>();
        }

        if (!captured.TryGetValue(display.Id, out var pixels))
        {
            CaptureFailed();
            return;
        }

        await services.GetRequiredService<CaptureRouter>().RouteAsync(
            new CapturedImage(CaptureImaging.WithScale(pixels, display.Scale), display.Bounds) { Kind = CaptureTargetKind.Display });
    }

    /// <summary>The standalone scrolling capture: a live selector, then the stitcher (§3.15.1).</summary>
    public async Task ScrollingAsync()
    {
        var scrolling = services.GetRequiredService<ScrollingCaptureController>();
        if (scrolling.IsRunning)
        {
            scrolling.Finish();
            return;
        }

        if (_countdown is not null)
        {
            CancelCountdown();
            return;
        }

        if (_session is not null)
        {
            return;
        }

        services.GetRequiredService<QuickPreviewController>().Close();
        await ClosePanelAsync();
        var outcome = await RunSessionAsync(new SelectorRequest
        {
            Tools = [CaptureTool.Screenshot],
            Tool = CaptureTool.Screenshot,
            ShowPalette = false,
            Standalone = true,
            ForceGeometry = true,
            ForceLive = true,
            Purpose = L.Get("screenshot.scrollingCaptureTitle"),
        });
        if (outcome is not null)
        {
            await RunScrollingAsync(outcome.Display, outcome.Region);
        }
    }

    // ── ICaptureSelector (the recorder and other modules) ───────────────
    public async Task<CaptureSelection?> SelectAsync(CaptureSelectorRequest request, CancellationToken cancellationToken = default)
    {
        // A recording region the user already chose in our chooser is handed over without a second selection.
        if (request.Tool == CaptureTool.Recording && _pendingRecording is { } pending)
        {
            _pendingRecording = null;
            if (DateTime.UtcNow <= pending.Until)
            {
                return pending.Selection;
            }
        }

        // Over a recording only a menu-less request for another tool opens, offering just that tool
        // (§3.3 step 2). The recorder's own Recording request arrives while it prepares, so it is never held back here.
        var duringRecording = request.Tool != CaptureTool.Recording && Recorder?.IsBusy == true;
        if ((duringRecording && request.AllowToolSwitching) || _session is not null || _countdown is not null)
        {
            return null;
        }

        var available = AvailableTools();
        IReadOnlyList<CaptureTool> tools = duringRecording ? [request.Tool]
            : available.Contains(request.Tool) ? available
            : [.. available, request.Tool];

        // The caller decides whether the palette shows (the recorder applies its own
        // "show capture menu" preference); the digit keys switch tools either way, as in every chooser.
        await ClosePanelAsync();
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => _session?.Cancel()));
        var outcome = await RunSessionAsync(new SelectorRequest { Tools = tools, Tool = request.Tool, ShowPalette = request.AllowToolSwitching });
        if (outcome is null)
        {
            return null;
        }

        if (outcome.Tool != request.Tool)
        {
            // The user switched tools: this result is ours to handle.
            await DeliverAsync(outcome, requestedTool: request.Tool);
            return null;
        }

        return ToSelection(outcome);
    }

    // ── Internals ──────────────────────────────────────────────────────
    private async Task<SelectorOutcome?> RunSessionAsync(SelectorRequest request)
    {
        var session = new SelectorSession(services, request);
        _session = session;
        try
        {
            return await session.RunAsync();
        }
        finally
        {
            _session = null;
        }
    }

    private async Task DeliverAsync(SelectorOutcome outcome, CaptureTool? requestedTool)
    {
        // A result whose tool was uninstalled in the meantime is dropped.
        var feature = outcome.Tool switch
        {
            CaptureTool.Recording => FeatureIds.ScreenRecorder,
            CaptureTool.Text => FeatureIds.ScreenOcr,
            CaptureTool.Color => FeatureIds.ColorPicker,
            _ => FeatureIds.Screenshot,
        };
        if (!_runtime.IsAvailable(feature))
        {
            return;
        }

        switch (outcome.Tool)
        {
            case CaptureTool.Screenshot when outcome.Scrolling:
                await RunScrollingAsync(outcome.Display, CaptureGeometry.SnapRegion(outcome.Region, outcome.Display.Bounds));
                break;
            case CaptureTool.Screenshot when outcome.Image is { } image:
                await services.GetRequiredService<CaptureRouter>().RouteAsync(image);
                break;
            case CaptureTool.Text when outcome.Image is { } text:
                await services.GetRequiredService<ScreenTextService>().ProcessAsync(text.Image);
                break;
            case CaptureTool.Color when outcome.Color is { } color:
                services.GetRequiredService<ColorPickService>().Copy(color);
                break;
            case CaptureTool.Recording when requestedTool != CaptureTool.Recording:
                await HandOverToRecorderAsync(ToSelection(outcome));
                break;
        }
    }

    private async Task RunScrollingAsync(ScreenInfo display, PixelRect region)
    {
        var image = await services.GetRequiredService<ScrollingCaptureController>().RunAsync(display, region);
        if (image is not null)
        {
            await services.GetRequiredService<CaptureRouter>().RouteAsync(image);
        }
    }

    /// <summary>
    /// Recording chosen in a chooser another tool opened: give the region to the
    /// recorder directly; failing that, keep it for the recorder's next
    /// <see cref="SelectAsync"/> and start the recorder through its action.
    /// </summary>
    private async Task HandOverToRecorderAsync(CaptureSelection selection)
    {
        if (Recorder is { } recorder && await recorder.TryRecordSelectionAsync(selection))
        {
            return;
        }

        var actions = services.GetRequiredService<ActionRegistry>();
        var action = RecorderActionIds.Select(actions.Get).FirstOrDefault(a => a is not null)
                     ?? actions.All.FirstOrDefault(a => a.FeatureId == FeatureIds.ScreenRecorder
                                                        && (a.Id.EndsWith(".toggle", StringComparison.Ordinal) || a.Id.EndsWith(".start", StringComparison.Ordinal) || a.Id.EndsWith(".record", StringComparison.Ordinal)));
        if (action is null)
        {
            services.GetRequiredService<IHud>().Show(L.Get("recorder.recordFailed"), HudStyle.Error, "ErrorCircle");
            return;
        }

        _pendingRecording = (selection, DateTime.UtcNow.AddSeconds(5));
        await actions.InvokeAsync(action.Id, ActionSource.Other);
    }

    private static CaptureSelection ToSelection(SelectorOutcome outcome) => new()
    {
        Tool = outcome.Tool,
        Kind = outcome.Kind switch
        {
            SelectorActionKind.ConfirmWindow => CaptureTargetKind.Window,
            SelectorActionKind.ConfirmDisplay => CaptureTargetKind.Display,
            _ => CaptureTargetKind.Area,
        },
        Bounds = outcome.Region,
        ScreenId = outcome.Display.Id,
        WindowHandle = outcome.Window?.Handle ?? 0,
        FrozenImage = outcome.Image?.Image,
        PickedColor = outcome.Color,
    };

    private bool ShowMenuPreference(CaptureTool tool) => tool switch
    {
        CaptureTool.Text => _settings.Get(CaptureSettings.ScreenOcrShowCaptureMenu),
        CaptureTool.Color => _settings.Get(CaptureSettings.ColorPickerShowCaptureMenu),
        CaptureTool.Recording => _settings.Get(CaptureSettings.RecorderShowCaptureMenu),
        _ => _settings.Get(CaptureSettings.ScreenshotShowCaptureMenu),
    };

    /// <summary>The optional countdown before a screenshot (§3.3 step 9); false when cancelled.</summary>
    private async Task<bool> CountdownAsync()
    {
        var seconds = _settings.Get(CaptureSettings.Delay);
        if (seconds <= 0)
        {
            return true;
        }

        var cts = new CancellationTokenSource();
        _countdown = cts;
        _countdownWindow = new CountdownWindow();
        try
        {
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                _countdownWindow.ShowValue(remaining);
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _countdownWindow?.Stop();
            _countdownWindow = null;
            if (ReferenceEquals(_countdown, cts))
            {
                _countdown = null;
            }

            cts.Dispose();
            // Give the HUD a frame to leave the screen before anything is photographed.
            await Task.Delay(60);
        }
    }

    /// <summary>The tray panel never belongs in a capture: close it and let it leave the screen first.</summary>
    private async Task ClosePanelAsync()
    {
        if (services.GetService<Rivet.App.Modules.IAppShell>() is { IsPanelOpen: true } shell)
        {
            shell.ClosePanel();
            await Task.Delay(150);
        }
    }

    private void CancelCountdown()
    {
        var cts = _countdown;
        _countdown = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CaptureFailed()
    {
        services.GetRequiredService<IHud>().Show(L.Get("screenshot.captureFailed"), HudStyle.Error, "ErrorCircle");
        services.GetRequiredService<ICapturePlatform>().Beep();
    }
}
