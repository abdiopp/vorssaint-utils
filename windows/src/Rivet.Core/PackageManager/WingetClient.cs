// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Settings;

namespace Rivet.Core.Maintenance.PackageManager;

/// <summary>Finds winget.exe (the App Installer execution alias).</summary>
public interface IWingetLocator
{
    /// <summary>Full path of winget, or null when App Installer is missing.</summary>
    string? Locate();
}

/// <summary>A read command's answer, or why there is none.</summary>
public sealed record WingetResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;

    public static WingetResult<T> Ok(T value) => new(value, null);

    public static WingetResult<T> Fail(string error) => new(default, error);
}

/// <summary>Rules for every value that reaches a winget argument.</summary>
public static class PackageArguments
{
    /// <summary>
    /// Package ids: non-empty, at most 256 characters, no leading '-' (it would
    /// read as an option), no quotes or control characters. Local ids
    /// ("ARP\Machine\X64\{GUID}") are allowed for uninstalling.
    /// </summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 256
        && id == id.Trim()
        && !id.StartsWith('-')
        && !id.Contains("..", StringComparison.Ordinal)
        && !id.EndsWith(WingetPackage.Ellipsis)
        && id.All(c => !char.IsControl(c) && c != '"');

    /// <summary>Search text: trimmed, 1–100 characters, no leading '-', no control characters.</summary>
    public static string? CleanQuery(string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 100 || text.StartsWith('-') || text.Any(char.IsControl))
        {
            return null;
        }

        return text;
    }

    public static bool IsValidSource(string? source) =>
        source is { Length: > 0 and <= 64 } && source.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && !source.StartsWith('-');
}

/// <summary>
/// The winget command line, used through argv arrays only. Tables are parsed
/// by <see cref="WingetOutput"/>; truncated ids are completed from
/// <c>winget export</c>. Nothing runs before the person accepted the source
/// agreements in the app (<see cref="PackageManagerSettings.SourceAgreementsAccepted"/>).
/// </summary>
public sealed class WingetClient
{
    /// <summary>Reads may update the source index first, which takes a while on a cold start.</summary>
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(120);

    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Operations end only after this long without output (never on total duration).</summary>
    public static readonly TimeSpan OperationSilence = TimeSpan.FromMinutes(15);

    private readonly ICommandRunner _runner;
    private readonly IWingetLocator _locator;
    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _detectGate = new(1, 1);
    private string? _path;
    private bool _supportsDisableInteractivity = true;
    private bool _supportsIncludeUnknown = true;

    public WingetClient(ICommandRunner runner, IWingetLocator locator, ISettingsStore settings)
    {
        _runner = runner;
        _locator = locator;
        _settings = settings;
    }

    public WingetAvailability Availability { get; private set; } = WingetAvailability.Unknown;

    public Version? Version { get; private set; }

    public bool AgreementsAccepted => _settings.Get(PackageManagerSettings.SourceAgreementsAccepted);

    /// <summary>Finds winget and reads its version (once, unless forced).</summary>
    public async Task<WingetAvailability> DetectAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (Availability != WingetAvailability.Unknown && !force)
        {
            return Availability;
        }

        await _detectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Availability != WingetAvailability.Unknown && !force)
            {
                return Availability;
            }

            _path = _locator.Locate();
            if (_path is null)
            {
                Availability = WingetAvailability.Missing;
                return Availability;
            }

            var result = await _runner.RunAsync(new CommandRequest { FileName = _path, Arguments = ["--version"], Timeout = TimeSpan.FromSeconds(20) }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Outcome == CommandOutcome.FailedToStart)
            {
                Availability = WingetAvailability.Missing;
                return Availability;
            }

            Version = WingetOutput.ParseVersion(result.Output);
            if (Version is { } version)
            {
                _supportsDisableInteractivity = version >= new Version(1, 4);
                _supportsIncludeUnknown = version >= new Version(1, 3);
            }

            Availability = WingetAvailability.Available;
            Log.Info("winget", $"winget {Version?.ToString() ?? "(unknown version)"} at {_path}.");
            return Availability;
        }
        finally
        {
            _detectGate.Release();
        }
    }

    /// <summary>Installed packages with a newer version (pinned and explicit-only ones flagged).</summary>
    public async Task<WingetResult<IReadOnlyList<WingetPackage>>> ListUpgradesAsync(CancellationToken cancellationToken)
    {
        List<string> args = ["upgrade"];
        if (_supportsIncludeUnknown)
        {
            args.Add("--include-unknown");
        }

        var result = await QueryAsync(args, ListTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            if (_supportsIncludeUnknown && result.ExitCode == WingetErrors.InvalidArguments)
            {
                _supportsIncludeUnknown = false;
                return await ListUpgradesAsync(cancellationToken).ConfigureAwait(false);
            }

            // "No installed package found" / "No applicable upgrade found": nothing to update.
            if (result.ExitCode is WingetErrors.NoPackagesFound or WingetErrors.UpdateNotApplicable)
            {
                return WingetResult<IReadOnlyList<WingetPackage>>.Ok([]);
            }

            return WingetResult<IReadOnlyList<WingetPackage>>.Fail(result.Error);
        }

        var packages = WingetOutput.ParsePackages(result.Output, WingetTableKind.Upgrade);
        packages = await ResolveIdsAsync(packages, cancellationToken).ConfigureAwait(false);
        return WingetResult<IReadOnlyList<WingetPackage>>.Ok(packages);
    }

    public async Task<WingetResult<IReadOnlyList<WingetPackage>>> ListInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await QueryAsync(["list"], ListTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return result.ExitCode == WingetErrors.NoPackagesFound
                ? WingetResult<IReadOnlyList<WingetPackage>>.Ok([])
                : WingetResult<IReadOnlyList<WingetPackage>>.Fail(result.Error);
        }

        var packages = WingetOutput.ParsePackages(result.Output, WingetTableKind.List);
        packages = await ResolveIdsAsync(packages, cancellationToken).ConfigureAwait(false);
        return WingetResult<IReadOnlyList<WingetPackage>>.Ok(packages);
    }

    public async Task<WingetResult<IReadOnlyList<WingetPackage>>> SearchAsync(string query, string? source, CancellationToken cancellationToken)
    {
        if (PackageArguments.CleanQuery(query) is not { } text)
        {
            return WingetResult<IReadOnlyList<WingetPackage>>.Ok([]);
        }

        List<string> args = ["search", "--query", text];
        if (PackageArguments.IsValidSource(source))
        {
            args.Add("--source");
            args.Add(source!);
        }

        var result = await QueryAsync(args, QueryTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            // "No package found matching input criteria." is an empty answer, not a failure.
            return result.ExitCode == WingetErrors.NoPackagesFound
                ? WingetResult<IReadOnlyList<WingetPackage>>.Ok([])
                : WingetResult<IReadOnlyList<WingetPackage>>.Fail(result.Error);
        }

        return WingetResult<IReadOnlyList<WingetPackage>>.Ok(WingetOutput.ParsePackages(result.Output, WingetTableKind.Search));
    }

    public async Task<WingetResult<WingetDetails>> ShowAsync(string id, string? source, CancellationToken cancellationToken)
    {
        if (!PackageArguments.IsValidId(id))
        {
            return WingetResult<WingetDetails>.Fail(L.Get("win.packageManager.errorNotFound"));
        }

        List<string> args = ["show", "--id", id, "--exact"];
        if (PackageArguments.IsValidSource(source))
        {
            args.Add("--source");
            args.Add(source!);
        }

        var result = await QueryAsync(args, QueryTimeout, cancellationToken).ConfigureAwait(false);
        return result.Error is not null
            ? WingetResult<WingetDetails>.Fail(result.Error)
            : WingetResult<WingetDetails>.Ok(WingetOutput.ParseShow(result.Output, id));
    }

    public async Task<WingetResult<IReadOnlyList<WingetSource>>> SourcesAsync(CancellationToken cancellationToken)
    {
        var result = await QueryAsync(["source", "list"], QueryTimeout, cancellationToken, acceptAgreements: false).ConfigureAwait(false);
        return result.Error is not null
            ? WingetResult<IReadOnlyList<WingetSource>>.Fail(result.Error)
            : WingetResult<IReadOnlyList<WingetSource>>.Ok(WingetOutput.ParseSources(result.Output));
    }

    /// <summary>The argv of an operation; null when an id is not valid or winget is missing.</summary>
    public CommandRequest? OperationRequest(PackageOperationKind kind, string? id, string? source)
    {
        if (_path is null || (kind is PackageOperationKind.Install or PackageOperationKind.Uninstall or PackageOperationKind.Upgrade && !PackageArguments.IsValidId(id)))
        {
            return null;
        }

        List<string> args = kind switch
        {
            PackageOperationKind.Install => ["install", "--id", id!, "--exact", "--silent", "--accept-package-agreements"],
            PackageOperationKind.Uninstall => ["uninstall", "--id", id!, "--exact", "--silent"],
            PackageOperationKind.Upgrade => ["upgrade", "--id", id!, "--exact", "--silent", "--accept-package-agreements"],
            PackageOperationKind.UpgradeAll => ["upgrade", "--all", "--silent", "--accept-package-agreements"],
            _ => ["source", "update"],
        };
        if (kind is PackageOperationKind.Install or PackageOperationKind.Uninstall or PackageOperationKind.Upgrade && PackageArguments.IsValidSource(source))
        {
            args.Add("--source");
            args.Add(source!);
        }

        if (kind != PackageOperationKind.UpdateSources)
        {
            args.Add("--accept-source-agreements");
        }

        if (_supportsDisableInteractivity)
        {
            args.Add("--disable-interactivity");
        }

        return new CommandRequest { FileName = _path, Arguments = args, InactivityTimeout = OperationSilence };
    }

    /// <summary>A human-readable message for a failed command (plain text, then the last output lines).</summary>
    public static string DescribeFailure(CommandResult result)
    {
        if (result.Outcome == CommandOutcome.FailedToStart)
        {
            return L.Get("win.packageManager.errorStart");
        }

        if (result.Outcome == CommandOutcome.TimedOut)
        {
            return L.Get("win.packageManager.errorTimeout");
        }

        if (result.Outcome == CommandOutcome.Cancelled)
        {
            return L.Get("Strings.homebrewOperationCancelled");
        }

        var code = result.ExitCodeHex;
        if (WingetErrors.MessageKey(code) is { } key)
        {
            return L.Get(key);
        }

        var tail = WingetOutput.MeaningfulTail(result.Output + "\n" + result.Error);
        var codeText = string.Create(CultureInfo.InvariantCulture, $"0x{code:X8}");
        return tail.Count > 0
            ? string.Join(Environment.NewLine, tail)
            : L.Format("win.packageManager.errorCodeFormat", codeText);
    }

    private async Task<(string Output, string? Error, uint? ExitCode)> QueryAsync(List<string> args, TimeSpan timeout, CancellationToken cancellationToken, bool acceptAgreements = true)
    {
        if (await DetectAsync(cancellationToken: cancellationToken).ConfigureAwait(false) != WingetAvailability.Available || _path is null)
        {
            return (string.Empty, L.Get("win.packageManager.missingTitle"), null);
        }

        if (acceptAgreements)
        {
            args.Add("--accept-source-agreements");
        }

        if (_supportsDisableInteractivity)
        {
            args.Add("--disable-interactivity");
        }

        var result = await _runner.RunAsync(new CommandRequest { FileName = _path, Arguments = args, Timeout = timeout }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCodeHex == WingetErrors.InvalidArguments && _supportsDisableInteractivity && result.Outcome == CommandOutcome.Exited)
        {
            // Very old winget: retry once without the newer switch.
            _supportsDisableInteractivity = false;
            args.Remove("--disable-interactivity");
            result = await _runner.RunAsync(new CommandRequest { FileName = _path, Arguments = args, Timeout = timeout }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (result.Succeeded)
        {
            return (result.Output, null, 0);
        }

        // Some reads exit non-zero with a usable table (e.g. "no applicable upgrade" after listing).
        if (result.Outcome == CommandOutcome.Exited && WingetOutput.ParseTables(result.Output).Count > 0)
        {
            return (result.Output, null, result.ExitCodeHex);
        }

        Log.Warn("winget", $"winget {args[0]} failed: {result.Outcome} 0x{result.ExitCodeHex:X8}.");
        return (result.Output, DescribeFailure(result), result.Outcome == CommandOutcome.Exited ? result.ExitCodeHex : null);
    }

    private async Task<IReadOnlyList<WingetPackage>> ResolveIdsAsync(IReadOnlyList<WingetPackage> packages, CancellationToken cancellationToken)
    {
        if (!packages.Any(p => p.IdTruncated) || _path is null)
        {
            return packages;
        }

        var file = Path.Combine(Path.GetTempPath(), $"{App.AppIdentity.Id}-winget-export-{Guid.NewGuid():N}.json");
        try
        {
            List<string> args = ["export", "--output", file, "--include-versions", "--accept-source-agreements"];
            if (_supportsDisableInteractivity)
            {
                args.Add("--disable-interactivity");
            }

            var result = await _runner.RunAsync(new CommandRequest { FileName = _path, Arguments = args, Timeout = ListTimeout }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!File.Exists(file))
            {
                Log.Warn("winget", $"winget export produced no file ({result.Outcome}, 0x{result.ExitCodeHex:X8}); truncated ids stay unresolved.");
                return packages;
            }

            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            return WingetOutput.ResolveTruncatedIds(packages, WingetOutput.ParseExport(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("winget", "Could not read the winget export.", ex);
            return packages;
        }
        finally
        {
            try
            {
                // Our own temporary file, created by the export above.
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
