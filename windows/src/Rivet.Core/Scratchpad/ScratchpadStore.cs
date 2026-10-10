// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Modules.Scratchpad;

/// <summary>
/// The notes file with the macOS safety rules (spec 07 §3.3.9):
/// <list type="bullet">
/// <item>notes are read only when first needed;</item>
/// <item>an existing file that cannot be read or decoded is never overwritten: saving stays blocked until a later load succeeds;</item>
/// <item>every save is atomic (temp file, flush, replace) and then read back and byte-compared;</item>
/// <item>tab operations are write-first: the new document is adopted only if its write succeeded;</item>
/// <item>the legacy <c>Scratchpad.txt</c> is migrated and deleted only after a verified save.</item>
/// </list>
/// Call on the UI thread.
/// </summary>
public sealed class ScratchpadStore
{
    public const string FileName = "Scratchpad.json";
    public const string LegacyFileName = "Scratchpad.txt";

    private readonly string _folder;
    private readonly Func<string> _baseName;
    private readonly Func<ScratchpadRetention> _retention;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string, byte[], bool>? _writeOverride;

    public ScratchpadStore(string folder, Func<string> baseName, Func<ScratchpadRetention> retention, Func<DateTimeOffset>? clock = null, Func<string, byte[], bool>? writeOverride = null)
    {
        _folder = folder;
        _baseName = baseName;
        _retention = retention;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _writeOverride = writeOverride;
    }

    /// <summary>Raised after the document or the save state changed.</summary>
    public event EventHandler? Changed;

    public string FilePath => Path.Combine(_folder, FileName);

    public string LegacyPath => Path.Combine(_folder, LegacyFileName);

    /// <summary>The working document; null until the first successful load.</summary>
    public ScratchpadDocument? Document { get; private set; }

    /// <summary>What is known to be on disk.</summary>
    public ScratchpadDocument? LastSaved { get; private set; }

    /// <summary>The last write failed; edits are kept in memory and every change retries.</summary>
    public bool SaveFailed { get; private set; }

    /// <summary>The last load failed; saving is blocked so unreadable notes are never overwritten.</summary>
    public bool LoadFailed { get; private set; }

    public bool HasUnsavedChanges => Document is not null && !Document.ContentEquals(LastSaved);

    /// <summary>
    /// Loads (or reuses) the document. With unsaved in-memory edits (an earlier
    /// save failed) it does not reload; it retries the save instead.
    /// Returns false when the notes could not be opened.
    /// </summary>
    public bool Load()
    {
        if (Document is not null && HasUnsavedChanges && !LoadFailed)
        {
            TrySave(Document);
            return true;
        }

        ScratchpadDocument loaded;
        var migratedLegacy = false;
        try
        {
            if (File.Exists(FilePath))
            {
                var bytes = File.ReadAllBytes(FilePath);
                loaded = ScratchpadCodec.Decode(bytes);
            }
            else if (File.Exists(LegacyPath))
            {
                var text = File.ReadAllText(LegacyPath, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(LegacyPath), TimeSpan.Zero);
                var fresh = ScratchpadDocument.Fresh(_baseName());
                loaded = fresh.WithText(fresh.SelectedId, text, mtime);
                migratedLegacy = true;
            }
            else
            {
                loaded = ScratchpadDocument.Fresh(_baseName());
            }
        }
        catch (Exception ex) when (ex is ScratchpadDecodeException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Log.Error("scratchpad", "Notes could not be read; they are left untouched and saving is blocked.", ex);
            LoadFailed = true;
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        LoadFailed = false;
        var cleaned = ScratchpadCodec.Clean(loaded, _baseName()).ApplyRetention(_retention(), _clock());
        Document = cleaned;
        if (TrySave(cleaned) && migratedLegacy)
        {
            TryDelete(LegacyPath);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Write-first commit for tab operations, marks, clear and similar: the new
    /// document becomes current only when it was written and verified.
    /// </summary>
    public bool Commit(ScratchpadDocument next)
    {
        if (LoadFailed)
        {
            return false;
        }

        if (!TrySave(next))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        Document = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Typing: updates the in-memory text now; the caller schedules <see cref="Flush"/>.</summary>
    public void UpdateText(Guid padId, string text)
    {
        if (Document is null || LoadFailed)
        {
            return;
        }

        var pad = Document.Find(padId);
        if (pad is null || pad.Text == text)
        {
            return;
        }

        Document = Document.WithText(padId, text, _clock());
    }

    /// <summary>Saves pending edits. Returns false when the write failed.</summary>
    public bool Flush()
    {
        if (Document is null || LoadFailed || !HasUnsavedChanges)
        {
            return !SaveFailed;
        }

        var ok = TrySave(Document);
        Changed?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    /// <summary>Forgets the in-memory document (settings import before a relaunch).</summary>
    public void Discard()
    {
        Document = null;
        LastSaved = null;
        SaveFailed = false;
    }

    private bool TrySave(ScratchpadDocument document)
    {
        if (LoadFailed)
        {
            return false;
        }

        if (document.ContentEquals(LastSaved))
        {
            SaveFailed = false;
            return true;
        }

        var bytes = ScratchpadCodec.Encode(document);
        var ok = _writeOverride?.Invoke(FilePath, bytes) ?? WriteVerified(FilePath, bytes);
        SaveFailed = !ok;
        if (ok)
        {
            LastSaved = document;
        }

        return ok;
    }

    /// <summary>Temp file in the same folder, flushed to disk, renamed over the target, then read back and compared.</summary>
    public static bool WriteVerified(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            var readBack = File.ReadAllBytes(path);
            if (!readBack.AsSpan().SequenceEqual(bytes))
            {
                Log.Error("scratchpad", "Read-back verification of the notes file failed.");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("scratchpad", "Notes could not be saved.", ex);
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("scratchpad", $"Could not delete {path}.", ex);
        }
    }
}
