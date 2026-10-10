// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Clipboard;

/// <summary>
/// Clipboard history (spec 06 §3.2): capture on WM_CLIPBOARDUPDATE inside the
/// shared lane transaction, attribution through the clipboard owner process,
/// the ignored-apps list, the privacy rules, persistence and the copy/write
/// side used by every surface. The stored history stays readable while
/// capture is off; nothing is loaded while the feature is uninstalled.
/// </summary>
public sealed class ClipboardHistoryService : IFeatureController, IClipboardChangeHandler, IDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly ISettingsStore _settings;
    private readonly ClipboardWatcher _watcher;
    private readonly IForegroundService _foreground;
    private readonly string _filePath;
    private readonly ClipboardHistory _history = new();
    private readonly ClipboardSearch _search = new();
    private readonly HashSet<string> _pendingImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveGate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private Task _saveChain = Task.CompletedTask;
    private bool _loaded;
    private bool _saveScheduled;
    private bool _running;
    private bool _baselinePending;
    private int _lastCount;
    private int _writeInFlight;

    public ClipboardHistoryService(ISettingsStore settings, ClipboardWatcher watcher, IForegroundService foreground, AppPaths paths)
    {
        _settings = settings;
        _watcher = watcher;
        _foreground = foreground;
        _filePath = Path.Combine(paths.LocalRoot, ClipboardHistoryFile.FileName);
        Images = new ClipboardImageStore(Path.Combine(paths.LocalRoot, ClipboardHistoryFile.ImageFolderName));
        _history.Changed += (_, _) => ScheduleSave();
        _watcher.OwnWrite += counter => _lastCount = (int)Math.Max((uint)_lastCount, counter);
        _subscriptions.Add(settings.Observe(() => UiThread.Run(() => _history.Limit = settings.Get(ClipboardSettings.Limit)), ClipboardSettings.Limit));
    }

    /// <summary>Raised on the UI thread after a capture was recorded.</summary>
    public event EventHandler<ClipboardEntry>? Captured;

    public ClipboardImageStore Images { get; }

    public bool IsRunning => _running;

    /// <summary>The ordered history (loaded on first use).</summary>
    public ClipboardHistory History
    {
        get
        {
            EnsureLoaded();
            return _history;
        }
    }

    public string ImageLabel => L.Get("clipboard.imageEntryLabel");

    /// <summary>Set by the history window while it holds the keyboard: its own copies keep their old source.</summary>
    public bool PanelHasKeyboard { get; set; }

    public void Sync(bool available)
    {
        var want = available && _settings.Get(ClipboardSettings.Enabled);
        UiThread.Run(() =>
        {
            if (want)
            {
                Start();
            }
            else
            {
                Stop();
            }
        });
    }

    public IReadOnlyList<ClipboardEntry> Search(string query)
    {
        _search.ImageLabel = ImageLabel;
        return _search.Search(query, History.Entries, History.Version);
    }

    // ── Capture ────────────────────────────────────────────────────────

    public int Order => 10;

    private void Start()
    {
        EnsureLoaded();
        if (_running)
        {
            return;
        }

        _running = true;
        _baselinePending = true;
        _watcher.Activate(this);
        _watcher.CheckNow();
        Log.Info("clipboard", "History capture started.");
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _watcher.Deactivate(this);
        _history.LatestCopyId = null;
        Log.Info("clipboard", "History capture stopped.");
    }

    /// <summary>Runs one capture now (the history window opening or closing).</summary>
    public void CheckNow()
    {
        if (_running)
        {
            _watcher.CheckNow();
        }
    }

    void IClipboardChangeHandler.OnClipboardChanged(IClipboardPlatform clipboard)
    {
        if (!_running)
        {
            return;
        }

        var since = (uint)Volatile.Read(ref _lastCount);
        var sequence = clipboard.SequenceNumber;
        if (sequence == since && !Volatile.Read(ref _baselinePending))
        {
            return;
        }

        var parts = ClipboardReadParts.Text | ClipboardReadParts.Url | ClipboardReadParts.Owner;
        if (_settings.Get(ClipboardSettings.IncludeImagesFiles))
        {
            parts |= ClipboardReadParts.Image | ClipboardReadParts.Files;
        }

        var content = clipboard.Read(parts);
        if (_watcher.LastRewrite is { } rewrite && rewrite.Counter == content.Sequence)
        {
            // The cleaner replaced this copy a moment ago: credit the app that made it.
            content = content with { OwnerApp = rewrite.OwnerApp };
        }

        var hash = content.Png is { } png ? ClipboardHistoryFile.Sha256Hex(png) : null;
        UiThread.Post(() => Accept(content, since, hash));
    }

    private void Accept(ClipboardContent content, uint since, string? imageHash)
    {
        if (!_running)
        {
            return;
        }

        var candidate = content.IsConcealed ? null : Candidate(content, imageHash);
        if (_baselinePending)
        {
            // The first read after (re)start only finds what is already saved.
            _baselinePending = false;
            _lastCount = (int)content.Sequence;
            _history.LatestCopyId = candidate is null ? null : _history.Entries.FirstOrDefault(e => e.HasSameContent(candidate))?.Id;
            return;
        }

        if (ClipboardChangeCount.Accepted(content.Sequence, since, (uint)_lastCount) is not { } accepted)
        {
            return;
        }

        _lastCount = (int)accepted;
        _history.LatestCopyId = null;
        if (candidate is null)
        {
            return;
        }

        if (content.IsOwnWrite)
        {
            // This app's own copies (answers, colours, cleaned links) are recorded with no app.
            Promote(candidate with { SourceApp = null }, content.Png, keepOldSource: false);
            return;
        }

        var foreground = _foreground.Current();
        var self = foreground?.IsSelf == true;
        if (IsIgnored(content.OwnerApp) || (!self && IsIgnored(foreground?.Identity)))
        {
            return;
        }

        var fromPanel = PanelHasKeyboard || (self && content.OwnerApp is null);
        var source = fromPanel ? null : content.OwnerApp ?? (self ? null : foreground?.Identity);
        Promote(candidate with { SourceApp = source }, content.Png, keepOldSource: fromPanel);
    }

    private ClipboardEntry? Candidate(ClipboardContent content, string? imageHash)
    {
        if (content.Files is { Count: > 0 and <= 100 } files)
        {
            return new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = files.ToList(), CopiedAt = DateTimeOffset.UtcNow };
        }

        if (content.Png is not null && imageHash is not null && content.ImageWidth > 0 && content.ImageHeight > 0)
        {
            return new ClipboardEntry
            {
                Kind = ClipboardEntryKind.Image,
                ImageHash = imageHash,
                ImageWidth = content.ImageWidth,
                ImageHeight = content.ImageHeight,
                CopiedAt = DateTimeOffset.UtcNow,
            };
        }

        var text = ClipboardPreferredText.Preferred(content.Text, content.Url)?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > ClipboardHistory.MaxTextLength)
        {
            return null;
        }

        if (_settings.Get(ClipboardSettings.SkipSensitive) && ClipboardSensitiveText.LooksSensitive(text))
        {
            return null;
        }

        return new ClipboardEntry { Kind = ClipboardEntryKind.Text, Text = text, CopiedAt = DateTimeOffset.UtcNow };
    }

    private void Promote(ClipboardEntry candidate, byte[]? png, bool keepOldSource)
    {
        if (candidate.Kind != ClipboardEntryKind.Image || _history.Entries.Any(e => e.HasSameContent(candidate)))
        {
            var recorded = _history.Record(candidate, keepOldSource);
            Captured?.Invoke(this, recorded);
            return;
        }

        // A new image: store the PNG first; a store failure records nothing.
        // The name is reserved before the write so a concurrent sweep keeps it.
        var bytes = png!;
        var name = ClipboardImageStore.NewName();
        _pendingImages.Add(name);
        Task.Run(() => Images.Save(name, bytes)).ContinueWith(task =>
        {
            var saved = task.IsCompletedSuccessfully && task.Result;
            UiThread.Post(() =>
            {
                _pendingImages.Remove(name);
                if (saved && _running)
                {
                    var recorded = _history.Record(candidate with { ImageFile = name }, keepOldSource);
                    Captured?.Invoke(this, recorded);
                }
            });
        }, TaskScheduler.Default);
    }

    private bool IsIgnored(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
        {
            return false;
        }

        var ignored = _settings.Get(ClipboardSettings.IgnoredApps);
        if (ignored.Count == 0)
        {
            return false;
        }

        var name = ClipboardEntry.FileName(identity);
        return ignored.Any(i => string.Equals(i, identity, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ClipboardEntry.FileName(i), name, StringComparison.OrdinalIgnoreCase));
    }

    // ── Writing (copy / paste from the history) ───────────────────────

    /// <summary>
    /// Puts entries on the clipboard (history order). Completes with false
    /// when the content is stale, a write is already pending, or the
    /// clipboard did not answer within 5 s. On success the entries are reused.
    /// </summary>
    public Task<bool> CopyAsync(IReadOnlyList<ClipboardEntry> entries)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordered = entries.OrderBy(e => _history.IndexOf(e.Id) is var i && i < 0 ? int.MaxValue : i).ToList();
        if (ordered.Count == 0 || Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0)
        {
            completion.SetResult(false);
            return completion.Task;
        }

        _watcher.Lane.Run<(bool Ok, uint Counter)>(
            WriteTimeout,
            (clipboard, expired) =>
            {
                var data = ClipboardBatch.Build(ordered, Images.Read, File.Exists);
                if (data is null || expired())
                {
                    return (false, 0);
                }

                var ok = clipboard.Write(data, ClipboardWriteMarks.OwnSource);
                return (ok, clipboard.SequenceNumber);
            },
            (result, completed) =>
            {
                var ok = completed && result.Ok;
                if (ok)
                {
                    _history.Reuse(ordered.Select(e => e.Id).ToList());
                    _history.LatestCopyId = ordered.Count == 1 ? ordered[0].Id : null;
                }

                completion.TrySetResult(ok);
            },
            (result, completed) =>
            {
                Interlocked.Exchange(ref _writeInFlight, 0);
                if (completed && result.Ok)
                {
                    _watcher.NoteOwnWrite(result.Counter);
                }
            });
        return completion.Task;
    }

    // ── Editing (UI thread) ────────────────────────────────────────────

    public bool TogglePin(Guid id) => History.TogglePin(id);

    public bool Move(Guid id, int delta) => History.Move(id, delta);

    public void Delete(IEnumerable<Guid> ids) => History.Delete(ids);

    public int ClearUnpinned(IReadOnlyCollection<Guid> counted) => History.ClearUnpinned(counted);

    public ClipboardEditResult Edit(Guid id, string text) => History.Edit(id, text);

    // ── Persistence ────────────────────────────────────────────────────

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _history.Limit = _settings.Get(ClipboardSettings.Limit);
        var entries = ClipboardHistoryFile.Load(_filePath);
        _history.Replace(entries);
        var live = _history.Entries.Select(e => e.ImageFile).OfType<string>().ToList();
        Task.Run(() => Images.Sweep(live));
    }

    private void ScheduleSave()
    {
        if (_saveScheduled || !_loaded)
        {
            return;
        }

        _saveScheduled = true;
        UiThread.Post(SaveNow);
    }

    private void SaveNow()
    {
        _saveScheduled = false;
        var snapshot = _history.Entries.ToList();
        var version = _history.Version;
        lock (_saveGate)
        {
            _saveChain = _saveChain.ContinueWith(_ => WriteSnapshot(snapshot, version), TaskScheduler.Default);
        }
    }

    private void WriteSnapshot(IReadOnlyList<ClipboardEntry> snapshot, int version)
    {
        var (bytes, written) = ClipboardHistoryFile.EncodeAll(snapshot);
        if (!ClipboardHistoryFile.Write(_filePath, bytes))
        {
            return;
        }

        UiThread.Post(() =>
        {
            if (_history.Version == version && written.Count != snapshot.Count)
            {
                _history.Replace(written);
            }

            var live = _history.Entries.Select(e => e.ImageFile).OfType<string>().Concat(_pendingImages).ToList();
            Task.Run(() => Images.Sweep(live));
        });
    }

    /// <summary>Writes any pending change synchronously (quit, so a last privacy Clear is durable).</summary>
    public void Flush()
    {
        if (!_loaded)
        {
            return;
        }

        Task chain;
        lock (_saveGate)
        {
            chain = _saveChain;
        }

        try
        {
            chain.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }

        // A save posted to a UI loop that has already stopped would never run: write it here.
        if (_saveScheduled)
        {
            _saveScheduled = false;
            var (bytes, _) = ClipboardHistoryFile.EncodeAll(_history.Entries.ToList());
            ClipboardHistoryFile.Write(_filePath, bytes);
        }
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        if (_running)
        {
            _watcher.Deactivate(this);
            _running = false;
        }

        Flush();
    }
}
