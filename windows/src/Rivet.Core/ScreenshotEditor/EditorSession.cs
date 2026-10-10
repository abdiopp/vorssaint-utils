// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.ScreenshotEditor;

/// <summary>The base image as the session sees it; the imaging layer supplies the pixels.</summary>
public interface IEditorImage
{
    int Width { get; }

    int Height { get; }
}

/// <summary>One undo entry: the base image and the annotation list at that moment.</summary>
public sealed record EditorSnapshot(IEditorImage Image, IReadOnlyList<Annotation> Annotations);

/// <summary>What is under the pointer, for choosing a cursor.</summary>
public enum HoverKind
{
    Nothing,
    Annotation,

    /// <summary>The selected mark while a creation tool is active (open hand: pressing moves it).</summary>
    SelectedBody,
    Handle,
    Endpoint,
    Word,
    CropHandle,
    CropInside,
    CropNew,
}

public readonly record struct HoverInfo(HoverKind Kind, ResizeHandle? Handle = null);

/// <summary>
/// The editor's document and gesture logic (spec 01 §3.10.4–§3.10.13),
/// independent of the UI toolkit and of the pixel engine. Pointer input
/// arrives as image points (for geometry) plus view points (for the
/// zoom-independent 7-point tap threshold).
/// </summary>
public sealed class EditorSession
{
    /// <summary>A press whose end lies within this many view points of its start is a tap.</summary>
    public const double TapRadius = 7;

    /// <summary>Movement below this many view points never nudges a mark (mouse-click jitter).</summary>
    public const double MoveThreshold = 3;

    private readonly ITextMeasurer _measurer;
    private readonly Func<IEditorImage, int, int, int, int, IEditorImage> _cropper;
    private readonly SnapshotHistory<EditorSnapshot> _history = new(60);
    private readonly Dictionary<IEditorImage, TextRecognition> _recognized = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IEditorImage, IReadOnlyList<ImgRect>> _carriedRuns = new(ReferenceEqualityComparer.Instance);
    private List<Annotation> _annotations = [];
    private EditorSnapshot _clean;

    // Gesture state.
    private Gesture _gesture;
    private ImgPoint _pressImage;
    private ImgPoint _pressView;
    private double _maxViewDistance;
    private bool _moved;
    private Annotation? _original;
    private ResizeHandle _handle;
    private int _endpoint;
    private Guid? _draftId;
    private int _undoDepthBeforeDraft;
    private ImgRect _cropOriginal;
    private ImgRect _cropPrevious;
    private bool _pressedSelectionWithCreationTool;

    private int _undoDepthBeforeNewText;

    public EditorSession(
        IEditorImage image,
        double scale,
        EditorStyle style,
        EditorTool tool,
        ITextMeasurer measurer,
        Func<IEditorImage, int, int, int, int, IEditorImage> cropper)
    {
        Image = image;
        Scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        Style = style;
        Tool = tool;
        _measurer = measurer;
        _cropper = cropper;
        _clean = Snapshot();
        if (tool == EditorTool.Crop)
        {
            CropDraft = Bounds;
        }
    }

    private enum Gesture
    {
        None,
        Create,
        TapCreate,
        Move,
        Resize,
        Endpoint,
        WordSelect,
        EmptyTap,
        CropResize,
        CropMove,
        CropNew,
    }

    /// <summary>Anything visible changed (redraw).</summary>
    public event EventHandler? Changed;

    /// <summary>The base image changed (crop, undo, redo): re-run recognition and the QR scan, drop pixel caches.</summary>
    public event EventHandler? ImageChanged;

    /// <summary>The style bar must re-read <see cref="Style"/> (selection sync or a refused change).</summary>
    public event EventHandler? StyleChanged;

    public event EventHandler? ToolChanged;

    /// <summary>A text mark should get the inline editor.</summary>
    public event EventHandler<Guid>? TextEditRequested;

    public IEditorImage Image { get; private set; }

    public double Scale { get; }

    public ImgRect Bounds => new(0, 0, Image.Width, Image.Height);

    public IReadOnlyList<Annotation> Annotations => _annotations;

    public EditorStyle Style { get; private set; }

    public EditorTool Tool { get; private set; }

    public Guid? SelectedId { get; private set; }

    public Annotation? Selected => SelectedId is { } id ? Find(id) : null;

    /// <summary>The crop rectangle while the Crop tool is active.</summary>
    public ImgRect? CropDraft { get; private set; }

    /// <summary>The edge point the crop loupe magnifies while a grip is dragged.</summary>
    public ImgPoint? CropLoupePoint { get; private set; }

    public TextRecognition Recognition { get; private set; } = TextRecognition.Pending;

    public IReadOnlyList<int> SelectedWords { get; private set; } = [];

    /// <summary>The text mark currently in the inline editor (the canvas does not draw it).</summary>
    public Guid? EditingTextId { get; private set; }

    public bool EditingIsNew { get; private set; }

    /// <summary>The mark being drawn right now (excluded from the layer buttons).</summary>
    public Guid? DraftId => _draftId;

    public bool IsGestureActive => _gesture != Gesture.None;

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    /// <summary>Optional check run before a blur style or level is applied; false snaps the controls back.</summary>
    public Func<BlurStyle, int, bool>? EnsureBlurSample { get; set; }

    public Annotation? Find(Guid id) => _annotations.FirstOrDefault(a => a.Id == id);

    public EditorSnapshot Snapshot() => new(Image, _annotations.ToArray());

    /// <summary>Image or annotations differ from the last clean snapshot (taken at open and after each export).</summary>
    public bool IsContentDirty =>
        !ReferenceEquals(Image, _clean.Image) || !_annotations.SequenceEqual(_clean.Annotations);

    public void MarkClean() => _clean = Snapshot();

    // ───────────────────────────── Tools and styles ─────────────────────────────

    public void SetTool(EditorTool tool)
    {
        if (Tool == tool && (tool != EditorTool.Crop || CropDraft is not null))
        {
            return;
        }

        CancelGesture();
        if (Tool == EditorTool.Select && tool != EditorTool.Select)
        {
            SelectedWords = [];
        }

        Tool = tool;
        if (tool == EditorTool.Crop)
        {
            SelectedId = null;
            CropDraft = Bounds;
        }
        else
        {
            CropDraft = null;
            CropLoupePoint = null;
        }

        ToolChanged?.Invoke(this, EventArgs.Empty);
        OnChanged();
    }

    public void SetColor(AnnotationColor color) =>
        ApplyStyle(Style with { Color = color }, a => a.UsesColor && a.Color != color, a => a with { Color = color });

    public void SetStroke(StrokeWidth stroke) =>
        ApplyStyle(Style with { Stroke = stroke }, a => a.UsesStroke && a.Stroke != stroke, a => a with { Stroke = stroke });

    public void SetTextSize(int size)
    {
        var s = TextSizes.Sanitize(size);
        ApplyStyle(
            Style with { TextSize = s },
            a => a.Kind == AnnotationKind.Text && a.TextSize != s,
            a => a with { TextSize = s, Rect = AnnotationMetrics.TextBounds(a.Text, a.Rect.Origin, s, Scale, _measurer) });
    }

    /// <summary>Switching to Scribbly gives the arrow a new random seed.</summary>
    public void SetArrowStyle(ArrowStyle style) =>
        ApplyStyle(
            Style with { ArrowStyle = style },
            a => a.Kind == AnnotationKind.Arrow && a.ArrowStyle != style,
            a => a with { ArrowStyle = style, Seed = style == ArrowStyle.Scribbly ? ScribbleRandom.NewSeed() : a.Seed });

    public void SetSticker(StickerKind sticker) =>
        ApplyStyle(Style with { Sticker = sticker }, a => a.Kind == AnnotationKind.Sticker && a.Sticker != sticker, a => a with { Sticker = sticker });

    public void SetBlurStyle(BlurStyle style)
    {
        if (!SampleAvailable(style, Selected is { Kind: AnnotationKind.Blur } b ? b.BlurLevel : Style.BlurLevel))
        {
            StyleChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ApplyStyle(Style with { BlurStyle = style }, a => a.Kind == AnnotationKind.Blur && a.BlurStyle != style, a => a with { BlurStyle = style });
    }

    public void SetBlurLevel(int level)
    {
        var l = Math.Clamp(level, BlurStyles.MinLevel, BlurStyles.MaxLevel);
        if (!SampleAvailable(Selected is { Kind: AnnotationKind.Blur } b ? b.BlurStyle : Style.BlurStyle, l))
        {
            StyleChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ApplyStyle(Style with { BlurLevel = l }, a => a.Kind == AnnotationKind.Blur && a.BlurLevel != l, a => a with { BlurLevel = l });
    }

    public void SetTextOnly(bool textOnly) =>
        ApplyStyle(Style with { TextOnly = textOnly }, a => a.Kind == AnnotationKind.Blur && a.TextOnly != textOnly, a => a with { TextOnly = textOnly });

    private bool SampleAvailable(BlurStyle style, int level) => EnsureBlurSample?.Invoke(style, level) ?? true;

    /// <summary>Changes the style for new marks and, as one undo step, the selected mark when it uses that attribute.</summary>
    private void ApplyStyle(EditorStyle style, Func<Annotation, bool> applies, Func<Annotation, Annotation> change)
    {
        Style = style;
        if (Selected is { } selected && applies(selected))
        {
            RecordUndo();
            Replace(change(selected));
        }

        StyleChanged?.Invoke(this, EventArgs.Empty);
        OnChanged();
    }

    /// <summary>Selecting syncs the controls to the mark's values, only for the attributes it uses.</summary>
    private void SyncStyleFrom(Annotation a)
    {
        var s = Style;
        if (a.UsesColor)
        {
            s = s with { Color = a.Color };
        }

        if (a.UsesStroke)
        {
            s = s with { Stroke = a.Stroke };
        }

        s = a.Kind switch
        {
            AnnotationKind.Text => s with { TextSize = a.TextSize },
            AnnotationKind.Arrow => s with { ArrowStyle = a.ArrowStyle },
            AnnotationKind.Sticker => s with { Sticker = a.Sticker },
            AnnotationKind.Blur => s with { BlurStyle = a.BlurStyle, BlurLevel = a.BlurLevel, TextOnly = a.TextOnly },
            _ => s,
        };
        if (s != Style)
        {
            Style = s;
            StyleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ───────────────────────────── Selection and layers ─────────────────────────────

    public void Select(Guid? id)
    {
        SelectedId = id is { } value && Find(value) is not null ? value : null;
        if (Selected is { } a)
        {
            SyncStyleFrom(a);
        }

        OnChanged();
    }

    public void Deselect()
    {
        if (SelectedId is null)
        {
            return;
        }

        SelectedId = null;
        OnChanged();
    }

    public void DeleteSelected()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        RecordUndo();
        _annotations = _annotations.Where(a => a.Id != selected.Id).ToList();
        SelectedId = null;
        Renumber();
        OnChanged();
    }

    /// <summary>Layer buttons appear when more than one mark exists, not counting one still being drawn.</summary>
    public bool ShowsLayerButtons => _annotations.Count(a => a.Id != _draftId) > 1;

    public bool CanBringForward => SelectedId is { } id && IndexOf(id) is var i && i >= 0 && i < _annotations.Count - 1;

    public bool CanSendBackward => SelectedId is { } id && IndexOf(id) > 0;

    public void BringForward() => SwapSelected(+1);

    public void SendBackward() => SwapSelected(-1);

    private void SwapSelected(int direction)
    {
        if (SelectedId is not { } id)
        {
            return;
        }

        var index = IndexOf(id);
        var other = index + direction;
        if (index < 0 || other < 0 || other >= _annotations.Count)
        {
            return;
        }

        RecordUndo();
        var list = _annotations.ToList();
        (list[index], list[other]) = (list[other], list[index]);
        _annotations = list;
        Renumber();
        OnChanged();
    }

    private int IndexOf(Guid id) => _annotations.FindIndex(a => a.Id == id);

    /// <summary>Counters are always 1…n in drawing order, so there are never gaps.</summary>
    private void Renumber()
    {
        var n = 0;
        var changed = false;
        var list = new List<Annotation>(_annotations.Count);
        foreach (var a in _annotations)
        {
            if (a.Kind == AnnotationKind.Counter)
            {
                n++;
                if (a.Number != n)
                {
                    changed = true;
                    list.Add(a with { Number = n });
                    continue;
                }
            }

            list.Add(a);
        }

        if (changed)
        {
            _annotations = list;
        }
    }

    // ───────────────────────────── Undo and redo ─────────────────────────────

    private void RecordUndo() => _history.Record(Snapshot());

    public void Undo()
    {
        CancelGesture();
        if (_history.TryUndo(Snapshot(), out var previous))
        {
            Restore(previous);
        }
    }

    public void Redo()
    {
        CancelGesture();
        if (_history.TryRedo(Snapshot(), out var next))
        {
            Restore(next);
        }
    }

    private void Restore(EditorSnapshot snapshot)
    {
        var imageChanged = !ReferenceEquals(snapshot.Image, Image);
        _annotations = snapshot.Annotations.ToList();
        SelectedId = null;
        EditingTextId = null;
        CropDraft = null;
        CropLoupePoint = null;
        if (Tool == EditorTool.Crop)
        {
            Tool = EditorTool.Select;
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }

        if (imageChanged)
        {
            Image = snapshot.Image;
            SelectedWords = [];
            Recognition = _recognized.TryGetValue(Image, out var known) ? known : TextRecognition.Pending;
            ImageChanged?.Invoke(this, EventArgs.Empty);
        }

        OnChanged();
    }

    // ───────────────────────────── Crop ─────────────────────────────

    /// <summary>Applies the crop draft (at least 8 × 8 px), shifting marks, words and runs; otherwise just switches to Select.</summary>
    public bool ApplyCrop()
    {
        if (CropDraft is not { } draft)
        {
            return false;
        }

        var snapped = CropMath.Snap(draft, Bounds);
        if (!CropMath.IsLargeEnough(snapped) || snapped.NearlyEquals(Bounds))
        {
            SetTool(EditorTool.Select);
            return false;
        }

        CancelGesture();
        RecordUndo();
        var (x, y, w, h) = CropMath.PixelRect(snapped);
        var cropped = _cropper(Image, x, y, w, h);
        var offset = new ImgRect(x, y, w, h);
        _annotations = _annotations.Select(a => a.Translated(-x, -y)).ToList();
        var shifted = Recognition.Cropped(offset);
        if (shifted.Runs is { } runs)
        {
            _carriedRuns[cropped] = runs;
        }

        Image = cropped;
        Recognition = shifted;
        SelectedWords = [];
        SelectedId = null;
        CropDraft = null;
        CropLoupePoint = null;
        Tool = EditorTool.Select;
        ToolChanged?.Invoke(this, EventArgs.Empty);
        ImageChanged?.Invoke(this, EventArgs.Empty);
        OnChanged();
        return true;
    }

    public void CancelCrop()
    {
        if (Tool == EditorTool.Crop)
        {
            SetTool(EditorTool.Select);
        }
    }

    // ───────────────────────────── Recognized text ─────────────────────────────

    /// <summary>
    /// Stores a finished recognition of <paramref name="image"/>. Runs carried
    /// over from before a crop are merged in (recognition can miss a line the
    /// crop cut through); failed recognitions keep runs null.
    /// </summary>
    public void SetRecognition(IEditorImage image, TextRecognition result)
    {
        var merged = result.Runs is { } runs && _carriedRuns.TryGetValue(image, out var carried)
            ? result with { Runs = runs.Concat(carried).ToList() }
            : result;
        _recognized[image] = merged;
        if (ReferenceEquals(image, Image))
        {
            Recognition = merged;
            SelectedWords = [];
            OnChanged();
        }
    }

    public string? SelectedWordsText =>
        SelectedWords.Count == 0 ? null : WordSelection.JoinedText(Recognition.Words, SelectedWords) is { Length: > 0 } text ? text : null;

    public void ClearWordSelection()
    {
        if (SelectedWords.Count > 0)
        {
            SelectedWords = [];
            OnChanged();
        }
    }

    // ───────────────────────────── Escape ─────────────────────────────

    /// <summary>Esc: cancel the crop draft → clear the word selection → deselect. False when there was nothing to cancel (close).</summary>
    public bool HandleEscape()
    {
        if (_gesture != Gesture.None)
        {
            CancelGesture();
            return true;
        }

        if (Tool == EditorTool.Crop && CropDraft is not null)
        {
            SetTool(EditorTool.Select);
            return true;
        }

        if (SelectedWords.Count > 0)
        {
            ClearWordSelection();
            return true;
        }

        if (SelectedId is not null)
        {
            Deselect();
            return true;
        }

        return false;
    }

    // ───────────────────────────── Inline text ─────────────────────────────

    public void BeginTextEdit(Guid id, bool isNew = false)
    {
        if (Find(id) is not { Kind: AnnotationKind.Text })
        {
            return;
        }

        EditingTextId = id;
        EditingIsNew = isNew;
        SelectedId = id;
        TextEditRequested?.Invoke(this, id);
        OnChanged();
    }

    /// <summary>
    /// Commits the inline editor: text is trimmed; a new text that ends up
    /// empty disappears with every undo entry it created; unchanged text is a
    /// no-op; an edit is one undo step; an existing text emptied is deleted.
    /// </summary>
    public void CommitTextEdit(string? raw)
    {
        if (EditingTextId is not { } id)
        {
            return;
        }

        var isNew = EditingIsNew;
        EditingTextId = null;
        EditingIsNew = false;
        var text = (raw ?? string.Empty).Trim();
        if (Find(id) is not { } a)
        {
            OnChanged();
            return;
        }

        if (isNew && text.Length == 0)
        {
            _annotations = _annotations.Where(x => x.Id != id).ToList();
            _history.TruncateTo(_undoDepthBeforeNewText);
            SelectedId = null;
            OnChanged();
            return;
        }

        if (text == a.Text)
        {
            if (!isNew)
            {
                OnChanged();
                return;
            }
        }
        else if (text.Length == 0)
        {
            RecordUndo();
            _annotations = _annotations.Where(x => x.Id != id).ToList();
            SelectedId = null;
            OnChanged();
            return;
        }
        else if (!isNew)
        {
            RecordUndo();
        }

        Replace(a with { Text = text, Rect = AnnotationMetrics.TextBounds(text, a.Rect.Origin, a.TextSize, Scale, _measurer) });
        OnChanged();
    }

    // ───────────────────────────── Pointer gestures ─────────────────────────────

    public void PointerDown(ImgPoint p, ImgPoint view)
    {
        CancelGesture();
        _pressImage = p;
        _pressView = view;
        _maxViewDistance = 0;
        _moved = false;
        _pressedSelectionWithCreationTool = false;

        switch (Tool)
        {
            case EditorTool.Crop:
                BeginCropGesture(p);
                return;
            case EditorTool.Select:
                if (!BeginEditOfSelection(p))
                {
                    var hit = HitTesting.TopmostAt(_annotations, p, Scale, Image.Width, Image.Height);
                    if (hit is not null)
                    {
                        Select(hit.Id);
                        BeginMove(hit);
                    }
                    else if (WordSelection.WordAt(Recognition.Words, p, Scale) is { } word)
                    {
                        Deselect();
                        _gesture = Gesture.WordSelect;
                        SelectedWords = [word];
                        OnChanged();
                    }
                    else
                    {
                        Deselect();
                        _gesture = Gesture.EmptyTap;
                    }
                }

                return;
            default:
                if (Selected is { } selected && HitTesting.OnSelection(selected, p, Scale, Image.Width, Image.Height))
                {
                    _pressedSelectionWithCreationTool = true;
                    if (!BeginEditOfSelection(p))
                    {
                        BeginMove(selected);
                    }

                    return;
                }

                Deselect();
                if (EditorTools.IsTapTool(Tool))
                {
                    _gesture = Gesture.TapCreate;
                    return;
                }

                BeginDraft(p);
                return;
        }
    }

    public void PointerMove(ImgPoint p, ImgPoint view)
    {
        if (_gesture == Gesture.None)
        {
            return;
        }

        var distance = ImgPoint.Distance(view, _pressView);
        _maxViewDistance = Math.Max(_maxViewDistance, distance);
        var dx = p.X - _pressImage.X;
        var dy = p.Y - _pressImage.Y;

        switch (_gesture)
        {
            case Gesture.Create when _draftId is { } draftId && Find(draftId) is { } draft:
                Replace(UpdateDraft(draft, p));
                OnChanged();
                break;
            case Gesture.Move when _original is not null:
                if (StartsMoving())
                {
                    Replace(_original.Translated(dx, dy));
                    OnChanged();
                }

                break;
            case Gesture.Resize when _original is not null:
                if (StartsMoving())
                {
                    Replace(_original with { Rect = HitTesting.Resize(_original.Rect, _handle, p) });
                    OnChanged();
                }

                break;
            case Gesture.Endpoint when _original is not null:
                if (StartsMoving())
                {
                    Replace(_endpoint == 0 ? _original with { Start = p } : _original with { End = p });
                    OnChanged();
                }

                break;
            case Gesture.WordSelect:
                SelectedWords = WordSelection.Select(Recognition.Words, _pressImage, p);
                OnChanged();
                break;
            case Gesture.CropResize:
                if (_maxViewDistance >= MoveThreshold)
                {
                    CropDraft = CropMath.Snap(HitTesting.Resize(_cropOriginal, _handle, p), Bounds);
                    CropLoupePoint = HitTesting.HandlePoint(CropDraft.Value, _handle);
                    OnChanged();
                }

                break;
            case Gesture.CropMove:
                if (_maxViewDistance >= MoveThreshold)
                {
                    CropDraft = CropMath.Snap(CropMath.Move(_cropOriginal, dx, dy, Bounds), Bounds);
                    OnChanged();
                }

                break;
            case Gesture.CropNew:
                var area = ImgRect.FromPoints(_pressImage, p).Intersect(Bounds);
                CropDraft = CropMath.Snap(area, Bounds);
                OnChanged();
                break;
        }
    }

    public void PointerUp(ImgPoint p, ImgPoint view)
    {
        var gesture = _gesture;
        if (gesture == Gesture.None)
        {
            return;
        }

        PointerMove(p, view);
        _gesture = Gesture.None;
        var isTap = ImgPoint.Distance(view, _pressView) < TapRadius;
        var draftId = _draftId;
        _draftId = null;
        _original = null;
        CropLoupePoint = null;

        switch (gesture)
        {
            case Gesture.Create when draftId is { } id:
                var draft = Find(id);
                var penTap = draft?.Kind == AnnotationKind.Freehand && _maxViewDistance < TapRadius;
                var tap = draft?.Kind == AnnotationKind.Freehand ? penTap : isTap;
                if (tap || draft is null)
                {
                    // A tap leaves nothing behind: drop the draft with its undo entry, then select what is under the cursor.
                    _annotations = _annotations.Where(a => a.Id != id).ToList();
                    _history.TruncateTo(_undoDepthBeforeDraft);
                    TapSelect(p);
                }
                else
                {
                    Select(id);
                }

                break;
            case Gesture.TapCreate when isTap:
                if (!TapSelect(p) && Bounds.Contains(p))
                {
                    CreateByTap(p);
                }

                break;
            case Gesture.Move or Gesture.Resize or Gesture.Endpoint when isTap:
                if (Selected is { } selected)
                {
                    if (_pressedSelectionWithCreationTool)
                    {
                        SetTool(EditorTool.Select);
                        Select(selected.Id);
                    }

                    if (selected.Kind == AnnotationKind.Text && gesture == Gesture.Move)
                    {
                        BeginTextEdit(selected.Id);
                    }
                }

                break;
            case Gesture.WordSelect when isTap:
                if (WordSelection.WordAt(Recognition.Words, _pressImage, Scale) is { } word)
                {
                    SelectedWords = [word];
                }

                break;
            case Gesture.EmptyTap when isTap:
                SelectedWords = [];
                break;
            case Gesture.CropNew when isTap:
                CropDraft = _cropPrevious;
                break;
        }

        OnChanged();
    }

    /// <summary>Abandons a gesture in progress (lost capture); a half-drawn mark stays as drawn.</summary>
    public void CancelGesture()
    {
        if (_gesture == Gesture.None)
        {
            return;
        }

        if (_gesture == Gesture.Create && _draftId is { } id)
        {
            Select(id);
        }

        _gesture = Gesture.None;
        _draftId = null;
        _original = null;
        CropLoupePoint = null;
        OnChanged();
    }

    /// <summary>What a press at <paramref name="p"/> would act on (for cursors).</summary>
    public HoverInfo HoverAt(ImgPoint p)
    {
        if (Tool == EditorTool.Crop)
        {
            var draft = CropDraft ?? Bounds;
            if (HitTesting.HandleAt(draft, p, HitTesting.CropHandleTolerance(Scale)) is { } h)
            {
                return new HoverInfo(HoverKind.CropHandle, h);
            }

            if (!draft.NearlyEquals(Bounds) && draft.Contains(p))
            {
                return new HoverInfo(HoverKind.CropInside);
            }

            return Bounds.Contains(p) ? new HoverInfo(HoverKind.CropNew) : default;
        }

        if (Selected is { } selected)
        {
            if (selected.IsResizable && HitTesting.HandleAt(selected.Rect, p, HitTesting.HandleTolerance(Scale)) is { } handle)
            {
                return new HoverInfo(HoverKind.Handle, handle);
            }

            if (HitTesting.EndpointAt(selected, p, Scale) is not null)
            {
                return new HoverInfo(HoverKind.Endpoint);
            }

            if (EditorTools.IsCreation(Tool) && HitTesting.Hits(selected, p, Scale, Image.Width, Image.Height))
            {
                return new HoverInfo(HoverKind.SelectedBody);
            }
        }

        if (Tool == EditorTool.Select)
        {
            if (HitTesting.TopmostAt(_annotations, p, Scale, Image.Width, Image.Height) is not null)
            {
                return new HoverInfo(HoverKind.Annotation);
            }

            if (WordSelection.WordAt(Recognition.Words, p, Scale) is not null)
            {
                return new HoverInfo(HoverKind.Word);
            }
        }

        return default;
    }

    private bool StartsMoving()
    {
        if (_moved)
        {
            return true;
        }

        if (_maxViewDistance < MoveThreshold)
        {
            return false;
        }

        // The first real movement records one undo step.
        _moved = true;
        RecordUndo();
        return true;
    }

    private bool BeginEditOfSelection(ImgPoint p)
    {
        if (Selected is not { } selected)
        {
            return false;
        }

        if (selected.IsResizable && HitTesting.HandleAt(selected.Rect, p, HitTesting.HandleTolerance(Scale)) is { } handle)
        {
            _gesture = Gesture.Resize;
            _handle = handle;
            _original = selected;
            return true;
        }

        if (HitTesting.EndpointAt(selected, p, Scale) is { } endpoint)
        {
            _gesture = Gesture.Endpoint;
            _endpoint = endpoint;
            _original = selected;
            return true;
        }

        return false;
    }

    private void BeginMove(Annotation a)
    {
        _gesture = Gesture.Move;
        _original = a;
    }

    private void BeginCropGesture(ImgPoint p)
    {
        var draft = CropDraft ?? Bounds;
        CropDraft = draft;
        if (HitTesting.HandleAt(draft, p, HitTesting.CropHandleTolerance(Scale)) is { } handle)
        {
            _gesture = Gesture.CropResize;
            _handle = handle;
            _cropOriginal = draft;
            CropLoupePoint = HitTesting.HandlePoint(draft, handle);
            OnChanged();
        }
        else if (!draft.NearlyEquals(Bounds) && draft.Contains(p))
        {
            _gesture = Gesture.CropMove;
            _cropOriginal = draft;
        }
        else if (CropMath.StartsNewSelection(draft, Bounds, p))
        {
            _gesture = Gesture.CropNew;
            _cropPrevious = draft;
        }
    }

    /// <summary>Starts a drag-tool mark at the press point (selected when the drag ends).</summary>
    private void BeginDraft(ImgPoint p)
    {
        var kind = EditorTools.KindFor(Tool);
        _undoDepthBeforeDraft = _history.UndoCount;
        RecordUndo();
        var draft = NewAnnotation(kind) with
        {
            Start = p,
            End = p,
            Rect = new ImgRect(p.X, p.Y, 0, 0),
            Points = kind == AnnotationKind.Freehand ? [p] : [],
        };
        _annotations = [.. _annotations, draft];
        _draftId = draft.Id;
        _gesture = Gesture.Create;
        OnChanged();
    }

    private Annotation UpdateDraft(Annotation draft, ImgPoint p) => draft.Kind switch
    {
        AnnotationKind.Arrow or AnnotationKind.Line => draft with { End = p },
        AnnotationKind.Freehand => draft.Points.Count > 0 && draft.Points[^1] == p ? draft : draft with { Points = [.. draft.Points, p] },
        _ => draft with { Rect = ImgRect.FromPoints(_pressImage, p) },
    };

    /// <summary>Creates a text, sticker or counter at a tap inside the image.</summary>
    private void CreateByTap(ImgPoint p)
    {
        var kind = EditorTools.KindFor(Tool);
        var depth = _history.UndoCount;
        RecordUndo();
        var a = NewAnnotation(kind);
        a = kind switch
        {
            AnnotationKind.Text => a with { Rect = AnnotationMetrics.TextBounds(string.Empty, p, a.TextSize, Scale, _measurer) },
            AnnotationKind.Sticker => a with
            {
                Rect = AnnotationMetrics.StickerRect(p, AnnotationMetrics.StickerSide(Image.Width, Image.Height, Scale), Bounds),
            },
            _ => a with { Rect = new ImgRect(p.X, p.Y, 0, 0) },
        };
        _annotations = [.. _annotations, a];
        Renumber();
        SelectedId = a.Id;
        if (kind == AnnotationKind.Text)
        {
            _undoDepthBeforeNewText = depth;
            BeginTextEdit(a.Id, isNew: true);
        }

        OnChanged();
    }

    /// <summary>
    /// A creation-tool tap on an existing mark selects it and switches to the
    /// Select tool (area shapes count only near their edge); a text enters
    /// inline editing. Returns whether something was hit.
    /// </summary>
    private bool TapSelect(ImgPoint p)
    {
        var hit = HitTesting.TopmostAt(_annotations, p, Scale, Image.Width, Image.Height, creationTap: true);
        if (hit is null)
        {
            return false;
        }

        SetTool(EditorTool.Select);
        Select(hit.Id);
        if (hit.Kind == AnnotationKind.Text)
        {
            BeginTextEdit(hit.Id);
        }

        return true;
    }

    private Annotation NewAnnotation(AnnotationKind kind) => new()
    {
        Kind = kind,
        Color = Style.Color,
        Stroke = Style.Stroke,
        TextSize = Style.TextSize,
        ArrowStyle = Style.ArrowStyle,
        Seed = kind == AnnotationKind.Arrow && Style.ArrowStyle == ArrowStyle.Scribbly ? ScribbleRandom.NewSeed() : 0,
        BlurStyle = Style.BlurStyle,
        BlurLevel = Style.BlurLevel,
        TextOnly = Style.TextOnly,
        Sticker = Style.Sticker,
    };

    private void Replace(Annotation updated)
    {
        var index = IndexOf(updated.Id);
        if (index < 0)
        {
            return;
        }

        var list = _annotations.ToList();
        list[index] = updated;
        _annotations = list;
    }

    /// <summary>Replaces the list wholesale (tests and document restore). Not recorded for undo.</summary>
    public void LoadAnnotations(IEnumerable<Annotation> annotations)
    {
        _annotations = annotations.ToList();
        Renumber();
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
