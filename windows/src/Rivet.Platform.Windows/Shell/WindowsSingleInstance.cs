// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Text.Json;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Util;

namespace Rivet.Platform.Windows.Shell;

/// <summary>
/// A named mutex per session decides the primary instance; later launches
/// send their arguments over a current-user-only named pipe and exit.
/// </summary>
public sealed class WindowsSingleInstance : ISingleInstanceService
{
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public event EventHandler<IReadOnlyList<string>>? ActivationRequested;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, AppIdentity.InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_cts.Token));
        return true;
    }

    public bool SendToPrimary(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", AppIdentity.InstancePipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            JsonSerializer.Serialize(client, args);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("instance", "Could not reach the running instance.", ex);
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    AppIdentity.InstancePipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                var args = await JsonSerializer.DeserializeAsync<string[]>(server, cancellationToken: token).ConfigureAwait(false) ?? [];
                UiThread.Post(() => ActivationRequested?.Invoke(this, args));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Log.Warn("instance", "Activation request failed.", ex);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
