// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Rivet.Core.RecordingEditor;
using Xunit;

namespace Rivet.Core.Tests.RecordingEditor;

public class EditDocumentTests
{
    private const string SpecExample = """
        {
          "trimStart": 1.25, "trimEnd": 41.8, "quality": "balanced", "exportSpeed": 1.25,
          "keepsSystemAudio": true, "keepsMicrophone": true, "systemAudioGain": 0.6, "microphoneGain": 1,
          "gifSize": "medium", "gifFrameRate": 12,
          "backdrop": "{\"kind\":\"preset\",\"presetID\":\"graphite\",\"padding\":0.45,\"cornerRadius\":0.35,\"blur\":0}",
          "aspect": "wide", "showsPointer": true, "pointerSmoothing": "smooth", "pointerSize": 1.2,
          "showsClickRing": true, "zoomEnabled": true, "zoomAmount": 1.8, "zoomsOnTyping": false,
          "zoomsGenerated": true,
          "cuts": [{"start": 12.4, "end": 15.0}],
          "zoomSegments": [
            {"id": "6F1E", "start": 2.7, "end": 5.5, "amount": 1.8},
            {"id": "A03B", "start": 20.0, "end": 22.0, "amount": 2.2, "focusX": 0.81, "focusY": 0.12}
          ],
          "texts": [{"id": "t1", "text": "Click Save", "start": 3, "end": 6, "anchor": "bottom", "size": 0.06, "palette": "white"}],
          "images": [{"id": "i1", "path": "C:\\Takes\\Take-1\\9C\\logo.png", "start": 0, "end": 41.8, "anchor": "bottomTrailing", "size": 0.18, "opacity": 1}],
          "blurs": [{"id": "b1", "start": 8, "end": 41.8, "x": 0.35, "y": 0.4, "width": 0.3, "height": 0.2, "strength": 3}]
        }
        """;

    [Fact]
    public void Reads_the_spec_example()
    {
        var doc = EditDocument.FromJson(SpecExample)!;
        Assert.Equal(1.25, doc.TrimStart);
        Assert.Equal(41.8, doc.TrimEnd);
        Assert.Equal(1.25, doc.ExportSpeed);
        Assert.Equal(0.6, doc.SystemAudioGain);
        Assert.Equal(CanvasAspect.Wide, doc.Aspect);
        Assert.Equal(1.2, doc.PointerSize);
        Assert.Single(doc.Cuts);
        Assert.Equal(2, doc.ZoomSegments.Count);
        Assert.False(doc.ZoomSegments[0].IsAimed);
        Assert.True(doc.ZoomSegments[1].IsAimed);
        Assert.Equal(0.81, doc.ZoomSegments[1].FocusX);
        Assert.Equal("Click Save", doc.Texts[0].Text);
        Assert.Equal(OverlayAnchor.BottomTrailing, doc.Images[0].Anchor);
        Assert.Equal(3, doc.Blurs[0].Strength);
        Assert.Equal(RecorderBackdropKind.Preset, doc.BackdropStyle.Kind);
        Assert.Equal("graphite", doc.BackdropStyle.PresetId);
    }

    [Fact]
    public void Missing_keys_take_defaults_and_unknown_keys_are_ignored()
    {
        var doc = EditDocument.FromJson("""{"trimStart": 2, "futureKey": [1, 2, 3]}""")!;
        Assert.Equal(2, doc.TrimStart);
        Assert.Equal(0, doc.TrimEnd);
        Assert.Equal(ExportQuality.Balanced, doc.Quality);
        Assert.Equal(1, doc.ExportSpeed);
        Assert.Equal(GifSize.Medium, doc.GifSize);
        Assert.Equal(12, doc.GifFrameRate);
        Assert.Equal(string.Empty, doc.Backdrop);
        Assert.Equal(PointerSmoothing.Smooth, doc.PointerSmoothing);
        Assert.True(doc.ShowsPointer && doc.ShowsClickRing && doc.ZoomEnabled && doc.KeepsMicrophone && doc.KeepsSystemAudio);
        Assert.Equal(1.8, doc.ZoomAmount);
        Assert.False(doc.ZoomsGenerated);
        Assert.Empty(doc.Cuts);
    }

    [Fact]
    public void Legacy_blur_without_strength_reads_as_three()
    {
        var doc = EditDocument.FromJson("""{"blurs":[{"id":"b","start":1,"end":3,"x":0.1,"y":0.1,"width":0.2,"height":0.2}]}""")!;
        Assert.Equal(3, doc.Blurs[0].Strength);
    }

    [Fact]
    public void Unreadable_json_is_null()
    {
        Assert.Null(EditDocument.FromJson("{not json"));
        Assert.Null(EditDocument.FromJson("[1,2]"));
    }

    [Fact]
    public void Round_trips_with_the_macos_key_names()
    {
        var doc = EditDocument.FromJson(SpecExample)!;
        var json = doc.ToJson();
        foreach (var key in new[] { "trimStart", "trimEnd", "quality", "exportSpeed", "keepsSystemAudio", "gifSize", "gifFrameRate",
                     "backdrop", "aspect", "showsPointer", "pointerSmoothing", "pointerSize", "showsClickRing", "zoomEnabled",
                     "zoomAmount", "zoomsOnTyping", "cuts", "zoomSegments", "zoomsGenerated", "texts", "images", "blurs",
                     "keepsMicrophone", "systemAudioGain", "microphoneGain" })
        {
            Assert.Contains($"\"{key}\":", json, StringComparison.Ordinal);
        }

        var back = EditDocument.FromJson(json)!;
        Assert.Equal(doc, back);
        // Follow-pointer zooms carry no focus keys.
        var zoom = (JsonNode.Parse(json)!["zoomSegments"] as JsonArray)![0]!.AsObject();
        Assert.False(zoom.ContainsKey("focusX"));
    }

    [Fact]
    public void Sanitizer_clamps_every_field()
    {
        var doc = new EditDocument
        {
            TrimStart = -5,
            TrimEnd = 99,
            ExportSpeed = 9,
            GifFrameRate = 30,
            PointerSize = double.NaN,
            ZoomAmount = 7,
            SystemAudioGain = 1.7,
            MicrophoneGain = double.PositiveInfinity,
            Backdrop = "{broken",
            Cuts = [new CutRange(2, 6), new CutRange(5, 9), new CutRange(3, 3.02)],
        }.Sanitized(20);
        Assert.Equal((0.0, 20.0), (doc.TrimStart, doc.TrimEnd));
        Assert.Equal(4, doc.ExportSpeed);
        Assert.Equal(12, doc.GifFrameRate);
        Assert.Equal(1, doc.PointerSize);
        Assert.Equal(3, doc.ZoomAmount);
        Assert.Equal(1, doc.SystemAudioGain);
        Assert.Equal(1, doc.MicrophoneGain);
        Assert.Equal(string.Empty, doc.Backdrop);
        Assert.Equal([new CutRange(2, 9)], doc.Cuts);

        Assert.Equal(1, new EditDocument { ExportSpeed = 0 }.Sanitized(10).ExportSpeed);
        Assert.Equal(1, new EditDocument { ExportSpeed = -2 }.Sanitized(10).ExportSpeed);
        Assert.Equal(0.25, new EditDocument { ExportSpeed = 0.1 }.Sanitized(10).ExportSpeed);
        Assert.Equal(1.8, new EditDocument { ZoomAmount = double.NaN }.Sanitized(10).ZoomAmount);
    }

    [Fact]
    public void Zoom_sanitizer_resolves_overlaps_by_moving_the_later_start()
    {
        var zooms = EditDocument.SanitizeZooms(
        [
            new ZoomSegment { Id = "b", Start = 3, End = 6, Amount = 9 },
            new ZoomSegment { Id = "a", Start = 1, End = 4 },
            new ZoomSegment { Id = "c", Start = 5.8, End = 6.1 },   // < 0.4 s after moving → dropped
            new ZoomSegment { Id = "d", Start = 8, End = 30, FocusX = 1.4, FocusY = -1 },
            new ZoomSegment { Id = "e", Start = 9, End = 9.2 },
        ], 10);
        Assert.Equal(["a", "b", "d"], zooms.Select(z => z.Id));
        Assert.Equal(4, zooms[1].Start);
        Assert.Equal(3, zooms[1].Amount);
        Assert.Equal(10, zooms[2].End);
        Assert.Equal((1.0, 0.0), (zooms[2].FocusX!.Value, zooms[2].FocusY!.Value));
    }

    [Fact]
    public void Overlay_sanitizers_follow_the_spec()
    {
        var longText = new string('x', 250);
        var texts = EditDocument.SanitizeTexts(
        [
            new TextOverlay { Id = "late", Text = "b", Start = 5, End = 7, Size = 0.5 },
            new TextOverlay { Id = "early", Text = longText, Start = -1, End = 2, Size = double.NaN },
            new TextOverlay { Id = "tiny", Text = "c", Start = 9.95, End = 12 },
        ], 10);
        Assert.Equal(["early", "late"], texts.Select(t => t.Id));
        Assert.Equal(200, texts[0].Text.Length);
        Assert.Equal(0, texts[0].Start);
        Assert.Equal(0.06, texts[0].Size);
        Assert.Equal(0.16, texts[1].Size);

        var absolute = OperatingSystem.IsWindows() ? @"C:\x\a.png" : "/x/a.png";
        var images = EditDocument.SanitizeImages(
        [
            new ImageOverlay { Id = "rel", Path = "a.png", Start = 0, End = 5 },
            new ImageOverlay { Id = "ok", Path = absolute, Start = 0, End = 5, Size = 0.01, Opacity = 3 },
        ], 10);
        Assert.Equal("ok", Assert.Single(images).Id);
        Assert.Equal(0.04, images[0].Size);
        Assert.Equal(1, images[0].Opacity);

        var blurs = EditDocument.SanitizeBlurs([new BlurRegion { Id = "b", Start = 0, End = 4, X = 0.995, Y = -0.5, Width = 0.5, Height = 0.2, Strength = 9 }], 10);
        var blur = Assert.Single(blurs);
        Assert.Equal(5, blur.Strength);
        Assert.True(blur.X + blur.Width <= 1.0000001 && blur.Width >= 0.01);
        Assert.Equal(0, blur.Y);
    }

    [Fact]
    public void Change_predicates_drive_rebuilds()
    {
        var a = new EditDocument();
        Assert.True(EditDocument.AffectsTiming(a, a with { TrimStart = 1 }));
        Assert.True(EditDocument.AffectsTiming(a, a with { Cuts = [new CutRange(1, 2)] }));
        Assert.True(EditDocument.AffectsPicture(a, a with { Cuts = [new CutRange(1, 2)] }));
        Assert.False(EditDocument.AffectsPicture(a, a with { TrimStart = 1 }));
        Assert.True(EditDocument.AffectsAudio(a, a with { MicrophoneGain = 0.5 }));
        Assert.False(EditDocument.AffectsAudio(a, a with { Quality = ExportQuality.High }));
        Assert.Equal(EditChange.ExportOnly, EditHistory.Classify(a, a with { ExportSpeed = 2 }));
        Assert.Equal(EditChange.Picture, EditHistory.Classify(a, a with { PointerSize = 1.5 }));
        Assert.Equal(EditChange.Timing | EditChange.Picture, EditHistory.Classify(a, a with { Cuts = [new CutRange(1, 2)] }));
    }

    [Fact]
    public void New_takes_are_seeded_from_settings()
    {
        var settings = Core.Settings.SettingsStore.InMemory();
        settings.Set(RecordingEditorSettings.Quality, "high");
        settings.Set(RecordingEditorSettings.SystemAudio, false);
        settings.Set(RecordingEditorSettings.GifSize, "large");
        settings.Set(RecordingEditorSettings.GifFrameRate, 15);
        settings.Set(RecordingEditorSettings.AutomaticZoom, false);
        var doc = RecordingEditorSettings.NewTakeDocument(settings);
        Assert.Equal(ExportQuality.High, doc.Quality);
        Assert.False(doc.KeepsSystemAudio);
        Assert.Equal(GifSize.Large, doc.GifSize);
        Assert.Equal(15, doc.GifFrameRate);
        Assert.False(doc.ZoomEnabled);
    }

    [Fact]
    public void Persisting_is_atomic_and_readable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rivet-edit-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var doc = new EditDocument { TrimStart = 1, Texts = [new TextOverlay { Id = "x", Text = "Hi", Start = 1, End = 3 }] };
            doc.Write(folder);
            Assert.Equal(doc, EditDocument.Read(folder));
            Assert.Single(Directory.GetFiles(folder));
            File.WriteAllText(Path.Combine(folder, EditDocument.FileName), "garbage");
            Assert.Null(EditDocument.Read(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class BackdropSchemaTests
{
    [Fact]
    public void Parses_macos_and_tolerant_spellings()
    {
        var mac = RecorderBackdrop.Parse("""{"kind":"preset","presetID":"graphite","padding":0.45,"cornerRadius":0.35,"blur":0}""");
        Assert.Equal(RecorderBackdropKind.Preset, mac.Kind);
        Assert.Equal("graphite", mac.PresetId);
        var pascal = RecorderBackdrop.Parse("""{"Kind":"Gradient","Colors":[[1,0,0],[0,0,1]],"Padding":0.2}""");
        Assert.Equal(RecorderBackdropKind.Gradient, pascal.Kind);
        Assert.Equal(2, pascal.Colors!.Count);
        Assert.Equal(0.2, pascal.Padding);
    }

    [Fact]
    public void Malformed_styles_demote_to_none_and_keep_sliders()
    {
        Assert.False(RecorderBackdrop.Parse("""{"kind":"preset","presetID":"nope"}""").Sanitized().HasBackdrop);
        Assert.False(RecorderBackdrop.Parse("""{"kind":"solid","colors":[[1,0,0],[0,1,0]]}""").Sanitized().HasBackdrop);
        Assert.False(RecorderBackdrop.Parse("""{"kind":"image","imagePath":""}""").Sanitized().HasBackdrop);
        Assert.False(RecorderBackdrop.Parse("""{"kind":"weird"}""").Sanitized().HasBackdrop);
        Assert.Equal(0.1, RecorderBackdrop.Parse("""{"kind":"none","cornerRadius":"NaN"}""").Sanitized().CornerRadius, 3);
        Assert.Equal(1, new RecorderBackdrop { Padding = 7 }.Sanitized().Padding);
    }

    [Fact]
    public void Writes_the_macos_form()
    {
        var json = Looks.StudioBackdrop.ToJson();
        Assert.Equal("""{"kind":"preset","presetID":"graphite","padding":0.45,"cornerRadius":0.35,"blur":0}""", json);
        Assert.Equal(string.Empty, RecorderBackdrop.None.ToDocumentString());
    }

    [Fact]
    public void Shared_custom_backgrounds_are_normalized_deduplicated_and_capped()
    {
        var settings = Core.Settings.SettingsStore.InMemory();
        IReadOnlyList<RecorderBackdrop> list = [];
        for (var i = 0; i < 14; i++)
        {
            list = RecordingEditorSettings.AddBackdropPreset(list, new RecorderBackdrop { Kind = RecorderBackdropKind.Solid, Colors = [new RgbValue(i / 20.0, 0, 0)], Padding = 0.9 });
        }

        list = RecordingEditorSettings.AddBackdropPreset(list, new RecorderBackdrop { Kind = RecorderBackdropKind.Solid, Colors = [new RgbValue(13 / 20.0, 0, 0)] });
        Assert.Equal(12, list.Count);
        Assert.All(list, p => Assert.Equal((0.5, 0.1, 0.0), (p.Padding, p.CornerRadius, p.Blur)));
        RecordingEditorSettings.WriteBackdropPresets(settings, list);
        Assert.IsType<string>(settings.GetRaw(RecordingEditorSettings.BackdropPresetsKey)!.GetValue<string>());
        Assert.Equal(12, RecordingEditorSettings.ReadBackdropPresets(settings).Count);

        // An array stored by another module stays an array.
        settings.SetRaw(RecordingEditorSettings.BackdropPresetsKey, new JsonArray(new RecorderBackdrop { Kind = RecorderBackdropKind.Preset, PresetId = "ocean" }.ToNode()));
        Assert.Single(RecordingEditorSettings.ReadBackdropPresets(settings));
        RecordingEditorSettings.WriteBackdropPresets(settings, list);
        Assert.IsType<JsonArray>(settings.GetRaw(RecordingEditorSettings.BackdropPresetsKey));
    }
}
