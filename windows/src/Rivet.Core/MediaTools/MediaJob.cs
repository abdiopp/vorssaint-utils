// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.MediaTools;

public enum MediaJobPhase
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A problem a job reports to the user (message already localized).</summary>
public sealed class MediaJobException(string message) : Exception(message);

public sealed record MediaItemFailure(string Input, string Message);

/// <summary>What a finished job produced.</summary>
public sealed record MediaJobResult
{
    public required MediaTool Tool { get; init; }

    public IReadOnlyList<string> Outputs { get; init; } = [];

    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>Original bytes of the successful items only.</summary>
    public long InputBytes { get; init; }

    public long OutputBytes { get; init; }

    public IReadOnlyList<MediaItemFailure> Failures { get; init; } = [];

    /// <summary>OCR text (Text tool).</summary>
    public string? Text { get; init; }

    public int Succeeded => Outputs.Count;
}

/// <summary>
/// One job at a time per service (spec 07 §3.6.3): idle → running(0–1) →
/// completed | failed | cancelled. Each job has an id and updates from a stale
/// job are dropped; starting a new job cancels the previous one; cancel
/// publishes "cancelled" immediately. Thread-safe; events fire on the caller's thread.
/// </summary>
public sealed class MediaJobState
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private int _jobId;

    public event EventHandler? Changed;

    public MediaJobPhase Phase { get; private set; } = MediaJobPhase.Idle;

    /// <summary>0…1 while running.</summary>
    public double Progress { get; private set; }

    public MediaJobResult? Result { get; private set; }

    public string? Error { get; private set; }

    public int JobId
    {
        get
        {
            lock (_gate)
            {
                return _jobId;
            }
        }
    }

    public bool IsRunning => Phase == MediaJobPhase.Running;

    /// <summary>Starts a new job (cancelling any running one). Returns its id and token.</summary>
    public (int Id, CancellationToken Token) Begin()
    {
        CancellationTokenSource? previous;
        int id;
        CancellationTokenSource cancel;
        lock (_gate)
        {
            previous = _cancel;
            id = ++_jobId;
            cancel = _cancel = new CancellationTokenSource();
            Phase = MediaJobPhase.Running;
            Progress = 0;
            Result = null;
            Error = null;
        }

        previous?.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
        return (id, cancel.Token);
    }

    /// <summary>Progress for job <paramref name="id"/>; stale jobs and backwards steps below 1% are ignored.</summary>
    public void Report(int id, double progress)
    {
        lock (_gate)
        {
            if (id != _jobId || Phase != MediaJobPhase.Running)
            {
                return;
            }

            var clamped = Math.Clamp(progress, 0, 1);
            if ((int)(clamped * 100) == (int)(Progress * 100))
            {
                Progress = clamped;
                return;
            }

            Progress = clamped;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Complete(int id, MediaJobResult result) => Finish(id, MediaJobPhase.Completed, result, null);

    public void Fail(int id, string message) => Finish(id, MediaJobPhase.Failed, null, message);

    /// <summary>Cancels the running job and publishes "cancelled" right away.</summary>
    public void Cancel()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (Phase != MediaJobPhase.Running)
            {
                return;
            }

            cancel = _cancel;
            _jobId++;
            Phase = MediaJobPhase.Cancelled;
        }

        cancel?.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back to idle (new input, cleared input, tool switch); cancels a running job silently.</summary>
    public void Reset()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            cancel = Phase == MediaJobPhase.Running ? _cancel : null;
            _jobId++;
            Phase = MediaJobPhase.Idle;
            Progress = 0;
            Result = null;
            Error = null;
        }

        cancel?.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Finish(int id, MediaJobPhase phase, MediaJobResult? result, string? error)
    {
        lock (_gate)
        {
            if (id != _jobId || Phase != MediaJobPhase.Running)
            {
                return;
            }

            Phase = phase;
            Progress = phase == MediaJobPhase.Completed ? 1 : Progress;
            Result = result;
            Error = error;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
