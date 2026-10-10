// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Launcher;

/// <summary>The outcome of a saved script run.</summary>
public sealed record ScriptResult(bool Started, int ExitCode, string Output)
{
    public bool Failed => !Started || (ExitCode != 0 && Output.Length == 0);
}

/// <summary>
/// Runs a saved script without a visible window (spec 06 §3.8.10, §7.6):
/// stdout and stderr merged, at most 64 KiB, trimmed, 5 s timeout then the
/// whole process tree is killed. Dispatch on Windows: .exe/.com directly,
/// .ps1 through powershell.exe -File (arguments are never evaluated),
/// .bat/.cmd through cmd.exe. Batch arguments containing cmd metacharacters
/// are refused rather than escaped (cmd's quoting rules cannot be made safe).
/// </summary>
public static class ScriptRunner
{
    public const int MaxOutputBytes = 64 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly char[] BatchMetacharacters = ['"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n'];

    /// <summary>The process to start, or null when the file type is not runnable.</summary>
    public static ProcessStartInfo? StartInfo(string path, string argument, bool windows)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        ProcessStartInfo info;
        if (windows)
        {
            switch (extension)
            {
                case ".exe" or ".com":
                    info = new ProcessStartInfo(path);
                    if (argument.Length > 0)
                    {
                        info.ArgumentList.Add(argument);
                    }

                    break;
                case ".ps1":
                    info = new ProcessStartInfo("powershell.exe");
                    foreach (var part in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
                    {
                        info.ArgumentList.Add(part);
                    }

                    if (argument.Length > 0)
                    {
                        info.ArgumentList.Add(argument);
                    }

                    break;
                case ".bat" or ".cmd":
                    if (argument.IndexOfAny(BatchMetacharacters) >= 0 || path.IndexOfAny(BatchMetacharacters) >= 0)
                    {
                        return null;
                    }

                    var command = argument.Length > 0 ? $"\"\"{path}\" \"{argument}\"\"" : $"\"\"{path}\"\"";
                    info = new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c " + command };
                    break;
                default:
                    return null;
            }
        }
        else
        {
            info = new ProcessStartInfo(path);
            if (argument.Length > 0)
            {
                info.ArgumentList.Add(argument);
            }
        }

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        return info;
    }

    public static async Task<ScriptResult> RunAsync(string path, string argument, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || StartInfo(path, argument, OperatingSystem.IsWindows()) is not { } info)
        {
            return new ScriptResult(false, -1, string.Empty);
        }

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var gate = new object();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                if (Encoding.UTF8.GetByteCount(output.ToString()) < MaxOutputBytes)
                {
                    output.AppendLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try
        {
            if (!process.Start())
            {
                return new ScriptResult(false, -1, string.Empty);
            }

            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return new ScriptResult(true, -1, Trimmed(output, gate));
            }

            return new ScriptResult(true, process.ExitCode, Trimmed(output, gate));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn("commandBar", "A saved script could not be started.", ex);
            return new ScriptResult(false, -1, string.Empty);
        }
    }

    private static string Trimmed(StringBuilder output, object gate)
    {
        lock (gate)
        {
            var text = output.ToString();
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > MaxOutputBytes)
            {
                text = Encoding.UTF8.GetString(bytes, 0, MaxOutputBytes);
            }

            return text.Trim();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
