// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Rivet.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Small rolling file logger. One file per day under the logs folder, seven
/// days kept. Writes happen on a background thread; call <see cref="Flush"/>
/// on exit. Until <see cref="Initialize"/> runs, lines only go to the debugger.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static string? _directory;
    private static Thread? _writer;
    private static readonly ManualResetEventSlim Drained = new(true);

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void Initialize(string directory, LogLevel minimumLevel = LogLevel.Info)
    {
        MinimumLevel = minimumLevel;
        try
        {
            Directory.CreateDirectory(directory);
            _directory = directory;
            PruneOldFiles(directory, TimeSpan.FromDays(7));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Log: cannot use {directory}: {ex.Message}");
            return;
        }

        if (_writer is null)
        {
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "LogWriter" };
            _writer.Start();
        }
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);

    public static void Warn(string category, string message, Exception? exception = null) =>
        Write(LogLevel.Warning, category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write(LogLevel.Error, category, message, exception);

    public static void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                _ => "ERR",
            })
            .Append(" [").Append(category).Append("] ")
            .Append(message);
        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            if (level >= LogLevel.Error && exception.StackTrace is { } stack)
            {
                line.AppendLine().Append(stack);
            }
        }

        var text = line.ToString();
        System.Diagnostics.Debug.WriteLine(text);
        if (_directory is not null)
        {
            Drained.Reset();
            Queue.TryAdd(text);
        }
    }

    /// <summary>Blocks until queued lines are on disk (at most <paramref name="timeout"/>).</summary>
    public static void Flush(TimeSpan? timeout = null)
    {
        if (_directory is null)
        {
            return;
        }

        Drained.Wait(timeout ?? TimeSpan.FromSeconds(2));
    }

    private static void WriteLoop()
    {
        foreach (var first in Queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_directory!, $"app-{DateTime.Now:yyyyMMdd}.log");
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(first);
                while (Queue.TryTake(out var next))
                {
                    writer.WriteLine(next);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Log write failed: {ex.Message}");
            }

            if (Queue.Count == 0)
            {
                Drained.Set();
            }
        }
    }

    private static void PruneOldFiles(string directory, TimeSpan maxAge)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "app-*.log"))
        {
            try
            {
                if (DateTime.Now - File.GetLastWriteTime(file) > maxAge)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
