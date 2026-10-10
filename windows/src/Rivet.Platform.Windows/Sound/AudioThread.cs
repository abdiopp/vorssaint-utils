// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Rivet.Core.Diagnostics;

namespace Rivet.Platform.Windows.Sound;

/// <summary>
/// The sound module's one serial worker: a multithreaded-apartment thread
/// that owns every Core Audio object. All device reads and writes run here,
/// in order; COM callbacks (which arrive on the audio service's own threads)
/// only post work. Started on first use, stopped on dispose.
/// </summary>
internal sealed class AudioThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly string _name;
    private readonly object _startGate = new();
    private Thread? _thread;
    private int _disposed;

    public AudioThread(string name)
    {
        _name = name;
    }

    public bool IsCurrent => Thread.CurrentThread == _thread;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Queues work; returns false (and drops it) after dispose.</summary>
    public bool Post(Action work)
    {
        if (IsDisposed)
        {
            return false;
        }

        EnsureStarted();
        try
        {
            _queue.Add(work);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Completed while posting: shutting down.
            return false;
        }
    }

    /// <summary>Runs <paramref name="work"/> on the thread (inline when already there).</summary>
    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        if (IsCurrent)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Post(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        if (!queued)
        {
            completion.TrySetException(new ObjectDisposedException(_name));
        }

        return completion.Task;
    }

    private void EnsureStarted()
    {
        if (_thread is not null)
        {
            return;
        }

        lock (_startGate)
        {
            if (_thread is not null || IsDisposed)
            {
                return;
            }

            var thread = new Thread(Run) { IsBackground = true, Name = _name };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            _thread = thread;
        }
    }

    private void Run()
    {
        // Initialize COM explicitly: the activation of the audio policy
        // factory (WinRT) needs the thread to be in the multithreaded apartment.
        var hr = SoundNative.CoInitializeEx(0, SoundNative.CoinitMultithreaded);
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Error("sound", "Audio thread work failed.", ex);
                }
            }
        }
        finally
        {
            if (hr >= 0)
            {
                SoundNative.CoUninitialize();
            }
        }
    }

    /// <summary>Runs <paramref name="final"/> last on the thread, then stops it (waiting up to 3 s).</summary>
    public void Shutdown(Action final)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_thread is null)
        {
            _queue.CompleteAdding();
            return;
        }

        // Not disposed: the final work may still run after a timed-out wait.
        var done = new ManualResetEventSlim(false);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    final();
                }
                finally
                {
                    done.Set();
                }
            });
        }
        catch (InvalidOperationException)
        {
        }

        _queue.CompleteAdding();
        if (!IsCurrent)
        {
            done.Wait(TimeSpan.FromSeconds(3));
            _thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    public void Dispose() => Shutdown(static () => { });
}
