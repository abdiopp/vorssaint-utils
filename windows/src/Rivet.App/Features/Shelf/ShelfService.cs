// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.App;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Modules.Shelf;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.App.Features.Shelf;

/// <summary>
/// The shelf (spec 07 §3.1): one item store shared by the classic floating
/// card and the docked pill/card, the session state (selection, expanded
/// piles, pins of the surfaces), the entry points (shortcut with the File
/// Explorer selection variant, tray menu, settings, radial menu, other
/// modules through <see cref="IShelfIntake"/>), drops, drag-out completion,
/// share/open/reveal and the automatic opens from <see cref="ShelfDragMonitor"/>.
/// Everything runs on the UI thread.
/// </summary>
public sealed class ShelfService : IShelfIntake, IFeatureController, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly FeatureRuntime _runtime;
    private readonly IShelfPlatform _platform;
    private readonly IScreenService _screens;
    private readonly List<IDisposable> _observers = [];
    private ShelfDragMonitor? _monitor;
    private ShelfCardWindow? _classic;
    private ShelfCardWindow? _dockedCard;
    private ShelfPillWindow? _pill;
    private int _ticket;
    private bool _available;
    private bool _dragActive;
    private bool _forcedOpen;
    private bool _catchShowing;
    private DispatcherTimer? _catchTimer;
    private DispatcherTimer? _dragEndTimer;
    private ShelfEdgeMatch? _peek;

    public ShelfService(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _platform = services.GetRequiredService<IShelfPlatform>();
        _screens = services.GetRequiredService<IScreenService>();
        var folder = services.GetRequiredService<AppPaths>().LocalFolder("Shelf");
        Store = new ShelfStore(
            folder,
            Path.Combine(folder, "ShelfFiles"),
            new ShelfRestoreContext { Heal = item => item.Bookmark is { } b ? _platform.ResolveBookmark(b) : null },
            ToPng,
            _platform.CreateBookmark);
        Thumbnails = new ShelfThumbnails(_platform);
        Store.Changed += OnStoreChanged;
        Selection.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _observers.Add(_settings.Observe(() => Dispatcher.UIThread.Post(SyncMonitor),
            ShelfSettings.ShakeToOpen, ShelfSettings.DropZoneEnabled, ShelfSettings.EdgeDragEnabled));
        _observers.Add(_settings.Observe(() => Dispatcher.UIThread.Post(UpdateDocked), ShelfSettings.DockPlacement));
    }

    public ShelfStore Store { get; }

    public ShelfSelection Selection { get; } = new();

    /// <summary>Expanded piles (session only).</summary>
    public HashSet<Guid> Expanded { get; } = [];

    public ShelfThumbnails Thumbnails { get; }

    /// <summary>Items, selection or expansion changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Installed and switched on.</summary>
    public bool IsAvailable => _available && _settings.Get(ShelfSettings.Enabled);

    public bool IsClassicVisible => _classic?.IsVisible == true;

    public bool IsDockedExpanded => _dockedCard?.IsVisible == true;

    public bool IsPillVisible => _pill?.IsVisible == true;

    public ShelfCardWindow? ClassicWindow => _classic;

    public ShelfCardWindow? DockedWindow => _dockedCard;

    public ShelfPillWindow? PillWindow => _pill;

    public ShelfDragMonitor? Monitor => _monitor;

    /// <summary>An internal tile drag is running (blocks automatic opens and non-tile drop targets).</summary>
    public bool InternalDragActive { get; private set; }

    public int LeafCount => Store.LeafCount;

    // ── Feature lifecycle ───────────────────────────────────────────────

    public void Sync(bool available)
    {
        _available = available;
        if (IsAvailable)
        {
            _ = Store.LoadAsync();
        }
        else
        {
            // Turning the shelf off never deletes items; pending reads are invalidated.
            _ticket++;
            HideClassic();
            HideDocked();
            Store.Flush();
        }

        SyncMonitor();
        UpdateDocked();
    }

    private void SyncMonitor()
    {
        var wanted = IsAvailable && (_settings.Get(ShelfSettings.ShakeToOpen) || _settings.Get(ShelfSettings.DropZoneEnabled) || _settings.Get(ShelfSettings.EdgeDragEnabled));
        if (wanted && _monitor is null)
        {
            _monitor = new ShelfDragMonitor(_services.GetRequiredService<IInputHooks>(), _platform, _screens, this);
        }
        else if (!wanted && _monitor is not null)
        {
            _monitor.Dispose();
            _monitor = null;
            _dragActive = false;
        }
    }

    // ── IShelfIntake ────────────────────────────────────────────────────

    bool IShelfIntake.IsAvailable => IsAvailable;

    public void AddFiles(IReadOnlyList<string> paths)
    {
        if (!IsAvailable || paths.Count == 0)
        {
            return;
        }

        // Other modules hand over their own outputs (screenshots, media results): keep owned copies
        // only for files inside the app's temp area, everything else by reference.
        var result = Store.AddFiles(paths);
        if (result == ShelfAddResult.Full)
        {
            _platform.Beep();
        }
    }

    public void AddText(string text)
    {
        if (!IsAvailable)
        {
            return;
        }

        if (Store.AddText(text) != ShelfAddResult.Added)
        {
            _platform.Beep();
        }
    }

    // ── Entry points ────────────────────────────────────────────────────

    /// <summary>The shortcut: toggles the classic card, or adds the File Explorer selection first (§3.1.4).</summary>
    public void ShortcutPressed()
    {
        if (!IsAvailable)
        {
            return;
        }

        if (!_settings.Get(ShelfSettings.ShortcutAddsExplorerSelection))
        {
            Toggle();
            return;
        }

        var ticket = ++_ticket;
        _ = Task.Run(() =>
        {
            IReadOnlyList<string>? selection = null;
            try
            {
                selection = _platform.ForegroundExplorerSelection();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn("shelf", "Reading the File Explorer selection failed.", ex);
            }

            Dispatcher.UIThread.Post(() => ResolveTicket(ticket, selection));
        });
    }

    /// <summary>Resolves a selection read exactly once: discard, toggle or add + summon.</summary>
    internal void ResolveTicket(int ticket, IReadOnlyList<string>? selection)
    {
        if (ticket != _ticket || !IsAvailable)
        {
            return;
        }

        var known = ShelfTree.Leaves(Store.Items).Where(l => l.Path is not null).Select(l => ShelfDropParser.Standardize(l.Path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = (selection ?? []).Select(ShelfDropParser.Standardize).Where(p => !known.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (fresh.Count == 0)
        {
            Toggle();
            return;
        }

        if (Store.AddFiles(fresh) != ShelfAddResult.Added)
        {
            _platform.Beep();
            Toggle();
            return;
        }

        Summon();
    }

    public void Toggle()
    {
        if (IsClassicVisible)
        {
            HideClassic();
        }
        else
        {
            Summon();
        }
    }

    /// <summary>Shows the classic card at the pointer (16 DIP below it, clamped 8 DIP inside the work area).</summary>
    public void Summon(PixelPoint? at = null)
    {
        if (!IsAvailable)
        {
            return;
        }

        var pointer = at ?? _screens.CursorPosition;
        var screen = _screens.ScreenFromPoint(pointer);
        var window = EnsureClassic();
        HideDocked();
        CancelPeek();
        window.ShowAt(size => ShelfGeometry.SummonPosition(pointer, size.Width, size.Height, screen.WorkArea, screen.Scale));
    }

    public void HideClassic(bool viaCloseButton = false)
    {
        if (viaCloseButton && _settings.Get(ShelfSettings.ClearOnClose))
        {
            ClearAll();
        }

        CancelPeek();
        _classic?.HideCard();
        UpdateDocked();
    }

    /// <summary>Tray menu, panel accessory, pill click: expand the docked shelf (the classic card when docking is off).</summary>
    public void ExpandDocked()
    {
        if (!IsAvailable)
        {
            return;
        }

        if (!DockingOn)
        {
            Summon();
            return;
        }

        _classic?.HideCard();
        if (Store.IsEmpty)
        {
            _forcedOpen = true;
        }

        ShowDockedCard();
    }

    public void CollapseDocked()
    {
        _dockedCard?.HideCard();
        if (Store.IsEmpty)
        {
            _forcedOpen = false;
        }

        UpdateDocked();
    }

    private bool DockingOn => IsAvailable && _settings.Get(ShelfSettings.DropZoneEnabled);

    private ShelfDockPlacement Placement => ShelfSettings.ParsePlacement(_settings.Get(ShelfSettings.DockPlacement));

    // ── Docked shelf ────────────────────────────────────────────────────

    /// <summary>Shows or hides the pill: docking on, no classic card, and items, a drag or a forced open.</summary>
    public void UpdateDocked()
    {
        var wanted = DockingOn && !IsClassicVisible && !IsDockedExpanded && (Store.LeafCount > 0 || _dragActive || _forcedOpen || _catchShowing);
        if (!wanted)
        {
            _pill?.Hide();
            return;
        }

        var pill = _pill ??= new ShelfPillWindow(this);
        pill.Refresh(Placement, Store.LeafCount, _catchShowing);
        pill.ShowDocked(DockFrame);
    }

    private void HideDocked()
    {
        _dockedCard?.HideCard();
        _pill?.Hide();
    }

    private void ShowDockedCard()
    {
        _pill?.Hide();
        var card = _dockedCard ??= new ShelfCardWindow(this, ShelfSurface.Docked);
        card.ShowAt(size => DockFrame(size).Position());
    }

    /// <summary>The docked frame for a window of <paramref name="size"/> on the tray icon's screen.</summary>
    internal PixelRect DockFrame((int Width, int Height) size)
    {
        var screen = DockScreen();
        return ShelfGeometry.DockFrame(Placement, size.Width, size.Height, screen, TrayIconRect);
    }

    /// <summary>The tray icon's screen rectangle; not exposed by the shell yet (docs: requests for shared code).</summary>
    internal PixelRect? TrayIconRect => null;

    private ScreenInfo DockScreen() => TrayIconRect is { } icon ? _screens.ScreenFromPoint(new PixelPoint(icon.X + (icon.Width / 2), icon.Y + (icon.Height / 2))) : _screens.Primary;

    // ── Drag monitor callbacks (UI thread) ──────────────────────────────

    /// <summary>A potential content drag started in another app.</summary>
    internal void OnDragStarted(string? sourceApp)
    {
        _dragEndTimer?.Stop();
        if (!AllowsAutomatic(sourceApp) || InternalDragActive)
        {
            return;
        }

        if (DockingOn && !IsClassicVisible)
        {
            _dragActive = true;
            UpdateDocked();
        }
    }

    /// <summary>Pointer moved during a potential drag (also called by the watchdog so dwells complete).</summary>
    internal void OnDragMoved(PixelPoint pointer, string? sourceApp, TimeSpan now)
    {
        if (!IsAvailable || InternalDragActive || !AllowsAutomatic(sourceApp))
        {
            return;
        }

        // Pill → card after a 150 ms dwell inside the trigger frame; card → pill when the pointer leaves it padded 32.
        if (DockingOn && !IsClassicVisible && _monitor is { } monitor)
        {
            var screen = DockScreen();
            if (IsDockedExpanded && _dockedCard!.FrameInPixels() is { } frame)
            {
                if (!ShelfGeometry.ContainsInclusive(ShelfGeometry.RetreatFrame(frame, screen.Scale), pointer))
                {
                    _dockedCard.HideCard();
                    UpdateDocked();
                }
            }
            else
            {
                var trigger = ShelfGeometry.TriggerFrame(_pill?.FrameInPixels(), Placement, screen, TrayIconRect);
                var inside = ShelfGeometry.ContainsInclusive(trigger, pointer) ? 1 : (int?)null;
                if (monitor.DockDwell.Update(inside, now) is not null)
                {
                    monitor.DockDwell.Reset();
                    ShowDockedCard();
                }
            }
        }

        // Edge peek.
        if (_settings.Get(ShelfSettings.EdgeDragEnabled))
        {
            if (_peek is { } peek)
            {
                var screen = _screens.Screens.FirstOrDefault(s => s.Id == peek.ScreenId);
                if (screen is null || ShelfGeometry.ShouldRetreat(pointer, screen, peek.Left))
                {
                    CancelPeek();
                    HideClassicIfEmpty();
                }
            }
            else if (!IsClassicVisible && _monitor is { } m)
            {
                var match = ShelfGeometry.MatchEdge(pointer, _screens.Screens);
                if (m.EdgeDwell.Update(match, now) is { } hit)
                {
                    m.EdgeDwell.Reset();
                    StartPeek(hit, pointer);
                }
            }
        }
    }

    /// <summary>Shake detected during a potential drag: summon at the pointer.</summary>
    internal void OnShake(PixelPoint pointer, string? sourceApp)
    {
        if (!IsAvailable || !_settings.Get(ShelfSettings.ShakeToOpen) || InternalDragActive || !AllowsAutomatic(sourceApp))
        {
            return;
        }

        if (!IsClassicVisible)
        {
            Summon(pointer);
            _classic?.MarkSpeculative();
        }
    }

    /// <summary>The button went up (or the watchdog saw it up).</summary>
    internal void OnDragEnded()
    {
        _dragEndTimer?.Stop();
        _dragEndTimer = new DispatcherTimer { Interval = ShelfConstants.DockDragEndGrace };
        _dragEndTimer.Tick += (_, _) => FinishDragEnd();
        _dragEndTimer.Start();
    }

    /// <summary>After the 0.15 s grace (a landing drop claims it first): collapse, retract, hide a speculative empty card.</summary>
    internal void FinishDragEnd()
    {
        _dragEndTimer?.Stop();
        _dragActive = false;
        if (IsDockedExpanded && !_catchShowing)
        {
            _dockedCard!.HideCard();
        }

        if (_peek is not null)
        {
            // Release without a drop: retract.
            CancelPeek();
            HideClassicIfEmpty();
        }

        // A speculative card (shake) that saw no drop disappears at once while empty.
        if (_classic is { IsSpeculative: true } && Store.IsEmpty)
        {
            _classic.HideCard();
        }

        UpdateDocked();
    }

    private void StartPeek(ShelfEdgeMatch match, PixelPoint pointer)
    {
        var screen = _screens.Screens.FirstOrDefault(s => s.Id == match.ScreenId);
        if (screen is null)
        {
            return;
        }

        _peek = match;
        var window = EnsureClassic();
        HideDocked();
        window.IsPeeking = true;
        window.ShowAt(size => ShelfGeometry.PeekPosition(screen, match.Left, size.Width, size.Height, pointer.Y));
        window.MarkSpeculative();
    }

    /// <summary>A drop landed while peeking: slide flush to the usable edge and stay.</summary>
    private void GraduatePeek()
    {
        if (_peek is not { } peek || _classic is null)
        {
            return;
        }

        var screen = _screens.Screens.FirstOrDefault(s => s.Id == peek.ScreenId);
        _peek = null;
        _classic.IsPeeking = false;
        if (screen is not null && _classic.FrameInPixels() is { } frame)
        {
            _classic.SlideTo(ShelfGeometry.RevealPosition(screen, peek.Left, frame.Width, frame.Height, frame.Y));
        }
    }

    private void CancelPeek()
    {
        if (_peek is not null && _classic is not null)
        {
            _classic.IsPeeking = false;
        }

        _peek = null;
    }

    private void HideClassicIfEmpty()
    {
        if (Store.IsEmpty)
        {
            _classic?.HideCard();
        }
    }

    private bool AllowsAutomatic(string? sourceApp) =>
        ShelfExclusions.AllowsAutomaticOpen(sourceApp, _settings.Get(ShelfSettings.AutomaticExclusions));

    // ── Drops ───────────────────────────────────────────────────────────

    /// <summary>A drop onto a shelf surface (null target = new items; else merge into that tile).</summary>
    public bool Drop(IReadOnlyList<ShelfDropEntry> entries, Guid? target, ShelfSurface surface)
    {
        if (!IsAvailable)
        {
            return false;
        }

        var parts = ShelfDropParser.Parse(entries);
        var result = target is { } id ? Store.MergeInto(id, parts) : Store.Add(parts);
        if (result != ShelfAddResult.Added)
        {
            return false;
        }

        GraduatePeek();
        _classic?.MarkSettled();
        if (surface == ShelfSurface.Docked)
        {
            Catch();
        }

        return true;
    }

    /// <summary>A tile dragged onto another tile (offered as Move).</summary>
    public bool MoveInto(Guid target, IReadOnlyList<Guid> sources) => IsAvailable && Store.MoveInto(target, sources);

    /// <summary>A drop landed on the docked window: green check for 0.9 s, collapse, clear forced-open.</summary>
    private void Catch()
    {
        _catchShowing = true;
        _forcedOpen = false;
        _dockedCard?.HideCard();
        UpdateDocked();
        _catchTimer?.Stop();
        _catchTimer = new DispatcherTimer { Interval = ShelfConstants.DockCatchTick };
        _catchTimer.Tick += (_, _) =>
        {
            _catchTimer?.Stop();
            _catchShowing = false;
            UpdateDocked();
        };
        _catchTimer.Start();
    }

    // ── Drag-out ────────────────────────────────────────────────────────

    /// <summary>The leaves a tile drag carries (selection or the tile), after the living check. Null = no drag.</summary>
    public IReadOnlyList<ShelfItem>? PrepareDragOut(Guid grabbed)
    {
        var candidates = ShelfTree.Scope(Store.Items, grabbed, Selection.Selected);
        var living = Store.Living(candidates, out var dead);
        if (living.Count == 0)
        {
            HandleDead(dead);
            return null;
        }

        return living;
    }

    /// <summary>Whether a destination may move the files away (only when nothing dragged is protected).</summary>
    public bool AllowsMove(IReadOnlyList<ShelfItem> leaves)
    {
        var protectedIds = ShelfTree.Protected(Store.Items);
        return ShelfDragOutPolicy.AllowsMove(_settings.Get(ShelfSettings.RemoveAfterDrop), leaves.Any(l => protectedIds.Contains(l.Id)));
    }

    public void BeginInternalDrag() => InternalDragActive = true;

    /// <summary>Completion: remove the dragged unprotected items and close the source surface as configured.</summary>
    public void CompleteDragOut(IReadOnlyList<ShelfItem> leaves, bool accepted, bool merged, ShelfSurface surface, bool surfacePinned)
    {
        InternalDragActive = false;
        var (remove, close) = ShelfDragOutPolicy.AfterDrop(accepted, merged, _settings.Get(ShelfSettings.RemoveAfterDrop), _settings.Get(ShelfSettings.CloseAfterDrop), surfacePinned);
        if (remove)
        {
            Store.RemoveAfterDrag(leaves.Select(l => l.Id).ToList());
        }

        if (close)
        {
            if (surface == ShelfSurface.Classic)
            {
                HideClassic();
            }
            else
            {
                CollapseDocked();
            }
        }
    }

    private void HandleDead(IReadOnlyList<ShelfItem> dead)
    {
        _services.GetService<IHud>()?.Show(L.Get("Strings.shelfFileMissing"), HudStyle.Warning, "DocumentDismiss", TimeSpan.FromSeconds(1.5));
        if (dead.Count > 0)
        {
            Store.Remove(dead.Select(d => d.Id).ToHashSet());
        }
    }

    // ── Commands ────────────────────────────────────────────────────────

    /// <summary>Living file paths of a scope; dead files trigger the dead-drag handling.</summary>
    public IReadOnlyList<string> LivingFiles(IReadOnlyList<ShelfItem> scope)
    {
        var files = scope.Where(l => l.Kind == ShelfItemKind.File).ToList();
        if (files.Count == 0)
        {
            return [];
        }

        var living = Store.Living(files, out var dead);
        if (living.Count == 0)
        {
            HandleDead(dead);
        }

        return living.Select(l => l.Path!).ToList();
    }

    /// <summary>The footer Share scope: the selection, or everything when nothing is selected.</summary>
    public IReadOnlyList<ShelfItem> FooterScope() => Selection.Count > 0
        ? ShelfTree.Leaves(ShelfTree.AllItems(Store.Items).Where(i => Selection.Contains(i.Id))).DistinctBy(l => l.Id).ToList()
        : ShelfTree.Leaves(Store.Items).ToList();

    public bool Share(IReadOnlyList<ShelfItem> scope, nint owner)
    {
        var paths = LivingFiles(scope);
        if (paths.Count == 0)
        {
            return false;
        }

        if (!_platform.Share(owner, paths, L.Get("Strings.shelfName")))
        {
            _platform.Beep();
            return false;
        }

        return true;
    }

    public void Open(IReadOnlyList<ShelfItem> scope)
    {
        var shell = _services.GetRequiredService<IShellService>();
        foreach (var path in LivingFiles(scope))
        {
            shell.OpenFile(path);
        }
    }

    /// <summary>"Open With": the Windows chooser for the first file (Windows has no multi-file "Open With").</summary>
    public void OpenWith(IReadOnlyList<ShelfItem> scope)
    {
        if (LivingFiles(scope).FirstOrDefault() is { } first)
        {
            _services.GetRequiredService<IShellService>().OpenWith(first);
        }
    }

    public void Reveal(IReadOnlyList<ShelfItem> scope)
    {
        var paths = LivingFiles(scope);
        if (paths.Count > 0)
        {
            _platform.Reveal(paths);
        }
    }

    /// <summary>"Edit" in the screenshot editor: only with the Screenshot feature and exactly one image.</summary>
    public bool CanEdit(IReadOnlyList<ShelfItem> scope) =>
        scope.Count == 1 && scope[0].IsImage && _services.GetService<IScreenshotEditor>() is not null && _runtime.IsAvailable(FeatureIds.Screenshot);

    public void Edit(IReadOnlyList<ShelfItem> scope)
    {
        if (!CanEdit(scope) || LivingFiles(scope).FirstOrDefault() is not { } path || _services.GetService<IScreenshotEditor>() is not { } editor)
        {
            return;
        }

        using var image = SkiaConvert.LoadImage(path);
        if (image is not null)
        {
            _ = editor.OpenAsync(new ScreenshotEditRequest { Image = SkiaConvert.ToPixelBuffer(image), SourcePath = path });
        }
    }

    public void TogglePin(IReadOnlyList<Guid> ids)
    {
        var items = ids.Select(id => ShelfTree.Find(Store.Items, id)).OfType<ShelfItem>().ToList();
        var pin = items.Any(i => !i.Pinned);
        foreach (var item in items)
        {
            Store.SetPinned(item.Id, pin);
        }
    }

    /// <summary>Tile ✕: removes the item even if pinned (a pile goes as a whole).</summary>
    public void RemoveItem(Guid id) => Store.Remove(new HashSet<Guid> { id });

    /// <summary>Trash with a selection: removes the selected ids, pinned ones included.</summary>
    public void RemoveSelected() => Store.Remove(Selection.Selected.ToHashSet());

    /// <summary>Trash with no selection: cancels pending reads, then removes every unprotected leaf.</summary>
    public void ClearAll()
    {
        _ticket++;
        Store.ClearUnprotected();
    }

    public void ToggleExpanded(Guid pileId)
    {
        if (!Expanded.Remove(pileId))
        {
            Expanded.Add(pileId);
        }
        else if (ShelfTree.Find(Store.Items, pileId) is { } pile)
        {
            // Collapsing a pile deselects its descendants.
            Selection.Deselect(ShelfTree.AllItems(pile.Children).Select(i => i.Id));
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ShelfRow> VisibleRows() => ShelfTree.VisibleRows(Store.Items, Expanded);

    public string FileKind(string path) => _platform.FileKind(path);

    public void Dispose()
    {
        foreach (var observer in _observers)
        {
            observer.Dispose();
        }

        _monitor?.Dispose();
        _monitor = null;
        Store.Dispose();
        Thumbnails.Dispose();
    }

    // ── Internals ───────────────────────────────────────────────────────

    private ShelfCardWindow EnsureClassic() => _classic ??= new ShelfCardWindow(this, ShelfSurface.Classic);

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        var surviving = ShelfTree.AllItems(Store.Items).Select(i => i.Id).ToHashSet();
        Selection.Prune(surviving);
        Expanded.IntersectWith(surviving);
        if (Store.IsEmpty)
        {
            _forcedOpen = false;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        UpdateDocked();
    }

    private static byte[]? ToPng(byte[] data)
    {
        try
        {
            using var image = SKImage.FromEncodedData(data);
            return image is null ? null : SkiaConvert.EncodePng(image);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}

internal static class PixelRectExtensions
{
    public static PixelPoint Position(this PixelRect rect) => new(rect.X, rect.Y);
}
