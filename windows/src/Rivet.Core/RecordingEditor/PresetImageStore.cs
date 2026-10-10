// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor;

/// <summary>
/// Private copies of edit-preset pictures (spec 02 §5.7):
/// <c>RecorderPresetImages/&lt;UUID&gt;/&lt;name&gt;</c>, so a preset outlives the
/// take it was made in. Copies are all-or-nothing and rolled back on failure.
/// </summary>
public sealed class PresetImageStore(string root, Func<string, bool> isDecodableImage)
{
    public string Root { get; } = root;

    /// <summary>Copies a document's overlays into the store; the result points at the copies.</summary>
    public IReadOnlyList<ImageOverlay> Capture(IReadOnlyList<ImageOverlay> images)
    {
        var copied = new List<string>();
        var result = new List<ImageOverlay>();
        try
        {
            foreach (var image in images)
            {
                var copy = RecordingFolders.CopyIntoPrivateFolder(image.Path, Root);
                copied.Add(Path.GetDirectoryName(copy)!);
                if (!isDecodableImage(copy))
                {
                    throw new IOException("The picture could not be read.");
                }

                result.Add(image with { Path = copy });
            }

            return result;
        }
        catch
        {
            foreach (var folder in copied)
            {
                RecordingFolders.TryDeleteFolder(folder);
            }

            throw;
        }
    }

    /// <summary>
    /// Copies a preset's pictures into a take, each with a fresh id spanning the
    /// whole recording. Every path must be inside the store; a closed take (its
    /// folder gone) is never recreated.
    /// </summary>
    public IReadOnlyList<ImageOverlay> Restore(IReadOnlyList<ImageOverlay> images, string takeFolder, double duration)
    {
        if (!Directory.Exists(takeFolder))
        {
            throw new IOException("The recording was closed.");
        }

        if (images.Any(i => !RecordingFolders.IsInsideStore(i.Path, Root)))
        {
            throw new IOException("A preset picture is outside the preset store.");
        }

        var copied = new List<string>();
        var result = new List<ImageOverlay>();
        try
        {
            foreach (var image in images)
            {
                var copy = RecordingFolders.CopyIntoPrivateFolder(image.Path, takeFolder);
                copied.Add(Path.GetDirectoryName(copy)!);
                if (!isDecodableImage(copy))
                {
                    throw new IOException("The picture could not be read.");
                }

                result.Add(image with { Id = EditDocument.NewId(), Path = copy, Start = 0, End = duration });
            }

            return result;
        }
        catch
        {
            foreach (var folder in copied)
            {
                RecordingFolders.TryDeleteFolder(folder);
            }

            throw;
        }
    }

    /// <summary>Deletes picture folders no remaining preset references.</summary>
    public void RemoveUnreferenced(IEnumerable<EditPreset> remaining)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var referenced = remaining.SelectMany(p => p.Images ?? [])
            .Select(i => Path.GetDirectoryName(Path.GetFullPath(i.Path)))
            .OfType<string>()
            .ToHashSet(comparer);
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (Guid.TryParse(Path.GetFileName(folder), out _) && !referenced.Contains(Path.GetFullPath(folder)))
            {
                RecordingFolders.TryDeleteFolder(folder);
            }
        }
    }
}
