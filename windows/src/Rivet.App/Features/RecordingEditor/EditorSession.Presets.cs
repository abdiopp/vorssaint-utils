// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>Edit presets and shared custom backgrounds (spec 02 §3.28, §3.29, §5.7).</summary>
public sealed partial class EditorSession
{
    /// <summary>Copying preset pictures: the Presets menu is disabled meanwhile.</summary>
    public bool IsPresetBusy { get; private set; }

    private PresetImageStore PresetStore => new(RecordingFolders.PresetImages(Paths), path =>
    {
        using var image = OrientedImage.Load(path, 64);
        return image is not null;
    });

    /// <summary>Saves the current look (with copies of its pictures) under a name; same name replaces in place.</summary>
    public async Task SavePresetAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || IsPresetBusy)
        {
            return;
        }

        IsPresetBusy = true;
        Raise(SessionChange.Presets);
        var doc = Document;
        var store = PresetStore;
        try
        {
            var images = await Task.Run(() => store.Capture(doc.Images)).ConfigureAwait(true);
            var current = RecordingEditorSettings.ReadPresets(Settings);
            var existing = current.FirstOrDefault(p => EditPreset.SameName(p.Name, name));
            var preset = EditPreset.Capture(name, doc, existing?.Id) with { Images = images };
            var updated = EditPreset.Upsert(current, preset);
            RecordingEditorSettings.WritePresets(Settings, updated);
            await Task.Run(() => store.RemoveUnreferenced(updated)).ConfigureAwait(true);
            Presets = updated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("recording-editor", "Could not save the preset's pictures.", ex);
            Hud?.Show(L.Get("recorder.imageImportFailed"), HudStyle.Error, duration: HudDuration);
        }
        finally
        {
            IsPresetBusy = false;
            Raise(SessionChange.Presets);
        }
    }

    /// <summary>
    /// Applies a preset's look; its pictures are copied into this take, each
    /// spanning the whole video, replacing the image list (legacy presets
    /// without a list leave images alone). Cuts, captions, blurs, audio, trim,
    /// quality and speed are untouched.
    /// </summary>
    public async Task ApplyPresetAsync(EditPreset preset)
    {
        if (!IsReady || IsPresetBusy)
        {
            return;
        }

        IsPresetBusy = true;
        Raise(SessionChange.Presets);
        try
        {
            IReadOnlyList<ImageOverlay>? images = null;
            if (preset.Images is { Count: > 0 } sources)
            {
                var store = PresetStore;
                var folder = Take.Folder;
                var duration = Duration;
                images = await Task.Run(() => store.Restore(sources, folder, duration)).ConfigureAwait(true);
                if (_disposed)
                {
                    foreach (var image in images)
                    {
                        RecordingFolders.TryDeleteFolder(Path.GetDirectoryName(image.Path)!);
                    }

                    return;
                }
            }
            else if (preset.Images is not null)
            {
                images = [];
            }

            var doc = preset.ApplyLook(Document);
            if (images is not null)
            {
                doc = doc with { Images = images };
            }

            Apply(Looks.RestoreAutomaticZooms(doc, Take.Pointer, Take.Typing, Duration));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("recording-editor", "Could not copy the preset's pictures.", ex);
            Hud?.Show(L.Get("recorder.imageImportFailed"), HudStyle.Error, duration: HudDuration);
        }
        finally
        {
            IsPresetBusy = false;
            Raise(SessionChange.Presets);
        }
    }

    /// <summary>Removes a preset and the pictures no remaining preset references.</summary>
    public void RemovePreset(EditPreset preset)
    {
        var updated = RecordingEditorSettings.ReadPresets(Settings).Where(p => p.Id != preset.Id).ToList();
        RecordingEditorSettings.WritePresets(Settings, updated);
        Presets = updated;
        var store = PresetStore;
        _ = Task.Run(() => store.RemoveUnreferenced(updated));
        Raise(SessionChange.Presets);
    }

    /// <summary>Re-reads presets and customs another editor may have changed.</summary>
    public void ReloadSharedLists()
    {
        Presets = RecordingEditorSettings.ReadPresets(Settings);
        BackdropCustoms = RecordingEditorSettings.ReadBackdropPresets(Settings);
        Raise(SessionChange.Presets);
    }

    /// <summary>Saves the current solid, gradient or image look to the list shared with screenshots.</summary>
    public void SaveBackdropCustom(RecorderBackdrop style)
    {
        var updated = RecordingEditorSettings.AddBackdropPreset(RecordingEditorSettings.ReadBackdropPresets(Settings), style);
        RecordingEditorSettings.WriteBackdropPresets(Settings, updated);
        BackdropCustoms = updated;
        Raise(SessionChange.Presets);
    }

    public void RemoveBackdropCustom(RecorderBackdrop style)
    {
        var updated = RecordingEditorSettings.ReadBackdropPresets(Settings).Where(p => !p.SameLook(style)).ToList();
        RecordingEditorSettings.WriteBackdropPresets(Settings, updated);
        BackdropCustoms = updated;
        Raise(SessionChange.Presets);
    }
}
