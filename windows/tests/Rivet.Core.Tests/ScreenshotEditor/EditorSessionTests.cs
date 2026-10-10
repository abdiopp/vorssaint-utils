// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Core.ScreenshotEditor;
using Xunit;

namespace Rivet.Core.Tests.ScreenshotEditor;

public class EditorSessionTests
{
    private sealed record FakeImage(int Width, int Height) : IEditorImage;

    /// <summary>A session at zoom 1 (view = image coordinates), scale 1.</summary>
    private static EditorSession NewSession(EditorTool tool = EditorTool.Arrow, int width = 800, int height = 600, double scale = 1) =>
        new(new FakeImage(width, height), scale, new EditorStyle(), tool, new MetricsTests.FixedMeasurer(), (img, x, y, w, h) => new FakeImage(w, h));

    private static void Drag(EditorSession s, ImgPoint from, ImgPoint to, params ImgPoint[] via)
    {
        s.PointerDown(from, from);
        foreach (var p in via)
        {
            s.PointerMove(p, p);
        }

        s.PointerMove(to, to);
        s.PointerUp(to, to);
    }

    private static void Tap(EditorSession s, ImgPoint at) => Drag(s, at, at);

    [Fact]
    public void Dragged_shapes_are_selected_only_when_the_drag_ends()
    {
        var s = NewSession(EditorTool.Rect);
        s.PointerDown(new(100, 100), new(100, 100));
        s.PointerMove(new(200, 180), new(200, 180));
        Assert.Single(s.Annotations);
        Assert.Null(s.SelectedId);
        s.PointerUp(new(200, 180), new(200, 180));
        Assert.Equal(s.Annotations[0].Id, s.SelectedId);
        Assert.Equal(new ImgRect(100, 100, 100, 80), s.Annotations[0].Rect);
        Assert.True(s.CanUndo);
    }

    [Fact]
    public void A_tap_with_a_drag_tool_leaves_nothing_behind()
    {
        var s = NewSession(EditorTool.Arrow);
        Drag(s, new(100, 100), new(106, 100));   // 6 view points: a tap
        Assert.Empty(s.Annotations);
        Assert.False(s.CanUndo);

        Drag(s, new(100, 100), new(107, 100));   // 7: a drag
        Assert.Single(s.Annotations);
    }

    [Fact]
    public void Closed_pen_strokes_survive_but_short_ones_do_not()
    {
        var s = NewSession(EditorTool.Freehand);
        Drag(s, new(100, 100), new(102, 101), new ImgPoint(150, 100), new ImgPoint(150, 150), new ImgPoint(100, 150));
        Assert.Single(s.Annotations);

        var t = NewSession(EditorTool.Freehand);
        Drag(t, new(100, 100), new(104, 103), new ImgPoint(103, 101));
        Assert.Empty(t.Annotations);
    }

    [Fact]
    public void Tapping_an_existing_mark_with_a_creation_tool_selects_it_and_switches_to_select()
    {
        var s = NewSession(EditorTool.Rect);
        s.SetColor(AnnotationColor.Blue);
        Drag(s, new(100, 100), new(300, 300));
        s.SetColor(AnnotationColor.Green);
        s.SetTool(EditorTool.Arrow);
        s.Deselect();
        Assert.Equal(AnnotationColor.Green, s.Style.Color);

        Tap(s, new(102, 200));   // on the edge ring
        Assert.Equal(EditorTool.Select, s.Tool);
        Assert.Equal(s.Annotations[0].Id, s.SelectedId);
        Assert.Equal(AnnotationColor.Green, s.Annotations[0].Color);   // the selection changed it when green was picked
    }

    [Fact]
    public void Selecting_syncs_only_the_attributes_the_mark_uses()
    {
        var s = NewSession(EditorTool.Highlight);
        s.SetStroke(StrokeWidth.Large);
        s.SetColor(AnnotationColor.Yellow);
        Drag(s, new(10, 10), new(200, 100));
        s.Deselect();
        s.SetStroke(StrokeWidth.Small);
        s.SetColor(AnnotationColor.Red);
        s.SetTool(EditorTool.Select);
        Tap(s, new(100, 50));
        Assert.Equal(AnnotationColor.Yellow, s.Style.Color);
        Assert.Equal(StrokeWidth.Small, s.Style.Stroke);   // highlighters don't use thickness
    }

    [Fact]
    public void Picking_a_thickness_for_a_highlight_never_records_an_edit()
    {
        var s = NewSession(EditorTool.Highlight);
        Drag(s, new(10, 10), new(200, 100));
        var before = s.Snapshot();
        s.Undo();
        s.Redo();
        s.Select(s.Annotations[0].Id);
        s.SetStroke(StrokeWidth.Large);
        Assert.Equal(before.Annotations, s.Annotations);
        s.Undo();
        Assert.Empty(s.Annotations);   // the only entry is the creation
    }

    [Fact]
    public void Counters_renumber_without_gaps_after_delete_and_reorder()
    {
        var s = NewSession(EditorTool.Counter);
        Tap(s, new(100, 100));
        Tap(s, new(200, 100));
        Tap(s, new(300, 100));
        Assert.Equal([1, 2, 3], s.Annotations.Select(a => a.Number));

        s.Select(s.Annotations[0].Id);
        s.DeleteSelected();
        Assert.Equal([1, 2], s.Annotations.Select(a => a.Number));
        Assert.Equal(200, s.Annotations[0].Rect.X);

        s.Select(s.Annotations[1].Id);
        Assert.False(s.CanBringForward);
        Assert.True(s.CanSendBackward);
        s.SendBackward();
        Assert.Equal(300, s.Annotations[0].Rect.X);
        Assert.Equal([1, 2], s.Annotations.Select(a => a.Number));
        Assert.False(s.CanSendBackward);
    }

    [Fact]
    public void Tap_tools_ignore_taps_outside_the_image()
    {
        var s = NewSession(EditorTool.Sticker, 400, 300);
        Tap(s, new(500, 100));
        Assert.Empty(s.Annotations);
        Tap(s, new(390, 290));
        Assert.Single(s.Annotations);
        Assert.True(new ImgRect(0, 0, 400, 300).Contains(s.Annotations[0].Rect.Origin));
    }

    [Fact]
    public void A_new_text_left_empty_disappears_with_its_undo_entries()
    {
        var s = NewSession(EditorTool.Text);
        Guid? requested = null;
        s.TextEditRequested += (_, id) => requested = id;
        Tap(s, new(50, 50));
        Assert.NotNull(requested);
        Assert.Equal(requested, s.EditingTextId);
        Assert.True(s.EditingIsNew);
        s.SetColor(AnnotationColor.Blue);   // a style change while editing records an entry too
        s.CommitTextEdit("   ");
        Assert.Empty(s.Annotations);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void Text_edits_trim_remeasure_and_record_one_step()
    {
        var s = NewSession(EditorTool.Text);
        Tap(s, new(50, 50));
        s.CommitTextEdit("  Hello  ");
        var text = s.Annotations.Single();
        Assert.Equal("Hello", text.Text);
        Assert.Equal(Math.Ceiling(5 * 19 * 0.5) + 4, text.Rect.Width);

        s.SetTool(EditorTool.Select);
        Tap(s, new(60, 60));            // a tap on a text enters inline editing
        Assert.Equal(text.Id, s.EditingTextId);
        s.CommitTextEdit("Hello");      // unchanged: no-op
        s.BeginTextEdit(text.Id);
        s.CommitTextEdit("Bye");
        Assert.Equal("Bye", s.Annotations.Single().Text);
        s.Undo();
        Assert.Equal("Hello", s.Annotations.Single().Text);
        s.Redo();
        s.BeginTextEdit(text.Id);
        s.CommitTextEdit(string.Empty);   // an existing text emptied is deleted
        Assert.Empty(s.Annotations);
    }

    [Fact]
    public void Moving_records_one_undo_step_on_the_first_real_movement()
    {
        var s = NewSession(EditorTool.Rect);
        Drag(s, new(100, 100), new(200, 200));
        s.SetTool(EditorTool.Select);
        s.PointerDown(new(120, 130), new(120, 130));
        s.PointerMove(new(121, 130), new(121, 130));     // jitter: no nudge
        Assert.Equal(100, s.Annotations[0].Rect.X);
        s.PointerMove(new(160, 140), new(160, 140));
        s.PointerMove(new(170, 150), new(170, 150));
        s.PointerUp(new(170, 150), new(170, 150));
        Assert.Equal(new ImgRect(150, 120, 100, 100), s.Annotations[0].Rect);
        s.Undo();
        Assert.Equal(new ImgRect(100, 100, 100, 100), s.Annotations[0].Rect);
    }

    [Fact]
    public void Resizing_past_the_opposite_edge_flips()
    {
        var s = NewSession(EditorTool.Ellipse);
        Drag(s, new(100, 100), new(200, 200));
        Drag(s, new(200, 200), new(50, 50));   // the selected shape's bottom-right handle, with a creation tool
        Assert.Equal(new ImgRect(50, 50, 50, 50), s.Annotations.Single().Rect);
        Assert.Equal(EditorTool.Ellipse, s.Tool);
    }

    [Fact]
    public void Dragging_an_arrow_endpoint_moves_only_that_end()
    {
        var s = NewSession(EditorTool.Arrow);
        Drag(s, new(100, 100), new(300, 100));
        Drag(s, new(300, 100), new(300, 250));
        var arrow = s.Annotations.Single();
        Assert.Equal(new ImgPoint(100, 100), arrow.Start);
        Assert.Equal(new ImgPoint(300, 250), arrow.End);
    }

    [Fact]
    public void Scribbly_arrows_get_a_seed_and_a_new_one_on_each_switch()
    {
        var s = NewSession(EditorTool.Arrow);
        s.SetArrowStyle(ArrowStyle.Scribbly);
        Drag(s, new(100, 100), new(300, 100));
        var first = s.Annotations.Single().Seed;
        Assert.NotEqual(0UL, first);
        s.SetArrowStyle(ArrowStyle.Open);
        s.SetArrowStyle(ArrowStyle.Scribbly);
        Assert.NotEqual(first, s.Annotations.Single().Seed);
    }

    [Fact]
    public void Crop_snaps_applies_and_shifts_marks()
    {
        IEditorImage? cropped = null;
        var s = new EditorSession(new FakeImage(800, 600), 1, new EditorStyle(), EditorTool.Rect, new MetricsTests.FixedMeasurer(),
            (img, x, y, w, h) => cropped = new FakeImage(w, h));
        var imageChanges = 0;
        s.ImageChanged += (_, _) => imageChanges++;
        Drag(s, new(300, 300), new(400, 400));
        s.SetTool(EditorTool.Crop);
        Assert.Equal(new ImgRect(0, 0, 800, 600), s.CropDraft);
        Assert.Null(s.SelectedId);

        Drag(s, new(100.4, 100.6), new(500.2, 450.5));   // a new crop rectangle inside the full-image draft
        Assert.Equal(new ImgRect(100, 101, 400, 350), s.CropDraft);
        Assert.True(s.ApplyCrop());
        Assert.Same(cropped, s.Image);
        Assert.Equal(EditorTool.Select, s.Tool);
        Assert.Equal(new ImgRect(200, 199, 100, 100), s.Annotations.Single().Rect);
        Assert.Equal(1, imageChanges);

        s.Undo();
        Assert.Equal(800, s.Image.Width);
        Assert.Equal(new ImgRect(300, 300, 100, 100), s.Annotations.Single().Rect);
        Assert.Equal(2, imageChanges);
    }

    [Fact]
    public void A_crop_smaller_than_eight_pixels_only_switches_to_select()
    {
        var s = NewSession(EditorTool.Crop);
        Drag(s, new(100, 100), new(107, 140));
        Assert.False(s.ApplyCrop());
        Assert.Equal(EditorTool.Select, s.Tool);
        Assert.Equal(800, s.Image.Width);
    }

    [Fact]
    public void A_tap_in_crop_mode_restores_the_previous_draft()
    {
        var s = NewSession(EditorTool.Crop);
        Drag(s, new(100, 100), new(300, 300));
        var draft = s.CropDraft;
        Tap(s, new(500, 500));
        Assert.Equal(draft, s.CropDraft);
    }

    [Fact]
    public void Escape_cancels_crop_then_words_then_selection_then_closes()
    {
        var s = NewSession(EditorTool.Rect);
        Drag(s, new(10, 10), new(100, 100));
        s.SetTool(EditorTool.Crop);
        Assert.True(s.HandleEscape());
        Assert.Equal(EditorTool.Select, s.Tool);
        s.SetRecognition(s.Image, new TextRecognition([new RecognizedWord("w", new ImgRect(300, 300, 40, 20), 0)], []));
        Tap(s, new(310, 310));
        Assert.Single(s.SelectedWords);
        Assert.Equal("w", s.SelectedWordsText);
        Assert.True(s.HandleEscape());
        Assert.Empty(s.SelectedWords);
        s.Select(s.Annotations[0].Id);
        Assert.True(s.HandleEscape());
        Assert.Null(s.SelectedId);
        Assert.False(s.HandleEscape());
    }

    [Fact]
    public void Carried_runs_merge_with_the_fresh_recognition_after_a_crop()
    {
        var s = NewSession(EditorTool.Crop);
        s.SetRecognition(s.Image, new TextRecognition([], [new ImgRect(150, 150, 50, 20)]));
        Drag(s, new(100, 100), new(500, 500));
        s.ApplyCrop();
        Assert.Equal([new ImgRect(50, 50, 50, 20)], s.Recognition.Runs);
        s.SetRecognition(s.Image, new TextRecognition([], [new ImgRect(10, 10, 5, 5)]));
        Assert.Equal(2, s.Recognition.Runs!.Count);
        // A failed fresh recognition keeps runs null (cover whole areas).
        s.SetRecognition(s.Image, new TextRecognition([], null));
        Assert.Null(s.Recognition.Runs);
    }

    [Fact]
    public void Dirty_tracks_content_against_the_clean_snapshot()
    {
        var s = NewSession(EditorTool.Rect);
        Assert.False(s.IsContentDirty);
        Drag(s, new(10, 10), new(100, 100));
        Assert.True(s.IsContentDirty);
        s.MarkClean();
        Assert.False(s.IsContentDirty);
        s.Undo();
        Assert.True(s.IsContentDirty);
        s.Redo();
        Assert.False(s.IsContentDirty);
    }

    [Fact]
    public void A_refused_blur_sample_snaps_the_controls_back()
    {
        var s = NewSession(EditorTool.Pixelate);
        Drag(s, new(10, 10), new(100, 100));
        var styleEvents = 0;
        s.StyleChanged += (_, _) => styleEvents++;
        s.EnsureBlurSample = (style, level) => level != 5;
        s.SetBlurLevel(5);
        Assert.Equal(3, s.Annotations.Single().BlurLevel);
        Assert.Equal(3, s.Style.BlurLevel);
        Assert.Equal(1, styleEvents);
        s.SetBlurStyle(BlurStyle.Erase);
        Assert.Equal(BlurStyle.Erase, s.Annotations.Single().BlurStyle);
    }

    [Fact]
    public void Undo_keeps_sixty_steps()
    {
        var s = NewSession(EditorTool.Line);
        for (var i = 0; i < 70; i++)
        {
            Drag(s, new(10, 10 + (i * 30)), new(100, 10 + (i * 30)));
        }

        var undos = 0;
        while (s.CanUndo)
        {
            s.Undo();
            undos++;
        }

        Assert.Equal(60, undos);
        Assert.Equal(10, s.Annotations.Count);
    }

    [Fact]
    public void Layer_buttons_ignore_a_shape_still_being_drawn()
    {
        var s = NewSession(EditorTool.Rect);
        Drag(s, new(10, 10), new(100, 100));
        s.PointerDown(new(200, 200), new(200, 200));
        s.PointerMove(new(300, 300), new(300, 300));
        Assert.Equal(2, s.Annotations.Count);
        Assert.False(s.ShowsLayerButtons);
        s.PointerUp(new(300, 300), new(300, 300));
        Assert.True(s.ShowsLayerButtons);
    }

    [Fact]
    public void Annotations_are_serializable_for_snapshots()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var a = new Annotation
        {
            Kind = AnnotationKind.Freehand, Points = [new(1, 2), new(3, 4)], Color = AnnotationColor.Purple, Stroke = StrokeWidth.Large,
        };
        var json = JsonSerializer.Serialize(a, options);
        var back = JsonSerializer.Deserialize<Annotation>(json, options);
        Assert.Equal(a, back);
        Assert.Equal(new ImgRect(1, 2, 2, 2), back!.Bounds(10, 10));
    }
}
