// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Launcher;

namespace Rivet.App.Features.Launcher;

/// <summary>
/// The bar's built-in sources, each published as an <see cref="ISearchProvider"/>
/// (ids <c>commandBar.&lt;source&gt;</c>) so other surfaces can search them. The
/// bar itself does not query these adapters: it ranks all sources together in
/// one pool (spec 06 §6.3, which needs per-kind caps, tiers and learning across
/// sources), and only asks providers from other modules for extra rows.
/// </summary>
public static class CommandBarProviders
{
    public const string Prefix = "commandBar.";

    public static bool IsOwn(ISearchProvider provider) => provider.Id.StartsWith(Prefix, StringComparison.Ordinal);

    public static IEnumerable<ISearchProvider> Create(IServiceProvider services)
    {
        CommandBarCatalog Catalog() => services.GetRequiredService<CommandBarCatalog>();
        ICommandBarPlatform Platform() => services.GetRequiredService<ICommandBarPlatform>();
        CommandBarPreferences Preferences() => services.GetRequiredService<CommandBarPreferences>();

        IEnumerable<CommandRow> CatalogOf(params CommandSource[] sources) =>
            Catalog().BuildCatalog().Where(r => sources.Contains(CommandSources.Of(r.Id)));

        yield return new CommandBarRowProvider("actions", CommandSource.Actions, FeatureIds.CommandBar, _ => Ui(() => CatalogOf(CommandSource.Actions)));
        yield return new CommandBarRowProvider("settingsPages", CommandSource.SettingsPages, FeatureIds.CommandBar, _ => Ui(() => CatalogOf(CommandSource.SettingsPages)));
        yield return new CommandBarRowProvider("snippets", CommandSource.Snippets, FeatureIds.TextSnippets, _ => Ui(() => CatalogOf(CommandSource.Snippets)));
        yield return new CommandBarRowProvider("links", CommandSource.Links, FeatureIds.CommandBar, _ => Ui(() => CatalogOf(CommandSource.Links, CommandSource.Folders)));
        yield return new CommandBarRowProvider("windowsSettings", CommandSource.MacSettings, FeatureIds.CommandBar, _ => Ui(() => Catalog().WindowsSettingsRows()));
        yield return new CommandBarRowProvider("clipboard", CommandSource.Clipboard, FeatureIds.ClipboardHistory, query => Ui(() => Catalog().ClipboardRows(query, 8)), ranked: false);
        yield return new CommandBarRowProvider("emoji", CommandSource.Emoji, FeatureIds.CommandBar, _ => Ui(() => CommandBarEmoji.All.Select(Catalog().EmojiRow)));
        yield return new CommandBarRowProvider("calculator", CommandSource.Calculator, FeatureIds.CommandBar,
            query => Ui(() => Catalog().AnswerRow(query.Trim()) is { } row ? [row] : Enumerable.Empty<CommandRow>()), ranked: false);
        yield return new CommandBarRowProvider("windows", CommandSource.Windows, FeatureIds.CommandBar, _ => Ui(() => Catalog().WindowRows(Platform().GetWindows())));
        yield return new CommandBarRowProvider("apps", CommandSource.Apps, FeatureIds.CommandBar, async _ =>
        {
            var apps = await Task.Run(() => Platform().GetAppsAsync(CancellationToken.None)).ConfigureAwait(false);
            return await Ui(() => Catalog().AppRows(apps, [])).ConfigureAwait(false);
        });
        yield return new CommandBarRowProvider("files", CommandSource.Files, FeatureIds.CommandBar, async query =>
        {
            var words = CommandBarSession.Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Sum(w => w.Length) < 2)
            {
                return [];
            }

            var preferences = Preferences();
            var platform = Platform();
            var scopes = preferences.FileScopes.Select(s => CommandBarLinks.PlaceTarget(s, platform.HomeFolder)).ToList();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var hits = scopes.Count > 0
                ? await platform.SearchFilesAsync(scopes, words, preferences.FileIgnores, 40, timeout.Token).ConfigureAwait(false)
                : await platform.RecentFilesAsync(words, 40, timeout.Token).ConfigureAwait(false);
            return await Ui(() => hits.Select(Catalog().FileRow)).ConfigureAwait(false);
        }, ranked: false);
    }

    private static async Task<IEnumerable<CommandRow>> Ui(Func<IEnumerable<CommandRow>> build) =>
        Dispatcher.UIThread.CheckAccess() ? build().ToList() : await Dispatcher.UIThread.InvokeAsync(() => (IEnumerable<CommandRow>)build().ToList());
}

/// <summary>One source of the bar as a search provider: rows ranked with the bar's own scoring.</summary>
public sealed class CommandBarRowProvider(string name, CommandSource source, string featureId, Func<string, Task<IEnumerable<CommandRow>>> pool, bool ranked = true)
    : ISearchProvider
{
    private const int Limit = 8;

    public string Id { get; } = CommandBarProviders.Prefix + name;

    public string FeatureId { get; } = featureId;

    public CommandSource Source { get; } = source;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (query.Trim().Length == 0)
        {
            return [];
        }

        var rows = (await pool(query).ConfigureAwait(false)).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        var picked = ranked
            ? CommandBarSearch.Rank(rows, query, RankingContext.Empty).Take(Limit).Select(r => (r.Row, Score: (double)r.Score)).ToList()
            : rows.Take(Limit).Select((r, i) => (Row: r, Score: 1000.0 - i)).ToList();
        return picked.Select(p => ToResult(p.Row, p.Score, query)).ToList();
    }

    private static SearchResult ToResult(CommandRow row, double score, string query) => new()
    {
        Id = row.StableKey,
        Title = row.Title,
        Subtitle = row.Subtitle,
        Icon = row.Icon,
        IconPath = row.IconPath,
        Score = score,
        Category = CommandSources.StorageId(CommandSources.Of(row.Id)),
        Activate = () => row.Run is { } run ? run(new CommandRunContext { Query = query }) : Task.CompletedTask,
    };
}
