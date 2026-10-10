// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.PackageManager;

/// <summary>
/// State behind the package manager surfaces (the Homebrew manager's flows on
/// winget): installed packages with their updates, search results, the
/// selected package's details and the configured sources. Every read
/// carries a generation number so a stale answer never replaces a newer one.
/// </summary>
public sealed class PackageManagerService
{
    private readonly WingetClient _client;
    private readonly PackageOperationLane _lane;
    private readonly ISettingsStore _settings;
    private int _installedGeneration;
    private int _searchGeneration;
    private int _detailsGeneration;

    public PackageManagerService(WingetClient client, PackageOperationLane lane, ISettingsStore settings)
    {
        _client = client;
        _lane = lane;
        _settings = settings;
        _lane.Changed += (_, _) => Raise();
        _lane.Finished += (_, state) => _ = OnOperationFinishedAsync(state);
    }

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event EventHandler? Changed;

    public WingetClient Client => _client;

    public PackageOperationLane Lane => _lane;

    public WingetAvailability Availability => _client.Availability;

    public bool AgreementsAccepted => _settings.Get(PackageManagerSettings.SourceAgreementsAccepted);

    public IReadOnlyList<WingetPackage> Installed { get; private set; } = [];

    public bool InstalledLoaded { get; private set; }

    public IReadOnlyList<WingetPackage> SearchResults { get; private set; } = [];

    public string? LastQuery { get; private set; }

    public IReadOnlyList<WingetSource> Sources { get; private set; } = [];

    public WingetPackage? Selected { get; private set; }

    public WingetDetails? Details { get; private set; }

    public bool LoadingInstalled { get; private set; }

    public bool Searching { get; private set; }

    public bool LoadingDetails { get; private set; }

    /// <summary>The last failed read, shown as a banner.</summary>
    public string? Error { get; private set; }

    public bool IsBusy => LoadingInstalled || Searching || LoadingDetails || _lane.IsRunning;

    public int UpdateCount => Installed.Count(p => p.HasUpdate && !p.IsLocal);

    public string SourceFilter
    {
        get => _settings.Get(PackageManagerSettings.SourceFilter);
        set
        {
            _settings.Set(PackageManagerSettings.SourceFilter, value);
            Raise();
        }
    }

    /// <summary>Installed packages through the source filter, updates first, then by name.</summary>
    public IReadOnlyList<WingetPackage> FilteredInstalled() =>
        Installed.Where(p => MatchesFilter(p, SourceFilter))
            .OrderByDescending(p => p.HasUpdate && !p.IsLocal)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public int CountFor(string filter) => Installed.Count(p => MatchesFilter(p, filter));

    public static bool MatchesFilter(WingetPackage package, string filter) => filter switch
    {
        "winget" => string.Equals(package.Source, "winget", StringComparison.OrdinalIgnoreCase),
        "msstore" => package.IsStore,
        "local" => package.IsLocal,
        _ => true,
    };

    /// <summary>Whether a search result is installed (by id), and the installed entry if so.</summary>
    public WingetPackage? InstalledEntry(string id) =>
        Installed.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public void AcceptAgreements()
    {
        _settings.Set(PackageManagerSettings.SourceAgreementsAccepted, true);
        Raise();
    }

    public async Task DetectAsync(bool force = false)
    {
        await _client.DetectAsync(force).ConfigureAwait(false);
        Raise();
    }

    public async Task LoadInstalledAsync()
    {
        if (!AgreementsAccepted)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _installedGeneration);
        LoadingInstalled = true;
        Raise();
        try
        {
            var result = await _client.ListInstalledAsync(CancellationToken.None).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _installedGeneration))
            {
                return;
            }

            if (result.Succeeded)
            {
                Installed = result.Value!;
                InstalledLoaded = true;
                Error = null;
                if (Selected is { } selected)
                {
                    Selected = InstalledEntry(selected.Id) ?? selected with { Available = null };
                }
            }
            else
            {
                Error = result.Error;
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _installedGeneration))
            {
                LoadingInstalled = false;
            }

            Raise();
        }
    }

    public async Task SearchAsync(string query)
    {
        if (!AgreementsAccepted || PackageArguments.CleanQuery(query) is not { } text)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _searchGeneration);
        Searching = true;
        LastQuery = text;
        Raise();
        try
        {
            var source = SourceFilter is "winget" or "msstore" ? SourceFilter : null;
            var result = await _client.SearchAsync(text, source, CancellationToken.None).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _searchGeneration))
            {
                return;
            }

            if (result.Succeeded)
            {
                SearchResults = result.Value!;
                Error = null;
            }
            else
            {
                SearchResults = [];
                Error = result.Error;
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _searchGeneration))
            {
                Searching = false;
            }

            Raise();
        }
    }

    public void ClearSearch()
    {
        Interlocked.Increment(ref _searchGeneration);
        SearchResults = [];
        LastQuery = null;
        Searching = false;
        Raise();
    }

    /// <summary>Selects a package and loads its details (local entries have none to load).</summary>
    public async Task SelectAsync(WingetPackage? package)
    {
        var generation = Interlocked.Increment(ref _detailsGeneration);
        Selected = package;
        Details = null;
        LoadingDetails = package is not null && !package.IsLocal && !package.IdTruncated;
        Raise();
        if (!LoadingDetails || package is null)
        {
            return;
        }

        try
        {
            var result = await _client.ShowAsync(package.Id, package.Source, CancellationToken.None).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _detailsGeneration))
            {
                return;
            }

            Details = result.Value;
        }
        finally
        {
            if (generation == Volatile.Read(ref _detailsGeneration))
            {
                LoadingDetails = false;
            }

            Raise();
        }
    }

    public async Task LoadSourcesAsync()
    {
        var result = await _client.SourcesAsync(CancellationToken.None).ConfigureAwait(false);
        if (result.Succeeded)
        {
            Sources = result.Value!;
        }

        Raise();
    }

    public Task<OperationResult> RunAsync(PackageOperationKind kind, WingetPackage? package) =>
        _lane.RunAsync(new PackageOperationRequest(kind, package?.Id, package?.Name, package?.IsLocal == true ? null : package?.Source));

    private async Task OnOperationFinishedAsync(PackageOperationState state)
    {
        // Refresh after every operation, even a failed one (part of an "update all" may have happened).
        await LoadInstalledAsync().ConfigureAwait(false);
        if (state.Result == OperationResult.Succeeded && state.Kind == PackageOperationKind.Uninstall)
        {
            await SelectAsync(null).ConfigureAwait(false);
        }
        else if (state.Result == OperationResult.Succeeded && state.PackageId is { } id && Selected?.Id == id)
        {
            await SelectAsync(InstalledEntry(id) ?? Selected).ConfigureAwait(false);
        }

        if (state.Kind == PackageOperationKind.UpdateSources)
        {
            await LoadSourcesAsync().ConfigureAwait(false);
        }
    }

    private void Raise() => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
}
