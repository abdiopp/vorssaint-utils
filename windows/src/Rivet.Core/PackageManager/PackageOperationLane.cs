// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.PackageManager;

/// <summary>One package to act on.</summary>
public sealed record PackageOperationRequest(PackageOperationKind Kind, string? PackageId, string? PackageName, string? Source = null);

public enum OperationResult
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>An immutable view of the running (or last) operation for the status card.</summary>
public sealed record PackageOperationState
{
    public required PackageOperationKind Kind { get; init; }

    /// <summary>Display name of the package, or null for "packages" (update all, sources).</summary>
    public string? PackageName { get; init; }

    public string? PackageId { get; init; }

    public required DateTime StartedUtc { get; init; }

    public DateTime? EndedUtc { get; init; }

    public OperationPhase Phase { get; init; } = OperationPhase.Preparing;

    /// <summary>0…1 once winget printed a percentage or byte counter.</summary>
    public double? Progress { get; init; }

    public string? Activity { get; init; }

    public OperationResult Result { get; init; } = OperationResult.Running;

    /// <summary>Plain-text failure (or the "restart to finish" note).</summary>
    public string? Message { get; init; }

    public bool NeedsRestart { get; init; }

    /// <summary>Position in a batch (1-based) and its size; 1 of 1 for single operations.</summary>
    public int BatchIndex { get; init; } = 1;

    public int BatchCount { get; init; } = 1;

    /// <summary>Names that failed in a batch.</summary>
    public IReadOnlyList<string> FailedNames { get; init; } = [];

    public string Log { get; init; } = string.Empty;

    public bool IsRunning => Result == OperationResult.Running;

    public TimeSpan Elapsed => (EndedUtc ?? DateTime.UtcNow) - StartedUtc;
}

/// <summary>
/// One package operation at a time, shared by the package manager, App
/// updates and the Uninstaller. Streams winget's output into a log and a
/// status (phase, percentage, activity), cancels by ending the process tree,
/// and clears itself like the macOS lane: success after 8 s, cancelled after
/// 6 s, failures keep their status and drop only the log after 20 s.
/// </summary>
public sealed class PackageOperationLane
{
    public static readonly TimeSpan SuccessClearDelay = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan CancelledClearDelay = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan FailedLogClearDelay = TimeSpan.FromSeconds(20);
    private const int LogCap = 256 * 1024;

    private readonly WingetClient _client;
    private readonly ICommandRunner _runner;
    private readonly object _gate = new();
    private readonly StringBuilder _log = new();
    private PackageOperationState? _state;
    private CancellationTokenSource? _cancellation;
    private int _generation;
    private long _lastNotifyTicks;

    public PackageOperationLane(WingetClient client, ICommandRunner runner)
    {
        _client = client;
        _runner = runner;
    }

    /// <summary>Raised on the UI thread when the status changes (throttled while output streams).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread once an operation (or batch) ends.</summary>
    public event EventHandler<PackageOperationState>? Finished;

    public PackageOperationState? Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsRunning => Current?.IsRunning == true;

    /// <summary>The technical log of the running or last operation.</summary>
    public string LogText
    {
        get
        {
            lock (_gate)
            {
                return _log.ToString();
            }
        }
    }

    public Task<OperationResult> RunAsync(PackageOperationRequest request) => RunBatchAsync([request]);

    /// <summary>
    /// Runs the requests one after another (progress per package). Returns
    /// Failed immediately, without running anything, while another operation runs.
    /// </summary>
    public async Task<OperationResult> RunBatchAsync(IReadOnlyList<PackageOperationRequest> requests)
    {
        if (requests.Count == 0)
        {
            return OperationResult.Succeeded;
        }

        CancellationTokenSource cancellation;
        int generation;
        lock (_gate)
        {
            if (_state?.IsRunning == true)
            {
                return OperationResult.Failed;
            }

            _cancellation?.Dispose();
            _cancellation = cancellation = new CancellationTokenSource();
            generation = ++_generation;
            _log.Clear();
            var first = requests[0];
            _state = new PackageOperationState
            {
                Kind = requests.Count > 1 ? PackageOperationKind.UpgradeAll : first.Kind,
                PackageName = requests.Count > 1 ? null : first.PackageName,
                PackageId = requests.Count > 1 ? null : first.PackageId,
                StartedUtc = DateTime.UtcNow,
                BatchCount = requests.Count,
            };
        }

        Notify(force: true);
        var failedNames = new List<string>();
        var failures = new List<string>();
        var needsRestart = false;
        var result = OperationResult.Succeeded;
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            Update(s => s with { BatchIndex = i + 1, Phase = OperationPhase.Preparing, Progress = null, Activity = null, PackageName = requests.Count > 1 ? request.PackageName : s.PackageName });
            var single = await RunOneAsync(request, cancellation.Token).ConfigureAwait(false);
            needsRestart |= single.NeedsRestart;
            if (single.Result == OperationResult.Cancelled)
            {
                result = OperationResult.Cancelled;
                break;
            }

            if (single.Result == OperationResult.Failed)
            {
                result = OperationResult.Failed;
                failedNames.Add(request.PackageName ?? request.PackageId ?? "?");
                if (single.Message is { } message)
                {
                    failures.Add(requests.Count > 1 ? $"{request.PackageName ?? request.PackageId}: {message}" : message);
                }
            }
        }

        string? finalMessage = result switch
        {
            OperationResult.Failed => string.Join(Environment.NewLine, failures.Take(3)),
            OperationResult.Cancelled => L.Get("Strings.homebrewOperationCancelled"),
            _ => needsRestart ? L.Get("win.packageManager.errorRestart") : null,
        };

        PackageOperationState final;
        lock (_gate)
        {
            final = _state! with
            {
                Result = result,
                Message = string.IsNullOrWhiteSpace(finalMessage) ? null : finalMessage,
                NeedsRestart = needsRestart,
                EndedUtc = DateTime.UtcNow,
                FailedNames = failedNames,
                Phase = result == OperationResult.Succeeded ? OperationPhase.Finishing : _state!.Phase,
                Progress = result == OperationResult.Succeeded ? 1 : _state!.Progress,
                Log = _log.ToString(),
            };
            _state = final;
        }

        Notify(force: true);
        UiThread.Post(() => Finished?.Invoke(this, final));
        ScheduleClear(generation, result);
        return result;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_state?.IsRunning == true)
            {
                _cancellation?.Cancel();
            }
        }
    }

    /// <summary>Clears the technical log of a finished operation.</summary>
    public void ClearLog()
    {
        lock (_gate)
        {
            if (_state is null || _state.IsRunning)
            {
                return;
            }

            _log.Clear();
            _state = _state with { Log = string.Empty };
        }

        Notify(force: true);
    }

    /// <summary>Removes a finished operation's status.</summary>
    public void Dismiss()
    {
        lock (_gate)
        {
            if (_state?.IsRunning == true)
            {
                return;
            }

            _state = null;
        }

        Notify(force: true);
    }

    private async Task<(OperationResult Result, string? Message, bool NeedsRestart)> RunOneAsync(PackageOperationRequest request, CancellationToken cancellationToken)
    {
        if (await _client.DetectAsync(cancellationToken: cancellationToken).ConfigureAwait(false) != WingetAvailability.Available)
        {
            return (OperationResult.Failed, L.Get("win.packageManager.missingTitle"), false);
        }

        var command = _client.OperationRequest(request.Kind, request.PackageId, request.Source);
        if (command is null)
        {
            return (OperationResult.Failed, L.Get("win.packageManager.errorNotFound"), false);
        }

        AppendLog("$ winget " + string.Join(' ', command.Arguments.Select(Quote)));
        var result = await _runner.RunAsync(command, line => OnLine(line, request.Kind), cancellationToken).ConfigureAwait(false);
        AppendLog(string.Create(CultureInfo.InvariantCulture, $"[{result.Outcome}, exit code 0x{result.ExitCodeHex:X8}]"));
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AppendLog(result.Error.Trim());
        }

        if (result.Outcome == CommandOutcome.Cancelled || cancellationToken.IsCancellationRequested)
        {
            return (OperationResult.Cancelled, null, false);
        }

        if (result.Succeeded)
        {
            return (OperationResult.Succeeded, null, false);
        }

        if (result.Outcome == CommandOutcome.Exited && WingetErrors.IsSuccessWithRestart(result.ExitCodeHex))
        {
            return (OperationResult.Succeeded, null, true);
        }

        Log.Warn("winget", $"Operation {request.Kind} {request.PackageId} failed: {result.Outcome} 0x{result.ExitCodeHex:X8}.");
        return (OperationResult.Failed, WingetClient.DescribeFailure(result), false);
    }

    private void OnLine(string raw, PackageOperationKind kind)
    {
        var line = WingetOutput.CleanLines(raw).FirstOrDefault() ?? string.Empty;
        if (line.Length == 0)
        {
            return;
        }

        var phase = WingetProgress.PhaseOf(line, kind);
        var progress = WingetProgress.ProgressOf(line);
        var activity = WingetProgress.ActivityOf(line);
        if (activity is not null)
        {
            AppendLog(line);
        }

        Update(s => s with
        {
            Phase = phase ?? s.Phase,
            Progress = progress ?? (phase is { } p && p != s.Phase ? null : s.Progress),
            Activity = activity ?? s.Activity,
        }, force: phase is not null);
    }

    private void AppendLog(string line)
    {
        lock (_gate)
        {
            if (_log.Length > LogCap)
            {
                _log.Remove(0, Math.Min(_log.Length, LogCap / 4));
            }

            _log.AppendLine(line);
        }
    }

    private void Update(Func<PackageOperationState, PackageOperationState> change, bool force = false)
    {
        lock (_gate)
        {
            if (_state is null)
            {
                return;
            }

            _state = change(_state);
        }

        Notify(force);
    }

    private void Notify(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastNotifyTicks);
        if (!force && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(120))
        {
            return;
        }

        Interlocked.Exchange(ref _lastNotifyTicks, now);
        UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private void ScheduleClear(int generation, OperationResult result)
    {
        var delay = result switch
        {
            OperationResult.Succeeded => SuccessClearDelay,
            OperationResult.Cancelled => CancelledClearDelay,
            _ => FailedLogClearDelay,
        };
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_generation != generation || _state is null || _state.IsRunning)
                {
                    return;
                }

                if (result == OperationResult.Failed)
                {
                    _log.Clear();
                    _state = _state with { Log = string.Empty };
                }
                else
                {
                    _state = null;
                }
            }

            Notify(force: true);
        }, TaskScheduler.Default);
    }

    private static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "._+@%/=:,-\\".Contains(c)) ? value : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
