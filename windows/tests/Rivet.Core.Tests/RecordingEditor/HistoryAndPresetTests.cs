// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests.RecordingEditor;

public class HistoryTests
{
    [Fact]
    public void Plain_changes_are_one_step_each_and_persist()
    {
        var history = new EditHistory(new EditDocument());
        var persisted = 0;
        history.Committed += _ => persisted++;
        Assert.True(history.Apply(history.Current with { PointerSize = 1.5 }));
        Assert.False(history.Apply(history.Current with { PointerSize = 1.5 }));
        Assert.True(history.Apply(history.Current with { ExportSpeed = 2 }));
        Assert.Equal(2, history.UndoCount);
        Assert.Equal(2, persisted);
        Assert.True(history.Undo());
        Assert.Equal(1, history.Current.ExportSpeed);
        Assert.True(history.Redo());
        Assert.Equal(2, history.Current.ExportSpeed);
        Assert.Equal(4, persisted);
    }

    [Fact]
    public void Interactions_coalesce_into_one_step_and_never_persist_midway()
    {
        var history = new EditHistory(new EditDocument());
        var persisted = 0;
        var live = 0;
        history.Committed += _ => persisted++;
        history.Changed += (_, _, _) => live++;
        history.BeginInteraction();
        for (var i = 1; i <= 10; i++)
        {
            history.SetLive(history.Current with { PointerSize = 1 + (i * 0.05) });
        }

        Assert.Equal(0, persisted);
        Assert.False(history.CanUndo);
        Assert.True(history.CommitInteraction());
        Assert.Equal(1, persisted);
        Assert.Equal(10, live);
        Assert.Equal(1, history.UndoCount);
        history.Undo();
        Assert.Equal(1, history.Current.PointerSize);
    }

    [Fact]
    public void An_unchanged_interaction_adds_nothing_and_new_changes_clear_redo()
    {
        var history = new EditHistory(new EditDocument());
        history.BeginInteraction();
        Assert.False(history.CommitInteraction());
        history.Apply(history.Current with { ZoomAmount = 2 });
        history.Undo();
        Assert.True(history.CanRedo);
        history.Apply(history.Current with { ZoomAmount = 2.5 });
        Assert.False(history.CanRedo);
    }
}

public class LookAndPresetTests
{
    [Fact]
    public void Looks_set_only_their_fields_and_match()
    {
        var doc = new EditDocument
        {
            TrimStart = 2,
            ExportSpeed = 1.5,
            Quality = ExportQuality.High,
            Aspect = CanvasAspect.Square,
            Texts = [new TextOverlay { Id = "t", Text = "x", Start = 0, End = 1 }],
        };
        var studio = Looks.Apply(RecorderLook.Studio, doc);
        Assert.True(Looks.Matches(RecorderLook.Studio, studio));
        Assert.Equal(CanvasAspect.Square, studio.Aspect);   // Studio leaves the shape alone
        Assert.Equal((2.0, 1.5, ExportQuality.High), (studio.TrimStart, studio.ExportSpeed, studio.Quality));
        Assert.Single(studio.Texts);

        var original = Looks.Apply(RecorderLook.Original, studio);
        Assert.True(Looks.Matches(RecorderLook.Original, original));
        Assert.False(Looks.Matches(RecorderLook.Smooth, original));
        Assert.Equal(CanvasAspect.Original, original.Aspect);
        Assert.True(original.ShowsPointer);   // the capture never contains a pointer
        Assert.True(Looks.Matches(RecorderLook.Smooth, Looks.Apply(RecorderLook.Smooth, original)));
    }

    [Fact]
    public void Applying_a_look_restores_automatic_zooms_when_the_lane_is_empty()
    {
        var pointer = new PointerTrack
        {
            Samples = [new PointerSample(0, 0.5f, 0.5f, 0, true)],
            Clicks = [new PointerClick(2, true), new PointerClick(2.1f, false)],
        };
        var doc = Looks.Apply(RecorderLook.Smooth, new EditDocument { ZoomEnabled = false });
        var restored = Looks.RestoreAutomaticZooms(doc, pointer, new TypingTrack([]), 20);
        Assert.Single(restored.ZoomSegments);
        Assert.True(restored.ZoomsGenerated);
        Assert.Empty(Looks.RestoreAutomaticZooms(doc with { ZoomEnabled = false }, pointer, new TypingTrack([]), 20).ZoomSegments);
    }

    [Fact]
    public void Presets_replace_by_name_ignoring_case_and_accents_and_keep_twelve()
    {
        IReadOnlyList<EditPreset> list = [];
        list = EditPreset.Upsert(list, EditPreset.Capture("Café", new EditDocument()));
        var id = list[0].Id;
        list = EditPreset.Upsert(list, EditPreset.Capture("CAFE", new EditDocument { PointerSize = 2 }));
        var single = Assert.Single(list);
        Assert.Equal(id, single.Id);
        Assert.Equal(2, single.PointerSize);
        for (var i = 0; i < 14; i++)
        {
            list = EditPreset.Upsert(list, EditPreset.Capture($"P{i}", new EditDocument()));
        }

        Assert.Equal(12, list.Count);
        Assert.Equal("P13", list[^1].Name);
        Assert.DoesNotContain(list, p => p.Name == "CAFE");
    }

    [Fact]
    public void Backups_carry_presets_without_image_paths()
    {
        var settings = SettingsStore.InMemory();
        var image = new ImageOverlay { Id = "i", Path = Path.Combine(Path.GetTempPath(), "x", "logo.png"), Start = 0, End = 5 };
        var preset = EditPreset.Capture("Brand", new EditDocument { Images = [image] });
        RecordingEditorSettings.WritePresets(settings, [preset]);

        var portable = settings.Get(RecordingEditorSettings.EditorPresets);
        Assert.DoesNotContain("logo.png", portable, StringComparison.Ordinal);
        Assert.True(RecordingEditorSettings.EditorPresetImages.IsMachineState);
        Assert.False(SettingsBackup.Export(settings).Contains("logo.png", StringComparison.Ordinal));

        var read = Assert.Single(RecordingEditorSettings.ReadPresets(settings));
        Assert.Equal(image.Path, Assert.Single(read.Images!).Path);

        // Another PC (no machine-local images): the preset behaves like a legacy one and leaves images alone.
        var other = SettingsStore.InMemory();
        other.Set(RecordingEditorSettings.EditorPresets, portable);
        Assert.Null(Assert.Single(RecordingEditorSettings.ReadPresets(other)).Images);
    }

    [Fact]
    public void Macos_layout_with_inline_images_and_legacy_presets_read()
    {
        var json = new JsonArray(
            JsonNode.Parse("""{"id":"A","name":"Legacy","backdrop":"","aspect":"square","showsPointer":true,"pointerSmoothing":"light","pointerSize":1.5,"showsClickRing":false,"zoomEnabled":true,"zoomAmount":2.2}"""),
            JsonNode.Parse("""{"id":"B","name":"With images","images":[]}"""),
            JsonNode.Parse("""{"name":"no id"}"""));
        var presets = EditPreset.ParseList(json);
        Assert.Equal(2, presets.Count);
        Assert.Null(presets[0].Images);
        Assert.Equal(CanvasAspect.Square, presets[0].Aspect);
        Assert.Equal(PointerSmoothing.Light, presets[0].PointerSmoothing);
        Assert.NotNull(presets[1].Images);
        Assert.Empty(presets[1].Images!);
    }

    [Fact]
    public void Preset_images_are_copied_all_or_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "rivet-presets-" + Guid.NewGuid());
        var take = Path.Combine(root, "Take");
        var store = new PresetImageStore(Path.Combine(root, "Store"), path => !path.EndsWith("bad.png", StringComparison.Ordinal));
        Directory.CreateDirectory(take);
        try
        {
            var good = Path.Combine(root, "good.png");
            var bad = Path.Combine(root, "bad.png");
            File.WriteAllBytes(good, [1, 2, 3]);
            File.WriteAllBytes(bad, [4, 5, 6]);
            var captured = store.Capture([new ImageOverlay { Id = "g", Path = good, Start = 1, End = 2 }]);
            Assert.True(RecordingFolders.IsInsideStore(captured[0].Path, store.Root));
            Assert.Throws<IOException>(() => store.Capture([new ImageOverlay { Id = "g", Path = good }, new ImageOverlay { Id = "b", Path = bad }]));
            Assert.Single(Directory.GetDirectories(store.Root));   // the failed batch was rolled back

            var restored = store.Restore(captured, take, 30);
            Assert.Equal((0.0, 30.0), (restored[0].Start, restored[0].End));
            Assert.NotEqual(captured[0].Id, restored[0].Id);
            Assert.StartsWith(take, restored[0].Path, StringComparison.Ordinal);
            Assert.Throws<IOException>(() => store.Restore([new ImageOverlay { Id = "x", Path = good }], take, 30));
            Assert.Throws<IOException>(() => store.Restore(captured, Path.Combine(root, "Closed"), 30));

            store.RemoveUnreferenced([]);
            Assert.Empty(Directory.GetDirectories(store.Root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Staged_files_commit_atomically_and_clean_up()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rivet-stage-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var destination = Path.Combine(folder, "out.mp4");
            File.WriteAllText(destination, "old");
            using (var staged = new StagedFile(destination))
            {
                File.WriteAllText(staged.StagingPath, "partial");
                // Not committed (cancel/failure): the old file survives, nothing beside it.
            }

            Assert.Equal("old", File.ReadAllText(destination));
            Assert.Single(Directory.GetFiles(folder));

            using (var staged = new StagedFile(destination))
            {
                Assert.StartsWith(".", Path.GetFileName(staged.StagingPath), StringComparison.Ordinal);
                File.WriteAllText(staged.StagingPath, "new");
                staged.Commit(CancellationToken.None);
            }

            Assert.Equal("new", File.ReadAllText(destination));
            Assert.Single(Directory.GetFiles(folder));

            using var cancelled = new StagedFile(destination);
            File.WriteAllText(cancelled.StagingPath, "never");
            Assert.Throws<OperationCanceledException>(() => cancelled.Commit(new CancellationToken(true)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
