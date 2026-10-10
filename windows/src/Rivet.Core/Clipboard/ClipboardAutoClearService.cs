// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Clipboard;

/// <summary>
/// Clipboard auto clear (spec 06 §3.3): empties the OS clipboard after N
/// seconds of stillness, on sleep, display sleep and lock. It never touches
/// saved history entries, never loops on its own clear and works with the
/// history capture off (only the feature being installed matters).
/// </summary>
public sealed class ClipboardAutoClearService : IFeatureController, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReadSlot = TimeSpan.FromSeconds(5);

    private readonly ISettingsStore _settings;
    private readonly ClipboardWatcher _watcher;
    private readonly ISessionEvents _session;
    private readonly ClipboardHistoryService _history;
    private readonly IDisposable _subscription;
    private Timer? _timer;
    private bool _available;
    private bool _sessionHooked;
    private bool _readInFlight;
    private int _generation;
    private uint _lastChangeCount;
    private uint? _lastClearedChangeCount;
    private DateTimeOffset _lastChangeDate = DateTimeOffset.UtcNow;

    public ClipboardAutoClearService(ISettingsStore settings, ClipboardWatcher watcher, ISessionEvents session, ClipboardHistoryService history)
    {
        _settings = settings;
        _watcher = watcher;
        _session = session;
        _history = history;
        _subscription = settings.Observe(() => UiThread.Run(Reconfigure),
            ClipboardSettings.AutoClearOnDelay, ClipboardSettings.AutoClearOnSleep,
            ClipboardSettings.AutoClearOnDisplaySleep, ClipboardSettings.AutoClearOnScreenLock);
    }

    /// <summary>Clock seam for tests.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public bool IsDelayRunning => _timer is not null;

    public void Sync(bool available)
    {
        UiThread.Run(() =>
        {
            _available = available;
            Reconfigure();
        });
    }

    private void Reconfigure()
    {
        _generation++;
        var delayOn = _available && _settings.Get(ClipboardSettings.AutoClearOnDelay);
        if (delayOn && _timer is null)
        {
            // Whatever is on the clipboard already gets a full delay.
            _lastChangeDate = Now();
            _watcher.Lane.Run(c => c.SequenceNumber, counter => _lastChangeCount = counter);
            _timer = new Timer(_ => UiThread.Post(OnTick), null, Tick, Tick);
        }
        else if (!delayOn && _timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }

        var wantSession = _available && (_settings.Get(ClipboardSettings.AutoClearOnSleep)
                                         || _settings.Get(ClipboardSettings.AutoClearOnDisplaySleep)
                                         || _settings.Get(ClipboardSettings.AutoClearOnScreenLock));
        if (wantSession != _sessionHooked)
        {
            _sessionHooked = wantSession;
            if (wantSession)
            {
                _session.Occurred += OnSessionEvent;
            }
            else
            {
                _session.Occurred -= OnSessionEvent;
            }
        }
    }

    private void OnTick()
    {
        if (_timer is null || _readInFlight)
        {
            return;
        }

        _readInFlight = true;
        _watcher.Lane.Run<uint>(ReadSlot, (c, _) => c.SequenceNumber,
            (counter, completed) =>
            {
                _readInFlight = false;
                if (completed)
                {
                    Decide(counter);
                }
            });
    }

    /// <summary>One tick of the delay trigger (public for tests).</summary>
    public void Decide(uint counter)
    {
        var delay = TimeSpan.FromSeconds(_settings.Get(ClipboardSettings.AutoClearDelaySeconds));
        switch (ClipboardAutoClearRules.Decide(counter, _lastChangeCount, _lastClearedChangeCount, Now(), _lastChangeDate, delay))
        {
            case AutoClearDecision.NoteChange:
                _lastChangeCount = counter;
                _lastChangeDate = Now();
                break;
            case AutoClearDecision.Clear:
                Clear(ClipboardSettings.AutoClearOnDelay, expected: counter);
                break;
        }
    }

    private void OnSessionEvent(object? sender, SessionEventKind kind)
    {
        var trigger = kind switch
        {
            SessionEventKind.Sleep => ClipboardSettings.AutoClearOnSleep,
            SessionEventKind.DisplaySleep => ClipboardSettings.AutoClearOnDisplaySleep,
            SessionEventKind.Lock => ClipboardSettings.AutoClearOnScreenLock,
            _ => null,
        };
        if (trigger is not null && _settings.Get(trigger))
        {
            Clear(trigger, expected: null);
        }
    }

    /// <summary>
    /// Clears unless the configuration changed since queueing, the trigger is
    /// now off, nothing new arrived since the last clear (lock fires display
    /// sleep too), or a new copy arrived that deserves its own full delay.
    /// </summary>
    private void Clear(Setting<bool> trigger, uint? expected)
    {
        var generation = _generation;
        var alreadyCleared = _lastClearedChangeCount;
        _watcher.Lane.Run<uint?>(ReadSlot, (clipboard, _) =>
        {
            if (generation != Volatile.Read(ref _generation) || !_available || !_settings.Get(trigger))
            {
                return null;
            }

            var counter = clipboard.SequenceNumber;
            if (counter == alreadyCleared || (expected is { } e && e != counter))
            {
                return null;
            }

            return clipboard.Clear() ? clipboard.SequenceNumber : null;
        }, (cleared, completed) =>
        {
            if (!completed || cleared is not { } counter)
            {
                return;
            }

            _lastChangeCount = counter;
            _lastClearedChangeCount = counter;
            _lastChangeDate = Now();
            _watcher.NoteOwnWrite(counter);
            _history.History.LatestCopyId = null;
            Log.Info("clipboard", "Auto clear emptied the clipboard.");
        });
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _timer?.Dispose();
        if (_sessionHooked)
        {
            _session.Occurred -= OnSessionEvent;
        }
    }
}
