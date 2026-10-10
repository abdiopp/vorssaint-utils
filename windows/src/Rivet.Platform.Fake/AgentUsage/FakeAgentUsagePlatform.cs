// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Agents;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;

namespace Rivet.Platform.Fake.Agents;

/// <summary>
/// The development platform for agent usage. It never looks at the real home
/// folder (whose agent logs are private): "home" is a sandbox under the app's
/// data folder, filled with synthetic sample logs so the AI agents views have
/// something to show. Environment overrides are ignored for the same reason.
/// </summary>
public sealed class FakeAgentUsagePlatform : PortableAgentUsagePlatform
{
    private readonly string _root;

    public FakeAgentUsagePlatform(AppPaths paths, bool createSamples = true)
    {
        _root = Path.Combine(paths.LocalRoot, "agent-samples");
        if (createSamples)
        {
            try
            {
                AgentSampleLogs.EnsureToday(HomeDirectory, DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("agents", "Could not write the sample agent logs.", ex);
            }
        }
    }

    public override string HomeDirectory => Path.Combine(_root, "home");

    public override string RoamingAppData => Path.Combine(_root, "roaming");

    public override string LocalAppData => Path.Combine(_root, "local");

    public override string PidDomain => "win32";

    public override string? GetEnvironmentVariable(string name) => null;
}

/// <summary>Synthetic Claude Code, Codex and Copilot logs (made-up ids and counters only).</summary>
public static class AgentSampleLogs
{
    public static void EnsureToday(string home, DateTimeOffset now)
    {
        var marker = Path.Combine(home, ".samples-day");
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (File.Exists(marker) && File.ReadAllText(marker) == day)
        {
            return;
        }

        Write(home, now);
        File.WriteAllText(marker, day);
    }

    public static void Write(string home, DateTimeOffset now)
    {
        var claude = new List<string>();
        var codex = new List<string>();
        var random = new Random(7);
        for (var d = 20; d >= 0; d--)
        {
            if (d % 3 == 1)
            {
                continue;
            }

            var day = now.UtcDateTime.Date.AddDays(-d).AddHours(9);
            for (var i = 0; i < 4 + random.Next(6); i++)
            {
                var t = new DateTimeOffset(day.AddMinutes(i * 37), TimeSpan.Zero);
                if (t > now)
                {
                    break;
                }

                claude.Add(Assistant(t, $"msg_{d}_{i}", random.Next(800, 4000), random.Next(20_000, 90_000), random.Next(300, 2500), i % 3 == 0 ? "claude-opus-5-5" : "claude-sonnet-4-6"));
                codex.Add(Codex(t.AddMinutes(5), $"resp_{d}_{i}", random.Next(2_000, 30_000), random.Next(200, 1500)));
            }
        }

        // A task in progress right now.
        claude.Add(User(now.AddMinutes(-3), "(sample prompt)"));
        claude.Add(Assistant(now.AddMinutes(-2), "msg_live", 1200, 40_000, 900, "claude-opus-5-5", "tool_use"));

        var claudeFile = Path.Combine(home, ".claude", "projects", "C--Users-dev-code-sample-app", "sample-session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(claudeFile)!);
        File.WriteAllText(claudeFile, string.Join('\n', claude) + "\n", new UTF8Encoding(false));

        var codexFile = Path.Combine(home, ".codex", "sessions", "sample", "rollout-sample.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(codexFile)!);
        var limits = JsonSerializer.Serialize(new
        {
            timestamp = Iso(now.AddMinutes(-1)),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                rate_limits = new
                {
                    primary = new { used_percent = 37.0, window_minutes = 300, resets_in_seconds = 9000 },
                    secondary = new { used_percent = 64.0, window_minutes = 10080, resets_in_seconds = 300_000 },
                    plan_type = "plus",
                },
            },
        });
        var header = new[]
        {
            JsonSerializer.Serialize(new { timestamp = Iso(now.AddDays(-21)), type = "session_meta", payload = new { id = "sample-codex", cwd = @"C:\Users\dev\code\sample-api" } }),
            JsonSerializer.Serialize(new { timestamp = Iso(now.AddDays(-21)), type = "turn_context", payload = new { model = "gpt-5.1-codex", cwd = @"C:\Users\dev\code\sample-api" } }),
        };
        File.WriteAllText(codexFile, string.Join('\n', header.Concat(codex).Append(limits)) + "\n", new UTF8Encoding(false));
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string User(DateTimeOffset t, string text) => JsonSerializer.Serialize(new
    {
        isSidechain = false,
        cwd = @"C:\Users\dev\code\sample-app",
        sessionId = "sample-session",
        type = "user",
        message = new { role = "user", content = text },
        timestamp = Iso(t),
    });

    private static string Assistant(DateTimeOffset t, string id, int input, int cacheRead, int output, string model, string stop = "end_turn") => JsonSerializer.Serialize(new
    {
        isSidechain = false,
        cwd = @"C:\Users\dev\code\sample-app",
        sessionId = "sample-session",
        type = "assistant",
        message = new
        {
            id,
            model,
            stop_reason = stop,
            usage = new { input_tokens = input, cache_creation_input_tokens = input / 2, cache_read_input_tokens = cacheRead, output_tokens = output },
        },
        requestId = "req_" + id,
        timestamp = Iso(t),
    });

    private static string Codex(DateTimeOffset t, string id, int input, int output) => JsonSerializer.Serialize(new
    {
        timestamp = Iso(t),
        type = "token_usage_record",
        payload = new { response_id = id, session_id = "sample-codex", usage = new { input_tokens = input, cached_input_tokens = input / 2, output_tokens = output } },
    });
}

public sealed class FakeAgentUsageRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) =>
        services.AddSingleton<IAgentUsagePlatform>(sp => new FakeAgentUsagePlatform(sp.GetRequiredService<AppPaths>()));
}
