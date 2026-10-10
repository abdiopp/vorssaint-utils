// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Rivet.Core.Agents;
using Xunit;

namespace Rivet.Core.Tests.Agents;

public class AgentPricingTests
{
    private static PriceList Prices => PriceList.Bundled;

    private static double Cost(string model, long input = 0, long cacheWrite = 0, long cacheRead = 0, long output = 0, long longWrite = 0, bool fast = false, bool usOnly = false, long webSearches = 0, bool aggregate = false)
    {
        var record = new UsageRecord
        {
            Key = "k", Provider = AgentProvider.Claude, Model = model,
            Tokens = new TokenCounts(input, cacheWrite, cacheRead, output),
            LongCacheWrite = longWrite, Fast = fast, UsOnly = usOnly, WebSearches = webSearches, IsAggregate = aggregate,
        };
        return AgentPricing.Cost(Prices, record).Cost ?? double.NaN;
    }

    [Fact]
    public void The_bundled_list_is_valid_and_complete()
    {
        Assert.Equal(new DateOnly(2026, 10, 2), Prices.Updated);
        Assert.Equal(24, Prices.ClaudeModels.Count);
        Assert.Equal(73, Prices.CodexModels.Count);
        Assert.Equal(0.01, Prices.ClaudeWebSearch);
        Assert.Equal(1.1, Prices.ClaudeUsOnlyMultiplier);
        Assert.Equal(6, Prices.ClaudePlans.Count);
    }

    [Fact]
    public void Spec_cost_anchors()
    {
        // Opus 5.5: 1000 input, 4000 five-minute writes, 6000 one-hour writes, 100,000 reads, 2000 output.
        Assert.Equal(0.132, Cost("claude-opus-5-5", input: 1000, cacheWrite: 10_000, longWrite: 6000, cacheRead: 100_000, output: 2000), 9);
        Assert.Equal(((300_000 * 10 * 4) + (1000 * 50 * 3)) / 1e6, Cost("gpt-6-astra", input: 300_000, output: 1000, fast: true), 9);
        Assert.Equal(2.72, Cost("gpt-6-astra", input: 272_000), 9);
        Assert.Equal(10, Cost("claude-opus-5", input: 1_000_000, fast: true), 9);
        Assert.Equal(5, Cost("claude-opus-4-7", input: 1_000_000, fast: true), 9);
        Assert.Equal(5.5, Cost("claude-opus-5", input: 1_000_000, usOnly: true), 9);
        Assert.Equal(1.5225, Cost("claude-sonnet-4-5", input: 250_000, output: 1000), 9);
        Assert.Equal(0.03, Cost("claude-sonnet-4-5", webSearches: 3), 9);
    }

    [Fact]
    public void Aggregates_without_tokens_cost_nothing_and_never_get_the_long_context_premium()
    {
        Assert.Equal(0, Cost("gpt-5", aggregate: true));
        Assert.Equal(250_000 * 3 / 1e6, Cost("claude-sonnet-4-5", input: 250_000, aggregate: true), 9);
    }

    [Theory]
    [InlineData("claude-opus-4-1-20250805", "claude-opus-4-1")]
    [InlineData("us.anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-sonnet-4-5")]
    [InlineData("anthropic/claude-sonnet-4.5", "claude-sonnet-4-5")]
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4-5")]
    [InlineData("GPT-5.1-Codex-Max", "gpt-5.1-codex-max")]
    [InlineData("openai/gpt-5-codex", "gpt-5-codex")]
    [InlineData("gpt-4o-2024-08-06", "gpt-4o")]
    [InlineData("gpt-4o-2024-05-13", "gpt-4o-2024-05-13")]
    [InlineData("gpt-5@2025-08-07", "gpt-5")]
    [InlineData("claude-opus-5-5[1m]", "claude-opus-5-5")]
    public void Lookup_finds_the_longest_family_at_a_word_boundary(string model, string family) =>
        Assert.Equal(family, AgentPricing.Lookup(Prices, model)?.Id);

    [Theory]
    [InlineData("claude-opus-5-6")]
    [InlineData("gpt-6-sol-2")]
    [InlineData("gpt-7")]
    [InlineData("gpt-5-codex-mini")]
    [InlineData("gpt-5.1-realtime")]
    [InlineData("gpt-5-search")]
    [InlineData("")]
    [InlineData("<synthetic>")]
    public void Unknown_versions_and_sibling_models_are_unpriced(string model) =>
        Assert.Null(AgentPricing.Lookup(Prices, model));

    [Fact]
    public void An_unknown_model_keeps_an_opencode_reported_cost()
    {
        var record = new UsageRecord { Key = "k", Provider = AgentProvider.OpenCode, Model = "qwen3-coder", Tokens = new TokenCounts(100, 0, 0, 10), ReportedCost = 0.42 };
        AgentPricing.Reprice(Prices, record);
        Assert.Equal(0.42, record.Cost);
        var unknown = new UsageRecord { Key = "k2", Provider = AgentProvider.OpenCode, Model = "qwen3-coder", Tokens = new TokenCounts(100, 0, 0, 10) };
        AgentPricing.Reprice(Prices, unknown);
        Assert.Null(unknown.Cost);
        Assert.Equal(0, unknown.Savings);
    }

    [Fact]
    public void Cache_savings_follow_the_read_discount()
    {
        var record = new UsageRecord { Key = "k", Provider = AgentProvider.Claude, Model = "claude-sonnet-4-6", Tokens = new TokenCounts(0, 0, 1_000_000, 0) };
        Assert.Equal(2.7, AgentPricing.Cost(Prices, record).Savings, 9);
    }

    [Fact]
    public void Plans_match_by_substring_and_codex_plans_exactly()
    {
        Assert.Equal(new AgentPlan("Max 20×", 200), AgentPricing.ClaudePlan(Prices, "default_claude_max_20x", "claude_max"));
        Assert.Equal(new AgentPlan("Pro", 20), AgentPricing.ClaudePlan(Prices, null, "claude_pro"));
        Assert.Equal(new AgentPlan("Galaxy Brain", null), AgentPricing.ClaudePlan(Prices, null, "claude_galaxy_brain"));
        Assert.Equal(new AgentPlan("Plus", 20), AgentPricing.CodexPlan(Prices, "plus"));
        Assert.Equal(new AgentPlan("Pro", null), AgentPricing.CodexPlan(Prices, "pro"));
        Assert.Equal(new AgentPlan("Business", null), AgentPricing.CodexPlan(Prices, "business"));
    }

    [Theory]
    [InlineData("""{"schema":2,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2023-12-31","claude":{"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-02-30","claude":{"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-y","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"claude-y","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":true,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":1001,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":1,"output":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1},{"id":"claude-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"Claude-X","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1,"fastMultiplier":11}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1,"longContext":{"above":999,"inputMultiplier":2,"outputMultiplier":1}}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"webSearch":2,"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"models":[]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("""{"schema":1,"updated":"2026-01-01","claude":{"plans":[{"match":"","name":"X"}],"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}""")]
    [InlineData("not json")]
    public void Any_rule_violation_rejects_the_whole_list(string json) =>
        Assert.Null(PriceList.Parse(Encoding.UTF8.GetBytes(json), out _));

    [Fact]
    public void Defaults_and_extra_fields_are_accepted()
    {
        var list = PriceList.Parse("""{"schema":1,"updated":"2026-01-01","extra":true,"claude":{"models":[{"id":"claude-x","input":2,"output":1,"cacheRead":1,"note":"hi"}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}"""u8, out var error);
        Assert.Null(error);
        var model = Assert.Single(list!.ClaudeModels);
        Assert.Equal(2, model.CacheWrite);
        Assert.Equal(2, model.CacheWriteLong);
        Assert.Equal(1, model.FastMultiplier);
        Assert.True(PriceList.Parse(new byte[PriceList.MaxBytes + 1], out _) is null);
    }

    [Fact]
    public void The_newer_list_wins_and_a_download_wins_a_tie()
    {
        var older = PriceList.Parse("""{"schema":1,"updated":"2026-01-01","claude":{"models":[{"id":"claude-x","input":1,"output":1,"cacheRead":1}]},"codex":{"models":[{"id":"gpt-x","input":1,"output":1,"cacheRead":1}]}}"""u8, out _)!;
        Assert.Same(Prices, AgentPriceManager.Choose(Prices, older));
        var sameDay = PriceList.Parse(Encoding.UTF8.GetBytes(BundledAgentPrices.Json), out _)!;
        Assert.Same(sameDay, AgentPriceManager.Choose(Prices, sameDay));
        Assert.Same(Prices, AgentPriceManager.Choose(Prices, null));
    }
}

public class AgentFormatTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-3-5-sonnet-20241022", "Sonnet 3.5")]
    [InlineData("claude-opus-4-1-20250805", "Opus 4.1")]
    [InlineData("claude-sonnet-4.5", "Sonnet 4.5")]
    [InlineData("gpt-5.1-codex-max", "GPT-5.1 Codex Max")]
    [InlineData("gpt-5", "GPT-5")]
    [InlineData("qwen3-coder", "qwen3-coder")]
    public void Model_display_names(string model, string expected) => Assert.Equal(expected, AgentFormat.ModelDisplayName(model));

    [Theory]
    [InlineData(999, "999")]
    [InlineData(4200, "4.2K")]
    [InlineData(4299, "4.2K")]
    [InlineData(48_000, "48K")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(25_000_000_000, "25B")]
    public void Tokens_round_down(long count, string expected) => Assert.Equal(expected, AgentFormat.Tokens(count, En));

    [Theory]
    [InlineData(1.234, "$1.23")]
    [InlineData(123.4, "$123")]
    [InlineData(12_345, "$12K")]
    public void Costs(double usd, string expected) => Assert.Equal(expected, AgentFormat.Cost(usd, En));

    [Fact]
    public void Durations_and_clocks()
    {
        Assert.Equal("4m 12s", AgentFormat.Duration(TimeSpan.FromSeconds(252)));
        Assert.Equal("1h 5m", AgentFormat.Duration(TimeSpan.FromMinutes(65)));
        Assert.Equal("2d 3h", AgentFormat.Duration(TimeSpan.FromHours(51)));
        Assert.Equal("12:34", AgentFormat.Clock(TimeSpan.FromSeconds(754)));
        Assert.Equal("1:02:03", AgentFormat.Clock(new TimeSpan(1, 2, 3)));
        Assert.Equal("0h 1m", AgentFormat.Countdown(TimeSpan.FromSeconds(10)));
        Assert.Equal("3d 4h", AgentFormat.Countdown(new TimeSpan(3, 4, 0, 0)));
    }
}
