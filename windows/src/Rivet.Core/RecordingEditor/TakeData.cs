// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Rivet.Core.Recording;

namespace Rivet.Core.RecordingEditor;

/// <summary>
/// Everything the editor reads from a take folder (spec 02 §3.20.2, §5.2):
/// the manifest, the master, the audio tracks, the pointer and typing tracks
/// and the edit document. Missing or corrupt side tracks read as empty.
/// </summary>
public sealed class TakeData
{
    private TakeData(string folder, TakeManifest? manifest, PointerTrack pointer, int pointerVersion, TypingTrack typing, EditDocument? document)
    {
        Folder = folder;
        Manifest = manifest;
        Pointer = pointer;
        PointerVersion = pointerVersion;
        Typing = typing;
        SavedDocument = document;
    }

    public string Folder { get; }

    public TakeManifest? Manifest { get; }

    public PointerTrack Pointer { get; }

    /// <summary>3 (macOS layout, points) or 4 (Windows, pixels).</summary>
    public int PointerVersion { get; }

    public TypingTrack Typing { get; }

    /// <summary>The stored edit document, or null for a first open.</summary>
    public EditDocument? SavedDocument { get; }

    public bool HasPointerTrack => !Pointer.IsEmpty;

    public string VideoPath => Path.Combine(Folder, Manifest?.Video.File is { Length: > 0 } file && IsPlainName(file) ? file : "take.mp4");

    public string? SystemAudioPath => AudioPath(TakeAudioSource.System);

    public string? MicrophoneAudioPath => AudioPath(TakeAudioSource.Microphone);

    /// <summary>The capture frame rate from the manifest, snapped to 30/60; 0 when unknown.</summary>
    public int ManifestFrameRate => Manifest is null ? 0 : ExportMath.SnapFrameRate(Manifest.Capture.Fps);

    public static TakeData Load(string folder)
    {
        var manifest = TakeManifest.Read(folder);
        var pointerFile = manifest?.PointerTrack?.File is { Length: > 0 } pf && IsPlainName(pf) ? pf : PointerTrack.FileName;
        var (pointer, version) = ReadPointer(Path.Combine(folder, pointerFile));
        var typingFile = manifest?.TypingTrack is { Length: > 0 } tf && IsPlainName(tf) ? tf : TypingTrack.FileName;
        var typing = TypingTrack.Read(Path.Combine(folder, typingFile));
        return new TakeData(folder, manifest, pointer, version, typing, EditDocument.Read(folder));
    }

    /// <summary>Decodes a pointer track and reports its layout version (4 when unreadable).</summary>
    public static (PointerTrack Track, int Version) ReadPointer(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (PointerTrack.Empty, PointerTrack.CurrentVersion);
            }

            var bytes = File.ReadAllBytes(path);
            var version = bytes.Length >= 6 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) : PointerTrack.CurrentVersion;
            return (PointerTrack.Decode(bytes), version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (PointerTrack.Empty, PointerTrack.CurrentVersion);
        }
    }

    private string? AudioPath(TakeAudioSource source)
    {
        if (Manifest is null)
        {
            var fallback = Path.Combine(Folder, source == TakeAudioSource.System ? "system.wav" : "mic.wav");
            return File.Exists(fallback) ? fallback : null;
        }

        var track = Manifest.Audio.FirstOrDefault(a => a.Source == source);
        if (track is null || !IsPlainName(track.File))
        {
            return null;
        }

        var path = Path.Combine(Folder, track.File);
        return File.Exists(path) ? path : null;
    }

    /// <summary>A file name inside the take folder (no separators, no parent references).</summary>
    private static bool IsPlainName(string name) =>
        name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && name != "." && name != "..";
}
