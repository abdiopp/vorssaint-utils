// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Rivet.Core.SystemMonitor;

namespace Rivet.SensorHelper;

/// <summary>
/// Installing and removing the helper, run elevated.
/// <para>
/// Install copies the helper and the files it needs from the app's folder to
/// Program Files and registers an on-demand scheduled task that runs that copy
/// as SYSTEM. The task never points into the app's own folder: a standard user
/// can write there, and whatever the task runs gets system rights. The task's
/// access list lets the one user start it, nothing more.
/// </para>
/// </summary>
internal static class HelperSetup
{
    private static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(30);

    public static int Install(SecurityIdentifier user)
    {
        if (HelperIdentity.IsInstalledCopy)
        {
            // The installed copy cannot replace itself; the app starts the one in its own folder.
            return ExitCodes.BadArguments;
        }

        var target = HelperIdentity.InstallFolder;
        var task = SensorHelperProtocol.TaskName(HelperIdentity.ProductId);
        Schtasks("/End", "/TN", task);

        try
        {
            ResetFolder(target);
            CopyClosure(AppContext.BaseDirectory, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            HelperLog.Write($"Copying the helper failed: {ex}");
            return ExitCodes.CopyFailed;
        }

        var definition = Path.Combine(target, "task.xml");
        try
        {
            File.WriteAllText(definition, TaskXml(Path.Combine(target, HelperIdentity.ExecutableName), user), Encoding.Unicode);
            var (code, output) = Schtasks("/Create", "/TN", task, "/XML", definition, "/F");
            if (code != 0)
            {
                HelperLog.Write($"Registering the task failed ({code}): {output}");
                return ExitCodes.TaskFailed;
            }
        }
        finally
        {
            File.Delete(definition);
        }

        HelperLog.Write($"Installed for {user.Value}.");
        return ExitCodes.Done;
    }

    public static int Uninstall()
    {
        var task = SensorHelperProtocol.TaskName(HelperIdentity.ProductId);
        Schtasks("/End", "/TN", task);
        Schtasks("/Delete", "/TN", task, "/F");
        if (HelperIdentity.IsInstalledCopy)
        {
            // Our own files are in use; the copy in the app's folder removes them.
            return ExitCodes.Done;
        }

        var target = HelperIdentity.InstallFolder;
        try
        {
            if (Directory.Exists(target))
            {
                DeleteFolder(target);
            }

            // The product folder under Program Files exists only for the helper.
            var parent = Path.GetDirectoryName(target);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExitCodes.Failed;
        }

        return ExitCodes.Done;
    }

    private static void ResetFolder(string target)
    {
        if (Directory.Exists(target))
        {
            DeleteFolder(target);
        }

        // Created under Program Files, so it inherits "administrators write, users read".
        Directory.CreateDirectory(target);
    }

    /// <summary>Deletes the folder; a helper that was just told to stop may hold its files for a moment.</summary>
    private static void DeleteFolder(string folder)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>
    /// Copies the helper and what its dependency manifest lists (its
    /// libraries and, in a self-contained build, the .NET runtime) and
    /// nothing else from the app's folder.
    /// </summary>
    private static void CopyClosure(string source, string target)
    {
        var name = Path.GetFileNameWithoutExtension(HelperIdentity.ExecutableName);
        var required = new[] { $"{name}.exe", $"{name}.dll", $"{name}.runtimeconfig.json", $"{name}.deps.json" };
        var files = new HashSet<string>(required, StringComparer.OrdinalIgnoreCase);

        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, $"{name}.deps.json"))))
        {
            foreach (var targetFramework in manifest.RootElement.GetProperty("targets").EnumerateObject())
            {
                foreach (var library in targetFramework.Value.EnumerateObject())
                {
                    foreach (var kind in (string[])["runtime", "native", "runtimeTargets"])
                    {
                        if (library.Value.ValueKind != JsonValueKind.Object
                            || !library.Value.TryGetProperty(kind, out var assets)
                            || assets.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var asset in assets.EnumerateObject())
                        {
                            var relative = asset.Name.Replace('/', Path.DirectorySeparatorChar);

                            // A build for one runtime lays every asset flat next to the exe;
                            // a portable build keeps "runtimes\…" paths.
                            files.Add(Path.GetFileName(relative));
                            files.Add(relative);
                        }
                    }
                }
            }
        }

        // The manifest only names files; never let a path in it leave either folder.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) + Path.DirectorySeparatorChar;
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)) + Path.DirectorySeparatorChar;
        foreach (var relative in files)
        {
            var from = Path.GetFullPath(Path.Combine(root, relative));
            var to = Path.GetFullPath(Path.Combine(destination, relative));
            if (!from.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !to.StartsWith(destination, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(from))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
        }

        foreach (var file in required)
        {
            if (!File.Exists(Path.Combine(target, file)))
            {
                throw new InvalidOperationException($"{file} is missing from {source}.");
            }
        }
    }

    private static string TaskXml(string executable, SecurityIdentifier user)
    {
        // SYSTEM and administrators manage the task; the user may read and start it.
        var access = $"D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{user.Value})";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{SecurityElement.Escape($"Reads the processor's temperature sensors for {HelperIdentity.DisplayName}.")}</Description>
                <SecurityDescriptor>{access}</SecurityDescriptor>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(executable)}</Command>
                  <Arguments>--serve --user {user.Value}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int Code, string Output) Schtasks(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return (-1, "schtasks did not start.");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(SchtasksTimeout))
        {
            process.Kill();
            return (-1, "schtasks timed out.");
        }

        return (process.ExitCode, (output.Result + error.Result).Trim());
    }
}
