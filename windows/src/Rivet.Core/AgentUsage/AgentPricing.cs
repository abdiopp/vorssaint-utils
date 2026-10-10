// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Rivet.Core.Agents;

/// <summary>Long-context premium: prompts strictly above <see cref="Above"/> tokens pay the multipliers.</summary>
public sealed record LongContextPrice(long Above, double InputMultiplier, double OutputMultiplier);

/// <summary>List prices of one model family, in USD per million tokens.</summary>
public sealed record ModelPrice(
    string Id,
    double Input,
    double Output,
    double CacheRead,
    double CacheWrite,
    double CacheWriteLong,
    double FastMultiplier,
    LongContextPrice? LongContext);

public sealed record PlanPrice(string Match, string Name, double? Monthly);

/// <summary>
/// A validated price list (spec 07 §6.8). Any rule violation rejects the
/// whole list; with no usable list every record stays unpriced.
/// </summary>
public sealed class PriceList
{
    public const int MaxBytes = 256 * 1024;

    private PriceList(DateOnly updated, double webSearch, double usOnly, IReadOnlyList<ModelPrice> claude, IReadOnlyList<ModelPrice> codex,
        IReadOnlyList<PlanPrice> claudePlans, IReadOnlyList<PlanPrice> codexPlans)
    {
        Updated = updated;
        ClaudeWebSearch = webSearch;
        ClaudeUsOnlyMultiplier = usOnly;
        ClaudeModels = claude;
        CodexModels = codex;
        ClaudePlans = claudePlans;
        CodexPlans = codexPlans;
    }

    public DateOnly Updated { get; }

    public double ClaudeWebSearch { get; }

    public double ClaudeUsOnlyMultiplier { get; }

    public IReadOnlyList<ModelPrice> ClaudeModels { get; }

    public IReadOnlyList<ModelPrice> CodexModels { get; }

    public IReadOnlyList<PlanPrice> ClaudePlans { get; }

    public IReadOnlyList<PlanPrice> CodexPlans { get; }

    /// <summary>The list bundled with the app.</summary>
    public static PriceList Bundled { get; } = Parse(System.Text.Encoding.UTF8.GetBytes(BundledAgentPrices.Json), out _)
        ?? throw new InvalidOperationException("The bundled price list is invalid.");

    /// <summary>Validates and parses a price list; returns null (with the reason) when any rule is broken.</summary>
    public static PriceList? Parse(ReadOnlySpan<byte> json, out string? error)
    {
        error = null;
        if (json.Length > MaxBytes)
        {
            error = "too large";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not an object";
                return null;
            }

            if (Exact(root.Get("schema")) != 1)
            {
                error = "schema";
                return null;
            }

            if (root.Get("updated").String() is not { } updatedText
                || !DateOnly.TryParseExact(updatedText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated)
                || updated.Year is < 2024 or > 2100)
            {
                error = "updated";
                return null;
            }

            var claude = root.Get("claude");
            var codex = root.Get("codex");
            if (claude is not { ValueKind: JsonValueKind.Object } c || codex is not { ValueKind: JsonValueKind.Object } x)
            {
                error = "tables";
                return null;
            }

            var webSearch = Range(c.Get("webSearch"), 0, 1, 0.01);
            var usOnly = Range(c.Get("usOnlyMultiplier"), 1, 3, 1.1);
            var claudeModels = Models(c.Get("models"), claudeTable: true);
            var codexModels = Models(x.Get("models"), claudeTable: false);
            var claudePlans = Plans(c.Get("plans"));
            var codexPlans = Plans(x.Get("plans"));
            if (webSearch is null || usOnly is null || claudeModels is null || codexModels is null || claudePlans is null || codexPlans is null)
            {
                error = "invalid field";
                return null;
            }

            return new PriceList(updated, webSearch.Value, usOnly.Value, claudeModels, codexModels, claudePlans, codexPlans);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static double? Exact(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out var v) && double.IsFinite(v) ? v : null;

    /// <summary>A number within [min, max]; a missing value gives the fallback; anything else (booleans included) is invalid.</summary>
    private static double? Range(JsonElement? element, double min, double max, double? fallback)
    {
        if (element is null || element.Value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return Exact(element) is { } v && v >= min && v <= max ? v : null;
    }

    private static List<ModelPrice>? Models(JsonElement? element, bool claudeTable)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var models = new List<ModelPrice>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.Get("id").String() is not { } id || !ValidId(id)
                || id.StartsWith("claude-", StringComparison.Ordinal) != claudeTable || !ids.Add(id))
            {
                return null;
            }

            var input = Range(item.Get("input"), 0, 1000, null);
            var output = Range(item.Get("output"), 0, 1000, null);
            var cacheRead = Range(item.Get("cacheRead"), 0, 1000, null);
            if (input is null || output is null || cacheRead is null)
            {
                return null;
            }

            var cacheWrite = Range(item.Get("cacheWrite"), 0, 1000, input);
            var cacheWriteLong = cacheWrite is null ? null : Range(item.Get("cacheWriteLong"), 0, 1000, cacheWrite);
            var fast = Range(item.Get("fastMultiplier"), 1, 10, 1);
            if (cacheWrite is null || cacheWriteLong is null || fast is null)
            {
                return null;
            }

            LongContextPrice? longContext = null;
            if (item.Get("longContext") is { ValueKind: not JsonValueKind.Null } lc)
            {
                var above = Range(lc.Get("above"), 1000, 10_000_000, null);
                var inMul = Range(lc.Get("inputMultiplier"), 1, 10, null);
                var outMul = Range(lc.Get("outputMultiplier"), 1, 10, null);
                if (lc.ValueKind != JsonValueKind.Object || above is null || inMul is null || outMul is null)
                {
                    return null;
                }

                longContext = new LongContextPrice((long)above.Value, inMul.Value, outMul.Value);
            }

            models.Add(new ModelPrice(id, input.Value, output.Value, cacheRead.Value, cacheWrite.Value, cacheWriteLong.Value, fast.Value, longContext));
        }

        return models.Count is >= 1 and <= 500 ? models : null;
    }

    private static List<PlanPrice>? Plans(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.Value.ValueKind != JsonValueKind.Array || element.Value.GetArrayLength() > 50)
        {
            return null;
        }

        var plans = new List<PlanPrice>();
        foreach (var item in element.Value.EnumerateArray())
        {
            var match = item.Get("match").String();
            var name = item.Get("name").String();
            if (match is not { Length: >= 1 and <= 40 } || name is not { Length: >= 1 and <= 24 })
            {
                return null;
            }

            double? monthly = null;
            if (item.Get("monthly") is { ValueKind: not JsonValueKind.Null } m)
            {
                monthly = Range(m, 0, 100_000, null);
                if (monthly is null)
                {
                    return null;
                }
            }

            plans.Add(new PlanPrice(match, name, monthly));
        }

        return plans;
    }

    private static bool ValidId(string id) =>
        id.Length is >= 1 and <= 64 && id.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-');
}

/// <summary>Model lookup and the API-value math (spec 07 §6.8).</summary>
public static class AgentPricing
{
    private static readonly HashSet<string> SiblingWords = new(StringComparer.Ordinal)
    {
        "mini", "nano", "pro", "lite", "research", "search", "audio", "realtime", "transcribe", "tts", "cyber", "image",
    };

    /// <summary>
    /// Lowercase and trim; cut before "claude-"; for Claude ids turn "." into
    /// "-" (Copilot writes claude-sonnet-4.5); keep what follows the last "/";
    /// cut at the first "@" or "[".
    /// </summary>
    public static string Normalize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var id = model.Trim().ToLowerInvariant();
        var claude = id.IndexOf("claude-", StringComparison.Ordinal);
        if (claude > 0)
        {
            id = id[claude..];
        }

        if (id.StartsWith("claude-", StringComparison.Ordinal))
        {
            id = id.Replace('.', '-');
        }

        var slash = id.LastIndexOf('/');
        if (slash >= 0)
        {
            id = id[(slash + 1)..];
        }

        var cut = id.IndexOfAny(['@', '[']);
        return cut >= 0 ? id[..cut] : id;
    }

    /// <summary>The list price for a model, or null: unknown versions are unpriced, never given the previous version's price.</summary>
    public static ModelPrice? Lookup(PriceList? list, string? model)
    {
        if (list is null)
        {
            return null;
        }

        var id = Normalize(model);
        if (id.Length == 0)
        {
            return null;
        }

        var table = id.StartsWith("claude-", StringComparison.Ordinal) ? list.ClaudeModels : list.CodexModels;
        ModelPrice? best = null;
        foreach (var price in table)
        {
            if ((best is null || price.Id.Length > best.Id.Length) && FamilyMatches(id, price.Id))
            {
                best = price;
            }
        }

        if (best is null)
        {
            return null;
        }

        var rest = id[best.Id.Length..];
        foreach (var word in rest.Split(['-', '_', ':', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (SiblingWords.Contains(word))
            {
                return null;
            }
        }

        return best;
    }

    /// <summary>A family matches at a word boundary: then "-", "_" or ":" followed by no digits or by a date (4+ digits).</summary>
    public static bool FamilyMatches(string id, string family)
    {
        if (!id.StartsWith(family, StringComparison.Ordinal))
        {
            return false;
        }

        if (id.Length == family.Length)
        {
            return true;
        }

        var next = id[family.Length];
        if (next is not ('-' or '_' or ':'))
        {
            return false;
        }

        var digits = 0;
        for (var i = family.Length + 1; i < id.Length && char.IsAsciiDigit(id[i]); i++)
        {
            digits++;
        }

        return digits == 0 || digits >= 4;
    }

    /// <summary>The API value and cache savings of a record; cost is null when the model has no price.</summary>
    public static (double? Cost, double Savings) Cost(PriceList? list, UsageRecord record)
    {
        if (record.IsAggregate && record.Tokens.Total == 0 && record.WebSearches == 0)
        {
            return (0, 0);
        }

        var price = Lookup(list, record.Model);
        if (price is null || list is null)
        {
            return (null, 0);
        }

        var claude = price.Id.StartsWith("claude-", StringComparison.Ordinal);
        var m = (record.Fast ? price.FastMultiplier : 1) * (record.UsOnly && claude ? list.ClaudeUsOnlyMultiplier : 1);
        var inRate = m;
        var outRate = m;
        var t = record.Tokens;
        if (!record.IsAggregate && price.LongContext is { } lc && t.Prompt > lc.Above)
        {
            inRate *= lc.InputMultiplier;
            outRate *= lc.OutputMultiplier;
        }

        var longWrite = Math.Min(record.LongCacheWrite, t.CacheWrite);
        var cost = (((t.Input * price.Input) + ((t.CacheWrite - longWrite) * price.CacheWrite) + (longWrite * price.CacheWriteLong) + (t.CacheRead * price.CacheRead)) * inRate / 1e6)
                   + (t.Output * price.Output * outRate / 1e6)
                   + (record.WebSearches * (claude ? list.ClaudeWebSearch : 0.01));
        var savings = t.CacheRead * Math.Max(0, price.Input - price.CacheRead) * inRate / 1e6;
        return (cost, savings);
    }

    /// <summary>
    /// Prices a record in place. OpenCode's own recorded cost is kept when the
    /// model has no list price.
    /// </summary>
    public static void Reprice(PriceList? list, UsageRecord record)
    {
        var (cost, savings) = Cost(list, record);
        if (cost is null && record.ReportedCost is > 0)
        {
            record.Cost = record.ReportedCost;
            record.Savings = 0;
            return;
        }

        record.Cost = cost;
        record.Savings = savings;
    }

    /// <summary>Claude plan from the account tier and organization type: substring match, first wins.</summary>
    public static AgentPlan? ClaudePlan(PriceList? list, string? tier, string? organizationType)
    {
        var haystack = $"{tier} {organizationType}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return null;
        }

        foreach (var plan in list?.ClaudePlans ?? [])
        {
            if (haystack.Contains(plan.Match, StringComparison.Ordinal))
            {
                return new AgentPlan(plan.Name, plan.Monthly);
            }
        }

        if (string.IsNullOrWhiteSpace(organizationType))
        {
            return null;
        }

        var type = organizationType.StartsWith("claude_", StringComparison.OrdinalIgnoreCase) ? organizationType[7..] : organizationType;
        return new AgentPlan(Prettify(type), null);
    }

    /// <summary>Codex plan from <c>plan_type</c>: exact match, else capitalized.</summary>
    public static AgentPlan? CodexPlan(PriceList? list, string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
        {
            return null;
        }

        var key = planType.Trim().ToLowerInvariant();
        foreach (var plan in list?.CodexPlans ?? [])
        {
            if (plan.Match == key)
            {
                return new AgentPlan(plan.Name, plan.Monthly);
            }
        }

        return new AgentPlan(Prettify(key), null);
    }

    private static string Prettify(string text) =>
        string.Join(' ', text.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
