// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Maintenance.Processes;

/// <summary>An external tool to run: an argv array, never a shell string.</summary>
public sealed record CommandRequest
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    /// <summary>Total time limit (read commands). Null means no total limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Ends the command after this long without any output (long operations).</summary>
    public TimeSpan? InactivityTimeout { get; init; }

    /// <summary>Output kept in the result; later output is dropped (the line callback still sees it).</summary>
    public int OutputCapChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>Extra environment variables (null value removes one).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    public override string ToString() => FileName + " " + string.Join(' ', Arguments);
}

public enum CommandOutcome
{
    Exited,
    TimedOut,
    Cancelled,
    FailedToStart,
}

public sealed record CommandResult(CommandOutcome Outcome, int ExitCode, string Output, string Error)
{
    public bool Succeeded => Outcome == CommandOutcome.Exited && ExitCode == 0;

    /// <summary>The exit code as an unsigned HRESULT-style value (winget reports 0x8A15xxxx codes).</summary>
    public uint ExitCodeHex => unchecked((uint)ExitCode);

    public static CommandResult NotStarted(string message) => new(CommandOutcome.FailedToStart, -1, string.Empty, message);
}

/// <summary>Runs external tools (winget, uninstallers, PowerShell for elevated helpers).</summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs the command and returns when it exits, times out or is cancelled.
    /// <paramref name="onLine"/> receives every output line (stdout and stderr,
    /// carriage-return progress updates included) on a background thread.
    /// </summary>
    Task<CommandResult> RunAsync(CommandRequest request, Action<string>? onLine = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ICommandRunner"/> over <see cref="Process"/>: no shell, UTF-8
/// output, CreateNoWindow, total and inactivity timeouts, cancellation that
/// ends the whole process tree, and a cap on the captured output.
/// </summary>
public sealed class CommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(CommandRequest request, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (request.WorkingDirectory is { } directory)
        {
            info.WorkingDirectory = directory;
        }

        if (request.Environment is { } environment)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null)
                {
                    info.Environment.Remove(name);
                }
                else
                {
                    info.Environment[name] = value;
                }
            }
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                return CommandResult.NotStarted("The process did not start.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Log.Warn("command", $"Could not start {request.FileName}.", ex);
            return CommandResult.NotStarted(ex.Message);
        }

        try
        {
            // Nothing is ever typed into these tools: an interactive prompt must fail, not hang.
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        var output = new CappedBuffer(request.OutputCapChars);
        var error = new CappedBuffer(request.OutputCapChars / 4);
        var lastActivity = Stopwatch.StartNew();
        var activityLock = new object();

        void Touch()
        {
            lock (activityLock)
            {
                lastActivity.Restart();
            }
        }

        TimeSpan SinceActivity()
        {
            lock (activityLock)
            {
                return lastActivity.Elapsed;
            }
        }

        var stdout = PumpAsync(process.StandardOutput, output, onLine, Touch);
        var stderr = PumpAsync(process.StandardError, error, onLine, Touch);
        var total = Stopwatch.StartNew();
        var outcome = CommandOutcome.Exited;
        var exited = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            while (true)
            {
                var tick = Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                await Task.WhenAny(exited, tick).ConfigureAwait(false);
                if (exited.IsCompleted)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    outcome = CommandOutcome.Cancelled;
                    break;
                }

                if (request.Timeout is { } timeout && total.Elapsed > timeout)
                {
                    outcome = CommandOutcome.TimedOut;
                    break;
                }

                if (request.InactivityTimeout is { } quiet && SinceActivity() > quiet)
                {
                    outcome = CommandOutcome.TimedOut;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcome = CommandOutcome.Cancelled;
        }

        if (outcome != CommandOutcome.Exited)
        {
            Stop(process);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("command", $"{Path.GetFileName(request.FileName)} did not release its output in time.");
        }

        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new CommandResult(outcome, exitCode, output.ToString(), error.ToString());
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Log.Warn("command", "Could not stop a timed-out or cancelled command.", ex);
        }
    }

    /// <summary>Reads a stream in chunks, splitting lines on LF and CR (progress updates overwrite with CR).</summary>
    private static async Task PumpAsync(StreamReader reader, CappedBuffer sink, Action<string>? onLine, Action touch)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                touch();
                sink.Append(buffer.AsSpan(0, read));
                if (onLine is null)
                {
                    continue;
                }

                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c is '\n' or '\r')
                    {
                        if (line.Length > 0)
                        {
                            SafeInvoke(onLine, line.ToString());
                            line.Clear();
                        }
                    }
                    else if (line.Length < 8192)
                    {
                        line.Append(c);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        if (onLine is not null && line.Length > 0)
        {
            SafeInvoke(onLine, line.ToString());
        }
    }

    private static void SafeInvoke(Action<string> onLine, string line)
    {
        try
        {
            onLine(line);
        }
        catch (Exception ex)
        {
            Log.Warn("command", "An output handler failed.", ex);
        }
    }

    private sealed class CappedBuffer(int cap)
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();

        public void Append(ReadOnlySpan<char> text)
        {
            lock (_gate)
            {
                var room = cap - _builder.Length;
                if (room <= 0)
                {
                    return;
                }

                _builder.Append(text.Length <= room ? text : text[..room]);
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _builder.ToString();
            }
        }
    }
}
