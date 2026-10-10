// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Modules;
using Rivet.Platform.Fake.PackageManager;

namespace Rivet.Platform.Fake.Processes;

/// <summary>A believable Windows process list that changes a little on every snapshot.</summary>
public sealed class FakeProcessPlatform : IProcessPlatform
{
    private static readonly long Boot = DateTime.UtcNow.AddHours(-6).ToFileTimeUtc();
    private readonly object _gate = new();
    private readonly List<Sample> _processes;
    private readonly Random _random = new(7);

    public FakeProcessPlatform()
    {
        const string sys = @"C:\Windows\System32\";
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        const string code = @"C:\Users\Alex\AppData\Local\Programs\Microsoft VS Code\Code.exe";
        _processes =
        [
            new(0, 0, "System Idle Process", null, null, 0, 8, "SYSTEM"),
            new(4, 0, "System", null, null, 1, 140, "SYSTEM"),
            new(108, 4, "Registry", null, null, 2, 60, "SYSTEM"),
            new(512, 4, "smss.exe", sys + "smss.exe", null, 3, 1, "SYSTEM"),
            new(640, 600, "csrss.exe", sys + "csrss.exe", null, 4, 6, "SYSTEM"),
            new(720, 600, "wininit.exe", sys + "wininit.exe", null, 5, 7, "SYSTEM"),
            new(860, 720, "services.exe", sys + "services.exe", null, 6, 11, "SYSTEM"),
            new(884, 720, "lsass.exe", sys + "lsass.exe", null, 7, 24, "SYSTEM"),
            new(1020, 860, "svchost.exe", sys + "svchost.exe", "Host Process for Windows Services", 8, 38, "SYSTEM"),
            new(1240, 860, "svchost.exe", sys + "svchost.exe", "Host Process for Windows Services", 9, 22, @"NT AUTHORITY\LOCAL SERVICE"),
            new(1496, 1020, "dwm.exe", sys + "dwm.exe", "Desktop Window Manager", 10, 180, "DWM-1"),
            new(5120, 4980, "explorer.exe", @"C:\Windows\explorer.exe", "Windows Explorer", 20, 210, @"PC\Alex", true),
            new(6200, 5120, "chrome.exe", chrome, "Google Chrome", 30, 420, @"PC\Alex", true),
            new(6240, 6200, "chrome.exe", chrome, "Google Chrome", 31, 260, @"PC\Alex"),
            new(6288, 6200, "chrome.exe", chrome, "Google Chrome", 32, 190, @"PC\Alex"),
            new(6310, 6200, "chrome.exe", chrome, "Google Chrome", 33, 95, @"PC\Alex"),
            new(6400, 6200, "chrome.exe", chrome, "Google Chrome", 34, 60, @"PC\Alex"),
            new(7020, 5120, "Code.exe", code, "Visual Studio Code", 40, 380, @"PC\Alex", true),
            new(7064, 7020, "Code.exe", code, "Visual Studio Code", 41, 210, @"PC\Alex"),
            new(7112, 7020, "Code.exe", code, "Visual Studio Code", 42, 140, @"PC\Alex"),
            new(7400, 7020, "node.exe", @"C:\Program Files\nodejs\node.exe", "Node.js JavaScript Runtime", 43, 120, @"PC\Alex"),
            new(8120, 5120, "Spotify.exe", @"C:\Users\Alex\AppData\Roaming\Spotify\Spotify.exe", "Spotify", 50, 310, @"PC\Alex", true),
            new(8600, 5120, "WindowsTerminal.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", "Windows Terminal", 60, 85, @"PC\Alex", true),
            new(8644, 8600, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell 7", 61, 70, @"PC\Alex"),
            new(9010, 860, "postgres.exe", @"C:\Program Files\PostgreSQL\16\bin\postgres.exe", "PostgreSQL Server", 70, 45, @"NT AUTHORITY\NETWORK SERVICE"),
            new(9100, 5120, "OneDrive.exe", @"C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe", "Microsoft OneDrive", 80, 66, @"PC\Alex"),
            new(9300, 5120, "Teams.exe", @"C:\Users\Alex\AppData\Local\Microsoft\Teams\current\Teams.exe", "Microsoft Teams", 90, 290, @"PC\Alex", true),
            new(9800, 860, "Admin Tool.exe", @"C:\Program Files\Contoso\AdminTool\Admin Tool.exe", "Contoso Admin Tool", 95, 40, @"PC\Admin"),
        ];
    }

    public int CurrentProcessId => 99999;

    public int LogicalProcessorCount => 8;

    public IReadOnlyList<RawProcess> Snapshot()
    {
        lock (_gate)
        {
            foreach (var process in _processes)
            {
                // Some processes keep working between refreshes.
                process.Cpu += process.Weight * _random.Next(0, 40_000);
            }

            return _processes.Select(p => new RawProcess
            {
                Pid = p.Pid,
                ParentPid = p.Parent,
                ImageName = p.Name,
                CreationTime = p.Pid == 0 ? 0 : Boot + (p.Order * 10_000_000L),
                CpuTime = p.Cpu,
                WorkingSetBytes = p.MemoryMb * 1024L * 1024,
                SessionId = p.User is "SYSTEM" ? 0 : 1,
            }).ToList();
        }
    }

    public string? ImagePath(ProcessIdentity identity) => Find(identity)?.Path;

    public ExecutableDescription? Describe(string path) =>
        _processes.FirstOrDefault(p => p.Path == path)?.Description is { } description ? new ExecutableDescription(description, null, description) : null;

    public string? UserName(ProcessIdentity identity) => Find(identity)?.User;

    public bool IsCritical(ProcessIdentity identity) => Find(identity)?.Name is "csrss.exe" or "smss.exe" or "wininit.exe" or "services.exe" or "lsass.exe";

    public IReadOnlySet<int> ProcessesWithWindows()
    {
        lock (_gate)
        {
            return _processes.Where(p => p.Windows).Select(p => p.Pid).ToHashSet();
        }
    }

    public KillOutcome CloseWindows(ProcessIdentity identity) => Remove(identity, needsWindows: true);

    public KillOutcome Terminate(ProcessIdentity identity) => Remove(identity, needsWindows: false);

    public Task<bool> WaitForExitAsync(ProcessIdentity identity, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(Find(identity) is null);

    public string? CommandLine(ProcessIdentity identity) => Find(identity) is { Path: { } path } ? $"\"{path}\"" : null;

    public bool Launch(string path, string? arguments, string? workingDirectory)
    {
        Log.Info("processes", $"[fake] launch {path} {arguments}");
        return true;
    }

    private Sample? Find(ProcessIdentity identity)
    {
        lock (_gate)
        {
            return _processes.FirstOrDefault(p => p.Pid == identity.Pid);
        }
    }

    private KillOutcome Remove(ProcessIdentity identity, bool needsWindows)
    {
        lock (_gate)
        {
            var process = _processes.FirstOrDefault(p => p.Pid == identity.Pid);
            if (process is null)
            {
                return KillOutcome.AlreadyGone;
            }

            if (process.User is "SYSTEM" or @"PC\Admin" or @"NT AUTHORITY\NETWORK SERVICE" or @"NT AUTHORITY\LOCAL SERVICE")
            {
                return KillOutcome.AccessDenied;
            }

            if (needsWindows && !process.Windows)
            {
                return KillOutcome.NoWindows;
            }

            _processes.Remove(process);
            Log.Info("processes", $"[fake] ended {process.Name} ({process.Pid})");
            return KillOutcome.Done;
        }
    }

    private sealed class Sample(int pid, int parent, string name, string? path, string? description, int order, int memoryMb, string user, bool windows = false)
    {
        public int Pid { get; } = pid;

        public int Parent { get; } = parent;

        public string Name { get; } = name;

        public string? Path { get; } = path;

        public string? Description { get; } = description;

        public int Order { get; } = order;

        public int MemoryMb { get; } = memoryMb;

        public string User { get; } = user;

        public bool Windows { get; } = windows;

        public long Cpu { get; set; } = order * 1_000_000L;

        public int Weight => Name switch
        {
            "chrome.exe" => 6,
            "Code.exe" => 4,
            "node.exe" => 8,
            "dwm.exe" => 2,
            "Teams.exe" => 3,
            _ => 1,
        };
    }
}

/// <summary>Listening sockets of the sample processes.</summary>
public sealed class FakePortTable : IPortTablePlatform
{
    public IReadOnlyList<PortEntry> ReadListeners(bool includeUdp)
    {
        var entries = new List<PortEntry>
        {
            new(PortProtocol.Tcp, "0.0.0.0", 135, 1020, false),
            new(PortProtocol.Tcp, "::", 445, 4, true),
            new(PortProtocol.Tcp, "127.0.0.1", 3000, 7400, false),
            new(PortProtocol.Tcp, "::1", 5173, 7400, true),
            new(PortProtocol.Tcp, "0.0.0.0", 5432, 9010, false),
            new(PortProtocol.Tcp, "::", 5432, 9010, true),
            new(PortProtocol.Tcp, "127.0.0.1", 9222, 6200, false),
            new(PortProtocol.Tcp, "127.0.0.1", 42050, 9100, false),
            new(PortProtocol.Tcp, "0.0.0.0", 7680, 1240, false),
        };
        if (includeUdp)
        {
            entries.Add(new PortEntry(PortProtocol.Udp, "0.0.0.0", 5353, 6200, false));
            entries.Add(new PortEntry(PortProtocol.Udp, "0.0.0.0", 5355, 1240, false));
            entries.Add(new PortEntry(PortProtocol.Udp, "::", 1900, 1240, true));
        }

        return entries;
    }
}

public sealed class FakeElevation : IElevationService
{
    public bool IsElevated => false;

    public bool RestartElevated()
    {
        Log.Info("elevation", "[fake] restart as administrator");
        return false;
    }
}

public sealed class FakeProcessesRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IProcessPlatform, FakeProcessPlatform>();
        services.AddSingleton<IPortTablePlatform, FakePortTable>();
        services.AddSingleton<IElevationService, FakeElevation>();
        services.AddSingleton<ICommandRunner, FakeWingetRunner>();
    }
}
