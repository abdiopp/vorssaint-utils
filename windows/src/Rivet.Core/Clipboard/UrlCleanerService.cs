// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Clipboard;

/// <summary>
/// Clean URL (spec 06 §3.5): the rule set in force, the automatic watcher
/// that rewrites a copied link when trackers were removed and nothing else
/// would be lost, and the manual copy used by the panels and the Command Bar.
/// </summary>
public sealed class UrlCleanerService : IFeatureController, IClipboardChangeHandler, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ClipboardWatcher _watcher;
    private readonly IDisposable _rulesSubscription;
    private UrlCleanerRules? _rules;
    private int _lastSeen;
    private bool _active;

    public UrlCleanerService(ISettingsStore settings, ClipboardWatcher watcher)
    {
        _settings = settings;
        _watcher = watcher;
        _rulesSubscription = settings.Observe(() =>
        {
            _rules = null;
            UiThread.Run(() => RulesChanged?.Invoke(this, EventArgs.Empty));
        }, ClipboardSettings.UrlCleanerCustomParameters, ClipboardSettings.UrlCleanerSiteParameters, ClipboardSettings.UrlCleanerDisabledParameters);
        _watcher.OwnWrite += counter => _lastSeen = (int)Math.Max((uint)_lastSeen, counter);
    }

    /// <summary>Raised on the UI thread when a rule changed (open cleaners recompute).</summary>
    public event EventHandler? RulesChanged;

    /// <summary>Raised on the UI thread after the automatic mode cleaned a copied link.</summary>
    public event EventHandler? Cleaned;

    public bool IsActive => _active;

    /// <summary>The names the last automatic cleaning removed (Settings shows "Removed utm_source, fbclid").</summary>
    public IReadOnlyList<string> LastRemoved { get; private set; } = [];

    public UrlCleanerRules Rules => _rules ??= UrlCleanerRules.FromStorage(
        _settings.Get(ClipboardSettings.UrlCleanerCustomParameters),
        _settings.Get(ClipboardSettings.UrlCleanerSiteParameters),
        _settings.Get(ClipboardSettings.UrlCleanerDisabledParameters));

    public void SaveRules(UrlCleanerRules rules)
    {
        _settings.Set(ClipboardSettings.UrlCleanerCustomParameters, rules.CustomParametersStorage());
        _settings.Set(ClipboardSettings.UrlCleanerSiteParameters, rules.SiteParametersStorage());
        _settings.Set(ClipboardSettings.UrlCleanerDisabledParameters, rules.DisabledParametersStorage());
        _rules = rules;
    }

    public UrlCleanOutcome Evaluate(string input) => UrlCleanOutcome.From(input, UrlCleaning.Clean(input, Rules));

    /// <summary>The message every surface shows for an outcome (spec 06 §3.5.1).</summary>
    public static string Message(UrlCleanOutcome outcome) => outcome.Kind switch
    {
        UrlCleanOutcomeKind.NotAUrl => L.Get("Strings.urlCleanerNoURL"),
        UrlCleanOutcomeKind.Unchanged => L.Get("Strings.urlCleanerNoChange"),
        UrlCleanOutcomeKind.Rewritten => L.Get("Strings.urlCleanerCleaned"),
        _ => L.Format("Strings.urlCleanerRemovedFormat", string.Join(", ", outcome.Removed)),
    };

    public void Sync(bool available)
    {
        var want = available && _settings.Get(ClipboardSettings.UrlCleanerEnabled);
        UiThread.Run(() =>
        {
            if (want == _active)
            {
                return;
            }

            _active = want;
            if (want)
            {
                // Baseline: whatever is on the clipboard already is left alone.
                _watcher.Lane.Run(clipboard => _lastSeen = (int)clipboard.SequenceNumber);
                _watcher.Activate(this);
            }
            else
            {
                _watcher.Deactivate(this);
            }
        });
    }

    public int Order => 0;

    void IClipboardChangeHandler.OnClipboardChanged(IClipboardPlatform clipboard)
    {
        if (!_active)
        {
            return;
        }

        var sequence = clipboard.SequenceNumber;
        if (sequence == (uint)Volatile.Read(ref _lastSeen))
        {
            return;
        }

        Volatile.Write(ref _lastSeen, (int)sequence);
        var content = clipboard.Read(ClipboardReadParts.Text | ClipboardReadParts.Url | ClipboardReadParts.Html | ClipboardReadParts.Owner);
        if (content.IsConcealed || content.IsOwnWrite || !ClipboardFormats.CanRewrite(content.Formats))
        {
            return;
        }

        var text = content.Text ?? content.Url;
        if (string.IsNullOrWhiteSpace(text) || UrlCleaning.Clean(text, Rules) is not { Removed.Count: > 0 } result)
        {
            return;
        }

        if (content.Html is { } html && !RichText.HtmlIsOnlyLink(RichText.HtmlFragment(html), text.Trim()))
        {
            return;
        }

        if (clipboard.SequenceNumber != sequence)
        {
            return; // a newer copy arrived meanwhile
        }

        if (!clipboard.Write(new ClipboardWriteData { Text = result.Url, Url = result.Url }, ClipboardWriteMarks.None))
        {
            return;
        }

        var rewritten = clipboard.SequenceNumber;
        Volatile.Write(ref _lastSeen, (int)rewritten);
        _watcher.LastRewrite = (rewritten, content.OwnerApp);
        Log.Info("urlCleaner", $"Removed {result.Removed.Count} tracking parameter(s) from a copied link.");
        UiThread.Post(() =>
        {
            LastRemoved = result.Removed;
            Cleaned?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Copies a cleaned link (Copy buttons, Command Bar): string plus URL flavour
    /// with the app's source mark, so the history records it with no app and
    /// the watcher does not process it again.
    /// </summary>
    public Task<bool> CopyAsync(string url)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.Lane.Run<bool>(TimeSpan.FromSeconds(5), (clipboard, _) =>
        {
            var ok = clipboard.Write(new ClipboardWriteData { Text = url, Url = url }, ClipboardWriteMarks.OwnSource);
            if (ok)
            {
                Volatile.Write(ref _lastSeen, (int)clipboard.SequenceNumber);
            }

            return ok;
        }, (ok, completed) => completion.TrySetResult(completed && ok));
        return completion.Task;
    }

    /// <summary>The clipboard's plain text, for the "Paste" buttons and "Clean the copied link".</summary>
    public async Task<string?> ReadClipboardTextAsync()
    {
        var (content, completed) = await _watcher.Lane.RunAsync(TimeSpan.FromSeconds(2), c => c.Read(ClipboardReadParts.Text | ClipboardReadParts.Url)).ConfigureAwait(true);
        return completed && content is { IsConcealed: false } ? content.Text ?? content.Url : null;
    }

    /// <summary>
    /// "Clean the copied link": cleans what is on the clipboard and copies the
    /// result when it changed. Returns the message to show.
    /// </summary>
    public async Task<string> CleanClipboardAsync()
    {
        var text = await ReadClipboardTextAsync().ConfigureAwait(true) ?? string.Empty;
        var outcome = Evaluate(text);
        if (outcome.Kind is UrlCleanOutcomeKind.Removed or UrlCleanOutcomeKind.Rewritten && outcome.Url is { } url)
        {
            await CopyAsync(url).ConfigureAwait(true);
        }

        return Message(outcome);
    }

    public void Dispose()
    {
        _rulesSubscription.Dispose();
        if (_active)
        {
            _watcher.Deactivate(this);
        }
    }
}
