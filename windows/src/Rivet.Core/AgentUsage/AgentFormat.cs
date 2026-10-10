// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Rivet.Core.Agents;

/// <summary>Display names and number formats of the AI agents views (spec 07 §3.8.10, §6.8 "Formatting").</summary>
public static partial class AgentFormat
{
    /// <summary>
    /// Claude: drop "claude-", dates and "v…" parts ("claude-opus-5-5" → "Opus
    /// 5.5", "claude-3-5-sonnet-20241022" → "Sonnet 3.5"). GPT: "GPT-n" plus
    /// capitalized words ("gpt-5.1-codex-max" → "GPT-5.1 Codex Max"). Others as is.
    /// </summary>
    public static string ModelDisplayName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "?";
        }

        var id = AgentPricing.Normalize(model);
        if (id.StartsWith("claude-", StringComparison.Ordinal))
        {
            var parts = id[7..].Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !(p.Length >= 6 && p.All(char.IsAsciiDigit)) && !VersionTag().IsMatch(p))
                .ToList();
            var names = parts.Where(p => !p.All(char.IsAsciiDigit)).ToList();
            var numbers = parts.Where(p => p.All(char.IsAsciiDigit)).ToList();
            var name = string.Join(' ', names.Select(Capitalize));
            return numbers.Count == 0 ? name : $"{name} {string.Join('.', numbers)}".Trim();
        }

        if (id.StartsWith("gpt-", StringComparison.Ordinal))
        {
            var parts = id[4..].Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0)
            {
                return "GPT";
            }

            var head = $"GPT-{parts[0]}";
            return parts.Count == 1 ? head : $"{head} {string.Join(' ', parts.Skip(1).Select(Capitalize))}";
        }

        return model.Trim();
    }

    /// <summary>Tokens: as is below 1000, then K/M/B with one decimal below 10, rounded down ("4.2K", "48K", "1.2M").</summary>
    public static string Tokens(long count, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (count < 1000)
        {
            return Math.Max(0, count).ToString(culture);
        }

        (double Divisor, string Suffix) unit = count switch
        {
            < 1_000_000 => (1e3, "K"),
            < 1_000_000_000 => (1e6, "M"),
            _ => (1e9, "B"),
        };
        var value = count / unit.Divisor;
        var text = value < 10
            ? (Math.Floor(value * 10) / 10).ToString("0.#", culture)
            : Math.Floor(value).ToString("0", culture);
        return text + unit.Suffix;
    }

    /// <summary>Cost: "$12K" from 10,000, whole dollars from 100, else two decimals.</summary>
    public static string Cost(double usd, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (usd >= 10_000)
        {
            return "$" + Math.Floor(usd / 1000).ToString("0", culture) + "K";
        }

        return usd >= 100 ? "$" + Math.Round(usd).ToString("0", culture) : "$" + usd.ToString("0.00", culture);
    }

    /// <summary>Durations in two units: "2d 3h", "1h 5m", "4m 12s", "42s".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s" : $"{duration.Seconds}s";
    }

    /// <summary>Stopwatch clock: "m:ss" or "h:mm:ss".</summary>
    public static string Clock(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    /// <summary>Countdown to renewal (at least a minute): "h m" or "d h".</summary>
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
        {
            remaining = TimeSpan.FromMinutes(1);
        }

        return remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays}d {remaining.Hours}h" : $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
    }

    public static string Percent(double value) => $"{Math.Round(value):0}%";

    private static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    [GeneratedRegex(@"^v\d")]
    private static partial Regex VersionTag();
}
