// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Contracts;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Capture;

/// <summary>What the selector returns (spec 01 §3.4.2).</summary>
public enum SelectorMode
{
    /// <summary>Pixels (screenshots, text recognition).</summary>
    Image,

    /// <summary>A region (recording, scrolling capture).</summary>
    Geometry,

    /// <summary>One pixel's colour.</summary>
    Color,
}

/// <summary>How a tool acquires pixels; tools with equal policies share the frozen photographs.</summary>
public sealed record SelectorSourcePolicy(bool Freeze, bool IncludePointer, bool HideOwnWindows, bool KeepContentOut);

/// <summary>Keys the selector understands (matched by virtual key; letters follow the active layout).</summary>
public enum SelectorKey
{
    Escape,
    Enter,
    Space,
    R,
    S,
    Z,
    C,
    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Left,
    Right,
    Up,
    Down,
}

public enum SelectorActionKind
{
    Cancel,
    ConfirmRegion,
    ConfirmWindow,
    ConfirmDisplay,
    PickColor,
    CopyColor,
    SwitchTool,
    MovePointer,
}

/// <summary>Something the session must do in response to input.</summary>
public sealed record SelectorAction
{
    public required SelectorActionKind Kind { get; init; }

    public string? DisplayId { get; init; }

    /// <summary>Region (global physical pixels) for <see cref="SelectorActionKind.ConfirmRegion"/>.</summary>
    public RectD Region { get; init; }

    public CaptureWindowInfo? Window { get; init; }

    /// <summary>Pixel position (global) for colour actions and pointer moves.</summary>
    public PointD Point { get; init; }

    public CaptureTool Tool { get; init; }

    /// <summary>The new tool needs fresh photographs (different source policy).</summary>
    public bool NeedsRefresh { get; init; }

    /// <summary>The region starts a scrolling capture.</summary>
    public bool Scrolling { get; init; }
}

/// <summary>Configuration of one selector session.</summary>
public sealed record SelectorConfig
{
    /// <summary>Tools offered, in the fixed order Screenshot, Recording, Text, Color.</summary>
    public required IReadOnlyList<CaptureTool> Tools { get; init; }

    public required CaptureTool InitialTool { get; init; }

    /// <summary>Show the 1–4 mode palette in the hint bar.</summary>
    public bool ShowPalette { get; init; } = true;

    /// <summary>The single-plate variant used by the standalone scrolling capture.</summary>
    public bool Standalone { get; init; }

    /// <summary>Only drags confirm (no window clicks, no full screen).</summary>
    public bool RegionOnly { get; init; }

    /// <summary>Screenshot mode offers S for scrolling capture.</summary>
    public bool ScrollingSupported { get; init; } = true;

    /// <summary>Force geometry mode whatever the tool (standalone scrolling capture).</summary>
    public bool ForceGeometry { get; init; }

    /// <summary>Title of the standalone plate ("Scrolling screenshot").</summary>
    public string? Purpose { get; init; }

    public bool LoupeStartsOn { get; init; }

    public double InitialZoom { get; init; } = 1;

    public bool SteppedZoomByDefault { get; init; }

    public bool ShowLastRegion { get; init; } = true;

    /// <summary>The regular policy settings for the Screenshot tool.</summary>
    public SelectorSourcePolicy ScreenshotPolicy { get; init; } = new(true, false, true, true);

    public bool HideOwnWindowsSetting { get; init; } = true;
}

/// <summary>
/// The capture selector's state machine (spec 01 §3.4), independent of any
/// UI: pointer and key events in global physical pixels go in, actions come
/// out. Every overlay window renders from the same model.
/// </summary>
public sealed class SelectorModel
{
    private static (string DisplayId, RectD LocalRect)? _lastRegion;

    private readonly Dictionary<string, ScreenInfo> _displays;
    private IReadOnlyList<CaptureWindowInfo> _windows = [];
    private PointD _dragOrigin;
    private PointD _dragCurrent;
    private PointD _spaceAnchor;
    private bool _pressed;
    private string? _dragDisplayId;
    private DateTime _copiedUntil;

    public SelectorModel(SelectorConfig config, IReadOnlyList<ScreenInfo> displays, PointD pointer)
    {
        Config = config;
        _displays = displays.ToDictionary(d => d.Id, StringComparer.Ordinal);
        Displays = displays;
        Tool = config.Tools.Contains(config.InitialTool) ? config.InitialTool : config.Tools[0];
        Zoom = LoupeMath.Clamp(config.InitialZoom);
        LoupeOn = Tool == CaptureTool.Color || config.LoupeStartsOn;
        SetPointer(pointer);
    }

    public SelectorConfig Config { get; }

    public IReadOnlyList<ScreenInfo> Displays { get; }

    public CaptureTool Tool { get; private set; }

    public SelectorMode Mode => ModeFor(Tool);

    public PointD Pointer { get; private set; }

    public string PointerDisplayId { get; private set; } = string.Empty;

    public ScreenInfo PointerDisplay => _displays[PointerDisplayId];

    public bool IsDragging => _pressed;

    /// <summary>Space held during a drag: the selection moves instead of resizing.</summary>
    public bool IsSpaceMoving { get; private set; }

    /// <summary>The current selection (global physical pixels), while dragging.</summary>
    public RectD? Selection { get; private set; }

    public string? SelectionDisplayId => Selection is null ? null : _dragDisplayId;

    public bool LoupeOn { get; private set; }

    public double Zoom { get; private set; }

    public bool Scrolling { get; private set; }

    public bool CapturePending { get; private set; }

    public bool Ended { get; private set; }

    public bool RefreshPending { get; private set; }

    /// <summary>The overlay showing the full-screen pill reports hover so the window highlight steps aside.</summary>
    public bool PointerOverPill { get; set; }

    /// <summary>The colour value shown with a check mark after C (1.4 s).</summary>
    public string? CopiedValue { get; private set; }

    public bool IsInert => CapturePending || Ended;

    public bool AllowsWindowClicks => Mode != SelectorMode.Color && !Config.RegionOnly && !Scrolling;

    public SelectorSourcePolicy Policy => PolicyFor(Tool);

    public static void ResetLastRegion() => _lastRegion = null;

    public SelectorMode ModeFor(CaptureTool tool) => tool switch
    {
        _ when Config.ForceGeometry => SelectorMode.Geometry,
        CaptureTool.Recording => SelectorMode.Geometry,
        CaptureTool.Color => SelectorMode.Color,
        _ => SelectorMode.Image,
    };

    /// <summary>Source policy per tool (§3.4.2 table).</summary>
    public SelectorSourcePolicy PolicyFor(CaptureTool tool) => tool switch
    {
        CaptureTool.Screenshot when Config.ForceGeometry => Config.ScreenshotPolicy with { IncludePointer = false },
        CaptureTool.Screenshot => Config.ScreenshotPolicy,
        CaptureTool.Recording => new SelectorSourcePolicy(true, false, false, true),
        _ => new SelectorSourcePolicy(true, false, Config.HideOwnWindowsSetting, Config.HideOwnWindowsSetting),
    };

    /// <summary>Pickable windows for the current source (rebuilt after every refresh).</summary>
    public IReadOnlyList<CaptureWindowInfo> Windows
    {
        get => _windows;
        set => _windows = value;
    }

    /// <summary>The window under the pointer that would be captured by a click.</summary>
    public CaptureWindowInfo? HighlightedWindow =>
        IsInert || _pressed || Selection is not null || !AllowsWindowClicks || PointerOverPill || RefreshPending
            ? null
            : WindowPicking.HitTest(_windows, Pointer.Floor());

    public bool HintBarVisible(string displayId) => displayId == PointerDisplayId && !_pressed && !IsInert;

    public bool FullScreenPillAvailable =>
        Tool == CaptureTool.Screenshot && !Config.RegionOnly && !Scrolling && !RefreshPending && !Config.ForceGeometry;

    public bool FullScreenPillVisible(string displayId) => FullScreenPillAvailable && HintBarVisible(displayId);

    public bool LoupeVisible(string displayId) => LoupeOn && displayId == PointerDisplayId && !IsSpaceMoving && !IsInert;

    /// <summary>R works when the remembered region's display is still here and the mode is not Color.</summary>
    public bool RepeatAvailable => Mode != SelectorMode.Color && _lastRegion is { } last && _displays.ContainsKey(last.DisplayId);

    /// <summary>The dashed outline of the last region on this display, when nothing is being selected.</summary>
    public RectD? GhostRect(string displayId)
    {
        if (!Config.ShowLastRegion || _pressed || Selection is not null || _lastRegion is not { } last || last.DisplayId != displayId
            || !_displays.TryGetValue(displayId, out var display))
        {
            return null;
        }

        return last.LocalRect.Offset(display.Bounds.X, display.Bounds.Y);
    }

    public bool IsCopiedFeedbackActive(DateTime now) => CopiedValue is not null && now < _copiedUntil;

    public void ShowCopied(string value, DateTime now)
    {
        CopiedValue = value;
        _copiedUntil = now.AddSeconds(LoupeMath.CopiedFeedbackSeconds);
    }

    // ── Pointer ─────────────────────────────────────────────────────────
    public void PointerMoved(PointD point, KeyModifiers modifiers)
    {
        if (Ended)
        {
            return;
        }

        if (_pressed && Mode != SelectorMode.Color && !IsInert)
        {
            if (IsSpaceMoving)
            {
                var delta = point - _spaceAnchor;
                _spaceAnchor = point;
                _dragOrigin += delta;
                _dragCurrent += delta;
            }
            else
            {
                _dragCurrent = point;
            }

            UpdateSelection(modifiers);
        }

        SetPointer(point);
    }

    public void PointerPressed(PointD point, KeyModifiers modifiers)
    {
        if (IsInert)
        {
            return;
        }

        SetPointer(point);
        _pressed = true;
        _dragOrigin = point;
        _dragCurrent = point;
        _dragDisplayId = PointerDisplayId;
        IsSpaceMoving = false;
        Selection = null;
    }

    public SelectorAction? PointerReleased(PointD point, KeyModifiers modifiers)
    {
        if (!_pressed)
        {
            return null;
        }

        if (IsInert)
        {
            _pressed = false;
            return null;
        }

        PointerMoved(point, modifiers);
        _pressed = false;
        IsSpaceMoving = false;
        var displayId = _dragDisplayId ?? PointerDisplayId;
        var scale = _displays.TryGetValue(displayId, out var display) ? display.Scale : 1;

        if (Mode == SelectorMode.Color)
        {
            Selection = null;
            return RefreshPending ? null : Confirm(new SelectorAction { Kind = SelectorActionKind.PickColor, DisplayId = PointerDisplayId, Point = Pointer });
        }

        if (CaptureGeometry.IsClick(_dragOrigin, point, scale))
        {
            Selection = null;
            if (!AllowsWindowClicks || RefreshPending)
            {
                return null;
            }

            var window = WindowPicking.HitTest(_windows, _dragOrigin.Floor());
            return window is null ? null : Confirm(new SelectorAction { Kind = SelectorActionKind.ConfirmWindow, Window = window, DisplayId = displayId });
        }

        var selection = Selection;
        Selection = null;
        if (selection is not { } rect || CaptureGeometry.IsTooSmall(rect, scale) || RefreshPending)
        {
            return null;
        }

        return ConfirmRegion(displayId, rect);
    }

    /// <summary>Wheel over the overlay zooms the loupe while it is on.</summary>
    public bool Wheel(double delta, bool continuous, bool altHeld)
    {
        if (!LoupeOn || IsInert || delta == 0)
        {
            return false;
        }

        var stepped = LoupeMath.UseStepped(Config.SteppedZoomByDefault, altHeld);
        var zoom = stepped ? LoupeMath.SteppedZoom(Zoom, delta) : LoupeMath.FastZoom(Zoom, delta, continuous);
        var changed = zoom != Zoom;
        Zoom = zoom;
        return changed;
    }

    // ── Keyboard ────────────────────────────────────────────────────────
    public SelectorAction? KeyDown(SelectorKey key, KeyModifiers modifiers)
    {
        if (Ended)
        {
            return null;
        }

        if (key == SelectorKey.Escape)
        {
            return Confirm(new SelectorAction { Kind = SelectorActionKind.Cancel });
        }

        if (CapturePending)
        {
            return null;
        }

        if (key == SelectorKey.Space)
        {
            if (_pressed && !IsSpaceMoving && Mode != SelectorMode.Color)
            {
                IsSpaceMoving = true;
                _spaceAnchor = Pointer;
            }

            return null;
        }

        if (key == SelectorKey.Enter)
        {
            if (_pressed || RefreshPending)
            {
                return null;
            }

            if (Mode == SelectorMode.Color)
            {
                return Confirm(new SelectorAction { Kind = SelectorActionKind.PickColor, DisplayId = PointerDisplayId, Point = Pointer });
            }

            return AllowsWindowClicks
                ? Confirm(new SelectorAction { Kind = SelectorActionKind.ConfirmDisplay, DisplayId = PointerDisplayId, Region = RectD.From(PointerDisplay.Bounds) })
                : null;
        }

        // Single keys never fire with Ctrl, Alt or Win held (those belong to other shortcuts).
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0)
        {
            return null;
        }

        switch (key)
        {
            case SelectorKey.Digit1:
            case SelectorKey.Digit2:
            case SelectorKey.Digit3:
            case SelectorKey.Digit4:
                var tool = (CaptureTool)(key - SelectorKey.Digit1);
                return Config.Tools.Contains(tool) && tool != Tool ? BeginSwitch(tool) : null;

            case SelectorKey.R:
                if (!RepeatAvailable || _pressed || RefreshPending || _lastRegion is not { } last || !_displays.TryGetValue(last.DisplayId, out var display))
                {
                    return null;
                }

                return ConfirmRegion(last.DisplayId, last.LocalRect.Offset(display.Bounds.X, display.Bounds.Y));

            case SelectorKey.S:
                if (Tool == CaptureTool.Screenshot && Mode == SelectorMode.Image && Config.ScrollingSupported && !Config.Standalone && !_pressed)
                {
                    Scrolling = !Scrolling;
                }

                return null;

            case SelectorKey.Z:
                if (Mode != SelectorMode.Color)
                {
                    LoupeOn = !LoupeOn;
                }

                return null;

            case SelectorKey.C:
                return LoupeOn && !_pressed && !RefreshPending
                    ? new SelectorAction { Kind = SelectorActionKind.CopyColor, DisplayId = PointerDisplayId, Point = Pointer }
                    : null;

            case SelectorKey.Left:
            case SelectorKey.Right:
            case SelectorKey.Up:
            case SelectorKey.Down:
                return LoupeOn && !_pressed ? Nudge(key, modifiers.HasFlag(KeyModifiers.Shift)) : null;
        }

        return null;
    }

    public void KeyUp(SelectorKey key)
    {
        if (key == SelectorKey.Space && IsSpaceMoving)
        {
            IsSpaceMoving = false;
            // Resizing resumes from the moved origin.
            _dragCurrent = Pointer;
        }
    }

    /// <summary>Applies a tool switch; when photographs must be refreshed, input stays blocked until <see cref="CompleteRefresh"/>.</summary>
    public SelectorAction BeginSwitch(CaptureTool tool)
    {
        var needsRefresh = PolicyFor(tool) != Policy;
        Tool = tool;
        Scrolling = false;
        LoupeOn = tool == CaptureTool.Color;
        _pressed = false;
        IsSpaceMoving = false;
        Selection = null;
        RefreshPending = needsRefresh;
        return new SelectorAction { Kind = SelectorActionKind.SwitchTool, Tool = tool, NeedsRefresh = needsRefresh };
    }

    /// <summary>The refresh for a tool switch finished (a failed refresh ends the session in the caller).</summary>
    public void CompleteRefresh() => RefreshPending = false;

    /// <summary>Marks the session over; later events are ignored.</summary>
    public void End() => Ended = true;

    /// <summary>Called after an action that finishes the session was accepted.</summary>
    public void MarkPending() => CapturePending = true;

    private SelectorAction Confirm(SelectorAction action)
    {
        if (action.Kind != SelectorActionKind.CopyColor && action.Kind != SelectorActionKind.MovePointer && action.Kind != SelectorActionKind.SwitchTool)
        {
            CapturePending = true;
        }

        return action;
    }

    private SelectorAction ConfirmRegion(string displayId, RectD rect)
    {
        if (_displays.TryGetValue(displayId, out var display))
        {
            _lastRegion = (displayId, rect.Offset(-display.Bounds.X, -display.Bounds.Y));
        }

        return Confirm(new SelectorAction { Kind = SelectorActionKind.ConfirmRegion, DisplayId = displayId, Region = rect, Scrolling = Scrolling });
    }

    private SelectorAction Nudge(SelectorKey key, bool shift)
    {
        var step = LoupeMath.NudgePixels(shift);
        var (dx, dy) = key switch
        {
            SelectorKey.Left => (-step, 0),
            SelectorKey.Right => (step, 0),
            SelectorKey.Up => (0, -step),
            _ => (0, step),
        };
        var target = new PointD(Math.Floor(Pointer.X) + dx, Math.Floor(Pointer.Y) + dy);
        if (!_displays.Values.Any(d => d.Bounds.Contains(target.Floor())))
        {
            var b = PointerDisplay.Bounds;
            target = new PointD(Math.Clamp(target.X, b.X, b.Right - 1), Math.Clamp(target.Y, b.Y, b.Bottom - 1));
        }

        SetPointer(target);
        return new SelectorAction { Kind = SelectorActionKind.MovePointer, Point = target, DisplayId = PointerDisplayId };
    }

    private void UpdateSelection(KeyModifiers modifiers)
    {
        if (_dragDisplayId is null || !_displays.TryGetValue(_dragDisplayId, out var display))
        {
            return;
        }

        var bounds = RectD.From(display.Bounds);
        var square = modifiers.HasFlag(KeyModifiers.Shift);
        var fromCenter = modifiers.HasFlag(KeyModifiers.Alt);
        if (IsSpaceMoving)
        {
            // Moving keeps the size: shift origin and end so the rectangle stays inside the display.
            var free = CaptureGeometry.SelectionRect(_dragOrigin, _dragCurrent, square, fromCenter, Unbounded);
            var dx = Math.Clamp(free.X, bounds.X, Math.Max(bounds.X, bounds.Right - free.Width)) - free.X;
            var dy = Math.Clamp(free.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - free.Height)) - free.Y;
            if (dx != 0 || dy != 0)
            {
                var shift = new PointD(dx, dy);
                _dragOrigin += shift;
                _dragCurrent += shift;
            }
        }

        Selection = CaptureGeometry.SelectionRect(_dragOrigin, _dragCurrent, square, fromCenter, bounds);
    }

    private static readonly RectD Unbounded = new(-1e9, -1e9, 2e9, 2e9);

    private void SetPointer(PointD point)
    {
        Pointer = point;
        var pixel = point.Floor();
        var display = Displays.FirstOrDefault(d => d.Bounds.Contains(pixel))
                      ?? Displays.OrderBy(d => DistanceSquared(d.Bounds, pixel)).First();
        PointerDisplayId = display.Id;
    }

    private static long DistanceSquared(PixelRect r, PixelPoint p)
    {
        long dx = Math.Max(Math.Max(r.X - p.X, 0), p.X - (r.Right - 1));
        long dy = Math.Max(Math.Max(r.Y - p.Y, 0), p.Y - (r.Bottom - 1));
        return (dx * dx) + (dy * dy);
    }
}
