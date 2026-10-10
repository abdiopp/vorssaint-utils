// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests.Processes;

public class ProcessRulesTests
{
    private const long T0 = 133_700_000_000_000_000;

    [Theory]
    [InlineData(0, "System Idle Process", false, true)]
    [InlineData(4, "System", false, true)]
    [InlineData(612, "csrss.exe", false, true)]
    [InlineData(700, "LSASS.EXE", false, true)]
    [InlineData(1200, "dwm.exe", false, true)]
    [InlineData(2400, "svchost.exe", true, true)] // critical: killing it stops Windows
    [InlineData(5000, "explorer.exe", false, false)]
    [InlineData(6000, "chrome.exe", false, false)]
    public void Core_and_critical_processes_are_protected(int pid, string name, bool critical, bool expected) =>
        Assert.Equal(expected, ProcessProtection.IsProtected(pid, name, ownPid: 9999, identityKnown: true, isCritical: critical));

    [Fact]
    public void Own_process_and_unknown_identities_are_protected()
    {
        Assert.True(ProcessProtection.IsProtected(9999, "Rivet.exe", ownPid: 9999, identityKnown: true, isCritical: false));
        Assert.True(ProcessProtection.IsProtected(1234, "Rivet.exe", ownPid: 9999, identityKnown: true, isCritical: false, ownName: "Rivet.exe"));
        Assert.True(ProcessProtection.IsProtected(1234, "app.exe", ownPid: 9999, identityKnown: false, isCritical: false));
    }

    [Fact]
    public void Tree_walk_is_deepest_first_and_ignores_reused_parent_ids()
    {
        var root = P(100, 1, T0);
        var child = P(200, 100, T0 + 10);
        var grandchild = P(300, 200, T0 + 20);
        var sibling = P(201, 100, T0 + 15);
        // PID 400 claims parent 100 but started before it: a leftover link to a reused PID.
        var impostor = P(400, 100, T0 - 50);
        var snapshot = new[] { root, child, grandchild, sibling, impostor };

        var descendants = ProcessTree.DescendantsDeepestFirst(root, snapshot);
        Assert.Equal([300, 201, 200], descendants.Select(p => p.Pid));
    }

    [Fact]
    public void Tree_walk_survives_cycles()
    {
        var a = P(10, 20, T0);
        var b = P(20, 10, T0);
        Assert.Single(ProcessTree.DescendantsDeepestFirst(a, [a, b]));
    }

    [Fact]
    public void Groups_fold_helpers_under_the_highest_ancestor_with_the_same_executable()
    {
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var explorer = P(50, 1, T0 - 100);
        var browser = P(100, 50, T0);
        var renderer = P(110, 100, T0 + 1);
        var gpu = P(120, 100, T0 + 2);
        var crashpad = P(130, 100, T0 + 3);
        var paths = new Dictionary<int, string?>
        {
            [50] = @"C:\Windows\explorer.exe",
            [100] = chrome,
            [110] = chrome,
            [120] = chrome,
            [130] = @"C:\Program Files\Google\Chrome\Application\crashpad_handler.exe",
        };
        var owners = ProcessTree.GroupOwners([explorer, browser, renderer, gpu, crashpad], p => paths[p.Pid]);
        Assert.Equal(100, owners[110]);
        Assert.Equal(100, owners[120]);
        Assert.Equal(130, owners[130]);
        Assert.Equal(100, owners[100]);
        Assert.Equal(50, owners[50]);
    }

    [Fact]
    public void Cpu_share_needs_two_samples_and_is_relative_to_all_processors()
    {
        var sampler = new CpuSampler();
        var first = sampler.Sample([P(10, 1, T0) with { CpuTime = 1_000_000 }], timestampTicks: 10_000_000, processors: 4);
        Assert.Empty(first);
        // 1 s of wall time on 4 processors, 2 s of CPU time used → 50 %.
        var second = sampler.Sample([P(10, 1, T0) with { CpuTime = 21_000_000 }], timestampTicks: 20_000_000, processors: 4);
        Assert.Equal(50, second[new ProcessIdentity(10, T0)], 3);
    }

    [Fact]
    public void Header_clicks_flip_or_switch_to_the_natural_direction()
    {
        Assert.Equal((ProcessSortColumn.Cpu, true), ProcessListRules.Click(ProcessSortColumn.Cpu, false, ProcessSortColumn.Cpu));
        Assert.Equal((ProcessSortColumn.Name, true), ProcessListRules.Click(ProcessSortColumn.Cpu, false, ProcessSortColumn.Name));
        Assert.Equal((ProcessSortColumn.Memory, false), ProcessListRules.Click(ProcessSortColumn.Name, true, ProcessSortColumn.Memory));
        Assert.Equal(ProcessSortColumn.Cpu, ProcessListRules.ParseSort("bogus"));
        Assert.Equal("pid", ProcessListRules.SortKey(ProcessSortColumn.Pid));
    }

    [Fact]
    public void Filter_matches_name_or_exact_pid()
    {
        var row = new ProcessRow { Identity = new ProcessIdentity(4321, T0), Name = "Visual Studio Code", ImageName = "Code.exe" };
        Assert.True(ProcessListRules.Matches(row, "studio"));
        Assert.True(ProcessListRules.Matches(row, "code.EXE"));
        Assert.True(ProcessListRules.Matches(row, "4321"));
        Assert.False(ProcessListRules.Matches(row, "432"));
        Assert.True(ProcessListRules.Matches(row, ""));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --profile \"work\"", "--profile \"work\"")]
    [InlineData("C:\\Windows\\notepad.exe C:\\notes.txt", "C:\\notes.txt")]
    [InlineData("app.exe", "")]
    [InlineData("  \"C:\\x y\\z.exe\"", "")]
    public void Restart_keeps_the_original_arguments(string commandLine, string expected) =>
        Assert.Equal(expected, ProcessService.ArgumentsOf(commandLine));

    [Fact]
    public async Task Service_groups_sorts_and_kills_with_identity_checks()
    {
        var platform = new FakePlatform();
        var settings = SettingsStore.InMemory();
        var service = new ProcessService(platform, settings);
        await service.RefreshAsync(force: true);
        Assert.Equal(5, service.ProcessCount);
        var browser = service.Rows.Single(r => r.ImageName == "chrome.exe");
        Assert.Equal(3, browser.MemberCount);
        Assert.Equal(600L * 1024 * 1024, browser.MemoryBytes);
        Assert.True(service.Rows.Single(r => r.ImageName == "csrss.exe").IsProtected);

        settings.Set(ProcessSettings.GroupRelated, false);
        Assert.Equal(5, service.Rows.Count);

        // Kill on a windowed app posts WM_CLOSE; force kill terminates.
        var report = await service.KillAsync(service.Processes.Single(p => p.Pid == 100), KillMode.Kill);
        Assert.Equal(1, report.Removed);
        Assert.Contains(100, platform.Closed);

        var denied = await service.KillAsync(service.Processes.Single(p => p.Pid == 700), KillMode.ForceKill);
        Assert.Single(denied.NeedsAdministrator);
        Assert.False(denied.Succeeded);

        var protectedReport = await service.KillAsync(service.Processes.Single(p => p.ImageName == "csrss.exe"), KillMode.ForceKill);
        Assert.Equal(1, protectedReport.Failed);
        Assert.DoesNotContain(612, platform.Terminated);
    }

    [Fact]
    public async Task Kill_tree_terminates_children_before_the_root()
    {
        var platform = new FakePlatform();
        var service = new ProcessService(platform, SettingsStore.InMemory());
        await service.RefreshAsync(force: true);
        var root = service.Processes.Single(p => p.Pid == 100);
        var report = await service.KillAsync(root, KillMode.KillTree);
        Assert.Equal([120, 110, 100], platform.Terminated);
        Assert.Equal(3, report.Removed);
    }

    private static RawProcess P(int pid, int parent, long created) =>
        new() { Pid = pid, ParentPid = parent, ImageName = $"p{pid}.exe", CreationTime = created };

    private sealed class FakePlatform : IProcessPlatform
    {
        private readonly RawProcess[] _processes =
        [
            new() { Pid = 612, ParentPid = 500, ImageName = "csrss.exe", CreationTime = T0 - 1000, WorkingSetBytes = 5L << 20 },
            new() { Pid = 100, ParentPid = 50, ImageName = "chrome.exe", CreationTime = T0, WorkingSetBytes = 300L << 20 },
            new() { Pid = 110, ParentPid = 100, ImageName = "chrome.exe", CreationTime = T0 + 1, WorkingSetBytes = 200L << 20 },
            new() { Pid = 120, ParentPid = 110, ImageName = "chrome.exe", CreationTime = T0 + 2, WorkingSetBytes = 100L << 20 },
            new() { Pid = 700, ParentPid = 1, ImageName = "elevated.exe", CreationTime = T0 + 5, WorkingSetBytes = 10L << 20 },
        ];

        public List<int> Closed { get; } = [];

        public List<int> Terminated { get; } = [];

        public int CurrentProcessId => 9999;

        public int LogicalProcessorCount => 8;

        public IReadOnlyList<RawProcess> Snapshot() => _processes;

        public string? ImagePath(ProcessIdentity identity) => identity.Pid switch
        {
            100 or 110 or 120 => @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            700 => @"C:\Tools\elevated.exe",
            _ => null,
        };

        public ExecutableDescription? Describe(string path) =>
            path.EndsWith("chrome.exe", StringComparison.Ordinal) ? new ExecutableDescription("Google Chrome", "Google LLC", "Google Chrome") : null;

        public string? UserName(ProcessIdentity identity) => identity.Pid == 612 ? null : @"PC\alex";

        public bool IsCritical(ProcessIdentity identity) => identity.Pid == 612;

        public IReadOnlySet<int> ProcessesWithWindows() => new HashSet<int> { 100 };

        public KillOutcome CloseWindows(ProcessIdentity identity)
        {
            Closed.Add(identity.Pid);
            return KillOutcome.Done;
        }

        public KillOutcome Terminate(ProcessIdentity identity)
        {
            if (identity.Pid == 700)
            {
                return KillOutcome.AccessDenied;
            }

            Terminated.Add(identity.Pid);
            return KillOutcome.Done;
        }

        public Task<bool> WaitForExitAsync(ProcessIdentity identity, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);

        public string? CommandLine(ProcessIdentity identity) => null;

        public bool Launch(string path, string? arguments, string? workingDirectory) => true;

    }
}
