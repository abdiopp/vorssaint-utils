// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Rivet.Core.Agents;

public enum AgentProcessState
{
    Alive,
    Dead,
    Unknown,
}

/// <summary>What the agent-usage reader needs from the OS.</summary>
public interface IAgentUsagePlatform
{
    string HomeDirectory { get; }

    /// <summary>%APPDATA%.</summary>
    string RoamingAppData { get; }

    /// <summary>%LOCALAPPDATA%.</summary>
    string LocalAppData { get; }

    string? GetEnvironmentVariable(string name);

    /// <summary>A stable id of a file (NTFS file id with the volume serial, or inode); 0 when unknown.</summary>
    ulong FileIdentity(string path);

    /// <summary>
    /// Whether a process runs. When <paramref name="startedBy"/> is given, a
    /// process created after it is a reused pid and counts as dead.
    /// </summary>
    AgentProcessState ProcessState(int pid, DateTimeOffset? startedBy = null);

    bool IsNetworkAvailable { get; }

    /// <summary>Time the machine has been awake (sleep excluded), for the offline grace.</summary>
    TimeSpan AwakeTime { get; }

    /// <summary>The <c>pidDomain</c> Claude Code writes in its session registry on this OS.</summary>
    string PidDomain { get; }
}

/// <summary>A portable implementation (creation time as file identity, Process for liveness).</summary>
public class PortableAgentUsagePlatform : IAgentUsagePlatform
{
    /// <summary>Clock slack when comparing a process start with its registry record.</summary>
    public static readonly TimeSpan PidReuseTolerance = TimeSpan.FromMinutes(1);

    public virtual string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public virtual string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public virtual string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public virtual string PidDomain => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";

    public virtual bool IsNetworkAvailable
    {
        get
        {
            try
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
            catch (NetworkInformationException)
            {
                return true;
            }
        }
    }

    public virtual TimeSpan AwakeTime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public virtual string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public virtual ulong FileIdentity(string path)
    {
        try
        {
            return (ulong)File.GetCreationTimeUtc(path).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public virtual AgentProcessState ProcessState(int pid, DateTimeOffset? startedBy = null)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return AgentProcessState.Dead;
            }

            return startedBy is { } limit && process.StartTime.ToUniversalTime() > limit.UtcDateTime + PidReuseTolerance
                ? AgentProcessState.Dead
                : AgentProcessState.Alive;
        }
        catch (ArgumentException)
        {
            return AgentProcessState.Dead;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return AgentProcessState.Unknown;
        }
    }
}

/// <summary>A discovered log or database.</summary>
public sealed record AgentLogFile(AgentProvider Provider, string Path, DateTime ModifiedUtc);

/// <summary>
/// Where each agent keeps its data on Windows (spec 07 §3.8.13). Every root
/// can be overridden in Settings; environment overrides are honored
/// (<c>CLAUDE_CONFIG_DIR</c>, <c>CODEX_HOME</c>, <c>XDG_DATA_HOME</c>,
/// <c>COPILOT_HOME</c>). An override names the agent's data folder: the one
/// holding <c>projects</c> (Claude), <c>sessions</c> (Codex),
/// <c>opencode.db</c> (OpenCode) or <c>session-state</c> (Copilot).
/// </summary>
public sealed class AgentUsagePaths(IAgentUsagePlatform platform, Func<AgentProvider, string?>? overrides = null)
{
    private string? Override(AgentProvider provider) => overrides?.Invoke(provider) is { Length: > 0 } value ? value : null;

    private string Home => platform.HomeDirectory;

    /// <summary>The Claude Code config folder (~/.claude or CLAUDE_CONFIG_DIR).</summary>
    public string ClaudeDirectory =>
        Override(AgentProvider.Claude) ?? (platform.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : System.IO.Path.Combine(Home, ".claude"));

    public IReadOnlyList<string> ClaudeProjectRoots
    {
        get
        {
            var roots = new List<string> { System.IO.Path.Combine(ClaudeDirectory, "projects") };
            if (Override(AgentProvider.Claude) is null)
            {
                roots.Add(System.IO.Path.Combine(Home, ".config", "claude", "projects"));
            }

            return roots;
        }
    }

    public IReadOnlyList<string> ClaudeSessionDirectories
    {
        get
        {
            var dirs = new List<string> { System.IO.Path.Combine(ClaudeDirectory, "sessions") };
            if (Override(AgentProvider.Claude) is null)
            {
                dirs.Add(System.IO.Path.Combine(Home, ".config", "claude", "sessions"));
            }

            return dirs;
        }
    }

    /// <summary>~/.claude.json, or .claude.json inside the configured folder.</summary>
    public string ClaudeConfigFile =>
        Override(AgentProvider.Claude) is { } dir ? System.IO.Path.Combine(dir, ".claude.json")
        : platform.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } env ? System.IO.Path.Combine(env, ".claude.json")
        : System.IO.Path.Combine(Home, ".claude.json");

    /// <summary>Candidates for the Claude desktop app's plan history (classic install, then MSIX packages).</summary>
    public IReadOnlyList<string> ClaudeAppHistoryCandidates
    {
        get
        {
            var list = new List<string> { System.IO.Path.Combine(platform.RoamingAppData, "Claude", "plan-usage-history.json") };
            var packages = System.IO.Path.Combine(platform.LocalAppData, "Packages");
            try
            {
                if (Directory.Exists(packages))
                {
                    foreach (var package in Directory.EnumerateDirectories(packages, "*Claude*"))
                    {
                        list.Add(System.IO.Path.Combine(package, "LocalCache", "Roaming", "Claude", "plan-usage-history.json"));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return list;
        }
    }

    /// <summary>Folders whose presence means the Claude desktop app is installed.</summary>
    public IReadOnlyList<string> ClaudeAppFolders => [System.IO.Path.Combine(platform.RoamingAppData, "Claude"), System.IO.Path.Combine(platform.LocalAppData, "AnthropicClaude")];

    public string CodexHome =>
        Override(AgentProvider.Codex) ?? (platform.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } env ? env : System.IO.Path.Combine(Home, ".codex"));

    public IReadOnlyList<string> CodexRoots => [System.IO.Path.Combine(CodexHome, "sessions"), System.IO.Path.Combine(CodexHome, "archived_sessions")];

    public IReadOnlyList<string> OpenCodeDatabases
    {
        get
        {
            if (Override(AgentProvider.OpenCode) is { } dir)
            {
                return [System.IO.Path.Combine(dir, "opencode.db")];
            }

            var list = new List<string>();
            if (platform.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg)
            {
                list.Add(System.IO.Path.Combine(xdg, "opencode", "opencode.db"));
            }

            list.Add(System.IO.Path.Combine(Home, ".local", "share", "opencode", "opencode.db"));
            list.Add(System.IO.Path.Combine(platform.LocalAppData, "opencode", "opencode.db"));
            return list;
        }
    }

    public string CopilotRoot =>
        System.IO.Path.Combine(Override(AgentProvider.Copilot) ?? (platform.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } env ? env : System.IO.Path.Combine(Home, ".copilot")), "session-state");

    /// <summary>The roots shown in Settings and watched for changes.</summary>
    public IReadOnlyList<string> Roots(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ClaudeProjectRoots,
        AgentProvider.Codex => CodexRoots,
        AgentProvider.OpenCode => OpenCodeDatabases,
        _ => [CopilotRoot],
    };

    public bool IsFound(AgentProvider provider) => provider == AgentProvider.OpenCode
        ? OpenCodeDatabases.Any(File.Exists)
        : Roots(provider).Any(Directory.Exists);

    /// <summary>
    /// Logs modified within the horizon: Claude and Codex *.jsonl anywhere
    /// under their roots, Copilot only &lt;root&gt;/&lt;session&gt;/events.jsonl, OpenCode
    /// the database (its time is the newer of the db and its -wal file). Main
    /// logs come first, then Claude subagent logs, each by modification time.
    /// </summary>
    public List<AgentLogFile> Discover(IReadOnlySet<AgentProvider> providers, DateTimeOffset now)
    {
        var cutoff = (now - AgentUsageConstants.Horizon).UtcDateTime;
        var main = new List<AgentLogFile>();
        var subagents = new List<AgentLogFile>();
        void Consider(AgentProvider provider, string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    return;
                }

                var modified = info.LastWriteTimeUtc;
                if (provider == AgentProvider.OpenCode && File.Exists(path + "-wal"))
                {
                    var wal = File.GetLastWriteTimeUtc(path + "-wal");
                    if (wal > modified) modified = wal;
                }

                if (modified < cutoff)
                {
                    return;
                }

                var file = new AgentLogFile(provider, info.FullName, modified);
                if (provider == AgentProvider.Claude && IsClaudeSubagent(info.FullName))
                {
                    subagents.Add(file);
                }
                else
                {
                    main.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        foreach (var provider in providers)
        {
            switch (provider)
            {
                case AgentProvider.Claude:
                case AgentProvider.Codex:
                    foreach (var root in Roots(provider).Where(Directory.Exists))
                    {
                        foreach (var file in SafeEnumerate(root, "*.jsonl", SearchOption.AllDirectories))
                        {
                            Consider(provider, file);
                        }
                    }

                    break;
                case AgentProvider.Copilot:
                    if (Directory.Exists(CopilotRoot))
                    {
                        foreach (var session in SafeDirectories(CopilotRoot))
                        {
                            if (!System.IO.Path.GetFileName(session).StartsWith('.'))
                            {
                                Consider(provider, System.IO.Path.Combine(session, "events.jsonl"));
                            }
                        }
                    }

                    break;
                case AgentProvider.OpenCode:
                    foreach (var db in OpenCodeDatabases.Where(File.Exists).Take(1))
                    {
                        Consider(provider, db);
                    }

                    break;
            }
        }

        main.Sort((a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc));
        subagents.Sort((a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc));
        return [.. main, .. subagents];
    }

    public static bool IsClaudeSubagent(string path) =>
        path.Replace('\\', '/').Contains("/subagents/", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Claude subagent log's turn belongs to its parent transcript (&lt;prefix&gt;.jsonl).</summary>
    public static string ClaudeParentLog(string subagentPath)
    {
        var normalized = subagentPath.Replace('\\', '/');
        var at = normalized.IndexOf("/subagents/", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? subagentPath : subagentPath[..at] + ".jsonl";
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern, SearchOption option)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
