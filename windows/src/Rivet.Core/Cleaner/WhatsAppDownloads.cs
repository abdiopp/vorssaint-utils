// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.Cleaner;

public enum WhatsAppCategory
{
    Image,
    Video,
    Audio,
    Document,
    Archive,
    Other,
}

/// <summary>Why a file is believed to come from WhatsApp.</summary>
public enum WhatsAppEvidence
{
    /// <summary>Its Mark of the Web names a WhatsApp host (written by Windows when the file arrived).</summary>
    WebMark,

    /// <summary>Only its name looks like WhatsApp's: forgeable and lost on rename, so never pre-selected.</summary>
    NameOnly,
}

public sealed record WhatsAppCandidate(
    string Path,
    string Name,
    long Size,
    DateTime DownloadedUtc,
    DateTime ModifiedUtc,
    WhatsAppCategory Category,
    WhatsAppEvidence Evidence,
    FileIdentity Identity)
{
    /// <summary>"volume:id" fingerprint: the exclusion key ("Keep").</summary>
    public string Fingerprint => $"{Identity.Volume:x}:{Identity.IdHigh:x}{Identity.IdLow:x16}";
}

/// <summary>Reads a file's Mark of the Web (the Zone.Identifier stream).</summary>
public interface IWebMarkReader
{
    string? ReadZoneIdentifier(string path);
}

/// <summary>
/// WhatsApp downloads on Windows (spec §3.7.7). macOS trusts only the
/// quarantine agent name; Windows has no per-app equivalent. Files whose
/// Mark of the Web points at a WhatsApp host are "confirmed"; files that
/// merely carry WhatsApp's default names are listed for review but never
/// pre-selected. There is no automatic cleanup and no organizer: every
/// removal is the person's own tick.
/// </summary>
public static partial class WhatsAppRules
{
    public static readonly IReadOnlyList<int> RetentionOptions = [1, 2, 7, 14, 30];

    private static readonly HashSet<string> Partial = new(StringComparer.OrdinalIgnoreCase) { "download", "part", "partial", "crdownload", "tmp" };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { "7z", "bz", "bz2", "cab", "dmg", "gz", "iso", "rar", "tar", "tgz", "xz", "zip" };

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        "csv", "doc", "docm", "docx", "epub", "key", "md", "numbers", "odp", "ods", "odt", "pages", "pdf", "ppt", "pptm",
        "pptx", "rtf", "tex", "txt", "xls", "xlsm", "xlsx",
    };

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "tif", "tiff", "avif" };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { "mp4", "mov", "m4v", "avi", "mkv", "webm", "3gp", "wmv" };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { "opus", "ogg", "mp3", "m4a", "aac", "wav", "amr", "flac", "wma" };

    public static WhatsAppCategory CategoryOf(string name)
    {
        var extension = System.IO.Path.GetExtension(name).TrimStart('.');
        if (Archives.Contains(extension))
        {
            return WhatsAppCategory.Archive;
        }

        if (Documents.Contains(extension))
        {
            return WhatsAppCategory.Document;
        }

        if (Images.Contains(extension))
        {
            return WhatsAppCategory.Image;
        }

        if (Videos.Contains(extension))
        {
            return WhatsAppCategory.Video;
        }

        return Audio.Contains(extension) ? WhatsAppCategory.Audio : WhatsAppCategory.Other;
    }

    public static string CategoryId(WhatsAppCategory category) => category.ToString().ToLowerInvariant();

    /// <summary>Comma list of category ids; absent → images, videos, audio; empty → none.</summary>
    public static IReadOnlySet<WhatsAppCategory> ParseCategories(string? value)
    {
        var set = new HashSet<WhatsAppCategory>();
        foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<WhatsAppCategory>(part, ignoreCase: true, out var category) && part.All(char.IsAsciiLetter))
            {
                set.Add(category);
            }
        }

        return set;
    }

    public static string FormatCategories(IEnumerable<WhatsAppCategory> categories) =>
        string.Join(',', categories.Distinct().OrderBy(c => c).Select(CategoryId));

    public static bool IsPartialDownload(string name) =>
        Partial.Contains(System.IO.Path.GetExtension(name).TrimStart('.'));

    /// <summary>WhatsApp Desktop's default names: "WhatsApp Image 2024-03-04 at 12.34.56_ab12cd34.jpg".</summary>
    public static bool HasDefaultName(string name) => DefaultNamePattern().IsMatch(name);

    /// <summary>The host of HostUrl (else ReferrerUrl) in a Zone.Identifier stream.</summary>
    public static string? WebMarkHost(string? zoneIdentifier)
    {
        if (string.IsNullOrWhiteSpace(zoneIdentifier))
        {
            return null;
        }

        string? host = null;
        foreach (var line in zoneIdentifier.Split('\n'))
        {
            var text = line.Trim();
            foreach (var key in new[] { "HostUrl=", "ReferrerUrl=" })
            {
                if (text.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(text[key.Length..].Trim(), UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                {
                    if (key == "HostUrl=" || host is null)
                    {
                        host = uri.Host;
                    }
                }
            }
        }

        return host;
    }

    public static bool IsWhatsAppHost(string? host) =>
        host is not null
        && (host.Equals("whatsapp.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".whatsapp.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("whatsapp.net", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".whatsapp.net", StringComparison.OrdinalIgnoreCase));

    /// <summary>Old enough: downloaded and modified at least <paramref name="days"/> calendar days ago.</summary>
    public static bool IsOldEnough(DateTime downloadedUtc, DateTime modifiedUtc, int days, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-days);
        return downloadedUtc <= cutoff && modifiedUtc <= cutoff;
    }

    /// <summary>The initial selection: confirmed by the Mark of the Web, an enabled type, old enough.</summary>
    public static bool IsPreselected(WhatsAppCandidate candidate, IReadOnlySet<WhatsAppCategory> categories, int days, DateTime nowUtc) =>
        candidate.Evidence == WhatsAppEvidence.WebMark
        && categories.Contains(candidate.Category)
        && IsOldEnough(candidate.DownloadedUtc, candidate.ModifiedUtc, days, nowUtc);

    public static int SanitizeRetention(int days) => RetentionOptions.Contains(days) ? days : 7;

    [GeneratedRegex(@"^WhatsApp (?:Image|Video|Audio|Ptt|Document|Sticker|GIF|Unknown) \d{4}-\d{2}-\d{2} at \d{1,2}\.\d{2}\.\d{2}(?:\s?[AaPp]\.?[Mm]\.?)?(?:[ _(].*)?\.[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultNamePattern();
}

/// <summary>WhatsApp downloads settings (keys match macOS; the automation keys have no Windows meaning).</summary>
public static class WhatsAppSettings
{
    public static readonly Setting<bool> Enabled = new("whatsAppDownloadsEnabled", false);

    public static readonly Setting<string> Categories = new("whatsAppDownloadsCategories", "image,video,audio");

    public static readonly Setting<int> RetentionDays = new("whatsAppDownloadsRetentionDays", 7, WhatsAppRules.SanitizeRetention);

    public static readonly Setting<List<string>> Exclusions = new("whatsAppDownloadsExclusions", [], machineState: true);
}

/// <summary>Scans the top level of Downloads and moves the files the person ticked to the Recycle Bin.</summary>
public sealed class WhatsAppDownloadsService
{
    private readonly ICleanerFileSystem _fs;
    private readonly IWebMarkReader _marks;
    private readonly IRecycler _recycler;
    private readonly IKnownFolders _folders;
    private readonly ISettingsStore _settings;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    public WhatsAppDownloadsService(ICleanerFileSystem fs, IWebMarkReader marks, IRecycler recycler, IKnownFolders folders, ISettingsStore settings)
    {
        _fs = fs;
        _marks = marks;
        _recycler = recycler;
        _folders = folders;
        _settings = settings;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<WhatsAppCandidate> Candidates { get; private set; } = [];

    public bool IsScanning { get; private set; }

    public bool IsCleaning { get; private set; }

    public bool HasScanned { get; private set; }

    public bool ScanFailed { get; private set; }

    public string? LastResultText { get; private set; }

    public (int Count, long Bytes, int Failed)? LastResult { get; private set; }

    public string DownloadsFolder => _folders.Folders.Downloads;

    public bool IsSelected(WhatsAppCandidate candidate) => _selected.Contains(candidate.Path);

    public bool IsExcluded(WhatsAppCandidate candidate) =>
        _settings.Get(WhatsAppSettings.Exclusions).Contains(candidate.Fingerprint, StringComparer.Ordinal);

    public int SelectedCount => Candidates.Count(c => IsSelected(c) && !IsExcluded(c));

    public long SelectedBytes => Candidates.Where(c => IsSelected(c) && !IsExcluded(c)).Sum(c => c.Size);

    public void SetSelected(WhatsAppCandidate candidate, bool selected)
    {
        if (IsExcluded(candidate))
        {
            return;
        }

        if (selected)
        {
            _selected.Add(candidate.Path);
        }
        else
        {
            _selected.Remove(candidate.Path);
        }

        Raise();
    }

    /// <summary>"Select by my rules": confirmed, enabled types, old enough, not kept.</summary>
    public void SelectByRules()
    {
        var categories = WhatsAppRules.ParseCategories(_settings.Get(WhatsAppSettings.Categories));
        var days = _settings.Get(WhatsAppSettings.RetentionDays);
        _selected.Clear();
        foreach (var candidate in Candidates.Where(c => !IsExcluded(c) && WhatsAppRules.IsPreselected(c, categories, days, DateTime.UtcNow)))
        {
            _selected.Add(candidate.Path);
        }

        Raise();
    }

    /// <summary>"Keep": never selected again; "Manage again" undoes it.</summary>
    public void SetKept(WhatsAppCandidate candidate, bool keep)
    {
        var exclusions = _settings.Get(WhatsAppSettings.Exclusions).ToList();
        exclusions.Remove(candidate.Fingerprint);
        if (keep)
        {
            exclusions.Add(candidate.Fingerprint);
            _selected.Remove(candidate.Path);
        }

        _settings.Set(WhatsAppSettings.Exclusions, exclusions);
        Raise();
    }

    public async Task ScanAsync()
    {
        if (IsScanning || IsCleaning)
        {
            return;
        }

        IsScanning = true;
        Raise();
        try
        {
            var folder = DownloadsFolder;
            var found = await Task.Run(() => Find(folder)).ConfigureAwait(false);
            Candidates = found
                .OrderByDescending(c => c.DownloadedUtc)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            ScanFailed = found.Count == 0 && _fs.Stat(folder) is null;
            HasScanned = true;
            SelectByRules();
        }
        catch (Exception ex)
        {
            Log.Warn("whatsapp", "Downloads scan failed.", ex);
            ScanFailed = true;
        }
        finally
        {
            IsScanning = false;
            Raise();
        }
    }

    /// <summary>Re-validates each ticked file (same place, same fingerprint, still a candidate) and recycles it.</summary>
    public async Task CleanSelectedAsync()
    {
        var chosen = Candidates.Where(c => IsSelected(c) && !IsExcluded(c)).ToList();
        if (chosen.Count == 0 || IsCleaning)
        {
            return;
        }

        IsCleaning = true;
        Raise();
        try
        {
            var (count, bytes, failed) = await Task.Run(() =>
            {
                var valid = new List<WhatsAppCandidate>();
                var failures = 0;
                foreach (var candidate in chosen)
                {
                    var fresh = Inspect(candidate.Path);
                    if (fresh is not null && fresh.Identity == candidate.Identity && SafePaths.IsDirectChild(candidate.Path, DownloadsFolder))
                    {
                        valid.Add(candidate);
                    }
                    else
                    {
                        failures++;
                    }
                }

                var outcomes = valid.Count == 0 ? [] : _recycler.Recycle(valid.Select(v => v.Path).ToList(), allowElevationPrompt: false);
                var recycled = outcomes.Where(o => o.Status == RecycleStatus.Recycled).Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                failures += valid.Count - recycled.Count;
                return (recycled.Count, valid.Where(v => recycled.Contains(v.Path)).Sum(v => v.Size), failures);
            }).ConfigureAwait(false);
            LastResult = (count, bytes, failed);
        }
        finally
        {
            IsCleaning = false;
        }

        await ScanAsync().ConfigureAwait(false);
    }

    private List<WhatsAppCandidate> Find(string folder)
    {
        var found = new List<WhatsAppCandidate>();
        foreach (var entry in _fs.List(folder))
        {
            if (Inspect(entry.Path, entry) is { } candidate)
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    private WhatsAppCandidate? Inspect(string path, FsEntry? known = null)
    {
        var entry = known ?? _fs.Stat(path);
        if (entry is null || entry.IsDirectory || entry.IsHidden || entry.IsReparsePoint || entry.IsCloudPlaceholder
            || WhatsAppRules.IsPartialDownload(entry.Name) || entry.Length <= 0)
        {
            return null;
        }

        var host = WhatsAppRules.WebMarkHost(_marks.ReadZoneIdentifier(entry.Path));
        WhatsAppEvidence evidence;
        if (WhatsAppRules.IsWhatsAppHost(host))
        {
            evidence = WhatsAppEvidence.WebMark;
        }
        else if (host is null && WhatsAppRules.HasDefaultName(entry.Name))
        {
            // A Mark of the Web from another site contradicts the name: not listed at all.
            evidence = WhatsAppEvidence.NameOnly;
        }
        else
        {
            return null;
        }

        if (_fs.Identity(entry.Path) is not { } identity)
        {
            return null;
        }

        var downloaded = entry.CreationUtc;
        return new WhatsAppCandidate(entry.Path, entry.Name, entry.Length, downloaded, entry.LastWriteUtc > downloaded ? entry.LastWriteUtc : downloaded, WhatsAppRules.CategoryOf(entry.Name), evidence, identity);
    }

    private void Raise() => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
}
