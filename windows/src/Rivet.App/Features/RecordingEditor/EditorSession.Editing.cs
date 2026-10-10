// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>Editing operations: trim, cuts, lanes, inspector values (spec 02 §3.20.7, §3.21–§3.31).</summary>
public sealed partial class EditorSession
{
    // ── Trim and cuts (§3.20.7) ───────────────────────────────────────

    /// <summary>Live while dragging the start handle; the dragged handle gives way at 0.2 s.</summary>
    public void DragTrimStart(double t)
    {
        var trim = Timeline.Trim;
        var start = Math.Clamp(t, 0, Math.Max(0, trim.End - EditTimeline.MinimumTrim));
        SetLive(Document with { TrimStart = start, TrimEnd = trim.End });
        Playback.Seek(0);
    }

    public void DragTrimEnd(double t)
    {
        var trim = Timeline.Trim;
        var end = Math.Clamp(t, Math.Min(Duration, trim.Start + EditTimeline.MinimumTrim), Duration);
        SetLive(Document with { TrimStart = trim.Start, TrimEnd = end });
        Playback.Seek(Timeline.NearestOutputTime(Math.Max(trim.Start, end - 0.05)));
    }

    public bool CanCutOut =>
        CutSelection is { } range && range.Length >= EditTimeline.MinimumCut
        && Timeline.OutputDurationWith(new CutRange(range.Start, range.End)) >= EditTimeline.MinimumOutput;

    /// <summary>Removes the selected stretch (one undo step) and seeks to where it was.</summary>
    public void CutOut()
    {
        if (!CanCutOut || CutSelection is not { } range)
        {
            return;
        }

        var cuts = EditTimeline.NormalizeCuts(Document.Cuts.Append(new CutRange(range.Start, range.End)), Duration);
        CutSelection = null;
        Apply(Document with { Cuts = cuts });
        SeekSource(range.Start);
    }

    /// <summary>Clicking a cut's seam restores it.</summary>
    public bool RestoreCutAt(double sourceTime)
    {
        var index = EditTimeline.CutIndexAt(Document.Cuts, sourceTime);
        if (index < 0)
        {
            return false;
        }

        Apply(Document with { Cuts = Document.Cuts.Where((_, i) => i != index).ToList() });
        return true;
    }

    // ── Lane items (§3.21) ────────────────────────────────────────────

    public IReadOnlyList<(string Id, double Start, double End)> Items(LaneKind kind) => kind switch
    {
        LaneKind.Zoom => Document.ZoomSegments.Select(z => (z.Id, z.Start, z.End)).ToList(),
        LaneKind.Text => Document.Texts.Select(t => (t.Id, t.Start, t.End)).ToList(),
        LaneKind.Image => Document.Images.Select(i => (i.Id, i.Start, i.End)).ToList(),
        _ => Document.Blurs.Select(b => (b.Id, b.Start, b.End)).ToList(),
    };

    /// <summary>Snap candidates: 0, the end, the playhead, every press, and the other blocks of the lane.</summary>
    public IEnumerable<double> SnapCandidates(LaneKind kind, string? exceptId)
    {
        yield return 0;
        yield return Duration;
        yield return PlayheadSource;
        foreach (var press in Take.Pointer.Clicks.Where(c => c.IsDown))
        {
            yield return press.Time;
        }

        foreach (var (id, start, end) in Items(kind))
        {
            if (id != exceptId)
            {
                yield return start;
                yield return end;
            }
        }
    }

    public void MoveItem(LaneKind kind, string id, double proposedStart)
    {
        var doc = Document;
        switch (kind)
        {
            case LaneKind.Zoom when doc.ZoomSegments.FirstOrDefault(z => z.Id == id) is { } zoom:
                var range = LaneMath.MoveZoom(zoom, proposedStart, doc.ZoomSegments, Duration);
                SetLive(doc with { ZoomSegments = Replace(doc.ZoomSegments, id, z => z with { Start = range.Start, End = range.End }) });
                break;
            case LaneKind.Text when doc.Texts.FirstOrDefault(t => t.Id == id) is { } text:
                var tr = LaneMath.MoveFree(text.Start, text.End, proposedStart, Duration);
                SetLive(doc with { Texts = Replace(doc.Texts, id, t => t with { Start = tr.Start, End = tr.End }) });
                break;
            case LaneKind.Image when doc.Images.FirstOrDefault(i => i.Id == id) is { } image:
                var ir = LaneMath.MoveFree(image.Start, image.End, proposedStart, Duration);
                SetLive(doc with { Images = Replace(doc.Images, id, i => i with { Start = ir.Start, End = ir.End }) });
                break;
            case LaneKind.Blur when doc.Blurs.FirstOrDefault(b => b.Id == id) is { } blur:
                var br = LaneMath.MoveFree(blur.Start, blur.End, proposedStart, Duration);
                SetLive(doc with { Blurs = Replace(doc.Blurs, id, b => b with { Start = br.Start, End = br.End }) });
                break;
        }
    }

    public void ResizeItem(LaneKind kind, string id, bool startEdge, double t)
    {
        var doc = Document;
        switch (kind)
        {
            case LaneKind.Zoom when doc.ZoomSegments.FirstOrDefault(z => z.Id == id) is { } zoom:
                var range = LaneMath.ResizeZoom(zoom, startEdge, t, doc.ZoomSegments, Duration);
                SetLive(doc with { ZoomSegments = Replace(doc.ZoomSegments, id, z => z with { Start = range.Start, End = range.End }) });
                break;
            case LaneKind.Text when doc.Texts.FirstOrDefault(x => x.Id == id) is { } text:
                var tr = LaneMath.ResizeFree(text.Start, text.End, startEdge, t, Duration);
                SetLive(doc with { Texts = Replace(doc.Texts, id, x => x with { Start = tr.Start, End = tr.End }) });
                break;
            case LaneKind.Image when doc.Images.FirstOrDefault(i => i.Id == id) is { } image:
                var ir = LaneMath.ResizeFree(image.Start, image.End, startEdge, t, Duration);
                SetLive(doc with { Images = Replace(doc.Images, id, i => i with { Start = ir.Start, End = ir.End }) });
                break;
            case LaneKind.Blur when doc.Blurs.FirstOrDefault(b => b.Id == id) is { } blur:
                var br = LaneMath.ResizeFree(blur.Start, blur.End, startEdge, t, Duration);
                SetLive(doc with { Blurs = Replace(doc.Blurs, id, b => b with { Start = br.Start, End = br.End }) });
                break;
        }
    }

    /// <summary>A click on empty lane space adds an item of that lane's kind at <paramref name="t"/>.</summary>
    public void AddAt(LaneKind kind, double t)
    {
        switch (kind)
        {
            case LaneKind.Zoom:
                AddZoomAt(t);
                break;
            case LaneKind.Text:
                AddTextAt(t);
                break;
            case LaneKind.Blur:
                AddBlurAt(t);
                break;
        }
    }

    public void Remove(LaneKind kind, string id)
    {
        var doc = Document;
        Apply(kind switch
        {
            LaneKind.Zoom => doc with { ZoomSegments = doc.ZoomSegments.Where(z => z.Id != id).ToList(), ZoomsGenerated = true },
            LaneKind.Text => doc with { Texts = doc.Texts.Where(t => t.Id != id).ToList() },
            LaneKind.Image => doc with { Images = doc.Images.Where(i => i.Id != id).ToList() },
            _ => doc with { Blurs = doc.Blurs.Where(b => b.Id != id).ToList() },
        });
    }

    public void RemoveSelected()
    {
        if (SelectedKind is { } kind && SelectedId is { } id)
        {
            Remove(kind, id);
        }
    }

    private static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> items, string id, Func<T, T> change)
        where T : class =>
        items.Select(item => IdOf(item) == id ? change(item) : item).ToList();

    private static string IdOf(object item) => item switch
    {
        ZoomSegment z => z.Id,
        TextOverlay t => t.Id,
        ImageOverlay i => i.Id,
        BlurRegion b => b.Id,
        _ => string.Empty,
    };

    // ── Zooms (§3.22) ─────────────────────────────────────────────────

    public ZoomSegment? SelectedZoom => SelectedKind == LaneKind.Zoom ? Document.ZoomSegments.FirstOrDefault(z => z.Id == SelectedId) : null;

    public void AddZoomAt(double t)
    {
        if (LaneMath.SlotForNewZoom(t, Document.ZoomSegments, Duration) is not { } slot)
        {
            return;
        }

        var zoom = new ZoomSegment
        {
            Id = EditDocument.NewId(),
            Start = slot.Start,
            End = slot.End,
            Amount = ZoomSegment.SanitizeAmount(LastZoomAmount ?? Document.ZoomAmount),
        };
        Apply(Document with { ZoomSegments = Document.ZoomSegments.Append(zoom).ToList(), ZoomEnabled = true, ZoomsGenerated = true });
        Select(LaneKind.Zoom, zoom.Id);
    }

    /// <summary>Live while dragging "How close"; remembered for new zooms.</summary>
    public void SetZoomAmount(string id, double amount)
    {
        amount = ZoomSegment.SanitizeAmount(amount);
        LastZoomAmount = amount;
        SetLive(Document with { ZoomSegments = Replace(Document.ZoomSegments, id, z => z with { Amount = amount }) });
    }

    public void SetZoomFocus(string id, double x, double y)
    {
        Apply(Document with { ZoomSegments = Replace(Document.ZoomSegments, id, z => z with { FocusX = Math.Clamp(x, 0, 1), FocusY = Math.Clamp(y, 0, 1) }) });
    }

    public void FollowPointer(string id)
    {
        EndModes();
        Apply(Document with { ZoomSegments = Replace(Document.ZoomSegments, id, z => z with { FocusX = null, FocusY = null }) });
    }

    /// <summary>"Zoom in on every click": off keeps the blocks but disables them; on re-enables and fills an empty lane.</summary>
    public void SetZoomEnabled(bool enabled)
    {
        var doc = Document with { ZoomEnabled = enabled };
        if (enabled && doc.ZoomSegments.Count == 0)
        {
            doc = WithGeneratedZooms(doc);
        }

        Apply(doc);
    }

    /// <summary>"Keep zoomed in while typing": regenerates every zoom (undoable) and turns zoom on.</summary>
    public void SetZoomsOnTyping(bool on) => Apply(WithGeneratedZooms(Document with { ZoomsOnTyping = on, ZoomEnabled = true }));

    /// <summary>"Back to one per click" / "Create automatic zooms".</summary>
    public void RegenerateZooms()
    {
        ClearSelection();
        Apply(WithGeneratedZooms(Document with { ZoomEnabled = true }));
    }

    public bool AutomaticZoomsWouldExist =>
        HasPointerTrack && AutoZoom.Generate(Take.Pointer.Clicks, Document.ZoomsOnTyping ? Take.Typing.Times : null, Duration, Document.ZoomAmount).Count > 0;

    private EditDocument WithGeneratedZooms(EditDocument doc) => doc with
    {
        ZoomSegments = HasPointerTrack
            ? AutoZoom.Generate(Take.Pointer.Clicks, doc.ZoomsOnTyping ? Take.Typing.Times : null, Duration, doc.ZoomAmount)
            : [],
        ZoomsGenerated = true,
    };

    /// <summary>
    /// The global "How close": the amount for new and regenerated zooms and
    /// the follow clusters. Unlike macOS it also moves every zoom that follows
    /// the pointer, so the slider visibly does something (hand-aimed zooms keep their own).
    /// </summary>
    public void SetGlobalZoomAmount(double amount)
    {
        amount = ZoomSegment.SanitizeAmount(amount);
        SetLive(Document with
        {
            ZoomAmount = amount,
            ZoomSegments = Document.ZoomSegments.Select(z => z.IsAimed ? z : z with { Amount = amount }).ToList(),
        });
    }

    // ── Captions (§3.23) ──────────────────────────────────────────────

    public TextOverlay? SelectedText => SelectedKind == LaneKind.Text ? Document.Texts.FirstOrDefault(t => t.Id == SelectedId) : null;

    public void AddTextAt(double t)
    {
        if (Duration <= LaneMath.MinimumLength)
        {
            return;
        }

        var start = LaneMath.NewItemStart(t, Duration);
        var text = new TextOverlay
        {
            Id = EditDocument.NewId(),
            Text = L.Get("recorder.textPlaceholder"),
            Start = start,
            End = Math.Min(Duration, start + 3.0),
        };
        Apply(Document with { Texts = Document.Texts.Append(text).ToList() });
        Select(LaneKind.Text, text.Id);
    }

    public void SetText(string id, string text) =>
        SetLive(Document with { Texts = Replace(Document.Texts, id, t => t with { Text = text.Length > TextOverlay.MaxLength ? text[..TextOverlay.MaxLength] : text }) });

    public void SetTextSize(string id, double size) => SetLive(Document with { Texts = Replace(Document.Texts, id, t => t with { Size = size }) });

    public void SetTextAnchor(string id, OverlayAnchor anchor) => Apply(Document with { Texts = Replace(Document.Texts, id, t => t with { Anchor = anchor }) });

    public void SetTextPalette(string id, CaptionPalette palette) => Apply(Document with { Texts = Replace(Document.Texts, id, t => t with { Palette = palette }) });

    // ── Image overlays (§3.24) ────────────────────────────────────────

    public ImageOverlay? SelectedImage => SelectedKind == LaneKind.Image ? Document.Images.FirstOrDefault(i => i.Id == SelectedId) : null;

    /// <summary>Copies the picture into the take off the UI thread, then adds it at <paramref name="t"/>.</summary>
    public async Task AddImageAsync(string sourcePath, double t)
    {
        if (!IsReady || Duration <= LaneMath.MinimumLength)
        {
            return;
        }

        string copy;
        try
        {
            copy = await Task.Run(() =>
            {
                var path = RecordingFolders.CopyIntoPrivateFolder(sourcePath, Take.Folder);
                using var image = OrientedImage.Load(path, 64);
                if (image is null)
                {
                    RecordingFolders.TryDeleteFolder(Path.GetDirectoryName(path)!);
                    throw new IOException("Not a picture.");
                }

                return path;
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("recording-editor", "Could not add an image.", ex);
            Hud?.Show(L.Get("recorder.imageImportFailed"), HudStyle.Error, duration: HudDuration);
            return;
        }

        if (_disposed)
        {
            // The editor closed while copying: remove the copy, recreate nothing.
            RecordingFolders.TryDeleteFolder(Path.GetDirectoryName(copy)!);
            return;
        }

        var image = new ImageOverlay
        {
            Id = EditDocument.NewId(),
            Path = copy,
            Start = LaneMath.NewItemStart(t, Duration),
            End = Duration,
        };
        Apply(Document with { Images = Document.Images.Append(image).ToList() });
        Select(LaneKind.Image, image.Id);
    }

    public void SetImageSize(string id, double size) => SetLive(Document with { Images = Replace(Document.Images, id, i => i with { Size = size }) });

    public void SetImageOpacity(string id, double opacity) => SetLive(Document with { Images = Replace(Document.Images, id, i => i with { Opacity = opacity }) });

    public void SetImageAnchor(string id, OverlayAnchor anchor) => Apply(Document with { Images = Replace(Document.Images, id, i => i with { Anchor = anchor }) });

    // ── Blur regions (§3.25) ──────────────────────────────────────────

    public BlurRegion? SelectedBlur => SelectedKind == LaneKind.Blur ? Document.Blurs.FirstOrDefault(b => b.Id == SelectedId) : null;

    /// <summary>Adds a blur for the rest of the video at <paramref name="t"/> and starts drawing its area.</summary>
    public void AddBlurAt(double t)
    {
        if (Duration <= LaneMath.MinimumLength)
        {
            return;
        }

        var blur = new BlurRegion { Id = EditDocument.NewId(), Start = LaneMath.NewItemStart(t, Duration), End = Duration };
        Apply(Document with { Blurs = Document.Blurs.Append(blur).ToList() });
        Select(LaneKind.Blur, blur.Id);
        StartDrawingBlur();
    }

    /// <summary>The drawn area (normalized); under 1 % of the picture on either side keeps the previous one.</summary>
    public void SetBlurArea(string id, double x0, double y0, double x1, double y1)
    {
        var left = Math.Clamp(Math.Min(x0, x1), 0, 1);
        var right = Math.Clamp(Math.Max(x0, x1), 0, 1);
        var top = Math.Clamp(Math.Min(y0, y1), 0, 1);
        var bottom = Math.Clamp(Math.Max(y0, y1), 0, 1);
        if (right - left >= 0.01 && bottom - top >= 0.01)
        {
            Apply(Document with { Blurs = Replace(Document.Blurs, id, b => b with { X = left, Y = top, Width = right - left, Height = bottom - top }) });
        }

        EndModes();
    }

    public void SetBlurStrength(string id, int strength) => SetLive(Document with { Blurs = Replace(Document.Blurs, id, b => b with { Strength = Math.Clamp(strength, 1, 5) }) });

    // ── Look tab (§3.27–§3.29) ────────────────────────────────────────

    public void ApplyLook(RecorderLook look) =>
        Apply(Looks.RestoreAutomaticZooms(Looks.Apply(look, Document), Take.Pointer, Take.Typing, Duration));

    /// <summary>Picking a swatch keeps the current margin, corners and blur.</summary>
    public void SetBackdropLook(RecorderBackdrop look)
    {
        var current = Document.BackdropStyle;
        var next = look.HasBackdrop ? current.WithLook(look) : current with { Kind = RecorderBackdropKind.None, PresetId = null, Colors = null, ImagePath = null };
        Apply(Document with { Backdrop = next.ToDocumentString() });
    }

    public void SetBackdropSliders(double? padding = null, double? corners = null, double? blur = null)
    {
        var style = Document.BackdropStyle;
        style = style with
        {
            Padding = padding ?? style.Padding,
            CornerRadius = corners ?? style.CornerRadius,
            Blur = blur ?? style.Blur,
        };
        SetLive(Document with { Backdrop = style.ToDocumentString() });
    }

    public void SetAspect(CanvasAspect aspect) => Apply(Document with { Aspect = aspect });

    public void SetShowsPointer(bool on) => Apply(Document with { ShowsPointer = on });

    public void SetSmoothing(PointerSmoothing smoothing) => Apply(Document with { PointerSmoothing = smoothing });

    public void SetPointerSize(double size) => SetLive(Document with { PointerSize = size });

    public void SetClickRing(bool on) => Apply(Document with { ShowsClickRing = on });

    // ── Audio (§3.26) ─────────────────────────────────────────────────

    public void SetKeepsSystemAudio(bool keep) => Apply(Document with { KeepsSystemAudio = keep });

    public void SetKeepsMicrophone(bool keep) => Apply(Document with { KeepsMicrophone = keep });

    public void SetSystemGain(double gain) => SetLive(Document with { SystemAudioGain = gain });

    public void SetMicrophoneGain(double gain) => SetLive(Document with { MicrophoneGain = gain });

    // ── Export settings (§3.30, §3.31) ────────────────────────────────

    public void SetExportSpeed(double speed) => Apply(Document with { ExportSpeed = ExportSpeed.Rounded(speed) });

    public void SetQuality(ExportQuality quality) => Apply(Document with { Quality = quality });

    /// <summary>The "Quality" readout: canvas × quality scale, even.</summary>
    public (int Width, int Height) ExportSize(ExportQuality quality) =>
        CanvasLayout.ExportSize(SourceWidth, SourceHeight, Document.BackdropStyle, Document.Aspect, quality);

    public static readonly TimeSpan HudDuration = TimeSpan.FromSeconds(1.5);
}
