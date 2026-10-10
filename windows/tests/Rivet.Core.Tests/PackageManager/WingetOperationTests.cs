// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests.PackageManager;

public class WingetOperationTests
{
    [Theory]
    [InlineData("Downloading https://github.com/git-for-windows/git/releases/download/v2.46.0.windows.1/Git-2.46.0-64-bit.exe", PackageOperationKind.Install, OperationPhase.Downloading)]
    [InlineData("Successfully verified installer hash", PackageOperationKind.Install, OperationPhase.Installing)]
    [InlineData("Starting package install...", PackageOperationKind.Upgrade, OperationPhase.Updating)]
    [InlineData("Starting package uninstall...", PackageOperationKind.Uninstall, OperationPhase.Removing)]
    [InlineData("Successfully installed", PackageOperationKind.Install, OperationPhase.Finishing)]
    [InlineData("Updating source: winget...", PackageOperationKind.UpdateSources, OperationPhase.Refreshing)]
    public void Phases_follow_winget_messages(string line, PackageOperationKind kind, OperationPhase expected) =>
        Assert.Equal(expected, WingetProgress.PhaseOf(line, kind));

    [Fact]
    public void Unknown_lines_keep_the_phase() =>
        Assert.Null(WingetProgress.PhaseOf("This application is licensed to you by its owner.", PackageOperationKind.Install));

    [Theory]
    [InlineData("  ██████████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  29.0 MB / 62.4 MB", 0.4647)]
    [InlineData("  ██████████████████████████████  100%", 1.0)]
    [InlineData("  ████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  27%", 0.27)]
    [InlineData("  512 KB / 1.00 MB", 0.5)]
    public void Progress_reads_percentages_and_byte_counters(string line, double expected) =>
        Assert.Equal(expected, WingetProgress.ProgressOf(line)!.Value, 3);

    [Fact]
    public void Lines_without_progress_report_none() =>
        Assert.Null(WingetProgress.ProgressOf("Found Git [Git.Git] Version 2.46.0"));

    [Fact]
    public void Activity_skips_progress_frames()
    {
        Assert.Null(WingetProgress.ActivityOf("  ██████████████▒▒▒▒▒▒  29.0 MB / 62.4 MB"));
        Assert.Null(WingetProgress.ActivityOf("   \\ "));
        Assert.Equal("Successfully installed", WingetProgress.ActivityOf("  Successfully installed  "));
        Assert.Equal("Fetching", WingetProgress.ActivityOf("==> Fetching"));
    }

    [Theory]
    [InlineData(42, "42s")]
    [InlineData(61, "1min")]
    [InlineData(3599, "59min")]
    [InlineData(3720, "1h 2min")]
    public void Elapsed_uses_the_macos_format(int seconds, string expected) =>
        Assert.Equal(expected, WingetProgress.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Common_exit_codes_have_plain_messages()
    {
        Assert.Equal("win.packageManager.errorNoUpdate", WingetErrors.MessageKey(0x8A15002B));
        Assert.Equal("win.packageManager.errorAdmin", WingetErrors.MessageKey(0x8A150019));
        Assert.Equal("win.packageManager.errorPolicy", WingetErrors.MessageKey(0x8A15003A));
        Assert.Equal("win.packageManager.errorInUse", WingetErrors.MessageKey(0x8A150101));
        Assert.Null(WingetErrors.MessageKey(0x80004005));
        Assert.True(WingetErrors.IsSuccessWithRestart(0x8A150109));
    }

    [Fact]
    public async Task Operation_requests_are_argv_arrays_with_agreements_and_no_shell()
    {
        var runner = new ScriptedRunner();
        var client = NewClient(runner);
        await client.DetectAsync();

        var install = client.OperationRequest(PackageOperationKind.Install, "Git.Git", "winget")!;
        Assert.Equal(["install", "--id", "Git.Git", "--exact", "--silent", "--accept-package-agreements", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"], install.Arguments);
        Assert.Equal(WingetClient.OperationSilence, install.InactivityTimeout);
        Assert.Null(install.Timeout);

        var uninstallLocal = client.OperationRequest(PackageOperationKind.Uninstall, "ARP\\Machine\\X64\\Contoso", null)!;
        Assert.DoesNotContain("--source", uninstallLocal.Arguments);
        Assert.DoesNotContain("--accept-package-agreements", uninstallLocal.Arguments);

        Assert.Null(client.OperationRequest(PackageOperationKind.Upgrade, "--all", null));
        Assert.Null(client.OperationRequest(PackageOperationKind.Upgrade, "Contoso.Studio…", null));
    }

    [Fact]
    public async Task Upgrade_listing_resolves_truncated_ids_through_export()
    {
        var runner = new ScriptedRunner
        {
            Handler = request =>
            {
                if (request.Arguments[0] == "upgrade")
                {
                    return Ok("""
                        Name               Id                       Version Available Source
                        --------------------------------------------------------------------
                        Contoso Studio     Contoso.StudioEnterpris… 1.0     2.0       winget
                        1 upgrades available.
                        """);
                }

                if (request.Arguments[0] == "export")
                {
                    var file = request.Arguments[request.Arguments.ToList().IndexOf("--output") + 1];
                    File.WriteAllText(file, """{"Sources":[{"Packages":[{"PackageIdentifier":"Contoso.StudioEnterpriseEdition"}],"SourceDetails":{"Name":"winget"}}]}""");
                    return Ok(string.Empty);
                }

                return Ok("v1.8.1911");
            },
        };
        var client = NewClient(runner);
        var result = await client.ListUpgradesAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        var package = Assert.Single(result.Value!);
        Assert.Equal("Contoso.StudioEnterpriseEdition", package.Id);
        Assert.Contains(runner.Calls, c => c.Arguments.SequenceEqual(["upgrade", "--include-unknown", "--accept-source-agreements", "--disable-interactivity"]));
    }

    [Fact]
    public async Task No_applicable_upgrade_is_an_empty_list_not_an_error()
    {
        var runner = new ScriptedRunner
        {
            Handler = request => request.Arguments[0] == "upgrade"
                ? new CommandResult(CommandOutcome.Exited, unchecked((int)0x8A15002B), "No applicable upgrade found.\r\n", string.Empty)
                : Ok("v1.9.25180"),
        };
        var result = await NewClient(runner).ListUpgradesAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Missing_winget_is_reported_without_running_anything()
    {
        var runner = new ScriptedRunner();
        var settings = SettingsStore.InMemory();
        var client = new WingetClient(runner, new FixedLocator(null), settings);
        Assert.Equal(WingetAvailability.Missing, await client.DetectAsync());
        var result = await client.ListInstalledAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Lane_runs_one_operation_at_a_time_and_tracks_progress()
    {
        var gate = new TaskCompletionSource();
        var runner = new ScriptedRunner
        {
            Streamer = async (request, onLine) =>
            {
                if (request.Arguments[0] == "--version")
                {
                    return Ok("v1.8.1911");
                }

                onLine?.Invoke("Found Git [Git.Git] Version 2.46.0");
                onLine?.Invoke("Downloading https://example.invalid/Git.exe");
                onLine?.Invoke("  ██████████▒▒▒▒▒▒▒▒▒▒  31.2 MB / 62.4 MB");
                await gate.Task;
                onLine?.Invoke("Successfully installed");
                return Ok(string.Empty);
            },
        };
        var client = NewClient(runner);
        var lane = new PackageOperationLane(client, runner);
        var first = lane.RunAsync(new PackageOperationRequest(PackageOperationKind.Install, "Git.Git", "Git", "winget"));
        await WaitUntil(() => lane.Current?.Phase == OperationPhase.Downloading && lane.Current.Progress is not null);
        Assert.True(lane.IsRunning);
        Assert.Equal(0.5, lane.Current!.Progress!.Value, 2);

        var second = await lane.RunAsync(new PackageOperationRequest(PackageOperationKind.Install, "Other.App", "Other"));
        Assert.Equal(OperationResult.Failed, second);

        gate.SetResult();
        Assert.Equal(OperationResult.Succeeded, await first);
        Assert.Equal(OperationResult.Succeeded, lane.Current!.Result);
        Assert.Contains("$ winget install --id Git.Git", lane.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lane_batches_report_failed_names_and_plain_errors()
    {
        var runner = new ScriptedRunner
        {
            Handler = request => request.Arguments[0] switch
            {
                "--version" => Ok("v1.8.1911"),
                _ when request.Arguments.Contains("Bad.App") => new CommandResult(CommandOutcome.Exited, unchecked((int)0x8A150101), "Installer failed\r\n", string.Empty),
                _ => Ok("Successfully installed"),
            },
        };
        var client = NewClient(runner);
        var lane = new PackageOperationLane(client, runner);
        var result = await lane.RunBatchAsync(
        [
            new PackageOperationRequest(PackageOperationKind.Upgrade, "Good.App", "Good"),
            new PackageOperationRequest(PackageOperationKind.Upgrade, "Bad.App", "Bad"),
        ]);
        Assert.Equal(OperationResult.Failed, result);
        Assert.Equal(["Bad"], lane.Current!.FailedNames);
        Assert.Equal(2, lane.Current.BatchCount);
        Assert.Contains("Bad", lane.Current.Message, StringComparison.Ordinal);
    }

    private static CommandResult Ok(string output) => new(CommandOutcome.Exited, 0, output, string.Empty);

    private static WingetClient NewClient(ScriptedRunner runner)
    {
        var settings = SettingsStore.InMemory();
        settings.Set(PackageManagerSettings.SourceAgreementsAccepted, true);
        return new WingetClient(runner, new FixedLocator("winget.exe"), settings);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FixedLocator(string? path) : IWingetLocator
    {
        public string? Locate() => path;
    }

    internal sealed class ScriptedRunner : ICommandRunner
    {
        public List<CommandRequest> Calls { get; } = [];

        public Func<CommandRequest, CommandResult>? Handler { get; init; }

        public Func<CommandRequest, Action<string>?, Task<CommandResult>>? Streamer { get; init; }

        public async Task<CommandResult> RunAsync(CommandRequest request, Action<string>? onLine = null, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add(request);
            }

            if (Streamer is not null)
            {
                return await Streamer(request, onLine);
            }

            return Handler?.Invoke(request) ?? (request.Arguments.Count > 0 && request.Arguments[0] == "--version"
                ? new CommandResult(CommandOutcome.Exited, 0, "v1.8.1911", string.Empty)
                : new CommandResult(CommandOutcome.Exited, 0, string.Empty, string.Empty));
        }
    }
}
