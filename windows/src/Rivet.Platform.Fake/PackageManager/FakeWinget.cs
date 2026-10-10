// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Modules;

namespace Rivet.Platform.Fake.PackageManager;

/// <summary>A pretend winget so the development build exercises the real parser and operation lane.</summary>
public sealed class FakeWingetLocator : IWingetLocator
{
    public const string Path = "winget";

    public string? Locate() => Path;
}

/// <summary>
/// Answers winget command lines with output shaped like the real CLI (padded
/// tables, "…" truncation, summary lines, streamed progress). Anything that
/// is not winget fails to start, as on a machine without that tool.
/// </summary>
public sealed class FakeWingetRunner : ICommandRunner
{
    private readonly object _gate = new();
    private readonly List<Package> _catalog =
    [
        new("Git.Git", "Git", "2.46.0", "2.47.0", "winget", "The Git Development Community", "Git for Windows focuses on offering a lightweight, native set of tools that bring the full feature set of the Git SCM to Windows.", "https://gitforwindows.org/", "GPL-2.0", true),
        new("Microsoft.VisualStudioCode", "Microsoft Visual Studio Code", "1.93.1", "1.94.2", "winget", "Microsoft Corporation", "Code editing. Redefined.", "https://code.visualstudio.com/", "MIT", true),
        new("Microsoft.Edge", "Microsoft Edge", "129.0.2792.65", "129.0.2792.79", "winget", "Microsoft Corporation", "The browser built for business.", "https://www.microsoft.com/edge", "Proprietary", true),
        new("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "14.36.32532.0", "14.40.33810.0", "winget", "Microsoft Corporation", "Runtime components of Visual C++ libraries.", "https://visualstudio.microsoft.com/", "Proprietary", true),
        new("Mozilla.Firefox", "Mozilla Firefox (x64 en-US)", "130.0", "131.0.2", "winget", "Mozilla", "Fast, private and free web browser.", "https://www.mozilla.org/firefox/", "MPL-2.0", true),
        new("7zip.7zip", "7-Zip 24.08 (x64)", "24.08", null, "winget", "Igor Pavlov", "Free and open source file archiver with a high compression ratio.", "https://www.7-zip.org/", "LGPL-2.1", true),
        new("Zoom.Zoom", "Zoom Workplace", "6.1.11", "6.2.3", "winget", "Zoom Video Communications, Inc.", "Video conferencing, web conferencing and webinars.", "https://zoom.us/", "Proprietary", true),
        new("Spotify.Spotify", "Spotify", "1.2.45.454", null, "winget", "Spotify AB", "Music for everyone.", "https://www.spotify.com/", "Proprietary", true),
        new("9N0DX20HK701", "Windows Terminal", "1.20.11781.0", "1.21.2361.0", "msstore", "Microsoft Corporation", "The new Windows Terminal.", "https://aka.ms/terminal", "MIT", true),
        new("9NBLGGH4NNS1", "App Installer", "1.24.25200.0", null, "msstore", "Microsoft Corporation", "Install apps and packages with winget.", "https://aka.ms/winget", "Proprietary", true),
        new("OpenJS.NodeJS.LTS", "Node.js LTS", "20.17.0", null, "winget", "OpenJS Foundation", "JavaScript runtime built on Chrome's V8 engine.", "https://nodejs.org/", "MIT", false),
        new("Python.Python.3.12", "Python 3.12", "3.12.7", null, "winget", "Python Software Foundation", "Python is a programming language that lets you work quickly.", "https://www.python.org/", "PSF", false),
        new("VideoLAN.VLC", "VLC media player", "3.0.21", null, "winget", "VideoLAN", "Free and open source cross-platform multimedia player.", "https://www.videolan.org/vlc/", "GPL-2.0", false),
        new("Obsidian.Obsidian", "Obsidian", "1.6.7", null, "winget", "Obsidian", "A second brain, for you, forever.", "https://obsidian.md/", "Proprietary", false),
        new("Microsoft.PowerToys", "PowerToys", "0.85.1", null, "winget", "Microsoft Corporation", "Windows system utilities to maximize productivity.", "https://github.com/microsoft/PowerToys", "MIT", false),
        new("VSCodium.VSCodium", "VSCodium", "1.94.2.24286", null, "winget", "VSCodium", "Free/Libre Open Source Software binaries of VS Code.", "https://vscodium.com/", "MIT", false),
        new("Microsoft.VisualStudioCode.Insiders", "Microsoft Visual Studio Code Insiders", "1.95.0", null, "winget", "Microsoft Corporation", "Insiders build of Visual Studio Code.", "https://code.visualstudio.com/insiders/", "MIT", false),
    ];

    private readonly List<(string Name, string Id, string Version)> _local =
    [
        ("Microsoft Edge Update", "Microsoft Edge Update", "1.3.195.19"),
        ("Windows Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe", "11.2405.2.0"),
        ("Contoso Helper", @"ARP\Machine\X64\{8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}", "3.1"),
    ];

    public async Task<CommandResult> RunAsync(CommandRequest request, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        if (request.FileName != FakeWingetLocator.Path || request.Arguments.Count == 0)
        {
            return CommandResult.NotStarted("Not available in the development build.");
        }

        var args = request.Arguments;
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        return args[0] switch
        {
            "--version" => Ok("v1.9.25200\r\n"),
            "list" => Ok(ListOutput()),
            "upgrade" when args.Contains("--id") || args.Contains("--all") => await OperateAsync(PackageOperationKind.Upgrade, args, onLine, cancellationToken).ConfigureAwait(false),
            "upgrade" => Ok(UpgradeOutput()),
            "search" => Ok(SearchOutput(Value(args, "--query") ?? string.Empty, Value(args, "--source"))),
            "show" => Show(Value(args, "--id") ?? string.Empty),
            "source" when args.Count > 1 && args[1] == "list" => Ok(Table(["Name", "Argument", "Explicit"], [["msstore", "https://storeedgefd.dsx.mp.microsoft.com/v9.0", "false"], ["winget", "https://cdn.winget.microsoft.com/cache", "false"]])),
            "source" => await StreamAsync(["Updating all sources...", "Updating source: msstore...", "Done", "Updating source: winget...", "  ██████████████████████████████  100%", "Done"], onLine, cancellationToken).ConfigureAwait(false),
            "install" => await OperateAsync(PackageOperationKind.Install, args, onLine, cancellationToken).ConfigureAwait(false),
            "uninstall" => await OperateAsync(PackageOperationKind.Uninstall, args, onLine, cancellationToken).ConfigureAwait(false),
            _ => new CommandResult(CommandOutcome.Exited, unchecked((int)WingetErrors.InvalidArguments), "Argument name was not recognized for the current command.\r\n", string.Empty),
        };
    }

    private static CommandResult Ok(string output) => new(CommandOutcome.Exited, 0, output, string.Empty);

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        var index = args.ToList().IndexOf(name);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private string ListOutput()
    {
        lock (_gate)
        {
            var rows = _catalog.Where(p => p.Installed)
                .Select(p => new[] { p.Name, p.Id, p.Version, p.Latest ?? string.Empty, p.Source })
                .Concat(_local.Select(l => new[] { l.Name, l.Id, l.Version, string.Empty, string.Empty }))
                .OrderBy(r => r[0], StringComparer.OrdinalIgnoreCase)
                .ToList();
            return "   - \r   \\ \r" + Table(["Name", "Id", "Version", "Available", "Source"], rows);
        }
    }

    private string UpgradeOutput()
    {
        lock (_gate)
        {
            var rows = _catalog.Where(p => p.Installed && p.Latest is not null).Select(p => new[] { p.Name, p.Id, p.Version, p.Latest!, p.Source }).ToList();
            if (rows.Count == 0)
            {
                return "No installed package found matching input criteria.\r\n";
            }

            return Table(["Name", "Id", "Version", "Available", "Source"], rows)
                   + string.Create(CultureInfo.InvariantCulture, $"{rows.Count} upgrades available.\r\n");
        }
    }

    private string SearchOutput(string query, string? source)
    {
        lock (_gate)
        {
            var rows = _catalog
                .Where(p => (source is null || p.Source == source)
                            && (p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new[] { p.Name, p.Id, p.Latest ?? p.Version, p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) && !p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? "Moniker: " + query.ToLowerInvariant() : string.Empty, p.Source })
                .ToList();
            return rows.Count == 0 ? "No package found matching input criteria.\r\n" : Table(["Name", "Id", "Version", "Match", "Source"], rows);
        }
    }

    private CommandResult Show(string id)
    {
        Package? package;
        lock (_gate)
        {
            package = _catalog.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        if (package is null)
        {
            return new CommandResult(CommandOutcome.Exited, unchecked((int)WingetErrors.NoPackagesFound), "No package found matching input criteria.\r\n", string.Empty);
        }

        var text = new StringBuilder()
            .Append("Found ").Append(package.Name).Append(" [").Append(package.Id).Append("]\r\n")
            .Append("Version: ").Append(package.Latest ?? package.Version).Append("\r\n")
            .Append("Publisher: ").Append(package.Publisher).Append("\r\n")
            .Append("Description: ").Append(package.Description).Append("\r\n")
            .Append("Homepage: ").Append(package.Homepage).Append("\r\n")
            .Append("License: ").Append(package.License).Append("\r\n")
            .Append("Installer:\r\n  Installer Type: exe\r\n");
        return Ok(text.ToString());
    }

    private async Task<CommandResult> OperateAsync(PackageOperationKind kind, IReadOnlyList<string> args, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var all = args.Contains("--all");
        var id = Value(args, "--id");
        List<Package> targets;
        lock (_gate)
        {
            targets = all
                ? _catalog.Where(p => p.Installed && p.Latest is not null).ToList()
                : _catalog.Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (targets.Count == 0)
        {
            if (id is not null && _local.Any(l => l.Id == id) && kind == PackageOperationKind.Uninstall)
            {
                var result = await StreamAsync(["Starting package uninstall...", "Successfully uninstalled"], onLine, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _local.RemoveAll(l => l.Id == id);
                }

                return result;
            }

            return new CommandResult(CommandOutcome.Exited, unchecked((int)WingetErrors.NoPackagesFound), "No package found matching input criteria.\r\n", string.Empty);
        }

        foreach (var package in targets)
        {
            var lines = kind == PackageOperationKind.Uninstall
                ? new List<string> { $"Found {package.Name} [{package.Id}]", "Starting package uninstall...", "  ██████████████████████████████  100%", "Successfully uninstalled" }
                : new List<string>
                {
                    $"Found {package.Name} [{package.Id}] Version {package.Latest ?? package.Version}",
                    "This application is licensed to you by its owner.",
                    "Microsoft is not responsible for, nor does it grant any licenses to, third-party packages.",
                    $"Downloading https://example.invalid/{package.Id}/setup.exe",
                    "  ██████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  12.0 MB / 58.3 MB",
                    "  ████████████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒  31.4 MB / 58.3 MB",
                    "  ██████████████████████████████  58.3 MB / 58.3 MB",
                    "Successfully verified installer hash",
                    "Starting package install...",
                    "  ████████████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒  54%",
                    "Successfully installed",
                };
            var result = await StreamAsync(lines, onLine, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != CommandOutcome.Exited)
            {
                return result;
            }

            lock (_gate)
            {
                package.Installed = kind != PackageOperationKind.Uninstall;
                if (kind != PackageOperationKind.Uninstall && package.Latest is not null)
                {
                    package.Version = package.Latest;
                    package.Latest = null;
                }
            }
        }

        return Ok(string.Empty);
    }

    private static async Task<CommandResult> StreamAsync(IReadOnlyList<string> lines, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        try
        {
            foreach (var line in lines)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false);
                output.Append(line).Append("\r\n");
                onLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(CommandOutcome.Cancelled, -1, output.ToString(), string.Empty);
        }

        return Ok(output.ToString());
    }

    /// <summary>Pads columns to their widest cell, shrinking the widest ones to fit 120 columns with "…".</summary>
    private static string Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(TextColumns.Width(h), rows.Count == 0 ? 0 : rows.Max(r => TextColumns.Width(r[i])))).ToArray();
        while (widths.Sum() + widths.Length - 1 > 120)
        {
            var widest = Array.IndexOf(widths, widths.Max());
            widths[widest]--;
        }

        var builder = new StringBuilder();
        builder.Append(Row(headers, widths)).Append("\r\n");
        builder.Append(new string('-', widths.Sum() + widths.Length - 1)).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(Row(row, widths)).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string Row(IReadOnlyList<string> cells, int[] widths)
    {
        var parts = new List<string>();
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (TextColumns.Width(cell) > widths[i])
            {
                var cut = cell;
                while (TextColumns.Width(cut) > widths[i] - 1)
                {
                    cut = cut[..^1];
                }

                cell = cut + "…";
            }

            parts.Add(cell + new string(' ', Math.Max(0, widths[i] - TextColumns.Width(cell))));
        }

        return string.Join(' ', parts).TrimEnd();
    }

    private sealed class Package(string id, string name, string version, string? latest, string source, string publisher, string description, string homepage, string license, bool installed)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string Version { get; set; } = version;

        public string? Latest { get; set; } = latest;

        public string Source { get; } = source;

        public string Publisher { get; } = publisher;

        public string Description { get; } = description;

        public string Homepage { get; } = homepage;

        public string License { get; } = license;

        public bool Installed { get; set; } = installed;
    }
}

public sealed class FakePackageManagerRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) => services.AddSingleton<IWingetLocator, FakeWingetLocator>();
}
